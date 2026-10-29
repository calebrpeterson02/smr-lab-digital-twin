using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Orbits the camera around the twins while keeping the ENTIRE current bounds
/// of whatever it's focused on in view, regardless of pose. Rather than
/// targeting a single fixed point at a fixed distance (which inevitably clips
/// parts out as the robot's shape changes), this recomputes a bounding volume
/// across the focused renderers every frame, and derives both the focus point
/// (its center) and the required camera distance (so the whole volume fits in
/// the camera's field of view) from that. A/D (or Left/Right) orbit
/// horizontally, W/S (or Up/Down) orbit vertically, the scroll wheel zooms.
/// Attach to the camera.
///
/// Twins are numbered for the focus buttons: robotRoot is twin 0, and
/// extraRoots[0], extraRoots[1], ... are twins 1, 2, ...
/// </summary>
[RequireComponent(typeof(Camera))]
public class CameraControl : MonoBehaviour
{
    [Tooltip("Twin 0: root of the robot-telemetry twin. All Renderers under it are combined into one bounding volume every frame.")]
    public Transform robotRoot;

    [Tooltip("Twins 1, 2, ...: the other rigs, e.g. element 0 = IMU twin, element 1 = camera twin. " +
             "Like robotRoot, each should contain only robot renderers.")]
    public Transform[] extraRoots;

    [Header("Speed (degrees per second)")]
    public float horizontalSpeed = 90f;
    public float verticalSpeed = 60f;

    [Header("Vertical limits (degrees above the horizon)")]
    [Range(-89f, 89f)] public float minPitch = -10f;
    [Range(-89f, 89f)] public float maxPitch = 80f;

    [Header("Framing")]
    [Tooltip("Extra breathing room beyond the tightest distance that fits the robot, as a multiplier (1 = no margin).")]
    public float framingMargin = 1.2f;

    [Tooltip("How quickly the camera eases toward the newly-required distance as the robot's bounds change. " +
             "Higher = snappier, lower = smoother/more lagged.")]
    public float distanceSmoothSpeed = 4f;

    [Tooltip("How quickly the camera glides to a new focus point (e.g. after a focus button). " +
             "Higher = snappier, lower = smoother.")]
    public float focusSmoothSpeed = 4f;

    [Header("Zoom (scroll wheel)")]
    [Tooltip("Distance multiplier per scroll notch. 1.15 = each notch moves 15% closer/further.")]
    public float zoomStepPerNotch = 1.15f;
    [Tooltip("Closest zoom, as a fraction of the distance that fits the whole focus in view.")]
    public float minZoom = 0.25f;
    [Tooltip("Furthest zoom, as a multiple of the distance that fits the whole focus in view.")]
    public float maxZoom = 3f;

    private float yaw;
    private float pitch;
    private float currentDistance;
    private Vector3 currentCenter;
    private float zoom = 1f; // multiplies the auto-fit distance; < 1 = closer

    private Camera cam;
    // Renderers per twin, in twin order (0 = robotRoot). An entry is empty if that root isn't assigned.
    private readonly List<Renderer[]> twinRenderers = new List<Renderer[]>();
    private int focusIndex = -1; // -1 = all twins

    void Start()
    {
        cam = GetComponent<Camera>();

        if (robotRoot == null)
        {
            Debug.LogError("CameraControl: robotRoot is not assigned. Nothing to frame.");
            enabled = false;
            return;
        }

        // Cached once - re-querying GetComponentsInChildren every frame would
        // allocate unnecessarily. Each Renderer's own .bounds already reflects
        // its current world-space extent as the robot moves, so we don't need
        // to re-walk the hierarchy every frame, just re-read .bounds.
        twinRenderers.Add(robotRoot.GetComponentsInChildren<Renderer>());
        if (extraRoots != null)
        {
            foreach (var root in extraRoots)
            {
                twinRenderers.Add(root != null ? root.GetComponentsInChildren<Renderer>() : new Renderer[0]);
            }
        }
        if (twinRenderers[0].Length == 0)
        {
            Debug.LogWarning("CameraControl: no Renderers found under robotRoot.");
        }

        Bounds bounds = ComputeFocusBounds();
        currentCenter = bounds.center;

        // Derive starting yaw/pitch/distance from wherever the camera was
        // placed in the scene, relative to the live bounds center.
        Vector3 offset = transform.position - bounds.center;
        currentDistance = Mathf.Max(offset.magnitude, RequiredDistance(bounds));

        if (offset.magnitude < 0.01f)
        {
            yaw = 0f;
            pitch = 20f;
        }
        else
        {
            yaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(Mathf.Asin(offset.y / offset.magnitude) * Mathf.Rad2Deg, minPitch, maxPitch);
        }

        ApplyOrbit(bounds);
    }

