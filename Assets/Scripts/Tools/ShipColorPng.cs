using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Edit a 256x1 (palette strip) texture by changing specific pixels, without modifying the source asset.
/// Writes the edited palette into a runtime Texture2D instance and assigns it directly to a chosen Material.
/// </summary>
[ExecuteAlways]
public class ShipColorPng : MonoBehaviour
{
    [Serializable]
    public struct PixelEdit
    {
        [Range(0, 4095)] public int pixelPos; // not hard-limited to 255 in case some packs differ
        public Color color;
    }

    [Header("Inputs")]
    [Tooltip("Source palette texture asset (typically 256x1). This asset is NEVER modified.")]
    public Texture2D sourcePng;

    [Tooltip("The material you want to modify (use your DUPLICATE material here).")]
    public Material targetMaterial;

    [Tooltip("Optional: apply targetMaterial to this renderer automatically.")]
    public Renderer targetRenderer;

    [Header("Palette Texture Slot")]
    [Tooltip("Texture property name the shader uses for the palette strip (common: _BaseMap, _MainTex).")]
    public string textureProperty = "_MainTex";

    [Tooltip("If true, will try common fallbacks if textureProperty is wrong.")]
    public bool tryAutoDetectTextureProperty = true;

    [Header("Texture Sampling (important for palette strips)")]
    [Tooltip("Point filtering prevents colors from blending between palette pixels.")]
    public bool forcePointFilter = true;

    [Tooltip("Clamp is typical for palette strips.")]
    public bool forceClampWrap = true;

    [Header("Edits (each entry edits one pixel on row 0)")]
    public List<PixelEdit> edits = new();

    [Header("Live Update")]
    public bool applyOnValidate = true;

    [Header("Debug (read-only)")]
    [SerializeField] private Texture2D _paletteInstance;

    private int _lastSourceId;
    private int _lastHash;

    private void Reset()
    {
        targetRenderer = GetComponentInChildren<Renderer>();
        if (targetRenderer && targetMaterial == null)
            targetMaterial = targetRenderer.sharedMaterial;
    }

    private void OnEnable()
    {
        if (applyOnValidate)
            Apply();
    }

    private void OnDisable()
    {
        CleanupTextureInstance();
    }

#if UNITY_EDITOR
    private void OnDestroy()
    {
        CleanupTextureInstance();
    }
#endif

    private void OnValidate()
    {
        if (!applyOnValidate) return;

#if UNITY_EDITOR
        EditorApplication.delayCall += () =>
        {
            if (this == null) return;
            Apply();
        };
#else
        Apply();
#endif
    }

