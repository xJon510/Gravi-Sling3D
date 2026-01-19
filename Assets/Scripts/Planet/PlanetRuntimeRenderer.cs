using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Streams (spawns/despawns) planet prefabs around the player within a radius,
/// using deterministic PlanetSectorGenerator.GetPlanetsForChunk.
/// Uses a simple pool and an id->instance dictionary.
/// </summary>
public class PlanetRuntimeRenderer : MonoBehaviour
{
    [Header("References")]
    public Transform player;
    public GameObject planetPrefab;

    [Header("World / Generation")]
    public int globalSeed = 12345;
    public float planetCellSize = 3000f;
    public float planetAvoidRadius = 200f;
    [Range(0f, 1f)] public float planetSpawnChance = 0.35f;

    [Header("Streaming")]
    [Min(50f)] public float renderRadius = 2500f;
    [Tooltip("How often we refresh visible planets (seconds).")]
    [Min(0.05f)] public float refreshInterval = 0.35f;
    public bool worldSpace = true; // true = use world coords, false = local to this object

    [Header("Pooling")]
    [Min(0)] public int prewarmCount = 16;

    [Header("Debug")]
    public bool drawDebugSphere = true;

    private readonly List<PlanetSectorGenerator.PlanetNode> _tmpPlanets = new List<PlanetSectorGenerator.PlanetNode>(64);
    private readonly Dictionary<int, GameObject> _active = new Dictionary<int, GameObject>(128);
    private readonly HashSet<int> _visibleIds = new HashSet<int>();
    private readonly Queue<GameObject> _pool = new Queue<GameObject>(128);

    private readonly Dictionary<int, GameObject> _activeById = new Dictionary<int, GameObject>(128);
    private readonly Dictionary<GameObject, int> _idByActive = new Dictionary<GameObject, int>(128);
    private readonly List<int> _tmpRemoveIds = new List<int>(256);
    private readonly List<PlanetSectorGenerator.PlanetNode> _desired = new List<PlanetSectorGenerator.PlanetNode>(128);


    private float _nextRefreshTime;

    private void Awake()
    {
        if (!player) player = Camera.main ? Camera.main.transform : transform;

        if (prewarmCount > 0 && planetPrefab)
        {
            for (int i = 0; i < prewarmCount; i++)
            {
                var go = CreatePooled();
                ReturnToPool(go);
            }
        }
    }

    private void Update()
    {
        if (!player || !planetPrefab) return;

        if (Time.time >= _nextRefreshTime)
        {
            _nextRefreshTime = Time.time + refreshInterval;
            Refresh();
        }
    }

    private void Refresh()
    {
        Vector3 center = player.position;

        float querySize = renderRadius * 2f;
        Vector3 queryOrigin = center - Vector3.one * renderRadius;

        PlanetSectorGenerator.GetPlanetsForChunk(
            globalSeed,
            queryOrigin,
            querySize,
            planetCellSize,
            planetAvoidRadius,
            planetSpawnChance,
            _tmpPlanets
        );

        float r2 = renderRadius * renderRadius;

        // Build desired list (sphere filtered)
        _desired.Clear();
        for (int i = 0; i < _tmpPlanets.Count; i++)
        {
            var p = _tmpPlanets[i];
            if ((p.position - center).sqrMagnitude <= r2)
                _desired.Add(p);
        }

        // 1) Despawn anything no longer desired
        _visibleIds.Clear();
        for (int i = 0; i < _desired.Count; i++)
            _visibleIds.Add(_desired[i].id);

        _tmpRemoveIds.Clear();
        foreach (var kv in _activeById)
        {
            if (!_visibleIds.Contains(kv.Key))
                _tmpRemoveIds.Add(kv.Key);
        }

        for (int i = 0; i < _tmpRemoveIds.Count; i++)
        {
            int id = _tmpRemoveIds[i];
            if (_activeById.TryGetValue(id, out var go) && go)
            {
                _activeById.Remove(id);
                _idByActive.Remove(go);
                ReturnToPool(go);
            }
            else
            {
                _activeById.Remove(id);
            }
        }

        // 2) Spawn / update desired planets
        for (int i = 0; i < _desired.Count; i++)
        {
            var p = _desired[i];

            if (_activeById.TryGetValue(p.id, out var go) && go)
            {
                // Already active: just update transform
                go.transform.position = p.position;
                if (!go.activeSelf) go.SetActive(true);
                continue;
            }

            // Need a new active instance (from pool)
            go = GetFromPool();
            go.name = $"Planet_{p.id}";
            go.transform.SetParent(worldSpace ? null : transform, worldPositionStays: true);
            go.transform.position = p.position;
            if (!go.activeSelf) go.SetActive(true);

            _activeById[p.id] = go;
            _idByActive[go] = p.id;
        }
    }


    // --- Pooling ---

    private GameObject GetFromPool()
    {
        while (_pool.Count > 0)
        {
            var go = _pool.Dequeue();
            if (go) return go;
        }
        return CreatePooled();
    }

    private GameObject CreatePooled()
    {
        var go = Instantiate(planetPrefab);
        go.SetActive(false);
        return go;
    }

    private void ReturnToPool(GameObject go)
    {
        if (!go) return;
        go.SetActive(false);
        // Keep hierarchy tidy
        if (!worldSpace) go.transform.SetParent(transform, worldPositionStays: true);
        _pool.Enqueue(go);
    }

    // temp list to avoid alloc
    private readonly List<int> _tmpKeys = new List<int>(256);

    private void OnDrawGizmosSelected()
    {
        if (!drawDebugSphere || !player) return;
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.25f);
        Gizmos.DrawWireSphere(player.position, renderRadius);
    }
    public bool TryGetPlanetInstance(int planetId, out GameObject planetGo)
    {
        return _activeById.TryGetValue(planetId, out planetGo) && planetGo != null;
    }
}
