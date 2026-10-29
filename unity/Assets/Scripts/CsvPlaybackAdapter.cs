using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Reads a recorded session CSV and plays it back through RobotJointDriver, timed using the real recorded_at gaps between rows
/// </summary>
public class CsvPlaybackAdapter : MonoBehaviour
{
    public RobotJointDriver driver;
   
    [Tooltip("Absolute path to the CSV file on disk, e.g. C:/data/Session381_Robot.csv " +
             "or /Users/you/data/Session381_Robot.csv. Works in Editor and Standalone " +
             "builds; won't work in WebGL, which has no filesystem access.")]
    public string csvFilePath;
    
    [Tooltip("1 = real recorded pace. 2 = twice as fast. 0.5 = half speed.")]
    public float playbackSpeed = 1f;
    
    public bool loop = true;
    public bool playOnStart = true;

    struct Frame
    {
        public double recordedAt;
        public float j1, j2, j3, j4, j5, j6;
    }
 
    List<Frame> frames = new List<Frame>();
 
    // CSV column indices, matching: id,ts_epoch, joint_1..6,x,y,z,w,p,r,recorded_at,ingested_at, device_id, session_id, frame_id
    const int COL_J1 = 2, COL_J2 = 3, COL_J3 = 4, COL_J4 = 5, COL_J5 = 6, COL_J6 = 7;
    const int COL_RECORDED_AT = 14;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        ParseCsv();
        if (playOnStart) StartCoroutine(PlaybackRoutine());
    }

    void ParseCsv()
    {
        frames.Clear();
        if (string.IsNullOrEmpty(csvFilePath) || !File.Exists(csvFilePath))
        {
            Debug.LogError($"CsvPlaybackAdapter: file not found at '{csvFilePath}'.");
            return;
        }

        string[] lines = File.ReadAllLines(csvFilePath);

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            string[] fields = line.Split(',');
            if (fields.Length <= COL_RECORDED_AT) continue;

            Frame f = new Frame
            {
                j1 = ParseFloat(fields[COL_J1]),
                j2 = ParseFloat(fields[COL_J2]),
                j3 = ParseFloat(fields[COL_J3]),
                j4 = ParseFloat(fields[COL_J4]),
                j5 = ParseFloat(fields[COL_J5]),
                j6 = ParseFloat(fields[COL_J6]),
                recordedAt = ParseDouble(fields[COL_RECORDED_AT]),
            };
            frames.Add(f);
        }

        Debug.Log($"CsvPlaybackAdapter: parsed {frames.Count} frames from {csvFilePath}.");
    }

    float ParseFloat(string raw) =>
        float.Parse(raw.Trim().Trim('"'), CultureInfo.InvariantCulture);
    
    double ParseDouble(string raw) =>
        double.Parse(raw.Trim().Trim('"'), CultureInfo.InvariantCulture);


    IEnumerator PlaybackRoutine()
    {
        if (frames.Count == 0) yield break;

        do
        {
            driver.SetTargetAnglesFromRobotTelemetry(frames[0].j1, frames[0].j2, frames[0].j3, frames[0].j4, frames[0].j5, frames[0].j6);

            for (int i = 1; i < frames.Count; i++)
            {
                double gap = frames[i].recordedAt - frames[i - 1].recordedAt;

                // Guard against bad/negative timestamps or unreasonably long gaps
                gap = System.Math.Clamp(gap, 0.0, 2.0);

                float wait = (float)(gap / playbackSpeed);
                if (wait > 0f) yield return new WaitForSeconds(wait);

                driver.SetTargetAnglesFromRobotTelemetry(frames[i].j1, frames[i].j2, frames[i].j3, frames[i].j4, frames[i].j5, frames[i].j6);
            }
        } while (loop);
    }
}
