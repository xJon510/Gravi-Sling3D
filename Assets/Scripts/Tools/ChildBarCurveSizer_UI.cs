using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class ChildBarCurveSizer_UI : MonoBehaviour
{
    [Header("Source")]
    public Transform parent;                 // If null, uses this.transform
    public bool includeInactive = true;
    public bool reverseOrder = false;

    [Header("Sizing")]
    public AnimationCurve heightCurve = new AnimationCurve(
        // Subtle rise, then drop off near the end
        new Keyframe(0.00f, 0.20f),
        new Keyframe(0.70f, 1.00f),
        new Keyframe(0.85f, 0.95f),
        new Keyframe(1.00f, 0.35f)
    );

    [Tooltip("Height at curve value = 1.0")]
    public float maxHeight = 60f;

    [Tooltip("Height at curve value = 0.0")]
    public float minHeight = 4f;

    public bool clampCurve01 = true;

    [Header("Target Axis")]
    public bool setHeight = true;            // sizeDelta.y
    public bool setScaleYInstead = false;    // localScale.y (useful if UI element is scaled)

    [Header("Live Update")]
    public bool autoApplyInEditor = true;

    void OnValidate()
    {
        if (!autoApplyInEditor) return;
        Apply();
    }

    [ContextMenu("Apply")]
    public void Apply()
    {
        Transform p = parent ? parent : transform;
        if (!p) return;

        int childCount = p.childCount;
        if (childCount <= 0) return;

        // Count eligible children
        int n = 0;
        for (int i = 0; i < childCount; i++)
        {
            var c = p.GetChild(i);
            if (!includeInactive && !c.gameObject.activeInHierarchy) continue;
            n++;
        }
        if (n <= 0) return;

        // Apply curve across eligible children
        int idx = 0;
        for (int i = 0; i < childCount; i++)
        {
            var child = p.GetChild(i);
            if (!includeInactive && !child.gameObject.activeInHierarchy) continue;

            float t = (n == 1) ? 1f : (float)idx / (n - 1);
            if (reverseOrder) t = 1f - t;

            float v = heightCurve.Evaluate(t);
            if (clampCurve01) v = Mathf.Clamp01(v);

            float h = Mathf.Lerp(minHeight, maxHeight, v);

            if (setHeight && !setScaleYInstead)
            {
                var rt = child as RectTransform;
                if (rt != null)
                {
                    var sd = rt.sizeDelta;
                    sd.y = h;
                    rt.sizeDelta = sd;
                }
            }
            else
            {
                Vector3 s = child.localScale;
                s.y = h; // treat as direct Y value (not a multiplier)
                child.localScale = s;
            }

            idx++;
        }
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(ChildBarCurveSizer_UI))]
public class ChildBarCurveSizer_UI_Editor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var t = (ChildBarCurveSizer_UI)target;

        GUILayout.Space(8);
        if (GUILayout.Button("Apply Now"))
        {
            t.Apply();
            EditorUtility.SetDirty(t);
        }
    }
}
#endif
