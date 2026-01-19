using System;
using System.Collections.Generic;
using UnityEngine;

public class PlanetQuestNavigator : MonoBehaviour
{
    [Header("References")]
    public Transform player;
    public Camera cam;
    public PlanetRuntimeRenderer planetRenderer;

    [Tooltip("A prefab that contains a Quad (or sphere/disc) + optional Billboard script.")]
    public GameObject questBeaconPrefab;

    [Header("Quest Selection (data-only)")]
    public int globalSeed = 12345;

    [Tooltip("How far from the player we search for candidate planets to pick as targets.")]
    public float pickRadius = 6000f;

    [Tooltip("Don't pick a target planet too close to the player.")]
    public float minTargetDistance = 1500f;

    [Tooltip("Max attempts to find a valid target from generated candidates.")]
    public int pickAttempts = 6;

    [Header("Planet Generation (MUST match your planet systems)")]
    public float planetCellSize = 3000f;
    public float planetAvoidRadius = 200f;
    [Range(0f, 1f)] public float planetSpawnChance = 0.35f;

    [Header("Beacon Placement (inside far clip)")]
    [Tooltip("Fraction of camera farClipPlane to place the beacon at.")]
    [Range(0.1f, 0.95f)] public float beaconDepthFrac = 0.8f;

    [Tooltip("If your far clip is small, also clamp to this max distance.")]
    public float beaconMaxDistance = 800f;

    [Header("Arrival / Trigger")]
    [Tooltip("When target planet gets within this distance, hide the beacon and arm the planet trigger.")]
    public float armTriggerDistance = 900f;

    [Tooltip("Name of the child object on the planet prefab that has the trigger collider.")]
    public string questArrivedChildName = "QuestArrived";

    [Tooltip("Optional: only count arrival when this tag enters the trigger.")]
    public string requiredPlayerTag = "";

    [Header("Debug")]
    public bool logPicks = true;

    private GameObject _beacon;
    private int _questIndex = 0;

    private PlanetSectorGenerator.PlanetNode _target;
    private bool _hasTarget;

    private readonly List<PlanetSectorGenerator.PlanetNode> _tmpPlanets = new List<PlanetSectorGenerator.PlanetNode>(256);
    private readonly List<PlanetSectorGenerator.PlanetNode> _candidates = new List<PlanetSectorGenerator.PlanetNode>(256);

    private void Awake()
    {
        if (!player) player = Camera.main ? Camera.main.transform : transform;
        if (!cam) cam = Camera.main;
    }

    private void Start()
    {
        EnsureBeacon();
        PickNewTarget();
    }

    private void Update()
    {
        if (!_hasTarget || !player || !cam)
            return;

        // Update beacon position (always within far clip)
        UpdateBeacon();

        // If close enough to target: hide beacon, arm trigger on the actual planet instance (if spawned)
        float distToTarget = Vector3.Distance(player.position, _target.position);

        if (distToTarget <= armTriggerDistance)
        {
            if (_beacon && _beacon.activeSelf) _beacon.SetActive(false);
            TryArmArrivalTriggerOnSpawnedPlanet();
        }
        else
        {
            if (_beacon && !_beacon.activeSelf) _beacon.SetActive(true);
        }
    }

    private void EnsureBeacon()
    {
        if (_beacon) return;
        if (!questBeaconPrefab)
        {
            Debug.LogError("[PlanetQuestNavigator] questBeaconPrefab is not assigned.");
            return;
        }

        _beacon = Instantiate(questBeaconPrefab);
        _beacon.name = "QuestBeacon";
        _beacon.SetActive(true);
    }

    private void UpdateBeacon()
    {
        if (!_beacon) return;

        Vector3 from = player.position;
        Vector3 to = _target.position;

        Vector3 dir = (to - from);
        if (dir.sqrMagnitude < 0.0001f)
            dir = player.forward;

        dir.Normalize();

        float far = cam.farClipPlane;
        float d = Mathf.Min(far * beaconDepthFrac, beaconMaxDistance);

        // Place the beacon in world space along the direction to the target, but near the player/camera.
        Vector3 beaconPos = from + dir * d;
        _beacon.transform.position = beaconPos;

        // NOTE: no “HUD” rotation locking; this is world-space.
        // If your beacon has a Billboard script, it’ll face camera automatically.
    }

