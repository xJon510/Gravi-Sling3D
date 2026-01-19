using UnityEngine;

/// <summary>
/// Central boost/energy manager (singleton).
/// - Drains energy while in FreeFlight AND boost is held AND player has movement input.
/// - Does NOT drain or regen while OrbitIdle.
/// - Regens ONLY while OrbitCharging (holding charge in SlingshotPlanet3D).
/// - Exposes Boost01 (0..1) for SimpleMove's additive boost logic.
/// - Drives a UI RectTransform bar by changing its width (sizeDelta.x) 0..maxWidth.
/// </summary>
public class BoostManager : MonoBehaviour
{
    public static BoostManager Instance { get; private set; }

    public enum Mode
    {
        FreeFlight,
        OrbitIdle,
        OrbitCharging
    }

    [Header("Mode (Read Only)")]
    [SerializeField] private Mode _mode = Mode.FreeFlight;

    [Header("Energy")]
    public float capacity = 100f;
    public bool startFull = true;
    public float startEnergy = 100f;

    [Tooltip("Energy drained per second at Boost01 = 1 (FreeFlight).")]
    public float drainPerSecond = 18f;

    [Tooltip("Energy regenerated per second (ONLY during OrbitCharging).")]
    public float regenPerSecond = 35f;

    [Header("Boost Smoothing (matches old boostCharge feel)")]
    [Tooltip("How fast Boost01 rises when boosting (per second).")]
    public float boostRampUp = 1.2f;

    [Tooltip("How fast Boost01 falls when not boosting (per second).")]
    public float boostRampDown = 2.0f;

    [Header("UI Fill Bar (Width-Based)")]
    [Tooltip("Assign the RectTransform of the bar that should change width.")]
    public RectTransform fillBarRect;

    [Tooltip("If true, force pivot.x = 0 so width grows to the right.")]
    public bool forceLeftPivot = true;

    private float _energy;
    private float _boost01;            // smoothed boost intensity 0..1
    private float _maxBarWidth;        // captured from fillBarRect on Awake

    // Inputs from SimpleMove each FixedUpdate
    private bool _boostHeld;
    private bool _hasMoveInput;

    public float Energy => _energy;
    public float Energy01 => (capacity <= 0.0001f) ? 0f : Mathf.Clamp01(_energy / capacity);

    /// <summary>Use this in SimpleMove instead of its private boostCharge.</summary>
    public float Boost01 => _boost01;

    private void Awake()
    {
        // Singleton
        if (Instance && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        _energy = startFull ? capacity : Mathf.Clamp(startEnergy, 0f, capacity);

        if (fillBarRect != null)
        {
            if (forceLeftPivot)
            {
                var p = fillBarRect.pivot;
                fillBarRect.pivot = new Vector2(0f, p.y);
            }

            // Cache "full" width at start
            _maxBarWidth = fillBarRect.sizeDelta.x;
        }

        ApplyBarWidth(); // set initial UI
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        // Decide if we *want* boosting this tick (only in free flight, with input)
        bool canBoostState = (_mode == Mode.FreeFlight);
        bool hasEnergy = _energy > 0.0001f;
        bool boostingWanted = canBoostState && _boostHeld && _hasMoveInput && hasEnergy;

        // Smooth Boost01 up/down (so SimpleMove keeps same feel)
        float delta = boostingWanted ? boostRampUp : -boostRampDown;
        _boost01 = Mathf.Clamp01(_boost01 + delta * dt);

        // Drain energy ONLY while boostingWanted (optionally scale by boost01)
        if (boostingWanted && drainPerSecond > 0f)
        {
            _energy -= drainPerSecond * _boost01 * dt;
            if (_energy <= 0f)
            {
                _energy = 0f;
                _boost01 = 0f; // hard drop if empty
            }
        }

        // Regen ONLY during orbit charging
        if (_mode == Mode.OrbitCharging && regenPerSecond > 0f)
        {
            _energy += regenPerSecond * dt;
            if (_energy > capacity) _energy = capacity;
        }

        ApplyBarWidth();
    }

    private void ApplyBarWidth()
    {
        if (fillBarRect == null) return;

        // If bar assigned late, capture max width once
        if (_maxBarWidth <= 0.0001f)
            _maxBarWidth = fillBarRect.sizeDelta.x;

        float w = _maxBarWidth * Energy01;

        var sz = fillBarRect.sizeDelta;
        sz.x = w;
        fillBarRect.sizeDelta = sz;
    }

    /// <summary>
    /// Called by SimpleMove each FixedUpdate (or Update) to tell BoostManager current input state.
    /// </summary>
    public void SetBoostInput(bool boostHeld, bool hasMoveInput)
    {
        _boostHeld = boostHeld;
        _hasMoveInput = hasMoveInput;
    }

    /// <summary>
    /// Called by SlingshotPlanet3D to tell BoostManager the orbit/charging state.
    /// </summary>
    public void SetMode(Mode m)
    {
        _mode = m;

        // If we left FreeFlight, drop boost intensity so we never "carry" boost into orbit
        if (_mode != Mode.FreeFlight)
            _boost01 = 0f;
    }

    // Optional helper if you want manual refills later
    public void AddEnergy(float amount)
    {
        _energy = Mathf.Clamp(_energy + amount, 0f, capacity);
        ApplyBarWidth();
    }
}
