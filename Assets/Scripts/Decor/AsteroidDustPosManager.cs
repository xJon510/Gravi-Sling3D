using System;
using UnityEngine;

/// <summary>
/// Manages a fixed-count "dust asteroid" set in an oriented recyclic box shell:
/// - Outer box defines the keep-in-bounds volume.
/// - Inner box defines a hollow hole around the player.
/// - Wrap happens primarily along the forward axis (velocity dir), but we also wrap X/Y to keep drift contained.
/// - Stores world-space positions + rotations and updates them with optional drift.
/// Rendering is handled later (instanced or pooled), using Positions/Rotations/Scales.
/// </summary>
public class AsteroidDustPosManager : MonoBehaviour
{
    [Header("Refs")]
    public Transform player;
    public Rigidbody shipRb; // optional: for forward axis from velocity

    [Header("Counts / Seed")]
    [Min(1)] public int count = 450;
    public int seed = 12345;
    public bool regenerateOnStart = true;

    [Header("Box Shell (in oriented axis-space)")]
    public Vector3 outerHalfExtents = new Vector3(600f, 600f, 600f);
    public Vector3 innerHalfExtents = new Vector3(80f, 80f, 80f);
    [Min(0f)] public float surfaceEpsilon = 0.5f; // pushes wraps slightly beyond a face to avoid instant re-trigger

    [Header("Forward Axis")]
    [Tooltip("If ship speed is below this, use player forward instead of velocity.")]
    public float minSpeedForVelocityAxis = 0.25f;

    [Tooltip("Smooth the axis to prevent hard direction flips causing mass wraps.")]
    [Range(0f, 30f)] public float axisSmoothing = 8f;

    [Header("Random Scale")]
    public Vector2 uniformScaleRange = new Vector2(0.6f, 2.0f);

    [Header("Random Rotation + Rotation Drift")]
    public bool randomRotation = true;
    public Vector2 angularSpeedRangeDeg = new Vector2(0f, 25f);

    [Header("Position Drift (world-space ambient motion)")]
    public bool enablePositionDrift = true;
    public Vector2 driftSpeedRange = new Vector2(0f, 1.25f); // units/sec
    [Range(0f, 1f)] public float driftDirectionalBias = 0.0f; // 0 = fully random, 1 = more aligned to forward axis

    [Header("Pop Hiding (for renderer later)")]
    [Tooltip("If > 0, we mark items as hidden for this many frames right after a wrap.")]
    [Min(0)] public int hideFramesOnWrap = 1;

    // Runtime buffers (world-space, stable count)
    public Vector3[] Positions { get; private set; }
    public Quaternion[] Rotations { get; private set; }
    public float[] Scales { get; private set; }

    // Extra per-instance motion
    private Vector3[] _angularVelDeg;  // degrees/sec on each axis
    private Vector3[] _driftVel;       // units/sec world-space
    private int[] _hideFrames;         // countdown frames to hide after wrap (renderer can consult)

    // Smoothed axis
    private Vector3 _axisFwd = Vector3.forward;

    // RNG
    private System.Random _rng;

    public event Action OnRegenerated;

    private void Awake()
    {
        if (!player) player = Camera.main ? Camera.main.transform : transform;
        _rng = new System.Random(seed);
    }

    private void Start()
    {
        if (regenerateOnStart)
            Regenerate();
    }

    public void Regenerate()
    {
        if (count < 1) count = 1;

        Positions = new Vector3[count];
        Rotations = new Quaternion[count];
        Scales = new float[count];
        _angularVelDeg = new Vector3[count];
        _driftVel = new Vector3[count];
        _hideFrames = new int[count];

        // Initialize axis
        _axisFwd = GetTargetAxis();

        for (int i = 0; i < count; i++)
        {
            // Place in the hollow shell: inside outer box but outside inner box
            Vector3 local = SamplePointInHollowBox(_rng, outerHalfExtents, innerHalfExtents);

            // Convert from axis-space -> world-space
            OrientedBasis basis = BuildBasis(_axisFwd);
            Vector3 worldPos = player.position + basis.ToWorld(local);

            Positions[i] = worldPos;

            // Rotation
            Rotations[i] = randomRotation ? RandomRotation(_rng) : Quaternion.identity;

            // Scale
            Scales[i] = RandomRange(_rng, uniformScaleRange.x, uniformScaleRange.y);

            // Angular drift
            float minA = angularSpeedRangeDeg.x;
            float maxA = angularSpeedRangeDeg.y;
            if (maxA < minA) (minA, maxA) = (maxA, minA);

            _angularVelDeg[i] = new Vector3(
                RandomSignedRange(_rng, minA, maxA),
                RandomSignedRange(_rng, minA, maxA),
                RandomSignedRange(_rng, minA, maxA)
            );

            // Position drift
            _driftVel[i] = enablePositionDrift
                ? RandomDriftVelocity(_rng, _axisFwd, driftSpeedRange, driftDirectionalBias)
                : Vector3.zero;

            _hideFrames[i] = 0;
        }

        OnRegenerated?.Invoke();
    }

