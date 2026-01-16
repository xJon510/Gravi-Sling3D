using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime collision detector for instanced asteroids.
/// Reads AsteroidFieldData (positions/scales/baseRadii) and resolves collisions
/// against a player BoxCollider / Rigidbody without spawning asteroid GameObjects.
/// </summary>
public class AsteroidFieldCollisionDetector : MonoBehaviour
{
    [Header("References")]
    public AsteroidFieldData fieldData;

    [Tooltip("Player Rigidbody (likely the same child RB used by SimpleMove).")]
    public Rigidbody playerRb;

    [Tooltip("Player BoxCollider used for collisions.")]
    public BoxCollider playerBox;

    [Header("Grid")]
    [Tooltip("Cell size for spatial hashing. Start with 20.")]
    public float cellSize = 20f;

    [Tooltip("How many neighbor cells to scan in each axis (1 = 3x3x3 = 27 cells).")]
    [Range(0, 3)] public int neighborRadius = 1;

    [Header("Collision Response")]
    [Tooltip("Extra separation added when resolving (prevents re-penetration jitter).")]
    public float separationSlop = 0.02f;

    [Tooltip("How much velocity to remove along the collision normal (0..1).")]
    [Range(0f, 1f)] public float normalDamp = 0.65f;

    [Tooltip("If true, also damp tangential velocity a bit (space 'scrape' feel).")]
    [Range(0f, 1f)] public float tangentDamp = 0.05f;

    [Tooltip("Max number of collisions resolved per FixedUpdate (safety cap).")]
    public int maxResolvesPerStep = 6;

    [Header("Smash Settings")]
    public float smashSpeedThreshold = 14f;
    public float smashMinAlignment = 0.65f;
    public float smashForwardNudge = 0.25f;
    public float extraSpeedFactor = 0.75f;         // extraSpeedFactor: how much harder side hits are vs head-on.
                                                   // 0.0 = angle doesn't matter, 1.0 = side hits need +100% speed, etc.

    [Tooltip("Renderer used to hide smashed instances (instanced rendering 'deletion').")]
    public AsteroidFieldInstancedRenderer instancedRenderer;

    [Tooltip("Place Asteroid VFX Particle Systems when asteroids are destroyed")]
    public AsteroidVFXPoolManager smashVfxPool;

    // Spatial hash: cellKey -> indices in that cell
    private Dictionary<long, List<int>> _cellToIndices;

    // Cache
    private float[] _radii;
    private float _maxRadius;
    private Vector3 _gridOrigin;

    // Track destroyed indices so we stop resolving them
    private BitArray _destroyed;

    private void Awake()
    {
        if (!playerRb)
            playerRb = GetComponentInChildren<Rigidbody>();

        if (!playerBox)
            playerBox = GetComponentInChildren<BoxCollider>();

        BuildIndex();
    }

    private void OnEnable()
    {
        // If you regenerate assets in editor play mode, you can toggle this component to rebuild.
        if (_cellToIndices == null || _cellToIndices.Count == 0)
            BuildIndex();
    }

