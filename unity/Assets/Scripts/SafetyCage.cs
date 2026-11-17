using UnityEngine;

/// <summary>
/// Builds a simple safety cage around the twins when Play starts: yellow posts,
/// black-framed wire-mesh panels, a top rail, an orange-framed door, and yellow
/// floor tape just inside the fence.
///
/// Placement is automatic: the cage is a square centred between the twins' J1
/// (base) axes, turned to face the way the robots face, with its floor at this
/// GameObject's height. The door goes on the chosen side relative to the robot.
///
/// All sizes are real-world metres. The robot model's units aren't necessarily
/// metres, so the cage is scaled using the robot itself as a ruler: the distance
/// between the J2 and J3 pivots in the model is the real lower arm's 1075 mm.
/// On Play the Console shows the resulting scale and how tall the model robot is
/// at that scale, as a sanity check.
///
/// The wire mesh is one quad per panel with a generated grid texture, cut out by
/// alpha clipping, rather than modelled wires: a handful of triangles per panel,
/// cheap enough for WebGL, and the camera can always see the arm through it.
///
/// The cage isn't built in the editor (generated objects would be saved into the
/// scene); select this GameObject to see its outline as a gizmo instead.
/// </summary>
public class SafetyCage : MonoBehaviour
{
    public enum Side { RobotFront, RobotRight, RobotBack, RobotLeft } // order matches how the sides are built

    [Header("Twins inside the cage")]
    [Tooltip("RobotJointDriver of each twin. The cage is centred between their J1 axes and faces the way " +
             "the first one faces; the first one is also the scale ruler.")]
    public RobotJointDriver[] twins;

    // Rough estimate (2026-11-17): twice the first guess of 4.41 m, which came from the
    // user frame's distance; a walk-around video suggests ~4-5 m from the robot to the
    // fence each way. Replace with measurements when available.
    [Header("Size (real-world metres)")]
    [Tooltip("Length of each side of the square cage.")]
    public float sideLength = 8.82f;

    [Tooltip("Fence height. 3.048 m = 10 ft.")]
    public float height = 3.048f;

    [Tooltip("Widest a single panel may be; each stretch of fence gets as many equal panels as it needs.")]
    public float maxPanelWidth = 1.5f;

    [Tooltip("Gap between the floor and the bottom of the mesh panels.")]
    public float panelBottomGap = 0.15f;

    [Tooltip("Width of the square posts and the top rail.")]
    public float postWidth = 0.08f;

    [Tooltip("Width of the black frame around each mesh panel.")]
    public float panelFrameWidth = 0.03f;

    [Tooltip("Size of one square of the wire mesh.")]
    public float meshCellSize = 0.05f;

    [Header("Door")]
    [Tooltip("Which side of the cage the door is on, relative to the robot (the first twin) at its start pose.")]
    public Side doorSide = Side.RobotLeft;
    public float doorWidth = 1.2f;
    [Tooltip("Door height. Anything at or above the fence height makes a full-height door, " +
             "stopping just under the top rail.")]
    public float doorHeight = 3.048f;
    [Tooltip("How far the door's centre is from the middle of its side, along the side.")]
    public float doorOffset = 0f;

    [Header("Floor tape")]
    public bool floorTape = true;
    public float tapeWidth = 0.075f;
    [Tooltip("How far inside the fence the tape runs.")]
    public float tapeInset = 0.3f;

    [Header("Scale")]
    [Tooltip("Real length of the lower arm, J2 axis to J3 axis (fitted to the controller's pose data).")]
    public float lowerArmLengthMetres = 1.075f;

    [Tooltip("Model units per real metre. Used when Measure Scale is off or the twins aren't set.")]
    public float unitsPerMetre = 1f;

    [Tooltip("Measure the scale from the first twin's J2-J3 pivot distance instead of using Units Per Metre.")]
    public bool measureScale = true;

    [Header("Look")]
    [Tooltip("Optional. URP Lit material for posts, rails, frames and tape (recoloured per part). " +
             "Empty = one is made at runtime.")]
    public Material frameMaterial;

    [Tooltip("Optional, but recommended for web builds: a URP Lit material with Alpha Clipping on and " +
             "Render Face = Both. Its texture is replaced by the generated wire grid.")]
    public Material meshMaterial;

