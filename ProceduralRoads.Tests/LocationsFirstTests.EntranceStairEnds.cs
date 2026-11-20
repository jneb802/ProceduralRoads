using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase onto a placed location's measured entrance is met at its ends as it is planned (RoadNetworkGenerator.EntryStairsEnds:
/// the one finished-connection verdict the road's final check gives it, RoadStairRuns.ConnectionRefusal and the stair-end audit's
/// reader, StairEnds.Read): one with no way off an end is a failed layout, and the arrival falls back.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>A Mistlands tower on a plateau, its entrance on top and its fallback with it (as a placed tower has), every
    /// staircase's top read with no way off it by the end reader. The staircase onto the entrance is refused as it is planned,
    /// not at the road's final check: the road is built, falling back past the entrance onto the tower's fallback landing (its own last
    /// stretch climbing onto it), with no staircase laid. Refused only at the final check, the road was refused with it and
    /// the tower left unreached.</summary>
    [Fact]
    public void AnEntranceStaircaseWithNoWayOffIsRefusedWhilePlanningAndTheArrivalFallsBack()
    {
        _world.Biome = Heightmap.Biome.Mistlands;
        PlacedLocation tower = TowerOnAPlateau(withEntrance: true, withFallback: true);
        int judged = 0;
        RoadStairRuns.EndsJudged = (run, ends) =>
        {
            foreach (StairEnds.End end in ends)
                if (end.Top) { end.Verdict = StairEnds.Verdict.Miss; end.Why = "no way off (the test's)"; judged++; }
        };
        try
        {
            bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the tower");
            Assert.True(judged > 0, "no staircase onto the entrance was laid out to judge");
            Assert.True(built, "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
            Assert.Empty(StairStructures.Runs);
            // Fallen back onto the tower's fallback landing, the road climbing onto it as its own last stretch.
            Assert.True(PlacedLocations.Landed(tower.Centre));
            Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
            Assert.True(RoadNetworkGenerator.EntryStairsEndsRefused > 0, "no staircase was refused as it was planned");
        }
        finally { RoadStairRuns.EndsJudged = null; }
    }

    /// <summary>Rule 13, one verdict on every path: the staircase onto the entrance judged as it is planned is the one the
    /// road's final check judges and the one laid, piece for piece, read by the same reader (RoadStairRuns.NoWayOff).</summary>
    [Fact]
    public void AnEntranceStaircaseIsJudgedWhilePlanningAsTheFinalCheckJudgesAndLaysIt()
    {
        _world.Biome = Heightmap.Biome.Mistlands;
        TowerOnAPlateau(withEntrance: true, withFallback: true);
        var judged = new List<List<Vector3>>();
        RoadStairRuns.EndsJudged = (run, ends) => judged.Add(run.Pieces.ConvertAll(p => p.Position));
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the tower"),
                "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
        }
        finally { RoadStairRuns.EndsJudged = null; }
        StairRun laid = Assert.Single(StairStructures.Runs);
        Assert.Equal(1, RoadNetworkGenerator.EntryStairLayouts);
        Assert.Equal(0, RoadNetworkGenerator.EntryStairsEndsRefused);
        // Judged twice: as it was planned (EntryStairsEnds), then by the road's final check (RoadRefusal).
        Assert.Equal(2, judged.Count);
        foreach (List<Vector3> pieces in judged)
        {
            Assert.Equal(laid.Pieces.Count, pieces.Count);
            for (int i = 0; i < pieces.Count; i++) Assert.True(Vector3.Distance(pieces[i], laid.Pieces[i].Position) < 1e-3f, $"piece {i}: {pieces[i]} vs {laid.Pieces[i].Position}");
        }
    }
}
