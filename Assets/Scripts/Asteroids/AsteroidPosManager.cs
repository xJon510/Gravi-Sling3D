using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime chunk manager: keeps a 3x3x3 set of AsteroidFieldData chunks around the player.
/// For now: just guarantees the 27 exist and are stable. Recycling/shift logic can be added next.
/// </summary>
public class AsteroidPosManager : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Player or camera root to track.")]
    public Transform player;

    [Header("Collision")]
    public AsteroidFieldCollisionDetector collisionDetector;

    [Header("Chunk Volume")]
    [Min(1f)] public float chunkSize = 1000f; // 1k x 1k x 1k
    [Tooltip("3 means 3x3x3. Keep odd.")]
    [Min(1)] public int gridWidth = 3;

    [Header("Generation")]
    public int globalSeed = 12345;
    public AsteroidFieldRuntimeGenerator.Settings settings = new AsteroidFieldRuntimeGenerator.Settings();

    [Header("Planets (super basic)")]
    public float planetCellSize = 3000f;
    public float planetAvoidRadius = 200f;
    [Range(0f, 1f)] public float planetSpawnChance = 0.35f;

    [Header("Debug")]
    public bool generateOnStart = true;
    public bool logChunkCreates = true;

    /// <summary>Raised whenever a chunk is created (or recreated).</summary>
    public event Action<Vector3Int, AsteroidFieldData> OnChunkCreated;

    // chunkCoord -> generated data (in-memory ScriptableObject)
    private readonly Dictionary<Vector3Int, AsteroidFieldData> _chunks = new Dictionary<Vector3Int, AsteroidFieldData>(64);

    private Vector3Int _centerChunk;
    private Vector3Int _lastCenterChunk;

    private readonly List<PlanetSectorGenerator.PlanetNode> _tmpPlanets = new List<PlanetSectorGenerator.PlanetNode>(32);

    private void Awake()
    {
        if (!player) player = Camera.main ? Camera.main.transform : transform;
        gridWidth = Mathf.Max(1, gridWidth);

        if (gridWidth % 2 == 0) gridWidth += 1; // enforce odd

        if (!collisionDetector)
            collisionDetector = FindFirstObjectByType<AsteroidFieldCollisionDetector>();
    }

    private void Start()
    {
        if (generateOnStart)
            EnsureGrid();

        _lastCenterChunk = WorldToChunkCoord(player.position);
    }

    private void Update()
    {
        Vector3Int newCenter = WorldToChunkCoord(player.position);

        if (newCenter != _lastCenterChunk)
        {
            ShiftGrid(newCenter);
            _lastCenterChunk = newCenter;

            UpdateCollisionChunk(newCenter);
        }
    }

    public IReadOnlyDictionary<Vector3Int, AsteroidFieldData> Chunks => _chunks;

    private void EnsureGrid()
    {
        if (!AsteroidFieldRuntimeGenerator.CanGenerate(settings))
            return;

        Vector3 pos = player ? player.position : Vector3.zero;
        Vector3Int currentCenter = WorldToChunkCoord(pos);

        // If you want to avoid regen spam later, we’ll compare and recycle.
        _centerChunk = currentCenter;

        int half = gridWidth / 2;

        for (int dz = -half; dz <= half; dz++)
            for (int dy = -half; dy <= half; dy++)
                for (int dx = -half; dx <= half; dx++)
                {
                    Vector3Int coord = new Vector3Int(_centerChunk.x + dx, _centerChunk.y + dy, _centerChunk.z + dz);

                    if (_chunks.ContainsKey(coord))
                        continue;

                    CreateChunk(coord);
                }
    }

    private void CreateChunk(Vector3Int coord)
    {
        Vector3 chunkOrigin = ChunkCoordToWorldOrigin(coord);

        int seed = HashSeed(globalSeed, coord);

        PlanetSectorGenerator.GetPlanetsForChunk(
            globalSeed,
            chunkOrigin,
            chunkSize,
            planetCellSize,
            planetAvoidRadius,
            planetSpawnChance,
            _tmpPlanets);

        AsteroidFieldData data = AsteroidFieldRuntimeGenerator.GenerateChunk(
            settings,
            chunkOrigin,
            chunkSize,
            seed,
            _tmpPlanets);

        _chunks[coord] = data;

        if (logChunkCreates)
            Debug.Log($"[AsteroidPosManager] Created chunk {coord} origin={chunkOrigin} count={data.count}");

        OnChunkCreated?.Invoke(coord, data);
    }

    public Vector3Int WorldToChunkCoord(Vector3 worldPos)
    {
        // Floor division into chunk coords.
        int cx = Mathf.FloorToInt(worldPos.x / chunkSize);
        int cy = Mathf.FloorToInt(worldPos.y / chunkSize);
        int cz = Mathf.FloorToInt(worldPos.z / chunkSize);
        return new Vector3Int(cx, cy, cz);
    }

    public Vector3 ChunkCoordToWorldOrigin(Vector3Int coord)
    {
        return new Vector3(coord.x * chunkSize, coord.y * chunkSize, coord.z * chunkSize);
    }

    private static int HashSeed(int baseSeed, Vector3Int c)
    {
        unchecked
        {
            // Simple stable hash. Good enough for chunk seeding.
            int h = baseSeed;
            h = (h * 397) ^ c.x;
            h = (h * 397) ^ c.y;
            h = (h * 397) ^ c.z;
            return h;
        }
    }
    private HashSet<Vector3Int> ComputeDesiredCoords(Vector3Int center)
    {
        int half = gridWidth / 2;
        var set = new HashSet<Vector3Int>();

        for (int z = -half; z <= half; z++)
            for (int y = -half; y <= half; y++)
                for (int x = -half; x <= half; x++)
                    set.Add(new Vector3Int(center.x + x, center.y + y, center.z + z));

        return set;
    }
    private void ShiftGrid(Vector3Int newCenter)
    {
        var desired = ComputeDesiredCoords(newCenter);

        // Find chunks that are no longer needed
        List<Vector3Int> toRemove = new List<Vector3Int>();
        foreach (var kv in _chunks)
        {
            if (!desired.Contains(kv.Key))
                toRemove.Add(kv.Key);
        }

        // Find coords we need but don't have
        Queue<Vector3Int> toAdd = new Queue<Vector3Int>();
        foreach (var coord in desired)
        {
            if (!_chunks.ContainsKey(coord))
                toAdd.Enqueue(coord);
        }

        // Recycle
        for (int i = 0; i < toRemove.Count; i++)
        {
            if (toAdd.Count == 0)
                break;

            Vector3Int oldCoord = toRemove[i];
            Vector3Int newCoord = toAdd.Dequeue();

            AsteroidFieldData data = _chunks[oldCoord];
            _chunks.Remove(oldCoord);

            RegenerateChunk(data, newCoord);
            _chunks[newCoord] = data;
        }
    }
    private void RegenerateChunk(AsteroidFieldData data, Vector3Int coord)
    {
        Vector3 origin = ChunkCoordToWorldOrigin(coord);
        int seed = HashSeed(globalSeed, coord);

        data.Clear(); // reuse memory object

        PlanetSectorGenerator.GetPlanetsForChunk(
            globalSeed,
            origin,
            chunkSize,
            planetCellSize,
            planetAvoidRadius,
            planetSpawnChance,
            _tmpPlanets);

        AsteroidFieldRuntimeGenerator.FillExistingChunk(
            data,
            settings,
            origin,
            chunkSize,
            seed,
            _tmpPlanets);
    }
    private void UpdateCollisionChunk(Vector3Int centerChunk)
    {
        if (!collisionDetector)
            return;

        if (_chunks.TryGetValue(centerChunk, out var data))
        {
            collisionDetector.fieldData = data;
            collisionDetector.Rebuild(); // we'll add this method next
        }
    }

#if UNITY_EDITOR
    [ContextMenu("Regenerate All Chunks (Editor Only)")]
    private void RegenerateAllEditorOnly()
    {
        foreach (var kv in _chunks)
            UnityEngine.Object.DestroyImmediate(kv.Value, allowDestroyingAssets: true);

        _chunks.Clear();
        EnsureGrid();
    }
#endif
}
