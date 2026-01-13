using UnityEngine;
using System.Collections;

/// <summary>
/// Central place to drive voxel thruster particle tuning based on movement state.
/// Keeps SimpleMove / SlingshotPlanet3D clean: they just call SetFreeFlight / SetOrbiting / SetCharging / OnLaunch.
/// </summary>
public class PlayerThrustManager : MonoBehaviour
{
    public static PlayerThrustManager Instance { get; private set; }

    public enum ThrustState
    {
        Idle,
        FreeFlight,
        Orbiting,
        Charging,
        LaunchCut
    }

    [Header("Thruster References")]
    [SerializeField] private ParticleSystem leftThruster;
    [SerializeField] private ParticleSystem rightThruster;
    [SerializeField] private ParticleSystem centerThruster;
    [SerializeField] private ParticleSystem MachRing;

    [Header("Global Multipliers")]
    [Tooltip("Master scale for all thruster size (keeps tuning easy).")]
    [SerializeField] private float globalSize = 1f;

    [Tooltip("Master multiplier for all thruster emission rate.")]
    [SerializeField] private float globalEmission = 1f;

    [Header("Free Flight (Input) - L/R")]
    [SerializeField] private float free_lr_minRate = 0f;
    [SerializeField] private float free_lr_maxRate = 45f;
    [SerializeField] private float free_lr_minLifetime = 0.06f;
    [SerializeField] private float free_lr_maxLifetime = 0.18f;
    [SerializeField] private float free_lr_minSpeed = 0.0f;
    [SerializeField] private float free_lr_maxSpeed = 1.6f;
    [SerializeField] private float free_lr_minSize = 0.10f;
    [SerializeField] private float free_lr_maxSize = 0.22f;

    [Tooltip("If true, L/R thrusters respond more to strafing input than forward input.")]
    [SerializeField] private bool free_lr_strafeEmphasis = true;

    [Header("Free Flight - Center (idle/boost)")]
    [SerializeField] private float free_center_idleRate = 0f;
    [SerializeField] private float free_center_idleLifetime = 0.08f;
    [SerializeField] private float free_center_idleSize = 0.12f;

    // NEW: center thruster rate while boosting (Shift)
    [SerializeField] private float free_center_boostRate = 55f;

    // Optional: give it a little push so it reads as “main engine”
    [SerializeField] private float free_center_boostSpeed = 1.6f;

    [Header("Orbiting - Center On")]
    [SerializeField] private float orbit_center_idleRate = 0f; // center thruster when just orbiting (not charging)
    [SerializeField] private float orbit_center_rate = 25f;
    [SerializeField] private float orbit_center_lifetime = 0.16f;
    [SerializeField] private float orbit_center_speed = 2.1f;
    [SerializeField] private float orbit_center_size = 0.18f;

    [Tooltip("How much L/R are allowed during orbit (0 = off).")]
    [SerializeField] private float orbit_lr_rate = 2f;
    [SerializeField] private float orbit_lr_lifetime = 0.09f;
    [SerializeField] private float orbit_lr_speed = 1.0f;
    [SerializeField] private float orbit_lr_size = 0.11f;

    [Header("Charging Ramp - Center Hard")]
    [SerializeField] private float charge_center_minRate = 35f;
    [SerializeField] private float charge_center_maxRate = 140f;
    [SerializeField] private float charge_center_minLifetime = 0.18f;
    [SerializeField] private float charge_center_maxLifetime = 0.55f;
    [SerializeField] private float charge_center_minSpeed = 2.0f;
    [SerializeField] private float charge_center_maxSpeed = 6.0f;
    [SerializeField] private float charge_center_minSize = 0.18f;
    [SerializeField] private float charge_center_maxSize = 0.40f;

    [Tooltip("Optional: stabilization flicker on L/R while charging.")]
    [SerializeField] private bool charge_enableLRStabilizers = true;

    [SerializeField] private float charge_lr_rate = 8f;
    [SerializeField] private float charge_lr_lifetime = 0.12f;
    [SerializeField] private float charge_lr_speed = 1.8f;
    [SerializeField] private float charge_lr_size = 0.14f;

    [Header("Smoothing")]
    [Tooltip("How fast particle properties blend to targets (bigger = snappier).")]
    [SerializeField] private float lerpSpeed = 14f;

    private ThrustState _state = ThrustState.Idle;

    // Cached current values so we can smooth without fighting modules each frame.
    private float _lRate, _rRate, _cRate;
    private float _lLife, _rLife, _cLife;
    private float _lSpeed, _rSpeed, _cSpeed;
    private float _lSize, _rSize, _cSize;

