using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase layout over level going (<see cref="RoadStairRuns.LevelGoing"/>) is not laid out: a long stretch whose
/// ground climbs little for its length is refused at once, as a stretch longer than any staircase is. Ends at a similar
/// height with a valley or a ridge between are climbing, and a road over a valley or a ridge keeps its staircases.
/// </summary>
public class FlatRunTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly bool _refuse = RoadStairRuns.RefuseLevelGoing;

    public FlatRunTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        RoadStairRuns.RefuseLevelGoing = _refuse;
        RoadStairRuns.ResetCache();
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>RoadStairRuns.Plan by reflection, as StairAllowanceTests calls it, with a corridor.</summary>
    private static StairPlan Plan(Vector2 a, Vector2 b, Func<float, float, float> ground, List<Vector2>? corridor)
    {
        MethodInfo plan = typeof(RoadStairRuns).GetMethod("Plan", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] given = { a, b, StairKit.Stone, RoadStairRuns.LayoutGround.Own(ground), null, null, corridor };
        var args = new object?[plan.GetParameters().Length];
        for (int i = 0; i < args.Length; i++) args[i] = i < given.Length ? given[i] : Type.Missing;
        return (StairPlan)plan.Invoke(null, args)!;
    }

    private static List<Vector2> Line(Vector2 a, Vector2 b, float every = 8f)
    {
        var line = new List<Vector2>();
        int n = Mathf.CeilToInt(Vector2.Distance(a, b) / every);
        for (int i = 0; i <= n; i++) line.Add(Vector2.Lerp(a, b, i / (float)n));
        return line;
    }

    /// <summary>A long run over a low bank: 200 m, the ground 40 m with a 3 m step half way (the cell step a road's search
    /// buys as a stair) and knee-high bumps. Laid out, it is mostly floors in a row and none is found; it is refused before
    /// any piece is searched.</summary>
    [Fact]
    public void ALongFlatRunIsRefusedWithoutASearch()
    {
        Func<float, float, float> bank = (x, z) => 40f + (x > 100f ? 3f : 0f) + 0.4f * Mathf.Sin(x * 0.3f);
        Vector2 a = new(0f, 0f), b = new(200f, 0f);

        Assert.True(RoadStairRuns.LevelGoing(a, b, Line(a, b), bank));
        RoadStairRuns.ResetCache();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        StairPlan plan = Plan(a, b, bank, Line(a, b));
        watch.Stop();

        Assert.False(plan.Reached);
        Assert.Equal(0, plan.Expansions);
        Assert.StartsWith("level going: ", plan.Reason);
        Assert.True(watch.ElapsedMilliseconds < 200, $"took {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>The same bank 60 m long is a short run of floors and a few stairs, and is laid out as before.</summary>
    [Fact]
    public void AShortFlatRunIsStillLaidOut()
    {
        Func<float, float, float> bank = (x, z) => 40f + (x > 30f ? 3f : 0f);
        Vector2 a = new(0f, 0f), b = new(60f, 0f);

        Assert.False(RoadStairRuns.LevelGoing(a, b, Line(a, b), bank));
    }

    /// <summary>Ends at the same height 120 m apart, a valley 24 m deep between: climbing, the ground's own climb
    /// and drop counted, not only the ends'.</summary>
    [Fact]
    public void SimilarEndsWithAValleyBetweenAreNotLevelGoing()
    {
        Func<float, float, float> valley = (x, z) => 40f - Mathf.Clamp(24f - Mathf.Abs(x - 60f) * 0.6f, 0f, 24f);
        Vector2 a = new(0f, 0f), b = new(120f, 0f);

        Assert.False(RoadStairRuns.LevelGoing(a, b, Line(a, b), valley));
    }

    /// <summary>Ends at the same height 120 m apart, a ridge 24 m high between: climbing, the ground's own climb
    /// and drop counted, not only the ends'.</summary>
    [Fact]
    public void SimilarEndsWithARidgeBetweenAreNotLevelGoing()
    {
        Func<float, float, float> ridge = (x, z) => 40f + Mathf.Clamp(24f - Mathf.Abs(x - 60f) * 0.6f, 0f, 24f);
        Vector2 a = new(0f, 0f), b = new(120f, 0f);

        Assert.False(RoadStairRuns.LevelGoing(a, b, Line(a, b), ridge));
    }

    /// <summary>A trench (valley) or a wall (ridge) 24 m deep or high across the whole world, faces of 1.2 no road climbs,
    /// level ground at 60 m either side (the valley floor above the water); sea past 200 m. The trench's floor is 20 m wide,
    /// the wall's top 4 m: 20 m wide, its staircases' tops joined across it walked 10 rows level on flat ground, longer than
    /// a rest and over no dip (StairLayout.PastARest), and no road crossed it; 8 m wide, its road was cut into it as a trench.</summary>
    private sealed class AcrossWorld : WorldGenerator
    {
        public float Sign;
        /// <summary>The wall's flat top, metres wide.</summary>
        public float Top = 4f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float d = Mathf.Abs(wx - 40f);
            return 60f + Sign * Mathf.Clamp(((Sign > 0f ? 20f + Top / 2f : 30f) - d) * 1.2f, 0f, 24f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 95;
    }

    /// <summary>The road's staircases over the world, level going refused or not: each run's ends.</summary>
    private List<string> Staircases(float sign, bool refuse)
    {
        RoadNetworkGenerator.Reset();
        var world = new AcrossWorld { Sign = sign };
        _scope.WithWorld(world);
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        RoadStairRuns.ResetCache();
        RoadStairRuns.RefuseLevelGoing = refuse;
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-60f, 0f), 0f, new Vector2(140f, 0f), 0f, 4f, "across"), "not built");
        return StairStructures.Runs.Select(r => FormattableString.Invariant($"{r.From.x:F1},{r.From.y:F1}->{r.To.x:F1},{r.To.y:F1}:{r.Pieces.Count}"))
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void SimilarEndsWithAValleyBetweenStillGetTheirStaircases()
    {
        List<string> off = Staircases(-1f, refuse: false);

        Assert.NotEmpty(off);
        Assert.Equal(off, Staircases(-1f, refuse: true));
    }

    [Fact]
    public void SimilarEndsWithARidgeBetweenStillGetTheirStaircases()
    {
        List<string> off = Staircases(1f, refuse: false);

        Assert.NotEmpty(off);
        Assert.Equal(off, Staircases(1f, refuse: true));
    }

    /// <summary>
    /// A ridge with a wide flat top, under the rule that no staircase walks level further than a rest (StairLayout.PastARest),
    /// pinned as the generator does it today. A top 20 m wide (the wall's before the rule) and one 32 m wide, both wider than a
    /// rest, are crossed as staircase, road, staircase: a staircase up each face to its rim and a road across the top between
    /// them (RoadStairRuns.RoadOverRidgeTops), never two staircases met on the crest (a boardwalk across the top a road can do
    /// the work of) nor one over both faces. Each staircase's level runs, read with the other's pieces as the road's final check
    /// reads staircases met end to end (StairEnds.LevelRuns), are a rest at most.
    /// </summary>
    [Theory]
    [InlineData(20f)]
    [InlineData(32f)]
    public void AWideFlatRidgeTopIsCrossedOnlyWideEnoughForARoadAcrossIt(float top)
    {
        RoadNetworkGenerator.Reset();
        var world = new AcrossWorld { Sign = 1f, Top = top };
        _scope.WithWorld(world);
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        RoadStairRuns.ResetCache();

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-60f, 0f), 0f, new Vector2(140f, 0f), 0f, 4f, "across"));
        Assert.Equal(2, StairStructures.Runs.Count);
        float Ground(float x, float z) => world.GetHeight(x, z);
        StairRun up = StairStructures.Runs[0], down = StairStructures.Runs[1];
        foreach (StairRun run in StairStructures.Runs)
        {
            // Its pieces on one face: where the road meets its top (a ring's radius past its exit) may be the top's middle.
            var walked = run.Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport).ToList();
            Assert.True(walked.All(p => p.Position.x < 40f) || walked.All(p => p.Position.x > 40f), "a staircase across the top");
            StairRun other = ReferenceEquals(run, up) ? down : up;
            Assert.All(StairEnds.LevelRuns(run, Ground, other.Pieces), l => Assert.InRange(l.Rows, 1, StairLayout.RestFloors));
        }
        // Not met on the crest: their walked pieces further apart than a rest (a run's ends are its road's path points, and
        // the road meets a top a ring's radius past its exit, so they may meet at the top's middle with the road between).
        var faces = StairStructures.Runs.Select(r => r.Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport).ToList()).ToList();
        float gap = faces.Single(w => w.All(p => p.Position.x > 40f)).Min(p => p.Position.x)
            - faces.Single(w => w.All(p => p.Position.x < 40f)).Max(p => p.Position.x);
        Assert.True(gap > StairLayout.RestFloors * StairLayout.PieceRun, $"tops {gap:F1} m apart: met on the crest");
        // And road across the top between them.
        bool road = false;
        for (float z = -60f; z <= 60f && !road; z += 1f) road = RoadSpatialGrid.TryGetRoadWithin(new Vector2(40f, z), 1f, out _);
        Assert.True(road, "no road across the top");
    }
}
