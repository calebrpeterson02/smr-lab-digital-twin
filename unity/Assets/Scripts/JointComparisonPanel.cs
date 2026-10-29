using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// A table comparing each twin's joint angles, J1-J6, as the rigs are showing
/// them right now (so it matches what you see side by side or overlaid):
///
///   Joint   Robot     IMU   Camera   |IMU-Robot|  |Camera-Robot|
///   J1      30.00   30.01      N/A          0.01             N/A
///
/// Put it on a UI object with a TextMeshPro text (or point Table Text at one).
/// The text box needs to be about 35 em wide (35 x the font size) and 7.5 lines tall.
///
/// J3: the rigs store it relative to the lower arm (the Unity hierarchy needs
/// that), while the robot controller reports it relative to horizontal. By
/// default the table converts every twin to the controller's convention
/// (J3 = rig J3 - rig J2), so the numbers match the data and the teach pendant;
/// see j3InControllerConvention.
/// </summary>
public class JointComparisonPanel : MonoBehaviour
{
    [Header("Twins (leave empty for N/A)")]
    public RobotJointDriver robotTwin;
    public RobotJointDriver imuTwin;
    public RobotJointDriver cameraTwin;

    [Tooltip("How many joints, from J1, the IMU twin actually measures. J6 has no IMU yet, so it " +
             "only holds its start-pose value - shown as N/A.")]
    [Range(0, 6)] public int imuMeasuredJoints = 5;

    [Tooltip("How many joints, from J1, the camera twin measures (once it exists).")]
    [Range(0, 6)] public int cameraMeasuredJoints = 6;

    [Header("Display")]
    public TMP_Text tableText;

    [Tooltip("On: J3 relative to horizontal, as the robot controller reports it (matches the data). " +
             "Off: J3 relative to the lower arm, as the Unity rig stores it.")]
    public bool j3InControllerConvention = true;

    [Tooltip("Table refreshes per second. The numbers change too fast to read at every frame.")]
    public float refreshPerSecond = 10f;

    [Tooltip("Differences at or above this (degrees) are shown in the warning color.")]
    public float warnDifference = 2f;
    [Tooltip("Differences at or above this (degrees) are shown in the error color.")]
    public float errorDifference = 5f;
    public Color warnColor = new Color(1f, 0.85f, 0.3f);
    public Color errorColor = new Color(1f, 0.4f, 0.4f);

    private float nextRefresh;
    private readonly StringBuilder text = new StringBuilder();

    void Awake()
    {
        // Table Text left empty: use a text component on this same object, if there is one
        if (tableText == null) tableText = GetComponent<TMP_Text>();
        if (tableText == null)
        {
            Debug.LogWarning("JointComparisonPanel: Table Text isn't set and this object has no TextMeshPro text, " +
                             "so there's nowhere to show the table.");
        }
    }

    void Update()
    {
        if (tableText == null || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 1f / Mathf.Max(refreshPerSecond, 0.1f);
        tableText.text = BuildTable();
    }

    // Table layout, in em (multiples of the font size). Each column is right-aligned
    // at its right edge. Headers and joint labels use the normal font; only the
    // numbers are monospaced (digits all the same width), so they line up without
    // spacing out the letters.
    private static readonly (string header, float rightEdgeEm)[] Columns =
    {
        ("Robot", 7.5f), ("IMU", 13f), ("Camera", 18.5f), ("|IMU-Robot|", 26f), ("|Camera-Robot|", 34f),
    };
    private const int NumberChars = 7;      // fits "-180.00"
    private const float NumberCharEm = 0.6f;

    private float[] headerWidthEm;          // measured in the actual font, so headers can be right-aligned too
    private float measuredAtFontSize;

    private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;

    private string BuildTable()
    {
        MeasureHeaders();
        text.Clear();

        text.Append("Joint");
        for (int c = 0; c < Columns.Length; c++)
        {
            Pos(Columns[c].rightEdgeEm - headerWidthEm[c]).Append(Columns[c].header);
        }
        text.Append('\n');

        for (int j = 0; j < 6; j++)
        {
            float? robot = Angle(robotTwin, j, 6);
            float? imu = Angle(imuTwin, j, imuMeasuredJoints);
            float? cam = Angle(cameraTwin, j, cameraMeasuredJoints);

            text.Append('J').Append(j + 1);
            NumberCell(0, Format(robot));
            NumberCell(1, Format(imu));
            NumberCell(2, Format(cam));
            DifferenceCell(3, imu, robot);
            DifferenceCell(4, cam, robot);
            text.Append('\n');
        }
        return text.ToString();
    }

    // Header widths depend on the font and its size, so measure them (again if the size changes)
    private void MeasureHeaders()
    {
        if (headerWidthEm != null && measuredAtFontSize == tableText.fontSize) return;
        measuredAtFontSize = tableText.fontSize;
        headerWidthEm = new float[Columns.Length];
        for (int c = 0; c < Columns.Length; c++)
        {
            headerWidthEm[c] = tableText.GetPreferredValues(Columns[c].header).x / Mathf.Max(tableText.fontSize, 1f);
        }
    }

    // <pos> moves the next text to a fixed horizontal position. Invariant culture:
    // a decimal comma (e.g. "7,5em") would break the tag.
    private StringBuilder Pos(float em)
    {
        return text.Append("<pos=").Append(em.ToString("0.###", Invariant)).Append("em>");
    }

    private void NumberCell(int column, string value, Color? color = null)
    {
        Pos(Columns[column].rightEdgeEm - NumberChars * NumberCharEm);
        if (color is Color c) text.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(c)).Append('>');
        text.Append("<mspace=").Append(NumberCharEm.ToString("0.###", Invariant)).Append("em>")
            .Append(value.PadLeft(NumberChars)).Append("</mspace>");
        if (color != null) text.Append("</color>");
    }

    /// <summary>
    /// The angle a rig is showing for joint index j (0 = J1), or null for N/A:
    /// no rig assigned, no data received yet, or a joint that source doesn't measure.
    /// </summary>
    private float? Angle(RobotJointDriver rig, int j, int measuredJoints)
    {
        if (rig == null || !rig.HasReceivedData || j >= measuredJoints) return null;
        if (rig.joints == null || j >= rig.joints.Length) return null;

        float angle = rig.joints[j].currentAngle;
        if (j == 2 && j3InControllerConvention && rig.joints.Length > 1)
        {
            // Rig J3 is relative to the lower arm; the controller's is relative to horizontal.
            // Inverse of SetTargetAnglesFromRobotTelemetry's (rig J3 = joint_3 + joint_2).
            angle -= rig.joints[1].currentAngle;
        }
        return angle;
    }

    // |a - b|, colored by the warning/error thresholds, or N/A if either side has no value
    private void DifferenceCell(int column, float? a, float? b)
    {
        if (a is not float x || b is not float y)
        {
            NumberCell(column, "N/A");
            return;
        }

        // Shortest way around, so 179 vs -179 is a 2 degree difference, not 358
        float diff = Mathf.Abs(Mathf.DeltaAngle(y, x));
        Color? color = diff >= errorDifference ? errorColor : diff >= warnDifference ? warnColor : (Color?)null;
        NumberCell(column, diff.ToString("F2", Invariant), color);
    }

    private static string Format(float? angle)
    {
        return angle is float a ? a.ToString("F2", Invariant) : "N/A";
    }
}
