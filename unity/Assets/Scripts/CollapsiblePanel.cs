using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Adds a Hide / Show button to a UI panel. Collapsing hides everything in the
/// panel except the button and shrinks the panel down to just the button, so
/// it stops covering the 3D view.
///
/// Put it on the panel itself (the object with the background Image). The button
/// is created at runtime in the panel's top-right corner, unless you assign your
/// own in Toggle Button. While open it's a small [-]; collapsed, it grows to show
/// the panel's name.
///
/// Sizes are in canvas units, so the button looks the same on every panel even
/// if a panel is scaled (the controls Panel is at 0.41 scale).
///
/// The panel shrinks towards its pivot, so the corner the panel is anchored to
/// stays put: a top-right panel (pivot 1,1) folds up into its top-right corner,
/// a bottom-right panel (pivot 1,0) folds down into its bottom-right corner.
///
/// Button Placement "Above Panel" puts the button just above the panel's top-right
/// corner instead, so it never covers the panel's contents.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class CollapsiblePanel : MonoBehaviour
{
    public enum ButtonPlacement
    {
        InsideTopRight, // in the panel's top-right corner
        AbovePanel,     // just above the panel, lined up with its right edge
    }

    [Tooltip("Optional. Leave empty and a button is created in the panel's top-right corner.")]
    public Button toggleButton;

    [Tooltip("Button text while the panel is open.")]
    public string expandedLabel = "[-]";
    [Tooltip("Button text while the panel is collapsed, e.g. 'Controls' or 'Joint table', " +
             "so you can tell the collapsed panels apart.")]
    public string collapsedLabel = "Show";

    public bool startCollapsed = false;

    [Header("Created button (only used when Toggle Button is empty)")]
    [Tooltip("Button size while the panel is open (the [-] button).")]
    public Vector2 expandedButtonSize = new Vector2(40f, 26f);
    [Tooltip("Button size while the panel is collapsed (shows the panel's name).")]
    public Vector2 collapsedButtonSize = new Vector2(140f, 30f);
    public ButtonPlacement buttonPlacement = ButtonPlacement.InsideTopRight;
    [Tooltip("Gap between the button and the panel's edges.")]
    public float buttonMargin = 8f;
    [Tooltip("Colors while the panel is open (the [-] button): #878787 grey with white text.")]
    public Color expandedButtonColor = new Color32(0x87, 0x87, 0x87, 0xFF);
    public Color expandedTextColor = Color.white;
    [Tooltip("Colors while the panel is collapsed (the button with the panel's name).")]
    public Color collapsedButtonColor = Color.white;
    public Color collapsedTextColor = new Color(0.2f, 0.2f, 0.2f);
    [Tooltip("Largest font size. The text shrinks below this if it doesn't fit the button.")]
    public float fontSize = 18f;

    public bool Collapsed { get; private set; }

    private RectTransform rect;
    private Vector2 expandedSize;
    private TMP_Text buttonLabel;
    private RectTransform createdButton; // null when the button was assigned in the Inspector
    private Image createdButtonImage;
    private float panelScale = 1f;       // the panel's scale relative to the canvas
    // Children that were active when the panel collapsed, so expanding brings back
    // exactly those (and not something that was meant to stay hidden)
    private readonly List<GameObject> hiddenChildren = new List<GameObject>();

    void Awake()
    {
        rect = (RectTransform)transform;
        panelScale = ScaleRelativeToCanvas();
        if (toggleButton == null) toggleButton = CreateButton();
        buttonLabel = toggleButton.GetComponentInChildren<TMP_Text>();
        toggleButton.onClick.AddListener(Toggle);
        RefreshButton();
    }

    void Start()
    {
        // Start, not Awake, so the other scripts in the panel get their Awake first
        if (startCollapsed) SetCollapsed(true);
    }

    void OnDestroy()
    {
        if (toggleButton != null) toggleButton.onClick.RemoveListener(Toggle);
    }

    public void Toggle() => SetCollapsed(!Collapsed);

    public void SetCollapsed(bool collapse)
    {
        if (collapse == Collapsed) return;
        Collapsed = collapse;

        if (collapse)
        {
            expandedSize = rect.sizeDelta;
            hiddenChildren.Clear();
            foreach (Transform child in transform)
            {
                // Keep the button visible (it may be a direct child or nested inside one)
                if (toggleButton.transform.IsChildOf(child)) continue;
                if (!child.gameObject.activeSelf) continue;
                hiddenChildren.Add(child.gameObject);
                child.gameObject.SetActive(false);
            }

            // Shrink to just the button. sizeDelta is the real size here because the
            // panels are anchored to a single point (anchor min = max), not stretched.
            RefreshButton(); // collapsed size first, so the panel shrinks around it
            Vector2 button = ((RectTransform)toggleButton.transform).rect.size;
            if (buttonPlacement == ButtonPlacement.AbovePanel)
            {
                // Button sits outside: shrink the panel to nothing under it
                rect.sizeDelta = new Vector2(button.x, 0f);
            }
            else
            {
                rect.sizeDelta = button + 2f * (buttonMargin / panelScale) * Vector2.one;
            }
        }
        else
        {
            rect.sizeDelta = expandedSize;
            RefreshButton();
            foreach (var child in hiddenChildren)
            {
                if (child != null) child.SetActive(true);
            }
            hiddenChildren.Clear();
        }
    }

    // Label for the current state, plus the size if this script created the button
    private void RefreshButton()
    {
        if (buttonLabel != null) buttonLabel.text = Collapsed ? collapsedLabel : expandedLabel;
        if (createdButton != null)
        {
            // Divide by the panel's scale so a scaled-down panel still gets a full-size button
            createdButton.sizeDelta = (Collapsed ? collapsedButtonSize : expandedButtonSize) / panelScale;
            createdButtonImage.color = Collapsed ? collapsedButtonColor : expandedButtonColor;
            if (buttonLabel != null) buttonLabel.color = Collapsed ? collapsedTextColor : expandedTextColor;
        }
    }

    // 1 for a panel at normal size, 0.41 for one scaled to 41%, etc. Relative to
    // the canvas because the canvas itself is scaled to fit the screen. Multiplies the
    // local scales up to the canvas rather than comparing lossyScale: the canvas's own
    // scale is set by the CanvasScaler and can still be 0 when Awake runs.
    private float ScaleRelativeToCanvas()
    {
        float scale = 1f;
        for (Transform t = transform; t != null && t.GetComponent<Canvas>() == null; t = t.parent)
        {
            scale *= t.localScale.x;
        }
        return scale > 1e-6f ? scale : 1f; // guard against a zero scale
    }

    // A standard-looking UI button, anchored to the panel's top-right corner
    // (inside it, or sitting on top of it for AbovePanel)
    private Button CreateButton()
    {
        var go = new GameObject("CollapseButton", typeof(RectTransform), typeof(Image), typeof(Button));
        var buttonRect = (RectTransform)go.transform;
        buttonRect.SetParent(transform, false);
        buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(1f, 1f);
        if (buttonPlacement == ButtonPlacement.AbovePanel)
        {
            // Bottom-right of the button on the panel's top-right corner, a margin's gap above it
            buttonRect.pivot = new Vector2(1f, 0f);
            buttonRect.anchoredPosition = new Vector2(0f, buttonMargin) / panelScale;
        }
        else
        {
            buttonRect.pivot = new Vector2(1f, 1f);
            buttonRect.anchoredPosition = new Vector2(-buttonMargin, -buttonMargin) / panelScale;
        }
        buttonRect.SetAsLastSibling(); // drawn on top of the rest of the panel

        createdButtonImage = go.GetComponent<Image>(); // colors set in RefreshButton

        var labelGo = new GameObject("Text (TMP)", typeof(RectTransform), typeof(TextMeshProUGUI));
        var labelRect = (RectTransform)labelGo.transform;
        labelRect.SetParent(buttonRect, false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = labelRect.offsetMax = Vector2.zero; // fill the button

        var label = labelGo.GetComponent<TextMeshProUGUI>();
        label.alignment = TextAlignmentOptions.Center;
        // Auto size: fontSize is the cap, smaller buttons get smaller text that still fits
        label.enableAutoSizing = true;
        label.fontSizeMax = fontSize / panelScale;
        label.fontSizeMin = 6f / panelScale;
        label.margin = new Vector4(2f, 1f, 2f, 1f) / panelScale; // keep text off the edges

        createdButton = buttonRect; // RefreshButton (called from Awake) sets its size and colors
        return go.GetComponent<Button>();
    }
}
