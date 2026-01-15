using TMPro;
using UnityEngine;

public class FPSCounterTMP : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text fpsText;

    [Header("Update")]
    [Tooltip("How often to update the displayed FPS (seconds).")]
    public float updateInterval = 0.1f;

    private float _timer;
    private int _frames;
    private float _accumulatedTime;

    private void Awake()
    {
        // Uncapped framerate (as much as possible)
        Application.targetFrameRate = -1;

        // Optional: disable v-sync so targetFrameRate actually matters.
        // If v-sync is enabled in Quality settings, it will still cap.
        QualitySettings.vSyncCount = 0;
    }

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        _frames++;
        _accumulatedTime += dt;
        _timer += dt;

        if (_timer >= updateInterval)
        {
            float fps = (_accumulatedTime > 0f) ? (_frames / _accumulatedTime) : 0f;

            if (fpsText)
                fpsText.text = $"{fps:0} FPS";

            _timer = 0f;
            _frames = 0;
            _accumulatedTime = 0f;
        }
    }
}
