using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The ground round a placed location as the game will draw it, rebuilt without
/// its zone being loaded: the terrain's natural height at each 1 m vertex, the
/// location's terrain modifiers applied as Heightmap.ApplyModifiers does, and
/// outside every place's footprint the road's own earthwork. So a cave the server
/// placed unseen (ZoneSystem.SpawnZone, Ghost) can have its approach staircase
/// laid before anyone comes. Rebuilt from the mod's own heights,
/// a frost cave's prefab modifiers and its turn, it matched the game's heightmap to
/// 1 mm at all 2121 vertices within 26 m of two caves. Pure.
///
/// The game's rules (decompiled Heightmap.LevelTerrain, SmoothTerrain2 and
/// TerrainModifier.SortByModifiers): modifiers lowest m_sortOrder first, then the
/// one nearer the world's origin (a location's carry no creation time); each is
/// centred on the vertex nearest it; a level sets every vertex within its radius
/// (square: the whole axis-aligned box out to its radius rounded up) to its height;
/// a smooth moves each vertex within its radius 1 - (d / R)^power of the way there,
/// d counted in vertices. A modifier's height is the location's root plus its own.
/// </summary>
public static class PlacedSiteGround
{
    /// <summary>
    /// Heights at the vertices x0..x0+w-1, z0..z0+h-1 (row by row in z).
    /// <paramref name="natural"/>: the terrain's height at a vertex before anything
    /// shaped it. <paramref name="road"/>: given a vertex outside every footprint
    /// (<paramref name="inFootprint"/>) and the height there so far, the height the
    /// road's earthwork leaves; null for none.
    /// </summary>
    public static float[] Build(int x0, int z0, int w, int h, Vector2 centre, float root, float yaw, IReadOnlyList<LevelOp> ops,
        Func<int, int, float> natural, Func<Vector2, bool>? inFootprint = null, Func<int, int, float, float>? road = null)
    {
        var heights = new float[w * h];
        for (int k = 0; k < h; k++)
            for (int i = 0; i < w; i++)
                heights[k * w + i] = natural(x0 + i, z0 + k);

        var placed = new List<(LevelOp op, Vector2 at)>();
        foreach (LevelOp op in ops) placed.Add((op, centre + LocationLevelling.OffsetAt(op, yaw)));
        placed.Sort((a, b) => a.op.SortOrder != b.op.SortOrder ? a.op.SortOrder.CompareTo(b.op.SortOrder)
            : Origin(a.at, root + a.op.Height).CompareTo(Origin(b.at, root + b.op.Height)));

        foreach (var (op, at) in placed)
        {
            float y = root + op.Height;
            int cx = Mathf.FloorToInt(at.x + 0.5f), cz = Mathf.FloorToInt(at.y + 0.5f);
            if (op.Radius > 0f)
            {
                int n = Mathf.CeilToInt(op.Radius);
                for (int vz = cz - n; vz <= cz + n; vz++)
                    for (int vx = cx - n; vx <= cx + n; vx++)
                    {
                        int i = vx - x0, k = vz - z0;
                        if (i < 0 || k < 0 || i >= w || k >= h) continue;
                        if (op.Square || Mathf.Sqrt((vx - cx) * (vx - cx) + (vz - cz) * (vz - cz)) <= op.Radius)
                            heights[k * w + i] = y;
                    }
            }
            if (op.SmoothRadius > 0f)
            {
                int n = Mathf.CeilToInt(op.SmoothRadius);
                for (int vz = cz - n; vz <= cz + n; vz++)
                    for (int vx = cx - n; vx <= cx + n; vx++)
                    {
                        int i = vx - x0, k = vz - z0;
                        if (i < 0 || k < 0 || i >= w || k >= h) continue;
                        float d = Mathf.Sqrt((vx - cx) * (vx - cx) + (vz - cz) * (vz - cz));
                        if (d > op.SmoothRadius) continue;
                        float t = Mathf.Pow(d / op.SmoothRadius, op.SmoothPower);
                        float here = heights[k * w + i];
                        heights[k * w + i] = here + (y - here) * (1f - t);
                    }
            }
        }

        if (road != null)
            for (int k = 0; k < h; k++)
                for (int i = 0; i < w; i++)
                {
                    int vx = x0 + i, vz = z0 + k;
                    if (inFootprint != null && inFootprint(new Vector2(vx, vz))) continue;
                    heights[k * w + i] = road(vx, vz, heights[k * w + i]);
                }
        return heights;
    }

    /// <summary>The game's tie-break between modifiers: squared distance from the world's origin.</summary>
    private static float Origin(Vector2 at, float y) => at.x * at.x + y * y + at.y * at.y;

    /// <summary>What <see cref="Sampler"/> answers off its grid: a wall.</summary>
    public const float OffGrid = 10000f;

    /// <summary>The ground between the vertices of a grid made by <see cref="Build"/>, as the game's
    /// mesh joins them (bilinear); beyond it, a wall the stair layout will not climb.</summary>
    public static Func<float, float, float> Sampler(float[] heights, int x0, int z0, int w, int h) => (x, z) =>
    {
        float fx = x - x0, fz = z - z0;
        int i = Mathf.FloorToInt(fx), k = Mathf.FloorToInt(fz);
        if (i < 0 || k < 0 || i + 1 >= w || k + 1 >= h) return OffGrid;
        float tx = fx - i, tz = fz - k;
        return Mathf.Lerp(Mathf.Lerp(heights[k * w + i], heights[k * w + i + 1], tx),
                          Mathf.Lerp(heights[(k + 1) * w + i], heights[(k + 1) * w + i + 1], tx), tz);
    };
}
