using TMPro;
using UnityEngine;

public class SpeedHUD : MonoBehaviour
{
    public static SpeedHUD Instance { get; private set; }

    [Header("Wiring")]
    [SerializeField] private TMP_Text speedText;

    [Header("Format")]
    [SerializeField] private string label = "Speed";
    [SerializeField] private float multiplier = 1f;     // set 0.5f if you want “km/s vibes”
    [SerializeField] private int decimals = 1;

    private void Awake()
    {
        Instance = this;
    }

    public void SetSpeed(float speedUnitsPerSec)
    {
        if (!speedText) return;
        float v = speedUnitsPerSec * multiplier;
        speedText.text = $"{label}: {v.ToString($"F{decimals}")}";
    }

    public void Clear()
    {
        if (!speedText) return;
        speedText.text = $"{label}: --";
    }
}
