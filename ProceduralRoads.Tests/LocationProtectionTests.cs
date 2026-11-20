using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public class LocationProtectionTests : IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope();
    public LocationProtectionTests() { RoadLocationProtection.Reset(); RoadLocationProtection.Source = null; }
    public void Dispose() { PlacedLocations.Reset(); RoadLocationProtection.Reset(); RoadLocationProtection.Source = null; RoadSpatialGrid.Clear(); _world.Dispose(); }
    private sealed class Flat : WorldGenerator { public override float GetHeight(float x, float z) => 60f; }
    private static void Pit(float radius = 12) => RoadLocationProtection.Set(new[] {
        new RoadLocationProtection.Footprint(new Vector2(0, 0), radius) });

    [Fact]
    public void AnUnselectedLocationIsSkirtedByTheSearch()
    {
        Pit();
        var path = new RoadPathfinder(new Flat()).FindPath(new Vector2(-80, 0), new Vector2(80, 0));
        Assert.NotNull(path);
        for (int i = 1; i < path!.Count; i++)
        {
            // Independent geometry, not the same predicate the search uses.
            Vector2 a=path[i-1], d=path[i]-a;
            float t=d.sqrMagnitude == 0 ? 0 : Mathf.Clamp01(-(a.x*d.x+a.y*d.y)/d.sqrMagnitude);
            Assert.True((a+d*t).sqrMagnitude >= 16f*16f-0.01f, "road cuts through the protected location");
        }
    }

    [Fact]
    public void LongDiagonalCannotJumpOverASmallFootprint()
    {
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(8,4), 1) });
        Assert.True(RoadLocationProtection.BlocksSegment(new Vector2(0,0), new Vector2(16,8), 0, default));
    }

    [Fact]
    public void AnEndpointsExemptionDoesNotExemptOtherLocations()
    {
        RoadLocationProtection.Set(new[] {
            new RoadLocationProtection.Footprint(new Vector2(0,0), 10),
            new RoadLocationProtection.Footprint(new Vector2(40,0), 10) });
        // The exempt location is on the preload list (RoadLocationProtection.Enterable).
        PlacedLocations.Preload(new Vector2(0,0));
        Assert.False(RoadLocationProtection.BlocksSegment(new Vector2(0,0), new Vector2(8,0),4,new RoadLocationProtection.Targets(new Vector2(0,0),null)));
        Assert.True(RoadLocationProtection.BlocksSegment(new Vector2(24,0), new Vector2(56,0),4,new RoadLocationProtection.Targets(new Vector2(0,0),null)));
    }

    [Fact]
    public void LookupWorksAcrossNegativeCellBoundariesAndIncludesTheVerge()
    {
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(-128,-128), 8) });
        Assert.True(RoadLocationProtection.Contains(new Vector2(-129,-129)));
        Assert.True(RoadLocationProtection.BlocksSegment(new Vector2(-145,-117),new Vector2(-110,-117),4,default));
        Assert.False(RoadLocationProtection.Contains(new Vector2(-128,-117)));
        Assert.Equal(8, RoadLocationProtection.RadiusAt(new Vector2(-128,-128),2));
    }

    [Fact]
    public void WriterPreservesLocationHeightAndPaintEvenForAnOldRoadThroughIt()
    {
        Pit(8);
        var world = new Flat(); _world.WithWorld(world);
        var zone = new Vector2s(0,0);
        var hm = _world.RegisterHeightmap(zone,64);
        var tc = hm.m_terrainComp!;
        int middle = 32 * 65 + 32;
        tc.m_levelDelta[middle] = 1.5f;
        tc.m_smoothDelta[middle] = 0.25f;
        var beforePaint = tc.m_paintMask[middle];
        var points = new List<RoadSpatialGrid.RoadPoint>();
        for (int x=-24;x<=24;x++) points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x,0),4,world.GetHeight(x,0)+4));
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone,points,hm,tc);
        Assert.Equal(1.5f,tc.m_levelDelta[middle]);
        Assert.Equal(0.25f,tc.m_smoothDelta[middle]);
        Assert.Equal(beforePaint,tc.m_paintMask[middle]);
        Assert.False(tc.m_modifiedPaint[middle]);
        Assert.True(tc.m_modifiedHeight[32*65+52]); // genuinely wrote outside
    }

    [Fact]
    public void LateLocationShapingCannotTurnAnOldRoadDeltaIntoARidge()
    {
        Pit(8);
        _world.WithWorld(new Flat());
        var zone = new Vector2s(0,0);
        var hm = _world.RegisterHeightmap(zone,64);
        var points = new List<RoadSpatialGrid.RoadPoint>();
        for (int x=-24;x<=24;x++) points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x,0),4,61.2f));
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone,points,hm,hm.m_terrainComp!);
        int middle=32*65+32;
        Assert.Equal(60f,hm.LastRenderedHeights![middle]);
        // The proxy's authored terrain appears on a later rebuild, after the
        // compiler already carries the current road stamp. No +1.2 survives
        // in the protected footprint to become a 70.2 m ridge.
        hm.AuthoredHeight = (x,z) => x*x+z*z < 64 ? 69f : 60f;
        hm.RebuildTerrain();
        Assert.Equal(69f,hm.LastRenderedHeights![middle]);
        Assert.Equal(0f,hm.m_terrainComp!.m_levelDelta[middle]);
        Assert.True(hm.m_terrainComp.m_modifiedHeight[32*65+52]);
    }

    [Fact]
    public void ResetDropsThePreviousWorldsFootprints()
    {
        Pit(); Assert.True(RoadLocationProtection.Contains(new Vector2(0,0)));
        RoadLocationProtection.Reset(); Assert.False(RoadLocationProtection.Contains(new Vector2(0,0)));
    }
}