    public void Apply()
    {
        if (!sourcePng || !targetMaterial)
            return;

        if (!sourcePng.isReadable)
        {
            Debug.LogWarning(
                $"[{nameof(ShipColorPng)}] Source texture '{sourcePng.name}' is not readable. " +
                $"Enable Read/Write in import settings.",
                this
            );
            return;
        }

        int sourceId = sourcePng.GetInstanceID();
        int hash = ComputeEditsHash();

        if (_lastSourceId == sourceId && _lastHash == hash && _paletteInstance != null)
            return;

        _lastSourceId = sourceId;
        _lastHash = hash;

        EnsureTextureInstance();

        // Copy source pixels
        var pixels = sourcePng.GetPixels();
        _paletteInstance.SetPixels(pixels);

        // Apply edits on row 0
        int w = _paletteInstance.width;
        int h = _paletteInstance.height;

        for (int i = 0; i < edits.Count; i++)
        {
            int x = Mathf.Clamp(edits[i].pixelPos, 0, w - 1);
            int y = 0;
            if (h <= 0) continue;

            _paletteInstance.SetPixel(x, y, edits[i].color);
        }

        _paletteInstance.Apply(updateMipmaps: false, makeNoLongerReadable: false);

        // Assign to material property
        string prop = ResolveTextureProperty(targetMaterial, textureProperty, tryAutoDetectTextureProperty);
        if (string.IsNullOrEmpty(prop))
        {
            Debug.LogWarning(
                $"[{nameof(ShipColorPng)}] Could not find a valid texture property on material '{targetMaterial.name}'. " +
                $"Set textureProperty to the correct slot used by the shader (e.g. _BaseMap, _MainTex, _Palette, etc.).",
                this
            );
            return;
        }

        targetMaterial.SetTexture(prop, _paletteInstance);

#if UNITY_EDITOR
        EditorUtility.SetDirty(targetMaterial);
#endif

        // Optional: assign material to renderer
        if (targetRenderer)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                targetRenderer.sharedMaterial = targetMaterial;
                EditorUtility.SetDirty(targetRenderer);
            }
            else
#endif
            {
                targetRenderer.material = targetMaterial;
            }
        }
    }

    private void EnsureTextureInstance()
    {
        if (_paletteInstance != null)
        {
            if (_paletteInstance.width != sourcePng.width || _paletteInstance.height != sourcePng.height)
            {
                CleanupTextureInstance();
            }
        }

        if (_paletteInstance == null)
        {
            // Use a safe format for palette textures
            var fmt = TextureFormat.RGBA32;
            _paletteInstance = new Texture2D(sourcePng.width, sourcePng.height, fmt, mipChain: false)
            {
                name = $"{sourcePng.name}_PALETTE_INSTANCE_{gameObject.name}",
                hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild
            };
        }

        if (forcePointFilter) _paletteInstance.filterMode = FilterMode.Point;
        else _paletteInstance.filterMode = sourcePng.filterMode;

        if (forceClampWrap) _paletteInstance.wrapMode = TextureWrapMode.Clamp;
        else _paletteInstance.wrapMode = sourcePng.wrapMode;
    }

    private void CleanupTextureInstance()
    {
        if (_paletteInstance == null) return;

#if UNITY_EDITOR
        if (!Application.isPlaying) DestroyImmediate(_paletteInstance);
        else Destroy(_paletteInstance);
#else
        Destroy(_paletteInstance);
#endif
        _paletteInstance = null;
    }

    private static string ResolveTextureProperty(Material mat, string preferred, bool tryDetect)
    {
        if (mat == null) return null;

        if (!string.IsNullOrWhiteSpace(preferred) && mat.HasProperty(preferred))
            return preferred;

        if (!tryDetect) return null;

        // Common texture slot names across pipelines / custom shaders
        string[] candidates =
        {
            "_BaseMap", "_MainTex",
            "_ColorTex", "_PaletteTex", "_Palette", "_PaletteMap",
            "_Albedo", "_Diffuse"
        };

        foreach (var c in candidates)
            if (mat.HasProperty(c))
                return c;

        return null;
    }

    private int ComputeEditsHash()
    {
        unchecked
        {
            int hash = 17;
            for (int i = 0; i < edits.Count; i++)
            {
                hash = hash * 31 + edits[i].pixelPos;
                hash = hash * 31 + edits[i].color.GetHashCode();
            }
            return hash;
        }
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(ShipColorPng))]
public class ShipColorPngEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var t = (ShipColorPng)target;

        EditorGUILayout.Space(8);

        using (new EditorGUI.DisabledScope(t.sourcePng == null || t.targetMaterial == null))
        {
            if (GUILayout.Button("Apply Palette Edits"))
                t.Apply();
        }

        if (t.sourcePng != null && !t.sourcePng.isReadable)
        {
            EditorGUILayout.HelpBox(
                "Source PNG is not readable. In the texture import settings, enable Read/Write.",
                MessageType.Warning
            );
        }

        EditorGUILayout.HelpBox(
            "If nothing changes: your shader probably doesn't use _MainTex. Find the palette texture slot name in the material inspector (or try _BaseMap). Also keep palette textures on Point filtering.",
            MessageType.Info
        );
    }
}
#endif
