using System;
using UnityEngine;

/// <summary>
/// Robot-telemetry client: connects to postgres_bridge.py (port 8765) and
/// drives the robot twin's RobotJointDriver with the controller's own joint
/// angles. Connection, session commands, status and playback in step with the
/// other twins live in BridgeConnection.
/// </summary>
public class BridgeClient : BridgeConnection
{
    public RobotJointDriver driver;

    [Tooltip("Robot data arrives ~17.6 times a second, slower than Unity renders. Consecutive frames closer " +
             "together than this (seconds) are joined by a steady movement instead of a step per frame. " +
             "Larger gaps (pauses, the start of a session) aren't bridged. 0 turns interpolation off.")]
    public float interpolateMaxGapSeconds = 0.5f;

    protected override string DefaultName => "Robot";

    private double lastFrameTime = double.NaN; // recorded time of the previous frame received

    // The robot-specific fields of a "data" message
    [Serializable]
    private class RobotData
    {
        public int frameId;
        public float[] joints;
        public double recordedAt;
    }

    private struct Frame
    {
        public float j1, j2, j3, j4, j5, j6;
    }

    protected override void HandleData(string json)
    {
        var msg = JsonUtility.FromJson<RobotData>(json);
        var frame = new Frame
        {
            j1 = msg.joints[0], j2 = msg.joints[1], j3 = msg.joints[2],
            j4 = msg.joints[3], j5 = msg.joints[4], j6 = msg.joints[5],
        };
        double time = msg.recordedAt;
        double gap = time - lastFrameTime; // NaN for the first frame of a session
        lastFrameTime = time;

        if (gap > 0 && gap <= interpolateMaxGapSeconds)
        {
            // Start moving toward this frame when the shared TwinClock reaches the
            // PREVIOUS frame's time, arriving exactly at this frame's time. The twin
            // then shows the straight line between samples, in step with the other
            // twins, instead of jumping to each sample and waiting ~57 ms for the next.
            AddItem(time - gap, () => Apply(frame, (float)gap));
        }
        else
        {
            // First frame, or after a pause: go straight to it at its own time
            AddItem(time, () => Apply(frame, 0f));
        }
    }

    protected override void OnSessionChanged(int sessionId, bool live)
    {
        lastFrameTime = double.NaN; // don't interpolate across sessions
    }

    private void Apply(Frame f, float recordedSeconds)
    {
        // Replay can run faster or slower than real time; the move takes the matching real time
        float arriveIn = recordedSeconds > 0f ? recordedSeconds / PlaybackSpeed : 0f;
        // Robot telemetry reports joint_3 relative to the world, hence the telemetry-specific entry point
        driver.SetTargetAnglesFromRobotTelemetry(f.j1, f.j2, f.j3, f.j4, f.j5, f.j6, arriveIn);
    }
}
