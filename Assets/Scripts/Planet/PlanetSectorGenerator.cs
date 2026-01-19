using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Deterministic "one site per large cell" planet placement.
/// This gives you Voronoi-ish regions without computing Voronoi meshes.
/// </summary>
public static class PlanetSectorGenerator
{
    [Serializable]
    public struct PlanetNode
    {
        public int id;
        public Vector3 position;
        public float avoidRadius; // how far asteroids should stay away (your "orbit space")
    }

    // Core API: get planets that could affect an asteroid chunk.
    public static void GetPlanetsForChunk(
        int globalSeed,
        Vector3 chunkOrigin,
        float chunkSize,
        float planetCellSize,
        float avoidRadius,
        float spawnChance,
        List<PlanetNode> outPlanets)
    {
        outPlanets.Clear();

        // Expand query bounds so planets just outside the chunk still cull asteroids inside.
        float pad = avoidRadius;
        Vector3 min = chunkOrigin - Vector3.one * pad;
        Vector3 max = chunkOrigin + Vector3.one * (chunkSize + pad);

        Vector3Int cmin = WorldToCell(min, planetCellSize);
        Vector3Int cmax = WorldToCell(max, planetCellSize);

        for (int z = cmin.z; z <= cmax.z; z++)
            for (int y = cmin.y; y <= cmax.y; y++)
                for (int x = cmin.x; x <= cmax.x; x++)
                {
                    var cell = new Vector3Int(x, y, z);
                    int cellSeed = HashSeed(globalSeed, cell);

                    // Deterministic roll: does this cell even have a planet?
                    // Use a simple int->float conversion, no allocations.
                    uint u = (uint)cellSeed;
                    float r01 = (u & 0x00FFFFFFu) / 16777216f; // 24-bit fraction [0,1)
                    if (r01 > spawnChance)
                        continue;

                    // Jittered position within the cell (with padding so it doesn't hug borders)
                    var rng = new System.Random(cellSeed);
                    Vector3 cellOrigin = new Vector3(x * planetCellSize, y * planetCellSize, z * planetCellSize);

                    float innerPad = planetCellSize * 0.15f; // tweak
                    float span = Mathf.Max(1f, planetCellSize - innerPad * 2f);

                    float px = (float)rng.NextDouble() * span + innerPad;
                    float py = (float)rng.NextDouble() * span + innerPad;
                    float pz = (float)rng.NextDouble() * span + innerPad;

                    Vector3 pos = cellOrigin + new Vector3(px, py, pz);

                    // Stable-ish unique ID (for renderer dictionary keys etc.)
                    int id = HashSeed(globalSeed ^ 0x5bd1e995, cell);

                    outPlanets.Add(new PlanetNode
                    {
                        id = id,
                        position = pos,
                        avoidRadius = avoidRadius
                    });
                }
    }

    private static Vector3Int WorldToCell(Vector3 p, float cellSize)
    {
        return new Vector3Int(
            Mathf.FloorToInt(p.x / cellSize),
            Mathf.FloorToInt(p.y / cellSize),
            Mathf.FloorToInt(p.z / cellSize)
        );
    }

    private static int HashSeed(int baseSeed, Vector3Int c)
    {
        unchecked
        {
            int h = baseSeed;
            h = (h * 397) ^ c.x;
            h = (h * 397) ^ c.y;
            h = (h * 397) ^ c.z;
            return h;
        }
    }
}