    private void Awake()
    {
        if (Instance && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // Optional: uncomment if you want it to persist across scenes.
        // DontDestroyOnLoad(gameObject);

        PrimeParticle(leftThruster);
        PrimeParticle(rightThruster);
        PrimeParticle(centerThruster);

        ResetToIdle(true);
    }

    private void PrimeParticle(ParticleSystem ps)
    {
        if (!ps) return;
        // Ensures we can control emission without weird “it never started” edge cases.
        if (!ps.isPlaying) ps.Play(true);
    }

    private void Update()
    {
        // Smoothly apply current cached values to particle modules
        ApplyTo(leftThruster, ref _lRate, ref _lLife, ref _lSpeed, ref _lSize);
        ApplyTo(rightThruster, ref _rRate, ref _rLife, ref _rSpeed, ref _rSize);
        ApplyTo(centerThruster, ref _cRate, ref _cLife, ref _cSpeed, ref _cSize);
    }

    private void ApplyTo(ParticleSystem ps, ref float rate, ref float life, ref float speed, ref float size)
    {
        if (!ps) return;

        var em = ps.emission;
        var main = ps.main;

        // Emission
        em.enabled = (rate > 0.001f);
        em.rateOverTime = rate * globalEmission;

        // Lifetime / Speed / Size (acts like “intensity knobs” for voxel exhaust)
        main.startLifetime = Mathf.Max(0.01f, life);
        main.startSpeed = Mathf.Max(0.0f, speed);
        main.startSize = Mathf.Max(0.01f, size) * globalSize;
    }

    private float ExpLerp(float current, float target, float speed)
    {
        float t = 1f - Mathf.Exp(-speed * Time.deltaTime);
        return Mathf.Lerp(current, target, t);
    }

    private void SetTargets(
        float lRate, float lLife, float lSpeed, float lSize,
        float rRate, float rLife, float rSpeed, float rSize,
        float cRate, float cLife, float cSpeed, float cSize
    )
    {
        _lRate = ExpLerp(_lRate, lRate, lerpSpeed);
        _lLife = ExpLerp(_lLife, lLife, lerpSpeed);
        _lSpeed = ExpLerp(_lSpeed, lSpeed, lerpSpeed);
        _lSize = ExpLerp(_lSize, lSize, lerpSpeed);

        _rRate = ExpLerp(_rRate, rRate, lerpSpeed);
        _rLife = ExpLerp(_rLife, rLife, lerpSpeed);
        _rSpeed = ExpLerp(_rSpeed, rSpeed, lerpSpeed);
        _rSize = ExpLerp(_rSize, rSize, lerpSpeed);

        _cRate = ExpLerp(_cRate, cRate, lerpSpeed);
        _cLife = ExpLerp(_cLife, cLife, lerpSpeed);
        _cSpeed = ExpLerp(_cSpeed, cSpeed, lerpSpeed);
        _cSize = ExpLerp(_cSize, cSize, lerpSpeed);
    }

    // -------------------------
    // Public API (call these)
    // -------------------------

    /// <summary>
    /// Called while player has normal movement enabled (not orbiting).
    /// input: raw-ish intent vector (x strafe, y up/down, z forward).
    /// speed01: normalized 0..1 based on your max speed.
    /// boost01: normalized 0..1 based on boost charge (optional).
    /// </summary>
    public void SetFreeFlight(Vector3 input, float speed01, float boost01 = 0f)
    {
        _state = ThrustState.FreeFlight;

        float inputMag = Mathf.Clamp01(input.magnitude);

        // Emphasize strafing jets if you want “fighter” vibe.
        float steerWeight = free_lr_strafeEmphasis
            ? Mathf.Clamp01(Mathf.Abs(input.x) * 1.2f + Mathf.Abs(input.y) * 0.7f + Mathf.Abs(input.z) * 0.6f)
            : inputMag;

        // Intensity ramp: input + some speed + optional boost
        float intensity = Mathf.Clamp01(0.65f * steerWeight + 0.35f * speed01);
        intensity = Mathf.Clamp01(intensity + boost01 * 0.35f);

        float lrRate = Mathf.Lerp(free_lr_minRate, free_lr_maxRate, intensity);
        float lrLife = Mathf.Lerp(free_lr_minLifetime, free_lr_maxLifetime, Mathf.Clamp01(speed01 + boost01 * 0.5f));
        float lrSpeed = Mathf.Lerp(free_lr_minSpeed, free_lr_maxSpeed, intensity);
        float lrSize = Mathf.Lerp(free_lr_minSize, free_lr_maxSize, intensity);

        // Center: off unless boosting
        float cRate = free_center_idleRate;
        float cLife = free_center_idleLifetime;
        float cSpeed = 0f;
        float cSize = free_center_idleSize;

        if (boost01 > 0.01f)
        {
            // Rate scales with boost (0..1)
            cRate = free_center_boostRate * Mathf.Clamp01(boost01);

            // Borrow existing “free” center settings (you can optionally scale a bit with speed01)
            cLife = free_center_idleLifetime; // or Mathf.Lerp(free_center_idleLifetime, free_lr_maxLifetime, speed01 * 0.5f);
            cSpeed = free_center_boostSpeed;  // helps it feel like real thrust
            cSize = free_center_idleSize;     // or slightly bigger if you want: free_center_idleSize * 1.1f;
        }

        // If no input, kill L/R emission cleanly
        if (inputMag < 0.01f)
        {
            lrRate = 0f;
            lrSpeed = 0f;
        }

        SetTargets(
            lrRate, lrLife, lrSpeed, lrSize,
            lrRate, lrLife, lrSpeed, lrSize,
            cRate, cLife, cSpeed, cSize
        );
    }

    /// <summary>
    /// Called while orbiting but not charging.
    /// orbitStrength01 could be 0..1 based on orbit speed / planet strength (optional).
    /// </summary>
    public void SetOrbiting(float orbitStrength01 = 0.5f)
    {
        _state = ThrustState.Orbiting;

        float s = Mathf.Clamp01(orbitStrength01);

        // Idle-orbit: center OFF (or very low if you want a faint glow)
        float cRate = orbit_center_idleRate;
        float cLife = orbit_center_lifetime;
        float cSpeed = orbit_center_speed;
        float cSize = orbit_center_size;

        float lrRate = orbit_lr_rate;
        float lrLife = orbit_lr_lifetime;
        float lrSpeed = orbit_lr_speed;
        float lrSize = orbit_lr_size;

        SetTargets(
            lrRate, lrLife, lrSpeed, lrSize,
            lrRate, lrLife, lrSpeed, lrSize,
            cRate, cLife, cSpeed, cSize
        );
    }

    /// <summary>
    /// Called while charging (holding the charge input). charge01: 0..1 ramp.
    /// </summary>
    public void SetCharging(float charge01)
    {
        _state = ThrustState.Charging;

        float t = Mathf.Clamp01(charge01);

        float cRate = Mathf.Lerp(charge_center_minRate, charge_center_maxRate, t);
        float cLife = Mathf.Lerp(charge_center_minLifetime, charge_center_maxLifetime, t);
        float cSpeed = Mathf.Lerp(charge_center_minSpeed, charge_center_maxSpeed, t);
        float cSize = Mathf.Lerp(charge_center_minSize, charge_center_maxSize, t);

        float lrRate = 0f, lrLife = 0.1f, lrSpeed = 0f, lrSize = 0.1f;
        if (charge_enableLRStabilizers)
        {
            // Small stabilization jets that become noticeable mid/late charge
            float s = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.85f, t));
            lrRate = charge_lr_rate * s;
            lrLife = charge_lr_lifetime;
            lrSpeed = charge_lr_speed * s;
            lrSize = charge_lr_size;
        }

