using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Simple pool for asteroid smash VFX (ParticleSystem prefabs).
/// Spawns VFX at a position/rotation and auto-returns when finished.
/// </summary>
public class AsteroidVFXPoolManager : MonoBehaviour
{
    [Header("Prefab")]
    [Tooltip("Prefab with a ParticleSystem (can have children). Main PS should have Stop Action = Callback.")]
    public GameObject vfxPrefab;

    [Header("Pool Settings")]
    public int prewarmCount = 10;
    public int maxPoolSize = 20;
    public bool allowExpand = true;

    [Header("Playback")]
    public bool playOnSpawn = true;

    private readonly Queue<PooledVFX> _available = new Queue<PooledVFX>();
    private readonly HashSet<PooledVFX> _inUse = new HashSet<PooledVFX>();

    private void Awake()
    {
        if (!vfxPrefab)
        {
            Debug.LogWarning($"{nameof(AsteroidVFXPoolManager)}: No vfxPrefab assigned.", this);
            return;
        }

        Prewarm(prewarmCount);
    }

    public void Prewarm(int count)
    {
        if (!vfxPrefab) return;

        count = Mathf.Clamp(count, 0, maxPoolSize);
        while (_available.Count + _inUse.Count < count)
        {
            var inst = CreateInstance();
            if (!inst) break;
            ReturnToPool(inst);
        }
    }

    /// <summary>
    /// Spawn a pooled VFX at world position/rotation.
    /// </summary>
    public void Spawn(Vector3 position, Quaternion rotation)
    {
        var vfx = GetFromPool();
        if (!vfx) return;

        Transform t = vfx.transform;
        t.SetPositionAndRotation(position, rotation);
        t.gameObject.SetActive(true);
    }

    public void SpawnImpact(
        Vector3 position,
        Vector3 smashDirWorld,
        float dirSpeed,
        float radialSpeed,
        float randomSpeed,
        int count)
    {
        var vfx = GetFromPool();
        if (!vfx) return;

        vfx.transform.SetPositionAndRotation(position, Quaternion.identity);
        vfx.gameObject.SetActive(true);

        vfx.PlayImpactBurst(smashDirWorld, dirSpeed, radialSpeed, randomSpeed, count);
    }

    /// <summary>
    /// Convenience: spawn with identity rotation.
    /// </summary>
    public void Spawn(Vector3 position) => Spawn(position, Quaternion.identity);

    private PooledVFX GetFromPool()
    {
        // If available, use one.
        if (_available.Count > 0)
        {
            var vfx = _available.Dequeue();
            _inUse.Add(vfx);
            return vfx;
        }

        // No available: expand if allowed.
        int total = _available.Count + _inUse.Count;
        if (allowExpand && total < maxPoolSize)
        {
            var vfx = CreateInstance();
            if (!vfx) return null;
            _inUse.Add(vfx);
            return vfx;
        }

        // Pool exhausted.
        return null;
    }

    private PooledVFX CreateInstance()
    {
        GameObject go = Instantiate(vfxPrefab, transform);
        go.name = $"{vfxPrefab.name}_Pooled";

        var pooled = go.GetComponent<PooledVFX>();
        if (!pooled) pooled = go.AddComponent<PooledVFX>();

        pooled.Bind(this);

        // Start inactive
        go.SetActive(false);
        return pooled;
    }

    internal void ReturnToPool(PooledVFX vfx)
    {
        if (!vfx) return;

        // If we got an extra instance beyond max, destroy it.
        int total = _available.Count + _inUse.Count;
        if (total > maxPoolSize)
        {
            Destroy(vfx.gameObject);
            return;
        }

        _inUse.Remove(vfx);

        vfx.StopAndClear();
        vfx.transform.SetParent(transform, worldPositionStays: false);
        vfx.gameObject.SetActive(false);

        _available.Enqueue(vfx);
    }

    // NEW: same as SpawnImpact but with a color
    public void SpawnImpact(
        Vector3 position,
        Vector3 smashDirWorld,
        float dirSpeed,
        float radialSpeed,
        float randomSpeed,
        int count,
        Color tint)
    {
        var vfx = GetFromPool();
        if (!vfx) return;

        vfx.transform.SetPositionAndRotation(position, Quaternion.identity);
        vfx.gameObject.SetActive(true);

        vfx.SetTint(tint); // NEW
        vfx.PlayImpactBurst(smashDirWorld, dirSpeed, radialSpeed, randomSpeed, count);
    }

    /// <summary>
    /// Component that lives on pooled instances.
    /// </summary>
    public class PooledVFX : MonoBehaviour
    {
        private AsteroidVFXPoolManager _pool;
        private ParticleSystem[] _systems;

        public void Bind(AsteroidVFXPoolManager pool)
        {
            _pool = pool;
            _systems = GetComponentsInChildren<ParticleSystem>(includeInactive: true);

            // Optional safety: ensure main system uses Callback if possible
            // (won't override if you already set it)
            if (_systems != null && _systems.Length > 0)
            {
                var main = _systems[0].main;
                // We can't reliably detect "main" system, but the root PS is usually first.
                // Leave your prefab configured properly.
            }
        }

        public void PlayImpactBurst(
            Vector3 smashDirWorld,
            float dirSpeed,
            float radialSpeed,
            float randomSpeed,
            int count)
        {
            if (_systems == null || _systems.Length == 0) return;

            var ps = _systems[0];

            smashDirWorld = (smashDirWorld.sqrMagnitude > 1e-6f)
                ? smashDirWorld.normalized
                : Vector3.forward;

            // Reset root system (prevents stacking/duplicates)
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Clear(true);

            var emitParams = new ParticleSystem.EmitParams
            {
                applyShapeToPosition = true
            };

            for (int i = 0; i < count; i++)
            {
                Vector3 radial = Random.onUnitSphere * radialSpeed;
                Vector3 directional = smashDirWorld * dirSpeed;
                Vector3 chaos = Random.onUnitSphere * randomSpeed;

                emitParams.velocity = radial + directional + chaos;
                ps.Emit(emitParams, 1);
            }

            ps.Play(true);
        }

        public void StopAndClear()
        {
            if (_systems == null) _systems = GetComponentsInChildren<ParticleSystem>(includeInactive: true);

            for (int i = 0; i < _systems.Length; i++)
            {
                if (!_systems[i]) continue;
                _systems[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                _systems[i].Clear(true);
            }
        }

        // This fires when a ParticleSystem with "Stop Action = Callback" stops.
        private void OnParticleSystemStopped()
        {
            // In case the callback comes from a child system, only return once.
            if (_pool != null && gameObject.activeInHierarchy)
                _pool.ReturnToPool(this);
        }

        public void SetTint(Color c)
        {
            if (_systems == null) _systems = GetComponentsInChildren<ParticleSystem>(includeInactive: true);

            for (int i = 0; i < _systems.Length; i++)
            {
                if (!_systems[i]) continue;

                var main = _systems[i].main;

                // Preserve alpha from the existing startColor (optional but nice)
                var existing = main.startColor;
                Color baseCol = existing.mode == ParticleSystemGradientMode.Color
                    ? existing.color
                    : Color.white;

                c.a = baseCol.a;
                main.startColor = new ParticleSystem.MinMaxGradient(c);
            }
        }
    }
}
