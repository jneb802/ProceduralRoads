using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A later road's ridge or valley join attaches to a staircase an earlier road built as it is, or is refused: it never
/// edits that staircase's plan. Its StairRun was copied when it joined the network, so an
/// edit (a crest floor carried on, its foot's sunk step dropped, its flight cut back under a span) reached the plan only:
/// the world kept the old pieces, with a floor's gap or an overlap at the join.
/// </summary>
public class StairJoinTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>A sharp ridge along z at x = 0, faces of 1.2 rising 24 m, flats at 40 beyond, sea past 200 m.</summary>
    private sealed class SharpRidgeWorld : WorldGenerator
    {
        public bool Valley;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float bump = Mathf.Clamp(24f - Mathf.Abs(wx) * 1.2f, 0f, 24f);
            return Valley ? 64f - bump : 40f + bump;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    public StairJoinTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    private static string Pieces(StairPlan plan) =>
        string.Join(" ", plan.Pieces.Select(p => FormattableString.Invariant($"{p.Kind.ToString()[0]}({p.Position.x:F2},{p.Position.y:F2},{p.Position.z:F2})")));

    /// <summary>The road's staircases laid and accepted, as RoadNetworkGenerator commits them.</summary>
    private static void Accept(List<Vector2> path, List<RoadStairSpan> spans)
    {
        foreach (RoadStairSpan s in spans)
        {
            if (s.Existing) continue;
            var run = StairRun.FromPlan(path[s.FromIndex], path[s.ToIndex], RoadStairRuns.Width, s.Plan);
            StairStructures.Add(run);
            StairStructures.MarkStanding(run);
            RoadStairRuns.Laid(run, s.Plan, s.Kit);
        }
    }

    /// <summary>A road down into a sharp valley (or up to a sharp ridge) from the west, its staircase accepted; then a road on
    /// over to the east, walking that staircase and meeting it at the bottom (or the crest) with its own.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALaterRoadThroughAValleyOrOverARidgeLeavesAnEarlierRoadsStaircaseAsItIs(bool valley)
    {
        var world = new SharpRidgeWorld { Valley = valley };
        _scope.WithWorld(world);
        var path = new List<Vector2>();
        for (float x = -40f; x <= 40.01f; x += 8f) path.Add(new Vector2(x, 0f));
        var steps = new List<(Vector2 from, Vector2 to)>();
        for (int i = 1; i < path.Count; i++)
            if (Mathf.Abs(world.GetHeight(path[i].x, 0f) - world.GetHeight(path[i - 1].x, 0f)) / 8f > 0.5f) steps.Add((path[i - 1], path[i]));
        int crest = path.FindIndex(p => Mathf.Abs(p.x) < 0.01f);
        List<Vector2> west = path.GetRange(0, crest + 1);

        List<RoadStairSpan>? first = RoadStairRuns.Lay(west, steps, new List<RoadCrossing>(), world);
        Assert.NotNull(first);
        Accept(west, first!);
        StairRun run = Assert.Single(StairStructures.Runs);
        StairPlan laid = RoadStairRuns.LaidPlan(run)!;
        string before = Pieces(laid);
        bool floored = laid.CrestFloored;

        List<RoadStairSpan>? second = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world);

        Assert.True(second != null && second.Count == 2 && second[0].Existing, "the second road did not walk the first's staircase: "
            + (second == null ? "not laid" : string.Join(" | ", second.Select(s => $"{s.FromIndex}..{s.ToIndex} existing {s.Existing}"))));
        Assert.Equal(before, Pieces(RoadStairRuns.LaidPlan(run)!));
        Assert.Equal(floored, RoadStairRuns.LaidPlan(run)!.CrestFloored);
    }

    /// <summary>The road generation panel counts a staircase when it joins the network, once, on the island laying it.</summary>
    [Fact]
    public void ALaidStaircaseIsCountedOnTheIslandsProgressRow()
    {
        var world = new SharpRidgeWorld { Valley = true };
        _scope.WithWorld(world);
        var path = new List<Vector2>();
        for (float x = -40f; x <= 0.01f; x += 8f) path.Add(new Vector2(x, 0f));
        var steps = new List<(Vector2 from, Vector2 to)>();
        for (int i = 1; i < path.Count; i++)
            if (Mathf.Abs(world.GetHeight(path[i].x, 0f) - world.GetHeight(path[i - 1].x, 0f)) / 8f > 0.5f) steps.Add((path[i - 1], path[i]));
        List<RoadStairSpan>? spans = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world);
        Assert.NotNull(spans);

        var owner = new RoadIslandActivity(1, 1);
        RoadNetworkGenerator.IslandActivity = owner.Claim(1, 1);
        try { Accept(path, spans!); }
        finally { RoadNetworkGenerator.IslandActivity = null; }

        Assert.Equal(StairStructures.Runs.Count(), owner.Snapshot()[0].Staircases);
        Assert.True(owner.Snapshot()[0].Staircases > 0);
    }
}
