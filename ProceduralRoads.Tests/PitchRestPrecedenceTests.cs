using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

[Collection("RoadStatics")]
public class PitchRestPrecedenceTests
{
    [Fact]
    public void LongerPitchesMustNotOverwriteTheNextRest()
    {
        var points = Enumerable.Range(0, 217).Select(x => new Vector2(x, 0)).ToList();
        var heights = points.Select(p => 40f + .25f * p.x).ToList();
        var shortPitch = RoadPitches.PlacePitches(points, heights, .35f, null, pitchLength: 8f)!;
        var longPitch = RoadPitches.PlacePitches(points, heights, .35f, null, pitchLength: 25f)!;
        var missing = Enumerable.Range(1, points.Count - 1).Where(i => shortPitch[i] <= .0901f && longPitch[i] > .0901f).ToArray();
        Assert.True(missing.Length == 0, $"{missing.Length} rest edges became steep pitches: {string.Join(",", missing)}");
    }

    [Fact]
    public void SteeperClimbKeepsEveryRestEdgeWhenNoRestsWereDropped()
    {
        var points = Enumerable.Range(0, 217).Select(x => new Vector2(x, 0)).ToList();
        var low = RoadPitches.PlacePitches(points, points.Select(p => 40f + .25f*p.x).ToList(), .35f, null)!;
        RoadPitches.ResetCounters();
        var high = RoadPitches.PlacePitches(points, points.Select(p => 40f + .35f*p.x).ToList(), .35f, null)!;
        Assert.Equal(0, RoadPitches.ClimbsThatLostEases);
        var missing = Enumerable.Range(1, points.Count - 1).Where(i => low[i] <= .0901f && high[i] > .0901f).ToArray();
        var actualHeights=points.Select(p => 40f + .35f*p.x).ToList();
        Assert.True(RoadGrade.Limit(points,actualHeights,RoadPitches.LimitCap(.35f,high),high));
        float worst=missing.Length == 0 ? 0 : missing.Max(i => Mathf.Abs(actualHeights[i]-actualHeights[i-1]));
        Assert.True(missing.Length == 0, $"{missing.Length} rest edges became steep pitches; worst resulting grade {worst:F3}; edges: {string.Join(",", missing)}");
    }
}
