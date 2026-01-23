using UnityEngine;

[RequireComponent(typeof(ParticleSystem))]
public class VelocityDustField : MonoBehaviour
{
    [Header("Refs")]
    public Rigidbody shipRb;                 // the same RB you use in SimpleMove (child RB)
    public ParticleSystem ps;

    [Header("Speed Mapping")]
    public float maxEffectSpeed = 400f;      // speed where effect is "fully on" (cap)
    public AnimationCurve response = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Header("Trails")]
    public bool driveTrails = true;
    public float trailLifetimeAtZero = 0.02f;
    public float trailLifetimeAtMax = 0.18f;

    [Header("Optional density/alpha feel")]
    public bool driveEmission = false;
    public float emissionAtZero = 30f;
    public float emissionAtMax = 140f;
    public AnimationCurve responseEmiss = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Header("Velocity Over Lifetime (Local -Z)")]
    public bool driveVelZ = true;

    // If true: faster speed => smaller vel (uses 1 - speed01).
    // If false: faster speed => larger vel (uses speed01).
    public bool invertSpeedForVel = true;

    // Local Z velocity (positive numbers here; we’ll apply as negative Z)
    public float velZAtZero = 1f;   // how fast particles move toward -Z when speed is 0
    public float velZAtMax = 25f;  // how fast particles move toward -Z when speed is max

    public AnimationCurve responseVelZ = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Header("Scaling")]
    public bool driveScaling = false;
    public float minScaling = 1f;
    public float maxScaling = 5f;
    public AnimationCurve responseScaling = AnimationCurve.EaseInOut(0, 0, 1, 1);

    void Reset()
    {
        ps = GetComponent<ParticleSystem>();
    }

    void Awake()
    {
        if (!ps) ps = GetComponent<ParticleSystem>();
    }

    void LateUpdate()
    {
        if (!shipRb || !ps) return;

        //// 1) speed01
        float speed = shipRb.linearVelocity.magnitude;
        float speed01 = Mathf.Clamp01(speed / Mathf.Max(0.0001f, maxEffectSpeed));
        float t = Mathf.Clamp01(response.Evaluate(speed01));
        float e = Mathf.Clamp01(responseEmiss.Evaluate(speed01));
        float s = Mathf.Clamp01(responseScaling.Evaluate(speed01));

        // --- Velocity mapping based on speed (optionally inverted)
        float velInput01 = invertSpeedForVel ? (1f - speed01) : speed01;
        float vz01 = Mathf.Clamp01(responseVelZ.Evaluate(velInput01));
        float vz = Mathf.Lerp(velZAtZero, velZAtMax, vz01);

        //// 2) Desired dust flow direction: opposite ship world velocity
        //Vector3 worldVel = shipRb.linearVelocity;
        //Vector3 worldDir = (worldVel.sqrMagnitude > 0.000001f) ? (worldVel.normalized) : Vector3.forward;

        //// Convert world direction into THIS particle system's local space
        //// (Because the ParticleSystem sim space is Local)
        //Vector3 localDir = transform.InverseTransformDirection(worldDir);

        //// Flow speed scales with ship speed (capped)
        //float flowSpeed = (speed * flowMultiplier) * t;

        //// 3) Apply particle velocity over lifetime (in local space)
        //var vol = ps.velocityOverLifetime;
        //vol.enabled = true;
        //vol.space = ParticleSystemSimulationSpace.Local;

        //// Move dust opposite our movement direction so it streams past us
        //Vector3 localFlow = -localDir * flowSpeed;

        //// Add subtle idle drift so "stopped" doesn't look frozen
        //if (speed < 1.0f)
        //{
        //    // small sideways drift in local space
        //    localFlow += new Vector3(
        //        Mathf.Sin(Time.time * 0.9f),
        //        Mathf.Sin(Time.time * 1.1f + 1.7f),
        //        Mathf.Sin(Time.time * 0.7f + 3.1f)
        //    ) * idleDriftSpeed;
        //}

        //vol.x = new ParticleSystem.MinMaxCurve(localFlow.x);
        //vol.y = new ParticleSystem.MinMaxCurve(localFlow.y);
        //vol.z = new ParticleSystem.MinMaxCurve(localFlow.z);

        // 4) Trails length based on speed
        if (driveTrails)
        {
            var trails = ps.trails;
            trails.enabled = true;

            float trailLife = Mathf.Lerp(trailLifetimeAtZero, trailLifetimeAtMax, t);
            trails.lifetime = new ParticleSystem.MinMaxCurve(trailLife);
        }

        // 5) Optional: emission scaling (NOT required; use if it feels too sparse when fast)
        if (driveEmission)
        {
            var emission = ps.emission;
            emission.rateOverTime = Mathf.Lerp(emissionAtZero, emissionAtMax, e);
        }

        if (driveVelZ)
        {
            var vol = ps.velocityOverLifetime;
            vol.enabled = true;
            vol.space = ParticleSystemSimulationSpace.Local;

            // Feed into local -Z
            vol.x = new ParticleSystem.MinMaxCurve(0f);
            vol.y = new ParticleSystem.MinMaxCurve(0f);
            vol.z = new ParticleSystem.MinMaxCurve(-vz);
        }

        // 6) Optional: size scaling (MULTIPLIER on top of start size randomness)
        if (driveScaling)
        {
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;

            // We want to multiply the start size, not override it
            sol.size = new ParticleSystem.MinMaxCurve(1f);

            // Scale multiplier driven by speed
            float scaleMul = Mathf.Lerp(minScaling, maxScaling, s);

            // Use separate axes so this acts as a uniform multiplier
            sol.separateAxes = true;
            sol.x = new ParticleSystem.MinMaxCurve(scaleMul);
            sol.y = new ParticleSystem.MinMaxCurve(scaleMul);
            sol.z = new ParticleSystem.MinMaxCurve(scaleMul);
        }
    }
}