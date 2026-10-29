using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// IMU client: connects to imu_bridge.py (port 8766), estimates joint angles
/// from the IMUs mounted on the arm, and drives the IMU twin's own
/// RobotJointDriver.
///
/// How a joint angle is estimated: each IMU reports its orientation relative
/// to the pose it was tared at (the session's start pose). The rotation of an
/// IMU relative to the IMU on the link before it is that joint's rotation;
/// keeping only the part about the joint's axis (a "twist") gives the angle.
/// J1 is measured against the fixed base, so its IMU's orientation is used
/// directly. Adding the robot's start-pose angle turns "rotation since the
/// tare" into an absolute joint angle.
///
/// A joint can instead read one of its IMU's raw angles (yaw, pitch or roll)
/// directly, optionally minus one of another joint's IMU angles. J4 does:
/// IMU 279's pitch tracks the robot's joint_4 within ~2 deg, while its full
/// orientation has 180 deg jumps that break the twist method.
///
/// J5 uses the arm's forward kinematics (KinematicWrist): IMU 283 measures the
/// whole wrist link's rotation, which mixes J1, J3 and J4 into its yaw/pitch/
/// roll in pose-dependent amounts, so no single-angle subtraction isolates J5.
/// Instead the J4 link is rebuilt from IMU 282 (J3 link) and the J4 angle, and
/// J5 is the wrist link's rotation relative to it. See "J5 IMU Kinematics".
///
/// The quaternions are only used for this arithmetic and never applied to a
/// Transform, so they stay in the IMU's own (right-handed) frame - no
/// conversion to Unity's left-handed axes is needed.
/// </summary>
public class ImuBridgeClient : BridgeConnection
{
    [Tooltip("RobotJointDriver on the IMU twin's rig - not the robot-telemetry rig.")]
    public RobotJointDriver driver;

    public enum Method
    {
        RelativeTwist,  // rotation relative to the previous joint's IMU, about `axis`
        ImuYaw,         // this joint's IMU's raw yaw
        ImuPitch,       // this joint's IMU's raw pitch
        ImuRoll,        // this joint's IMU's raw roll
        KinematicWrist, // J5 only: wrist link (IMU 283) relative to the J4 link rebuilt from IMU 282 + J4
    }

    public enum ReferenceChannel
    {
        SameAsMethod,   // subtract the same angle (yaw/pitch/roll) as the joint itself reads
        Yaw,
        Pitch,
        Roll,
    }

    [Serializable]
    public class JointAxis
    {
        [Tooltip("How this joint's angle is estimated from the IMUs.")]
        public Method method = Method.RelativeTwist;

        [Tooltip("RelativeTwist only: this joint's rotation axis, in the IMU frame at the start (tare) pose.")]
        public Vector3 axis;

        [Tooltip("Raw methods only: subtract an angle from this joint's IMU (0 = none), to cancel " +
                 "motion this IMU picks up from joints further up the arm. E.g. J5 uses its yaw minus J3's pitch.")]
        public int referenceJoint;

        [Tooltip("Which of the reference joint's IMU angles to subtract.")]
        public ReferenceChannel referenceChannel = ReferenceChannel.SameAsMethod;

        [Tooltip("+1 or -1, so the estimated angle uses the same sign convention as the robot.")]
        public float sign = 1f;
    }

    // Defaults checked against robot telemetry (sessions 499-513): J1, J2 under
    // ~1 deg error; J4 (raw pitch) ~2 deg. J2 and J3 are pitch (the Y axis in the
    // IMU's Z-Y-X convention). J3's sign follows from IMU 282's pitch = -joint_3
    // and IMU 281's pitch = joint_2 over the recorded motion, but no session bends
    // the elbow relative to the lower arm yet. J5 uses the kinematic method: within
    // 0.47-0.99 deg of the robot in 12 sessions (7 not used for fitting), versus 5.6-10.2
    // deg for IMU 283's yaw minus IMU 282's pitch. J5 never moves in the recorded
    // sessions, so accuracy during real J5 motion is untested.
    [Header("Joint estimation, J1-J5 (no J6 IMU yet)")]
    public JointAxis[] jointAxes =
    {
        new JointAxis { axis = new Vector3(0, 0, 1), sign = 1f },   // J1: vertical
        new JointAxis { axis = new Vector3(0, 1, 0), sign = 1f },   // J2: pitch, relative to J1's IMU
        new JointAxis { axis = new Vector3(0, 1, 0), sign = -1f },  // J3: pitch, relative to J2's IMU
        new JointAxis { method = Method.ImuPitch, axis = new Vector3(0, 1, 0), sign = 1f },  // J4: IMU 279's pitch
        new JointAxis { method = Method.KinematicWrist, axis = new Vector3(0, 1, 0), sign = 1f },  // J5: kinematic
    };