    public void PickNewTarget()
    {
        _questIndex++;

        // Build a cube query that contains pickRadius sphere.
        float querySize = pickRadius * 2f;
        Vector3 queryOrigin = player.position - Vector3.one * pickRadius;

        PlanetSectorGenerator.GetPlanetsForChunk(
            globalSeed,
            queryOrigin,
            querySize,
            planetCellSize,
            planetAvoidRadius,
            planetSpawnChance,
            _tmpPlanets
        );

        // Candidates within sphere and not too close.
        _candidates.Clear();
        float r2 = pickRadius * pickRadius;
        float min2 = minTargetDistance * minTargetDistance;

        Vector3 center = player.position;

        for (int i = 0; i < _tmpPlanets.Count; i++)
        {
            var p = _tmpPlanets[i];
            float d2 = (p.position - center).sqrMagnitude;
            if (d2 > r2) continue;
            if (d2 < min2) continue;

            _candidates.Add(p);
        }

        if (_candidates.Count == 0)
        {
            _hasTarget = false;
            if (logPicks) Debug.Log("[PlanetQuestNavigator] No candidate planets found. Try increasing pickRadius or spawnChance.");
            if (_beacon) _beacon.SetActive(false);
            return;
        }

        // Pick a deterministic-ish random candidate based on (seed + questIndex)
        int pickSeed = unchecked(globalSeed * 73856093 ^ _questIndex * 19349663);
        var rng = new System.Random(pickSeed);

        PlanetSectorGenerator.PlanetNode chosen = default;
        bool found = false;

        // Try a few times to avoid picking same target again, etc. (simple)
        for (int attempt = 0; attempt < Mathf.Max(1, pickAttempts); attempt++)
        {
            int idx = rng.Next(0, _candidates.Count);
            var p = _candidates[idx];

            if (_hasTarget && p.id == _target.id)
                continue;

            chosen = p;
            found = true;
            break;
        }

        if (!found)
            chosen = _candidates[rng.Next(0, _candidates.Count)];

        _target = chosen;
        _hasTarget = true;

        if (_beacon) _beacon.SetActive(true);

        if (logPicks)
            Debug.Log($"[PlanetQuestNavigator] New target planet id={_target.id} pos={_target.position}");

        // Immediately disarm any previously-armed triggers (safe)
        DisarmAllActivePlanetTriggers();
    }

    private void TryArmArrivalTriggerOnSpawnedPlanet()
    {
        if (!planetRenderer) return;

        if (!planetRenderer.TryGetPlanetInstance(_target.id, out var planetGo))
            return; // not spawned yet (should be soon)

        Transform child = planetGo.transform.Find(questArrivedChildName);
        if (!child) return;

        // Ensure it has the trigger script
        var trigger = child.GetComponent<QuestArrivedTrigger>();
        if (!trigger)
            trigger = child.gameObject.AddComponent<QuestArrivedTrigger>();

        trigger.Init(this, _target.id, requiredPlayerTag);

        // Enable the child so collider becomes active
        if (!child.gameObject.activeSelf)
            child.gameObject.SetActive(true);
    }

    private void DisarmAllActivePlanetTriggers()
    {
        if (!planetRenderer) return;

        // Simple approach: we can’t iterate renderer’s private dictionary without extra API,
        // so we just leave old triggers off by design: the trigger object should default inactive in prefab.
        // (If you want a stronger cleanup pass, we can add another public method to renderer later.)
    }

    // Called by QuestArrivedTrigger when player enters
    public void NotifyArrived(int planetId)
    {
        if (!_hasTarget || planetId != _target.id)
            return;

        if (logPicks)
            Debug.Log($"[PlanetQuestNavigator] Arrived at planet id={planetId}. Rolling new quest...");

        // Hide beacon briefly (optional)
        if (_beacon) _beacon.SetActive(false);

        PickNewTarget();
    }
}
