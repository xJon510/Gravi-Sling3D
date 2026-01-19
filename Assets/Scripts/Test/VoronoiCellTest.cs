using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Simple 3D Voronoi test:
/// - Generates random "sites" in a box volume (deterministic via seed)
/// - Instantiates primitive spheres at those sites
/// - Draws Gizmos that approximate Voronoi regions via coarse sampling
///   (either ownership voxels and/or boundary points).
/// </summary>
[ExecuteAlways]
public class VoronoiCellTest : MonoBehaviour
{
    [Header("Volume")]
    public Vector3 boxSize = new Vector3(200f, 200f, 200f);
    public bool boxCenteredOnTransform = true;

    [Header("Sites")]
    [Min(1)] public int siteCount = 20;
    public int seed = 12345;
    public float sitePadding = 5f;            // keep sites away from the box walls
    public float sphereRadius = 2.5f;
    public bool regenerateInEditMode = false; // flip to true if you want live updates while tweaking

    [Header("Gizmos Sampling")]
    [Tooltip("Distance between sample points (smaller = more detail, slower).")]
    public float sampleStep = 8f;

    [Header("Boundary Visualization")]
    [Tooltip("Draws points where nearest and 2nd-nearest sites are almost tied.")]
    public bool drawBoundaries = true;
    [Tooltip("Threshold for boundary detection (smaller = thinner borders).")]
    public float boundaryEpsilon = 1.5f;
    public float boundaryPointSize = 0.6f;

    [Header("Ownership Voxels (Optional)")]
    [Tooltip("Draws semi-transparent cubes showing which site owns each sample point. Can be heavy.")]
    public bool drawOwnershipVoxels = false;
    [Range(0.01f, 1f)] public float ownershipAlpha = 0.12f;

    [Header("Debug")]
    public bool drawSiteLabels = false;

    // Internal
    [SerializeField, HideInInspector] private List<Vector3> _sites = new List<Vector3>();
    private readonly List<GameObject> _spawned = new List<GameObject>();
    private int _lastHash;

    private void OnEnable()
    {
        EnsureGenerated(force: true);
    }

    private void OnDisable()
    {
        // In edit mode, clean up spawned preview objects if desired.
        CleanupSpawned();
    }

    private void Update()
    {
        if (!Application.isPlaying && regenerateInEditMode)
            EnsureGenerated(force: false);
    }

    private void EnsureGenerated(bool force)
    {
        int hash = ComputeSettingsHash();
        if (!force && hash == _lastHash) return;

        _lastHash = hash;
        GenerateSites();
        RespawnPrimitives();
    }

