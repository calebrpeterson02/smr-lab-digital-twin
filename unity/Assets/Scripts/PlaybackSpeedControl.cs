using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A UI slider that sets the replay speed of every twin (TwinClock.playbackSpeed),
/// taking effect immediately - no pause or reload needed. Replay only: live data
/// has to keep pace with the real robot, so the speed doesn't apply there.
///
/// Put it on the Slider object (or point Slider at one). The slider is wired up in
/// code, so don't also hook its On Value Changed in the Inspector.
/// </summary>
public class PlaybackSpeedControl : MonoBehaviour
{
    [Tooltip("The UI Slider. Leave empty to use one on this object. Its min/max are set by this script.")]
    public Slider slider;

    [Tooltip("Shows the current speed, e.g. \"0.50x\". Optional.")]
    public TMP_Text label;

    [Tooltip("Slowest replay speed (fraction of real time). With the logarithmic scale, 1x sits in the middle " +
             "when min x max = 1 (e.g. 0.25 and 4).")]
    public float minSpeed = 0.25f;

    [Tooltip("Fastest replay speed (multiple of real time).")]
    public float maxSpeed = 4f;

    [Tooltip("On: equal slider distances are equal ratios, so 0.5x and 2x sit the same distance either side " +
             "of 1x, and slow speeds get as much of the slider as fast ones. Off: plain linear scale.")]
    public bool logarithmic = true;

    [Tooltip("Speeds within this fraction of 1x snap to exactly 1x, so normal speed is easy to hit.")]
    public float snapToNormal = 0.05f;

    void Start()
    {
        if (slider == null) slider = GetComponent<Slider>();
        if (slider == null)
        {
            Debug.LogWarning("PlaybackSpeedControl: no Slider assigned or on this object.");
            enabled = false;
            return;
        }

        // The slider runs 0..1 and this script maps it to a speed, so the scale can be logarithmic
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.wholeNumbers = false;
        slider.SetValueWithoutNotify(SpeedToSlider(CurrentSpeed()));
        slider.onValueChanged.AddListener(OnSliderChanged);
        UpdateLabel(CurrentSpeed());
    }

    void OnDestroy()
    {
        if (slider != null) slider.onValueChanged.RemoveListener(OnSliderChanged);
    }

    /// <summary>Hook a "1x" button's OnClick() here to return to normal speed.</summary>
    public void ResetToNormal()
    {
        SetSpeed(1f);
        if (slider != null) slider.SetValueWithoutNotify(SpeedToSlider(1f));
    }

    private void OnSliderChanged(float value)
    {
        float speed = SliderToSpeed(value);
        if (Mathf.Abs(speed - 1f) <= snapToNormal)
        {
            speed = 1f;
            slider.SetValueWithoutNotify(SpeedToSlider(1f)); // move the handle onto the snap point
        }
        SetSpeed(speed);
    }

    private void SetSpeed(float speed)
    {
        var clock = TwinClock.Active;
        if (clock == null)
        {
            Debug.LogWarning("PlaybackSpeedControl: no TwinClock in the scene yet, so the speed wasn't changed.");
            return;
        }
        clock.playbackSpeed = speed;
        UpdateLabel(speed);
    }

    private float CurrentSpeed() => TwinClock.Active != null ? TwinClock.Active.playbackSpeed : 1f;

    private float SliderToSpeed(float t)
    {
        t = Mathf.Clamp01(t);
        return logarithmic
            ? minSpeed * Mathf.Pow(maxSpeed / minSpeed, t)   // 0.25x .. 4x by default, 1x in the middle
            : Mathf.Lerp(minSpeed, maxSpeed, t);
    }

    private float SpeedToSlider(float speed)
    {
        speed = Mathf.Clamp(speed, minSpeed, maxSpeed);
        return logarithmic
            ? Mathf.Log(speed / minSpeed) / Mathf.Log(maxSpeed / minSpeed)
            : Mathf.InverseLerp(minSpeed, maxSpeed, speed);
    }

    private void UpdateLabel(float speed)
    {
        if (label != null) label.text = $"{speed:0.00}x";
    }
}
