using UnityEngine;

public class TestJointSweep : MonoBehaviour
{
    public RobotJointDriver driver;

    // Update is called once per frame
    void Update()
    {
        //float t = Mathf.Sin(Time.time) * 30f; // sweeps +- 30 degrees
        driver.SetTargetAngles(0, 0, 30, 0, 0, 0);
    }
}