    public Color postColor = new Color(1f, 0.78f, 0f);          // safety yellow
    public Color panelFrameColor = new Color(0.08f, 0.08f, 0.08f);
    public Color doorColor = new Color(1f, 0.4f, 0.05f);        // orange
    public Color meshColor = new Color(0.12f, 0.12f, 0.12f);

    private const string LitShader = "Universal Render Pipeline/Lit";

    // Materials for this build, one per color
    private Material postMat, panelFrameMat, doorMat, meshMat;

    void Start()
    {
        BuildCage();
    }

    private void BuildCage()
    {
        float scale = ModelUnitsPerMetre();
        CagePlacement(out Vector3 centre, out Quaternion facing);

        // Everything below is built in metres under one root, which is then placed
        // between the twins and scaled to the model's units
        var root = new GameObject("Cage").transform;
        root.SetParent(transform, false);
        root.SetPositionAndRotation(centre, facing);
        root.localScale = Vector3.one * scale / Mathf.Max(transform.lossyScale.x, 1e-6f);

        postMat = ColoredMaterial(postColor);
        panelFrameMat = ColoredMaterial(panelFrameColor);
        doorMat = ColoredMaterial(doorColor);
        meshMat = MeshMaterial();

        float half = sideLength / 2f;

        // Each side is built along local X at z = +half, then turned 90 degrees for the
        // next: front (+Z, the robot's forward), right, back, left
        for (int side = 0; side < 4; side++)
        {
            var sideRoot = new GameObject(((Side)side).ToString()).transform;
            sideRoot.SetParent(root, false);
            sideRoot.localRotation = Quaternion.Euler(0f, 90f * side, 0f);

            if (side == (int)doorSide)
            {
                // Fence up to the door, the door between two posts, then fence to the corner
                float limit = half - doorWidth / 2f - postWidth;
                float doorCentre = Mathf.Clamp(doorOffset, -limit, limit);
                float d0 = doorCentre - doorWidth / 2f, d1 = doorCentre + doorWidth / 2f;
                BuildFenceRun(sideRoot, -half, d0, half);
                AddPost(sideRoot, d0, half);
                AddDoor(sideRoot, d0, d1, half);
                BuildFenceRun(sideRoot, d1, half, half);
            }
            else
            {
                BuildFenceRun(sideRoot, -half, half, half);
            }

            // Top rail along the whole side
            AddBox(sideRoot, "Top rail", new Vector3(0f, height - postWidth / 2f, half),
                   new Vector3(sideLength, postWidth, postWidth), postMat);

            if (floorTape)
            {
                float tapeZ = half - tapeInset;
                AddBox(sideRoot, "Floor tape", new Vector3(0f, 0.001f, tapeZ),
                       new Vector3(2f * tapeZ + tapeWidth, 0.002f, tapeWidth), postMat);
            }
        }

        LogModelHeight(scale);
    }

    // Posts and framed mesh panels from x0 to x1 along a side at depth z. Puts a post at
    // x0 but not at x1: the next stretch (or the next side's corner) starts with one.
    private void BuildFenceRun(Transform side, float x0, float x1, float z)
    {
        if (x1 - x0 < postWidth * 2f) return;
        int panels = Mathf.Max(1, Mathf.CeilToInt((x1 - x0) / maxPanelWidth));
        float step = (x1 - x0) / panels;
        float bottom = panelBottomGap, top = height - postWidth; // panels stop under the top rail

        for (int k = 0; k < panels; k++)
        {
            float start = x0 + k * step;
            AddPost(side, start, z);
            AddFramedPanel(side, start + step / 2f, step - postWidth, bottom, top, z, panelFrameMat);
        }
    }

    // A closed door between posts at x0 and x1, with a mesh panel above it up to the top rail
    private void AddDoor(Transform side, float x0, float x1, float z)
    {
        float centre = (x0 + x1) / 2f, width = x1 - x0 - postWidth;
        const float floorGap = 0.03f;
        float doorTop = DoorTop();
        AddFramedPanel(side, centre, width, floorGap, doorTop, z, doorMat, 2f);

        // Handle near the far edge, on both faces
        AddBox(side, "Door handle", new Vector3(x1 - postWidth / 2f - 0.12f, 1.05f, z),
               new Vector3(0.03f, 0.15f, 0.08f), doorMat);

        float above = height - postWidth;
        if (above - doorTop > 0.1f)
        {
            AddFramedPanel(side, centre, width, doorTop + panelFrameWidth, above, z, panelFrameMat);
        }
    }

