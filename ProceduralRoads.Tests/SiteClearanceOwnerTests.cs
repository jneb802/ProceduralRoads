using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>One site clearance for the searched and the final road line (RoadSiteProtection.BlocksRoute for the search,
/// BlocksRoad for every shaping step and the final check).</summary>
[Collection("RoadStatics")]
public class SiteClearanceOwnerTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight = 0; width = 0; }
    }

    private readonly ValheimWorldScope _scope = new ValheimWorldScope();
    private readonly Func<IEnumerable<RoadSiteProtection.Footprint>?>? previousSource = RoadSiteProtection.Source;
    private readonly FlatWorld world = new();

    public SiteClearanceOwnerTests()
    {
        _scope.WithWorld(world);
        RoadSpatialGrid.Clear();
        RoadSiteProtection.Source = null;
        RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
    }

    [Fact]
    public void TheSearchMayArriveAtItsEndsPlaceButNotLeaveItsClearanceAgain()
    {
        var end = new Vector2(0, 0);
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(end, 10f) });
        var start = new Vector2(-100, 0);
        // Into the ring and on to the centre: the arrival, trimmed away.
        Assert.False(RoadSiteProtection.BlocksRoute(new Vector2(-20, 0), new Vector2(-8, 0), 4f, start, end));
        Assert.False(RoadSiteProtection.BlocksRoute(new Vector2(-8, 0), new Vector2(0, 0), 4f, start, end));
        // Out of the ring again (round the place to come in from its far side): kept by the trim, and refused at the end.
        Assert.True(RoadSiteProtection.BlocksRoute(new Vector2(-12, 6), new Vector2(-12, 20), 4f, start, end));
        Assert.True(RoadSiteProtection.BlocksRoute(new Vector2(-16, 12), new Vector2(16, 12), 4f, start, end));
        // The old exemption let both through; it stays for the callers that clip their own end (stairs at a place).
        Assert.False(RoadSiteProtection.BlocksSegment(new Vector2(-16, 12), new Vector2(16, 12), 4f, start, end));
    }

    [Fact]
    public void TheSearchLeavesItsStartsPlaceOnceAndDoesNotComeBack()
    {
        var start = new Vector2(0, 0);
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(start, 10f) });
        var end = new Vector2(100, 0);
        Assert.False(RoadSiteProtection.BlocksRoute(new Vector2(0, 0), new Vector2(8, 0), 4f, start, end));
        Assert.False(RoadSiteProtection.BlocksRoute(new Vector2(8, 0), new Vector2(20, 0), 4f, start, end));
        Assert.True(RoadSiteProtection.BlocksRoute(new Vector2(-16, 12), new Vector2(16, 12), 4f, start, end));
    }

    [Fact]
    public void ASwayedLegIntoASitesClearanceLosesItsSwayThereOnly()
    {
        var even = Enumerable.Range(0, 11).Select(i => new Vector2(i * 12, 0)).ToList();
        var swayed = even.ToList();
        swayed[2] = new Vector2(24, 6);
        swayed[5] = new Vector2(60, 6);
        // Clear of the unswayed line by a metre, and of the swayed point (and its sideways step, all the sway was checked
        // for before), but not of the leg from it to the next point.
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(66, 6), 1f) });
        Assert.True(RoadSiteProtection.FirstBlockedLeg(swayed, 4f, null, even[0], even[10]) > 0);
        Assert.False(RoadSiteProtection.BlocksSegment(even[5], swayed[5], RoadSiteProtection.Clearance(4f), null, null));

        var kept = RoadWiggle.KeepClearOfSites(swayed.ToList(), even, even, 4f);

        Assert.NotNull(kept);
        Assert.Equal(-1, RoadSiteProtection.FirstBlockedLeg(kept!, 4f, null, even[0], even[10]));
        Assert.Equal(even[5], kept![5]);
        Assert.Equal(new Vector2(24, 6), kept[2]);
    }

    [Fact]
    public void ASwayWhoseUnswayedLegsCutASitesClearanceIsDropped()
    {
        var even = Enumerable.Range(0, 11).Select(i => new Vector2(i * 12, 0)).ToList();
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(60, 3), 1f) });
        Assert.Null(RoadWiggle.KeepClearOfSites(even.ToList(), even, even, 4f));
    }

    [Fact]
    public void ASplineThatWouldSwingIntoASitesClearanceKeepsToItsLegsAndIsBuilt()
    {
        // A right-angle corner: the spline swings about 3 m out past the second leg a third of the way along it, onto a
        // site the leg itself keeps 1 m clear of. The final check refused this road.
        var path = new List<Vector2> { new(-40, 0), new(0, 0), new(0, 40) };
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(6, 12), 1f) });
        var plan = RoadSpatialGrid.PlanRoadPath(path, 4, world);
        Assert.True(plan != null, RoadSpatialGrid.LastRefusal);
        Assert.Equal(-1, RoadSiteProtection.FirstBlockedLeg(plan!.Points, 4f, plan.Widths, path[0], path[2]));
    }

    [Fact]
    public void APullThatWouldCutASitesClearanceIsNotMade()
    {
        // A dog-leg round a site: pulled straight, the road would run 2 m from it.
        var path = new List<Vector2> { new(0, 0), new(10, 0), new(20, 10), new(30, 0), new(40, 0) };
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(20, 2), 1f) });
        Assert.Equal(-1, RoadSiteProtection.FirstBlockedLeg(path, 4f, null, path[0], path[4]));
        using (StaticOverride.Set(() => RoadPathPull.Tolerance, 0f))
        {
            var pulled = RoadPathPull.Pull(path, 4f, _ => 40f, _ => false, 1f);
            Assert.Equal(-1, RoadSiteProtection.FirstBlockedLeg(pulled, 4f, null, path[0], path[4]));
            Assert.Contains(new Vector2(20, 10), pulled);
        }
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadSiteProtection.Reset();
        RoadSiteProtection.Source = previousSource;
        _scope.Dispose();
    }
}
