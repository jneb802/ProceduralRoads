using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase set beside its road (its edge on the road's edge) and turning back down along it, on a hillside falling
/// away from the road. The staircase's cuts, at its treads' heights, and their batter dig into the road's side, deeper
/// as the flight goes down: the ground under the road falls metres within a metre of the road's surface. Paint stays on
/// the road's surface: not on the ground the cut left below it (held to that ground, the paint ran up the face to the
/// road), nor on a vertex a face falls away from or rises over, down whose cell the game stretches the paint. Seen in the
/// Mistlands beside a staircase with its top at (4984, 4368): the road's paint stretched down a face about 10 m tall.
/// </summary>
public sealed class StairBesidePaintTests : IDisposable
{
    private const int Width = 64;
    private const float Road = 60f;
    private readonly ValheimWorldScope _scope = new();

    public void Dispose() => _scope.Dispose();

    /// <summary>Falling 1 m per metre southwards (towards -z), the road along z = 0 at the ground's height.</summary>
    private sealed class Hillside : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Road + wy;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private TerrainComp Write(out Func<int, int, float> ground)
    {
        using var plain = PlainEarthworks.Apply();
        var world = new Hillside();
        _scope.WithWorld(world);
        var points = new List<RoadSpatialGrid.RoadPoint>();
        for (float x = -30f; x <= 30f; x += 1f)
            points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, 0f), 4f, Road));
        // A 2 m staircase beside the road's south edge, its top at the road's height at x = 8, going down westwards at
        // a rise to its run, with the cuts its layout makes under its treads within StairLayout.EndGuardReach of the
        // top (and on, where the ground covers it).
        var top = new Vector2(8f, -3f);
        Vector2 down = new(-1f, 0f), across = new(0f, 1f);
        float grade = StairLayout.PieceRise / StairLayout.PieceRun;
        for (float along = StairLayout.CutSpacing / 2f; along < 18f; along += StairLayout.CutSpacing)
        {
            float surface = Road - along * grade;
            var row = new List<Vector2>();
            bool covered = false;
            for (float side = -0.75f; side <= 0.76f; side += StairLayout.CutSpacing)
            {
                Vector2 p = top + down * along + across * side;
                row.Add(p);
                covered |= world.GetHeight(p.x, p.y) > surface + StairLayout.CoverTolerance;
            }
            if (along > StairLayout.DefaultEndGuardReach && !covered) continue;
            foreach (Vector2 p in row)
                points.Add(new RoadSpatialGrid.RoadPoint(p, StairLayout.CutRadius * 2f, surface - StairLayout.CutBelowSurface, cutOnly: true));
        }
        var zone = new Vector2s(0, 0);
        Heightmap hm = _scope.RegisterHeightmap(zone, Width);
        TerrainComp tc = hm.m_terrainComp!;
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
        ground = (x, z) =>
        {
            int i = Index(x, z);
            return BiomeBlendedHeight.GetBlendedHeight(x, z, world) + (tc.m_modifiedHeight[i] ? tc.m_levelDelta[i] + tc.m_smoothDelta[i] : 0f);
        };
        return tc;
    }

    private static int Index(int x, int z) => (z + Width / 2) * (Width + 1) + (x + Width / 2);

    [Fact]
    public void PaintStaysOnTheRoadsSurfaceBesideAStaircaseThatCutsIntoIt()
    {
        TerrainComp tc = Write(out var ground);
        // The staircase's cut does take the road's side: the ground under the road falls metres within a metre.
        float lowest = float.PositiveInfinity;
        for (int x = -28; x <= 28; x++) lowest = Mathf.Min(lowest, ground(x, 0));
        Assert.True(lowest < Road - 3f, $"the cut did not reach the road (lowest {lowest:F2})");
        for (int z = -4; z <= 4; z++)
            for (int x = -28; x <= 28; x++)
            {
                if (!tc.m_modifiedPaint[Index(x, z)]) continue;
                float h = ground(x, z);
                Assert.True(h >= Road - RoadTerrainModifier.PaintBelow,
                    $"paint at ({x}, {z}) on ground {h:F2}, {Road - h:F2} m below the road's surface");
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        Assert.True(Mathf.Abs(ground(x + dx, z + dz) - h) <= RoadTerrainModifier.PaintFace,
                            $"paint at ({x}, {z}) on ground {h:F2} beside a face: ({x + dx}, {z + dz}) stands at {ground(x + dx, z + dz):F2}");
            }
        // The road keeps its paint wherever its surface is still there and off any face: away from the staircase, and
        // along most of the staircase's top, where the cut is shallow.
        int kept = 0;
        for (int z = -1; z <= 1; z++)
            for (int x = -28; x <= 28; x++)
            {
                bool level = true;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        level &= Mathf.Abs(ground(x + dx, z + dz) - Road) < 0.25f;
                if (!level) continue;
                Assert.True(tc.m_modifiedPaint[Index(x, z)], $"the road's surface unpainted at ({x}, {z})");
                kept++;
            }
        Assert.True(kept > 100, $"only {kept} vertices of the road left level");
    }
}
