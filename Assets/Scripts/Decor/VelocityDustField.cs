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

    [Header("Recycling Shell (World Space)")]
    [Tooltip("Outer radius of the spherical shell around the ship.")]
    public float shellOuterRadius = 600f;

    [Tooltip("Shell thickness (inner radius = outer - thickness).")]
    public float shellThickness = 80f;

    [Tooltip("How far toward the velocity direction we treat as 'front' (fraction of outer radius).")]
    [Range(0.2f, 1.5f)] public float frontPlane = 1.0f;

    [Tooltip("How far opposite velocity we treat as 'back' (fraction of outer radius).")]
    [Range(0.2f, 1.5f)] public float backPlane = 1.0f;

    [Tooltip("Extra scatter sideways when recycling (0 = perfectly on-axis).")]
    [Range(0f, 1f)] public float lateralScatter = 0.9f;

    [Tooltip("If true, also re-seed particles that get too close to the ship.")]
    public bool enforceInnerHole = true;

    [Tooltip("Minimum radius around ship to keep clear (prevents 'spawning too close').")]
    public float innerHoleRadius = 40f;

    [Header("Visibility Hiding")]
    public float visibleSize = 1f;     // match your particle system start size
    public float hiddenSize = 0f;      // 0 = invisible

    [Header("Trails")]
    public bool driveTrails = true;
    public float trailLifetimeAtZero = 0.02f;
    public float trailLifetimeAtMax = 0.18f;

    [Header("Optional density/alpha feel")]
    public bool driveEmission = false;
    public float emissionAtZero = 30f;
    public float emissionAtMax = 140f;

    // Cache original per-particle size for range-based start sizes
    private readonly System.Collections.Generic.Dictionary<uint, float> _sizeCache
        = new System.Collections.Generic.Dictionary<uint, float>(2048);

    // Internal particle buffer (reused every frame, no GC)
    private ParticleSystem.Particle[] _parts;

    void Reset()
    {
        ps = GetComponent<ParticleSystem>();
    }

    void Awake()
    {
        if (!ps) ps = GetComponent<ParticleSystem>();

        // We want "static world dust" + manual recycling.
        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        // Ensure a buffer exists sized to maxParticles.
        EnsureBuffer();
    }

    void OnValidate()
    {
        if (ps) EnsureBuffer();
        shellThickness = Mathf.Max(0.001f, shellThickness);
        shellOuterRadius = Mathf.Max(shellThickness + 0.001f, shellOuterRadius);
        innerHoleRadius = Mathf.Max(0f, innerHoleRadius);
    }

    void EnsureBuffer()
    {
        var main = ps.main;
        int max = Mathf.Max(1, main.maxParticles);
        if (_parts == null || _parts.Length != max)
            _parts = new ParticleSystem.Particle[max];
    }

    void LateUpdate()
    {
        if (!shipRb || !ps) return;

        // 1) speed01 (kept)
        float speed = shipRb.linearVelocity.magnitude;
        float speed01 = Mathf.Clamp01(speed / Mathf.Max(0.0001f, maxEffectSpeed));
        float t = Mathf.Clamp01(response.Evaluate(speed01));

        // 2) Velocity direction (kept idea, but used for front/back warping)
        Vector3 worldVel = shipRb.linearVelocity;
        Vector3 worldDir = (worldVel.sqrMagnitude > 0.000001f) ? worldVel.normalized : shipRb.transform.forward;

        // 3) Recycle particles onto spherical shell in WORLD space
        RecycleOnShell(shipRb.worldCenterOfMass, worldDir);

        // 4) Trails length based on speed (kept)
        if (driveTrails)
        {
            var trails = ps.trails;
            trails.enabled = true;

            float trailLife = Mathf.Lerp(trailLifetimeAtZero, trailLifetimeAtMax, t);
            trails.lifetime = new ParticleSystem.MinMaxCurve(trailLife);
        }

        // 5) Optional emission scaling (kept)
        if (driveEmission)
        {
            var emission = ps.emission;
            emission.rateOverTime = Mathf.Lerp(emissionAtZero, emissionAtMax, t);
        }
    }

    void RecycleOnShell(Vector3 shipPos, Vector3 velDir)
    {
        EnsureBuffer();

        int count = ps.GetParticles(_parts);
        if (count <= 0) return;

        float outer = shellOuterRadius;
        float inner = Mathf.Max(0.001f, outer - shellThickness);

        // Planes along movement axis (front/back zones)
        float frontZ = outer * frontPlane;
        float backZ = -outer * backPlane;

        // Build a stable perpendicular basis for lateral scatter
        Vector3 up = (Mathf.Abs(Vector3.Dot(velDir, Vector3.up)) > 0.9f) ? Vector3.right : Vector3.up;
        Vector3 right = Vector3.Normalize(Vector3.Cross(up, velDir));
        Vector3 orthoUp = Vector3.Normalize(Vector3.Cross(velDir, right));

        for (int i = 0; i < count; i++)
        {
            var p = _parts[i];

            // If hidden, restore its cached size
            if (p.startSize <= hiddenSize + 0.0001f)
            {
                if (_sizeCache.TryGetValue(p.randomSeed, out float originalSize))
                {
                    p.startSize = originalSize;
                    _sizeCache.Remove(p.randomSeed);
                }
                else
                {
                    p.startSize = visibleSize; // fallback
                }

                _parts[i] = p;
                continue;
            }

            Vector3 toP = p.position - shipPos;
            float dist = toP.magnitude;

            bool reseed = false;

            if (dist > outer + 0.001f) reseed = true;
            if (enforceInnerHole && dist < Mathf.Max(innerHoleRadius, 0.001f)) reseed = true;

            float z = Vector3.Dot(toP, velDir);
            if (z < backZ) reseed = true;
            if (z > frontZ) reseed = true;

            if (reseed)
            {
                // Cache original size BEFORE hiding (for ranged start sizes)
                _sizeCache[p.randomSeed] = p.startSize;

                // Compute new position FIRST
                Vector2 disk = Random.insideUnitCircle * (outer * lateralScatter);
                Vector3 lateral = right * disk.x + orthoUp * disk.y;
                Vector3 dir = (velDir * outer + lateral).normalized;

                float r = Random.Range(inner, outer);
                p.position = shipPos + dir * r;

                // Hide particle for this frame so the teleport is never seen
                p.startSize = hiddenSize;
                p.velocity = Vector3.zero;
            }

            _parts[i] = p;
        }

        ps.SetParticles(_parts, count);
    }
}
