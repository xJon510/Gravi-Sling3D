using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// Builds a “peg tach/speedo” meter in UI by instantiating a Peg prefab.
/// - X axis = bar index across a width centered on parent.
/// - Y axis = stacked “tiers” (rows) per bar.
/// - heightCurve controls how many tiers are active per bar (shape).
/// - tierDensityCurve controls vertical step size per tier (spacing/density as you go up).
[ExecuteAlways]
public class PegMeterBuilder_UI : MonoBehaviour
{
    [Header("Build Target")]
    [Tooltip("Container to build under. If null, uses this.transform.")]
    public RectTransform parent;

    [Tooltip("Prefab for a single vertical peg. Must have a RectTransform.")]
    public RectTransform pegPrefab;

    [Header("Layout")]
    [Min(1)] public int barCount = 48;
    [Min(0.01f)] public float totalWidth = 600f;

    [Tooltip("If true, bars are laid out right-to-left.")]
    public bool reverseOrder = false;

    [Header("Tiers")]
    [Min(1)] public int tierRows = 6;

    [Tooltip("Base vertical step in pixels. This is multiplied by tierDensityCurve.")]
    [Min(0f)] public float baseTierStep = 8f;

    [Tooltip("Optional: scale each tier peg down as it goes up (300ZX-ish taper).")]
    public AnimationCurve tierScaleCurve = new AnimationCurve(
        new Keyframe(0f, 1.00f),
        new Keyframe(1f, 0.75f)
    );

    [Tooltip("Controls spacing density as tiers go up. 1 = normal, <1 = denser, >1 = more spaced.")]
    public AnimationCurve tierDensityCurve = new AnimationCurve(
        new Keyframe(0f, 1.00f),
        new Keyframe(1f, 0.60f)
    );

    [Header("Shape (per bar)")]
    [Tooltip("Controls how tall each bar is (0..1). Used to decide how many tiers are active.")]
    public AnimationCurve heightCurve = new AnimationCurve(
        new Keyframe(0.00f, 0.20f),
        new Keyframe(0.70f, 1.00f),
        new Keyframe(0.85f, 0.95f),
        new Keyframe(1.00f, 0.35f)
    );

    [Header("Global Height Mult (per bar)")]
    [Tooltip("Overall height multiplier per bar (X = bar index 0..1, Y = height mult).")]
    public AnimationCurve barHeightMultCurve = new AnimationCurve(
    new Keyframe(0f, 1f),
    new Keyframe(1f, 1f)
    );

    [Min(0f)]
    public float barHeightMult = 1f;

    public bool clampCurves01 = true;

    [Header("Rebuild Behavior")]
    [Tooltip("If true, clears children under parent before rebuilding.")]
    public bool clearBeforeBuild = true;

    [Tooltip("Name prefix so we can safely clear only what we built.")]
    public string builtPrefix = "PEG_";

    // -------------------------
    // Public API (Buttons)
    // -------------------------

    [ContextMenu("Rebuild Peg Meter")]
    public void Rebuild()
    {
        var p = parent ? parent : (RectTransform)transform;
        if (!p)
        {
            Debug.LogError("PegMeterBuilder_UI: No parent RectTransform.");
            return;
        }

        if (!pegPrefab)
        {
            Debug.LogError("PegMeterBuilder_UI: pegPrefab is not assigned.");
            return;
        }

        if (clearBeforeBuild)
            ClearBuiltOnly();

        // Horizontal placement: centered from -W/2..+W/2
        float halfW = totalWidth * 0.5f;
        float stepX = (barCount <= 1) ? 0f : (totalWidth / (barCount - 1));

        for (int i = 0; i < barCount; i++)
        {
            float tBar = (barCount <= 1) ? 1f : (float)i / (barCount - 1);
            if (reverseOrder) tBar = 1f - tBar;

            float x = -halfW + (i * stepX);

            // Height curve decides how many tiers are "active"
            float v = heightCurve.Evaluate(tBar);
            if (clampCurves01) v = Mathf.Clamp01(v);

            float tierFill = Mathf.Clamp(v * tierRows, 0f, tierRows); // e.g. 3.4 tiers
            BuildSingleBar(p, i, x, tierFill, tBar);
        }
    }

