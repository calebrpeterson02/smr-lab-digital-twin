using TMPro;
using UnityEngine;

/// <summary>
/// Switches the twins between side by side (where they were placed in the
/// scene) and overlaid on top of each other, so differences between data
/// sources are easy to see. Hook a button's OnClick() to ToggleOverlay().
///
/// Overlaid, every twin moves onto the first one in the list, then is enlarged
/// very slightly around the camera's position (redone every frame as the camera
/// orbits), which puts it a few millimetres further away along every line of
/// sight while covering exactly the same pixels. Two identical meshes in the
/// same place would otherwise flicker where their surfaces coincide
/// ("z-fighting"); this way, wherever the twins agree, the first twin's surface
/// is always the nearer one, so its color shows. A moved twin shows through only
/// where it really differs. Give each twin its own TwinTint color.
/// </summary>
[DefaultExecutionOrder(100)] // after CameraControl has moved the camera this frame
public class TwinLayout : MonoBehaviour
{
    [Tooltip("Root of each twin (the same objects as CameraControl's Robot Root / Extra Roots). " +
             "The first stays where it is and shows in front where the twins agree; the others move onto it.")]
    public Transform[] twins;

    [Tooltip("Seconds to move between layouts. 0 = instant.")]
    public float transitionSeconds = 0.5f;

    [Tooltip("While overlaid, how far (metres) the moved twins sit behind the first one along the camera's " +
             "line of sight. Differences in depth smaller than this are hidden; too small and coinciding " +
             "surfaces flicker. Sideways differences always show.")]
    public float overlayDepthOffset = 0.005f;

    [Tooltip("Camera the twins are viewed through. Empty = the main camera.")]
    public Camera viewCamera;

    [Header("Button label (optional)")]
    public TMP_Text toggleButtonLabel;
    public string overlayLabel = "Overlay twins";
    public string sideBySideLabel = "Side by side";

    /// <summary>True when the twins are (or are moving to be) overlaid.</summary>
    public bool Overlaid { get; private set; }

    // Each twin's side-by-side placement, recorded at startup
    private Vector3[] homePosition;
    private Quaternion[] homeRotation;
    private Vector3[] homeScale;
    private float blend;        // 0 = side by side, 1 = overlaid
    private bool labelDirty = true;

    void Start()
    {
        if (twins == null) twins = new Transform[0];
        homePosition = new Vector3[twins.Length];
        homeRotation = new Quaternion[twins.Length];
        homeScale = new Vector3[twins.Length];
        for (int i = 0; i < twins.Length; i++)
        {
            if (twins[i] == null) continue;
            homePosition[i] = twins[i].position;
            homeRotation[i] = twins[i].rotation;
            homeScale[i] = twins[i].localScale;
        }
        if (twins.Length < 2 || twins[0] == null)
        {
            Debug.LogWarning("TwinLayout: needs at least two twins, the first one assigned, to overlay anything.");
        }
        else
        {
            // Easy to get backwards in the Inspector, so say which twin wins where they agree
            Debug.Log($"TwinLayout: when overlaid, '{twins[0].name}' stays in place and shows in front; " +
                      "the others move onto it, just behind.");
        }
    }

    /// <summary>Hook up to the overlay button's OnClick().</summary>
    public void ToggleOverlay() => SetOverlay(!Overlaid);

    public void SetOverlay(bool overlaid)
    {
        Overlaid = overlaid;
        labelDirty = true;
    }

    // LateUpdate: the overlaid placement depends on where the camera is this frame
    void LateUpdate()
    {
        if (labelDirty && toggleButtonLabel != null)
        {
            // The label names what pressing the button will do next
            toggleButtonLabel.text = Overlaid ? sideBySideLabel : overlayLabel;
            labelDirty = false;
        }

        if (homePosition == null || twins.Length < 2 || twins[0] == null) return;

        float target = Overlaid ? 1f : 0f;
        // Side by side and settled: nothing to do. Overlaid, keep updating - the camera may move.
        if (blend == target && !Overlaid) return;

        // Unscaled time, so the layout still changes while a replay is paused
        blend = transitionSeconds > 0f
            ? Mathf.MoveTowards(blend, target, Time.unscaledDeltaTime / transitionSeconds)
            : target;
        float s = Mathf.SmoothStep(0f, 1f, blend); // ease in and out

        // Overlaid placement: the first twin's position and rotation, then enlarged
        // around the CAMERA by a factor just over 1. Scaling around the camera moves
        // every point straight back along its own line of sight, so from the camera
        // the moved twin covers exactly the same pixels, only slightly further away -
        // behind the first twin everywhere they agree, whatever the angle. (Sliding the
        // whole twin backwards instead isn't exact: off-centre faces turned toward the
        // slide direction can end up in front.)
        var cam = viewCamera != null ? viewCamera : Camera.main;
        Vector3 overlaidPosition = homePosition[0];
        float overlaidScale = 1f;
        if (cam != null)
        {
            Vector3 camPos = cam.transform.position;
            float distance = (homePosition[0] - camPos).magnitude;
            if (distance > 0.01f)
            {
                overlaidScale = 1f + overlayDepthOffset / distance; // ~overlayDepthOffset further back at the twin
                overlaidPosition = camPos + (homePosition[0] - camPos) * overlaidScale;
            }
        }

        for (int i = 1; i < twins.Length; i++)
        {
            if (twins[i] == null) continue;
            twins[i].SetPositionAndRotation(
                Vector3.Lerp(homePosition[i], overlaidPosition, s),
                Quaternion.Slerp(homeRotation[i], homeRotation[0], s));
            twins[i].localScale = homeScale[i] * Mathf.Lerp(1f, overlaidScale, s);
        }
    }
}
