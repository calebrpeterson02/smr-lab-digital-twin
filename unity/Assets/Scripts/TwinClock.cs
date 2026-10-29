using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The shared playback clock that keeps every twin showing the same moment.
/// Time is in recorded_at seconds (the robot controller's and IMUs' clocks,
/// which agree to within ~6 ms), so whatever each twin shows at a given frame
/// was recorded at the same instant.
///
/// Replay: waits until every twin replaying a session has its data, then
/// starts them all together from the earliest recorded sample. Long gaps and
/// looping are applied to the shared timeline, so twins stay in step.
///
/// Live: plays a fixed delay behind the freshest sample from ANY twin, so all
/// twins show the same delayed instant even though their data arrives in
/// separate ~1 s batches.
///
/// Pause (replay only) stops replay time, and RobotJointDriver holds every rig
/// still, so all twins freeze on the same recorded moment.
///
/// One per scene. Bridge clients find it automatically (and add one if the
/// scene has none), so it only needs adding by hand to change its settings.
/// </summary>
[DefaultExecutionOrder(-100)] // advance the clock before the bridge clients apply this frame's data
public class TwinClock : MonoBehaviour
{
    [Header("Replay")]
    public float playbackSpeed = 1f;
    public bool loop = true;

    [Tooltip("Pauses in the recording longer than this (seconds, with no data from any twin) are shortened to this.")]
    public float maxGapSeconds = 2f;

    [Tooltip("If one twin's replay data is still loading this long after another's is ready, start without " +
             "it; it joins in step when its data arrives.")]
    public float replayStartTimeoutSeconds = 5f;

    [Header("Live")]
    [Tooltip("The database receives rows in ~1 s batches, so live data arrives in bursts. When on, live rows " +
             "are played at their recorded pace, Live Delay Seconds behind the newest data, instead of " +
             "jumping to the end of each burst. When off, each twin jumps to its newest data (not in sync).")]
    public bool smoothLive = true;

    [Tooltip("How far behind the newest received data live playback runs. Must cover the time between batches " +
             "(~1 s today) or playback catches up and pauses between bursts. Lower it if the pipeline's batch " +
             "interval is shortened.")]
    public float liveDelaySeconds = 1.2f;

    /// <summary>The clock in use, so components like RobotJointDriver can check it without wiring.</summary>
    public static TwinClock Active { get; private set; }

    void OnEnable() { Active = this; }
    void OnDisable() { if (Active == this) Active = null; }

    // --- Pause (replay only) ---

    /// <summary>True while a replay is paused: replay time stops and the rigs hold still.</summary>
    public bool Paused { get; private set; }

    /// <summary>Pausing only applies while some twin is replaying (or loading) a session.</summary>
    public bool CanPause => participants.Count > 0;

    /// <summary>Pauses or resumes the replay. Returns false if nothing is replaying.</summary>
    public bool TogglePause()
    {
        if (!Paused && !CanPause) return false;
        Paused = !Paused;
        return true;
    }

    // --- Live ---

    private double liveOffset;  // recorded_at clock minus this machine's clock (seconds)
    private bool hasLiveOffset;

    /// <summary>The recorded_at time live twins should be showing now.</summary>
    public double LiveTime => Time.realtimeSinceStartupAsDouble + liveOffset - liveDelaySeconds;

    /// <summary>
    /// Bridge clients report every live sample's recorded time. The clock follows
    /// the freshest one seen from any twin; using the data's own timestamps means
    /// this PC's clock doesn't need to agree with the robot's or the IMUs'.
    /// </summary>
    public void ObserveLiveSample(double recordedAt)
    {
        double offset = recordedAt - Time.realtimeSinceStartupAsDouble;
        if (!hasLiveOffset || offset > liveOffset)
        {
            liveOffset = offset;
            hasLiveOffset = true;
        }
    }

    // --- Replay ---

    private class Participant
    {
        public bool ready;
        public double firstTime;
    }

    private readonly Dictionary<BridgeConnection, Participant> participants = new Dictionary<BridgeConnection, Participant>();
    private float firstReadyAt = -1f;   // unscaled time the first participant became ready
    private double replayStart;

    /// <summary>True once the shared replay timeline is running.</summary>
    public bool ReplayRunning { get; private set; }

    /// <summary>The recorded_at time replaying twins should be showing now.</summary>
    public double ReplayTime { get; private set; }

    /// <summary>Changes every time the replay (re)starts; clients rewind to their first item when it does.</summary>
    public int ReplayCycle { get; private set; }

    /// <summary>A client started loading a replay. Holds the timeline until it's ready (or the timeout passes).</summary>
    public void JoinReplay(BridgeConnection client)
    {
        participants[client] = new Participant();
        // A new session is loading: restart the shared timeline once everyone has data.
        // Keep the timeout running if another twin already has its data.
        ReplayRunning = false;
        Paused = false; // a newly requested session plays rather than sitting paused
        bool anyReady = false;
        foreach (var p in participants.Values) anyReady |= p.ready;
        if (!anyReady) firstReadyAt = -1f;
    }

    /// <summary>A client has all of its replay data; firstTime is its earliest recorded sample.</summary>
    public void ReplayReady(BridgeConnection client, double firstTime)
    {
        if (!participants.TryGetValue(client, out var p)) return;
        p.ready = true;
        p.firstTime = firstTime;
        if (firstReadyAt < 0f) firstReadyAt = Time.unscaledTime;
    }

    /// <summary>A client went live, had no data, or was destroyed.</summary>
    public void LeaveReplay(BridgeConnection client)
    {
        participants.Remove(client);
        if (participants.Count == 0)
        {
            ReplayRunning = false;
            firstReadyAt = -1f;
            Paused = false; // back to live: nothing left to pause
        }
    }

    void Update()
    {
        if (participants.Count == 0) return;

        if (!ReplayRunning)
        {
            TryStartReplay();
            return;
        }

        if (Paused) return; // replay time stands still

        ReplayTime += Time.deltaTime * playbackSpeed;

        // Earliest item any ready twin still has to play
        double next = double.PositiveInfinity;
        foreach (var pair in participants)
        {
            if (pair.Value.ready) next = System.Math.Min(next, pair.Key.NextReplayItemTime);
        }

        if (double.IsPositiveInfinity(next))
        {
            // Every twin has played everything
            if (loop) Restart(replayStart);
        }
        else if (next - ReplayTime > maxGapSeconds)
        {
            // Nothing to show for a while: skip ahead, leaving a maxGapSeconds pause
            ReplayTime = next - maxGapSeconds;
        }
    }

    private void TryStartReplay()
    {
        int ready = 0;
        double start = double.PositiveInfinity;
        foreach (var p in participants.Values)
        {
            if (!p.ready) continue;
            ready++;
            start = System.Math.Min(start, p.firstTime);
        }

        if (ready == 0) return;
        bool everyoneReady = ready == participants.Count;
        bool timedOut = firstReadyAt >= 0f && Time.unscaledTime - firstReadyAt >= replayStartTimeoutSeconds;
        if (!everyoneReady && !timedOut) return;

        replayStart = start;
        Restart(start);
        ReplayRunning = true;
    }

    private void Restart(double time)
    {
        ReplayTime = time;
        ReplayCycle++;
    }
}
