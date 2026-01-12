using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime-safe generator that fills an AsteroidFieldData instance with instance buffers,
/// using the same logic as AsteroidFieldPosGenerator (but without any UnityEditor stuff).
/// </summary>
public static class AsteroidFieldRuntimeGenerator
{
    [Serializable]
    public class TypeEntry
    {
        public string name = "AsteroidType";
        [Min(0f)] public float weight = 1f;
        [Min(0.0001f)] public float baseRadius = 1f;
    }

    [Serializable]
    public class Settings
    {
        [Header("Asteroid Types (weighted)")]
        public List<TypeEntry> types = new List<TypeEntry>();

        [Header("Count / Placement")]
        public int count = 250;
        public float separationPadding = 0.25f;
        public int maxAttemptsPerAsteroid = 50;

        [Header("Random Scale")]
        public Vector2 uniformScaleRange = new Vector2(0.6f, 2.5f);

        [Header("Random Rotation")]
        public bool randomRotation = true;

        [Header("Rotation Drift (no position drift)")]
        public Vector2 angularSpeedRangeDeg = new Vector2(0f, 25f);

        [Header("Spatial Index Bake (Grid)")]
        public bool bakeSpatialIndex = true;
        [Min(0.0001f)] public float gridCellSize = 20f;

        [Header("Placement (Grid-accelerated)")]
        public bool useGridPlacement = true;

        // If <= 0, defaults to gridCellSize.
        [Min(0.0f)] public float placementCellSize = 0f;

        // How many random candidate points we try inside a chosen cell each time.
        [Min(1)] public int candidatesPerCell = 6;

        // Max asteroids allowed in a single cell (prevents “cell clumps” + grid artifacts).
        [Min(1)] public int maxPerCell = 2;

        // How many random cell-picks we attempt to hit 'count' before giving up.
        // (Higher = more likely to fill dense fields; lower = faster)
        [Min(1)] public int maxCellPicks = 5000;
    }

    private struct PlacedSphere
    {
        public Vector3 center;
        public float radius;
    }

    public static bool CanGenerate(Settings s)
    {
        if (s == null) return false;
        if (s.types == null || s.types.Count == 0) return false;
        if (s.count <= 0) return false;

        float totalWeight = 0f;
        foreach (var t in s.types)
            if (t != null && t.weight > 0f) totalWeight += t.weight;

        return totalWeight > 0f;
    }

