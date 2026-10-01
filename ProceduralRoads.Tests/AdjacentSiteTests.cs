using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Two sites close enough that clearing their protected footprints leaves
/// nothing to lay between them. A road there would have ended exactly where
/// the ground already is, so the question is not whether a road exists but
/// whether the network already reaches the place.
/// </summary>
public class AdjacentSiteTests : System.IDisposable
{
    public void Dispose() => RoadSpatialGrid.Clear();

    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float w, out float width) { w = 0f; width = 0f; }
    }

    private static List<Vector2> Line(float x0, float x1, float z)
    {
        var path = new List<Vector2>();
        for (float x = x0; x <= x1; x += 4f) path.Add(new Vector2(x, z));
        return path;
    }

    [Fact]
    public void ASiteTheNetworkAlreadyReachesCountsAsServed()
    {
        // The project's own rule for "served": a road point inside the site's
        // footprint plus one path-grid step. A road running past the site is
        // what a road TO the site would have amounted to.
        RoadSpatialGrid.Clear();
        RoadSpatialGrid.AddRoadPath(Line(-400f, 400f, 0f), 4f, new Flat(), null, null);

        Assert.True(RoadNetworkGenerator.AlreadyServedByNetwork(new Vector3(0f, 0f, 20f), 32f),
            "a road through the site's own footprint did not count as reaching it");
        // Just outside the footprint plus a grid step.
        Assert.False(RoadNetworkGenerator.AlreadyServedByNetwork(new Vector3(0f, 0f, 200f), 32f));
    }

    [Fact]
    public void TwoAdjacentSitesWithNoRoadNearThemAreNotConnected()
    {
        // The case that must NOT be counted: their footprints meet, so nothing
        // can be laid between them, and neither is reached by anything. Two
        // adjacent places in the wild are still adjacent and still unreached.
        RoadSpatialGrid.Clear();
        Assert.False(RoadNetworkGenerator.AlreadyServedByNetwork(new Vector3(5000f, 0f, 5000f), 32f));
        Assert.False(RoadNetworkGenerator.AlreadyServedByNetwork(new Vector3(5040f, 0f, 5000f), 32f));
    }

    [Fact]
    public void ReachIsWhereARoadWouldHaveStoppedNotTheExteriorRadius()
    {
        // The reach has to follow the TRIMMER. A site whose terrain modifiers
        // reach well past its exterior radius pushes roads out to there, and
        // measuring from the exterior radius would call such a site unreached
        // while a road runs along its edge.
        RoadSpatialGrid.Clear();
        RoadSiteProtection.Reset();
        RoadSpatialGrid.AddRoadPath(Line(-400f, 400f, 0f), 4f, new Flat(), null, null);

        var centre = new Vector3(0f, 0f, 90f);
        const float exterior = 20f;
        // With only the exterior radius, a road 90 m away is out of reach.
        RoadSiteProtection.Source = () => new RoadSiteProtection.Footprint[0];
        RoadSiteProtection.Prime();
        Assert.False(RoadNetworkGenerator.AlreadyServedByNetwork(centre, exterior));

        // Declare the site's real footprint -- its levelling reaches 80 m --
        // and the same road is now where a road to it would have stopped.
        RoadSiteProtection.Reset();
        RoadSiteProtection.Source = () => new[] {
            new RoadSiteProtection.Footprint(new Vector2(centre.x, centre.z), 80f) };
        RoadSiteProtection.Prime();
        Assert.True(RoadNetworkGenerator.AlreadyServedByNetwork(centre, exterior));

        RoadSiteProtection.Source = null;
        RoadSiteProtection.Reset();
    }
}
