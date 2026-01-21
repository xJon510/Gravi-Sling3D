using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Minimal renderer for AsteroidDustPosManager using GPU instancing.
/// - Randomly assigns each instance a mesh from a list (sticky assignment).
/// - Draws in 1023-sized batches via Graphics.DrawMeshInstanced.
/// - Can skip draw for a few frames after wrap using posManager.IsHidden(i).
/// </summary>
public class AsteroidDustInstancedRenderer : MonoBehaviour
{
    [Header("Refs")]
    public AsteroidDustPosManager posManager;

    [Header("Rendering")]
    public Material material;
    public List<Mesh> meshes = new List<Mesh>();

    [Tooltip("If set, overrides posManager.player for render bounds.")]
    public Transform boundsCenterOverride;

    [Tooltip("Layer used for instanced rendering.")]
    public int layer = 0;

    [Tooltip("Shadow settings (dust usually off).")]
    public ShadowCastingMode shadows = ShadowCastingMode.Off;
    public bool receiveShadows = false;

    [Header("Bounds / Culling")]
    [Tooltip("Big bounds to avoid Unity culling your instances. Should cover your outer box extents.")]
    public float boundsPadding = 50f;

    [Tooltip("If true, only renders when mesh+material are valid.")]
    public bool disableIfInvalid = true;

    [Header("Distance Band Scaling")]
    public Transform distanceFrom; // usually camera or player
    [Tooltip("If true, normalize distance using posManager.outerHalfExtents magnitude.")]
    public bool normalizeByOuterBox = true;

    [Tooltip("Manual max distance if not normalizing by outer box.")]
    public float maxDistance = 600f;

    [Tooltip("Band curve: x = 0 near, x = 1 far. y = scale multiplier (0..1).")]
    public AnimationCurve scaleBand = AnimationCurve.Linear(0, 0, 0.5f, 1);

    [Range(0f, 2f)] public float bandStrength = 1f; // 0 disables, 1 full
    [Tooltip("Clamp very small scales to 0 to effectively hide.")]
    public float cullScaleThreshold = 0.02f;

    [Header("Random Assignment")]
    public int meshSeed = 1337;

    // Per-instance mesh id (sticky)
    private int[] _meshId;

    // Reusable per-mesh matrix lists (to avoid allocations)
    private List<Matrix4x4>[] _perMeshMatrices;

    // Temp batch buffer
    private static readonly Matrix4x4[] _batch = new Matrix4x4[1023];

    private void Awake()
    {
        if (!posManager) posManager = GetComponent<AsteroidDustPosManager>();
        RebuildMeshAssignments();
    }

    private void OnEnable()
    {
        if (posManager != null)
            posManager.OnRegenerated += RebuildMeshAssignments;
    }

    private void OnDisable()
    {
        if (posManager != null)
            posManager.OnRegenerated -= RebuildMeshAssignments;
    }

    private void LateUpdate()
    {
        if (!posManager || posManager.Positions == null) return;

        if (!IsValid())
        {
            if (disableIfInvalid) return;
        }

        EnsureBuffers();

        // Clear per-mesh lists (keep capacity)
        for (int m = 0; m < _perMeshMatrices.Length; m++)
            _perMeshMatrices[m].Clear();

        int n = posManager.Positions.Length;

        // Build matrices
        for (int i = 0; i < n; i++)
        {
            if (posManager.IsHidden(i))
                continue;

            int mid = _meshId[i];
            if ((uint)mid >= (uint)meshes.Count) mid = 0;

            Vector3 pos = posManager.Positions[i];
            float s = posManager.Scales[i];
            Quaternion rot = posManager.Rotations[i];
            rot = NormalizeSafe(rot);

            // Distance band scale
            float d01 = ComputeDistance01(pos);
            float band = Mathf.Clamp01(scaleBand.Evaluate(d01));
            float bandScale = Mathf.Lerp(1f, band, Mathf.Clamp01(bandStrength));

            float finalS = s * bandScale;

            // If it's basically invisible, skip drawing entirely (saves fill + avoids tiny specks)
            if (finalS <= cullScaleThreshold)
                continue;

            _perMeshMatrices[mid].Add(Matrix4x4.TRS(pos, rot, Vector3.one * finalS));
        }

        // Draw each mesh bucket
        for (int m = 0; m < meshes.Count; m++)
        {
            Mesh mesh = meshes[m];
            if (!mesh) continue;

            var list = _perMeshMatrices[m];
            int total = list.Count;
            int offset = 0;

            while (offset < total)
            {
                int take = Mathf.Min(1023, total - offset);

                // Copy into fixed array for DrawMeshInstanced
                for (int k = 0; k < take; k++)
                    _batch[k] = list[offset + k];

                Graphics.DrawMeshInstanced(
                    mesh,
                    submeshIndex: 0,
                    material: material,
                    matrices: _batch,
                    count: take,
                    properties: null,
                    castShadows: shadows,
                    receiveShadows: receiveShadows,
                    layer: layer,
                    camera: null,
                    lightProbeUsage: UnityEngine.Rendering.LightProbeUsage.Off,
                    lightProbeProxyVolume: null
                );

                offset += take;
            }
        }
    }