    public static AsteroidFieldData GenerateChunk(
        Settings settings,
        Vector3 chunkOrigin,
        float chunkSize,
        int seed)
    {
        if (!CanGenerate(settings))
            throw new InvalidOperationException("AsteroidFieldRuntimeGenerator: invalid settings (types/weights/count).");

        // Create in-memory ScriptableObject (works in builds)
        var data = ScriptableObject.CreateInstance<AsteroidFieldData>();
        data.Clear(); // uses your built-in Clear() :contentReference[oaicite:2]{index=2}

        Vector3 fieldSize = new Vector3(chunkSize, chunkSize, chunkSize);
        Vector3 fieldCenter = chunkOrigin + fieldSize * 0.5f;

        data.fieldCenter = fieldCenter;
        data.fieldSize = fieldSize;
        data.useFixedSeed = true;
        data.seed = seed;

        var rng = new System.Random(seed);

        // Preallocate instance buffers
        var positions = new Vector3[settings.count];
        var rotations = new Quaternion[settings.count];
        var scales = new float[settings.count];
        var typeIds = new int[settings.count];
        var angularVel = new Vector3[settings.count];
        var baseRadii = new float[settings.count];

        int placedCount = 0;

        float cellSize = settings.placementCellSize > 0f
            ? settings.placementCellSize
            : Mathf.Max(0.0001f, settings.gridCellSize);

        float maxPossibleRadius = ComputeMaxPossibleRadius(settings);
        Vector3 gridOrigin = fieldCenter - fieldSize * 0.5f; // == chunkOrigin

        var placed = new List<PlacedSphere>(Mathf.Max(128, settings.count));
        var hash = new PlacementHash(Mathf.Max(32, settings.count / 2));

        if (settings.useGridPlacement)
        {
            placedCount = PlaceUsingGridJitter(
                rng, settings,
                gridOrigin, fieldCenter, fieldSize,
                cellSize, maxPossibleRadius,
                positions, rotations, scales, typeIds, angularVel, baseRadii,
                placed, hash
            );
        }
        else
        {
            // fallback to your original approach (keeps functionality / regression safety)
            int attemptsCap = Mathf.Max(1, settings.maxAttemptsPerAsteroid);

            for (int i = 0; i < settings.count; i++)
            {
                if (!TryPlaceOne(rng, settings, fieldCenter, fieldSize, placed, attemptsCap,
                        out Vector3 pos, out Quaternion rot, out float scale,
                        out int typeId, out Vector3 angVelDeg, out float baseRadius))
                    continue;

                positions[placedCount] = pos;
                rotations[placedCount] = rot;
                scales[placedCount] = scale;
                typeIds[placedCount] = typeId;
                angularVel[placedCount] = angVelDeg;
                baseRadii[placedCount] = baseRadius;
                placedCount++;
            }
        }

        // Shrink arrays to actual placed count
        if (placedCount != settings.count)
        {
            Array.Resize(ref positions, placedCount);
            Array.Resize(ref rotations, placedCount);
            Array.Resize(ref scales, placedCount);
            Array.Resize(ref typeIds, placedCount);
            Array.Resize(ref angularVel, placedCount);
            Array.Resize(ref baseRadii, placedCount);
        }

        // Spatial index bake (matches your asset fields: cellKeys/cellStarts/cellIndices) :contentReference[oaicite:3]{index=3}
        long[] cellKeys = Array.Empty<long>();
        int[] cellStarts = Array.Empty<int>();
        int[] cellIndices = Array.Empty<int>();

        if (settings.bakeSpatialIndex && placedCount > 0)
        {
            BuildSpatialIndex(
                positions,
                gridOrigin,
                Mathf.Max(0.0001f, settings.gridCellSize),
                out cellKeys,
                out cellStarts,
                out cellIndices);
        }

        data.count = placedCount;
        data.positions = positions;
        data.rotations = rotations;
        data.scales = scales;
        data.typeIds = typeIds;
        data.angularVelocityDeg = angularVel;
        data.baseRadii = baseRadii;

        data.cellSize = Mathf.Max(0.0001f, settings.gridCellSize);
        data.gridOrigin = gridOrigin;
        data.cellKeys = cellKeys;
        data.cellStarts = cellStarts;
        data.cellIndices = cellIndices;

        return data;
    }

