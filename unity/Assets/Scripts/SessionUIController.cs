using TMPro;
using UnityEngine;
using StatusLevel = BridgeConnection.StatusLevel;

/// <summary>
/// Connects the session-selection UI (an input field + Load / Go Live / Pause
/// buttons) to every bridge's SelectSession/UseLatest commands and the shared
/// TwinClock, so all twins show the same session and pause together, and shows
/// the most recent status/error message from any bridge in a text box.
/// </summary>
public class SessionUIController : MonoBehaviour
{
    [Tooltip("Every bridge client the buttons control, e.g. the robot BridgeClient and the ImuBridgeClient.")]
    public BridgeConnection[] bridges;
    public TMP_InputField sessionIdInput;

    [Header("Status display")]
    [Tooltip("Shows only the most recent message. The Console keeps the full history.")]
    public TMP_Text statusText;
    public Color infoColor = Color.white;
    public Color warningColor = new Color(1f, 0.85f, 0.3f);
    public Color errorColor = new Color(1f, 0.4f, 0.4f);

    [Tooltip("A warning/error stays on screen at least this long before a routine (info) message " +
             "can replace it. Without this, a warning can flash by unread - e.g. the bridge reports an " +
             "empty session and immediately switches back to live. Set to 0 for strict 'latest message wins'.")]
    public float warningHoldSeconds = 3f;

    [Header("Pause button (replay only)")]
    [Tooltip("The pause button's text, switched between the two labels below. Optional.")]
    public TMP_Text pauseButtonLabel;
    public string pauseLabel = "Pause";
    public string resumeLabel = "Resume";

    private float holdUntil;
    private bool shownPaused; // what the pause button label currently says
    private bool labelSet;    // label written at least once (replaces the editor's placeholder text)
    private string pendingInfo; // newest info message that arrived during a hold

    void OnEnable()
    {
        pendingInfo = null;
        if (bridges == null) return;

        foreach (var bridge in bridges)
        {
            if (bridge == null) continue;
            bridge.StatusChanged += HandleStatus;

            // Catch up on whatever happened before this subscribed (or while it was disabled)
            if (bridge.LastStatusMessage != null)
            {
                HandleStatus(bridge.LastStatusMessage, bridge.LastStatusLevel);
            }
        }
    }

    void OnDisable()
    {
        if (bridges == null) return;
        foreach (var bridge in bridges)
        {
            if (bridge != null) bridge.StatusChanged -= HandleStatus;
        }
    }

    void Update()
    {
        // An info message arrived while a warning/error was being held; show it once the hold ends
        if (pendingInfo != null && Time.unscaledTime >= holdUntil)
        {
            Show(pendingInfo, StatusLevel.Info);
            pendingInfo = null;
        }

        // Keep the label in step with the clock - a pause also ends when a new
        // session loads or the twins go live, not only when the button is pressed
        var clock = TwinClock.Active;
        bool paused = clock != null && clock.Paused;
        if (pauseButtonLabel != null && (paused != shownPaused || !labelSet))
        {
            pauseButtonLabel.text = paused ? resumeLabel : pauseLabel;
            shownPaused = paused;
            labelSet = true;
        }
    }

    private void HandleStatus(string message, StatusLevel level)
    {
        if (level == StatusLevel.Info && Time.unscaledTime < holdUntil)
        {
            pendingInfo = message; // only the newest is kept
            return;
        }

        // Anything shown now is newer than whatever was pending, so the pending one is stale
        pendingInfo = null;
        Show(message, level);

        if (level != StatusLevel.Info)
        {
            holdUntil = Time.unscaledTime + warningHoldSeconds;
        }
    }

    private void Show(string message, StatusLevel level)
    {
        if (statusText == null) return;

        statusText.text = message;
        statusText.color = level switch
        {
            StatusLevel.Warning => warningColor,
            StatusLevel.Error => errorColor,
            _ => infoColor,
        };
    }

    // Messages originating in this script: logged to the Console like everything
    // else (the UI only keeps the latest) and shown through the same path as the
    // ones coming from the bridges.
    private void Report(string message, StatusLevel level = StatusLevel.Info)
    {
        string logged = $"SessionUIController: {message}";
        switch (level)
        {
            case StatusLevel.Warning: Debug.LogWarning(logged); break;
            case StatusLevel.Error: Debug.LogError(logged); break;
            default: Debug.Log(logged); break;
        }
        HandleStatus(message, level);
    }

    // Without this, a button with an empty Bridges list would do nothing and say nothing
    private bool HasBridges()
    {
        if (bridges != null)
        {
            foreach (var bridge in bridges)
            {
                if (bridge != null) return true;
            }
        }
        Report("No bridges assigned - add the BridgeClient and ImuBridgeClient to " +
               "SessionUIController's Bridges list in the Inspector.", StatusLevel.Error);
        return false;
    }

    // Hook these up to the buttons' OnClick() in the Inspector. If you ever
    // switch to AddListener in code instead, remove the Inspector hookups so
    // each click doesn't fire twice.
    public void OnLoadSessionClicked()
    {
        if (!HasBridges()) return;

        if (int.TryParse(sessionIdInput.text, out int sessionId))
        {
            Report($"Requesting session {sessionId}...");
            foreach (var bridge in bridges)
            {
                if (bridge != null) _ = bridge.SelectSession(sessionId);
            }
        }
        else
        {
            Report($"'{sessionIdInput.text}' isn't a valid session id.", StatusLevel.Warning);
        }
    }

    /// <summary>Hook up to the pause button's OnClick(). Pauses/resumes every twin's replay together.</summary>
    public void OnPauseClicked()
    {
        var clock = TwinClock.Active;
        if (clock == null)
        {
            Report("No TwinClock in the scene to pause.", StatusLevel.Warning);
            return;
        }
        if (!clock.TogglePause())
        {
            Report("Pause only works while a session is replaying.", StatusLevel.Warning);
            return;
        }
        Report(clock.Paused ? "Replay paused" : "Replay resumed");
    }

    public void OnGoLiveClicked()
    {
        if (!HasBridges()) return;

        Report("Requesting live mode...");
        foreach (var bridge in bridges)
        {
            if (bridge != null) _ = bridge.UseLatest();
        }
    }
}
