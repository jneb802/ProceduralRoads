using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road whose last stretch is its own crossing out to its end at a Location's ring (RoadNetworkGenerator.RoadEnd): in game two
/// SunkenCrypt4s' roads ended on a raised ford to their ring end ((-815.1, -3086.8): the ford from (-823.3, -3062.5), the road's end,
/// to (-821.0, -3053.9); (2957.9, -455.5) likewise), the road's pieces stopping at the ford's near bank: "no road at the end", no way
/// onto the landing tried, the crypt kept at its ring.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The road across the swamp to its ring end 22 m out, its last three waypoints a raised ford whose structure is its own
    /// (the road's pieces stop at its near bank): the road ends on the ford, and its way onto the landing is planned from there.</summary>
    [Fact]
    public void ARoadEndingOnItsOwnFordAtTheRingIsCarriedOnOntoTheLanding()
    {
        PlacedLocation crypt = SunkenCryptInASwamp();
        var (path, plans) = RoadAcrossTheSwamp();
        RoadSpatialGrid.PlannedPath whole = plans[0];
        Vector2 nearBank = path[path.Count - 4];
        int upTo = whole.Points.FindIndex(p => Vector2.Distance(p, nearBank) < 0.5f);
        Assert.True(upTo > 0);
        plans[0] = whole.Slice(0, upTo);
        var ford = new RoadCrossing
        {
            Kind = CrossingKind.Ford, Style = FordStyle.Raise, Shallow = true, FromIndex = path.Count - 4, ToIndex = path.Count - 1,
            FromBank = nearBank, ToBank = path[path.Count - 1], RiverbedHeight = SwampShallows,
        };
        var crossings = new List<RoadCrossing> { ford };

        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, path, plans, new List<RoadStairSpan>(), crossings, RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why);

        Assert.DoesNotContain("no road at the end", why);
        Assert.True(entered && (road != null || span != null), why);
    }
}
