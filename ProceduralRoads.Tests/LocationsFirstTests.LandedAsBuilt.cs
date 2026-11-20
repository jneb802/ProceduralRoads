using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// What counts as reached is read from what was built (RoadNetworkGenerator.LandedAsBuilt), and both log lines of a destination
/// print one verdict (EntranceArrivals.ArrivalRung). In game a staircase onto a troll cave's landing (TrollCave02 at (1150.4, 1917.8),
/// its landing (1149.5, 1917.7) at 56.36) recorded its end at the landing while its pieces and the road stopped 30 m short at x 1121,
/// 2.1 m under it: the cave was counted reached, "[ENTRANCE] reached, its way onto the landing laid" beside "[ARRIVAL] fallback".
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>A way onto a landing laid but stopping 30 m short of it and 2.12 m under it (as the game's staircase: its last piece at
    /// 54.24, the landing at 56.36) does not land the Location nor record an arrival; a way that ends on the landing, level with it, does,
    /// a wade's painted bed (a sunken crypt's way in over its ford) as any road.</summary>
    [Fact]
    public void AWayOntoALandingThatStopsShortOfItAsBuiltDoesNotLandTheLocation()
    {
        PlacedLocation cave = Cave();
        PlacedLocations.Ready(cave);
        RoadSpatialGrid.Clear();
        EntranceArrivals.Reset();
        Vector2 landing = cave.Shape.Landing;
        float level = cave.Ground(landing.x, landing.y);
        Vector2 back = new Vector2(1f, 0f);
        // The way as built: up to 30 m short of the landing, 2.12 m under it.
        var shortPoints = Enumerable.Range(0, 22).Select(k => landing + back * (51f - k)).ToList();
        RoadSpatialGrid.CommitLevelled(shortPoints, shortPoints.ConvertAll(_ => level - 2.12f), RoadNetworkGenerator.RoadWidth);
        RoadSpatialGrid.FinalizeRoadNetwork();

        RoadNetworkGenerator.LandedAsBuilt(cave, null, landing, null);

        Assert.False(PlacedLocations.Landed(cave.Centre), "a way that stops 30 m short of its landing does not land the Location");
        Assert.Null(RoadNetworkGenerator.ReachedTarget(cave));

        // A way that ends on the landing, level with it, lands it: here a wade's painted bed, the ground's own surface.
        var onto = Enumerable.Range(0, 6).Select(k => landing + back * (5f - k)).ToList();
        var painted = new RoadSpatialGrid.PlannedPath(onto, onto.ConvertAll(p => cave.Ground(p.x, p.y)), onto.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, onto.Count - 1, true);
        RoadSpatialGrid.CommitRoad(new List<RoadSpatialGrid.PlannedPath> { painted }, intoLocation: true);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkGenerator.LandedAsBuilt(cave, null, landing, null);
        Assert.True(PlacedLocations.Landed(cave.Centre));
    }

    /// <summary>The [ARRIVAL] and [ENTRANCE] lines print one verdict, read from what was laid: a road only planned records nothing;
    /// a laid way onto a landing gives its road's rung; a road laid with its end kept at the ring gives its ring rung ("ring" where
    /// none was recorded); a destination the network does not reach is unreached whatever was laid.</summary>
    [Fact]
    public void BothArrivalLinesPrintTheVerdictOfWhatWasLaid()
    {
        PlacedLocation cave = Cave();
        EntranceArrivals.Reset();
        var none = (entrance: -1, why: "", rung: (string?)null);
        Assert.Equal("ring", EntranceArrivals.ArrivalRung(cave.Centre, true, false, none));
        Assert.Equal("unreached", EntranceArrivals.ArrivalRung(cave.Centre, false, false, none));
        EntranceArrivals.Ring(cave, "unreached");
        Assert.Equal("ring", EntranceArrivals.ArrivalRung(cave.Centre, true, false, none));
        EntranceArrivals.Ring(cave, "level-ring");
        Assert.Equal("level-ring", EntranceArrivals.ArrivalRung(cave.Centre, true, false, none));
        Assert.Equal("level", EntranceArrivals.ArrivalRung(cave.Centre, true, true, (0, "", "level")));
        Assert.Equal("unreached", EntranceArrivals.ArrivalRung(cave.Centre, false, true, (0, "", "level")));
    }
}
