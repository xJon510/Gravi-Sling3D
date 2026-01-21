using System;
using UnityEngine;

[CreateAssetMenu(menuName = "Asteroids/Asteroid Dust Field Data")]
public class AsteroidDustFieldData : ScriptableObject
{
    public int count;
    public Vector3[] positions;
    public Quaternion[] rotations;
    public float[] scales;

    public Vector3[] angularVelocityDeg;
    public Vector3[] driftVelocity;

    public void Clear()
    {
        count = 0;
        positions = Array.Empty<Vector3>();
        rotations = Array.Empty<Quaternion>();
        scales = Array.Empty<float>();
        angularVelocityDeg = Array.Empty<Vector3>();
        driftVelocity = Array.Empty<Vector3>();
    }
}