    private bool IsValid()
    {
        if (material == null) return false;
        if (meshes == null || meshes.Count == 0) return false;

        // Ensure at least one non-null mesh
        for (int i = 0; i < meshes.Count; i++)
            if (meshes[i] != null) return true;

        return false;
    }

    private void EnsureBuffers()
    {
        int n = posManager.Positions.Length;

        // mesh ids
        if (_meshId == null || _meshId.Length != n)
            RebuildMeshAssignments();

        // per-mesh lists
        if (_perMeshMatrices == null || _perMeshMatrices.Length != meshes.Count)
        {
            _perMeshMatrices = new List<Matrix4x4>[meshes.Count];
            for (int i = 0; i < meshes.Count; i++)
                _perMeshMatrices[i] = new List<Matrix4x4>(Mathf.Max(64, n / Mathf.Max(1, meshes.Count)));
        }
    }

    private void RebuildMeshAssignments()
    {
        if (!posManager || posManager.Positions == null) return;

        int n = posManager.Positions.Length;
        _meshId = new int[n];

        int meshCount = Mathf.Max(1, meshes.Count);
        var rng = new System.Random(meshSeed);

        for (int i = 0; i < n; i++)
            _meshId[i] = rng.Next(meshCount);

        // Rebuild per-mesh lists if needed
        if (_perMeshMatrices == null || _perMeshMatrices.Length != meshes.Count)
        {
            _perMeshMatrices = new List<Matrix4x4>[meshes.Count];
            for (int i = 0; i < meshes.Count; i++)
                _perMeshMatrices[i] = new List<Matrix4x4>(Mathf.Max(64, n / Mathf.Max(1, meshes.Count)));
        }
    }

    private Bounds ComputeWorldBounds()
    {
        Transform centerT = boundsCenterOverride ? boundsCenterOverride : posManager.player;
        Vector3 c = centerT ? centerT.position : Vector3.zero;

        // Should cover your oriented outer cube extents regardless of axis orientation.
        // We use a big AABB in world space sized from outerHalfExtents.
        Vector3 half = posManager.outerHalfExtents;
        float pad = Mathf.Max(0f, boundsPadding);

        Vector3 size = new Vector3(
            (half.x + pad) * 2f,
            (half.y + pad) * 2f,
            (half.z + pad) * 2f
        );

        return new Bounds(c, size);
    }

    private static Quaternion NormalizeSafe(Quaternion q)
    {
        // Fast path: if it's already basically unit length, return as-is
        float ls = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;

        // Handle NaN/Inf or degenerate
        if (!float.IsFinite(ls) || ls < 1e-12f)
            return Quaternion.identity;

        // Normalize if slightly off
        float inv = 1.0f / Mathf.Sqrt(ls);
        return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
    }
    private float ComputeDistance01(Vector3 instancePos)
    {
        Vector3 fromPos = (distanceFrom ? distanceFrom.position :
                          (posManager.player ? posManager.player.position : Vector3.zero));

        float d = Vector3.Distance(fromPos, instancePos);

        float denom;
        if (normalizeByOuterBox && posManager != null)
        {
            // Use an approximate "radius" for your box. Magnitude works well as a single knob.
            denom = posManager.outerHalfExtents.magnitude;
        }
        else
        {
            denom = Mathf.Max(0.0001f, maxDistance);
        }

        return Mathf.Clamp01(d / Mathf.Max(0.0001f, denom));
    }
}
