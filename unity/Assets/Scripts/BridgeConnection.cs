using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

    [Tooltip("e.g. ws://localhost:8765 or ws://<bridge-host>:8765 if running elsewhere.")]
    public string serverUrl = "ws://localhost:8765";

    [Tooltip("Give up on a connection attempt after this many seconds (an unreachable host can otherwise hang for a long time).")]
    public float connectTimeoutSeconds = 5f;

    [Header("Session selection (Inspector for now)")]
    [Tooltip("If >= 0, sends select_session for this id on connect. If -1, stays on " +
             "whatever the bridge is already doing (live, by default).")]
    public int sessionIdOverride = -1;

    [Header("Playback")]
    [Tooltip("Shared clock that keeps all twins showing the same recorded moment (replay speed, looping and " +
             "live smoothing are set there). Leave empty to use the one in the scene; one is added if there is none.")]
    public TwinClock clock;

    /// <summary>Used when displayName is empty.</summary>
    protected abstract string DefaultName { get; }
    public string Name => string.IsNullOrEmpty(displayName) ? DefaultName : displayName;

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

    public enum StatusLevel { Info, Warning, Error }

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

    private ClientWebSocket socket;
    private CancellationTokenSource cts;
    private Task<bool> connectTask; // latest connection attempt; shared by callers while it's still running
    private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
    // ClientWebSocket allows only one SendAsync in flight at a time
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

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

    protected virtual async void Start()
    {
        cts = new CancellationTokenSource();

        if (!await EnsureConnectedAsync()) return;

        if (sessionIdOverride >= 0)
        {
            await SelectSession(sessionIdOverride);
        }
    }

    /// <summary>
    /// Returns true if the socket is open, connecting first if it isn't.
    /// Concurrent callers (e.g. a button mashed during a slow connect) share
    /// the same attempt instead of each opening their own socket.
    /// </summary>
    private Task<bool> EnsureConnectedAsync()
    {
        if (socket != null && socket.State == WebSocketState.Open) return Task.FromResult(true);
        if (connectTask == null || connectTask.IsCompleted) connectTask = ConnectAsync();
        return connectTask;
    }

    private async Task<bool> ConnectAsync()
    {
        try
        {
            // A ClientWebSocket can't be reused once closed/aborted, so each attempt needs a fresh one
            socket?.Dispose();
            var newSocket = new ClientWebSocket();
            socket = newSocket;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(connectTimeoutSeconds));

            ReportStatus($"Connecting to {serverUrl}...");
            await newSocket.ConnectAsync(new Uri(serverUrl), timeout.Token);
            ReportStatus($"Connected to {serverUrl}");

            _ = ReceiveLoop(newSocket); // fire-and-forget; feeds the queue Update() drains
            return true;
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            ReportStatus($"Connection to {serverUrl} timed out after {connectTimeoutSeconds}s.", StatusLevel.Error);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false; // shutting down
        }
        catch (Exception e)
        {
            ReportStatus($"Connection to {serverUrl} failed: {e.Message}", StatusLevel.Error);
            return false;
        }
    }

    protected virtual void Update()
    {
        // Drain whatever arrived on the background thread since last frame.
        // All Unity API access (driver, Transforms) happens here, on the main thread.
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

    // Takes the socket as a parameter rather than reading the field, so a
    // reconnect swapping in a new socket can't confuse a loop that's winding down
    private async Task ReceiveLoop(ClientWebSocket socket)
    {
        var buffer = new byte[8192];

        try
        {
            while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                using var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                string json = Encoding.UTF8.GetString(ms.ToArray());
                incoming.Enqueue(json);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown - OnDestroy cancels the pending receive
        }
        catch (WebSocketException e)
        {
            // The bridge went away without a close handshake (e.g. Ctrl+C)
            ReportStatus($"Connection lost: {e.Message}", StatusLevel.Warning);
        }
        catch (Exception e)
        {
            ReportStatus($"Receive loop error: {e.Message}", StatusLevel.Error);
        }
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

    public async Task SelectSession(int sessionId)
    {
        await SendCommand($"{{\"command\":\"select_session\",\"sessionId\":{sessionId}}}");
    }

    public async Task UseLatest()
    {
        await SendCommand("{\"command\":\"use_latest\"}");
    }

    private async Task SendCommand(string json)
    {
        // Not connected (bridge started late, restarted, etc.) - try to connect first
        if (!await EnsureConnectedAsync())
        {
            ReportStatus("Can't send command: not connected to the bridge.", StatusLevel.Warning, consoleDetail: json);
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        // Callers (e.g. UI buttons) fire-and-forget this task, so failures
        // must be logged here or they'd vanish silently
        try
        {
            await sendLock.WaitAsync(cts.Token);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
            }
            finally
            {
                sendLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down - nothing to report
        }
        catch (Exception e)
        {
            ReportStatus($"Failed to send command: {e.Message}", StatusLevel.Error, consoleDetail: json);
        }
    }

    protected virtual async void OnDestroy()
    {
        // Don't leave the shared replay waiting for a twin that no longer exists
        if (clock != null) clock.LeaveReplay(this);

        // Close before cancelling: cancelling the pending ReceiveAsync aborts
        // the socket, after which a close handshake is no longer possible.
        if (socket != null && socket.State == WebSocketState.Open)
        {
            try
            {
                // CloseOutputAsync sends our close frame without waiting for the
                // server's reply, so shutdown can't hang on an unresponsive bridge.
                // The timeout guards against waiting forever on an in-flight send.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await sendLock.WaitAsync(timeout.Token);
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", timeout.Token);
                }
                finally
                {
                    sendLock.Release();
                }
            }
            catch { /* best-effort close on shutdown */ }
        }
        cts?.Cancel();
        socket?.Dispose();
        cts?.Dispose();
    }
}
