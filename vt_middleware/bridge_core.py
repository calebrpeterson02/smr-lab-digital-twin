"""Shared engine for the Postgres -> WebSocket bridges.

Each bridge streams one data source (robot telemetry, IMUs, ...) to Unity on
its own port. Everything that doesn't depend on the source lives here:
session tracking, live vs. replay mode per viewer, and sending to clients.
The source-specific parts (which table, how far the live stream has got,
what a message looks like) are a DataSource subclass - see
postgres_bridge.py and imu_bridge.py.

Every connected client (a "viewer") picks its own mode, and gets nothing
until it does: use_latest goes live, select_session replays. Live viewers all
follow the newest session, so they share one live feed and one DB poll.
A viewer that selects a session gets its own replay, which no other viewer sees.
"""
import asyncio
import contextlib
import itertools
import json
import os
import signal
from pathlib import Path

import asyncpg
import websockets
from dotenv import load_dotenv
from websockets.asyncio.server import serve as websocket_serve

# Explicit path: load_dotenv() with no arguments searches from the current
# working directory when run under a debugger, and can miss this file
load_dotenv(Path(__file__).with_name(".env"))

POLL_INTERVAL_SECONDS = 0.05    # How often to check for new rows
SESSION_CHECK_INTERVAL_SECONDS = 2.0 # How often to check for a new session
ERROR_RETRY_SECONDS = 1.0       # back-off after a failed loop iteration (e.g. DB hiccup)
DB_POOL_MIN_SIZE = 2            # each bridge's live feed + session watcher query concurrently
DB_POOL_MAX_SIZE = 10           # headroom for several bridges + viewers' replay fetches
WEBSOCKET_HOST = "0.0.0.0"
SHUTDOWN_TIMEOUT_SECONDS = 5.0  # how long to wait for clients to acknowledge the close on Ctrl+C
SHUTDOWN_CLOSE_REASON = "bridge was shut down"  # sent to clients with the close; Unity displays it

# Sessions are registered here by the dashboard's "Start Session" button,
# before the robot is enabled - so a new session is visible well before its
# first data rows arrive
SESSION_TABLE_NAME = '"session"'
SESSION_TABLE_ID_COLUMN = "id"


async def get_latest_session_id(pool):
    """Newest registered session, whether or not it has data yet"""
    return await pool.fetchval(f"SELECT MAX({SESSION_TABLE_ID_COLUMN}) FROM {SESSION_TABLE_NAME}")


class DataSource:
    """The source-specific half of a bridge.

    The "cursor" is whatever the live stream needs to remember about how far
    it has got (a frame_id, a row id, ...). The engine never looks inside it;
    it just hands it back to the source.
    """
    name = "source"         # used as the log prefix
    rows_per_message = 1    # rows grouped into each "data" message

    def initial_cursor(self):
        """Cursor that streams a session from its very first row."""
        raise NotImplementedError

    async def latest_cursor(self, pool, session_id):
        """Cursor just past the session's newest row ("jump to now")."""
        raise NotImplementedError

    async def fetch_new(self, pool, session_id, cursor):
        """Rows newer than the cursor, oldest first."""
        raise NotImplementedError

    def advance(self, cursor, row):
        """Cursor after `row` has been sent."""
        raise NotImplementedError

    async def fetch_session(self, pool, session_id):
        """The whole session for replay, in playback order."""
        raise NotImplementedError

    def to_message(self, session_id, rows):
        """The "data" message sent to clients for these rows."""
        raise NotImplementedError


class Viewer:
    """One connected client, and the mode it has chosen.

    live=True: on the shared live feed (newest session). live=False with a
    session_id: replaying it on its own, via `replay_task`. live=False with no
    session_id: just connected, nothing chosen yet.
    """
    _ids = itertools.count(1)

    def __init__(self, websocket):
        self.websocket = websocket
        self.id = next(Viewer._ids)     # only for log messages
        self.live = False
        self.session_id = None
        self.replay_task = None

    async def send(self, payload: dict):
        await self.websocket.send(json.dumps(payload))

    async def send_session_changed(self):
        await self.send({
            "type": "session_changed",
            "sessionId": self.session_id,
            "autoFollow": self.live,
        })

    async def stop_replay(self):
        """Cancels a running replay and waits for it to finish, so none of
        its messages can arrive after whatever is sent next."""
        task, self.replay_task = self.replay_task, None
        if task is not None and not task.done():
            task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await task


