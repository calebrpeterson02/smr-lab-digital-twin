using UnityEngine;

/// <summary>
/// Tints every part of a twin one color, so the twins are easy to tell apart.
/// Attach to a rig's root (e.g. the robot twin's Base) and pick a color.
///
/// The material assets aren't changed: the color is overridden per renderer
/// with a MaterialPropertyBlock, so both rigs can keep sharing the same
/// materials. Removing or disabling this component restores the original look.
/// Works in edit mode too, so the result shows in the Scene view.
/// </summary>
[ExecuteAlways]
public class TwinTint : MonoBehaviour
{
    [Tooltip("Color for this twin.")]
    public Color tint = new Color(0.3f, 0.6f, 1f);

    [Tooltip("0 = each part's own material color, 1 = entirely the tint color. In between keeps " +
             "some of the original colors visible (useful once the model has real materials).")]
    [Range(0f, 1f)] public float strength = 0.7f;

    // Base color property: "_BaseColor" in URP's Lit/Unlit shaders, "_Color" in the built-in ones
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int LegacyColorId = Shader.PropertyToID("_Color");

    private MaterialPropertyBlock block;
    private bool dirty;

    void OnEnable() => Apply();
    void OnDisable() => Clear();

    // Changing values in the Inspector: re-apply on the next Update rather than
    // inside OnValidate, where Unity restricts what may be called
    void OnValidate() => dirty = true;

    void Update()
    {
        if (!dirty) return;
        dirty = false;
        Apply();
    }

    /// <summary>Re-applies the tint, e.g. after parts are added to the rig at runtime.</summary>
    public void Apply()
    {
        if (block == null) block = new MaterialPropertyBlock();

        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            var materials = r.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null) continue;

                int colorId = material.HasProperty(BaseColorId) ? BaseColorId
                            : material.HasProperty(LegacyColorId) ? LegacyColorId
                            : -1;
                if (colorId < 0) continue; // shader without a base color

                Color original = material.GetColor(colorId);
                Color tinted = Color.Lerp(original, tint, strength);
                tinted.a = original.a; // keep transparency as it was

                // Per material slot, so parts with several materials are all tinted
                block.Clear();
                r.GetPropertyBlock(block, i);
                block.SetColor(colorId, tinted);
                r.SetPropertyBlock(block, i);
            }
        }
    }

    private void Clear()
    {
        if (block == null) block = new MaterialPropertyBlock();
        block.Clear();
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            for (int i = 0; i < r.sharedMaterials.Length; i++)
            {
                r.SetPropertyBlock(block, i); // an empty block removes the override
            }
        }
    }
}