    private void BuildIndex()
    {
        if (fieldData == null || fieldData.count <= 0 || fieldData.positions == null)
        {
            _cellToIndices = null;
            _radii = null;
            _maxRadius = 0f;
            return;
        }

        int n = fieldData.count;
        _destroyed = new BitArray(n, false);
        cellSize = Mathf.Max(0.0001f, cellSize);

        // Use field bounds min as a stable origin (matches how your generator defines volume bounds)
        _gridOrigin = fieldData.fieldCenter - fieldData.fieldSize * 0.5f;

        _cellToIndices = new Dictionary<long, List<int>>(Mathf.Max(16, n / 8));
        _radii = new float[n];
        _maxRadius = 0f;

        // Precompute per-instance radius (baseRadius * scale)
        for (int i = 0; i < n; i++)
        {
            float baseR = 1f;
            if (fieldData.baseRadii != null && i < fieldData.baseRadii.Length)
                baseR = Mathf.Max(0.0001f, fieldData.baseRadii[i]);

            float s = (fieldData.scales != null && i < fieldData.scales.Length) ? fieldData.scales[i] : 1f;
            float r = baseR * s;

            _radii[i] = r;
            if (r > _maxRadius) _maxRadius = r;

            long key = ComputeCellKey(fieldData.positions[i]);

            if (!_cellToIndices.TryGetValue(key, out var list))
            {
                list = new List<int>(8);
                _cellToIndices.Add(key, list);
            }
            list.Add(i);
        }
    }
    public void Rebuild()
    {
        BuildIndex();
    }
    private void FixedUpdate()
    {
        if (fieldData == null || fieldData.count <= 0) return;
        if (playerRb == null || playerBox == null) return;
        if (_cellToIndices == null) return;

        // World-space AABB of the player box
        Bounds b = playerBox.bounds;

        // Expand by max asteroid radius so we catch spheres whose centers are outside the box but still overlapping
        float expand = _maxRadius + separationSlop;
        Vector3 min = b.min - new Vector3(expand, expand, expand);
        Vector3 max = b.max + new Vector3(expand, expand, expand);

        // Determine cell range overlapped by this expanded AABB
        Vector3Int cmin = WorldToCell(min);
        Vector3Int cmax = WorldToCell(max);

        int resolves = 0;

        for (int cx = cmin.x - neighborRadius; cx <= cmax.x + neighborRadius; cx++)
            for (int cy = cmin.y - neighborRadius; cy <= cmax.y + neighborRadius; cy++)
                for (int cz = cmin.z - neighborRadius; cz <= cmax.z + neighborRadius; cz++)
                {
                    long key = PackCell(cx, cy, cz);
                    if (!_cellToIndices.TryGetValue(key, out var list))
                        continue;

                    for (int li = 0; li < list.Count; li++)
                    {
                        int i = list[li];

                        if (_destroyed != null && _destroyed[i])
                            continue;

                        Vector3 sphereCenter = fieldData.positions[i];
                        float sphereRadius = _radii[i];

                        if (SphereIntersectsAABB(sphereCenter, sphereRadius, b, out Vector3 pushNormal, out float pushDist))
                        {
                            if (TrySmash(i, pushNormal))
                                continue;

                            Resolve(pushNormal, pushDist);

                            resolves++;
                            if (resolves >= maxResolvesPerStep)
                                return;
                        }
                    }
                }
    }

    private void Resolve(Vector3 normal, float pushDist)
    {
        // Push RB out
        Vector3 pos = playerRb.position;
        pos += normal * (pushDist + separationSlop);
        playerRb.MovePosition(pos);

        // Dampen velocity
        Vector3 v = playerRb.linearVelocity; // matches your SimpleMove usage :contentReference[oaicite:1]{index=1}
        float vn = Vector3.Dot(v, normal);

        // Remove some inward normal component
        if (vn < 0f)
        {
            Vector3 vN = vn * normal;
            Vector3 vT = v - vN;

            vN *= (1f - normalDamp);
            vT *= (1f - tangentDamp);

            playerRb.linearVelocity = vT + vN;
        }
    }

    // --- Sphere vs AABB test ---
    private static bool SphereIntersectsAABB(Vector3 c, float r, Bounds aabb, out Vector3 normal, out float pushDist)
    {
        // Closest point on AABB to sphere center
        Vector3 p = aabb.ClosestPoint(c);
        Vector3 d = p - c;
        float distSq = d.sqrMagnitude;

        if (distSq > r * r)
        {
            normal = Vector3.up;
            pushDist = 0f;
            return false;
        }

        float dist = Mathf.Sqrt(Mathf.Max(distSq, 1e-12f));

        // If center is inside the box or extremely close, pick a stable normal:
        if (dist < 1e-6f)
        {
            Vector3 toCenter = c - aabb.center;
            if (toCenter.sqrMagnitude < 1e-8f) toCenter = Vector3.up;
            normal = toCenter.normalized;
            pushDist = r;
            return true;
        }

        normal = d / dist;
        pushDist = r - dist;
        return true;
    }

    // --- Grid hashing ---
    private Vector3Int WorldToCell(Vector3 p)
    {
        int cx = Mathf.FloorToInt((p.x - _gridOrigin.x) / cellSize);
        int cy = Mathf.FloorToInt((p.y - _gridOrigin.y) / cellSize);
        int cz = Mathf.FloorToInt((p.z - _gridOrigin.z) / cellSize);
        return new Vector3Int(cx, cy, cz);
    }

