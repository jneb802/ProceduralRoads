using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// One classification of a staircase's foot (RoadStairRuns.Lay, decision 6af): a staircase down from a crest another
/// staircase met is finished at its foot onto the road that carries on past it, as any staircase's own foot is (its stairs
/// carried on down toward the road, StairLayout.LowerFoot), its crest kept as it was met; with no road past its foot, it
/// is finished onto nothing. Before, the crest's meet kept its foot from being lowered while the span still called the
/// foot its own road's (RoadStairSpan.OwnFoot).
/// </summary>
[Collection("StairLayout")]
public class StairFootClassTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public StairFootClassTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); StairStructures.Reset(); }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        _scope.Dispose();
    }

    /// <summary>A sharp ridge along z (faces of 1.2 up to a crest line at x = 0, 24 m over flats at 40), its east flats falling
    /// away at <see cref="Fall"/> for 20 m from 4 m past the foot of its face: the staircase down from the crest comes down to the
    /// flat by the road, which falls away from its foot further than the road climbs to it, against the face's bank.</summary>
    private sealed class CrestOverAFallingRoadWorld : WorldGenerator
    {
        public float FallFrom = 24f, Fall = 0.45f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float h = 40f + Mathf.Clamp(24f - Mathf.Abs(wx) * 1.2f, 0f, 24f);
            return wx > FallFrom ? h - Fall * Mathf.Min(wx - FallFrom, 20f) : h;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>The ridge's staircases laid over the road's path through <paramref name="xs"/> (on z = 0), the steps where the
    /// path climbs past 0.5; the span coming down from the crest.</summary>
    private (List<RoadStairSpan> spans, RoadStairSpan down, string said) LayOver(WorldGenerator world, params float[] xs)
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        _scope.WithWorld(world);
        var path = xs.Select(x => new Vector2(x, 0f)).ToList();
        var steps = new List<(Vector2 from, Vector2 to)>();
        for (int i = 1; i < path.Count; i++)
            if (Mathf.Abs(world.GetHeight(path[i].x, 0f) - world.GetHeight(path[i - 1].x, 0f)) / Vector2.Distance(path[i - 1], path[i]) > 0.5f)
                steps.Add((path[i - 1], path[i]));
        List<RoadStairSpan>? spans = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world);
        Assert.True(spans != null, $"not laid: {RoadStairRuns.LastRefusal} at {RoadStairRuns.LastFailedStretch}");
        string said = string.Join(" | ", spans!.Select(s => FormattableString.Invariant(
            $"{s.FromIndex}..{s.ToIndex} rev {s.Plan.Reversed} topMet {s.TopMet} lowered {s.Plan.FootLowered} apron {s.Plan.FootApron} noRoad {s.NoRoadAtFoot} foot {s.Plan.Foot}")));
        RoadStairSpan? down = spans.Find(s => s.TopMet && s.Plan.Reversed);
        Assert.True(down != null, "no staircase down from the crest: " + said);
        return (spans, down!, said);
    }

    /// <summary>The fall past the flat at 55 %, steeper than a flight: the staircase down from the crest is not laid on down it
    /// from the foot started lower (RoadStairRuns.StartLower), so it ends at the flat and its foot is lowered toward the road.
    /// At 45 % it was so too while a staircase walked every point of its road's path; it is now laid on down the fall
    /// (<see cref="AStaircaseDownFromACrestIsLaidOnDownAFallGentlerThanAFlightToItsRoad"/>).</summary>
    [Fact]
    public void AStaircaseDownFromACrestIsFinishedOntoTheRoadPastItsFootWithItsCrestKept()
    {
        var world = new CrestOverAFallingRoadWorld { Fall = 0.55f };
        var (spans, down, said) = LayOver(world, -48f, -40f, -32f, -24f, -16f, -8f, 0f, 8f, 16f, 24f, 32f, 40f, 48f);
        // Its foot carried on down toward the road past it, a bank cut for the road to meet it, and the span's foot its own road's.
        Assert.True(down.Plan.FootLowered > 0, "the foot down from the crest was not lowered toward its road: " + said);
        Assert.True(down.Plan.FootApron > 0, "no bank cut at the foot down from the crest: " + said);
        Assert.False(down.NoRoadAtFoot, said);
        Assert.True(down.OwnFoot != null, "no own foot: " + said);
        // Its crest kept as it was met: the staircase up to it still steps onto it.
        RoadStairSpan up = spans[spans.IndexOf(down) - 1];
        Func<float, float, float> ground = (x, z) => RoadStairRuns.GroundAt(x, z, world);
        StairRun first = StairRun.FromPlan(up.FromPoint, up.ToPoint, RoadStairRuns.Width, up.Plan, up.Kit);
        StairRun second = StairRun.FromPlan(down.FromPoint, down.ToPoint, RoadStairRuns.Width, down.Plan, down.Kit);
        StairEnds.End top = StairEnds.Read(first, ground, second.Pieces).Single(e => e.Top);
        Assert.True(top.Joined, "the crest is not joined: " + StairEnds.Line(top) + "; " + said);

        // No road past its foot (the road's path ends there): finished onto nothing, no foot lowered, no bank cut.
        var (_, alone, saidAlone) = LayOver(world, -48f, -40f, -32f, -24f, -16f, -8f, 0f, 8f, 16f, 24f);
        Assert.Equal(0, alone.Plan.FootLowered);
        Assert.Equal(0, alone.Plan.FootApron);
        Assert.True(alone.NoRoadAtFoot, saidAlone);
        Assert.Null(alone.OwnFoot);
    }

    /// <summary>The fall past the flat at 45 %, gentler than a flight: the staircase down from the crest is laid on down it to
    /// the road from the foot started lower (RoadStairRuns.StartLower), straight down the road's line (StairLayout.FollowedRoute),
    /// its foot on the road's ground there with none left to lower, and its crest kept as it was met. Walked through every point
    /// of the road's path, it was not laid from the lower foot, and the foot kept at the flat was lowered instead.</summary>
    [Fact]
    public void AStaircaseDownFromACrestIsLaidOnDownAFallGentlerThanAFlightToItsRoad()
    {
        var world = new CrestOverAFallingRoadWorld { Fall = 0.45f };
        var (spans, down, said) = LayOver(world, -48f, -40f, -32f, -24f, -16f, -8f, 0f, 8f, 16f, 24f, 32f, 40f, 48f);
        Assert.True(down.Plan.Foot is Vector3 foot && foot.x > world.FallFrom + 8f, "not laid on down the fall: " + said);
        Vector3 at = down.Plan.Foot!.Value;
        Assert.True(Mathf.Abs(at.y - world.GetHeight(at.x, at.z)) <= StairLayout.PieceRise, "its foot is off the road's ground: " + said);
        Assert.Equal(0, down.Plan.FootLowered);
        Assert.False(down.NoRoadAtFoot, said);
        RoadStairSpan up = spans[spans.IndexOf(down) - 1];
        Func<float, float, float> ground = (x, z) => RoadStairRuns.GroundAt(x, z, world);
        StairRun first = StairRun.FromPlan(up.FromPoint, up.ToPoint, RoadStairRuns.Width, up.Plan, up.Kit);
        StairRun second = StairRun.FromPlan(down.FromPoint, down.ToPoint, RoadStairRuns.Width, down.Plan, down.Kit);
        StairEnds.End top = StairEnds.Read(first, ground, second.Pieces).Single(e => e.Top);
        Assert.True(top.Joined, "the crest is not joined: " + StairEnds.Line(top) + "; " + said);
    }
}