    // Top of the door: never into the top rail, which runs along the top of the fence
    private float DoorTop() => Mathf.Min(doorHeight, height - postWidth);

    private void AddPost(Transform side, float x, float z)
    {
        AddBox(side, "Post", new Vector3(x, height / 2f, z), new Vector3(postWidth, height, postWidth), postMat);
    }

    // A mesh panel with a frame bar on each edge. frameScale thickens the frame (doors).
    private void AddFramedPanel(Transform side, float centreX, float width, float bottom, float top, float z,
                                Material frame, float frameScale = 1f)
    {
        float f = panelFrameWidth * frameScale, h = top - bottom;
        if (width <= 2f * f || h <= 2f * f) return;

        AddMeshPanel(side, new Vector3(centreX, bottom + f, z), width - 2f * f, h - 2f * f);

        var bar = new Vector3(width, f, f);
        AddBox(side, "Frame", new Vector3(centreX, bottom + f / 2f, z), bar, frame);
        AddBox(side, "Frame", new Vector3(centreX, top - f / 2f, z), bar, frame);
        var upright = new Vector3(f, h, f);
        AddBox(side, "Frame", new Vector3(centreX - width / 2f + f / 2f, bottom + h / 2f, z), upright, frame);
        AddBox(side, "Frame", new Vector3(centreX + width / 2f - f / 2f, bottom + h / 2f, z), upright, frame);
    }

    // Where the cage goes: centred between the twins' J1 axes (at this object's height),
    // turned so its "front" is the first twin's forward direction at its start pose.
    // Falls back to this object's own position and rotation.
    private void CagePlacement(out Vector3 centre, out Quaternion facing)
    {
        centre = transform.position;
        facing = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);

        Vector3 sum = Vector3.zero;
        int count = 0;
        foreach (var twin in twins ?? new RobotJointDriver[0])
        {
            Transform j1 = Joint(twin, 0);
            if (j1 == null) continue;
            sum += j1.position;
            count++;
        }
        if (count > 0)
        {
            centre = new Vector3(sum.x / count, transform.position.y, sum.z / count);
        }