    private static bool TryPlaceOne(
        System.Random rng,
        Settings s,
        Vector3 fieldCenter,
        Vector3 fieldSize,
        List<PlacedSphere> placed,
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

        typeId = PickWeightedTypeIndex(rng, s.types);
        if (typeId < 0 || typeId >= s.types.Count) return false;

        var entry = s.types[typeId];
        if (entry == null) return false;

        for (int attempt = 0; attempt < attemptsCap; attempt++)
        {
            pos = RandomPointInBox(rng, fieldCenter, fieldSize);
            scale = RandomRange(rng, s.uniformScaleRange.x, s.uniformScaleRange.y);
            rot = s.randomRotation ? RandomRotation(rng) : Quaternion.identity;

            baseRadius = Mathf.Max(0.0001f, entry.baseRadius);
            float radius = baseRadius * scale;

            if (IsOverlapping(pos, radius, placed, s.separationPadding))
                continue;

            float min = s.angularSpeedRangeDeg.x;
            float max = s.angularSpeedRangeDeg.y;
            if (max < min) (min, max) = (max, min);

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

    private static bool IsOverlapping(Vector3 candidateCenter, float candidateRadius, List<PlacedSphere> placed, float padding)
    {
        float pad = Mathf.Max(0f, padding);
        for (int i = 0; i < placed.Count; i++)
        {
            float minDist = placed[i].radius + candidateRadius + pad;
            if ((placed[i].center - candidateCenter).sqrMagnitude < minDist * minDist)
                return true;
        }
        return false;
    }

    private static int PickWeightedTypeIndex(System.Random rng, List<TypeEntry> types)
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

        for (int i = types.Count - 1; i >= 0; i--)
            if (types[i] != null && types[i].weight > 0f) return i;

        return -1;
    }

    private static Vector3 RandomPointInBox(System.Random rng, Vector3 center, Vector3 size)
    {
        Vector3 half = size * 0.5f;
        return new Vector3(
            RandomRange(rng, center.x - half.x, center.x + half.x),
            RandomRange(rng, center.y - half.y, center.y + half.y),
            RandomRange(rng, center.z - half.z, center.z + half.z)
        );
    }

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

    // ---- Spatial index (same packing + CSR layout as your editor tool) :contentReference[oaicite:4]{index=4}

    private static void BuildSpatialIndex(
        Vector3[] positions,
        Vector3 origin,
        float cellSize,
        out long[] outKeys,
        out int[] outStarts,
        out int[] outIndices)
    {
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

        outKeys = new long[map.Count];
        map.Keys.CopyTo(outKeys, 0);
        Array.Sort(outKeys);

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

    private const int CELL_BITS = 21;
    private const int CELL_BIAS = 1 << (CELL_BITS - 1);
    private const long CELL_MASK = (1L << CELL_BITS) - 1L;

    private static long PackCell(int x, int y, int z)
    {
        long lx = ((long)(x + CELL_BIAS)) & CELL_MASK;
        long ly = ((long)(y + CELL_BIAS)) & CELL_MASK;
        long lz = ((long)(z + CELL_BIAS)) & CELL_MASK;

        return lx | (ly << CELL_BITS) | (lz << (CELL_BITS * 2));
    }
    public static void FillExistingChunk(
    AsteroidFieldData data,
    Settings settings,
    Vector3 chunkOrigin,
    float chunkSize,
    int seed)
    {
        if (!CanGenerate(settings))
            throw new InvalidOperationException("AsteroidFieldRuntimeGenerator: invalid settings.");

        data.Clear();

        Vector3 fieldSize = new Vector3(chunkSize, chunkSize, chunkSize);
        Vector3 fieldCenter = chunkOrigin + fieldSize * 0.5f;

        data.fieldCenter = fieldCenter;
        data.fieldSize = fieldSize;
        data.useFixedSeed = true;
        data.seed = seed;

        var rng = new System.Random(seed);

        var positions = new Vector3[settings.count];
        var rotations = new Quaternion[settings.count];
        var scales = new float[settings.count];
        var typeIds = new int[settings.count];
        var angularVel = new Vector3[settings.count];
        var baseRadii = new float[settings.count];

        int placedCount = 0;

        float cellSize = settings.placementCellSize > 0f
            ? settings.placementCellSize
            : Mathf.Max(0.0001f, settings.gridCellSize);

        float maxPossibleRadius = ComputeMaxPossibleRadius(settings);
        Vector3 gridOrigin = fieldCenter - fieldSize * 0.5f;

        var placed = new List<PlacedSphere>(Mathf.Max(128, settings.count));
        var hash = new PlacementHash(Mathf.Max(32, settings.count / 2));

        if (settings.useGridPlacement)
        {
            placedCount = PlaceUsingGridJitter(
                rng, settings,
                gridOrigin, fieldCenter, fieldSize,
                cellSize, maxPossibleRadius,
                positions, rotations, scales, typeIds, angularVel, baseRadii,
                placed, hash
            );
        }
        else
        {
            int attemptsCap = Mathf.Max(1, settings.maxAttemptsPerAsteroid);

            for (int i = 0; i < settings.count; i++)
            {
                if (!TryPlaceOne(rng, settings, fieldCenter, fieldSize, placed, attemptsCap,
                        out Vector3 pos, out Quaternion rot, out float scale,
                        out int typeId, out Vector3 angVelDeg, out float baseRadius))
                    continue;

                positions[placedCount] = pos;
                rotations[placedCount] = rot;
                scales[placedCount] = scale;
                typeIds[placedCount] = typeId;
                angularVel[placedCount] = angVelDeg;
                baseRadii[placedCount] = baseRadius;
                placedCount++;
            }
        }

        if (placedCount != settings.count)
        {
            Array.Resize(ref positions, placedCount);
            Array.Resize(ref rotations, placedCount);
            Array.Resize(ref scales, placedCount);
            Array.Resize(ref typeIds, placedCount);
            Array.Resize(ref angularVel, placedCount);
            Array.Resize(ref baseRadii, placedCount);
        }

        long[] cellKeys = Array.Empty<long>();
        int[] cellStarts = Array.Empty<int>();
        int[] cellIndices = Array.Empty<int>();

        if (settings.bakeSpatialIndex && placedCount > 0)
        {
            BuildSpatialIndex(
                positions,
                gridOrigin,
                Mathf.Max(0.0001f, settings.gridCellSize),
                out cellKeys,
                out cellStarts,
                out cellIndices);
        }

        data.count = placedCount;
        data.positions = positions;
        data.rotations = rotations;
        data.scales = scales;
        data.typeIds = typeIds;
        data.angularVelocityDeg = angularVel;
        data.baseRadii = baseRadii;

        data.cellSize = Mathf.Max(0.0001f, settings.gridCellSize);
        data.gridOrigin = gridOrigin;
        data.cellKeys = cellKeys;
        data.cellStarts = cellStarts;
        data.cellIndices = cellIndices;
    }
    private sealed class PlacementHash
    {
        public readonly Dictionary<long, List<int>> cellToIndices;
        public readonly Dictionary<long, int> cellCounts;

        public PlacementHash(int capacityHint)
        {
            cellToIndices = new Dictionary<long, List<int>>(capacityHint);
            cellCounts = new Dictionary<long, int>(capacityHint);
        }

        public int GetCellCount(long key)
        {
            return cellCounts.TryGetValue(key, out int c) ? c : 0;
        }

        public void IncrementCellCount(long key)
        {
            cellCounts.TryGetValue(key, out int c);
            cellCounts[key] = c + 1;
        }

        public void AddIndex(long key, int index)
        {
            if (!cellToIndices.TryGetValue(key, out var list))
            {
                list = new List<int>(4);
                cellToIndices.Add(key, list);
            }
            list.Add(index);
        }
    }

    private static float ComputeMaxPossibleRadius(Settings s)
    {
        float maxBase = 0f;
        for (int i = 0; i < s.types.Count; i++)
        {
            var t = s.types[i];
            if (t == null) continue;
            maxBase = Mathf.Max(maxBase, Mathf.Max(0.0001f, t.baseRadius));
        }

        float maxScale = Mathf.Max(s.uniformScaleRange.x, s.uniformScaleRange.y);
        return maxBase * maxScale;
    }

    private static int PlaceUsingGridJitter(
    System.Random rng,
    Settings s,
    Vector3 gridOrigin,
    Vector3 fieldCenter,
    Vector3 fieldSize,
    float cellSize,
    float maxPossibleRadius,
    Vector3[] positions,
    Quaternion[] rotations,
    float[] scales,
    int[] typeIds,
    Vector3[] angularVel,
    float[] baseRadii,
    List<PlacedSphere> placed,
    PlacementHash hash)
    {
        int target = s.count;
        int placedCount = 0;

        // How many cells exist along each axis within this chunk?
        int nx = Mathf.Max(1, Mathf.CeilToInt(fieldSize.x / cellSize));
        int ny = Mathf.Max(1, Mathf.CeilToInt(fieldSize.y / cellSize));
        int nz = Mathf.Max(1, Mathf.CeilToInt(fieldSize.z / cellSize));

        int candidatesPerCell = Mathf.Max(1, s.candidatesPerCell);
        int maxPerCell = Mathf.Max(1, s.maxPerCell);

        // Controls total time spent trying to fill the chunk.
        int maxCellPicks = Mathf.Max(1, s.maxCellPicks);

        float pad = Mathf.Max(0f, s.separationPadding);

        for (int pick = 0; pick < maxCellPicks && placedCount < target; pick++)
        {
            // Pick a random cell in the chunk (this avoids iterating 100k+ cells).
            int cx = rng.Next(0, nx);
            int cy = rng.Next(0, ny);
            int cz = rng.Next(0, nz);

            long cellKey = PackCell(cx, cy, cz);

            // Per-cell cap (prevents “one cell became a clump” artifacts).
            if (hash.GetCellCount(cellKey) >= maxPerCell)
                continue;

            // Try a handful of jittered candidates inside this cell.
            for (int c = 0; c < candidatesPerCell && placedCount < target; c++)
            {
                // Jittered point inside cell
                Vector3 pos = RandomPointInCell(rng, gridOrigin, cx, cy, cz, cellSize);

                int typeId = PickWeightedTypeIndex(rng, s.types);
                if (typeId < 0 || typeId >= s.types.Count) continue;
                var entry = s.types[typeId];
                if (entry == null) continue;

                float scale = RandomRange(rng, s.uniformScaleRange.x, s.uniformScaleRange.y);
                float baseRadius = Mathf.Max(0.0001f, entry.baseRadius);
                float radius = baseRadius * scale;

                if (IsOverlappingHashed(pos, radius, gridOrigin, cellSize, maxPossibleRadius, pad, placed, hash.cellToIndices))
                    continue;

                Quaternion rot = s.randomRotation ? RandomRotation(rng) : Quaternion.identity;

                float min = s.angularSpeedRangeDeg.x;
                float max = s.angularSpeedRangeDeg.y;
                if (max < min) (min, max) = (max, min);

                Vector3 angVelDeg = new Vector3(
                    RandomSignedRange(rng, min, max),
                    RandomSignedRange(rng, min, max),
                    RandomSignedRange(rng, min, max)
                );

                // Commit
                positions[placedCount] = pos;
                rotations[placedCount] = rot;
                scales[placedCount] = scale;
                typeIds[placedCount] = typeId;
                angularVel[placedCount] = angVelDeg;
                baseRadii[placedCount] = baseRadius;

                placed.Add(new PlacedSphere { center = pos, radius = radius });

                // Update hash
                int newIndex = placed.Count - 1;
                hash.AddIndex(cellKey, newIndex);
                hash.IncrementCellCount(cellKey);

                placedCount++;

                // If we hit per-cell cap, stop trying this cell.
                if (hash.GetCellCount(cellKey) >= maxPerCell)
                    break;
            }
        }

        return placedCount;
    }

    private static Vector3 RandomPointInCell(System.Random rng, Vector3 origin, int cx, int cy, int cz, float cellSize)
    {
        // Uniform jitter inside the cell (not center-jitter only).
        float x = (cx + (float)rng.NextDouble()) * cellSize;
        float y = (cy + (float)rng.NextDouble()) * cellSize;
        float z = (cz + (float)rng.NextDouble()) * cellSize;
        return origin + new Vector3(x, y, z);
    }

    private static bool IsOverlappingHashed(
        Vector3 candidateCenter,
        float candidateRadius,
        Vector3 origin,
        float cellSize,
        float maxPossibleRadius,
        float padding,
        List<PlacedSphere> placed,
        Dictionary<long, List<int>> cellToIndices)
    {
        // Conservative neighbor range so we never miss overlaps even with variable radii.
        float reach = candidateRadius + maxPossibleRadius + padding;
        int r = Mathf.Max(1, Mathf.CeilToInt(reach / cellSize));

        int ccx = Mathf.FloorToInt((candidateCenter.x - origin.x) / cellSize);
        int ccy = Mathf.FloorToInt((candidateCenter.y - origin.y) / cellSize);
        int ccz = Mathf.FloorToInt((candidateCenter.z - origin.z) / cellSize);

        float pad = Mathf.Max(0f, padding);

        for (int dz = -r; dz <= r; dz++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    long key = PackCell(ccx + dx, ccy + dy, ccz + dz);

                    if (!cellToIndices.TryGetValue(key, out var list))
                        continue;

                    for (int i = 0; i < list.Count; i++)
                    {
                        var s = placed[list[i]];
                        float minDist = s.radius + candidateRadius + pad;
                        if ((s.center - candidateCenter).sqrMagnitude < minDist * minDist)
                            return true;
                    }
                }

        return false;
    }


}
