using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A location's approach is improved (RoadLocationApproach.Improve) by its real caller, the planning pipeline
/// (RoadNetworkGenerator.PlanFromPath): a candidate that ranks better replaces the road's own approach only where the whole
/// road with it is built (RoadNetworkGenerator.PlanCandidate, then BuiltRoadRefusal); with none built the road's own approach
/// goes on to the same planning, which refuses it where it is not built either.
/// </summary>
[Collection("RoadStatics")]
public sealed class LocationApproachAcceptanceTests : IDisposable
{
    /// <summary>A ridge east of the location at the origin: the approach from the east goes round it, north or south alike.</summary>
    private sealed class Hillside : WorldGenerator
    {
        public override float GetHeight(float x, float z) =>
            60f + 8f * (float)Math.Exp(-Math.Pow((x - 28f) / 16f, 2) - Math.Pow(z / 7f, 2));
    }

    private readonly WorldGenerator _world = new Hillside();
    private readonly ValheimWorldScope _scope;
    private readonly IDisposable _source = StaticOverride.Keep(() => LocationLevelling.Source);
    private readonly IDisposable _grade = GradeCap.At(0.35f);

    public LocationApproachAcceptanceTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope = new ValheimWorldScope().WithWorld(_world);
        // The location levels the ground round its centre, so its approach is improved.
        LocationLevelling.Source = c => c.magnitude < 0.5f ? new[] { new LevelOp(0, 0, 0f, 8, false) } : null;
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        _grade.Dispose();
        _source.Dispose();
        _scope.Dispose();
    }

    /// <summary>The road from 200 m east to the location's keep-out edge, planned as generation plans it.</summary>
    private RoadNetworkGenerator.RoadPlan Planned()
    {
        var path = new List<Vector2>();
        for (int x = 200; x >= 0; x -= 8) path.Add(new Vector2(x, 0));
        return RoadNetworkGenerator.PlanFromPath(new RoadPathfinder(_world), path, path[0], 0f, new Vector2(0f, 0f), 16f,
            false, false, true, false, 4f);
    }

    /// <summary>
    /// The best-ranked approach round the ridge (south of it) crosses another road; the one north of it ranks as well and
    /// crosses none. The road takes the north one and is built. Before, the best-ranked approach was taken unjudged and the
    /// whole road was refused for crossing the other road.
    /// </summary>
    [Fact]
    public void AnApproachTheWholeRoadIsRefusedWithGivesWayToOneItIsBuiltWith()
    {
        var other = new List<Vector2>();
        for (float t = 0f; t <= 16f; t += 4f) other.Add(new Vector2(32f, -12f - t));
        RoadSpatialGrid.Commit(RoadSpatialGrid.PlanRoadPath(other, 4, _world)!);
        RoadSpatialGrid.FinalizeRoadNetwork();

        var planned = Planned();
        Assert.True(planned.Plans != null, RoadSpatialGrid.LastRefusal);
        string? why = RoadNetworkGenerator.BuiltRoadRefusal(planned.Plans!, planned.Crossings, out Vector2 at);
        Assert.True(why == null, $"{why} at {at}");
        Vector2 end = planned.Path[planned.Path.Count - 1];
        Assert.True(end.y > 5f, $"the approach ends at {end}, not round the ridge's north side");
    }

    /// <summary>
    /// Control: a road whose own route crosses a protected location before its approach is built with no approach: every
    /// candidate's road crosses it too, and the road's own approach, planned by the same continuation, is refused.
    /// </summary>
    [Fact]
    public void ARoadNoApproachIsBuiltWithIsRefusedWithItsOwn()
    {
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(120f, 0f), 6f) });
        try { Assert.Null(Planned().Plans); }
        finally { RoadLocationProtection.Reset(); }
    }
}
