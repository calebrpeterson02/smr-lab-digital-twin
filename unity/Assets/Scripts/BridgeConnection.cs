using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;

/// <summary>
/// The parts of a bridge client that don't depend on the data source: the
/// WebSocket connection, session commands, status reporting, and playing data
/// in step with the other twins on the shared TwinClock. Each bridge (robot telemetry on 8765, IMUs on 8766, ...) gets a
/// subclass that only handles its own "data" messages - see BridgeClient and
/// ImuBridgeClient.
/// </summary>
public abstract class BridgeConnection : MonoBehaviour
{
    [Tooltip("Prefix for this bridge's status messages, e.g. \"Robot\" or \"IMU\". Leave empty for the default.")]
    public string displayName = "";

    [Tooltip("Used in the Editor and desktop builds, e.g. ws://localhost:8765 or ws://<bridge-host>:8765 if running elsewhere. " +
             "A web build ignores it and connects through the server that served the page.")]
    public string serverUrl = "ws://localhost:8765";

    [Tooltip("Give up on a connection attempt after this many seconds (an unreachable host can otherwise hang for a long time).")]
    public float connectTimeoutSeconds = 5f;

    [Tooltip("While the bridge is unreachable (not started yet, or stopped), try to reconnect this often.")]
    public float reconnectIntervalSeconds = 3f;

    [Header("Session selection (Inspector for now)")]
    [Tooltip("If >= 0, replays this session on the first connect. If -1, starts live.")]
    public int sessionIdOverride = -1;

    [Header("Playback")]
    [Tooltip("Shared clock that keeps all twins showing the same recorded moment (replay speed, looping and " +
             "live smoothing are set there). Leave empty to use the one in the scene; one is added if there is none.")]
    public TwinClock clock;

    /// <summary>Used when displayName is empty.</summary>
    protected abstract string DefaultName { get; }
    public string Name => string.IsNullOrEmpty(displayName) ? DefaultName : displayName;

    /// <summary>
    /// Path a web build connects to on the server that served the page, e.g. "/ws/robot".
    /// The web server (nginx) passes it on to this bridge's port.
    /// </summary>
    protected abstract string WebSocketPath { get; }

    /// <summary>True while following a live session, false while a replay is loading/playing.</summary>
    protected bool IsLive { get; private set; } = true;

    /// <summary>Recorded seconds that pass per real second: 1 live, the clock's playbackSpeed in replay.</summary>
    protected float PlaybackSpeed => IsLive ? 1f : Mathf.Max(clock.playbackSpeed, 0.01f);

    /// <summary>Handle a "data" message: pass each sample to AddItem.</summary>
    protected abstract void HandleData(string json);

    /// <summary>A new session started (live or replay). Drop any per-session state. Optional.</summary>
    protected virtual void OnSessionChanged(int sessionId, bool live) { }

    /// <summary>
    /// Called once per frame after any items were applied that frame
    /// (e.g. recompute joint angles from the newest IMU samples). Optional.
    /// </summary>
    protected virtual void AfterItemsApplied() { }

    /// <summary>
    /// Subclasses pass every sample here, with its recorded time and what to do
    /// when it's due. Nothing is applied directly: the item plays when the
    /// shared TwinClock reaches its recorded time, so every twin stays in step.
    /// (Exception: with the clock's live smoothing off, live items apply at once.)
    /// </summary>
    protected void AddItem(double recordedAt, Action apply)
    {
        if (IsLive)
        {
            if (!clock.smoothLive)
            {
                apply();
                appliedThisFrame = true;
                return;
            }
            clock.ObserveLiveSample(recordedAt);
            liveItems.Add((recordedAt, sequence++, apply));
            liveSorted = false;
        }
        else
        {
            // Replay: buffered until replay_complete, then played on the shared replay clock
            replayItems.Add((recordedAt, sequence++, apply));
        }
    }

    /// <summary>Recorded time of the next replay item this client still has to play (infinity if none).</summary>
    public double NextReplayItemTime =>
        replayReady && replayIndex < replayItems.Count ? replayItems[replayIndex].time : double.PositiveInfinity;

