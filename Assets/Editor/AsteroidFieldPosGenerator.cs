using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Step 1 of GPU-instancing pipeline:
/// Generates and stores asteroid instance data (position/rotation/scale/typeId/spin) into a ScriptableObject.
/// No prefabs. No GameObjects. Just data.
/// </summary>

#if UNITY_EDITOR
public class AsteroidFieldPosGenerator : EditorWindow
{
    [Serializable]
    public class TypeEntry
    {
        [Tooltip("Optional name for readability (TypeId is the array index).")]
        public string name = "AsteroidType";

        [Min(0f)] public float weight = 1f;

        [Tooltip("Approx radius (meters) when scale = 1. Used for overlap checks and later culling/LOD heuristics.")]
        [Min(0.0001f)] public float baseRadius = 1f;
    }

    // ---- Config (mirrors your current generator style) ----

    [Header("Output")]
    [SerializeField] private AsteroidFieldData outputAsset;

    [Header("Asteroid Types (weighted)")]
    [SerializeField] private List<TypeEntry> types = new List<TypeEntry>();

    [Header("Field Volume (Box)")]
    [SerializeField] private Vector3 fieldCenter = Vector3.zero;
    [SerializeField] private Vector3 fieldSize = new Vector3(200f, 200f, 200f);

    [Header("Count / Placement")]
    [SerializeField] private int count = 250;
    [SerializeField] private float separationPadding = 0.25f;
    [SerializeField] private int maxAttemptsPerAsteroid = 50;

    [Header("Random Scale")]
    [SerializeField] private Vector2 uniformScaleRange = new Vector2(0.6f, 2.5f);

    [Header("Random Rotation")]
    [SerializeField] private bool randomRotation = true;

    [Header("Rotation Drift (no position drift)")]
    [Tooltip("Random angular velocity in degrees/sec. X/Y/Z each randomized within this range (signed).")]
    [SerializeField] private Vector2 angularSpeedRangeDeg = new Vector2(0f, 25f);

    [Header("Spatial Index Bake (Grid)")]
    [SerializeField] private bool bakeSpatialIndex = true;
    [SerializeField, Min(0.0001f)] private float gridCellSize = 20f;

    [Header("Seed")]
    [SerializeField] private bool useFixedSeed = false;
    [SerializeField] private int seed = 12345;

    // Cached spheres for overlap checks
    private struct PlacedSphere
    {
        public Vector3 center;
        public float radius;
    }

    private readonly List<PlacedSphere> placed = new List<PlacedSphere>(4096);

    [MenuItem("Tools/Asteroids/Asteroid Field Pos Generator")]
    public static void Open()
    {
        GetWindow<AsteroidFieldPosGenerator>("Asteroid Field (Data)");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        outputAsset = (AsteroidFieldData)EditorGUILayout.ObjectField("Output Asset", outputAsset, typeof(AsteroidFieldData), false);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Create New Data Asset", GUILayout.Height(22)))
                CreateNewAsset();
            using (new EditorGUI.DisabledScope(outputAsset == null))
            {
                if (GUILayout.Button("Select Asset", GUILayout.Height(22)))
                    Selection.activeObject = outputAsset;
            }
        }

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Asteroid Types", EditorStyles.boldLabel);

        SerializedObject so = new SerializedObject(this);
        so.Update();
        EditorGUILayout.PropertyField(so.FindProperty("types"), true);
        so.ApplyModifiedProperties();

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Field Volume", EditorStyles.boldLabel);
        fieldCenter = EditorGUILayout.Vector3Field("Center", fieldCenter);
        fieldSize = EditorGUILayout.Vector3Field("Size", fieldSize);

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Placement", EditorStyles.boldLabel);
        count = EditorGUILayout.IntField("Count", count);
        separationPadding = EditorGUILayout.FloatField("Separation Padding", separationPadding);
        maxAttemptsPerAsteroid = EditorGUILayout.IntField("Max Attempts / Asteroid", maxAttemptsPerAsteroid);

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Randomization", EditorStyles.boldLabel);
        uniformScaleRange = EditorGUILayout.Vector2Field("Uniform Scale Range", uniformScaleRange);
        randomRotation = EditorGUILayout.Toggle("Random Rotation", randomRotation);

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Rotation Drift", EditorStyles.boldLabel);
        angularSpeedRangeDeg = EditorGUILayout.Vector2Field("Angular Speed Range (deg/s)", angularSpeedRangeDeg);

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Spatial Index Bake", EditorStyles.boldLabel);
        bakeSpatialIndex = EditorGUILayout.Toggle("Bake Grid Index", bakeSpatialIndex);
        using (new EditorGUI.DisabledScope(!bakeSpatialIndex))
            gridCellSize = EditorGUILayout.FloatField("Grid Cell Size", gridCellSize);

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Seed", EditorStyles.boldLabel);
        useFixedSeed = EditorGUILayout.Toggle("Use Fixed Seed", useFixedSeed);
        using (new EditorGUI.DisabledScope(!useFixedSeed))
            seed = EditorGUILayout.IntField("Seed", seed);

