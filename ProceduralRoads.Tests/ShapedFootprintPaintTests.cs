using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road into a placed Location across the ring its levelling shapes: the Location levels a plateau and smooths the ground
/// round it down to the natural ground, and the road is laid level with the plateau out over the ring. The terrain writer
/// levels the ring from the ground the Location leaves (PlacedLocations.GroundAt), whatever heights it is handed. The server's
/// bake hands it the natural ground (the Location's objects do not stand yet when it writes), as this heightmap double does;
/// the road's paint read its ground from those heights, so across the ring, where the Location's ground and the natural
/// ground part by a metre or more per metre, the road looked held over a slope or on a face and was left unpainted: grass
/// across the road between the paint outside the ring and the paint on the plateau.
/// </summary>
public sealed class ShapedFootprintPaintTests : IDisposable
{
    private const float Natural = 34f, Plateau = 40f, Flat = 8f, Ring = 12f, Footprint = 20f, Width = RoadConstants.DefaultRoadWidth;
    private static readonly Vector2 Centre = new(0f, 0f);
    private readonly ValheimWorldScope _scope = new();

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Natural;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 31;
    }

    /// <summary>The ground the Location leaves: its plateau out to <see cref="Flat"/>, then smoothed down to the natural ground by
    /// <see cref="Ring"/>, 1.5 m a metre.</summary>
    private static float Shaped(float x, float z)
    {
        float r = new Vector2(x, z).magnitude;
        return r <= Flat ? Plateau : r >= Ring ? Natural : Mathf.Lerp(Plateau, Natural, (r - Flat) / (Ring - Flat));
    }

    public ShapedFootprintPaintTests()
    {
        _scope.WithWorld(new FlatWorld());
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(Centre, Footprint) });
        PlacedLocations.Preload(Centre, Shaped);
    }

    public void Dispose()
    {
        RoadTerrainModifier.ResetForWorld();
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
        _scope.Dispose();
    }

    /// <summary>The road from the plateau out across the ring and on past the footprint, level with the plateau, stored into the
    /// Location and written from the natural ground: its core is painted end to end, over the ring too, and the ground it
    /// leaves there is the road's.</summary>
    [Fact]
    public void ARoadAcrossALocationsShapedRingIsPaintedWhereTheWriteSawNaturalGround()
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (int x = 2; x <= 30; x++) { points.Add(new Vector2(x, 0.3f)); heights.Add(Plateau); }
        var plan = new RoadSpatialGrid.PlannedPath(points, heights, new List<RoadPointDebugInfo>(), Width, 40f, false)
        {
            Locations = new RoadLocationProtection.Targets(null, Centre),
        };
        RoadSpatialGrid.Commit(plan, intoLocation: true);

        Vector2s zone = new(0, 0);
        Heightmap hm = Heightmap.CreateForZone(zone);
        TerrainComp tc = hm.m_terrainComp!;
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, tc);

        int grid = tc.m_width + 1;
        float spacing = RoadConstants.ZoneSize / tc.m_width;
        var unpainted = new List<string>();
        for (int x = 3; x <= 28; x++)
            for (int z = 0; z <= 1; z++)
            {
                int i = Mathf.RoundToInt(z / spacing + tc.m_width / 2f) * grid + Mathf.RoundToInt(x / spacing + tc.m_width / 2f);
                // The ground the road leaves there: the plateau's height, ring or not (the writer levels from the Location's ground).
                Assert.InRange(Shaped(x, z) + tc.m_levelDelta[i] + tc.m_smoothDelta[i], Plateau - 0.05f, Plateau + 0.05f);
                if (!tc.m_modifiedPaint[i] || tc.m_paintMask[i].b < 0.99f)
                    unpainted.Add(FormattableString.Invariant($"({x}, {z}) {new Vector2(x, z).magnitude:F1} m out"));
            }
        Assert.True(unpainted.Count == 0, "the road's core is not painted at " + string.Join(", ", unpainted));
    }
}