    private int ComputeSettingsHash()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + boxSize.GetHashCode();
            h = h * 31 + boxCenteredOnTransform.GetHashCode();
            h = h * 31 + siteCount;
            h = h * 31 + seed;
            h = h * 31 + sitePadding.GetHashCode();
            h = h * 31 + sphereRadius.GetHashCode();
            return h;
        }
    }

    private void GenerateSites()
    {
        _sites.Clear();

        var rng = new System.Random(seed);

        Vector3 half = boxSize * 0.5f;
        Vector3 min = -half + Vector3.one * sitePadding;
        Vector3 max = half - Vector3.one * sitePadding;

        for (int i = 0; i < siteCount; i++)
        {
            float x = Lerp(min.x, max.x, (float)rng.NextDouble());
            float y = Lerp(min.y, max.y, (float)rng.NextDouble());
            float z = Lerp(min.z, max.z, (float)rng.NextDouble());

            Vector3 local = new Vector3(x, y, z);
            Vector3 world = boxCenteredOnTransform ? transform.TransformPoint(local) : local;

            _sites.Add(world);
        }
    }

    private void RespawnPrimitives()
    {
        CleanupSpawned();

        // Spawn spheres as children for easy cleanup.
        for (int i = 0; i < _sites.Count; i++)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"VoronoiSite_{i:00}";
            go.transform.SetParent(transform, worldPositionStays: true);
            go.transform.position = _sites[i];
            go.transform.localScale = Vector3.one * (sphereRadius * 2f);

            // Make collider less annoying in scene view
            var col = go.GetComponent<Collider>();
            if (col) col.enabled = false;

            // Give each sphere a unique-ish color (editor only)
#if UNITY_EDITOR
            var mr = go.GetComponent<MeshRenderer>();
            if (mr)
            {
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                Color c = SiteColor(i);
                mat.color = c;
                mr.sharedMaterial = mat;
            }
#endif
            _spawned.Add(go);
        }
    }

    private void CleanupSpawned()
    {
        for (int i = _spawned.Count - 1; i >= 0; i--)
        {
            var go = _spawned[i];
            if (!go) continue;

#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(go);
            else
                Destroy(go);
#else
            Destroy(go);
#endif
        }
        _spawned.Clear();
    }

    private void OnDrawGizmos()
    {
        EnsureGenerated(force: false);

        DrawBoxGizmo();

        if (_sites == null || _sites.Count == 0)
            return;

        // Draw sites as gizmos
        for (int i = 0; i < _sites.Count; i++)
        {
            Gizmos.color = SiteColor(i);
            Gizmos.DrawWireSphere(_sites[i], sphereRadius * 1.2f);

#if UNITY_EDITOR
            if (drawSiteLabels)
            {
                Handles.color = Gizmos.color;
                Handles.Label(_sites[i] + Vector3.up * (sphereRadius * 1.6f), $"#{i}");
            }
#endif
        }

        // Sample grid for boundaries / ownership
        float step = Mathf.Max(0.5f, sampleStep);
        Vector3 half = boxSize * 0.5f;

        // Sample in LOCAL box space, then convert to world if needed
        Vector3 localMin = -half;
        Vector3 localMax = half;

        // To keep gizmos sane, cap sample counts.
        int nx = Mathf.CeilToInt((localMax.x - localMin.x) / step);
        int ny = Mathf.CeilToInt((localMax.y - localMin.y) / step);
        int nz = Mathf.CeilToInt((localMax.z - localMin.z) / step);

        int total = nx * ny * nz;
        const int hardCap = 250_000;
        if (total > hardCap)
            return; // too dense; reduce sampleStep

        for (int iz = 0; iz < nz; iz++)
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                {
                    float x = localMin.x + (ix + 0.5f) * step;
                    float y = localMin.y + (iy + 0.5f) * step;
                    float z = localMin.z + (iz + 0.5f) * step;

                    Vector3 localP = new Vector3(x, y, z);
                    Vector3 worldP = boxCenteredOnTransform ? transform.TransformPoint(localP) : localP;

                    // Find nearest + second nearest site (squared distances)
                    FindNearestTwo(worldP, out int i0, out float d0, out int i1, out float d1);

                    if (drawOwnershipVoxels)
                    {
                        Color c = SiteColor(i0);
                        c.a = ownershipAlpha;
                        Gizmos.color = c;
                        Gizmos.DrawCube(worldP, Vector3.one * (step * 0.95f));
                    }

                    if (drawBoundaries)
                    {
                        // boundary if closest and second closest are almost tied
                        // Use sqrt distances for epsilon in world units.
                        float diff = Mathf.Abs(Mathf.Sqrt(d1) - Mathf.Sqrt(d0));
                        if (diff < boundaryEpsilon)
                        {
                            // Color boundary by mix of the two regions
                            Color c0 = SiteColor(i0);
                            Color c1 = SiteColor(i1);
                            Gizmos.color = Color.Lerp(c0, c1, 0.5f);
                            Gizmos.DrawCube(worldP, Vector3.one * boundaryPointSize);
                        }
                    }
                }
    }

    private void DrawBoxGizmo()
    {
        Gizmos.color = new Color(1f, 1f, 1f, 0.35f);

        if (boxCenteredOnTransform)
        {
            Matrix4x4 old = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, boxSize);
            Gizmos.matrix = old;
        }
        else
        {
            Gizmos.DrawWireCube(Vector3.zero, boxSize);
        }
    }

    private void FindNearestTwo(Vector3 p, out int i0, out float d0, out int i1, out float d1)
    {
        i0 = -1; i1 = -1;
        d0 = float.PositiveInfinity;
        d1 = float.PositiveInfinity;

        for (int i = 0; i < _sites.Count; i++)
        {
            float d = (p - _sites[i]).sqrMagnitude;

            if (d < d0)
            {
                d1 = d0; i1 = i0;
                d0 = d; i0 = i;
            }
            else if (d < d1)
            {
                d1 = d; i1 = i;
            }
        }

        if (i1 < 0) { i1 = i0; d1 = d0; }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static Color SiteColor(int i)
    {
        // Stable-ish palette using HSV
        float h = Mathf.Repeat(i * 0.1618f, 1f); // golden-ish step
        return Color.HSVToRGB(h, 0.75f, 1f);
    }
}
