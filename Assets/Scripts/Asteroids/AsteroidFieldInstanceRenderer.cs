using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public class AsteroidFieldInstancedRenderer : MonoBehaviour
{
    [Serializable]
    public class AsteroidTypeRender
    {
        public string name = "Type";
        public Material material;

        [Header("LOD Meshes")]
        public Mesh lod0Mesh;
        public Mesh lod1Mesh;
        public Mesh lod2Mesh;

        [Tooltip("Optional: override base radius for culling/LOD heuristics later. Not used in Step 2.")]
        public float baseRadius = 1f;

        public bool IsValid =>
            material != null &&
            lod0Mesh != null &&
            lod1Mesh != null &&
            lod2Mesh != null;
    }

    [Header("Input Data (Runtime Chunks)")]
    public List<AsteroidFieldData> fieldDatas = new List<AsteroidFieldData>();

    [Tooltip("If null, uses Camera.main.")]
    public Camera renderCamera;

    [Header("Type -> LOD Mesh Map (size should be 15)")]
    public AsteroidTypeRender[] typeRenders = new AsteroidTypeRender[15];

    [Header("Global LOD Distances (meters)")]
    [Tooltip("Distance < LOD0Distance => LOD0\n" +
             "LOD0Distance..LOD1Distance => LOD1\n" +
             ">= LOD1Distance => LOD2")]
    public float lod0Distance = 60f;

    public float lod1Distance = 140f;

    [Header("Rendering")]
    public ShadowCastingMode shadowCasting = ShadowCastingMode.Off;
    public bool receiveShadows = false;

    [Tooltip("Layer used for rendering (affects culling masks, etc.)")]
    public int renderLayer = 0;

    [Tooltip("Only render in play mode? If false, will also render in edit mode (Scene/Game view).")]
    public bool onlyRenderInPlayMode = false;

    [Header("Optional: Rotation Drift")]
    [Tooltip("If true, applies angularVelocityDeg from the data (degrees/sec) to spin asteroids. " +
             "This updates rotations & matrices each frame in play mode.")]
    public bool applyRotationDriftInPlayMode = false;

    // Internal cached matrices (one per instance)
    private Matrix4x4[] _matrices;
    private Quaternion[] _runtimeRotations; // only used if drift is enabled
    private bool _initialized;

    // Buckets: [typeId, lodIndex] -> list of matrices
    private List<Matrix4x4>[,] _buckets;

    private const int MaxInstancesPerCall = 1023;

    private void OnEnable()
    {
        //InitIfNeeded(force: true);
    }

    private void OnDisable()
    {
        _initialized = false;
        _matrices = null;
        _runtimeRotations = null;
        _buckets = null;
    }

    private void Update()
    {
        if (onlyRenderInPlayMode && !Application.isPlaying)
            return;

        if (fieldDatas == null || fieldDatas.Count == 0)
            return;

        var cam = renderCamera ? renderCamera : Camera.main;
        if (!cam)
            return;

        foreach (var data in fieldDatas)
        {
            if (data == null || data.count <= 0)
                continue;

            InitIfNeeded(data, force: false);

            if (applyRotationDriftInPlayMode && Application.isPlaying)
                ApplyRotationDrift(data, Time.deltaTime);

            Render(data, cam);
        }
    }
    private void InitIfNeeded(AsteroidFieldData fieldData, bool force)
    {
        if (!force && _initialized)
            return;

        if (fieldData == null)
        {
            _initialized = false;
            return;
        }

        int n = fieldData.count;

        _matrices = new Matrix4x4[n];
        _runtimeRotations = new Quaternion[n];

        for (int i = 0; i < n; i++)
        {
            Vector3 pos = fieldData.positions[i];
            Quaternion rot = fieldData.rotations[i];
            float s = fieldData.scales[i];

            // Sanitize rotation
            if (!IsFinite(rot) ||
                (rot.x == 0f && rot.y == 0f && rot.z == 0f && rot.w == 0f))
            {
                rot = Quaternion.identity;
            }
            else
            {
                rot = Quaternion.Normalize(rot);
            }

            _runtimeRotations[i] = rot;
            _matrices[i] = Matrix4x4.TRS(pos, rot, Vector3.one * s);
        }

        int typeCount = (typeRenders != null) ? typeRenders.Length : 0;
        if (typeCount <= 0) typeCount = 15;

        _buckets = new List<Matrix4x4>[typeCount, 3];
        for (int t = 0; t < typeCount; t++)
        {
            for (int l = 0; l < 3; l++)
                _buckets[t, l] = new List<Matrix4x4>(256);
        }

        _initialized = true;
    }

    private void ApplyRotationDrift(AsteroidFieldData fieldData, float dt)
    {
        // Rebuild matrices if rotation drift changes rotations
        // NOTE: This is O(N) and fine for hundreds/thousands. For 50k+, we'd move this to a cheaper path.
        int n = fieldData.count;

        var ang = fieldData.angularVelocityDeg;
        if (ang == null || ang.Length != n)
            return;

        for (int i = 0; i < n; i++)
        {
            Vector3 av = ang[i]; // degrees/sec per axis
            if (av.sqrMagnitude < 0.000001f)
                continue;

            Quaternion delta = Quaternion.Euler(av * dt);
            Quaternion q = _runtimeRotations[i] * delta;

            // normalize + recover if it ever goes weird
            if (!IsFinite(q) || (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f))
                q = Quaternion.identity;
            else
                q = Quaternion.Normalize(q);

            _runtimeRotations[i] = q;
            _matrices[i] = Matrix4x4.TRS(fieldData.positions[i], q, Vector3.one * fieldData.scales[i]);
        }
    }

    private void Render(AsteroidFieldData fieldData, Camera cam)
    {
        // Clear buckets without reallocating
        int typeCount = _buckets.GetLength(0);
        for (int t = 0; t < typeCount; t++)
        {
            for (int l = 0; l < 3; l++)
                _buckets[t, l].Clear();
        }

        Vector3 camPos = cam.transform.position;
        float d0 = Mathf.Max(0f, lod0Distance);
        float d1 = Mathf.Max(d0, lod1Distance);

        int n = fieldData.count;
        var typeIds = fieldData.typeIds;

        // Bucketize
        for (int i = 0; i < n; i++)
        {
            int typeId = (typeIds != null && i < typeIds.Length) ? typeIds[i] : 0;
            if (typeId < 0 || typeId >= typeCount)
                continue;

            // Skip invalid type entries (prevents errors / wasted work)
            var tr = typeRenders != null && typeId < typeRenders.Length ? typeRenders[typeId] : null;
            if (tr == null || !tr.IsValid)
                continue;

            Vector3 pos = fieldData.positions[i];
            float dist = Vector3.Distance(camPos, pos);

            int lod = (dist < d0) ? 0 : (dist < d1 ? 1 : 2);
            _buckets[typeId, lod].Add(_matrices[i]);
        }

        // Draw each bucket
        for (int typeId = 0; typeId < typeCount; typeId++)
        {
            var tr = typeRenders != null && typeId < typeRenders.Length ? typeRenders[typeId] : null;
            if (tr == null || !tr.IsValid)
                continue;

            DrawBucket(tr.lod0Mesh, tr.material, _buckets[typeId, 0]);
            DrawBucket(tr.lod1Mesh, tr.material, _buckets[typeId, 1]);
            DrawBucket(tr.lod2Mesh, tr.material, _buckets[typeId, 2]);
        }
    }

    private void DrawBucket(Mesh mesh, Material mat, List<Matrix4x4> matrices)
    {
        int count = matrices.Count;
        if (count <= 0)
            return;

        // Chunk because DrawMeshInstanced caps at 1023
        int offset = 0;
        while (offset < count)
        {
            int batchCount = Mathf.Min(MaxInstancesPerCall, count - offset);

            // Copy into a temporary array without allocations:
            // We can reuse a static buffer sized 1023.
            // BUT Unity requires an array, so we do a shared static.
            Matrix4x4[] buffer = MatrixBufferCache.Get(batchCount);
            matrices.CopyTo(offset, buffer, 0, batchCount);

            Graphics.DrawMeshInstanced(
                mesh,
                submeshIndex: 0,
                material: mat,
                matrices: buffer,
                count: batchCount,
                properties: null,
                castShadows: shadowCasting,
                receiveShadows: receiveShadows,
                layer: renderLayer,
                camera: null, // null = render for all cameras; you can set to cam if you want strict control
                lightProbeUsage: LightProbeUsage.Off,
                lightProbeProxyVolume: null
            );

            offset += batchCount;
        }
    }

    /// <summary>
    /// Shared per-frame buffers to avoid allocating Matrix4x4[1023] repeatedly.
    /// </summary>
    private static class MatrixBufferCache
    {
        private static Matrix4x4[] _buffer1023;

        public static Matrix4x4[] Get(int needed)
        {
            // Always return the same big buffer; caller uses first "needed" entries.
            if (_buffer1023 == null || _buffer1023.Length != MaxInstancesPerCall)
                _buffer1023 = new Matrix4x4[MaxInstancesPerCall];

            return _buffer1023;
        }
    }

    private static bool IsFinite(Quaternion q)
    {
        return float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w);
    }
}
