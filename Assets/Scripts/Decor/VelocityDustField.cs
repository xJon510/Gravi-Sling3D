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

    [Header("Particle Motion")]
    [Tooltip("Multiplier for how fast dust flows past you relative to ship speed.")]
    public float flowMultiplier = 1.0f;

    [Header("Trails")]
    public bool driveTrails = true;
    public float trailLifetimeAtZero = 0.02f;
    public float trailLifetimeAtMax = 0.18f;

    [Header("Optional density/alpha feel")]
    public bool driveEmission = false;
    public float emissionAtZero = 30f;
    public float emissionAtMax = 140f;

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

        // 1) speed01
        float speed = shipRb.linearVelocity.magnitude;
        float speed01 = Mathf.Clamp01(speed / Mathf.Max(0.0001f, maxEffectSpeed));
        float t = Mathf.Clamp01(response.Evaluate(speed01));

        // 2) Desired dust flow direction: opposite ship world velocity
        Vector3 worldVel = shipRb.linearVelocity;
        Vector3 worldDir = (worldVel.sqrMagnitude > 0.000001f) ? (worldVel.normalized) : Vector3.forward;

        // Convert world direction into THIS particle system's local space
        // (Because the ParticleSystem sim space is Local)
        Vector3 localDir = transform.InverseTransformDirection(worldDir);

        // Flow speed scales with ship speed (capped)
        float flowSpeed = (speed * flowMultiplier) * t;

        // 3) Apply particle velocity over lifetime (in local space)
        var vol = ps.velocityOverLifetime;
        vol.enabled = true;
        vol.space = ParticleSystemSimulationSpace.Local;

        // Move dust opposite our movement direction so it streams past us
        Vector3 localFlow = -localDir * flowSpeed;

        vol.x = new ParticleSystem.MinMaxCurve(localFlow.x);
        vol.y = new ParticleSystem.MinMaxCurve(localFlow.y);
        vol.z = new ParticleSystem.MinMaxCurve(localFlow.z);

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
            emission.rateOverTime = Mathf.Lerp(emissionAtZero, emissionAtMax, t);
        }
    }
}