    /// <summary>Frame one twin (0 = robot, 1 = first extra root, ...). Hook up to a button's OnClick with the index.</summary>
    public void FocusTwin(int index)
    {
        if (index < 0 || index >= twinRenderers.Count || twinRenderers[index].Length == 0)
        {
            Debug.LogWarning($"CameraControl: there's no twin {index} to focus on (check robotRoot / extraRoots). Keeping the current view.");
            return;
        }
        focusIndex = index;
        zoom = 1f; // a focus button always gives the "everything fits" view
    }

    /// <summary>Frame every twin at once. Hook up to the "all twins" button's OnClick.</summary>
    public void FocusAll()
    {
        focusIndex = -1;
        zoom = 1f;
    }

    // LateUpdate so the robot has finished moving/posing this frame before we frame it
    void LateUpdate()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            float horizontal = 0f, vertical = 0f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) horizontal += 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) horizontal -= 1f;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) vertical += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) vertical -= 1f;

            yaw += horizontal * horizontalSpeed * Time.deltaTime;
            pitch = Mathf.Clamp(pitch + vertical * verticalSpeed * Time.deltaTime, minPitch, maxPitch);
        }

        HandleZoom();
        ApplyOrbit(ComputeFocusBounds());
    }

    private void HandleZoom()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;

        float scroll = mouse.scroll.ReadValue().y;
        if (scroll == 0f) return;

        // Scrolling over a UI panel (e.g. the session controls) shouldn't also zoom
        var eventSystem = EventSystem.current;
        if (eventSystem != null && eventSystem.IsPointerOverGameObject()) return;

        // Scroll units differ by platform/device (a wheel notch can read as 1 or 120),
        // so cap it at one notch per frame; trackpads' small values still zoom finely.
        float notches = Mathf.Clamp(scroll, -1f, 1f);
        // Scrolling up (positive) moves closer, i.e. a smaller distance multiplier
        zoom = Mathf.Clamp(zoom * Mathf.Pow(zoomStepPerNotch, -notches), minZoom, maxZoom);
    }

    // Combined bounds of the focused twin, or of every twin when focusIndex is -1
    private Bounds ComputeFocusBounds()
    {
        Bounds bounds = new Bounds(robotRoot.position, Vector3.zero);
        bool first = true;

        for (int t = 0; t < twinRenderers.Count; t++)
        {
            if (focusIndex >= 0 && t != focusIndex) continue;

            foreach (var r in twinRenderers[t])
            {
                if (r == null || !r.enabled) continue; // a renderer could be disabled/destroyed at runtime
                if (first)
                {
                    bounds = r.bounds;
                    first = false;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }
        }

        return bounds;
    }

    // The distance at which a sphere of the bounds' radius exactly fills the
    // camera's field of view, using whichever axis (vertical or horizontal)
    // is more restrictive, so the whole robot fits in both directions.
    private float RequiredDistance(Bounds bounds)
    {
        float radius = bounds.extents.magnitude;
        if (radius < 0.01f) radius = 0.5f; // guard against a degenerate/empty bounds

        float verticalHalfFovRad = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
        float horizontalFovRad = 2f * Mathf.Atan(Mathf.Tan(verticalHalfFovRad) * cam.aspect);
        float horizontalHalfFovRad = horizontalFovRad * 0.5f;

        float minHalfFovRad = Mathf.Min(verticalHalfFovRad, horizontalHalfFovRad);
        float distance = radius / Mathf.Sin(minHalfFovRad);

        return distance * framingMargin;
    }

    private void ApplyOrbit(Bounds bounds)
    {
        // Ease both the distance and the focus point, so switching focus glides
        // rather than jumps. 1 - exp(-speed * dt) gives the same smoothing at any frame rate.
        float desiredDistance = RequiredDistance(bounds) * zoom;
        currentDistance = Mathf.Lerp(currentDistance, desiredDistance, 1f - Mathf.Exp(-distanceSmoothSpeed * Time.deltaTime));
        currentCenter = Vector3.Lerp(currentCenter, bounds.center, 1f - Mathf.Exp(-focusSmoothSpeed * Time.deltaTime));

        // Negative pitch on X tilts forward upward, so positive pitch = camera above the target
        Vector3 direction = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
        transform.position = currentCenter + direction * currentDistance;
        transform.LookAt(currentCenter);
    }
}
