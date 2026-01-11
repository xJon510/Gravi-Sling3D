using UnityEngine;

[RequireComponent(typeof(AsteroidFieldInstancedRenderer))]
public class AsteroidChunkRendererBinder : MonoBehaviour
{
    public AsteroidPosManager posManager;

    private AsteroidFieldInstancedRenderer asteroidRenderer;

    private void Awake()
    {
        asteroidRenderer = GetComponent<AsteroidFieldInstancedRenderer>();
        if (!posManager)
            posManager = FindFirstObjectByType<AsteroidPosManager>();
    }
    private void LateUpdate()
    {
        if (!posManager) return;

        asteroidRenderer.fieldDatas.Clear();
        foreach (var kv in posManager.Chunks)
            asteroidRenderer.fieldDatas.Add(kv.Value);
    }

    private void OnEnable()
    {
        if (posManager)
            posManager.OnChunkCreated += HandleChunkCreated;
    }

    private void OnDisable()
    {
        if (posManager)
            posManager.OnChunkCreated -= HandleChunkCreated;
    }

    private void HandleChunkCreated(Vector3Int coord, AsteroidFieldData data)
    {
        if (!asteroidRenderer.fieldDatas.Contains(data))
            asteroidRenderer.fieldDatas.Add(data);
    }
}