        EditorGUILayout.Space(12);

        using (new EditorGUI.DisabledScope(!CanGenerate()))
        {
            if (GUILayout.Button("Generate & Save To Asset", GUILayout.Height(34)))
                GenerateToAsset();
        }

        using (new EditorGUI.DisabledScope(outputAsset == null))
        {
            if (GUILayout.Button("Clear Asset Data"))
            {
                Undo.RecordObject(outputAsset, "Clear Asteroid Field Data");
                outputAsset.Clear();
                EditorUtility.SetDirty(outputAsset);
                AssetDatabase.SaveAssets();
            }
        }

        EditorGUILayout.HelpBox(
            "This generates asteroid instance DATA only (TRS + typeId + angular drift).\n" +
            "No GameObjects are instantiated. Later, a runtime instanced renderer will draw meshes at these transforms.\n\n" +
            "Overlap is prevented via sphere-sphere checks using (type.baseRadius * scale) + padding.",
            MessageType.Info
        );
    }

    private bool CanGenerate()
    {
        if (outputAsset == null) return false;
        if (types == null || types.Count == 0) return false;

        float totalWeight = 0f;
        foreach (var t in types)
        {
            if (t == null) continue;
            if (t.weight > 0f) totalWeight += t.weight;
        }

        if (totalWeight <= 0f) return false;
        if (count <= 0) return false;
        if (fieldSize.x <= 0f || fieldSize.y <= 0f || fieldSize.z <= 0f) return false;
        return true;
    }

    private void CreateNewAsset()
    {
        string path = EditorUtility.SaveFilePanelInProject(
            "Create Asteroid Field Data Asset",
            "AsteroidFieldData",
            "asset",
            "Choose where to save the AsteroidFieldData asset."
        );

        if (string.IsNullOrEmpty(path))
            return;

        var asset = ScriptableObject.CreateInstance<AsteroidFieldData>();
        AssetDatabase.CreateAsset(asset, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        outputAsset = asset;
        Selection.activeObject = outputAsset;
        EditorGUIUtility.PingObject(outputAsset);
    }

    private void GenerateToAsset()
    {
        // Deterministic RNG for everything (avoid mixing UnityEngine.Random here)
        System.Random rng = useFixedSeed ? new System.Random(seed) : new System.Random(Guid.NewGuid().GetHashCode());

        placed.Clear();

        // Preallocate instance buffers
        var positions = new Vector3[count];
        var rotations = new Quaternion[count];
        var scales = new float[count];
        var typeIds = new int[count];
        var angularVel = new Vector3[count];
        var baseRadii = new float[count];

        int placedCount = 0;
        int failedCount = 0;

        int attemptsCap = Mathf.Max(1, maxAttemptsPerAsteroid);

        for (int i = 0; i < count; i++)
        {
            bool success = TryPlaceOne(rng, attemptsCap,
                out Vector3 pos,
                out Quaternion rot,
                out float scale,
                out int typeId,
                out Vector3 angVelDeg,
                out float baseRadius);

            if (!success)
            {
                failedCount++;
                continue;
            }

            positions[placedCount] = pos;
            rotations[placedCount] = rot;
            scales[placedCount] = scale;
            typeIds[placedCount] = typeId;
            angularVel[placedCount] = angVelDeg;
            baseRadii[placedCount] = baseRadius;

            placedCount++;
        }

        // Shrink arrays to actual placed count
        if (placedCount != count)
        {
            Array.Resize(ref positions, placedCount);
            Array.Resize(ref rotations, placedCount);
            Array.Resize(ref scales, placedCount);
            Array.Resize(ref typeIds, placedCount);
            Array.Resize(ref angularVel, placedCount);
            Array.Resize(ref baseRadii, placedCount);
        }

        Vector3 gridOrigin = fieldCenter - fieldSize * 0.5f;

        long[] cellKeys = Array.Empty<long>();
        int[] cellStarts = Array.Empty<int>();
        int[] cellIndices = Array.Empty<int>();

        if (bakeSpatialIndex && placedCount > 0)
        {
            BuildSpatialIndex(
                positions,
                gridOrigin,
                Mathf.Max(0.0001f, gridCellSize),
                out cellKeys,
                out cellStarts,
                out cellIndices
            );
        }

        Undo.RecordObject(outputAsset, "Generate Asteroid Field Data");

        outputAsset.fieldCenter = fieldCenter;
        outputAsset.fieldSize = fieldSize;
        outputAsset.useFixedSeed = useFixedSeed;
        outputAsset.seed = seed;

        outputAsset.count = placedCount;
        outputAsset.positions = positions;
        outputAsset.rotations = rotations;
        outputAsset.scales = scales;
        outputAsset.typeIds = typeIds;
        outputAsset.angularVelocityDeg = angularVel;
        outputAsset.baseRadii = baseRadii;

        outputAsset.cellSize = Mathf.Max(0.0001f, gridCellSize);
        outputAsset.gridOrigin = gridOrigin;
        outputAsset.cellKeys = cellKeys;
        outputAsset.cellStarts = cellStarts;
        outputAsset.cellIndices = cellIndices;

        EditorUtility.SetDirty(outputAsset);
        AssetDatabase.SaveAssets();

        Debug.Log($"Asteroid Field Data: placed {placedCount}/{count}. Failed: {failedCount}. Asset: {outputAsset.name}");
        Selection.activeObject = outputAsset;
        EditorGUIUtility.PingObject(outputAsset);
    }

    private bool TryPlaceOne(
        System.Random rng,
        int attemptsCap,
        out Vector3 pos,
        out Quaternion rot,
        out float scale,
        out int typeId,
        out Vector3 angVelDeg,
        out float baseRadius)
    {
        pos = default;
        rot = Quaternion.identity;
        scale = 1f;
        typeId = 0;
        angVelDeg = Vector3.zero;
        baseRadius = 1f;

        typeId = PickWeightedTypeIndex(rng);
        if (typeId < 0 || typeId >= types.Count) return false;

        var entry = types[typeId];
        if (entry == null) return false;

        for (int attempt = 0; attempt < attemptsCap; attempt++)
        {
            pos = RandomPointInBox(rng, fieldCenter, fieldSize);
            scale = RandomRange(rng, uniformScaleRange.x, uniformScaleRange.y);

            rot = randomRotation ? RandomRotation(rng) : Quaternion.identity;

            baseRadius = Mathf.Max(0.0001f, entry.baseRadius);
            float radius = baseRadius * scale;

            if (IsOverlapping(pos, radius))
                continue;

            // Rotation drift only (no position drift)
            float min = angularSpeedRangeDeg.x;
            float max = angularSpeedRangeDeg.y;
            if (max < min) (min, max) = (max, min);

            // Random signed angular speed per axis
            angVelDeg = new Vector3(
                RandomSignedRange(rng, min, max),
                RandomSignedRange(rng, min, max),
                RandomSignedRange(rng, min, max)
            );

            placed.Add(new PlacedSphere { center = pos, radius = radius });
            return true;
        }

        return false;
    }

    private bool IsOverlapping(Vector3 candidateCenter, float candidateRadius)
    {
        float pad = Mathf.Max(0f, separationPadding);
        float cand = candidateRadius;

        for (int i = 0; i < placed.Count; i++)
        {
            float minDist = placed[i].radius + cand + pad;
            float sqrMinDist = minDist * minDist;
            if ((placed[i].center - candidateCenter).sqrMagnitude < sqrMinDist)
                return true;
        }
        return false;
    }

    private int PickWeightedTypeIndex(System.Random rng)
    {
        float total = 0f;
        for (int i = 0; i < types.Count; i++)
        {
            var t = types[i];
            if (t == null || t.weight <= 0f) continue;
            total += t.weight;
        }
        if (total <= 0f) return -1;

        float roll = (float)(rng.NextDouble() * total);
        float accum = 0f;

        for (int i = 0; i < types.Count; i++)
        {
            var t = types[i];
            if (t == null || t.weight <= 0f) continue;
            accum += t.weight;
            if (roll <= accum) return i;
        }

        // Fallback: last valid
        for (int i = types.Count - 1; i >= 0; i--)
        {
            var t = types[i];
            if (t != null && t.weight > 0f) return i;
        }
        return -1;
    }

    private static Vector3 RandomPointInBox(System.Random rng, Vector3 center, Vector3 size)
    {
        Vector3 half = size * 0.5f;
        float x = RandomRange(rng, center.x - half.x, center.x + half.x);
        float y = RandomRange(rng, center.y - half.y, center.y + half.y);
        float z = RandomRange(rng, center.z - half.z, center.z + half.z);
        return new Vector3(x, y, z);
    }

    private static float RandomRange(System.Random rng, float min, float max)
    {
        if (max < min) (min, max) = (max, min);
        return (float)(min + (max - min) * rng.NextDouble());
    }

    private static float RandomSignedRange(System.Random rng, float minAbs, float maxAbs)
    {
        // Picks magnitude in [minAbs, maxAbs], then random sign.
        float mag = RandomRange(rng, minAbs, maxAbs);
        return (rng.NextDouble() < 0.5) ? -mag : mag;
    }

    private static Quaternion RandomRotation(System.Random rng)
    {
        // Uniform-ish random rotation without touching UnityEngine.Random
        // Method: random unit quaternion
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

    private static void BuildSpatialIndex(
    Vector3[] positions,
    Vector3 origin,
    float cellSize,
    out long[] outKeys,
    out int[] outStarts,
    out int[] outIndices)
    {
        // cellKey -> indices
        var map = new Dictionary<long, List<int>>(Mathf.Max(16, positions.Length / 8));

        for (int i = 0; i < positions.Length; i++)
        {
            long key = ComputeCellKey(positions[i], origin, cellSize);
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<int>(8);
                map.Add(key, list);
            }
            list.Add(i);
        }

        // Sort keys for binary search at runtime
        outKeys = new long[map.Count];
        map.Keys.CopyTo(outKeys, 0);
        Array.Sort(outKeys);

        // Build CSR layout: starts + flat indices
        outStarts = new int[outKeys.Length + 1];

        int total = 0;
        for (int k = 0; k < outKeys.Length; k++)
        {
            outStarts[k] = total;
            total += map[outKeys[k]].Count;
        }
        outStarts[outKeys.Length] = total;

        outIndices = new int[total];

        int write = 0;
        for (int k = 0; k < outKeys.Length; k++)
        {
            var list = map[outKeys[k]];
            for (int j = 0; j < list.Count; j++)
                outIndices[write++] = list[j];
        }
    }

    private static long ComputeCellKey(Vector3 pos, Vector3 origin, float cellSize)
    {
        int cx = Mathf.FloorToInt((pos.x - origin.x) / cellSize);
        int cy = Mathf.FloorToInt((pos.y - origin.y) / cellSize);
        int cz = Mathf.FloorToInt((pos.z - origin.z) / cellSize);
        return PackCell(cx, cy, cz);
    }

    // Packs 3 signed ints into one long (21 bits each). Supports coords roughly [-1,048,576 .. +1,048,575].
    private const int CELL_BITS = 21;
    private const int CELL_BIAS = 1 << (CELL_BITS - 1); // 1,048,576
    private const long CELL_MASK = (1L << CELL_BITS) - 1L;

    private static long PackCell(int x, int y, int z)
    {
        long lx = ((long)(x + CELL_BIAS)) & CELL_MASK;
        long ly = ((long)(y + CELL_BIAS)) & CELL_MASK;
        long lz = ((long)(z + CELL_BIAS)) & CELL_MASK;

        return lx | (ly << CELL_BITS) | (lz << (CELL_BITS * 2));
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.matrix = Matrix4x4.identity;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(fieldCenter, fieldSize);
    }
}
#endif
