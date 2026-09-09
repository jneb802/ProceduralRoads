using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Fords prototype (config Fords/Enabled, off by default). With fords off
/// nothing changes. With fords on the pathfinder jumps a knee-deep river,
/// the crossing is detected on the finished road and painted in its style,
/// later roads share an earlier road's crossing, and the crossings survive
/// a save and load.
/// </summary>
public class FordTests
{
    // ---- worlds ----

    /// <summary>A knee-deep gully: bed at Bed for |x| &lt; HalfWidth, banks at
    /// 33 (+EastRise on the east). River core under it, so it is a crossing,
    /// not a puddle; wider than a knight move, so no cell-to-cell step clears
    /// it. Sea beyond |x|, |y| = 100.</summary>
    private sealed class GullyWorld : WorldGenerator
    {
        public float Bed = 29.5f;
        public float HalfWidth = 12f;
        public float EastRise = 0f;
        public Heightmap.Biome Land = Heightmap.Biome.Meadows;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 100f || Mathf.Abs(wy) > 100f) return 20f;
            if (Mathf.Abs(wx) < HalfWidth) return Bed;
            return wx > 0f ? 33f + EastRise : 33f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Land;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Clamp01(1f - Mathf.Abs(wx) / (HalfWidth * 2f));
            width = weight > 0f ? HalfWidth * 4f : 0f;
        }
    }

    /// <summary>A knee-deep band with no river under it.</summary>
    private sealed class BandWorld : WorldGenerator
    {
        public Heightmap.Biome Land = Heightmap.Biome.Meadows;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 100f || Mathf.Abs(wy) > 100f) return 20f;
            return Mathf.Abs(wx) < 14f ? 29.5f : 33f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Land;
    }

    private static RoadPathfinder Pathfinder(WorldGenerator world, bool fords) =>
        new RoadPathfinder(world) { Fords = fords };

    /// <summary>The one long segment of a path whose middle lies over river core.</summary>
    private static (Vector2 a, Vector2 b)? FindJump(List<Vector2> path, WorldGenerator world)
    {
        for (int i = 1; i < path.Count; i++)
        {
            if (Vector2.Distance(path[i - 1], path[i]) <= RoadPathfinder.CellSize * 1.5f) continue;
            Vector2 mid = (path[i - 1] + path[i]) * 0.5f;
            world.GetRiverWeight(mid.x, mid.y, out float w, out _);
            if (w > RoadConstants.RiverImpassableThreshold) return (path[i - 1], path[i]);
        }
        return null;
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    private static void TearDownGeneration()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadCrossingDetector.SetFordStyleWeights(1f, 1f);
        WorldGenerator.instance = null;
    }

    // ---- pathfinder ----

    [Fact]
    public void FordsAreOffUnlessConfigured()
    {
        Assert.False(RoadPathfinder.FordsEnabled);
        Assert.False(new RoadPathfinder(new SyntheticWorld()).Fords);
    }

    [Fact]
    public void WithFordsOffRiversBlockAsBeforeAndDryRoadsAreUnchanged()
    {
        Assert.Null(Pathfinder(new GullyWorld(), false).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f)));

        var dry = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = dry;
        try
        {
            byte[]? Generate(bool fords)
            {
                RoadNetworkGenerator.Reset();
                SetPathfinder(Pathfinder(dry, fords));
                Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-300f, -100f), 0f, new Vector2(200f, 150f), 0f, 4f, "Dry"));
                Assert.Empty(RoadNetworkGenerator.GetRoadCrossings());
                return RoadSpatialGrid.SerializeAllRoadPoints();
            }
            byte[]? on = Generate(true);
            Assert.NotNull(on);
            Assert.Equal(on, Generate(false));
        }
        finally { TearDownGeneration(); }
    }

    [Fact]
    public void AKneeDeepRiverIsFordedButDeeperOrWiderWaterStillBlocks()
    {
        var world = new GullyWorld();
        var path = Pathfinder(world, true).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f));
        Assert.NotNull(path);
        var jump = FindJump(path!, world);
        Assert.True(jump.HasValue, "Expected one jump across the gully");
        Assert.InRange(Vector2.Distance(jump!.Value.a, jump.Value.b), RoadPathfinder.CellSize * 2f, RoadConstants.MaxRiverCrossingCells * RoadPathfinder.CellSize);
        foreach (Vector2 p in path!)
        {
            world.GetRiverWeight(p.x, p.y, out float weight, out _);
            Assert.True(weight <= RoadConstants.RiverImpassableThreshold, $"Waypoint {p} sits in the river core");
            Assert.True(world.GetHeight(p.x, p.y) >= RoadConstants.ShallowWaterHeight, $"Waypoint {p} is in the water");
        }

        // Deeper than wading: no ford, and no other way across.
        Assert.Null(Pathfinder(new GullyWorld { Bed = 28.5f }, true).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f)));
        // Wider than the ford cap.
        Assert.Null(Pathfinder(new GullyWorld { HalfWidth = 30f }, true).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f)));
        // Knee-deep water with no river under it (a pond) is not forded outside a swamp.
        Assert.Null(Pathfinder(new BandWorld(), true).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f)));
    }

    [Fact]
    public void BanksMustBeNearLevel()
    {
        Assert.NotNull(Pathfinder(new GullyWorld { EastRise = RoadConstants.MaxFordBankDelta - 1f }, true).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f)));
        Assert.Null(Pathfinder(new GullyWorld { EastRise = RoadConstants.MaxFordBankDelta + 1f }, true).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f)));
    }

    [Fact]
    public void SwampShallowsAreWadedWithFordsOnAndOtherShallowsAreNot()
    {
        var swamp = new BandWorld { Land = Heightmap.Biome.Swamp };
        Assert.NotNull(Pathfinder(swamp, true).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f)));
        Assert.Null(Pathfinder(swamp, false).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f)));
        Assert.Null(Pathfinder(new BandWorld { Land = Heightmap.Biome.Meadows }, true).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f)));
    }

    // ---- detection and painting ----

    [Fact]
    public void TheCrossingHasDryBanksOnTheJumpAndAStyle()
    {
        var world = new GullyWorld();
        var path = Pathfinder(world, true).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f))!;
        var crossing = Assert.Single(RoadCrossingDetector.Detect(path, world));
        Assert.Equal(CrossingKind.Ford, crossing.Kind);
        Assert.NotEqual(FordStyle.None, crossing.Style);
        Assert.Equal(crossing.FromIndex + 1, crossing.ToIndex);
        Assert.True(RoadCrossingDetector.IsRoadGround(crossing.FromBank, world), "FromBank is not road ground");
        Assert.True(RoadCrossingDetector.IsRoadGround(crossing.ToBank, world), "ToBank is not road ground");
        Assert.InRange(crossing.Width, 22f, 36f); // straight or diagonal jump
        Assert.Equal(world.Bed, crossing.RiverbedHeight, 2);
        Assert.Equal(0f, crossing.FairwayWidth);
        Assert.Equal(RoadConstants.SeaLevel, crossing.WaterLevel);
        Assert.InRange(crossing.Direction.magnitude, 0.99f, 1.01f);

        // The banks lie on the jump segment, so the crossing lies on the road.
        Vector2 a = path[crossing.FromIndex], b = path[crossing.ToIndex];
        Vector2 dir = (b - a).normalized;
        foreach (Vector2 bank in new[] { crossing.FromBank, crossing.ToBank })
        {
            Vector2 rel = bank - a;
            Assert.True(Mathf.Abs(rel.x * dir.y - rel.y * dir.x) < 0.1f, $"Bank {bank} is off the jump");
        }

        // Deterministic.
        var again = Assert.Single(RoadCrossingDetector.Detect(new List<Vector2>(path), world));
        Assert.Equal(crossing.FromBank, again.FromBank);
        Assert.Equal(crossing.Style, again.Style);

        // A dry road has none.
        var dry = new SyntheticWorld { HasRiver = false, HasMountain = false };
        Assert.Empty(RoadCrossingDetector.Detect(Pathfinder(dry, true).FindPath(new Vector2(-300f, -100f), new Vector2(200f, 150f))!, dry));
    }

    [Theory]
    [InlineData(FordStyle.Wade)]
    [InlineData(FordStyle.Raise)]
    public void EachFordStyleTreatsTheShallowsItsOwnWay(FordStyle style)
    {
        var world = new GullyWorld();
        WorldGenerator.instance = world;
        RoadNetworkGenerator.Reset();
        SetPathfinder(Pathfinder(world, true));
        RoadCrossingDetector.SetFordStyleWeights(style == FordStyle.Wade ? 1f : 0f, style == FordStyle.Raise ? 1f : 0f);
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "ford"));
            var crossing = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
            Assert.Equal(style, crossing.Style);

            var middle = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(crossing.Center.x, 0f, crossing.Center.y), 3f);
            Assert.NotEmpty(middle);
            if (style == FordStyle.Wade)
                Assert.All(middle, p => Assert.InRange(p.h, world.Bed - 0.01f, world.Bed + 0.01f));
            else
                Assert.All(middle, p => Assert.True(p.h >= RoadPathfinder.LandingFloor - 0.01f, $"raised ford point at {p.h:F2}"));

            // Both banks are served by road on the land side too.
            foreach (Vector2 bank in new[] { crossing.FromBank, crossing.ToBank })
                Assert.True(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(bank.x, 0f, bank.y), 6f).Count > 0, $"No road within 6 m of the bank at {bank}");
        }
        finally { TearDownGeneration(); }
    }

    [Fact]
    public void FordStylesFollowTheWeights()
    {
        var eligible = new List<FordStyle> { FordStyle.Raise, FordStyle.Wade };
        try
        {
            RoadCrossingDetector.SetFordStyleWeights(0f, 0f);
            Assert.Equal(FordStyle.Raise, RoadCrossingDetector.PickFordStyle(eligible, 12345));
            RoadCrossingDetector.SetFordStyleWeights(1f, 1f);
            var even = Enumerable.Range(0, 200).Select(h => RoadCrossingDetector.PickFordStyle(eligible, h * 7919)).ToList();
            Assert.Contains(FordStyle.Wade, even);
            Assert.Contains(FordStyle.Raise, even);
            RoadCrossingDetector.SetFordStyleWeights(5f, 0f);
            Assert.All(Enumerable.Range(0, 200), h => Assert.Equal(FordStyle.Wade, RoadCrossingDetector.PickFordStyle(eligible, h * 7919)));
            // Wading is offered only where the water is ankle deep; a deeper gully raises.
            var deeper = new GullyWorld { Bed = RoadConstants.SeaLevel - RoadConstants.FordWadeMaxDepth - 0.1f };
            var path = Pathfinder(deeper, true).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f))!;
            Assert.Equal(FordStyle.Raise, Assert.Single(RoadCrossingDetector.Detect(path, deeper)).Style);
        }
        finally { RoadCrossingDetector.SetFordStyleWeights(1f, 1f); }
    }

    [Fact]
    public void ALaterRoadJoinsTheFirstFordInsteadOfCrossingBesideIt()
    {
        var world = new GullyWorld();
        WorldGenerator.instance = world;
        RoadNetworkGenerator.Reset();
        try
        {
            var alone = Pathfinder(world, true).FindPath(new Vector2(-80f, 40f), new Vector2(80f, 40f));
            Assert.NotNull(alone);
            var aloneJump = FindJump(alone!, world)!.Value;

            SetPathfinder(Pathfinder(world, true));
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "first"));
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 40f), 0f, new Vector2(80f, 40f), 0f, 4f, "second"));

            var crossings = RoadNetworkGenerator.GetRoadCrossings();
            Assert.Equal(2, crossings.Count);
            Assert.True(Mathf.Abs(aloneJump.a.y - crossings[0].FromBank.y) > RoadCrossing.SharedBankRadius,
                $"the lone second road already crossed where the first does (y={aloneJump.a.y:F0})");
            Assert.True(RoadCrossing.SameBanks(crossings[0], crossings[1]),
                $"second road crossed at {crossings[1].FromBank}-{crossings[1].ToBank}, the first at {crossings[0].FromBank}-{crossings[0].ToBank}");

            // Sharing saves half the crossing price: a third road from twice as
            // far away still comes to the first crossing rather than pay for its own.
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, -80f), 0f, new Vector2(80f, -80f), 0f, 4f, "third"));
            Assert.Equal(3, crossings.Count);
            Assert.True(RoadCrossing.SameBanks(crossings[0], crossings[2]),
                $"third road crossed at {crossings[2].FromBank}-{crossings[2].ToBank} instead of the shared crossing");
        }
        finally { TearDownGeneration(); }
    }

    // ---- review regressions ----

    /// <summary>A riverbed sloping across the road: wading must leave it alone.</summary>
    private sealed class SlopedBedWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 29.5f + wy * 0.2f;
    }

    [Fact]
    public void AWadedFordPaintsButDoesNotLevelTheBed()
    {
        var world = new SlopedBedWorld();
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        try
        {
            RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-25f, 0f), new(25f, 0f) }, 4f, world, followTerrain: true);
            var zone = new Vector2s(0, 0);
            Heightmap hm = Heightmap.CreateForZone(zone);
            var points = RoadSpatialGrid.GetRoadPointsInZone(zone);
            Assert.NotEmpty(points);
            Assert.All(points, p => Assert.True(p.paintOnly));
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, hm.m_terrainComp!);

            TerrainComp tc = hm.m_terrainComp!;
            Assert.DoesNotContain(tc.m_modifiedHeight, m => m);
            Assert.All(tc.m_levelDelta, d => Assert.True(Mathf.Abs(d) < 0.001f, $"wade changed the terrain by {d:F3} m"));
            Assert.Contains(tc.m_modifiedPaint, m => m);

            // The flag survives the network's save format.
            byte[] data = RoadSpatialGrid.SerializeAllRoadPoints()!;
            RoadSpatialGrid.Clear();
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(data));
            Assert.All(RoadSpatialGrid.GetRoadPointsInZone(zone), p => Assert.True(p.paintOnly));
        }
        finally { RoadSpatialGrid.Clear(); WorldGenerator.instance = null; }
    }

    [Fact]
    public void TheNetworkVersionChangesWhenAFordTurnsFromWadedToRaised()
    {
        // Zones are stamped with the network version and skip an unchanged
        // network on load; a point that stops being paint-only changes the
        // terrain and so must change the version, whatever its height.
        var world = new SlopedBedWorld();
        WorldGenerator.instance = world;
        try
        {
            var path = new List<Vector2> { new(-25f, 0f), new(25f, 0f) };
            RoadSpatialGrid.Clear();
            RoadSpatialGrid.AddRoadPath(path, 4f, world, followTerrain: true);
            RoadSpatialGrid.FinalizeRoadNetwork();
            int waded = RoadSpatialGrid.RoadNetworkVersion;

            // Same points and heights, leveling on.
            RoadSpatialGrid.Clear();
            RoadSpatialGrid.AddRoadPath(path, 4f, world, followTerrain: false, minHeight: float.NegativeInfinity);
            var points = RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0, 0));
            RoadSpatialGrid.Clear();
            RoadSpatialGrid.AddRoadPath(path, 4f, world, followTerrain: true);
            var wadedPoints = RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0, 0));
            Assert.Equal(wadedPoints.Count, points.Count);

            RoadSpatialGrid.Clear();
            RoadSpatialGrid.AddRoadPath(path, 4f, world, followTerrain: false, minHeight: float.NegativeInfinity);
            // The plane slope smooths to itself, so only the flag differs.
            RoadSpatialGrid.FinalizeRoadNetwork();
            int leveled = RoadSpatialGrid.RoadNetworkVersion;
            Assert.NotEqual(0, waded);
            Assert.NotEqual(waded, leveled);
        }
        finally { RoadSpatialGrid.Clear(); WorldGenerator.instance = null; }
    }

    /// <summary>A knee-deep gully with a 4 m deep channel hidden between the
    /// 8 m cell samples at x = 0 and x = 8.</summary>
    private sealed class HiddenChannelWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 100f || Mathf.Abs(wy) > 100f) return 20f;
            if (Mathf.Abs(wx) >= 12f) return 33f;
            return Mathf.Abs(wx - 4f) < 2f ? 26f : 29.5f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Abs(wx) < 12f ? 1f : 0f;
            width = 24f;
        }
    }

    [Fact]
    public void ARoadSegmentOverDeepWaterIsNotRecordedAsAFord()
    {
        // An ordinary move whose interior dips into a channel (today's
        // behaviour between two dry cells) is not a ford: nothing is
        // recorded, and nothing is raised over sailable water.
        var world = new HiddenChannelWorld();
        var segment = new List<Vector2> { new(-8f, 0f), new(8f, -16f) };
        Assert.True(world.GetHeight(4f, -12f) < RoadConstants.SeaLevel - RoadConstants.FordWadeDepth);
        Assert.Empty(RoadCrossingDetector.Detect(segment, world));

        // The same move over knee-deep water is a ford.
        var shallow = new GullyWorld();
        var ford = Assert.Single(RoadCrossingDetector.Detect(new List<Vector2> { new(-16f, 0f), new(16f, 0f) }, shallow));
        Assert.Equal(CrossingKind.Ford, ford.Kind);
    }

    [Fact]
    public void AChannelHiddenBetweenCellSamplesIsNotForded()
    {
        var world = new HiddenChannelWorld();
        Assert.Null(Pathfinder(world, true).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f)));
    }

    // ---- persistence ----

    [Fact]
    public void CrossingsSurviveASaveAndLoad()
    {
        var world = new GullyWorld();
        WorldGenerator.instance = world;
        ZDOMan.instance = new ZDOMan();
        RoadNetworkGenerator.Reset();
        SetPathfinder(Pathfinder(world, true));
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "ford"));
            var saved = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
            RoadSpatialGrid.FinalizeRoadNetwork();
            RoadNetworkPersistence.EnsureMetadataInstance();
            typeof(RoadNetworkGenerator).GetField("m_roadsGenerated", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, true);
            RoadNetworkGenerator.SaveGlobalRoadData();

            RoadNetworkGenerator.Reset();
            Assert.Empty(RoadNetworkGenerator.GetRoadCrossings());
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), "saved network did not load back");
            var loaded = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
            Assert.Equal(saved.FromBank, loaded.FromBank);
            Assert.Equal(saved.ToBank, loaded.ToBank);
            Assert.Equal(saved.RiverbedHeight, loaded.RiverbedHeight);
            Assert.Equal(saved.FairwayCenter, loaded.FairwayCenter);
            Assert.Equal(saved.FairwayWidth, loaded.FairwayWidth);
            Assert.Equal(saved.Kind, loaded.Kind);
            Assert.Equal(saved.Style, loaded.Style);
            Assert.Equal(saved.Width, loaded.Width);
            Assert.Equal(saved.Direction, loaded.Direction);

            // A network saved without crossings loads back with none.
            RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2 position, string label)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());
            var none = new List<RoadCrossing> { saved };
            Assert.True(RoadNetworkPersistence.TryLoadGlobalRoadData(new List<(Vector2 position, string label)>(), none, new HashSet<Vector2s>()));
            Assert.Empty(none);
        }
        finally
        {
            RoadNetworkPersistence.Reset();
            ZDOMan.instance = null;
            TearDownGeneration();
        }
    }
}