        SetTargets(
            lrRate, lrLife, lrSpeed, lrSize,
            lrRate, lrLife, lrSpeed, lrSize,
            cRate, cLife, cSpeed, cSize
        );
    }

    /// <summary>
    /// Call exactly when you launch/exit orbit. Cuts all emission quickly.
    /// </summary>
    public void OnLaunch()
    {
        _state = ThrustState.LaunchCut;

        SetTargets(
            0f, _lLife, 0f, _lSize,
            0f, _rLife, 0f, _rSize,
            0f, _cLife, 0f, _cSize
        );

        // Mach ring burst
        PlayOneShot(MachRing);
    }

    /// <summary>
    /// Put the system back into a calm state ready for free-flight input.
    /// </summary>
    public void ResetToIdle(bool immediate = false)
    {
        _state = ThrustState.Idle;

        if (immediate)
        {
            _lRate = _rRate = _cRate = 0f;
            _lSpeed = _rSpeed = _cSpeed = 0f;

            // Keep lifetimes/sizes non-zero so particles don’t freak out if you re-enable quickly
            _lLife = _rLife = free_lr_minLifetime;
            _cLife = free_center_idleLifetime;

            _lSize = _rSize = free_lr_minSize;
            _cSize = free_center_idleSize;
            return;
        }

        SetTargets(
            0f, free_lr_minLifetime, 0f, free_lr_minSize,
            0f, free_lr_minLifetime, 0f, free_lr_minSize,
            0f, free_center_idleLifetime, 0f, free_center_idleSize
        );
    }

    private void PlayOneShot(ParticleSystem ps)
    {
        if (!ps) return;

        GameObject go = ps.gameObject;

        // Reset & play
        go.SetActive(true);
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ps.Play(true);

        // Disable after it's done
        StartCoroutine(DisableAfter(ps));
    }

    private IEnumerator DisableAfter(ParticleSystem ps)
    {
        if (!ps) yield break;

        var main = ps.main;
        float duration = main.duration;

        // Account for start lifetime so we don't cut particles early
        float maxLifetime = main.startLifetime.constantMax;
        yield return new WaitForSeconds(duration + maxLifetime);

        if (ps)
            ps.gameObject.SetActive(false);
    }
}