    // How IMUs 282 and 283 sit on their links, fitted from sessions 514/510/451/418/415
    // against the controller's own pose data. Each is a rotation from the sensor's axes
    // to the link's, as a quaternion (w, x, y, z): 282 is ~aligned with an 8.2 deg tilt,
    // 283 is turned ~90 deg about Y with a 2.9 deg tilt. Refit if a sensor is remounted.
    [Header("Kinematic J5 (fitted IMU mountings - don't edit by hand)")]
    [Tooltip("IMU 282 (J3 link) mounting, quaternion (w, x, y, z).")]
    public Vector4 j3ImuMounting = new Vector4(0.997445f, 0.023445f, -0.054954f, -0.039162f);
    [Tooltip("IMU 283 (J5 link) mounting, quaternion (w, x, y, z).")]
    public Vector4 j5ImuMounting = new Vector4(0.720940f, -0.003781f, -0.692828f, 0.014837f);

    [Tooltip("Robot joint angles (degrees, J1-J6) at the pose the IMUs were tared in - the start pose " +
             "of every session so far. J6 has no IMU, so it stays at this value.")]
    public float[] startPoseAngles = { 0f, 0f, 0f, 0f, -90f, 0f };

    protected override string DefaultName => "IMU";

    // The IMU-specific fields of a "data" message
    [Serializable]
    private class ImuData
    {
        public Sample[] samples;
    }

    [Serializable]
    private class Sample
    {
        public int deviceId;
        public int joint;           // 1-5; 0 = IMU not mapped to a joint by the bridge
        public double recordedAt;   // seconds
        public float[] quat;        // w, x, y, z
        public float[] ypr;         // raw yaw, pitch, roll in degrees
    }

    // Latest orientation and raw angles per joint, indexed by joint number
    // (index 0 unused). Nullable: a joint has nothing until its IMU has reported.
    private readonly Quaternion?[] latest = new Quaternion?[7];
    private readonly Vector3?[] latestYpr = new Vector3?[7];
    private readonly float[] angles = new float[6];

    // Called when the component is first added in the editor (or Reset from its menu)
    void Reset()
    {
        serverUrl = "ws://localhost:8766";
    }

    protected override void HandleData(string json)
    {
        var msg = JsonUtility.FromJson<ImuData>(json);
        if (msg.samples == null) return;

        // Each sample just updates its joint's latest orientation when the shared
        // TwinClock reaches its recorded time; angles are recomputed once per frame
        // in AfterItemsApplied, not once per sample.
        foreach (var sample in msg.samples) AddItem(sample.recordedAt, () => Store(sample));
    }

    protected override void AfterItemsApplied()
    {
        ApplyAngles();
    }

    protected override void OnSessionChanged(int sessionId, bool live)
    {
        // Orientations from the previous session were tared differently; don't mix them in
        Array.Clear(latest, 0, latest.Length);
        Array.Clear(latestYpr, 0, latestYpr.Length);
    }

    private void Store(Sample s)
    {
        if (s.joint < 1 || s.joint >= latest.Length) return;
        if (s.quat != null && s.quat.Length == 4)
        {
            // Unity's Quaternion constructor takes (x, y, z, w); the bridge sends w first
            latest[s.joint] = new Quaternion(s.quat[1], s.quat[2], s.quat[3], s.quat[0]);
        }
        if (s.ypr != null && s.ypr.Length == 3)
        {
            latestYpr[s.joint] = new Vector3(s.ypr[0], s.ypr[1], s.ypr[2]); // x = yaw, y = pitch, z = roll
        }
    }

    private void ApplyAngles()
    {
        for (int j = 1; j <= 6; j++)
        {
            float startAngle = j <= startPoseAngles.Length ? startPoseAngles[j - 1] : 0f;
            angles[j - 1] = startAngle;

            if (j > jointAxes.Length) continue;
            var config = jointAxes[j - 1];

            if (config.method == Method.KinematicWrist)
            {
                // Absolute angle in the controller's convention; uses this frame's J4 (angles[3])
                if (TryKinematicWrist(angles[3], out float wrist)) angles[j - 1] = wrist;
                continue;
            }

            if (config.method != Method.RelativeTwist)
            {
                // Read one of this joint's IMU angles directly
                if (latestYpr[j] is not Vector3 ypr) continue;
                float raw = Channel(ypr, config.method);

                if (config.referenceJoint > 0)
                {
                    int r = config.referenceJoint;
                    if (r >= latestYpr.Length || latestYpr[r] is not Vector3 refYpr) continue; // reference IMU hasn't reported yet
                    float reference = config.referenceChannel switch
                    {
                        ReferenceChannel.Yaw => refYpr.x,
                        ReferenceChannel.Pitch => refYpr.y,
                        ReferenceChannel.Roll => refYpr.z,
                        _ => Channel(refYpr, config.method),
                    };
                    // DeltaAngle keeps the difference in -180..180 when either angle wraps past +/-180
                    raw = Mathf.DeltaAngle(reference, raw);
                }

                angles[j - 1] = startAngle + config.sign * raw;
                continue;
            }

            if (latest[j] is not Quaternion child) continue;

            Quaternion relative;
            if (j == 1)
            {
                relative = child; // J1's parent is the fixed base, which the tare already aligned with
            }
            else if (latest[j - 1] is Quaternion parent)
            {
                // Rotation of this link relative to the previous link
                relative = Quaternion.Inverse(parent) * child;
            }
            else
            {
                continue; // parent IMU hasn't reported yet
            }

            angles[j - 1] = startAngle + config.sign * TwistDegrees(relative, config.axis);
        }

        // Chain-relative angles, so the plain entry point (no robot-telemetry J3 correction)
        driver.SetTargetAngles(angles[0], angles[1], angles[2], angles[3], angles[4], angles[5]);
    }