    private void Update()
    {
        if (Positions == null || Positions.Length == 0) return;

        float dt = Time.deltaTime;

        // Smooth axis to prevent mass wrap spikes on direction changes
        Vector3 targetAxis = GetTargetAxis();
        _axisFwd = Vector3.Slerp(_axisFwd, targetAxis, 1f - Mathf.Exp(-axisSmoothing * dt));
        if (_axisFwd.sqrMagnitude < 1e-6f) _axisFwd = player.forward;

        OrientedBasis basis = BuildBasis(_axisFwd);

        Vector3 outer = outerHalfExtents;
        Vector3 inner = innerHalfExtents;

        // Safety: inner must be smaller than outer per-axis
        inner = new Vector3(
            Mathf.Min(inner.x, outer.x - 0.001f),
            Mathf.Min(inner.y, outer.y - 0.001f),
            Mathf.Min(inner.z, outer.z - 0.001f)
        );

        for (int i = 0; i < Positions.Length; i++)
        {
            // Apply ambient position drift in world space
            Positions[i] += _driftVel[i] * dt;

            // Apply rotation drift
            Vector3 av = _angularVelDeg[i];
            if (av.sqrMagnitude > 0f)
            {
                Quaternion dq = Quaternion.Euler(av * dt);
                Rotations[i] = dq * Rotations[i];
            }

            // Wrap logic in axis-space
            Vector3 relWorld = Positions[i] - player.position;
            Vector3 rel = basis.ToLocal(relWorld);

            bool wrapped = false;

            // --- 1) If inside INNER hole region, push to BACK face (negative z),
            // keeping x/y so it feels "same asteroid" continuing.
            if (Mathf.Abs(rel.x) < inner.x &&
                Mathf.Abs(rel.y) < inner.y &&
                Mathf.Abs(rel.z) < inner.z)
            {
                // Move it to back face of inner box, preserving lateral offsets.
                rel.z = -inner.z - surfaceEpsilon;
                wrapped = true;
            }

            // --- 2) Keep within OUTER bounds by wrapping like a 3D recyclic box.
            // We wrap X/Y too so drift can't leak them out forever.
            if (rel.x > outer.x) { rel.x -= 2f * outer.x + surfaceEpsilon; wrapped = true; }
            else if (rel.x < -outer.x) { rel.x += 2f * outer.x + surfaceEpsilon; wrapped = true; }

            if (rel.y > outer.y) { rel.y -= 2f * outer.y + surfaceEpsilon; wrapped = true; }
            else if (rel.y < -outer.y) { rel.y += 2f * outer.y + surfaceEpsilon; wrapped = true; }

            // The main “flow”: if it goes too far BACK, bring it to FRONT.
            // This is the JumpSpace conveyor belt: always refill in front.
            if (rel.z < -outer.z)
            {
                rel.z += 2f * outer.z + surfaceEpsilon;
                wrapped = true;
            }
            // Optional: if it goes too far FRONT (rare), wrap to back
            else if (rel.z > outer.z)
            {
                rel.z -= 2f * outer.z + surfaceEpsilon;
                wrapped = true;
            }

            if (wrapped)
            {
                Positions[i] = player.position + basis.ToWorld(rel);
                if (hideFramesOnWrap > 0)
                    _hideFrames[i] = hideFramesOnWrap;
            }
            else if (_hideFrames[i] > 0)
            {
                _hideFrames[i]--;
            }
        }
    }

    /// <summary>
    /// For your future renderer: if > 0, skip drawing this instance (one-frame "no pop" on wraps).
    /// </summary>
    public bool IsHidden(int index) => _hideFrames != null && (uint)index < (uint)_hideFrames.Length && _hideFrames[index] > 0;

    // ----------------- Axis + Basis -----------------

    private Vector3 GetTargetAxis()
    {
        if (shipRb)
        {
            Vector3 v = shipRb.linearVelocity;
            if (v.sqrMagnitude >= minSpeedForVelocityAxis * minSpeedForVelocityAxis)
                return v.normalized;
        }
        return player.forward.sqrMagnitude > 1e-6f ? player.forward.normalized : Vector3.forward;
    }

