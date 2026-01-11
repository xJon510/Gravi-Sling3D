using System;
using UnityEngine;

[CreateAssetMenu(menuName = "Asteroids/Asteroid Field Data")]
public class AsteroidFieldData : ScriptableObject
{
    [Header("Metadata")]
    public Vector3 fieldCenter = Vector3.zero;
    public Vector3 fieldSize = new Vector3(200f, 200f, 200f);
    public bool useFixedSeed = false;
    public int seed = 12345;

    [Header("Instances")]
    public int count;

    public Vector3[] positions;
    public Quaternion[] rotations;
    public float[] scales;              // uniform scale
    public int[] typeIds;               // asteroid family index
    public Vector3[] angularVelocityDeg; // degrees/sec per axis
    public float[] baseRadii;

    [Header("Spatial Index (Baked Grid)")]
    [Min(0.0001f)] public float cellSize = 20f;

    // Min corner of the field bounds used for cell coord computations.
    public Vector3 gridOrigin;

    // Sorted unique keys for occupied cells.
    public long[] cellKeys;

    // Start offsets into cellIndices for each cell key. Length = cellKeys.Length + 1.
    public int[] cellStarts;

    // Flattened asteroid indices grouped by cell.
    public int[] cellIndices;

    public void Clear()
    {
        count = 0;

        positions = Array.Empty<Vector3>();
        rotations = Array.Empty<Quaternion>();
        scales = Array.Empty<float>();
        typeIds = Array.Empty<int>();
        angularVelocityDeg = Array.Empty<Vector3>();
        baseRadii = Array.Empty<float>();

        cellSize = 20f;
        gridOrigin = Vector3.zero;
        cellKeys = Array.Empty<long>();
        cellStarts = Array.Empty<int>();
        cellIndices = Array.Empty<int>();
    }
}
