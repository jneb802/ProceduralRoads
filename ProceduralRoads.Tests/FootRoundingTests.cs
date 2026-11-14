using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// The one rounding of a staircase foot's drop to whole stairs (StairLayout.StairsDownTo), on every path that lowers a foot: a
/// foot over its road's ground (StairLayout.LowerFoot, generation, marked manual roads and approach trials through
/// RoadStairRuns.Lay) and a foot carried to a road held at a crossing's end (StairLayout.FootToRoad). A leftover within a step
/// keeps the shorter flight; one up to half a rise keeps it with the road's landing raised to the foot where the road's earthwork
/// may (RoadStairRuns.LandingRaised), else the stair is added; past half a rise the stair is added.
/// The foot is the mound's (-9308.1,690.3: the bottom of the stairs stood 2.3 m over its road).
/// </summary>
public class FootRoundingTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public void Dispose()
    {
        RoadNetworkGenerator.RefusedFirst = null;
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>The mound at the foot at -9308.1,690.3: its foot 2.3 m up, the ground falling 0.46 per metre toward the road.</summary>
    private static float Mound(float x, float z) => z < -5f ? 0f : z < 0f ? 0.46f * (z + 5f) : 2.3f + 0.5f * z;

    private static StairPlan OnTheMound()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 20f), Mound, arriving: 0f, sunkFoot: true);
        Assert.True(plan.Reached && plan.SunkPieces > 0 && plan.Foot.HasValue, plan.Reason);
        Assert.Equal(2.3f, plan.Foot!.Value.y, 2);
        return plan;
    }

    /// <summary>The foot over its road's ground by <paramref name="over"/> (the road's ground back from it, no climb): 1.1 m
    /// (leftover 0.1, within a step) takes one stair; 1.3 m (leftover 0.3) one stair, the road's landing raised 0.3 m to the
    /// foot, where the earthwork may, else two; 1.7 m (leftover 0.7) two. Rounded up alone (the parent), 1.3 m took two
    /// stairs and its landing was cut 0.7 m into the ground.</summary>
    [Theory]
    [InlineData(1.1f, true, 1, 0f)]
    [InlineData(1.3f, true, 1, 0.3f)]
    [InlineData(1.3f, false, 2, 0f)]
    [InlineData(1.7f, true, 2, 0f)]
    [InlineData(0.3f, true, 0, 0.3f)]
    [InlineData(0.3f, false, 1, 0f)]
    public void AFootsLeftoverKeepsTheShorterFlightOnlyWhereItsLandingIsRaised(float over, bool raises, int stairs, float raised)
    {
        StairPlan plan = OnTheMound();
        float foot = plan.Foot!.Value.y;
        var asked = new List<(Vector3 foot, float fill)>();
        Vector3? low = StairLayout.LowerFoot(plan, Mound, 2f, foot - over, 5f, 0f, (f, fill) => { asked.Add((f, fill)); return raises; });
        Assert.Equal(stairs, plan.FootLowered);
        Assert.Equal(stairs > 0, low.HasValue);
        Assert.Equal(raised, plan.FootRaised, 3);
        float left = over - stairs * StairLayout.PieceRise;
        if (raised > 0f)
        {
            // Asked once, with the foot the shorter flight leaves: its stairs on down the mound, the leftover over its road.
            var (at, fill) = Assert.Single(asked);
            Assert.Equal(left, fill, 3);
            Assert.Equal(foot - stairs * StairLayout.PieceRise, at.y, 3);
            Assert.Equal(-stairs * StairLayout.PieceRun, at.z, 2);
            return;
        }
        // Within a step of its road, or under it by less than a rise (a stair a bit sunk).
        Assert.True(StairLayout.Walkable(left, 0f) || left < 0f && left > -StairLayout.PieceRise, $"left {left} m off its road");
    }

    /// <summary>Nothing to raise the road (no earthwork verdict, as at a road held at a height): the stair is added.</summary>
    [Fact]
    public void AFootWithNoEarthworkToRaiseItsRoadAddsTheStair()
    {
        StairPlan plan = OnTheMound();
        StairLayout.LowerFoot(plan, Mound, 2f, plan.Foot!.Value.y - 1.3f, 5f, 0f);
        Assert.Equal(2, plan.FootLowered);
        Assert.Equal(0f, plan.FootRaised);
    }

    /// <summary>Parity (BRIEF rule 13): the same drop, with nothing raising the road, takes the same walked stairs over its road's
    /// ground (LowerFoot: stairs added) and over a road held at a height (FootToRoad: stairs carried, and the one added sunk
    /// into the road under the foot where it would stand more than a step under it), and both are the one rounding's.</summary>
    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    [InlineData(0.7f)]
    [InlineData(1.1f)]
    [InlineData(1.3f)]
    [InlineData(1.45f)]
    [InlineData(1.7f)]
    [InlineData(1.9f)]
    public void AFootRoundsTheSameAtItsRoadsGroundAndAtARoadHeldAtAHeight(float over)
    {
        int rule = StairLayout.StairsDownTo(over);
        StairPlan lowered = OnTheMound();
        StairLayout.LowerFoot(lowered, Mound, 2f, lowered.Foot!.Value.y - over, 5f, 0f, (_, _) => false);
        StairPlan carried = OnTheMound();
        var met = StairLayout.FootToRoad(carried, Mound, 2f, carried.Foot!.Value.y - over);
        Assert.True(met.HasValue, "the foot does not meet the held road");
        int walked = carried.FootLowered + (met!.Value.sunk ? 1 : 0);
        Assert.Equal(rule, lowered.FootLowered);
        Assert.Equal(rule, walked);
    }

    /// <summary>The one rounding's cases: a leftover within a step keeps the shorter flight; over it, up to half a rise, only
    /// where the landing is raised; past half a rise, never.</summary>
    [Theory]
    [InlineData(0.15f, false, 0)]
    [InlineData(0.25f, true, 0)]
    [InlineData(0.25f, false, 1)]
    [InlineData(0.5f, true, 0)]
    [InlineData(0.55f, true, 1)]
    [InlineData(2.2f, false, 2)]
    [InlineData(2.45f, true, 2)]
    [InlineData(2.45f, false, 3)]
    [InlineData(-0.4f, true, 0)]
    public void TheOneRoundingKeepsTheShorterFlightUpToHalfARiseOnARaisedLanding(float over, bool raises, int stairs) =>
        Assert.Equal(stairs, StairLayout.StairsDownTo(over, (_, _) => raises));

    /// <summary>A world dry at 40 m but for a pool from 3 m back along the road (z under -2.5, its ground at 29 m).</summary>
    private sealed class PoolWorld : WorldGenerator
    {
        public bool Pool;
        public override float GetHeight(float wx, float wy) => Pool && wy < -2.5f ? 29f : 40f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 77;
    }

    /// <summary>The road's earthwork may raise the landing to a foot 0.3 m over flat dry ground; not where the landing stands in
    /// water (a fill over a pool holds nothing), nor past the earthwork cap (RoadNetworkGenerator.PastTerrainLimit).</summary>
    [Fact]
    public void ALandingIsRaisedOnlyWhereTheRoadsEarthworkMay()
    {
        var path = new List<Vector2> { new(0f, -16f), new(0f, -8f), new(0f, 0f) };
        var plan = new StairPlan { Reached = true, StartHeading = 0f, Foot = new Vector3(0f, 40.3f, 2f) };
        var foot = new Vector3(0f, 40.3f, 2f);
        bool Raised(bool pool)
        {
            var world = new PoolWorld { Pool = pool };
            _scope.WithWorld(world);
            RoadStairRuns.ForgetGround();
            return RoadStairRuns.LandingRaised(plan, foot, path, 2, -1, world, default);
        }
        Assert.True(Raised(pool: false));
        Assert.False(Raised(pool: true));
        using var cap = StaticOverride.Keep(() => RoadNetworkGenerator.EarthworkCap);
        RoadNetworkGenerator.EarthworkCap = 0.2f;
        Assert.False(Raised(pool: false));
    }
    /// <summary>Flat ground at 40 m, a shoulder rising 0.46 per metre for 8 m from x = 0, then a face of 1.2 for 28.8 m to x = 32 (its rim on a search cell line): a road
    /// east climbs the face on a staircase whose foot stands on the shoulder, 0.31 m over what its road climbs to it
    /// (no stair, the landing raised).</summary>
    private sealed class ShoulderWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f
            : wx < 0f ? 40f : wx < 8f ? 40f + 0.46f * wx : Mathf.Min(40f + 0.46f * 8f + 1.2f * (wx - 8f), 40f + 0.46f * 8f + 28.8f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    private static StairPlan GenerateShoulderRoad(ShoulderWorld world)
    {
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(100f, 0f), 0f, 4f, "shoulder"), RoadSpatialGrid.LastRefusal);
        StairRun run = Assert.Single(StairStructures.Runs);
        return RoadStairRuns.LaidPlan(run)!;
    }

    /// <summary>
    /// A road refused at its commit (the one list, RoadNetworkGenerator.RoadRefusal) with a foot kept on a raised landing is
    /// built once more with that foot's stair added (RoadNetworkGenerator.WithStairsAdded), before it is searched for again:
    /// generated through GenerateRoad, the commit refusing any road whose own foot stands on a raised landing (a test's
    /// refusal naming the landing, as an earthwork or connection refusal there would), the road is built, its foot on the
    /// stair added, and the generation log's counts say so. Unrefused, the same road keeps the foot on its raised landing.
    /// </summary>
    [Fact]
    public void ARoadRefusedAtItsCommitForARaisedLandingIsBuiltWithTheStairAdded()
    {
        var world = new ShoulderWorld();
        _scope.WithWorld(world);
        RoadNetworkGenerator.Reset(); StairStructures.Reset(); RoadSpatialGrid.Clear(); RoadStairRuns.ForgetGround();
        StairPlan kept = GenerateShoulderRoad(world);
        Assert.True(kept.FootRaised > StairLayout.StepUp && kept.FootRaised <= StairLayout.StairFitsFrom, $"raised {kept.FootRaised}");
        Assert.Equal(0, kept.FootLowered);
        Assert.Equal(0, RoadNetworkGenerator.m_stairsAddedCommit);

        RoadNetworkGenerator.Reset(); StairStructures.Reset(); RoadSpatialGrid.Clear(); RoadStairRuns.ForgetGround();
        int refusedRaised = 0;
        RoadNetworkGenerator.RefusedFirst = road =>
            road.Stairs.Find(st => !st.Existing && st.Plan.FootRaised > 0f) is RoadStairSpan raised && raised.Plan.Foot is Vector3 foot && ++refusedRaised > 0
                ? new RoadNetworkGenerator.Refusal(RoadNetworkGenerator.EarthworkRefusal, new Vector2(foot.x, foot.z)) : null;
        StairPlan added = GenerateShoulderRoad(world);
        Assert.True(refusedRaised >= 1, "the commit was never asked of the raised landing");
        Assert.Equal(0f, added.FootRaised);
        Assert.Equal(1, added.FootLowered);
        Assert.Equal(1, RoadNetworkGenerator.m_stairsAddedCommit);
        Assert.Equal(1, RoadNetworkGenerator.m_stairsAddedCommitSaved);

        // Refused far from the foot (at the road's start, 80 m off): the refusal names no raised landing, so the road is not
        // laid again with the stair added.
        RoadNetworkGenerator.Reset(); StairStructures.Reset(); RoadSpatialGrid.Clear(); RoadStairRuns.ForgetGround();
        RoadNetworkGenerator.RefusedFirst = road =>
            road.Stairs.Exists(st => !st.Existing && st.Plan.FootRaised > 0f)
                ? new RoadNetworkGenerator.Refusal(RoadNetworkGenerator.EarthworkRefusal, new Vector2(-80f, 0f)) : null;
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(100f, 0f), 0f, 4f, "shoulder");
        Assert.Equal(0, RoadNetworkGenerator.m_stairsAddedCommit);
    }
    private static Island Everywhere()
    {
        var i = new Island { Id = 1, CellSize = 8, WorldOffset = 0, Min = new Vector2(-280, -280), Max = new Vector2(280, 280) };
        for (int x = -35; x <= 35; x++) for (int z = -35; z <= 35; z++) i.Cells.Add(new Vector2Int(x, z));
        return i;
    }

    /// <summary>
    /// Parity of the retry (BRIEF rule 13): the shoulder road, its commit refusing any road whose own foot stands on a raised
    /// landing, is laid again with that foot's stair added by the one owner (RoadNetworkGenerator.WithStairsAdded) on every
    /// path that checks a road as planned, through its own caller: an approach trial (TrialBuilds) builds it, a marked manual
    /// road (ManualRoads.Prepare) is planned with the foot one stair lower. Unrefused, both keep the foot on its raised landing.
    /// </summary>
    [Fact]
    public void AnApproachTrialAndAMarkedRoadAreLaidAgainWithTheStairAddedAsGenerationIs()
    {
        var world = new ShoulderWorld();
        Vector2 a = new(-80f, 0f), b = new(100f, 0f);
        void Begin()
        {
            _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
            RoadNetworkGenerator.Reset(); StairStructures.Reset(); RoadSpatialGrid.Clear(); RoadStairRuns.ForgetGround();
            RoadNetworkGenerator.MarkLocationsReady();
            RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        }
        (bool trial, StairPlan? manual) Paths()
        {
            Begin();
            var finder = new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
            List<Vector2> cells = finder.FindPath(a, b)!;
            Assert.NotNull(cells);
            bool trial = RoadNetworkGenerator.TrialBuilds(finder, cells, a, 0f, b, 0f, false, false, false, false, 4f, 0f, 0f);
            Begin();
            ManualRoads.Plan plan = ManualRoads.Prepare(new[] { a, b }, false, new[] { Everywhere() });
            return (trial, Assert.Single(plan.Stairs).Plan);
        }

        var (trialAsIs, manualAsIs) = Paths();
        Assert.True(trialAsIs, "the trial is not built unrefused");
        Assert.True(manualAsIs!.FootRaised > StairLayout.StepUp, $"the marked road's foot raised {manualAsIs.FootRaised}");
        Assert.Equal(0, manualAsIs.FootLowered);

        int refusedRaised = 0;
        RoadNetworkGenerator.RefusedFirst = road =>
            road.Stairs.Find(st => !st.Existing && st.Plan.FootRaised > 0f) is RoadStairSpan raised && raised.Plan.Foot is Vector3 foot && ++refusedRaised > 0
                ? new RoadNetworkGenerator.Refusal(RoadNetworkGenerator.EarthworkRefusal, new Vector2(foot.x, foot.z)) : null;
        var (trial, manual) = Paths();
        Assert.True(refusedRaised >= 2, $"refused {refusedRaised} time(s)");
        Assert.True(trial, "the approach trial was not laid again with the stair added");
        Assert.Equal(0f, manual!.FootRaised);
        Assert.Equal(1, manual.FootLowered);
    }
}