    private struct OrientedBasis
    {
        public Vector3 right, up, fwd;

        public Vector3 ToWorld(Vector3 local) => right * local.x + up * local.y + fwd * local.z;
        public Vector3 ToLocal(Vector3 world) => new Vector3(Vector3.Dot(world, right), Vector3.Dot(world, up), Vector3.Dot(world, fwd));
    }

    private static OrientedBasis BuildBasis(Vector3 fwd)
    {
        fwd = (fwd.sqrMagnitude > 1e-6f) ? fwd.normalized : Vector3.forward;

        // Pick an up that won't collapse when fwd ~ worldUp
        Vector3 upSeed = (Mathf.Abs(Vector3.Dot(fwd, Vector3.up)) > 0.9f) ? Vector3.right : Vector3.up;

        Vector3 right = Vector3.Normalize(Vector3.Cross(upSeed, fwd));
        Vector3 up = Vector3.Normalize(Vector3.Cross(fwd, right));

        return new OrientedBasis { right = right, up = up, fwd = fwd };
    }

    // ----------------- Sampling -----------------

    private static Vector3 SamplePointInHollowBox(System.Random rng, Vector3 outerHalf, Vector3 innerHalf)
    {
        // Rejection sampling is fine for 450, and it's only during initial placement.
        // (We could do a non-reject version later if you want it deterministic/no-loop.)
        for (int tries = 0; tries < 10000; tries++)
        {
            float x = RandomRange(rng, -outerHalf.x, outerHalf.x);
            float y = RandomRange(rng, -outerHalf.y, outerHalf.y);
            float z = RandomRange(rng, -outerHalf.z, outerHalf.z);

            bool insideInner =
                Mathf.Abs(x) < innerHalf.x &&
                Mathf.Abs(y) < innerHalf.y &&
                Mathf.Abs(z) < innerHalf.z;

            if (!insideInner)
                return new Vector3(x, y, z);
        }

        // Fallback: guaranteed outside inner (push to outer face)
        return new Vector3(outerHalf.x, 0f, 0f);
    }

    // ----------------- Random helpers -----------------

    private static float RandomRange(System.Random rng, float min, float max)
    {
        if (max < min) (min, max) = (max, min);
        return (float)(min + (max - min) * rng.NextDouble());
    }

    private static float RandomSignedRange(System.Random rng, float minAbs, float maxAbs)
    {
        float mag = RandomRange(rng, minAbs, maxAbs);
        return (rng.NextDouble() < 0.5) ? -mag : mag;
    }

    private static Quaternion RandomRotation(System.Random rng)
    {
        // Uniform random rotation (same method you used elsewhere) :contentReference[oaicite:2]{index=2}
        double u1 = rng.NextDouble();
        double u2 = rng.NextDouble();
        double u3 = rng.NextDouble();

        double sqrt1MinusU1 = Math.Sqrt(1.0 - u1);
        double sqrtU1 = Math.Sqrt(u1);

        float x = (float)(sqrt1MinusU1 * Math.Sin(2.0 * Math.PI * u2));
        float y = (float)(sqrt1MinusU1 * Math.Cos(2.0 * Math.PI * u2));
        float z = (float)(sqrtU1 * Math.Sin(2.0 * Math.PI * u3));
        float w = (float)(sqrtU1 * Math.Cos(2.0 * Math.PI * u3));

        return new Quaternion(x, y, z, w);
    }

    private static Vector3 RandomDriftVelocity(System.Random rng, Vector3 axisFwd, Vector2 speedRange, float bias01)
    {
        float s = RandomRange(rng, speedRange.x, speedRange.y);

        // Random unit direction
        Vector3 dir = UnityRandomOnUnitSphere(rng);

        // Bias toward forward axis if requested
        if (bias01 > 0f)
        {
            Vector3 biased = Vector3.Slerp(dir, axisFwd.normalized, Mathf.Clamp01(bias01));
            dir = biased.normalized;
        }

        return dir * s;
    }

    private static Vector3 UnityRandomOnUnitSphere(System.Random rng)
    {
        // Simple method: sample spherical coords
        double u = rng.NextDouble();
        double v = rng.NextDouble();

        double theta = 2.0 * Math.PI * u;
        double phi = Math.Acos(2.0 * v - 1.0);

        float sinPhi = (float)Math.Sin(phi);
        float x = sinPhi * (float)Math.Cos(theta);
        float y = sinPhi * (float)Math.Sin(theta);
        float z = (float)Math.Cos(phi);

        return new Vector3(x, y, z);
    }
}
