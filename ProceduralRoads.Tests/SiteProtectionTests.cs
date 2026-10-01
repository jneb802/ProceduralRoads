using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class SiteProtectionTests : IDisposable
{
    public SiteProtectionTests() { RoadSiteProtection.Reset(); RoadSiteProtection.Source = null; }
    public void Dispose() { RoadSiteProtection.Reset(); RoadSiteProtection.Source = null; Heightmap.Registered = null; RoadSpatialGrid.Clear(); }
    private sealed class Flat : WorldGenerator { public override float GetHeight(float x, float z) => 60f; }
    private static void Pit(float radius = 12) => RoadSiteProtection.Set(new[] {
        new RoadSiteProtection.Footprint(new Vector2(0, 0), radius) });

    [Fact]
    public void AnUnselectedSiteIsSkirtedByTheSearch()
    {
        Pit();
        var path = new RoadPathfinder(new Flat()).FindPath(new Vector2(-80, 0), new Vector2(80, 0));
        Assert.NotNull(path);
        for (int i = 1; i < path!.Count; i++)
        {
            // Independent geometry, not the same predicate the search uses.
            Vector2 a=path[i-1], d=path[i]-a;
            float t=d.sqrMagnitude == 0 ? 0 : Mathf.Clamp01(-(a.x*d.x+a.y*d.y)/d.sqrMagnitude);
            Assert.True((a+d*t).sqrMagnitude >= 16f*16f-0.01f, "road cuts through the protected site");
        }
    }

    [Fact]
    public void LongDiagonalCannotJumpOverASmallFootprint()
    {
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(8,4), 1) });
        Assert.True(RoadSiteProtection.BlocksSegment(new Vector2(0,0), new Vector2(16,8), 0, null, null));
    }

    [Fact]
    public void AnEndpointsExemptionDoesNotExemptOtherSites()
    {
        RoadSiteProtection.Set(new[] {
            new RoadSiteProtection.Footprint(new Vector2(0,0), 10),
            new RoadSiteProtection.Footprint(new Vector2(40,0), 10) });
        Assert.False(RoadSiteProtection.BlocksSegment(new Vector2(0,0), new Vector2(8,0),4,new Vector2(0,0),null));
        Assert.True(RoadSiteProtection.BlocksSegment(new Vector2(24,0), new Vector2(56,0),4,new Vector2(0,0),null));
    }

    [Fact]
    public void LookupWorksAcrossNegativeCellBoundariesAndIncludesTheVerge()
    {
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(-128,-128), 8) });
        Assert.True(RoadSiteProtection.Contains(new Vector2(-129,-129)));
        Assert.True(RoadSiteProtection.BlocksSegment(new Vector2(-145,-117),new Vector2(-110,-117),4,null,null));
        Assert.False(RoadSiteProtection.Contains(new Vector2(-128,-117)));
        Assert.Equal(8, RoadSiteProtection.RadiusAt(new Vector2(-128,-128),2));
    }

    [Fact]
    public void WriterPreservesSiteHeightAndPaintEvenForAnOldRoadThroughIt()
    {
        Pit(8);
        var world = new Flat(); WorldGenerator.instance = world;
        var zone = new Vector2s(0,0);
        var hm = Heightmap.CreateForZone(zone,64); Heightmap.Registered = hm;
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
        WorldGenerator.instance = null;
    }

    [Fact]
    public void LateLocationShapingCannotTurnAnOldRoadDeltaIntoARidge()
    {
        Pit(8);
        WorldGenerator.instance = new Flat();
        var zone = new Vector2s(0,0);
        var hm = Heightmap.CreateForZone(zone,64); Heightmap.Registered = hm;
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
        WorldGenerator.instance = null;
    }

    [Fact]
    public void ResetDropsThePreviousWorldsFootprints()
    {
        Pit(); Assert.True(RoadSiteProtection.Contains(new Vector2(0,0)));
        RoadSiteProtection.Reset(); Assert.False(RoadSiteProtection.Contains(new Vector2(0,0)));
    }
}
