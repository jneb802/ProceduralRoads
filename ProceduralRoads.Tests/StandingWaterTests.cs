using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class StandingWaterTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithNetwork(server: true);

    public StandingWaterTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); RoadNetworkPersistence.Reset(); _scope.Dispose(); }

    // A level pool with gentle banks. The wet run is exactly Length metres; the river control changes only its weight.
    private sealed class Pool : WorldGenerator
    {
        public float Length = 16f, Depth = 0.4f, BankRise, BankSlope = 0.1f;
        public bool River, Pocket, SideChannel;
        /// <summary>East of this x the ground is Mistlands (a road from the swamp into the Mistlands); the rest is swamp.</summary>
        public float MistlandsEastOf = float.PositiveInfinity;
        public int Reads;
        public override float GetHeight(float x, float z)
        {
            Reads++;
            if (SideChannel && z >= 4f) return 20f;
            if (Mathf.Abs(x) > 128f || Mathf.Abs(z) > 64f) return 0f;
            float edge = Mathf.Abs(x) - Length * 0.5f;
            float height = RoadConstants.SeaLevel + Mathf.Min(1f, Mathf.Max(-Depth, edge * BankSlope)) + Mathf.Max(0f, -x - 16f) * BankRise;
            // Past the Mistlands edge the ground rises a metre over 10 m, onto ground a Mistlands road stands on (its landing floor).
            if (x > MistlandsEastOf) height += Mathf.Min(1f, (x - MistlandsEastOf) * 0.1f);
            return Pocket ? Mathf.Min(height, RoadConstants.SeaLevel - Depth + Mathf.Abs(x)) : height;
        }
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
        { Reads++; return x > MistlandsEastOf ? Heightmap.Biome.Mistlands : Heightmap.Biome.Swamp; }
        // The same ground in either biome, so a heightmap across the edge blends nothing.
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            if (float.IsPositiveInfinity(MistlandsEastOf)) return base.GetBiomeHeight(biome, wx, wy, out mask, preGeneration, riverPreDN);
            mask = default;
            return GetHeight(wx, wy);
        }
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { Reads++; weight = River && Mathf.Abs(x) < Length * 0.5f ? 1f : 0f; width = weight > 0f ? Length : 0f; }
    }

    private Pool World(float length = 16f, float depth = 0.4f)
    { var world = new Pool { Length = length, Depth = depth }; _scope.WithWorld(world); return world; }

    private static List<Vector2> Line() => Enumerable.Range(-6, 13).Select(i => new Vector2(i * 8f, 0f)).ToList();

    private sealed class PoolBesideAPuddle : WorldGenerator
    {
        public override float GetHeight(float x, float z) => RoadConstants.SeaLevel + Mathf.Min(1f,
            Mathf.Min(Mathf.Max(-0.4f, (Mathf.Abs(x + 8f) - 8f) * 0.1f), (Mathf.Abs(x - 4f) - 3f) * 0.25f));
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Swamp;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    // A pool from x -14 to 2 whose far bank is the foot of a cliff: the ground east of it rises CliffSlope per metre to a
    // plateau 20 m up, so the first search cell past the pool (x 8) stands 6 m on from the bank.
    private sealed class PoolBelowACliff : WorldGenerator
    {
        public float CliffSlope = 2f;
        public override float GetHeight(float x, float z)
        {
            if (Mathf.Abs(x) > 128f || Mathf.Abs(z) > 64f) return 0f;
            float edge = Mathf.Abs(x + 6f) - 8f;
            return RoadConstants.SeaLevel + (x < -6f ? Mathf.Min(1f, Mathf.Max(-0.4f, edge * 0.1f)) : Mathf.Min(20f, Mathf.Max(-0.4f, edge * CliffSlope)));
        }
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Swamp;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    /// <summary>A raised pool at a cliff's foot: the road its far bank starts climbs past the earthwork cap however it is
    /// graded, and the search refuses the crossing as the planner refuses the road (one owner, PastTerrainLimitOffBank and
    /// the plans' measure); a gentle bank is crossed and planned within the cap. Measured on an island where a raised pool's
    /// bank stood 17.5 m below the ground 11 m on.</summary>
    [Theory]
    [InlineData(2f, false, false)]
    [InlineData(0.1f, false, true)]
    [InlineData(2f, true, false)]
    [InlineData(0.1f, true, true)]
    public void TheSearchRefusesAPoolBankTheEarthworkCapRefusesThePlannerFor(float cliffSlope, bool bridges, bool builds)
    {
        using var cap = StaticOverride.Keep(() => RoadNetworkGenerator.EarthworkCap);
        RoadNetworkGenerator.EarthworkCap = RoadNetworkGenerator.DefaultEarthworkCap;
        // Fords only, the cliff's pool is raised (its bank too steep to wade) and the gentle one waded; with bridges the cliff's
        // is spanned. Whatever its treatment, the planner (which may raise a span, KeepStructuresApart) meets the bank.
        var world = new PoolBelowACliff { CliffSlope = cliffSlope };
        _scope.WithWorld(world);
        var path = Line();
        var crossings = RoadCrossingDetector.Detect(path, world, bridges);
        var crossing = Assert.Single(crossings);
        Assert.Equal(builds ? FordStyle.Wade : bridges ? FordStyle.Span : FordStyle.Raise, crossing.Style);
        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, crossings, 4f, null, null);
        // The planner refuses the cliff's road (no profile, or one past the cap) and plans the gentle one within it.
        Assert.Equal(builds, plans != null && RoadNetworkGenerator.PastTerrainLimit(plans, RoadNetworkGenerator.EarthworkLimit, new List<Vector2>()) == 0);

        var finder = new RoadPathfinder(world) { Fords = true, Bridges = bridges };
        // The jump from the dry cell before the pool lands on the first cell past its bank: on the cliff, as measured on the
        // island, no road is planned from that bank within the cap, and the search does not take it.
        Assert.Equal(builds, Ask(finder, -2).found);
        Assert.Equal(builds, Ask(finder, -1).found);
    }

    [Fact]
    public void AWadesBankNeverWalksAcrossAnotherWetRunToFindItsDryFloor()
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        RoadCrossingDetector.SetFordStyleWeights(1f, 0f, 0f);
        var world = new PoolBesideAPuddle();
        _scope.WithWorld(world);
        var c = Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true));
        Assert.Equal(FordStyle.Span, c.Style);
        Assert.True(c.ToBank.x <= 0f, "the first pool consumed the deeper puddle across its dry bank");
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        var result = RoadNetworkGenerator.PlanCandidate(finder, Line(), Line()[0], 0f, Line().Last(), 0f, false, false, true, true, 4f);
        var plans = result.Plans;
        Assert.NotNull(plans);
        Assert.Equal(FordStyle.Span, Assert.Single(result.Crossings).Style);
        Assert.All(plans!.SelectMany(p => p.Points.Select((at, i) => (at, h: p.Heights[i])))
            .Where(p => p.at.x >= 1f && p.at.x <= 7f), p => Assert.True(p.h >= RoadConstants.DryRoadFloor));
    }

    /// <summary>A wade over standing water whose bed is raised to the wade limit (RoadCrossingDetector.WadeSurface: built as
    /// road, smoothed and limited) is held at each bank to the bank's ground (RoadNetworkGenerator.FordBankHeight), as the road
    /// on land is: its smoothed end was pulled 0.9 m under the road it met at its bank (seen on a generated network, where a
    /// second road joined at that bank). Built, every piece meets the next within a step.</summary>
    [Fact]
    public void ARaisedWadeMeetsTheRoadOnLandAtItsBanksWithinAStep()
    {
        var world = World(depth: 0.6f);
        Assert.True(0.6f > RoadConstants.FordWadeMaxDepth);
        var path = Line();
        var crossing = Assert.Single(RoadCrossingDetector.Detect(path, world, bridges: false));
        Assert.Equal(FordStyle.Wade, crossing.Style);
        Assert.False(RoadCrossingDetector.WadeSurface(crossing.Shallow, RoadConstants.SeaLevel - crossing.RiverbedHeight).followTerrain);
        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing> { crossing }, 4f, null, null);
        Assert.True(plans != null, RoadSpatialGrid.LastRefusal);
        Assert.True(plans!.Count >= 3);
        for (int i = 1; i < plans.Count; i++)
            Assert.True(StairLayout.Walkable(plans[i - 1].Heights.Last(), plans[i].Heights[0]),
                $"pieces {i - 1} and {i} meet {Mathf.Abs(plans[i - 1].Heights.Last() - plans[i].Heights[0]):F2} m apart");
    }

    [Fact]
    public void KneeDeepStandingWaterUsesItsNaturalBedInTheBuilderAndApproachTrial()
    {
        var world = World();
        var path = Line();
        var crossing = Assert.Single(RoadCrossingDetector.Detect(path, world, bridges: true));
        Assert.True(crossing.Shallow);
        Assert.Equal(FordStyle.Wade, crossing.Style);
        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing> { crossing }, 4f, null, null);
        Assert.NotNull(plans);
        Assert.True(world.GetHeight(crossing.FromBank.x, crossing.FromBank.y) >= RoadConstants.DryRoadFloor);
        Assert.True(world.GetHeight(crossing.ToBank.x, crossing.ToBank.y) >= RoadConstants.DryRoadFloor);
        for (int i = 1; i < plans!.Count; i++)
            Assert.True(StairLayout.Walkable(plans[i - 1].Heights.Last(), plans[i].Heights[0]), "a raised lip at the wade's bank");
        var middle = plans!.SelectMany(p => p.Points.Select((at, i) => (at, h: p.Heights[i], p.FollowTerrain)))
            .Where(p => Mathf.Abs(p.at.x) < 2f).ToList();
        Assert.NotEmpty(middle);
        Assert.All(middle, p => { Assert.True(p.FollowTerrain); Assert.Equal(world.GetHeight(p.at.x, p.at.y), p.h); });
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        var trial = RoadNetworkGenerator.PlanFromPath(finder, Line(), path[0], 0f, path[path.Count - 1], 0f, false, false, true, true, 4f);
        Assert.True(trial.Stop == RoadNetworkGenerator.PlanStop.Planned, $"trial stopped at {trial.Stop}");
        Assert.Equal(FordStyle.Wade, Assert.Single(trial.Crossings).Style);
        Assert.NotNull(trial.Plans);
        Assert.Null(RoadNetworkGenerator.BuiltRoadRefusal(trial.Plans!, trial.Crossings, out _));
    }

    [Theory]
    [InlineData(8f, 0.2f, CrossingKind.Ford, FordStyle.Wade)]
    [InlineData(16f, 0.5f, CrossingKind.Ford, FordStyle.Wade)]
    [InlineData(24f, 0.8f, CrossingKind.Ford, FordStyle.Wade)]
    [InlineData(40f, 1.5f, CrossingKind.Bridge, FordStyle.None)]
    [InlineData(64f, 0.4f, CrossingKind.Bridge, FordStyle.None)]
    [InlineData(96f, 3.5f, CrossingKind.Bridge, FordStyle.None)]
    public void StandingWaterUsesTheExistingDepthLengthAndFairwayLimits(float length, float depth, CrossingKind kind, FordStyle style)
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        RoadCrossingDetector.SetFordStyleWeights(1f, 1f, 0f);
        var world = World(length, depth);
        var c = Assert.Single(RoadCrossingDetector.Detect(new List<Vector2> { new(-80f, 0f), new(80f, 0f) }, world, bridges: true));
        Assert.False(c.Invalid);
        Assert.Equal(kind, c.Kind);
        Assert.Equal(style, c.Style);
    }

    [Theory]
    [InlineData(0.8f)]
    [InlineData(3f)]
    public void ADeepPoolRaisesItsWadeOnlyToTheWadeLimitOnEveryPlanningPath(float depth)
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        RoadCrossingDetector.SetFordStyleWeights(1f, 1f, 0f);
        var world = World(16f, depth); world.Pocket = true;
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true, Meander = 0f, SlopeMultiplier = 0f };
        var searched = finder.FindPath(Line()[0], Line().Last());
        Assert.NotNull(searched);
        // The crossing on the line the search took (any of its sixteen headings), as the detector builds it.
        var detected = Assert.Single(RoadCrossingDetector.Detect(searched!, world, bridges: true));
        Assert.Equal(FordStyle.Wade, detected.Style);
        var generated = RoadNetworkGenerator.PlanSearchedPath(finder, searched!, searched![0], 0f, searched!.Last(), 0f,
            false, false, true, true, 4f, default);
        var trial = RoadNetworkGenerator.PlanFromPath(finder, new List<Vector2>(searched!), searched![0], 0f, searched!.Last(), 0f,
            false, false, true, true, 4f);
        var direct = RoadNetworkGenerator.PlanRoadPathWithCrossings(new List<Vector2>(searched!), new List<RoadCrossing> { detected }, 4f, null, null);
        foreach (var result in new[] { generated, trial })
        {
            Assert.NotNull(result.Plans);
            var crossing = Assert.Single(result.Crossings);
            Assert.Equal((detected.Kind, detected.Style, detected.FromBank, detected.ToBank),
                (crossing.Kind, crossing.Style, crossing.FromBank, crossing.ToBank));
        }
        foreach (var plans in new[] { direct, generated.Plans, trial.Plans })
        {
            Assert.NotNull(plans);
            var middle = plans!.SelectMany(p => p.Points.Select((at, i) => (at, h: p.Heights[i], p.FollowTerrain)))
                .Where(p => Mathf.Abs(p.at.x) < 2f).ToList();
            Assert.NotEmpty(middle);
            Assert.All(middle, p => { Assert.False(p.FollowTerrain); Assert.Equal(RoadConstants.SeaLevel - RoadConstants.FordWadeMaxDepth, p.h); });
            for (int i = 1; i < plans!.Count; i++)
                Assert.True(StairLayout.Walkable(plans[i - 1].Heights.Last(), plans[i].Heights[0]));
        }
    }

    [Fact]
    public void ADeepPoolUsesTheStyleOwnerBeforeItsDryRaiseRecovery()
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        var world = World(16f, 0.8f);
        RoadCrossingDetector.SetFordStyleWeights(0f, 1f, 1f);
        Assert.Equal(FordStyle.Wade, Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true)).Style);
        RoadCrossingDetector.SetFordStyleWeights(1f, 1f, 0f);
        Assert.Equal(FordStyle.Wade, Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true)).Style);
        RoadCrossingDetector.SetFordStyleWeights(0f, 1f, 0f);
        Assert.Equal(FordStyle.Wade, Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true)).Style);
        world.Pocket = true;
        world.Depth = RoadCrossingDetector.ShallowMaxDepth + RoadConstants.FordWadeMaxDepth;
        Assert.Equal(CrossingKind.Bridge, Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true)).Kind);
    }

    [Fact]
    public void AFiveMetrePuddleKeepsTheDryRoadFloor()
    {
        var world = World(5f, 0.2f);
        var crossings = RoadCrossingDetector.Detect(Line(), world, bridges: true);
        Assert.Empty(crossings);
        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(Line(), crossings, 4f, null, null);
        Assert.NotNull(plans);
        Assert.All(plans!.SelectMany(p => p.Heights), h => Assert.True(h >= RoadConstants.DryRoadFloor));
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(7f)]
    public void PuddleEndpointsRemainReachableThroughTheRealSearches(float length)
    {
        var world = World(length, 0.2f);
        Vector2 start = new(-48f, 0f), wet = new(0f, 0f);
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        Assert.NotNull(finder.FindPath(start, wet));
        Assert.NotNull(finder.FindPath(wet, start));
        Assert.NotNull(finder.FindPathBetween(new[] { (start, 0f) }, new[] { (wet, 0f) }, start, wet));
        var road = RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(0f, -16f), new(0f, 16f) }, 4f, world);
        Assert.NotNull(road);
        RoadSpatialGrid.Commit(road!);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var join = finder.FindPathToNetwork(start, 4f, () => false, new[] { wet });
        Assert.NotNull(join);
        Assert.True(Mathf.Abs(join![join.Count - 1].x) <= 4f);
    }

    [Theory]
    [InlineData(7f, false)]
    [InlineData(8f, true)]
    public void ThePuddleBoundaryMeasuresOnlyTheWetLength(float length, bool crossing)
    {
        var world = World(length, 0.2f);
        var crossings = RoadCrossingDetector.Detect(Line(), world, bridges: true);
        Assert.Equal(crossing ? 1 : 0, crossings.Count);
        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(Line(), crossings, 4f, null, null);
        Assert.NotNull(plans);
        if (!crossing) Assert.All(plans!.SelectMany(p => p.Heights), h => Assert.True(h >= RoadConstants.DryRoadFloor));
    }

    [Fact]
    public void TheSearchUsesTheBuildersCompleteDryFloorBanks()
    {
        var world = World();
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true, Meander = 0f, SlopeMultiplier = 0f };
        Assert.True(Ask(finder, -1).found);
        var built = Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true));
        var facts = typeof(RoadPathfinder).GetMethod("CrossingFactsFor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(finder, new object?[] { new Vector2i(-1, 0), null, new Vector2Int(1, 0), true })!;
        object Read(string name) => facts.GetType().GetField(name)!.GetValue(facts)!;
        Assert.Equal(built.FromBank, (Vector2)Read("FromBank"));
        Assert.Equal(built.ToBank, (Vector2)Read("ToBank"));
        Assert.Equal(built.Kind == CrossingKind.Bridge, (bool)Read("Bridge"));
        Assert.Equal(built.Style, (FordStyle)Read("Style"));
        // A ford with these dry-floor banks is the natural-bed wade the builder lays.
        Assert.Equal(FordStyle.Wade, built.Style);
        var searched = finder.FindPath(new Vector2(-48f, 0f), new Vector2(48f, 0f));
        Assert.NotNull(searched);
        var planned = RoadNetworkGenerator.PlanFromPath(finder, searched!, searched![0], 0f, searched![searched.Count - 1], 0f,
            false, false, true, true, 4f);
        Assert.NotNull(planned.Plans);
        var actual = Assert.Single(planned.Crossings);
        // The search may cross on a knight's heading: the builder's crossing of the line it took.
        built = Assert.Single(RoadCrossingDetector.Detect(searched!, world, bridges: true));
        Assert.True((built.Kind, built.Style, built.FromBank, built.ToBank) ==
            (actual.Kind, actual.Style, actual.FromBank, actual.ToBank),
            $"{actual.Style} {actual.FromBank}->{actual.ToBank}; search {string.Join(";", searched!)}");
    }

    /// <summary>A pool crossed on a knight's heading, one of the sixteen the search steps on: standing water is jumped on it as
    /// on the eight grid headings, through the same crossing owner (a swamp road is no longer laid bank to bank on the grid's
    /// eight headings alone). A river keeps the eight: its dry-shore scan walks whole cells.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void APoolIsJumpedOnAKnightsHeadingAndARiverIsNot(bool river, bool found)
    {
        var world = World();
        world.River = river;
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        var answer = Ask(finder, -2, direction: new Vector2Int(2, 1));
        Assert.Equal(found, answer.found);
        if (!found) return;
        Assert.Equal(0, (answer.landing.x + 2) - 2 * answer.landing.y);
        Vector2 to = new(answer.landing.x * RoadConstants.PathfindingCellSize, answer.landing.y * RoadConstants.PathfindingCellSize);
        Assert.True(RoadWater.AtOrOverSea(world.GetHeight(to.x, to.y)), $"landed at {to} in the pool");
        var built = Assert.Single(RoadCrossingDetector.Detect(new List<Vector2> { new(-16f, 0f), to }, world, bridges: true));
        Assert.Equal(CrossingKind.Ford, built.Kind);
    }

    /// <summary>A searched road through standing water is pulled and swayed before its crossings are found, and the pool's
    /// crossing is detected on that line, the one its pieces are planned on (RoadNetworkGenerator.PlanCandidate): with the
    /// mod's sway, the planned road's crossing is the detector's on the shaped line, and the pieces are not swayed again.</summary>
    [Fact]
    public void ARoadThroughAPoolIsSwayedBeforeItsCrossingIsFound()
    {
        var world = World(24f, 0.4f);
        PlainRoads.WithModDefaults(() =>
        {
            var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
            var searched = finder.FindPath(new Vector2(-96f, 0f), new Vector2(96f, 0f));
            Assert.NotNull(searched);
            var before = RoadNetworkGenerator.ShapedBeforeCrossings.shaped;
            var planned = RoadNetworkGenerator.PlanFromPath(finder, searched!, searched![0], 0f, searched.Last(), 0f, false, false, true, true, 4f);
            Assert.NotNull(planned.Plans);
            Assert.True(RoadNetworkGenerator.ShapedBeforeCrossings.shaped > before);
            var crossing = Assert.Single(planned.Crossings);
            var onShaped = Assert.Single(RoadCrossingDetector.Detect(planned.Path, world, bridges: true));
            Assert.Equal((onShaped.Kind, onShaped.Style), (crossing.Kind, crossing.Style));
            Assert.NotEqual(searched, planned.Path);
        });
    }

    /// <summary>Shaping first is the swamp's (RoadNetworkGenerator.PlanCandidate): a road through a swamp pool that runs on into
    /// the Mistlands (from 16 m past the pool) is planned on the search's own line alone, never shaped before its crossings are
    /// found; the same road kept in the swamp is shaped first and planned. Before, a road with Mistlands on it was shaped first
    /// wherever it touched standing water, and its valley roads were planned twice.</summary>
    [Fact]
    public void ARoadIntoTheMistlandsIsNeverShapedFirstAndASwampRoadIs()
    {
        foreach (bool intoTheMistlands in new[] { true, false })
        {
            RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear();
            var world = World(24f, 0.4f);
            if (intoTheMistlands) world.MistlandsEastOf = 28f;
            PlainRoads.WithModDefaults(() =>
            {
                var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
                var searched = finder.FindPath(new Vector2(-96f, 0f), new Vector2(96f, 0f));
                Assert.NotNull(searched);
                var before = RoadNetworkGenerator.ShapedBeforeCrossings.shaped;
                RoadNetworkGenerator.PlanFromPath(finder, searched!, searched![0], 0f, searched.Last(), 0f, false, false, true, true, 4f);
                int shaped = RoadNetworkGenerator.ShapedBeforeCrossings.shaped - before;
                if (intoTheMistlands) Assert.Equal(0, shaped);
                else Assert.True(shaped > 0, "the swamp road was not shaped first");
            });
        }
    }

    /// <summary>A line is shaped only between its kept points (RoadNetworkGenerator.ShapedBetweenKept): a kept consecutive
    /// pair (a viaduct's or a stair step's bought jump) stays one consecutive pair of the same two points, a kept run is copied
    /// as it is, and only the free stretches are shaped, their ends where they were. Before, the whole line was resampled at
    /// 12 m with the kept points kept, so a 48 m jump came back as five points and its viaduct was no longer recognised
    /// (DvergrViaduct.AddJumps matches adjacent points).</summary>
    [Fact]
    public void AShapedLineKeepsEachBoughtJumpOneConsecutivePair()
    {
        var on = Enumerable.Range(0, 13).Select(i => new Vector2(i * 8f, 0f)).ToList();
        on.Insert(7, new Vector2(56f + 48f, 0f));
        on = on.Take(8).Concat(Enumerable.Range(1, 6).Select(i => new Vector2(104f + i * 8f, 0f))).ToList();
        var keep = new bool[on.Count];
        keep[0] = keep[on.Count - 1] = true;
        keep[6] = keep[7] = true;   // (48, 0) -> (104, 0), a jump
        var shaped = RoadNetworkGenerator.ShapedBetweenKept(on, keep, stretch =>
        {
            var moved = new List<Vector2> { stretch[0] };
            for (int i = 1; i < stretch.Count - 1; i++) moved.Add(stretch[i] + new Vector2(0f, 3f));
            moved.Add(stretch[stretch.Count - 1]);
            return moved;
        });
        int a = shaped.IndexOf(new Vector2(48f, 0f)), b = shaped.IndexOf(new Vector2(104f, 0f));
        Assert.True(a >= 0 && b == a + 1, $"the jump is not one consecutive pair: {string.Join(";", shaped)}");
        Assert.Equal(on[0], shaped[0]);
        Assert.Equal(on[on.Count - 1], shaped[shaped.Count - 1]);
        Assert.Contains(shaped, p => p.y == 3f);
        Assert.Equal(on.Count, shaped.Count);
    }

    /// <summary>A crossing found on the shaped candidate records the search's own moves (RoadNetworkGenerator.SearchedMovesOn),
    /// so a ban after it matches the jump the search made (RoadPathfinder.Banned needs both ends within a cell of one), not the
    /// swayed line's short segments.</summary>
    [Fact]
    public void AShapedCrossingRecordsTheSearchsOwnMoves()
    {
        var searched = new List<Vector2> { new(-48f, 0f), new(-16f, 0f), new(16f, 0f), new(48f, 0f) };
        var c = RoadCrossing.Between(new Vector2(-13f, 2f), new Vector2(13f, 3f), 29.5f, new Vector2(0f, 2.5f), 26f, CrossingKind.Ford);
        var moves = RoadNetworkGenerator.SearchedMovesOn(searched, c);
        Assert.Contains((new Vector2(-16f, 0f), new Vector2(16f, 0f)), moves);
        Assert.All(moves, m => Assert.Contains(m.from, searched));
    }

    private static (bool found, Vector2i landing, float cost) Ask(RoadPathfinder finder, int cell, Vector2? leaving = null, Vector2Int? direction = null)
    {
        var args = new object?[] { new Vector2i(cell, 0), leaving, direction ?? new Vector2Int(1, 0), null, null };
        bool found = (bool)typeof(RoadPathfinder).GetMethod("TryGetRiverCrossing", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(finder, args)!;
        return (found, (Vector2i)args[3]!, (float)args[4]!);
    }

    [Theory]
    [InlineData(16f, 0.4f)]
    [InlineData(24f, 0.8f)]
    [InlineData(64f, 0.4f)]
    public void SearchAndBuilderJudgeTheSameStandingWaterSegment(float length, float depth)
    {
        var world = World(length, depth);
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        int cell = -Mathf.CeilToInt(length * 0.5f / RoadConstants.PathfindingCellSize);
        var answer = Ask(finder, cell);
        Assert.True(answer.found);
        Vector2 from = new(cell * RoadConstants.PathfindingCellSize, 0f), to = new(answer.landing.x * RoadConstants.PathfindingCellSize, answer.landing.y * RoadConstants.PathfindingCellSize);
        var built = Assert.Single(RoadCrossingDetector.Detect(new List<Vector2> { from, to }, world, bridges: true));
        Assert.Equal(built.Kind == CrossingKind.Ford, RoadCrossingDetector.IsFordJump(from, to, world));
        world.Reads = 0;
        Assert.Equal(answer, Ask(finder, cell));
        Assert.Equal(0, world.Reads);
        Assert.Equal(answer, Ask(new RoadPathfinder(world) { Fords = true, Bridges = true }, cell));
        Assert.True(Ask(finder, cell, from + new Vector2(-1.5f, 0f)).found);
    }

    [Theory]
    [InlineData(false, FordStyle.Wade)]
    [InlineData(true, FordStyle.Wade)]
    [InlineData(false, FordStyle.Span)]
    [InlineData(true, FordStyle.Span)]
    public void StandingFordSearchesPayTheExistingShallowWaterPrice(bool lowMemory, FordStyle style)
    {
        using var settings = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight)
            .AndKeep(() => GenerationMemory.Low);
        RoadCrossingDetector.SetFordStyleWeights(style == FordStyle.Wade ? 1f : 0f, 0f, style == FordStyle.Span ? 1f : 0f);
        GenerationMemory.Low = lowMemory;
        var world = World(16f, 0.8f);
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        var answer = Ask(finder, -1);
        Assert.True(answer.found);
        var crossing = Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true));
        Assert.Equal(FordStyle.Wade, crossing.Style);
        Assert.Equal(finder.StandingWaterPenalty * world.Length / RoadPathfinder.CellSize,
            answer.cost - finder.BaseCost * crossing.Width);
        // A different shallow-water price is read on the next call, even with terrain facts cached.
        finder.StandingWaterPenalty = RoadConstants.RiverCrossingPenalty;
        Assert.Equal(finder.StandingWaterPenalty * world.Length / RoadPathfinder.CellSize,
            Ask(finder, -1).cost - finder.BaseCost * crossing.Width);
    }

    [Fact]
    public void StandingWaterFactsRespectTheCrossingModesAndMemoryPolicy()
    {
        using var modes = StaticOverride.Keep(() => RoadCrossingDetector.Shallows).AndKeep(() => GenerationMemory.Low);
        var world = World();
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        Assert.True(Ask(finder, -1).found);
        RoadCrossingDetector.Shallows = false;
        Assert.False(Ask(finder, -1).found);
        RoadCrossingDetector.Shallows = true;
        GenerationMemory.Low = true;
        Assert.Equal(Ask(finder, -1), Ask(new RoadPathfinder(world) { Fords = true, Bridges = true }, -1));
        finder.Fords = false;
        Assert.True(Ask(finder, -1).found);
        Assert.Equal(Ask(finder, -1), Ask(new RoadPathfinder(world) { Fords = false, Bridges = true }, -1));
    }

    [Theory]
    [InlineData(0.4f, false)]
    [InlineData(0.8f, false)]
    [InlineData(3f, false)]
    [InlineData(0.4f, true)]
    [InlineData(0.8f, true)]
    public void MarkedRoadsAndApproachTrialsUseTheGenerationCrossing(float depth, bool wetEnd)
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        RoadCrossingDetector.SetFordStyleWeights(1f, 1f, 0f);
        var world = World(16f, depth); world.Pocket = true;
        _scope.WithZdos().WithZoneSystem();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        var island = new Island { Id = 1, CellSize = RoadConstants.PathfindingCellSize, WorldOffset = 0,
            Min = new Vector2(-128f, -128f), Max = new Vector2(128f, 128f) };
        for (int x = -16; x <= 16; x++) for (int z = -16; z <= 16; z++) island.Cells.Add(new Vector2Int(x, z));
        var marks = new[] { new Vector2(-48f, 0f), new Vector2(wetEnd ? 0f : 48f, 0f) };
        var manual = ManualRoads.Prepare(marks, false, new[] { island });
        var marked = Assert.Single(manual.NewCrossings);
        Assert.Equal(FordStyle.Wade, marked.Style);
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        var searched = finder.FindPath(marks[0], marks[1]);
        Assert.NotNull(searched);
        var generated = RoadNetworkGenerator.PlanSearchedPath(finder, searched!, marks[0], 0f, marks[1], 0f,
            false, false, false, false, 4f, default);
        var trial = RoadNetworkGenerator.PlanFromPath(finder, new List<Vector2>(searched!), marks[0], 0f, marks[1], 0f,
            false, false, false, false, 4f);
        foreach (var plan in new[] { generated, trial })
        {
            Assert.NotNull(plan.Plans);
            var c = Assert.Single(plan.Crossings);
            Assert.Equal((marked.Kind, marked.Style, marked.Shallow), (c.Kind, c.Style, c.Shallow));
            Assert.Null(RoadNetworkGenerator.BuiltRoadRefusal(plan.Plans!, plan.Crossings, out _));
        }
        Assert.True(RoadNetworkGenerator.TrialBuilds(finder, new List<Vector2>(searched!), marks[0], 0f, marks[1], 0f,
            false, false, false, false, 4f, 0f, 0f));

        var network = RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(48f, -32f), new(48f, 32f) }, 4f, world);
        Assert.NotNull(network);
        RoadSpatialGrid.Commit(network!);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        var joined = ManualRoads.Prepare(new[] { marks[0] }, true, new[] { island });
        var joinCrossing = Assert.Single(joined.NewCrossings);
        Assert.Equal((marked.Kind, marked.Style, marked.Shallow), (joinCrossing.Kind, joinCrossing.Style, joinCrossing.Shallow));
    }

    [Fact]
    public void RiverCrossingsKeepTheirExistingKindAndStyle()
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        RoadCrossingDetector.SetFordStyleWeights(1f, 0f, 0f);
        var world = World(); world.River = true;
        var c = Assert.Single(RoadCrossingDetector.Detect(new List<Vector2> { new(-24f, 0f), new(24f, 0f) }, world, bridges: true));
        Assert.False(c.Shallow);
        Assert.Equal(CrossingKind.Ford, c.Kind);
        Assert.Equal(FordStyle.Wade, c.Style);
    }

    [Theory]
    [InlineData(16f)]
    [InlineData(0f)]
    public void AWorkingWadeIsKeptEvenWhenItsSpanCannotBuild(float turn)
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        RoadCrossingDetector.SetFordStyleWeights(0f, 0f, 1f);
        var world = World(16f, 0.8f); world.BankRise = 0.8f;
        List<Vector2> Path() => new() { new(-24f, turn), new(-8f, 0f), new(8f, 0f), new(24f, turn) };
        var first = Assert.Single(RoadCrossingDetector.Detect(Path(), world, bridges: true));
        Assert.Equal(FordStyle.Wade, first.Style);
        var span = Assert.Single(RoadCrossingDetector.Detect(Path(), world, bridges: true,
            refusedShallows: new[] { (first.ShallowSite!.Value, FordStyle.Wade) }));
        Assert.Equal(FordStyle.Span, span.Style);
        Assert.Null(RoadNetworkGenerator.PlanRoadPathWithCrossings(Path(), new List<RoadCrossing> { span }, 4f, null, null));
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        foreach (bool trial in new[] { false, true })
        {
            var path = Path();
            var result = trial
                ? RoadNetworkGenerator.PlanFromPath(finder, path, path[0], 0f, path[path.Count - 1], 0f, false, false, true, true, 4f)
                : RoadNetworkGenerator.PlanCandidate(finder, path, path[0], 0f, path[path.Count - 1], 0f, false, false, true, true, 4f);
            Assert.NotNull(result.Plans);
            var kept = Assert.Single(result.Crossings);
            Assert.True(kept.Shallow);
            Assert.False(kept.Invalid);
            Assert.Equal(FordStyle.Wade, kept.Style);
            var middle = result.Plans!.SelectMany(p => p.Points.Select((at, i) => (at, h: p.Heights[i])))
                .Where(p => Mathf.Abs(p.at.x) < 2f).ToList();
            Assert.NotEmpty(middle);
            Assert.All(middle, p => Assert.InRange(p.h, RoadConstants.SeaLevel - RoadConstants.FordWadeMaxDepth, RoadConstants.SeaLevel));
        }
    }

    [Fact]
    public void ADeepPoolUsesDryRaiseOnlyAfterItsOtherTreatmentsAreRefused()
    {
        var world = World(16f, 0.8f);
        var first = Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true));
        var refused = new[] { (first.ShallowSite!.Value, FordStyle.Wade), (first.ShallowSite!.Value, FordStyle.Span) };
        var fallback = Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true, refusedShallows: refused));
        Assert.False(fallback.Invalid);
        Assert.Equal(FordStyle.Raise, fallback.Style);
        Assert.Contains("recovery Raise", fallback.ShallowNote!);
        var raised = RoadNetworkGenerator.PlanRoadPathWithCrossings(Line(), new List<RoadCrossing> { fallback }, 4f, null, null);
        Assert.NotNull(raised);
        Assert.All(raised!.SelectMany(p => p.Heights), h => Assert.True(h >= RoadConstants.DryRoadFloor));
        var exhausted = Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true,
            refusedShallows: refused.Append((first.ShallowSite.Value, FordStyle.Raise)).ToArray()));
        Assert.True(exhausted.Invalid);
        Assert.Equal(FordStyle.None, exhausted.Style);
        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(Line(), new List<RoadCrossing> { exhausted }, 4f, null, null);
        Assert.NotNull(plans);
        Assert.NotNull(RoadNetworkGenerator.BuiltRoadRefusal(plans!, new List<RoadCrossing> { exhausted }, out _));
    }

    [Theory]
    [InlineData(16f)]
    public void ADeepPoolRecoversThroughDryRaiseWhenItsSpanCannotMeetTheRoad(float turn)
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        RoadCrossingDetector.SetFordStyleWeights(0f, 1f, 1f);
        var world = World(16f, 0.8f); world.BankSlope = 0.5f; world.BankRise = 0.8f;
        List<Vector2> Path() => new() { new(-24f, turn), new(-8f, 0f), new(8f, 0f), new(24f, turn) };
        var first = Assert.Single(RoadCrossingDetector.Detect(Path(), world, bridges: true));
        Assert.Equal(FordStyle.Span, first.Style);
        Assert.Null(RoadNetworkGenerator.PlanRoadPathWithCrossings(Path(), new List<RoadCrossing> { first }, 4f, null, null));
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        foreach (bool trial in new[] { false, true })
        {
            var path = Path();
            var result = trial
                ? RoadNetworkGenerator.PlanFromPath(finder, path, path[0], 0f, path.Last(), 0f, false, false, true, true, 4f)
                : RoadNetworkGenerator.PlanCandidate(finder, path, path[0], 0f, path.Last(), 0f, false, false, true, true, 4f);
            Assert.NotNull(result.Plans);
            var kept = Assert.Single(result.Crossings);
            Assert.Equal(FordStyle.Raise, kept.Style);
            Assert.Contains("recovery Raise", kept.ShallowNote!);
            Assert.Null(RoadNetworkGenerator.BuiltRoadRefusal(result.Plans!, result.Crossings, out _));
        }
    }

    [Fact]
    public void AZeroWeightFallbackStillChoosesAnEligibleTreatment()
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        RoadCrossingDetector.SetFordStyleWeights(0f, 0f, 0f);
        Assert.Equal(FordStyle.Span, RoadCrossingDetector.PickFordStyle(new List<FordStyle> { FordStyle.Span }, 0));
    }

    private sealed class TwoPools : WorldGenerator
    {
        public bool RiverFailure, Exhaust;
        public override float GetHeight(float x, float z)
        {
            if (RiverFailure && x > 80f) return x < 112f ? 20f : 80f;
            float local = x - (x < 0f ? -40f : 40f);
            return RoadConstants.SeaLevel + Mathf.Min(1f, Mathf.Max(-0.8f, (Mathf.Abs(local) - 8f) * (Exhaust && x > 0f ? 0.5f : 0.1f)))
                + Mathf.Min(8f, Mathf.Max(0f, -local - 16f)) * (Exhaust && x > 0f ? 4f : 0.8f);
        }
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Swamp;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight = RiverFailure && x > 80f && x < 112f ? 1f : 0f; width = weight > 0f ? 32f : 0f; }
    }

    private static List<Vector2> AcrossTwoPools() => new()
    { new(-64f, 16f), new(-48f, 0f), new(-32f, 0f), new(-16f, 16f),
      new(16f, 16f), new(32f, 0f), new(48f, 0f), new(64f, 16f) };

    [Fact]
    public void TwoWorkingWadesAreKeptWhenTheirSpansCannotBuild()
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        RoadCrossingDetector.SetFordStyleWeights(0f, 0f, 1f);
        var world = new TwoPools(); _scope.WithWorld(world);
        var path = AcrossTwoPools();
        var detected = RoadCrossingDetector.Detect(path, world, bridges: true);
        Assert.Equal(2, detected.Count);
        Assert.All(detected, c => Assert.Equal(FordStyle.Wade, c.Style));
        var spans = RoadCrossingDetector.Detect(AcrossTwoPools(), world, bridges: true,
            refusedShallows: detected.Select(c => (c.ShallowSite!.Value, FordStyle.Wade)).ToArray());
        Assert.All(spans, c => Assert.Equal(FordStyle.Span, c.Style));
        Assert.Null(RoadNetworkGenerator.PlanRoadPathWithCrossings(AcrossTwoPools(), spans, 4f, null, null));
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        foreach (bool trial in new[] { false, true })
        {
            path = AcrossTwoPools();
            var planned = trial
                ? RoadNetworkGenerator.PlanFromPath(finder, path, path[0], 0f, path.Last(), 0f, false, false, true, true, 4f)
                : RoadNetworkGenerator.PlanCandidate(finder, path, path[0], 0f, path.Last(), 0f, false, false, true, true, 4f);
            Assert.True(planned.Plans != null, RoadSpatialGrid.LastRefusal);
            Assert.Equal(2, planned.Crossings.Count);
            Assert.All(planned.Crossings, c => Assert.Equal(FordStyle.Wade, c.Style));
        }
    }

    [Fact]
    public void APoolRecoveryPreservesTheOtherPoolsWorkingWade()
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        RoadCrossingDetector.SetFordStyleWeights(1f, 0f, 0f);
        var world = new TwoPools { Exhaust = true }; _scope.WithWorld(world);
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(64f, 16f), new(80f, 16f) }, new List<float> { 100f, 100f }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var path = AcrossTwoPools();
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        var result = RoadNetworkGenerator.PlanCandidate(finder, path, path[0], 0f, path.Last(), 0f, false, false, true, true, 4f);
        Assert.Null(result.Plans);
        Assert.Equal(FordStyle.Wade, result.Crossings[0].Style);
        Assert.False(result.Crossings[0].Invalid);
        Assert.True(result.Crossings[1].Invalid);
        Assert.NotNull(result.Unmet);
        Assert.Equal(result.Crossings[1].ShallowSite, result.Unmet!.ShallowSite);
    }

    [Fact]
    public void AnExhaustedPoolKeepsItsUnmetCrossingAndSearchedMoves()
    {
        var world = World(16f, 0.8f); world.BankSlope = 0.5f; world.BankRise = 4f;
        var path = new List<Vector2> { new(-24f, 16f), new(-8f, 0f), new(8f, 0f), new(24f, 16f) };
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        var result = RoadNetworkGenerator.PlanCandidate(finder, path, path[0], 0f, path.Last(), 0f, false, false, true, true, 4f);
        Assert.Null(result.Plans);
        Assert.True(Assert.Single(result.Crossings).Invalid);
        Assert.NotNull(result.Unmet);
        Assert.NotEmpty(result.Unmet!.Searched!);
        Assert.Equal(FordStyle.Raise, result.Unmet.Style);
    }

    [Fact]
    public void AFailedRiverBridgeBlacklistsNoPoolTreatment()
    {
        var world = new TwoPools { RiverFailure = true }; _scope.WithWorld(world);
        var path = AcrossTwoPools(); path.AddRange(new[] { new Vector2(80f, 16f), new Vector2(112f, 16f), new Vector2(120f, 16f) });
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        var result = RoadNetworkGenerator.PlanCandidate(finder, path, path[0], 0f, path.Last(), 0f, false, false, true, true, 4f);
        Assert.Null(result.Plans);
        Assert.NotNull(result.Unmet);
        Assert.False(result.Unmet!.Shallow);
        Assert.Equal(CrossingKind.Bridge, result.Unmet.Kind);
        Assert.All(result.Crossings.Where(c => c.Shallow), c => { Assert.False(c.Invalid); Assert.Equal(FordStyle.Wade, c.Style); });
        Assert.Equal(2, result.Crossings.Count(c => c.Shallow));
    }

    [Fact]
    public void StandingTreatmentOrderKeepsTheWadeRegardlessOfWeights()
    {
        using var weights = StaticOverride.Keep(() => RoadCrossingDetector.ConfiguredWadeWeight)
            .AndKeep(() => RoadCrossingDetector.ConfiguredRaiseWeight).AndKeep(() => RoadCrossingDetector.ConfiguredSpanWeight);
        var world = World(16f, 0.8f);
        RoadCrossingDetector.SetFordStyleWeights(0f, 1f, 1f);
        var first = Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true));
        Assert.Equal(FordStyle.Wade, first.Style);
        var second = Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true,
            refusedShallows: new[] { (first.ShallowSite!.Value, FordStyle.Wade) }));
        Assert.Equal(FordStyle.Span, second.Style);
    }

    [Theory]
    [InlineData(16f, 0.4f)]
    [InlineData(24f, 0.8f)]
    public void AWetDestinationInACrossingIsReachedAndBuiltAsAWadeThroughRealSearches(float length, float depth)
    {
        var world = World(length, depth);
        Vector2 start = new(-48f, 0f), wet = new(0f, 0f);
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true, Meander = 0f };
        var paths = new[] { finder.FindPath(start, wet), finder.FindPath(wet, start),
            finder.FindPathBetween(new[] { (start, 0f) }, new[] { (wet, 0f) }, start, wet) };
        foreach (var path in paths)
        {
            Assert.NotNull(path);
            var planned = RoadNetworkGenerator.PlanSearchedPath(finder, path!, path![0], 0f, path!.Last(), 0f,
                false, false, true, true, 4f, default);
            Assert.NotNull(planned.Plans);
            Assert.Equal(FordStyle.Wade, Assert.Single(planned.Crossings).Style);
            Assert.Contains(planned.Plans!.SelectMany(p => p.Points), p => p == wet);
            Assert.Null(RoadNetworkGenerator.BuiltRoadRefusal(planned.Plans!, planned.Crossings, out _));
        }
        // The road the join meets wades the pool along it as a road through water is built (RoadCrossingDetector.WadeSurface:
        // its painted bed in a knee-deep pool, levelled at the wade floor in a deeper one), so the join's own wade meets it.
        var (paintsBed, wadeFloor) = RoadCrossingDetector.WadeSurface(standing: true, depth);
        var road = RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(0f, -16f), new(0f, 16f) }, 4f, world, followTerrain: paintsBed, minHeight: wadeFloor);
        Assert.NotNull(road);
        RoadSpatialGrid.Commit(road!);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var join = finder.FindPathToNetwork(start, 4f, () => false, new[] { wet });
        Assert.NotNull(join);
        Assert.True(RoadWater.Wet(world.GetHeight(join!.Last().x, join.Last().y)));
        var joined = RoadNetworkGenerator.PlanSearchedPath(finder, join, start, 0f, join.Last(), 0f,
            false, false, true, true, 4f, default);
        Assert.NotNull(joined.Plans);
        Assert.Equal(FordStyle.Wade, Assert.Single(joined.Crossings).Style);
    }

    private sealed class DiagonalPool : WorldGenerator
    {
        public Vector2 Center;
        public float Diameter, Depth = 0.4f, Slope = 0.1f;
        public override float GetHeight(float x, float z) => RoadConstants.SeaLevel
            + Mathf.Clamp((Vector2.Distance(new Vector2(x, z), Center) - Diameter * 0.5f) * Slope, -Depth, 1f);
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Swamp;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    [Theory]
    [InlineData(8f, 8f, 10f)]
    [InlineData(8f, 0f, 8f)]
    public void AdjacentDiagonalWaterUsesTheSameProbeAndPriceAsLongMoves(float x, float z, float diameter)
    {
        Vector2 start = new(0f, 0f), end = new(x, z);
        var world = new DiagonalPool { Center = end * 0.5f, Diameter = diameter }; _scope.WithWorld(world);
        Assert.Single(RoadCrossingDetector.Detect(new List<Vector2> { start, end }, world, bridges: true));
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true, Meander = 0f, SlopeMultiplier = 0f };
        var to = new Vector2i((int)(x / RoadPathfinder.CellSize), (int)(z / RoadPathfinder.CellSize));
        int direction = Array.FindIndex(RoadPathfinder.Directions, d => d.x == to.x && d.y == to.y);
        var args = new object[] { new Vector2i(0, 0), to, direction, 0, 0 };
        float move = (float)typeof(RoadPathfinder).GetMethod("GetMoveCost", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(finder, args)!;
        Assert.True(move >= RoadConstants.RiverCrossingPenalty, $"unpriced diagonal: {move}");
        var crossingArgs = new object?[] { new Vector2i(0, 0), null, new Vector2Int(to.x, to.y), null, null };
        Assert.True((bool)typeof(RoadPathfinder).GetMethod("TryGetRiverCrossing", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(finder, crossingArgs)!);
        Assert.True((float)crossingArgs[4]! >= finder.StandingWaterPenalty);
        Assert.NotNull(finder.FindPath(start, end));
    }

    [Fact]
    public void ASplitBridgeApronDoesNotImplicateTheWorkingPool()
    {
        var world = World(16f, 0.8f);
        var path = Enumerable.Range(-6, 23).Select(i => new Vector2(i * 8f, 0f)).ToList();
        var pool = Assert.Single(RoadCrossingDetector.Detect(path, world, bridges: true));
        Assert.Equal(FordStyle.Wade, pool.Style);
        var bridge = RoadCrossing.Between(new Vector2(80f, 0f), new Vector2(112f, 0f), 20f,
            new Vector2(96f, 0f), 32f, CrossingKind.Bridge);
        bridge.FromIndex = path.IndexOf(bridge.FromBank); bridge.ToIndex = path.IndexOf(bridge.ToBank);
        bridge.FromEnd = new StructureEnds.End(bridge.FromBank, bridge.Direction, 31f, 0);
        bridge.ToEnd = new StructureEnds.End(bridge.ToBank, -bridge.Direction, 31f, 0);
        // Only the bridge's isolated near apron meets this conflicting road. The middle ends beyond its reach.
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(82f, -2f), new(82f, 2f) }, new List<float> { 100f, 100f }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var failed = new List<RoadCrossing>();
        Assert.Null(RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing> { pool, bridge },
            4f, null, null, failedCrossings: failed));
        Assert.Same(bridge, Assert.Single(failed));
        Assert.DoesNotContain(pool, failed);
        Assert.Equal(FordStyle.Wade, pool.Style);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStubReportsBothOwnedEndsSoItsStandingSpanCanRecover(bool sharedRiver)
    {
        var world = World(24f, 0.8f);
        var path = Line();
        var wade = Assert.Single(RoadCrossingDetector.Detect(path, world, bridges: true));
        var span = Assert.Single(RoadCrossingDetector.Detect(path, world, bridges: true,
            refusedShallows: new[] { (wade.ShallowSite!.Value, FordStyle.Wade) }));
        Assert.Equal(FordStyle.Span, span.Style);
        // Saved end geometry can bring a bridge's foot close to a later span's foot.
        var bridge = RoadCrossing.Between(new Vector2(-40f, 0f), new Vector2(-16f, 0f), 20f,
            new Vector2(-28f, 0f), 24f, CrossingKind.Bridge);
        bridge.FromIndex = 1; bridge.ToIndex = 4; bridge.Shared = sharedRiver;
        bridge.FromEnd = new StructureEnds.End(bridge.FromBank, bridge.Direction, 31f, 0);
        bridge.ToEnd = new StructureEnds.End(bridge.ToBank, -bridge.Direction, 31f, 0);
        span.FromEnd = new StructureEnds.End(new Vector2(-12f, 0f), span.Direction, 32f, 0);
        span.ToEnd = new StructureEnds.End(new Vector2(12f, 0f), -span.Direction, 32f, 0);
        var failed = new List<RoadCrossing>();
        Assert.Null(RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing> { bridge, span },
            4f, null, null, failedCrossings: failed));
        Assert.Equal("a stub of road between two structures", RoadSpatialGrid.LastRefusal);
        Assert.Contains(span, failed);
        Assert.Equal(!sharedRiver, failed.Contains(bridge));
        Assert.Same(sharedRiver ? span : bridge, RoadNetworkGenerator.LastStubBetween);
        var refused = new[] { (wade.ShallowSite!.Value, FordStyle.Wade), (span.ShallowSite!.Value, FordStyle.Span) };
        var recovered = Assert.Single(RoadCrossingDetector.Detect(Line(), world, bridges: true, refusedShallows: refused));
        Assert.False(recovered.Invalid);
        Assert.Equal(FordStyle.Raise, recovered.Style);
        Assert.NotNull(RoadNetworkGenerator.PlanRoadPathWithCrossings(Line(), new List<RoadCrossing> { recovered }, 4f, null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARejectedDiagonalPoolCannotBecomeAnOrdinaryDryMove(bool lowMemory)
    {
        using var memory = StaticOverride.Keep(() => GenerationMemory.Low);
        GenerationMemory.Low = lowMemory;
        Vector2 start = new(0f, 0f), end = new(8f, 8f);
        var world = new DiagonalPool { Center = end * 0.5f, Diameter = 10f, Depth = 4f, Slope = 2f };
        _scope.WithWorld(world);
        Assert.True(Assert.Single(RoadCrossingDetector.Detect(new List<Vector2> { start, end }, world, bridges: false)).Invalid);
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = false, Meander = 0f, SlopeMultiplier = 0f };
        int direction = Array.FindIndex(RoadPathfinder.Directions, d => d.x == 1 && d.y == 1);
        for (int repeat = 0; repeat < 2; repeat++)
        {
            var args = new object[] { new Vector2i(0, 0), new Vector2i(1, 1), direction, 0, 0 };
            float move = (float)typeof(RoadPathfinder).GetMethod("GetMoveCost", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(finder, args)!;
            Assert.True(move >= RoadConstants.RiverCrossingPenalty, $"unpriced rejected diagonal: {move}");
            var crossing = new object?[] { new Vector2i(0, 0), null, new Vector2Int(1, 1), null, null };
            Assert.False((bool)typeof(RoadPathfinder).GetMethod("TryGetRiverCrossing", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(finder, crossing)!);
        }
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(8f, 6f), new(8f, 10f) }, new List<float> { 31f, 31f }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        foreach (var path in new[] { finder.FindPath(start, end),
            finder.FindPathBetween(new[] { (start, 0f) }, new[] { (end, 0f) }, start, end),
            finder.FindPathToNetwork(start, 4f, () => false, new[] { end }) })
        {
            Assert.NotNull(path);
            Assert.True(path!.Count > 2, "the search walked straight over an inadmissible pool");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWetTargetIsCheckedAfterClippingTheEligiblePool(bool squeezedTarget)
    {
        var world = World(24f); world.SideChannel = true;
        Vector2 start = new(-48f, 0f), wet = new(0f, 0f);
        // Deep water beside the chosen shore makes the squeeze independent of shallow fill eligibility.
        var location = new RoadLocationProtection.Footprint(new Vector2(squeezedTarget ? 0f : 24f, -8f), 1f);
        RoadLocationProtection.Set(new[] { location });
        try
        {
            var finder = new RoadPathfinder(world) { Fords = true, Bridges = true, Meander = 0f };
            Assert.Equal(squeezedTarget, finder.LocationShoreSqueeze(wet));
            Assert.True(finder.LocationShoreSqueeze(new Vector2(squeezedTarget ? 0f : 24f, 0f)));
            RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(-2f, 0f), new(2f, 0f) }, new List<float> { 29.6f, 29.6f }, 4f);
            RoadSpatialGrid.FinalizeRoadNetwork();
            foreach (var path in new[] { finder.FindPath(start, wet),
                finder.FindPathBetween(new[] { (start, 0f) }, new[] { (wet, 0f) }, start, wet),
                finder.FindPathToNetwork(start, 4f, () => false, new[] { wet }) })
            {
                if (squeezedTarget) Assert.Null(path);
                else
                {
                    Assert.NotNull(path);
                    var planned = RoadNetworkGenerator.PlanSearchedPath(finder, path!, start, 0f, wet, 0f,
                        false, false, true, true, 4f, default);
                    Assert.True(planned.Plans != null, $"{RoadSpatialGrid.LastRefusal}; path {string.Join(", ", path!)}");
                    Assert.Equal(FordStyle.Wade, Assert.Single(planned.Crossings).Style);
                }
            }
        }
        finally { RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>()); }
    }

    [Fact]
    public void AStandingCrossingRoundTripsItsWaterSource()
    {
        var world = World();
        _scope.WithZdos();
        RoadNetworkPersistence.EnsureMetadataInstance();
        var crossings = RoadCrossingDetector.Detect(Line(), world, bridges: true);
        RoadSpatialGrid.CommitLevelled(Line(), Line().Select(_ => 31f).ToList(), 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), crossings, new HashSet<Vector2s>());
        byte[] bytes = RoadNetworkPersistence.SavedRecord("ProceduralRoads_Crossings")!;
        RoadSpatialGrid.Clear();
        var loaded = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.TryLoadGlobalRoadData(new List<(Vector2, string)>(), loaded, new HashSet<Vector2s>()));
        Assert.True(Assert.Single(loaded).Shallow);
        Assert.Equal(bytes, RoadNetworkPersistence.SerializeRoadCrossings(loaded));
    }

    // A pool 2 m deep from x -14 to 0 and, past a bank a metre or more wide, a shallow pool ShallowDepth deep from x Gap to
    // Gap + 10: a span's end with another standing-water crossing on its road at once, as at a swamp ford on the tour world
    // (a 14 m span over 2 m of water, its end 1.8 m from a raise over 0.2 m).
    private sealed class SpanBesideAPool : WorldGenerator
    {
        public float Gap = 1.5f, ShallowDepth = 0.2f, ShallowSlope = 0.5f;
        public override float GetHeight(float x, float z)
        {
            if (Mathf.Abs(x) > 128f || Mathf.Abs(z) > 64f) return 0f;
            float deep = RoadConstants.SeaLevel + Mathf.Clamp((Mathf.Abs(x + 7f) - 7f) * 0.35f, -2f, 1f);
            float shallow = RoadConstants.SeaLevel + Mathf.Clamp((Mathf.Abs(x - Gap - 5f) - 5f) * ShallowSlope, -ShallowDepth, 1f);
            return Mathf.Min(deep, shallow);
        }
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Swamp;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    // Raised: the shallow pool is raised beside the span (KeepStructuresApart). Waded: it is waded, its bank on the span
    // end's levelled stretch, the road's points from 3 m or 5 m past the cell grid.
    [Theory]
    [InlineData(1.5f, 0.2f, 0.5f, 0f)]
    [InlineData(4f, 0.3f, 0.3f, 3f)]
    [InlineData(4f, 0.3f, 0.3f, 5f)]
    [InlineData(4.5f, 0.2f, 0.25f, 4f)]
    public void AStructuresEndIsMetLevelWhereAnotherCrossingAdjoinsIt(float gap, float depth, float shallowSlope, float shift)
    {
        var world = new SpanBesideAPool { Gap = gap, ShallowDepth = depth, ShallowSlope = shallowSlope };
        _scope.WithWorld(world);
        List<Vector2> Path() => Enumerable.Range(-6, 13).Select(i => new Vector2(i * 8f + shift, 0f)).ToList();
        var finder = new RoadPathfinder(world) { Fords = true, Bridges = true };
        foreach (bool trial in new[] { false, true })
        {
            var path = Path();
            var result = trial
                ? RoadNetworkGenerator.PlanFromPath(finder, path, path[0], 0f, path.Last(), 0f, false, false, true, true, 4f)
                : RoadNetworkGenerator.PlanCandidate(finder, path, path[0], 0f, path.Last(), 0f, false, false, true, true, 4f);
            Assert.True(result.Plans != null, RoadSpatialGrid.LastRefusal);
            Assert.Equal(2, result.Crossings.Count);
            // Every structure end: the road over its levelled stretch (ApronLength from its foot) at the foot's height,
            // whatever crossing that road runs over.
            foreach (RoadCrossing c in result.Crossings)
            {
                var (near, far) = BridgeStructures.Ends(c, world);
                if (!near.IsSet) continue;
                foreach (StructureEnds.End end in new[] { near, far })
                {
                    var onIt = result.Plans!.SelectMany(p => p.Points.Select((at, i) => (at, h: p.Heights[i])))
                        .Where(x => Vector2.Distance(x.at, end.Meet) < StructureEnds.ApronLength).ToList();
                    Assert.NotEmpty(onIt);
                    Assert.All(onIt, x => Assert.True(Mathf.Abs(x.h - end.MeetHeight) <= 0.05f,
                        FormattableString.Invariant($"{(trial ? "trial" : "candidate")}: the road at ({x.at.x:F1}, {x.at.y:F1}) stands {x.h:F2}, the end of {c.Style} {c.FromBank} -> {c.ToBank} at {end.MeetHeight:F2}")));
                }
            }
        }
    }
}