    [ContextMenu("Clear Built Pegs")]
    public void ClearBuiltOnly()
    {
        var p = parent ? parent : (RectTransform)transform;
        if (!p) return;

        // Only remove objects we created (prefix-based)
        var toDestroy = new List<GameObject>();
        for (int i = 0; i < p.childCount; i++)
        {
            var child = p.GetChild(i);
            if (child && child.name.StartsWith(builtPrefix))
                toDestroy.Add(child.gameObject);
        }

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            foreach (var go in toDestroy) DestroyImmediate(go);
        }
        else
#endif
        {
            foreach (var go in toDestroy) Destroy(go);
        }
    }

    // -------------------------
    // Internals
    // -------------------------

    void BuildSingleBar(RectTransform p, int barIndex, float x, float tierFill, float tBar)
    {
        // Create a column container so the pegs for this bar are grouped
        var colGO = new GameObject($"{builtPrefix}Column_{barIndex:00}", typeof(RectTransform));
        var colRT = colGO.GetComponent<RectTransform>();
        colRT.SetParent(p, false);

        // Place column at X, centered vertically at parent's pivot (you can move the parent to position the whole meter)
        colRT.anchorMin = new Vector2(0.5f, 0.5f);
        colRT.anchorMax = new Vector2(0.5f, 0.5f);
        colRT.pivot = new Vector2(0.5f, 0.0f); // bottom pivot for stacking
        colRT.anchoredPosition = new Vector2(x, 0f);
        colRT.sizeDelta = Vector2.zero;

        // Use prefab height as a baseline (if you want)
        float prefabHeight = pegPrefab.sizeDelta.y;

        float barMult = barHeightMultCurve.Evaluate(tBar) * barHeightMult;
        barMult = Mathf.Clamp(barMult, 0f, 10f);

        float y = 0f;
        for (int tier = 0; tier < tierRows; tier++)
        {
            float tTier = (tierRows <= 1) ? 1f : (float)tier / (tierRows - 1);

            float density = tierDensityCurve.Evaluate(tTier);
            float scale = tierScaleCurve.Evaluate(tTier);

            if (clampCurves01)
            {
                // density isn't necessarily 0..1, but clamping helps prevent negative/insane values.
                density = Mathf.Clamp(density, 0f, 5f);
                scale = Mathf.Clamp(scale, 0f, 5f);
            }

            var peg = Instantiate(pegPrefab, colRT);
            peg.name = $"{builtPrefix}Peg_{barIndex:00}_{tier:00}";

            // Anchor pegs to bottom-center of the column so Y is clean
            peg.anchorMin = new Vector2(0.5f, 0f);
            peg.anchorMax = new Vector2(0.5f, 0f);
            peg.pivot = new Vector2(0.5f, 0f);

            if (clampCurves01)
                scale = Mathf.Clamp(scale, 0f, 5f);

            float pegH = prefabHeight * scale * barMult;

            // Adjust actual height, not transform scale
            var sd = peg.sizeDelta;
            sd.y = pegH;
            peg.sizeDelta = sd;

            // Stack upward using density-controlled step
            float stepY = Mathf.Max(0f, baseTierStep * density);

            peg.anchoredPosition = new Vector2(0f, y);

            // Smooth tier fill: bottom tiers full, top tier partial
            float fill01 = Mathf.Clamp01(tierFill - tier); // 1 = full, 0 = off, between = partial

            bool on = fill01 > 0f;
            peg.gameObject.SetActive(on);

            if (on)
            {
                // Apply partial fill to the top-most tier by shrinking its height
                float finalPegH = pegH * fill01;

                var sd2 = peg.sizeDelta;
                sd2.y = finalPegH;
                peg.sizeDelta = sd2;

                // Advance by the *actual* height used
                y += finalPegH + stepY;
            }
            else
            {
                // If it's off, don't advance stacking further (optional)
                // If you DO want consistent spacing even for off tiers, use: y += stepY;
                // For your "only built up to height" silhouette, do nothing:
                // y += 0;
            }
        }
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(PegMeterBuilder_UI))]
public class PegMeterBuilder_UI_Editor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        GUILayout.Space(10);

        var t = (PegMeterBuilder_UI)target;

        using (new GUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Rebuild"))
            {
                t.Rebuild();
                EditorUtility.SetDirty(t);
            }

            if (GUILayout.Button("Clear Built"))
            {
                t.ClearBuiltOnly();
                EditorUtility.SetDirty(t);
            }
        }

        EditorGUILayout.HelpBox(
            "Rebuild instantiates pegs once (no auto spam). " +
            "heightCurve controls how many tiers are active per bar. " +
            "tierDensityCurve controls vertical spacing per tier as it goes upward.",
            MessageType.Info
        );
    }
}
#endif