    // Items waiting for their recorded time. Live items are removed once played;
    // replay items are kept (and indexed) so the replay can loop. Sorted by time,
    // with arrival order as the tie-breaker - IMU rows arrive grouped by device,
    // not in time order across devices.
    private readonly List<(double time, long sequence, Action apply)> liveItems =
        new List<(double time, long sequence, Action apply)>();
    private readonly List<(double time, long sequence, Action apply)> replayItems =
        new List<(double time, long sequence, Action apply)>();
    private bool liveSorted = true;
    private long sequence;
    private bool replayReady;   // all of this replay's data has arrived
    private int replayIndex;    // next replay item to play
    private int replayCycle = -1; // the clock's ReplayCycle we're playing; a change means rewind
    private bool appliedThisFrame;

    private static int CompareItems((double time, long sequence, Action apply) a, (double time, long sequence, Action apply) b)
    {
        return a.time != b.time ? a.time.CompareTo(b.time) : a.sequence.CompareTo(b.sequence);
    }

    // --- Fields shared by every message type. JsonUtility fills only the fields
    // present in a given message and ignores the rest, so subclasses parse
    // their own "data" fields separately. ---
    [Serializable]
    private class Envelope
    {
        public string type;
        public int sessionId;
        public bool autoFollow;
        public int frameCount;
    }

    // Success: something that was failing works now (e.g. connected to the bridge)
    public enum StatusLevel { Info, Warning, Error, Success }

    /// <summary>
    /// Raised on the main thread whenever there's something worth showing the
    /// user (connection progress, session changes, errors). Subscribe from UI code.
    /// </summary>
    public event Action<string, StatusLevel> StatusChanged;

    /// <summary>The most recent status, so UI that subscribes late (or was disabled) can catch up.</summary>
    public string LastStatusMessage { get; private set; }
    public StatusLevel LastStatusLevel { get; private set; }

    // Status can be reported from async continuations as well as from Update(),
    // so it's queued here and raised from Update() - UI must only be touched on the main thread.
    private readonly ConcurrentQueue<(string message, StatusLevel level)> statusQueue =
        new ConcurrentQueue<(string message, StatusLevel level)>();

    // NativeWebSocket rather than System.Net.WebSockets: browsers don't allow raw
    // sockets, so a web build has to use the browser's own WebSocket, which
    // NativeWebSocket wraps. In the Editor and desktop builds it uses ClientWebSocket.
    private WebSocket socket;       // the current connection attempt or open connection
    private string url;             // where socket connects: serverUrl, or the page's server in a web build
    private CancellationTokenSource cts;
    private Task<bool> connectTask; // latest connection attempt; shared by callers while it's still running
    private bool firstConnection = true;
    private bool retryingQuietly;   // a failure was reported; later retries stay silent until one succeeds
    private int currentSessionId = -1;  // from the last session_changed; re-requested if its replay was cut off
    private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();

    private bool IsOpen => socket != null && socket.State == WebSocketState.Open;

    protected virtual void Awake()
    {
        if (clock == null) clock = FindAnyObjectByType<TwinClock>();
        if (clock == null)
        {
            clock = gameObject.AddComponent<TwinClock>();
            Debug.Log($"{GetType().Name}: no TwinClock in the scene, so one was added to '{name}' with default settings. " +
                      "Add a TwinClock to the scene yourself to change replay/live settings.");
        }
    }

    protected virtual void Start()
    {
        // Keep playing live data while the browser tab or Editor window isn't focused
        Application.runInBackground = true;
        url = ResolveUrl();
        cts = new CancellationTokenSource();
        _ = ConnectionLoop();
    }

