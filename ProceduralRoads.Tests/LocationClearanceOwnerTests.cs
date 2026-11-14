using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>One location clearance for the searched and the final road line (RoadLocationProtection.BlocksRoute for the search,
/// BlocksRoad for every shaping step and the final check).</summary>
[Collection("RoadStatics")]
public class LocationClearanceOwnerTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight = 0; width = 0; }
    }

    private readonly ValheimWorldScope _scope = new ValheimWorldScope();
    private readonly Func<IEnumerable<RoadLocationProtection.Footprint>?>? previousSource = RoadLocationProtection.Source;
    private readonly FlatWorld world = new();

    public LocationClearanceOwnerTests()
    {
        _scope.WithWorld(world);
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Source = null;
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
    }

    [Fact]
    public void TheSearchMayArriveAtItsEndsLocationButNotLeaveItsClearanceAgain()
    {
        var end = new Vector2(0, 0);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(end, 10f) });
        var start = new Vector2(-100, 0);
        // Into the ring and on to the centre: the arrival, trimmed away.
        Assert.False(RoadLocationProtection.BlocksRoute(new Vector2(-20, 0), new Vector2(-8, 0), 4f, new RoadLocationProtection.Targets(null, end)));
        Assert.False(RoadLocationProtection.BlocksRoute(new Vector2(-8, 0), new Vector2(0, 0), 4f, new RoadLocationProtection.Targets(null, end)));
        // Out of the ring again (round the location to come in from its far side): kept by the trim, and refused at the end.
        Assert.True(RoadLocationProtection.BlocksRoute(new Vector2(-12, 6), new Vector2(-12, 20), 4f, new RoadLocationProtection.Targets(null, end)));
        Assert.True(RoadLocationProtection.BlocksRoute(new Vector2(-16, 12), new Vector2(16, 12), 4f, new RoadLocationProtection.Targets(null, end)));
        // The old exemption let both through; it stays for the callers that clip their own end (stairs at a location), for a
        // location on the preload list (RoadLocationProtection.Enterable) only.
        Assert.True(RoadLocationProtection.BlocksSegment(new Vector2(-16, 12), new Vector2(16, 12), 4f, new RoadLocationProtection.Targets(null, end)));
        PlacedLocations.Preload(end);
        Assert.False(RoadLocationProtection.BlocksSegment(new Vector2(-16, 12), new Vector2(16, 12), 4f, new RoadLocationProtection.Targets(null, end)));
    }

    [Fact]
    public void TheSearchLeavesItsStartsLocationOnceAndDoesNotComeBack()
    {
        var start = new Vector2(0, 0);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(start, 10f) });
        var end = new Vector2(100, 0);
        Assert.False(RoadLocationProtection.BlocksRoute(new Vector2(0, 0), new Vector2(8, 0), 4f, new RoadLocationProtection.Targets(start, null)));
        Assert.False(RoadLocationProtection.BlocksRoute(new Vector2(8, 0), new Vector2(20, 0), 4f, new RoadLocationProtection.Targets(start, null)));
        Assert.True(RoadLocationProtection.BlocksRoute(new Vector2(-16, 12), new Vector2(16, 12), 4f, new RoadLocationProtection.Targets(start, null)));
    }

    [Fact]
    public void ASwayedLegIntoALocationsClearanceLosesItsSwayThereOnly()
    {
        var even = Enumerable.Range(0, 11).Select(i => new Vector2(i * 12, 0)).ToList();
        var swayed = even.ToList();
        swayed[2] = new Vector2(24, 6);
        swayed[5] = new Vector2(60, 6);
        // Clear of the unswayed line by a metre, and of the swayed point (and its sideways step, all the sway was checked
        // for before), but not of the leg from it to the next point.
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(66, 6), 1f) });
        Assert.True(RoadLocationProtection.FirstBlockedLeg(swayed, 4f, null, even[0], even[10], default) > 0);
        Assert.False(RoadLocationProtection.BlocksSegment(even[5], swayed[5], RoadLocationProtection.Clearance(4f), default));

        var kept = RoadWiggle.KeepClearOfLocations(swayed.ToList(), even, even, 4f);

        Assert.NotNull(kept);
        Assert.Equal(-1, RoadLocationProtection.FirstBlockedLeg(kept!, 4f, null, even[0], even[10], default));
        Assert.Equal(even[5], kept![5]);
        Assert.Equal(new Vector2(24, 6), kept[2]);
    }

    [Fact]
    public void ASwayWhoseUnswayedLegsCutALocationsClearanceIsDropped()
    {
        var even = Enumerable.Range(0, 11).Select(i => new Vector2(i * 12, 0)).ToList();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(60, 3), 1f) });
        Assert.Null(RoadWiggle.KeepClearOfLocations(even.ToList(), even, even, 4f));
    }

    [Fact]
    public void ASplineThatWouldSwingIntoALocationsClearanceKeepsToItsLegsAndIsBuilt()
    {
        // A right-angle corner: the spline swings about 3 m out past the second leg a third of the way along it, onto a
        // location the leg itself keeps 1 m clear of. The final check refused this road.
        var path = new List<Vector2> { new(-40, 0), new(0, 0), new(0, 40) };
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(6, 12), 1f) });
        var plan = RoadSpatialGrid.PlanRoadPath(path, 4, world);
        Assert.True(plan != null, RoadSpatialGrid.LastRefusal);
        Assert.Equal(-1, RoadLocationProtection.FirstBlockedLeg(plan!.Points, 4f, plan.Widths, path[0], path[2], default));
    }

    [Fact]
    public void APullThatWouldCutALocationsClearanceIsNotMade()
    {
        // A dog-leg round a location: pulled straight, the road would run 2 m from it.
        var path = new List<Vector2> { new(0, 0), new(10, 0), new(20, 10), new(30, 0), new(40, 0) };
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(20, 2), 1f) });
        Assert.Equal(-1, RoadLocationProtection.FirstBlockedLeg(path, 4f, null, path[0], path[4], default));
        using (StaticOverride.Set(() => RoadPathPull.Tolerance, 0f))
        {
            var pulled = RoadPathPull.Pull(path, 4f, _ => 40f, _ => false, 1f);
            Assert.Equal(-1, RoadLocationProtection.FirstBlockedLeg(pulled, 4f, null, path[0], path[4], default));
            Assert.Contains(new Vector2(20, 10), pulled);
        }
    }

    public void Dispose()
    {
        PlacedLocations.Reset();
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Reset();
        RoadLocationProtection.Source = previousSource;
        _scope.Dispose();
    }
}