    /// <summary>
    /// J5 from the arm's forward kinematics (fitted to the controller's own pose data):
    ///   J3 link  L3 = Rz(J1) . Ry(-J3)          (J3 relative to horizontal)
    ///   J4 link  L4 = L3 . Rx(-J4)
    ///   J5 link  L5 = L4 . Ry(-J5)              (J5 = 0 straight along the forearm, -90 = tool down, + = up)
    /// L3 comes from IMU 282's full orientation (which already carries J1), L5 from
    /// IMU 283's, each as: link at the start pose . mounting . reading since the tare .
    /// mounting^-1. Then L4^-1 . L5 = Ry(-J5). False until both IMUs have reported.
    /// </summary>
    private bool TryKinematicWrist(float j4, out float j5)
    {
        j5 = 0f;
        if (latest[3] is not Quaternion imu282 || latest[5] is not Quaternion imu283) return false;

        // Start (tare) pose. startPoseAngles are rig angles, where J3 is relative to the
        // lower arm; the kinematics use the controller's J3 (relative to horizontal).
        float Start(int i) => i < startPoseAngles.Length ? startPoseAngles[i] : 0f;
        Quaternion l3Start = Rz(Start(0)) * Ry(-(Start(2) - Start(1)));
        Quaternion l5Start = l3Start * Rx(-Start(3)) * Ry(-Start(4));

        Quaternion m3 = FromWxyz(j3ImuMounting), m5 = FromWxyz(j5ImuMounting);
        Quaternion l3 = l3Start * m3 * imu282 * Quaternion.Inverse(m3);
        Quaternion l5 = l5Start * m5 * imu283 * Quaternion.Inverse(m5);
        Quaternion l4 = l3 * Rx(-j4);

        // The wrist relative to the J4 link should be a pure turn about Y by -J5
        j5 = -TwistDegrees(Quaternion.Inverse(l4) * l5, new Vector3(0, 1, 0));
        return true;
    }

    // Rotations about the robot's base axes (degrees). Pure quaternion arithmetic in
    // the IMUs' right-handed frame; never applied to a Unity Transform.
    private static Quaternion Rx(float degrees) => Quaternion.AngleAxis(degrees, new Vector3(1, 0, 0));
    private static Quaternion Ry(float degrees) => Quaternion.AngleAxis(degrees, new Vector3(0, 1, 0));
    private static Quaternion Rz(float degrees) => Quaternion.AngleAxis(degrees, new Vector3(0, 0, 1));

    // Inspector fields store (w, x, y, z); Unity's Quaternion constructor takes (x, y, z, w)
    private static Quaternion FromWxyz(Vector4 q) => new Quaternion(q.y, q.z, q.w, q.x).normalized;

    // The raw angle a method reads: x = yaw, y = pitch, z = roll
    private static float Channel(Vector3 ypr, Method method)
    {
        return method switch
        {
            Method.ImuYaw => ypr.x,
            Method.ImuPitch => ypr.y,
            _ => ypr.z,
        };
    }

    /// <summary>
    /// Swing-twist decomposition: how far q rotates about `axis`, in degrees (-180..180].
    /// A unit quaternion is (cos(a/2), sin(a/2) * rotationAxis); projecting its
    /// vector part onto `axis` keeps only the rotation about that axis.
    /// </summary>
    private static float TwistDegrees(Quaternion q, Vector3 axis)
    {
        axis.Normalize();
        float projection = q.x * axis.x + q.y * axis.y + q.z * axis.z;
        float degrees = 2f * Mathf.Atan2(projection, q.w) * Mathf.Rad2Deg;
        return Mathf.DeltaAngle(0f, degrees); // q and -q are the same rotation; wrap into range
    }
}