    /// <summary>
    /// serverUrl, except in a web build: there the bridge is reached through the
    /// server that served the page, so the same build works on any host.
    /// ws:// for an http page, wss:// for https (browsers block ws:// from https pages).
    /// </summary>
    private string ResolveUrl()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        var page = new Uri(Application.absoluteURL);
        string scheme = page.Scheme == "https" ? "wss" : "ws";
        return $"{scheme}://{page.Authority}{WebSocketPath}";
#else
        return serverUrl;
#endif
    }

    /// <summary>
    /// Keeps the connection up: connects at startup, and reconnects every
    /// reconnectIntervalSeconds whenever the bridge isn't reachable - not
    /// started yet, or stopped and started again. Runs until OnDestroy.
    /// </summary>
    private async Task ConnectionLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            if (!IsOpen) await EnsureConnectedAsync();
            try
            {
                await WaitUntil(() => false, Mathf.Max(reconnectIntervalSeconds, 0.5f));
            }
            catch (OperationCanceledException)
            {
                return; // shutting down
            }
        }
    }

    /// <summary>
    /// Waits until done() is true or `seconds` of real time pass, checking once a
    /// frame, and returns done(). Unaffected by Time.timeScale. An Awaitable rather
    /// than Task.Delay, which needs a thread pool that WebGL doesn't have.
    /// Throws OperationCanceledException on shutdown.
    /// </summary>
    private async Awaitable<bool> WaitUntil(Func<bool> done, float seconds)
    {
        float end = Time.realtimeSinceStartup + seconds;
        while (!done() && Time.realtimeSinceStartup < end)
        {
            await Awaitable.NextFrameAsync(cts.Token);
        }
        return done();
    }

    /// <summary>
    /// Returns true if the socket is open, connecting first if it isn't.
    /// Concurrent callers (e.g. a button mashed during a slow connect) share
    /// the same attempt instead of each opening their own socket.
    /// </summary>
    private Task<bool> EnsureConnectedAsync()
    {
        if (IsOpen) return Task.FromResult(true);
        if (connectTask == null || connectTask.IsCompleted) connectTask = ConnectAsync();
        return connectTask;
    }

    private async Task<bool> ConnectAsync()
    {
        // A socket can't be reused once closed, so each attempt needs a fresh one
        var ws = new WebSocket(url);
        socket = ws;
        bool opened = false, closed = false;
        string error = null;

        // NativeWebSocket raises its events on the main thread. Messages and closes
        // from a socket that a newer attempt has replaced are ignored.
        ws.OnOpen += () => opened = true;
        ws.OnError += message => error = message;
        ws.OnMessage += bytes =>
        {
            if (socket == ws) incoming.Enqueue(Encoding.UTF8.GetString(bytes));
        };
        ws.OnClose += code =>
        {
            closed = true;
            if (opened && socket == ws) ReportClosed(code);
        };

        if (!retryingQuietly) ReportStatus($"Connecting to {url}...");
        try
        {
            // Not awaited: in the Editor and desktop builds, Connect() only completes
            // when the connection closes. In WebGL it returns at once. Either way,
            // OnOpen or OnClose says how the attempt went.
            _ = ws.Connect();
            if (!await WaitUntil(() => opened || closed, connectTimeoutSeconds))
            {
                Abandon(ws);
                ReportConnectFailure($"Connection to {url} timed out after {connectTimeoutSeconds}s.");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            Abandon(ws);
            return false; // shutting down
        }
        catch (Exception e)
        {
            // WebGL's Connect() throws straight away if the socket is in the wrong state
            ReportConnectFailure($"Connection to {url} failed: {e.Message}");
            return false;
        }

        if (!opened)
        {
            ReportConnectFailure(string.IsNullOrEmpty(error)
                ? $"Connection to {url} failed."
                : $"Connection to {url} failed: {error}");
            return false;
        }

        retryingQuietly = false;
        ReportStatus($"Connected to the bridge at {url}", StatusLevel.Success);
        await SendStartupCommand();
        return true;
    }

    /// <summary>
    /// Gives up on a connection attempt that hasn't opened yet. The native and
    /// WebGL sockets need different calls for this.
    /// </summary>
    private void Abandon(WebSocket ws)
    {
        if (socket == ws) socket = null;
#if UNITY_WEBGL && !UNITY_EDITOR
        try { ws.Close(); } catch (Exception) { }  // the browser aborts a socket closed while connecting
#else
        ws.CancelConnection();
#endif
    }

    /// <summary>
    /// The bridge closed the connection, or it dropped. NativeWebSocket passes on
    /// the close code but not the reason text, so it's the bridge's code 1001
    /// (Going Away) that says it was shut down.
    /// </summary>
    private void ReportClosed(WebSocketCloseCode code)
    {
        if (cts.IsCancellationRequested) return; // we closed it ourselves in OnDestroy

        string why = code switch
        {
            WebSocketCloseCode.Away => "the bridge was shut down",
            WebSocketCloseCode.Abnormal => "the connection was lost",
            _ => $"close code {(int)code}",
        };
        ReportStatus($"Bridge connection terminated ({why}). Reconnecting when it's back.", StatusLevel.Warning);
        retryingQuietly = true; // the message above already says so; don't add a failure per retry
    }

    // Only the first failure in a row is shown; ConnectionLoop keeps retrying
    // quietly until one succeeds, so the status box isn't flooded
    private void ReportConnectFailure(string message)
    {
        if (retryingQuietly) return;
        retryingQuietly = true;
        ReportStatus($"{message} Retrying every {reconnectIntervalSeconds:0.#}s.", StatusLevel.Error);
    }

    /// <summary>
    /// Tells the bridge what this client wants, right after connecting. The
    /// bridge keeps a new connection idle until told, so a reconnect can't
    /// interrupt a replay that is already playing locally.
    /// Sends directly rather than through SendCommand: if the socket closed
    /// already, SendCommand would wait on the connection attempt making this call.
    /// </summary>
    private Task SendStartupCommand()
    {
        if (firstConnection)
        {
            firstConnection = false;
            return Send(sessionIdOverride >= 0 ? SelectSessionCommand(sessionIdOverride) : UseLatestCommand);
        }
        if (IsLive) return Send(UseLatestCommand);
        // The connection dropped while the replay was still downloading: ask again
        if (!replayReady && currentSessionId >= 0) return Send(SelectSessionCommand(currentSessionId));
        return Task.CompletedTask;  // the whole replay is here; it keeps playing locally
    }

    protected virtual void Update()
    {
        // Handle whatever arrived since last frame. Messages are queued rather than
        // handled as they arrive, so they're processed before this frame's playback.
        while (incoming.TryDequeue(out string json))
        {
            ProcessMessage(json);
        }

        if (IsLive) PlayDueLiveItems();
        else PlayDueReplayItems();

        if (appliedThisFrame)
        {
            appliedThisFrame = false;
            AfterItemsApplied();
        }

        while (statusQueue.TryDequeue(out var status))
        {
            LastStatusMessage = status.message;
            LastStatusLevel = status.level;
            StatusChanged?.Invoke(status.message, status.level);
        }
    }

    // Applies every buffered live item whose recorded time has come up on the shared live clock
    private void PlayDueLiveItems()
    {
        if (liveItems.Count == 0) return;

        if (!liveSorted)
        {
            liveItems.Sort(CompareItems);
            liveSorted = true;
        }

        double now = clock.LiveTime;
        int due = 0;
        while (due < liveItems.Count && liveItems[due].time <= now)
        {
            liveItems[due].apply();
            due++;
        }

        if (due > 0)
        {
            liveItems.RemoveRange(0, due);
            appliedThisFrame = true;
        }
    }

    // Applies every replay item whose recorded time has come up on the shared replay clock
    private void PlayDueReplayItems()
    {
        if (!replayReady || !clock.ReplayRunning) return;

        if (clock.ReplayCycle != replayCycle)
        {
            // The shared replay (re)started - from the beginning
            replayCycle = clock.ReplayCycle;
            replayIndex = 0;
        }

        double now = clock.ReplayTime;
        while (replayIndex < replayItems.Count && replayItems[replayIndex].time <= now)
        {
            replayItems[replayIndex].apply();
            replayIndex++;
            appliedThisFrame = true;
        }
    }

    // Data from a previous session (or mode) must not play into the next one
    private void ClearItems()
    {
        liveItems.Clear();
        liveSorted = true;
        replayItems.Clear();
        replayReady = false;
        replayIndex = 0;
        appliedThisFrame = false;
    }

    // Logs to the console (which keeps the full history, since the UI only shows
    // the latest message) and queues the message for the UI. Safe to call from any
    // thread: Debug.Log is thread-safe, and the UI side is only touched in Update().
    // consoleDetail is extra context (e.g. a raw payload) that's logged but not shown in the UI.
    protected void ReportStatus(string message, StatusLevel level = StatusLevel.Info, string consoleDetail = null)
    {
        // Prefixed with the bridge name, since several bridges share one status box
        string shown = $"{Name}: {message}";
        string logged = consoleDetail == null ? $"{GetType().Name} {shown}" : $"{GetType().Name} {shown} [{consoleDetail}]";

        switch (level)
        {
            case StatusLevel.Warning: Debug.LogWarning(logged); break;
            case StatusLevel.Error: Debug.LogError(logged); break;
            default: Debug.Log(logged); break;
        }

        statusQueue.Enqueue((shown, level));
    }

    private void ProcessMessage(string json)
    {
        Envelope msg;
        try
        {
            msg = JsonUtility.FromJson<Envelope>(json);
        }
        catch (Exception e)
        {
            ReportStatus($"Couldn't parse a message from the bridge: {e.Message}", StatusLevel.Warning, consoleDetail: json);
            return;
        }

        try
        {
            switch (msg.type)
            {
                case "session_changed":
                    ReportStatus(msg.autoFollow
                        ? $"Live: following session {msg.sessionId}"
                        : $"Loading session {msg.sessionId}...");

                    // Drop the previous session's data - a burst for a new session is
                    // either starting now, or we've switched to live.
                    ClearItems();
                    currentSessionId = msg.sessionId;
                    IsLive = msg.autoFollow;
                    if (IsLive) clock.LeaveReplay(this);
                    else clock.JoinReplay(this); // holds the shared replay until this twin's data arrives
                    OnSessionChanged(msg.sessionId, msg.autoFollow);
                    break;
                case "data":
                    HandleData(json);
                    break;
                case "replay_complete":
                    HandleReplayComplete(msg.sessionId);
                    break;
                case "session_empty":
                    // The bridge follows this with a session_changed back to live mode
                    ReportStatus($"Session {msg.sessionId} has no data; switching to live mode.", StatusLevel.Warning);
                    break;
                default:
                    ReportStatus($"Unknown message type '{msg.type}' from the bridge.", StatusLevel.Warning);
                    break;
            }
        }
        catch (Exception e)
        {
            // A malformed message shouldn't stop Update() from draining the rest of the queue
            ReportStatus($"Couldn't handle a '{msg.type}' message: {e.Message}", StatusLevel.Warning, consoleDetail: json);
        }
    }

    // The whole replay burst has arrived: hand it to the shared clock, which
    // starts every twin together once they all have their data
    private void HandleReplayComplete(int sessionId)
    {
        if (replayItems.Count == 0)
        {
            clock.LeaveReplay(this);
            return;
        }

        replayItems.Sort(CompareItems);
        replayReady = true;
        replayIndex = 0;
        clock.ReplayReady(this, replayItems[0].time);
        ReportStatus($"Replaying session {sessionId} ({replayItems.Count} samples)");
    }

    private const string UseLatestCommand = "{\"command\":\"use_latest\"}";
    private static string SelectSessionCommand(int sessionId) =>
        $"{{\"command\":\"select_session\",\"sessionId\":{sessionId}}}";

    public Task SelectSession(int sessionId) => SendCommand(SelectSessionCommand(sessionId));

    public Task UseLatest() => SendCommand(UseLatestCommand);

    private async Task SendCommand(string json)
    {
        // Not connected (bridge started late, restarted, etc.) - try to connect first
        if (!await EnsureConnectedAsync())
        {
            ReportStatus("Can't send command: not connected to the bridge.", StatusLevel.Warning, consoleDetail: json);
            return;
        }
        await Send(json);
    }

    private async Task Send(string json)
    {
        // Callers (e.g. UI buttons) fire-and-forget this task, so failures
        // must be logged here or they'd vanish silently. NativeWebSocket queues
        // sends itself, so overlapping commands don't need a lock.
        try
        {
            await socket.SendText(json);
        }
        catch (Exception e)
        {
            ReportStatus($"Failed to send command: {e.Message}", StatusLevel.Error, consoleDetail: json);
        }
    }

    protected virtual void OnDestroy()
    {
        // Don't leave the shared replay waiting for a twin that no longer exists
        if (clock != null) clock.LeaveReplay(this);

        // Stops ConnectionLoop and any attempt in progress. Also tells ReportClosed
        // not to report the close below. (Not disposed: socket events may still
        // arrive and check it.)
        cts?.Cancel();

        if (socket == null) return;
        if (socket.State == WebSocketState.Open) CloseQuietly(socket);
        else if (socket.State == WebSocketState.Connecting) Abandon(socket);
    }

    // Sends a close frame so the bridge drops this viewer straight away. Not
    // awaited by OnDestroy: the native socket waits for the bridge's reply, and
    // shutdown shouldn't. Best effort: errors are ignored.
    private static async void CloseQuietly(WebSocket ws)
    {
        try
        {
            await ws.Close(WebSocketCloseCode.Normal, "closing");
        }
        catch (Exception) { }
    }
}