class Bridge:
    """One data source served on one WebSocket port."""

    def __init__(self, source: DataSource, port: int):
        self.source = source
        self.port = port
        self.pool = None
        self.viewers = set()

        # Shared live feed: the newest session, and how far it has been sent
        self.live_session = None
        self.cursor = source.initial_cursor()
        # Held while the feed sends a batch, and while anything changes the live
        # session, the cursor or a viewer's mode. Keeps a viewer that leaves
        # live mode from receiving one more live batch after its new
        # session_changed, and keeps the cursor consistent with the session.
        self.live_lock = asyncio.Lock()

    def log(self, message):
        print(f"[{self.source.name}] {message}")

    def live_viewers(self):
        return [v for v in self.viewers if v.live]

    # --- Clients ---

    async def handle_client(self, websocket):
        viewer = Viewer(websocket)
        self.viewers.add(viewer)
        self.log(f"Viewer {viewer.id} connected. Total viewers: {len(self.viewers)}")
        try:
            # A new viewer gets nothing until it sends use_latest or
            # select_session. Unity sends one right after connecting; after a
            # reconnect mid-replay it sends neither, so its local replay keeps playing.
            async for raw_message in websocket:
                # A bad message shouldn't drop this client's connection
                try:
                    await self.handle_client_message(viewer, raw_message)
                except websockets.ConnectionClosed:
                    raise
                except Exception as e:
                    self.log(f"Error handling viewer {viewer.id} message {raw_message!r}: {type(e).__name__}: {e}")
        except websockets.ConnectionClosed:
            pass    # closed while we were replying; the finally below cleans up
        finally:
            await viewer.stop_replay()
            self.viewers.discard(viewer)
            self.log(f"Viewer {viewer.id} disconnected. Total viewers: {len(self.viewers)}")

    async def handle_client_message(self, viewer: Viewer, raw_message: str):
        try:
            message = json.loads(raw_message)
        except json.JSONDecodeError:
            self.log(f"Ignoring malformed client message: {raw_message!r}")
            return

        if not isinstance(message, dict):
            self.log(f"Ignoring non-object client message: {raw_message!r}")
            return

        command = message.get("command")

        if command == "select_session":
            # Accept 418 or "418"; reject missing/non-numeric values before they reach asyncpg
            try:
                session_id = int(message.get("sessionId"))
            except (TypeError, ValueError):
                self.log(f"select_session needs an integer 'sessionId', got {message.get('sessionId')!r}; ignoring.")
                return
            await self.start_replay(viewer, session_id)

        elif command == "use_latest":
            self.log(f"Viewer {viewer.id} requested live mode.")
            await viewer.stop_replay()
            await self.join_live(viewer)

        else:
            self.log(f"Ignoring unknown command: {command!r}")

    # --- Live mode (shared) ---

    def set_live_session(self, session_id):
        """Caller holds live_lock. Streams the new session from its first row:
        a session found by the watcher is at most one check interval old, so
        any rows it already has are the start of the motion, not stale history."""
        self.live_session = session_id
        self.cursor = self.source.initial_cursor()
        self.log(f"Live feed now on session {session_id}.")

    async def notify_live_viewers(self):
        """Caller holds live_lock. Tells every live viewer the live session changed."""
        viewers = self.live_viewers()
        for viewer in viewers:
            viewer.session_id = self.live_session
        await asyncio.gather(*(v.send_session_changed() for v in viewers), return_exceptions=True)

    async def join_live(self, viewer: Viewer):
        """Puts a viewer on the shared live feed (used by use_latest, and when
        a selected session turns out to be empty)."""
        async with self.live_lock:
            latest = await get_latest_session_id(self.pool)
            if latest is not None and latest != self.live_session:
                self.set_live_session(latest)
                await self.notify_live_viewers()
            # The feed doesn't poll while nobody is live, so its cursor may be
            # far behind. The first viewer to join starts it at "now".
            if not self.live_viewers() and self.live_session is not None:
                self.cursor = await self.source.latest_cursor(self.pool, self.live_session)
            viewer.live = True
            viewer.session_id = self.live_session
            # No session exists yet: the watcher tells this viewer when one appears
            if self.live_session is not None:
                await viewer.send_session_changed()

    async def session_watcher(self):
        """Moves the live feed to each newly registered session."""
        while True:
            await asyncio.sleep(SESSION_CHECK_INTERVAL_SECONDS)
            try:
                latest = await get_latest_session_id(self.pool)
                if latest is None or latest == self.live_session:
                    continue
                async with self.live_lock:
                    if latest != self.live_session:  # a viewer joining may have switched already
                        self.set_live_session(latest)
                        await self.notify_live_viewers()
            except Exception as e:
                # Skip this check; the next one runs after the normal interval
                self.log(f"Session watcher error: {type(e).__name__}: {e}")

    async def live_feed(self):
        """Polls the live session and sends new rows to every live viewer.
        One poll serves all of them; it idles while no viewer is live."""
        size = self.source.rows_per_message
        while True:
            await asyncio.sleep(POLL_INTERVAL_SECONDS)
            try:
                async with self.live_lock:
                    viewers = self.live_viewers()
                    if not viewers or self.live_session is None:
                        continue
                    rows = await self.source.fetch_new(self.pool, self.live_session, self.cursor)
                    for start in range(0, len(rows), size):
                        chunk = rows[start:start + size]
                        message = json.dumps(self.source.to_message(self.live_session, chunk))
                        # A viewer whose connection failed is removed by its own handler
                        await asyncio.gather(*(v.websocket.send(message) for v in viewers),
                                             return_exceptions=True)
                        self.cursor = self.source.advance(self.cursor, chunk[-1])
            except Exception as e:
                # Log and retry rather than letting one failure take down the bridge
                self.log(f"Live feed error: {type(e).__name__}: {e}. Retrying in {ERROR_RETRY_SECONDS}s.")
                await asyncio.sleep(ERROR_RETRY_SECONDS)

    # --- Replay mode (per viewer) ---

    async def start_replay(self, viewer: Viewer, session_id):
        await viewer.stop_replay()
        async with self.live_lock:
            viewer.live = False
            viewer.session_id = session_id
            self.log(f"Viewer {viewer.id} selected session {session_id}.")
            await viewer.send_session_changed()
        viewer.replay_task = asyncio.create_task(self.replay(viewer, session_id))

    async def replay(self, viewer: Viewer, session_id):
        """Sends the whole selected session to this viewer as one burst - no
        pacing, no looping. Unity owns replay pacing/looping, the same way
        CsvPlaybackAdapter paces a loaded CSV. Cancelled by the viewer's next
        command or by its disconnect."""
        try:
            while True:
                try:
                    rows = await self.source.fetch_session(self.pool, session_id)
                    break
                except Exception as e:
                    self.log(f"Replay fetch error for session {session_id}: {type(e).__name__}: {e}. "
                             f"Retrying in {ERROR_RETRY_SECONDS}s.")
                    await asyncio.sleep(ERROR_RETRY_SECONDS)

            if not rows:
                self.log(f"Session {session_id} has no rows to send. Viewer {viewer.id} falls back to live mode.")
                await viewer.send({"type": "session_empty", "sessionId": session_id})
                await self.join_live(viewer)
                return

            self.log(f"Sending session {session_id} to viewer {viewer.id} for local replay: {len(rows)} rows.")
            size = self.source.rows_per_message
            for start in range(0, len(rows), size):
                await viewer.send(self.source.to_message(session_id, rows[start:start + size]))

            # Explicit end-of-burst marker, so the client knows when it's safe to
            # start replaying rather than guessing via a timeout
            await viewer.send({
                "type": "replay_complete",
                "sessionId": session_id,
                "frameCount": len(rows),
            })
        except websockets.ConnectionClosed:
            pass    # viewer left mid-burst; its handler cleans up

    # --- Lifecycle ---

    async def run(self, pool, stop: asyncio.Event):
        """Serves until `stop` is set, then closes every client connection."""
        self.pool = pool
        async with websocket_serve(self.handle_client, WEBSOCKET_HOST, self.port,
                                   close_timeout=SHUTDOWN_TIMEOUT_SECONDS) as server:
            self.log(f"WebSocket server listening on ws://{WEBSOCKET_HOST}:{self.port}")
            # Listening already, so a client that connects during the startup
            # query isn't refused; join_live waits for the lock held here
            async with self.live_lock:
                latest = await get_latest_session_id(pool)
                if latest is None:
                    self.log("No sessions found yet. Waiting...")
                elif latest != self.live_session:
                    self.set_live_session(latest)

            tasks = [asyncio.create_task(self.session_watcher()),
                     asyncio.create_task(self.live_feed())]
            await stop.wait()

            for task in tasks:
                task.cancel()
            await asyncio.gather(*tasks, return_exceptions=True)
            # Close each viewer with "going away" (1001) and a reason Unity shows
            # in its status box. Waits up to close_timeout for each to answer.
            await asyncio.gather(*(v.websocket.close(1001, SHUTDOWN_CLOSE_REASON)
                                   for v in list(self.viewers)), return_exceptions=True)
            # Stops listening, then waits for the handlers, which cancel their
            # viewers' replays, to finish
            server.close()
            await server.wait_closed()
            self.log("Stopped.")


