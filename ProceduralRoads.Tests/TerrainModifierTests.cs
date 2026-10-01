using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// RoadTerrainModifier end to end on a shimmed zone: road points in, level
/// deltas and paint mask out. Pins the cross-section wiring — the leveled
/// footprint is wider than the painted one, the flat core is fully leveled,
/// terrain beyond the blend margin is untouched, and paint alpha survives.
/// </summary>
public class TerrainModifierTests
{
    private const int Width = 64;                 // vanilla zone: 64 vertices per side, 1 m apart
    private const float RoadWidth = 4f;
    private const float Lift = 1.5f;              // road height above natural terrain

    /// <summary>A straight east-west road through the zone centre, one point per metre.</summary>
    private static (Heightmap hm, TerrainComp tc, float natural) BuildZone(SyntheticWorld world) =>
        BuildZone(world, new Vector2s(0, 0));

    private static (Heightmap hm, TerrainComp tc, float natural) BuildZone(SyntheticWorld world, Vector2s zone)
    {
        PlainEarthworks.Begin();
        Vector3 centre = ZoneSystem.GetZonePos(zone);
        Heightmap hm = Heightmap.CreateForZone(zone, Width);
        Heightmap.Registered = hm;
        TerrainComp tc = hm.m_terrainComp!;
        for (int i = 0; i < tc.m_paintMask.Length; i++)
            tc.m_paintMask[i] = new Color(0f, 0f, 0f, 0.25f);   // alpha must survive painting

        float natural = BiomeBlendedHeight.GetBlendedHeight(centre.x, centre.z, world);
        var points = new List<RoadSpatialGrid.RoadPoint>();
        for (float x = -40f; x <= 40f; x += 1f)
            points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(centre.x + x, centre.z), RoadWidth, natural + Lift));

        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
        return (hm, tc, natural);
    }

    /// <summary>Vertex index for (x, z) metres from the zone centre on the zone grid.</summary>
    private static int Index(int x, int z) => (z + Width / 2) * (Width + 1) + (x + Width / 2);

    private static SyntheticWorld FlatWorld()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        return world;
    }

    [Fact]
    public void FlatCoreIsLeveledToRoadHeight()
    {
        try
        {
            var (_, tc, natural) = BuildZone(FlatWorld());
            // The flat core spans the whole 2 m half-width: the centreline and
            // the vertices 1 m either side sit fully on the road height.
            foreach (int z in new[] { -1, 0, 1 })
            {
                int i = Index(0, z);
                Assert.True(tc.m_modifiedHeight[i], $"vertex z={z} not modified");
                float expected = Lift + (natural - BiomeBlendedHeight.GetBlendedHeight(0f, z, WorldGenerator.instance!));
                Assert.True(Mathf.Abs(tc.m_levelDelta[i] - expected) < 0.05f,
                    $"z={z}: delta {tc.m_levelDelta[i]:F2}, expected {expected:F2}");
            }
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void LeveledFootprintIsWiderThanPaint()
    {
        try
        {
            var (_, tc, _) = BuildZone(FlatWorld());
            // Paint fades to zero at 0.85 * half-width = 1.7 m; leveling
            // reaches half-width + blend margin = 4 m. So at 2 m from the
            // centreline the terrain is leveled but carries no paint: the
            // grassy verge.
            int verge = Index(0, 2);
            Assert.True(tc.m_modifiedHeight[verge], "verge vertex not leveled");
            Assert.True(tc.m_levelDelta[verge] > 0f, "verge vertex has no positive delta");
            Assert.False(tc.m_modifiedPaint[verge], "verge vertex was painted");

            // Inside the solid core the mask is fully the surface: at the
            // world centre that is dirt.
            int core = Index(0, 1);
            Assert.True(tc.m_modifiedPaint[core], "core vertex not painted");
            AssertMask(Heightmap.m_paintMaskDirt, tc.m_paintMask[core], "core paint at the world centre");
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void TerrainBeyondBlendMarginIsUntouched()
    {
        try
        {
            var (_, tc, _) = BuildZone(FlatWorld());
            // Half-width 2 m + TerrainBlendMargin 2 m = 4 m: the vertex at
            // 4 m has blend 0 and must not be touched; 5 m certainly not.
            foreach (int z in new[] { 4, 5, -4, -5 })
            {
                int i = Index(0, z);
                Assert.False(tc.m_modifiedHeight[i], $"vertex z={z} was leveled");
                Assert.Equal(0f, tc.m_levelDelta[i]);
                Assert.False(tc.m_modifiedPaint[i], $"vertex z={z} was painted");
            }
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void ShoulderFallsMonotonicallyAndPaintAlphaSurvives()
    {
        try
        {
            var (hm, tc, _) = BuildZone(FlatWorld());
            float previous = float.MaxValue;
            for (int z = 0; z <= 4; z++)
            {
                int i = Index(0, z);
                Assert.True(tc.m_levelDelta[i] <= previous + 1e-4f,
                    $"delta rises from {previous:F3} to {tc.m_levelDelta[i]:F3} at z={z}");
                Assert.False(float.IsNaN(tc.m_levelDelta[i]), $"NaN delta at z={z}");
                previous = tc.m_levelDelta[i];
                Assert.True(Mathf.Abs(tc.m_paintMask[i].a - 0.25f) < 1e-5f, $"paint alpha changed at z={z}");
            }
            Assert.Equal(1, tc.SaveCount);
            Assert.Equal(1, hm.PokeCount);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void RoadPastTheSwampRingIsPavedLikeTheHoe()
    {
        try
        {
            // Zone (40, 0) is centred 2560 m out: past the 2000 m ring the
            // road is the game's own paving, exactly what the painter wrote
            // before surfaces existed.
            var (_, tc, _) = BuildZone(FlatWorld(), new Vector2s(40, 0));
            int core = Index(0, 1);
            Assert.True(tc.m_modifiedPaint[core], "core vertex not painted");
            AssertMask(Heightmap.m_paintMaskPaved, tc.m_paintMask[core], "core paint at 2560 m");
            Assert.True(Mathf.Abs(tc.m_paintMask[core].a - 0.25f) < 1e-5f, "paint alpha changed");
        }
        finally { Cleanup(); }
    }

    private static void AssertMask(Color expected, Color actual, string what) =>
        Assert.True(Mathf.Abs(actual.r - expected.r) < 1e-4f && Mathf.Abs(actual.g - expected.g) < 1e-4f
                    && Mathf.Abs(actual.b - expected.b) < 1e-4f,
            $"{what} is ({actual.r:F2}, {actual.g:F2}, {actual.b:F2}), expected ({expected.r:F2}, {expected.g:F2}, {expected.b:F2})");

    private static void Cleanup()
    {
        Heightmap.Registered = null;
        WorldGenerator.instance = null;
        PlainEarthworks.End();
    }
}