    private long ComputeCellKey(Vector3 p)
    {
        Vector3Int c = WorldToCell(p);
        return PackCell(c.x, c.y, c.z);
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
    private void OnDrawGizmosSelected()
    {
        DrawDebugGizmos();
    }

    private void DrawDebugGizmos()
    {
        if (fieldData == null || playerBox == null)
            return;

        // Build caches if needed (OnDrawGizmosSelected can run before Awake/OnEnable).
        if (_cellToIndices == null || _radii == null || _radii.Length != fieldData.count)
            BuildIndex();

        if (_cellToIndices == null || _radii == null)
            return;

        Bounds b = playerBox.bounds;

        float expand = _maxRadius + separationSlop;
        Vector3 min = b.min - Vector3.one * expand;
        Vector3 max = b.max + Vector3.one * expand;

        Vector3Int cmin = WorldToCell(min);
        Vector3Int cmax = WorldToCell(max);

        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.35f);

        for (int cx = cmin.x - neighborRadius; cx <= cmax.x + neighborRadius; cx++)
            for (int cy = cmin.y - neighborRadius; cy <= cmax.y + neighborRadius; cy++)
                for (int cz = cmin.z - neighborRadius; cz <= cmax.z + neighborRadius; cz++)
                {
                    long key = PackCell(cx, cy, cz);
                    if (!_cellToIndices.TryGetValue(key, out var list))
                        continue;

                    foreach (int i in list)
                    {
                        Vector3 center = fieldData.positions[i];
                        float r = _radii[i];
                        Gizmos.DrawWireSphere(center, r);
                    }
                }

        // Draw player AABB
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(b.center, b.size);
    }
    private bool TrySmash(int index, Vector3 pushNormal)
    {
        if (smashSpeedThreshold <= 0f) return false;

        Vector3 v = playerRb.linearVelocity;
        float speed = v.magnitude;
        if (speed < smashSpeedThreshold) return false;

        // pushNormal points ~ from asteroid -> player.
        // Head-on hit means velocity points into asteroid along -pushNormal.
        float align = 0f;
        if (speed > 0.0001f)
            align = Vector3.Dot(v / speed, -pushNormal);   // -1..1, usually 0..1 for contact

        align = Mathf.Clamp01((align + 1f) * 0.5f); // map -1..1 to 0..1

        // Required speed goes up as align goes down.
        float requiredSpeed = smashSpeedThreshold * (1f + (1f - align) * extraSpeedFactor);

        if (speed < requiredSpeed)
            return false;

        Vector3 vel = playerRb.linearVelocity;

        // Fallback-safe velocity direction
        Vector3 velDir = (vel.sqrMagnitude > 1e-6f)
            ? vel.normalized
            : (-pushNormal);

        // PushNormal points asteroid -> player, so invert for "away from impact"
        Vector3 awayFromImpact = -pushNormal;

        // Blend: mostly velocity, slightly surface response
        Vector3 smashDir = Vector3.Normalize(
            velDir * 0.85f +
            awayFromImpact * 0.15f
        );

        // Scalar strength (tune this)            // How Much Speed Gets Applied - Min - Max
        float vfxSpeed = Mathf.Clamp(vel.magnitude * 0.8f, 8f, 400f);

        DestroyAsteroid(index, smashDir, vfxSpeed);

        // small nudge forward so we don't remain overlapping for a frame
        if (smashForwardNudge > 0f)
        {
            Vector3 dir = (speed > 0.0001f) ? (v / speed) : (-pushNormal);
            playerRb.MovePosition(playerRb.position + dir * smashForwardNudge);
        }

        return true;
    }
    private void DestroyAsteroid(int index, Vector3 smashDir, float vfxSpeed)
    {
        if (_destroyed != null && index >= 0 && index < _destroyed.Length)
            _destroyed[index] = true;

        if (smashVfxPool != null)
        {
            // Example tuning knobs
            float t = Mathf.InverseLerp(smashSpeedThreshold, smashSpeedThreshold * 2f, playerRb.linearVelocity.magnitude);
            float dirSpeed = vfxSpeed;                                  // from your velocity-based scalar
            float radialSpeed = Mathf.Lerp(6f, 50f, t);                // explosion strength
            float randomSpeed = Mathf.Lerp(1f, 20f, t);                  // chaos
            int count = Mathf.RoundToInt(Mathf.Lerp(20f, 60f, t));      // particles

            Vector3 asteroidPos = fieldData.positions[index];
            Vector3 hitPos = playerBox.bounds.ClosestPoint(asteroidPos);

            smashVfxPool.SpawnImpact(hitPos, smashDir, dirSpeed, radialSpeed, randomSpeed, count);
        }

        // Hide from instanced renderer (visual deletion)
        if (instancedRenderer != null)
            instancedRenderer.SetInstanceHidden(fieldData, index, true);
    }
}
