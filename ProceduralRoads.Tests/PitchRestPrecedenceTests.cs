using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

[Collection("RoadStatics")]
public class PitchRestPrecedenceTests
{
    [Fact]
    public void ASteeperClimbKeepsEveryRestEdgeWhenNoRestsWereDropped()
    {
        var points = Enumerable.Range(0, 217).Select(x => new Vector2(x, 0)).ToList();
        var low = RoadPitches.PlacePitches(points, points.Select(p => 40f + .20f*p.x).ToList(), .35f, null)!;
        RoadPitches.ResetCounters();
        var high = RoadPitches.PlacePitches(points, points.Select(p => 40f + .25f*p.x).ToList(), .35f, null)!;
        Assert.Equal(0, RoadPitches.ClimbsTooSteepForRests);
        var missing = Enumerable.Range(1, points.Count - 1).Where(i => low[i] <= .0901f && high[i] > .0901f).ToArray();
        var actualHeights=points.Select(p => 40f + .25f*p.x).ToList();
        Assert.True(RoadGrade.Limit(points,actualHeights,.35f,high));
        float worst=missing.Length == 0 ? 0 : missing.Max(i => Mathf.Abs(actualHeights[i]-actualHeights[i-1]));
        Assert.True(missing.Length == 0, $"{missing.Length} rest edges became steep pitches; worst resulting grade {worst:F3}; edges: {string.Join(",", missing)}");
    }
}