def install_stop_handlers(stop: asyncio.Event):
    """Ctrl+C (SIGINT) or `docker stop` (SIGTERM) asks for a clean shutdown.
    A second Ctrl+C exits immediately, in case shutdown hangs."""
    loop = asyncio.get_running_loop()

    def on_signal(signame):
        if stop.is_set():
            print(f"{signame} again: exiting without waiting.", flush=True)
            os._exit(1)
        print(f"{signame} received: shutting down (press Ctrl+C again to force).")
        stop.set()

    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, on_signal, sig.name)


async def serve(*bridges: Bridge):
    """Runs one or more bridges in this process, sharing one connection pool,
    until Ctrl+C or SIGTERM."""
    dsn = os.environ.get("DB_DSN")
    if not dsn:
        # Without this check asyncpg silently falls back to localhost:5432,
        # which fails with a confusing "connection refused"
        raise SystemExit(f"DB_DSN is not set. Put it in {Path(__file__).with_name('.env')}.")

    stop = asyncio.Event()
    install_stop_handlers(stop)

    # A pool rather than a single connection: the live feed, session watcher
    # and viewers' replays all query concurrently, and one asyncpg
    # connection can only run one query at a time. pool.fetch()/fetchval()
    # each borrow a free connection for the duration of the query.
    async def connect():
        return await asyncpg.create_pool(dsn, min_size=DB_POOL_MIN_SIZE, max_size=DB_POOL_MAX_SIZE)

    # Race the connection against Ctrl+C: with the NAS unreachable, connecting
    # can take a minute to time out, and Ctrl+C should stop it straight away
    connecting = asyncio.create_task(connect())
    stopping = asyncio.create_task(stop.wait())
    await asyncio.wait({connecting, stopping}, return_when=asyncio.FIRST_COMPLETED)
    stopping.cancel()
    if not connecting.done():
        connecting.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await connecting
        print("Stopped before connecting to Postgres.")
        return

    async with connecting.result() as pool:
        print("Connected to Postgres.")
        await asyncio.gather(*(bridge.run(pool, stop) for bridge in bridges))
    print("Bridges stopped.")
