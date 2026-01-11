using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class AsteroidFieldGeneratorWindow : EditorWindow
{
    [Serializable]
    public class PrefabEntry
    {
        public GameObject prefab;
        [Min(0f)] public float weight = 1f;

        [Tooltip("Approx radius (in meters) when scale = 1. Used if we can't read renderer bounds.")]
        [Min(0.0001f)] public float baseRadius = 1f;
    }

    [Header("Prefabs")]
    [SerializeField] private List<PrefabEntry> prefabs = new List<PrefabEntry>();

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

    [Header("Parent / Cleanup")]
    [SerializeField] private Transform parent;
    [SerializeField] private string generatedRootName = "AsteroidField_Generated";

    [Header("Seed")]
    [SerializeField] private bool useFixedSeed = false;
    [SerializeField] private int seed = 12345;

    // Cached spheres for overlap checks
    private struct PlacedSphere
    {
        public Vector3 center;
        public float radius;
    }
    private readonly List<PlacedSphere> placed = new List<PlacedSphere>();

    [MenuItem("Tools/Asteroids/Asteroid Field Generator")]
    public static void Open()
    {
        GetWindow<AsteroidFieldGeneratorWindow>("Asteroid Field");
    }

    private void OnGUI()
    {
        SerializedObject so = new SerializedObject(this);
        so.Update();

        EditorGUILayout.LabelField("Prefabs", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("prefabs"), true);

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Field Volume", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("fieldCenter"));
        EditorGUILayout.PropertyField(so.FindProperty("fieldSize"));

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Placement", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("count"));
        EditorGUILayout.PropertyField(so.FindProperty("separationPadding"));
        EditorGUILayout.PropertyField(so.FindProperty("maxAttemptsPerAsteroid"));

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Randomization", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("uniformScaleRange"));
        EditorGUILayout.PropertyField(so.FindProperty("randomRotation"));

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Parent / Seed", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("parent"));
        EditorGUILayout.PropertyField(so.FindProperty("generatedRootName"));
        EditorGUILayout.PropertyField(so.FindProperty("useFixedSeed"));
        if (useFixedSeed) EditorGUILayout.PropertyField(so.FindProperty("seed"));

        so.ApplyModifiedProperties();

        EditorGUILayout.Space(12);

        using (new EditorGUI.DisabledScope(!CanGenerate()))
        {
            if (GUILayout.Button("Generate Field", GUILayout.Height(32)))
                Generate();
        }

        if (GUILayout.Button("Clear Generated Root"))
            ClearGeneratedRoot();

        EditorGUILayout.HelpBox(
            "Overlap is prevented by sphere-sphere checks using either renderer bounds (if available) or baseRadius * scale.\n" +
            "If placement fails often, increase field size, lower count, reduce scale range, or raise maxAttempts.",
            MessageType.Info
        );
    }

    private bool CanGenerate()
    {
        if (prefabs == null || prefabs.Count == 0) return false;
        float totalWeight = 0f;
        foreach (var p in prefabs)
        {
            if (p != null && p.prefab != null && p.weight > 0f) totalWeight += p.weight;
        }
        return totalWeight > 0f && count > 0 && fieldSize.x > 0 && fieldSize.y > 0 && fieldSize.z > 0;
    }

    private void Generate()
    {
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Generate Asteroid Field");

        System.Random rng = useFixedSeed ? new System.Random(seed) : new System.Random(Guid.NewGuid().GetHashCode());

        Transform root = EnsureRoot();

        placed.Clear();

        int placedCount = 0;
        int failedCount = 0;

        for (int i = 0; i < count; i++)
        {
            bool success = TryPlaceOne(rng, root, out GameObject created);
            if (success)
            {
                placedCount++;
            }
            else
            {
                failedCount++;
                // If you want: break early if failing too much
            }
        }

        Undo.CollapseUndoOperations(undoGroup);

        Debug.Log($"Asteroid Field Generator: placed {placedCount}/{count}. Failed: {failedCount}.");
        Selection.activeTransform = root;
    }

    private Transform EnsureRoot()
    {
        if (parent != null) return parent;

        // Find or create a root in the scene
        GameObject existing = GameObject.Find(generatedRootName);
        if (existing != null) return existing.transform;

        GameObject root = new GameObject(generatedRootName);
        Undo.RegisterCreatedObjectUndo(root, "Create Asteroid Root");
        root.transform.position = Vector3.zero;
        return root.transform;
    }

    private void ClearGeneratedRoot()
    {
        GameObject existing = GameObject.Find(generatedRootName);
        if (existing == null)
        {
            Debug.Log("No generated root found to clear.");
            return;
        }
        Undo.DestroyObjectImmediate(existing);
    }

    private bool TryPlaceOne(System.Random rng, Transform root, out GameObject created)
    {
        created = null;

        PrefabEntry entry = PickWeightedPrefab(rng);
        if (entry == null || entry.prefab == null) return false;

        for (int attempt = 0; attempt < Mathf.Max(1, maxAttemptsPerAsteroid); attempt++)
        {
            Vector3 pos = RandomPointInBox(rng, fieldCenter, fieldSize);
            float scale = RandomRange(rng, uniformScaleRange.x, uniformScaleRange.y);
            Quaternion rot = randomRotation ? UnityEngine.Random.rotation : Quaternion.identity; // uses Unity RNG for rotation

            float radius = EstimateRadius(entry.prefab, entry.baseRadius) * scale;

            if (IsOverlapping(pos, radius))
                continue;

            // Place
            GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(entry.prefab);
            if (go == null) return false;

            Undo.RegisterCreatedObjectUndo(go, "Create Asteroid");

            go.transform.SetParent(root, true);
            go.transform.position = pos;
            go.transform.rotation = rot;
            go.transform.localScale = Vector3.one * scale;

            // Cache sphere for next placements
            placed.Add(new PlacedSphere { center = pos, radius = radius });

            created = go;
            return true;
        }

        return false;
    }

    private bool IsOverlapping(Vector3 candidateCenter, float candidateRadius)
    {
        float pad = Mathf.Max(0f, separationPadding);
        for (int i = 0; i < placed.Count; i++)
        {
            float minDist = placed[i].radius + candidateRadius + pad;
            float sqrMinDist = minDist * minDist;
            if ((placed[i].center - candidateCenter).sqrMagnitude < sqrMinDist)
                return true;
        }
        return false;
    }

    private PrefabEntry PickWeightedPrefab(System.Random rng)
    {
        float total = 0f;
        foreach (var p in prefabs)
        {
            if (p == null || p.prefab == null || p.weight <= 0f) continue;
            total += p.weight;
        }
        if (total <= 0f) return null;

        float roll = (float)(rng.NextDouble() * total);
        float accum = 0f;

        foreach (var p in prefabs)
        {
            if (p == null || p.prefab == null || p.weight <= 0f) continue;
            accum += p.weight;
            if (roll <= accum) return p;
        }
        return prefabs[prefabs.Count - 1];
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

    private static float EstimateRadius(GameObject prefab, float fallbackBaseRadius)
    {
        // Try to estimate from renderer bounds (in prefab asset)
        // Note: bounds on prefab assets can be tricky depending on import/state.
        // This is a best-effort; fallback is used otherwise.
        try
        {
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            if (renderers != null && renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                    b.Encapsulate(renderers[i].bounds);

                // sphere radius ~ half of the max dimension
                float r = 0.5f * Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                if (r > 0.0001f) return r;
            }
        }
        catch { /* ignore */ }

        return Mathf.Max(0.0001f, fallbackBaseRadius);
    }

    private void OnDrawGizmosSelected()
    {
        // This won't draw unless the window is active in some contexts.
        // Optional: move gizmo drawing to a separate MonoBehaviour if you want always-on.
        Gizmos.matrix = Matrix4x4.identity;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(fieldCenter, fieldSize);
    }
}
