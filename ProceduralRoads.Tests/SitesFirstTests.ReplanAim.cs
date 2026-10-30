using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

public partial class SitesFirstTests
{
    /// <summary>An earthwork re-route of a road to a placed place comes in head on to the ring point the road's own search
    /// was aimed at. Here that point is round the ring from the landing's side (the landing's side is dry at the ring but the
    /// sea is just beyond it); worked out again at no turn with only the ring point on ground, the re-route aimed at the
    /// landing's side, did not match the search's lead-in, and lost its ring point: it stopped at the lead-in, 8 m out.</summary>
    [Fact]
    public void AnEarthworkReRouteComesInToTheRingPointTheRoadWasAimedAt()
    {
        var sea = new SeaOnTheLandingsSide();
        _scope.WithWorld(sea);
        PlacedSite cave = Cave();
        Func<float, float, float> ground = PlacedSiteGround.Sampler(cave.Record.Ground((x, z) => sea.GetHeight(x, z)), cave.Record.X0, cave.Record.Z0, cave.Record.W, cave.Record.H);
        var site = new PlacedSite { Record = cave.Record, Ground = ground, Shape = PlacedSiteShape.Of(cave.Centre, 90f, CaveFloor[0], 0f, null, ground, false, cave.Record.Root) };
        PlacedSites.Ready(site);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        var finder = new RoadPathfinder(sea) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f };
        SetPathfinder(finder);
        float ring = RoadSiteProtection.RadiusAt(site.Centre, 10f) + 4f * 0.5f + 2f;
        bool Ground(Vector2 p) => RoadCrossingDetector.IsRoadGround(p, sea);
        Vector2 LeadIn(Vector2 a) => a + (a - site.Centre).normalized * RoadNetworkGenerator.LeadInLength;
        Vector2 aim = PlacedSites.Aims(site, ring, p => Ground(p) && Ground(LeadIn(p)))[0];
        // The point-only aim is the landing's side, not where the road was aimed.
        Assert.True(Vector2.Distance(PlacedSites.Aims(site, ring, Ground)[0], aim) > 2f);

        var retry = RoadNetworkGenerator.Replan(finder, new Vector2(-20f, 120f), LeadIn(aim), new Vector2(-20f, 120f), 0f, site.Centre, ring, 4f, null, aim,
            startReached: true, endReached: RoadNetworkGenerator.EndReached(site.Centre, 10f));

        Assert.NotNull(retry);
        Vector2 end = retry!.Value.path[retry.Value.path.Count - 1];
        Assert.True(Vector2.Distance(end, aim) < 2f, $"the re-route ends at {end}, its ring point is {aim}");
    }
}
