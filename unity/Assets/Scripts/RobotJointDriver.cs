using UnityEngine;

/// Drives the robot rig kinematically (direct Transform rotation, no physics) from
/// live joint-angle data. Attach to the root of the rig. Call SetTargetAngles()
/// whenever new data arrives from a source adapter (robot telemetry, IMU, ArUco).
public class RobotJointDriver : MonoBehaviour
{
    [System.Serializable]
    public class JointConfig
    {
        public Transform jointTransform;

        [Tooltip("Local axis this joint rotates around, e.g. (0,0,1) for local Z. " +
                 "Verify per-joint in Blender/Unity - not all joints necessarily share the same axis.")]
        public Vector3 rotationAxis = Vector3.forward;

        [Tooltip("Degrees, from the FANUC datasheet motion range for this axis.")]
        public float minAngle = -180f;
        public float maxAngle = 180f;

        [Tooltip("Degrees/sec, from the FANUC datasheet max speed for this axis. Caps how " +
                 "fast the visual joint can move between data updates, to avoid snapping.")]
        public float maxSpeed = 105f;

        [HideInInspector] public float currentAngle;
        [HideInInspector] public float targetAngle;
        [HideInInspector] public float speed;   // deg/s toward the current target (never above maxSpeed)
    }

    [Header("Joints in order: J1, J2, J3, J4, J5, J6")]
    public JointConfig[] joints = new JointConfig[6];

    /// <summary>False until the first SetTargetAngles call, so displays can tell "no data yet" from a real 0.</summary>
    public bool HasReceivedData { get; private set; }

    [Header("Balancer (geometric, not independently driven)")]
    public Transform balancerCylinder;
    public Transform balancerRod;
    public Transform balancerA; // fixed pivot on the turret
    public Transform balancerB; // moving pivot on the lower arm (J2)


    /// Feed new joint angles here (degrees), in order J1-J6. Each angle is relative to
    /// the previous link, matching the rig's hierarchy (IMU and camera adapters compute
    /// angles this way). Values are clamped to each joint's configured limits before
    /// being used as the new interpolation target.
    ///
    /// arriveInSeconds > 0 moves each joint at a steady speed so it reaches the target
    /// in that time - for data sampled less often than Unity renders frames, so motion
    /// stays smooth instead of stepping to each sample and waiting for the next.
    /// 0 (the default) moves at each joint's maxSpeed. Speed never exceeds maxSpeed.
    public void SetTargetAngles(float j1, float j2, float j3, float j4, float j5, float j6, float arriveInSeconds = 0f)
    {
        HasReceivedData = true;
        float[] incoming = { j1, j2, j3, j4, j5, j6 };
        for (int i = 0; i < joints.Length && i < incoming.Length; i++)
        {
            var joint = joints[i];
            joint.targetAngle = Mathf.Clamp(incoming[i], joint.minAngle, joint.maxAngle);
            joint.speed = arriveInSeconds > 0f
                ? Mathf.Min(Mathf.Abs(joint.targetAngle - joint.currentAngle) / arriveInSeconds, joint.maxSpeed)
                : joint.maxSpeed;
        }
    }

    /// Entry point for the robot controller's own joint angles (live bridge, CSV files).
    /// The controller reports joint_3 as the upper arm's angle relative to the world,
    /// not relative to the lower arm (J2), so it's converted to a chain-relative angle
    /// here. The "+" (rather than "-") is correct only because J2's rotationAxis is
    /// negated in the Inspector; if J2's axis changes, re-derive this sign.
    public void SetTargetAnglesFromRobotTelemetry(float j1, float j2, float j3, float j4, float j5, float j6, float arriveInSeconds = 0f)
    {
        SetTargetAngles(j1, j2, j3 + j2, j4, j5, j6, arriveInSeconds);
    }

    void Update()
    {
        // Replay paused: hold this pose, even if a joint is partway to its target
        var clock = TwinClock.Active;
        if (clock != null && clock.Paused) return;

        float dt = Time.deltaTime;

        foreach (var joint in joints)
        {
            if (joint.jointTransform == null) continue;

            joint.currentAngle = Mathf.MoveTowards(
                joint.currentAngle, joint.targetAngle, joint.speed * dt);

            joint.jointTransform.localRotation =
                Quaternion.AngleAxis(joint.currentAngle, joint.rotationAxis.normalized);
        }

        UpdateBalancer();
    }

    // Updates the balancer cylinder and rod. 
    // Points the balancer cylinder towards balancerB, which is the connection point on the lower arm for the balancer rod. 
    // The rod follows due to being parented to the cylinder.
    // Then, extends or retracts the rod respective to the movement of balancerB as the lower arm rotates.
    void UpdateBalancer()
    {
        if (balancerCylinder == null || balancerRod == null ||
            balancerA == null || balancerB == null) return;

        Vector3 direction = (balancerB.position - balancerA.position).normalized;
        balancerCylinder.rotation = Quaternion.FromToRotation(Vector3.left, direction);
        balancerRod.position = balancerB.position;
    }
}