        // Forward = horizontal direction from the base axis to the wrist (J5)
        Transform baseAxis = Joint(FirstTwin(), 0), wrist = Joint(FirstTwin(), 4);
        if (baseAxis != null && wrist != null)
        {
            Vector3 forward = wrist.position - baseAxis.position;
            forward.y = 0f;
            if (forward.sqrMagnitude > 1e-8f) facing = Quaternion.LookRotation(forward, Vector3.up);
        }
    }

    // The robot model's units per real metre, from its lower arm length if possible
    private float ModelUnitsPerMetre()
    {
        if (!measureScale) return unitsPerMetre;
        Transform j2 = Joint(FirstTwin(), 1), j3 = Joint(FirstTwin(), 2);
        if (j2 == null || j3 == null) return unitsPerMetre;

        float modelLength = Vector3.Distance(j2.position, j3.position);
        if (modelLength < 1e-6f || lowerArmLengthMetres <= 0f) return unitsPerMetre;
        return modelLength / lowerArmLengthMetres;
    }

    // Logs the scale and the model robot's height at that scale, to check the scale by eye
    private void LogModelHeight(float scale)
    {
        string robotHeight = "";
        var twin = FirstTwin();
        if (twin != null)
        {
            var renderers = twin.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                robotHeight = $"; the model robot stands {b.size.y / scale:0.00} m tall at this scale";
            }
        }
        Debug.Log($"[SafetyCage] 1 m = {scale:0.###} model units{robotHeight}. " +
                  $"Cage {sideLength:0.##} x {sideLength:0.##} x {height:0.##} m, door on the {doorSide}.");
    }

    private RobotJointDriver FirstTwin() => twins != null && twins.Length > 0 ? twins[0] : null;

    private static Transform Joint(RobotJointDriver twin, int index)
    {
        if (twin == null || twin.joints == null || twin.joints.Length <= index || twin.joints[index] == null) return null;
        return twin.joints[index].jointTransform;
    }

    private static void AddBox(Transform parent, string name, Vector3 centre, Vector3 size, Material material)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        Destroy(box.GetComponent<Collider>()); // nothing needs to collide with the cage
        box.transform.SetParent(parent, false);
        box.transform.localPosition = centre;
        box.transform.localScale = size;
        box.GetComponent<MeshRenderer>().sharedMaterial = material;
    }

    // One quad, bottom edge centred on `bottomCentre`, facing along Z. Its UVs count mesh
    // cells rather than 0-1, so the shared grid texture repeats at the same real size on
    // every panel whatever its width.
    private void AddMeshPanel(Transform parent, Vector3 bottomCentre, float width, float panelHeight)
    {
        float w = width / 2f, u = width / meshCellSize, v = panelHeight / meshCellSize;
        var quad = new Mesh
        {
            name = "Mesh panel",
            vertices = new[]
            {
                new Vector3(-w, 0f, 0f), new Vector3(w, 0f, 0f),
                new Vector3(-w, panelHeight, 0f), new Vector3(w, panelHeight, 0f),
            },
            uv = new[] { new Vector2(0f, 0f), new Vector2(u, 0f), new Vector2(0f, v), new Vector2(u, v) },
            triangles = new[] { 0, 2, 1, 2, 3, 1 },
        };
        quad.RecalculateNormals();
        quad.RecalculateBounds();

        var panel = new GameObject("Mesh panel");
        panel.transform.SetParent(parent, false);
        panel.transform.localPosition = bottomCentre;
        panel.AddComponent<MeshFilter>().sharedMesh = quad;
        var renderer = panel.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = meshMat;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // wire shadows aren't worth it
    }

    private Material ColoredMaterial(Color color)
    {
        var material = frameMaterial != null ? new Material(frameMaterial) : new Material(Shader.Find(LitShader));
        material.SetColor("_BaseColor", color);
        return material;
    }

    private Material MeshMaterial()
    {
        Material material;
        if (meshMaterial != null)
        {
            material = new Material(meshMaterial);
        }
        else
        {
            // Works in the editor; a web build may strip the alpha-clip shader variant
            // unless some material in the build uses it, hence the Mesh Material field
            Debug.LogWarning("[SafetyCage] No Mesh Material set; making one. Assign one for web builds.");
            material = new Material(Shader.Find(LitShader));
            material.SetFloat("_AlphaClip", 1f);
            material.EnableKeyword("_ALPHATEST_ON");
            material.SetFloat("_Cull", 0f); // both faces
        }
        material.SetTexture("_BaseMap", WireGridTexture());
        material.SetColor("_BaseColor", meshColor);
        material.SetFloat("_Cutoff", 0.5f);
        return material;
    }

    // One cell of wire mesh: opaque wires along two edges, transparent inside.
    // Repeated across each panel by its UVs.
    private static Texture2D WireGridTexture()
    {
        const int size = 32, wire = 3;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            name = "Wire grid",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
        };
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool isWire = x < wire || y < wire;
                pixels[y * size + x] = isWire ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(true);
        return texture;
    }

    // Outline of the cage, and a mark on the door side, in the Scene view while selected
    void OnDrawGizmosSelected()
    {
        float scale = ModelUnitsPerMetre();
        CagePlacement(out Vector3 centre, out Quaternion facing);
        Gizmos.matrix = Matrix4x4.TRS(centre, facing, Vector3.one * scale);
        Gizmos.color = postColor;
        Gizmos.DrawWireCube(new Vector3(0f, height / 2f, 0f), new Vector3(sideLength, height, sideLength));

        // The door: its side's local +Z, turned like the side itself
        Quaternion sideTurn = Quaternion.Euler(0f, 90f * (int)doorSide, 0f);
        Gizmos.color = doorColor;
        Vector3 size = sideTurn * new Vector3(doorWidth, DoorTop(), 0.05f);
        Gizmos.DrawWireCube(sideTurn * new Vector3(doorOffset, DoorTop() / 2f, sideLength / 2f),
                            new Vector3(Mathf.Abs(size.x), size.y, Mathf.Abs(size.z))); // turning can flip signs
    }
}
