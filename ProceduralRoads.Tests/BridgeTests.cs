using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Bridges prototype (config Bridges/Enabled, off by default). With bridges
/// off nothing changes. With bridges on the pathfinder jumps a river where
/// there is no cheaper way around, the crossing is detected and left
/// unpaved, the wooden bridge planned at it stands on its own support and
/// leaves the fairway open, and the crossings survive a save and load.
/// </summary>
public class BridgeTests
{
    // ---- worlds ----

    /// <summary>A ship-sailable river: flat 4 m deep bed for |x| &lt; 35.
    /// The west bank rises gently over 10 m to a plateau at 32; the east
    /// bank is a wet shelf up to x = 41, then a step up to a plateau at
    /// 32 + EastRise, so the two banks differ in height by EastRise. The
    /// river core covers |x| &lt; 40 and the first dry cells outside it sit
    /// at |x| = 48: a 96 m jump. Sea beyond |x| = 220 and |y| = 120, so
    /// there is no way around.</summary>
    internal sealed class WideRiverWorld : WorldGenerator
    {
        public float EastRise;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 220f || Mathf.Abs(wy) > 120f) return 20f;
            float ax = Mathf.Abs(wx);
            if (ax <= 35f) return 26f;
            if (wx < 0f)
                return ax >= 45f ? 32f : Mathf.Lerp(26f, 32f, (ax - 35f) / 10f);
            return ax < 41f ? Mathf.Lerp(26f, 30.4f, (ax - 35f) / 6f) : 32f + EastRise;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Clamp01(1f - Mathf.Abs(wx) / 80f);
            width = weight > 0f ? 160f : 0f;
        }
    }

    /// <summary>The wide river ends at y = 600 and land continues north to
    /// y = 700, so the road can go around. The ground is rough everywhere
    /// except the level crossing approach, so the detour is honestly dear.</summary>
    private sealed class RiverWithAnEndWorld : WorldGenerator
    {
        private static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                return (h & 0xFFFF) / 65535f;
            }
        }
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 220f || wy < -120f || wy > 700f) return 20f;
            float ax = Mathf.Abs(wx);
            bool approach = ax <= 60f && Mathf.Abs(wy) <= 20f;
            float rough = approach ? 0f : (Hash(Mathf.FloorToInt(wx / 6f), Mathf.FloorToInt(wy / 6f)) - 0.5f) * 7f;
            if (wy > 600f) return 33f + rough;
            if (ax <= 35f) return 26f;
            if (ax >= 45f) return 33f + rough;
            return Mathf.Lerp(26f, 33f, (ax - 35f) / 10f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = wy > 600f ? 0f : Mathf.Clamp01(1f - Mathf.Abs(wx) / 80f);
            width = weight > 0f ? 160f : 0f;
        }
    }

    /// <summary>A lake: 40 m of water with no river under it, across the whole world.</summary>
    private sealed class LakeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) < 20f ? 27f : 33f;
    }

    private static RoadPathfinder Pathfinder(WorldGenerator world, bool bridges) =>
        new RoadPathfinder(world) { Fords = bridges, Bridges = bridges };

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

    private static (RoadCrossing crossing, WideRiverWorld world) WideCrossing(float eastRise = 0f)
    {
        var world = new WideRiverWorld { EastRise = eastRise };
        var path = Pathfinder(world, true).FindPath(new Vector2(-160f, 0f), new Vector2(160f, 0f));
        Assert.NotNull(path);
        var crossing = Assert.Single(RoadCrossingDetector.Detect(path!, world, true));
        return (crossing, world);
    }

    // ---- pathfinder ----

    [Fact]
    public void BridgesAreOffUnlessConfigured()
    {
        Assert.False(RoadPathfinder.BridgesEnabled);
        Assert.False(new RoadPathfinder(new SyntheticWorld()).Bridges);
    }

    [Fact]
    public void WithBridgesOffRiversBlockAsBefore()
    {
        Assert.Null(Pathfinder(new SyntheticWorld { HasRiver = true, HasMountain = false }, false)
            .FindPath(new Vector2(-300f, 0f), new Vector2(400f, 0f)));
        Assert.Null(Pathfinder(new WideRiverWorld(), false)
            .FindPath(new Vector2(-160f, 0f), new Vector2(160f, 0f)));
    }

    [Fact]
    public void WithBridgesOnTheRiverIsJumpedInOneSegment()
    {
        var world = new SyntheticWorld { HasRiver = true, HasMountain = false };
        var path = Pathfinder(world, true).FindPath(new Vector2(-300f, 0f), new Vector2(400f, 0f));
        Assert.NotNull(path);

        var jump = FindJump(path!, world);
        Assert.True(jump.HasValue, "Expected one jump across the river");
        float length = Vector2.Distance(jump!.Value.a, jump.Value.b);
        Assert.InRange(length, RoadPathfinder.CellSize * 2f, RoadConstants.MaxBridgeCrossingCells * RoadPathfinder.CellSize);

        // Both ends of the jump are ordinary road cells, and so is every other waypoint.
        foreach (Vector2 p in path!)
        {
            world.GetRiverWeight(p.x, p.y, out float weight, out _);
            Assert.True(weight <= RoadConstants.RiverImpassableThreshold, $"Waypoint {p} sits in the river core");
            Assert.True(world.GetHeight(p.x, p.y) >= RoadConstants.ShallowWaterHeight, $"Waypoint {p} is in the water");
        }
    }

    [Fact]
    public void AWideSailableRiverIsBridgedStraightAcross()
    {
        var world = new WideRiverWorld();
        var path = Pathfinder(world, true).FindPath(new Vector2(-160f, 0f), new Vector2(160f, 0f));
        Assert.NotNull(path);
        var jump = FindJump(path!, world);
        Assert.True(jump.HasValue);
        float dx = Mathf.Abs(jump!.Value.a.x - jump.Value.b.x);
        float dy = Mathf.Abs(jump.Value.a.y - jump.Value.b.y);
        Assert.InRange(dx, 90f, 100f); // dry cells at |x| = 48
        Assert.True(dy <= dx * 0.15f, $"Jump is oblique: dx={dx:F0} dy={dy:F0}");
    }

    [Fact]
    public void LakesAreNotBridged()
    {
        Assert.Null(Pathfinder(new LakeWorld(), true).FindPath(new Vector2(-100f, 0f), new Vector2(100f, 0f)));
    }

    [Fact]
    public void ARiverWiderThanTheCapStillBlocks()
    {
        var world = new SyntheticWorld { HasRiver = true, HasMountain = false, RiverHalfWidth = 170f }; // core 170 m > 128 m cap
        Assert.Null(Pathfinder(world, true).FindPath(new Vector2(-300f, 0f), new Vector2(400f, 0f)));
    }

    [Fact]
    public void BanksMustBeNearLevel()
    {
        Assert.NotNull(Pathfinder(new WideRiverWorld { EastRise = RoadConstants.MaxBridgeBankDelta - 1f }, true)
            .FindPath(new Vector2(-160f, 0f), new Vector2(160f, 0f)));
        Assert.Null(Pathfinder(new WideRiverWorld { EastRise = RoadConstants.MaxBridgeBankDelta + 1f }, true)
            .FindPath(new Vector2(-160f, 0f), new Vector2(160f, 0f)));
    }

    [Fact]
    public void ABridgeIsALastResort()
    {
        // The road goes around the river's end over 1.5 km of rough ground
        // rather than build a 96 m bridge: bridges appear where a river is
        // the only way.
        var world = new RiverWithAnEndWorld();
        var path = Pathfinder(world, true).FindPath(new Vector2(-160f, 0f), new Vector2(160f, 0f));
        Assert.NotNull(path);
        Assert.False(FindJump(path!, world).HasValue, "Path bridged the river instead of going around its end");
        Assert.True(path!.Any(p => p.y > 600f), "Path did not go around the river's end");

        // Cheap bridges are taken where dear ones are not: the lever works.
        var cheap = new RoadPathfinder(world) { Bridges = true, BridgeCostFixed = 1000f, BridgeCostPerMeter = 10f };
        var cheapPath = cheap.FindPath(new Vector2(-160f, 0f), new Vector2(160f, 0f));
        Assert.NotNull(cheapPath);
        Assert.True(FindJump(cheapPath!, world).HasValue, "A cheap bridge should be taken");
    }

    // ---- crossing detection ----

    [Fact]
    public void TheCrossingHasDryBanksOnTheJumpAndASailableFairway()
    {
        var world = new SyntheticWorld { HasRiver = true, HasMountain = false };
        var path = Pathfinder(world, true).FindPath(new Vector2(-300f, 0f), new Vector2(400f, 0f));
        Assert.NotNull(path);

        var crossing = Assert.Single(RoadCrossingDetector.Detect(path!, world, true));
        Assert.True(crossing.ToIndex > crossing.FromIndex);
        Assert.True(RoadCrossingDetector.IsRoadGround(crossing.FromBank, world), "FromBank is not road ground");
        Assert.True(RoadCrossingDetector.IsRoadGround(crossing.ToBank, world), "ToBank is not road ground");
        Assert.InRange(crossing.Width, 4f, 40f);
        Assert.True(crossing.RiverbedHeight < RoadConstants.DeepWaterHeight, $"Riverbed {crossing.RiverbedHeight:F1} is not deep water");
        Assert.True(crossing.FairwayWidth > 0f, "Expected a sailable fairway");
        world.GetRiverWeight(crossing.FairwayCenter.x, crossing.FairwayCenter.y, out float w, out _);
        Assert.True(w > 0f, "Fairway centre is not in the river");
        Assert.Equal(RoadConstants.SeaLevel, crossing.WaterLevel);
        Assert.InRange(crossing.Direction.magnitude, 0.99f, 1.01f);

        // The banks lie on the road: on the jump segment, or on the approach
        // where the deck springs from a bank top.
        foreach (Vector2 bank in new[] { crossing.FromBank, crossing.ToBank })
        {
            float nearest = float.MaxValue;
            for (int i = 1; i < path!.Count; i++)
            {
                Vector2 a = path[i - 1], b = path[i];
                float len = Vector2.Distance(a, b);
                if (len < 0.01f) continue;
                float t = Mathf.Clamp01(Vector2.Dot(bank - a, b - a) / (len * len));
                nearest = Mathf.Min(nearest, Vector2.Distance(bank, Vector2.Lerp(a, b, t)));
            }
            Assert.True(nearest < 0.1f, $"Bank {bank} is {nearest:F2} m off the road");
        }

        var again = RoadCrossingDetector.Detect(new List<Vector2>(path), world, true);
        Assert.Equal(crossing.FromBank, Assert.Single(again).FromBank);
        Assert.Equal(crossing.FairwayWidth, again[0].FairwayWidth);
    }

    [Fact]
    public void ADryPathHasNoCrossings()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        var path = Pathfinder(world, true).FindPath(new Vector2(-300f, -100f), new Vector2(200f, 150f));
        Assert.NotNull(path);
        Assert.Empty(RoadCrossingDetector.Detect(path!, world, true));
    }

    // ---- generation ----

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    private static void TearDownGeneration()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadCrossingDetector.SetFordStyleWeights(1f, 1f, 1f);
        WorldGenerator.instance = null;
    }

    [Fact]
    public void GenerateRoadLeavesTheWaterUnpavedAndRecordsTheCrossing()
    {
        var world = new SyntheticWorld { HasRiver = true, HasMountain = false };
        WorldGenerator.instance = world;
        RoadNetworkGenerator.Reset();
        SetPathfinder(Pathfinder(world, true));
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-300f, 0f), 0f, new Vector2(400f, 0f), 0f, 4f, "Cross river"));
            var crossing = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());

            // No road terrain lands in the river...
            RoadSpatialGrid.GetRoadWeight(crossing.FairwayCenter.x, crossing.FairwayCenter.y, out float wetWeight, out _);
            Assert.Equal(0f, wetWeight);

            // ...while both banks are served by painted road.
            foreach (Vector2 bank in new[] { crossing.FromBank, crossing.ToBank })
                Assert.True(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(bank.x, 0f, bank.y), 6f).Count > 0,
                    $"No road within 6 m of the bank at {bank}");
        }
        finally { TearDownGeneration(); }
    }

    [Fact]
    public void WithBridgesOffGenerationIsUnchanged()
    {
        // A dry road comes out the same with the feature on or off, and a
        // river with bridges off stays uncrossed with no crossing recorded.
        var dry = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = dry;
        try
        {
            byte[]? Generate(bool bridges)
            {
                RoadNetworkGenerator.Reset();
                SetPathfinder(Pathfinder(dry, bridges));
                Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-300f, -100f), 0f, new Vector2(200f, 150f), 0f, 4f, "Dry"));
                Assert.Empty(RoadNetworkGenerator.GetRoadCrossings());
                return RoadSpatialGrid.SerializeAllRoadPoints();
            }
            byte[]? on = Generate(true);
            byte[]? off = Generate(false);
            Assert.NotNull(on);
            Assert.Equal(on, off);

            var river = new SyntheticWorld { HasRiver = true, HasMountain = false };
            WorldGenerator.instance = river;
            RoadNetworkGenerator.Reset();
            SetPathfinder(Pathfinder(river, false));
            Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector2(-300f, 0f), 0f, new Vector2(400f, 0f), 0f, 4f, "Blocked"));
            Assert.Empty(RoadNetworkGenerator.GetRoadCrossings());
        }
        finally { TearDownGeneration(); }
    }

    // ---- layout ----

    [Fact]
    public void ThePlanIsDeterministic()
    {
        var (crossing, world) = WideCrossing();
        var a = BridgeLayout.Solve(crossing, world, 12345);
        var b = BridgeLayout.Solve(crossing, world, 12345);
        Assert.NotEmpty(a);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Prefab, b[i].Prefab);
            Assert.Equal(a[i].Position, b[i].Position);
            Assert.Equal(a[i].HealthFraction, b[i].HealthFraction);
        }
        var c = BridgeLayout.Solve(crossing, world, 99999);
        Assert.True(c.Count != a.Count || c.Where((p, i) => Vector3.Distance(p.Position, a[i].Position) > 0f || p.HealthFraction != a[i].HealthFraction).Any(),
            "Different seeds should ruin the bridge differently");
    }

    [Fact]
    public void TheNavigationGapIsLeftOpen()
    {
        var (crossing, world) = WideCrossing();
        Assert.True(crossing.FairwayWidth >= 60f, "The whole 4 m deep bed is sailable");
        var plan = BridgeLayout.Solve(crossing, world, 42);
        Assert.NotEmpty(plan);

        float fairwayMid = crossing.Along(crossing.FairwayCenter);
        float gapHalf = BridgeLayout.FairwayGap(crossing) * 0.5f;
        Assert.True(gapHalf >= BridgeLayout.FairwayGapWidth * 0.5f);
        foreach (var piece in plan)
        {
            float along = crossing.Along(new Vector2(piece.Position.x, piece.Position.z));
            float reach = piece.Kind == BridgePieceKind.Deck ? BridgeLayout.DeckSpan * 0.5f : 0f;
            Assert.True(Mathf.Abs(along - fairwayMid) - reach >= gapHalf - 0.01f,
                $"{piece.Kind} at along={along:F1} inside the {BridgeLayout.FairwayGap(crossing):F0} m navigation gap around {fairwayMid:F1}");
        }

        // Piers march in from BOTH banks: a ruined bridge, not two abutments.
        int west = plan.Count(p => p.Kind == BridgePieceKind.Post && p.Position.x < 0f);
        int east = plan.Count(p => p.Kind == BridgePieceKind.Post && p.Position.x > 0f);
        Assert.True(west >= 4 && east >= 4, $"Piers west={west} east={east}");
        Assert.Contains(plan, p => p.Kind == BridgePieceKind.Deck);
        Assert.Contains(plan, p => p.Kind == BridgePieceKind.Beam);
    }

    [Fact]
    public void TheDeckStaysAboveTheWaterAndMeetsTheBanks()
    {
        var (crossing, world) = WideCrossing(eastRise: 1.5f);
        var plan = BridgeLayout.Solve(crossing, world, 7);
        (float fromH, float toH) = BridgeLayout.DeckEndHeights(crossing, world);
        Assert.InRange(fromH, world.GetHeight(crossing.FromBank.x, crossing.FromBank.y) - 0.01f, world.GetHeight(crossing.FromBank.x, crossing.FromBank.y) + 0.01f);
        Assert.InRange(toH, world.GetHeight(crossing.ToBank.x, crossing.ToBank.y) - 0.01f, world.GetHeight(crossing.ToBank.x, crossing.ToBank.y) + 0.01f);

        foreach (var deck in plan.Where(p => p.Kind == BridgePieceKind.Deck))
        {
            Assert.True(deck.Position.y >= crossing.WaterLevel + BridgeLayout.DeckFreeboard - 0.01f, "Deck below the freeboard");
            float t = crossing.Along(new Vector2(deck.Position.x, deck.Position.z)) / crossing.Width;
            Assert.InRange(deck.Position.y, Mathf.Lerp(fromH, toH, t) - 0.3f, Mathf.Lerp(fromH, toH, t) + 0.3f);
        }

        // The bank stations stand on the banks themselves, and each end is a
        // stair down from the deck edge into the bank: its top meets the
        // deck, its foot is in the dirt.
        foreach ((Vector2 bank, float deckH) in new[] { (crossing.FromBank, fromH), (crossing.ToBank, toH) })
        {
            Assert.Contains(plan, p => p.Kind == BridgePieceKind.Beam && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), bank) < 0.1f);
            var stairs = plan.Where(p => p.Kind == BridgePieceKind.Stair && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), bank) < 2.5f).ToList();
            Assert.NotEmpty(stairs);
            var top = stairs.OrderByDescending(st => st.Position.y).First();
            Assert.InRange(top.Position.y + 1f, deckH - 0.05f, deckH + 0.05f);
            Assert.True(top.Position.y <= world.GetHeight(top.Position.x, top.Position.z) + 0.15f, "the stair's foot is in the dirt");
        }

        // Ruin flavour across seeds: toppled debris on the bed, never standing.
        bool anyDebris = false;
        for (int seed = 1; seed <= 20 && !anyDebris; seed++)
            foreach (var d in BridgeLayout.Solve(crossing, world, seed).Where(p => p.Kind == BridgePieceKind.Debris))
            {
                anyDebris = true;
                Assert.True(d.PitchDegrees > 30f, "Debris should be toppled");
            }
        Assert.True(anyDebris, "no debris in 20 seeds");

        Assert.All(plan, p => Assert.InRange(p.HealthFraction, BridgeLayout.RuinHealthMin - 0.001f, BridgeLayout.RuinHealthMax + 0.001f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.5f)]
    [InlineData(2.4f)]
    public void EveryPieceIsGroundedOrConnected(float eastRise)
    {
        var (crossing, world) = WideCrossing(eastRise);
        for (int seed = 1; seed <= 25; seed++)
        {
            var plan = BridgeLayout.Solve(crossing, world, seed);
            var floaters = Floaters(plan, world);
            Assert.True(floaters.Count == 0, $"rise {eastRise} seed {seed}: {floaters.Count} of {plan.Count} pieces float: " +
                string.Join("; ", floaters.Take(4).Select(i => $"{plan[i].Kind} at ({plan[i].Position.x:F1},{plan[i].Position.y:F1},{plan[i].Position.z:F1})")));
        }
    }

    [Fact]
    public void OnlyCrossingsWithTheSameBanksShareABridge()
    {
        var a = RoadCrossing.Between(new Vector2(-40f, 0f), new Vector2(40f, 0f), 26f, new Vector2(0f, 0f), 60f);
        var reversed = RoadCrossing.Between(new Vector2(40f, 1f), new Vector2(-40f, -1f), 26f, new Vector2(0f, 0f), 60f);
        var parallel = RoadCrossing.Between(new Vector2(-40f, 6f), new Vector2(40f, 6f), 26f, new Vector2(0f, 6f), 60f);
        var angled = RoadCrossing.Between(new Vector2(-40f, 0f), new Vector2(30f, 25f), 26f, new Vector2(0f, 12f), 60f);
        Assert.Single(BridgeLayout.DistinctSites(new[] { a, reversed }));
        Assert.Equal(2, BridgeLayout.DistinctSites(new[] { a, parallel }).Count);
        Assert.Equal(2, BridgeLayout.DistinctSites(new[] { a, angled }).Count);
        Assert.Equal(3, BridgeLayout.DistinctSites(new[] { a, parallel, angled, reversed }).Count);
    }

    [Fact]
    public void ALaterRoadJoinsTheFirstBridgeInsteadOfBuildingAParallelOne()
    {
        var world = new WideRiverWorld();
        WorldGenerator.instance = world;
        RoadNetworkGenerator.Reset();
        try
        {
            // Alone, the second road would cross at its own latitude.
            var alone = Pathfinder(world, true).FindPath(new Vector2(-160f, 40f), new Vector2(160f, 40f));
            Assert.NotNull(alone);
            var aloneJump = FindJump(alone!, world)!.Value;

            SetPathfinder(Pathfinder(world, true));
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-160f, 0f), 0f, new Vector2(160f, 0f), 0f, 4f, "first"));
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-160f, 40f), 0f, new Vector2(160f, 40f), 0f, 4f, "second"));

            var crossings = RoadNetworkGenerator.GetRoadCrossings();
            Assert.Equal(2, crossings.Count);
            Assert.True(Mathf.Abs(aloneJump.a.y - crossings[0].FromBank.y) > RoadCrossing.SharedBankRadius,
                $"the lone second road already crossed where the first does (y={aloneJump.a.y:F0})");
            Assert.True(RoadCrossing.SameBanks(crossings[0], crossings[1]),
                $"second road crossed at {crossings[1].FromBank}-{crossings[1].ToBank}, the first at {crossings[0].FromBank}-{crossings[0].ToBank}");
            Assert.Single(BridgeLayout.DistinctSites(crossings));
            Assert.Equal(BridgeLayout.Solve(crossings[0], world, 0).Count, BridgePlans.TotalPlannedPieces);
        }
        finally { TearDownGeneration(); }
    }

    // ---- plans and spawned zones ----

    [Fact]
    public void PlansAreBucketedByZoneAndSpawnedZonesAreRemembered()
    {
        var world = new SyntheticWorld { HasRiver = true, HasMountain = false };
        WorldGenerator.instance = world;
        RoadNetworkGenerator.Reset();
        SetPathfinder(Pathfinder(world, true));
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-300f, 0f), 0f, new Vector2(400f, 0f), 0f, 4f, "Cross river"));
            var crossing = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
            var plan = BridgeLayout.Solve(crossing, world, world.GetSeed());
            Assert.NotEmpty(plan);
            Assert.Equal(plan.Count, BridgePlans.TotalPlannedPieces);
            Assert.True(BridgePlans.PlannedZoneCount >= 1);

            Vector2i zone = ZoneSystem.GetZone(plan[0].Position);
            Assert.True(BridgePlans.PlannedPieceCount(zone) > 0);
            Assert.Equal(plan.Count(p => ZoneSystem.GetZone(p.Position) == zone), BridgePlans.PlannedPieceCount(zone));
            Assert.Null(BridgePlans.PlanFor(new Vector2i(500, 500)));

            Assert.False(BridgePlans.IsSpawned(zone));
            BridgePlans.MarkSpawned(zone);
            Assert.True(BridgePlans.IsSpawned(zone));
            Assert.Contains(zone, BridgePlans.SpawnedZones);

            BridgePlans.ForgetSpawned();
            Assert.False(BridgePlans.IsSpawned(zone));
            Assert.Equal(plan.Count, BridgePlans.TotalPlannedPieces);

            BridgePlans.MarkSpawned(zone);
            RoadNetworkGenerator.Reset();
            Assert.Empty(BridgePlans.SpawnedZones);
            Assert.Equal(0, BridgePlans.TotalPlannedPieces);
        }
        finally { TearDownGeneration(); }
    }

    [Fact]
    public void SpawnedZonesSurviveASaveAndLoadEvenOnALoadedNetwork()
    {
        var world = new SyntheticWorld { HasRiver = true, HasMountain = false };
        WorldGenerator.instance = world;
        ZDOMan.instance = new ZDOMan();
        RoadNetworkGenerator.Reset();
        SetPathfinder(Pathfinder(world, true));
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-300f, 0f), 0f, new Vector2(400f, 0f), 0f, 4f, "Cross river"));
            RoadSpatialGrid.FinalizeRoadNetwork();
            RoadNetworkPersistence.EnsureMetadataInstance();
            var zoneA = new Vector2i(12, 0);
            BridgePlans.MarkSpawned(zoneA);
            typeof(RoadNetworkGenerator).GetField("m_roadsGenerated", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, true);
            RoadNetworkGenerator.SaveGlobalRoadData();

            // Next session: the network loads, another zone spawns its bridge,
            // and only the zone record is saved (the network was not generated).
            RoadNetworkGenerator.Reset();
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
            RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            Assert.True(BridgePlans.IsSpawned(zoneA));
            Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
            var zoneB = new Vector2i(13, 0);
            BridgePlans.MarkSpawned(zoneB);
            RoadNetworkGenerator.SaveBridgeZones();

            RoadNetworkGenerator.Reset();
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
            Assert.True(BridgePlans.IsSpawned(zoneA));
            Assert.True(BridgePlans.IsSpawned(zoneB));
            Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
        }
        finally
        {
            RoadNetworkPersistence.Reset();
            ZDOMan.instance = null;
            TearDownGeneration();
        }
    }

    [Fact]
    public void ARejectedIslandRegenerationKeepsTheBridges()
    {
        var world = new SyntheticWorld { HasRiver = true, HasMountain = false };
        WorldGenerator.instance = world;
        var zones = new ZoneSystem();
        zones.Locations.Add(new ZoneSystem.LocationInstance
        {
            m_location = new ZoneSystem.ZoneLocation { m_prefab = new ZoneSystem.ZoneLocation.PrefabEntry { Name = "StartTemple" }, m_exteriorRadius = 25f },
            m_position = new Vector3(-200f, world.GetHeight(-200f, 0f), 0f),
        });
        ZoneSystem.instance = zones;
        ZDOMan.instance = new ZDOMan();
        RoadNetworkGenerator.Reset();
        SetPathfinder(Pathfinder(world, true));
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-300f, 0f), 0f, new Vector2(400f, 0f), 0f, 4f, "Cross river"));
            var zone = new Vector2i(12, 0);
            BridgePlans.MarkSpawned(zone);

            // Nothing to regenerate here: an ocean point, then an island with no locations.
            Assert.False(RoadNetworkGenerator.RegenerateIslandAt(new Vector3(3000f, 0f, 3000f), out string ocean));
            Assert.Contains("No island", ocean);
            Assert.False(RoadNetworkGenerator.RegenerateIslandAt(Vector3.zero, out string empty));
            Assert.Contains("no road-eligible", empty);

            Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
            Assert.True(BridgePlans.IsSpawned(zone));
            Assert.True(BridgePlans.TotalPlannedPieces > 0);
        }
        finally
        {
            ZDOMan.instance = null;
            ZoneSystem.instance = null;
            TearDownGeneration();
        }
    }

    [Fact]
    public void RendersTheBridge()
    {
        var (crossing, world) = WideCrossing();
        var path = Pathfinder(world, true).FindPath(new Vector2(-160f, 0f), new Vector2(160f, 0f))!;
        var plan = BridgeLayout.Solve(crossing, world, 42);
        var paths = new List<(List<Vector2>, byte, byte, byte)> { (path, 220, 40, 40) };
        var markers = new List<(Vector2, byte, byte, byte)>();
        foreach (var piece in plan)
        {
            (byte r, byte g, byte b) c = piece.Kind switch
            {
                BridgePieceKind.Post => ((byte)230, (byte)180, (byte)60),
                BridgePieceKind.Deck => ((byte)250, (byte)240, (byte)120),
                _ => ((byte)200, (byte)80, (byte)80),
            };
            markers.Add((new Vector2(piece.Position.x, piece.Position.z), c.r, c.g, c.b));
        }
        string output = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(BridgeTests).Assembly.Location)!, "debug-bridge.bmp");
        WorldRenderer.Render(world, paths, markers, output, -120f, 120f, 0.5f);
        Assert.True(System.IO.File.Exists(output));
    }

    // ---- persistence ----

    [Fact]
    public void CrossingsSurviveASaveAndLoad()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        ZDOMan.instance = new ZDOMan();
        RoadSpatialGrid.Clear();
        try
        {
            RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-40f, 0f), new(40f, 0f) }, 4f, world);
            RoadSpatialGrid.FinalizeRoadNetwork();
            RoadNetworkPersistence.EnsureMetadataInstance();

            var saved = new List<RoadCrossing>
            {
                RoadCrossing.Between(new Vector2(-42.5f, 0f), new Vector2(42.5f, 0f), 26f, new Vector2(0.5f, 0f), 79f),
                RoadCrossing.Between(new Vector2(100f, 50f), new Vector2(100f, 62f), 28.5f, new Vector2(100f, 56f), 0f),
            };
            RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2 position, string label)>(), saved, new HashSet<Vector2i> { new(3, 4) });

            RoadSpatialGrid.Clear();
            var loaded = new List<RoadCrossing> { saved[0] }; // stale content is replaced
            var zones = new HashSet<Vector2i>();
            Assert.True(RoadNetworkPersistence.TryLoadGlobalRoadData(new List<(Vector2 position, string label)>(), loaded, zones));
            Assert.Equal(2, loaded.Count);
            Assert.Equal(new HashSet<Vector2i> { new(3, 4) }, zones);
            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(saved[i].FromBank, loaded[i].FromBank);
                Assert.Equal(saved[i].ToBank, loaded[i].ToBank);
                Assert.Equal(saved[i].RiverbedHeight, loaded[i].RiverbedHeight);
                Assert.Equal(saved[i].FairwayCenter, loaded[i].FairwayCenter);
                Assert.Equal(saved[i].FairwayWidth, loaded[i].FairwayWidth);
                Assert.Equal(saved[i].Width, loaded[i].Width);
                Assert.Equal(saved[i].Center, loaded[i].Center);
                Assert.Equal(saved[i].Direction, loaded[i].Direction);
            }

            // A network saved without crossings loads back with none.
            RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-40f, 0f), new(40f, 0f) }, 4f, world);
            RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2 position, string label)>(), new List<RoadCrossing>(), new HashSet<Vector2i>());
            Assert.True(RoadNetworkPersistence.TryLoadGlobalRoadData(new List<(Vector2 position, string label)>(), loaded, zones));
            Assert.Empty(loaded);
            Assert.Empty(zones);
        }
        finally
        {
            RoadNetworkPersistence.Reset();
            RoadSpatialGrid.Clear();
            ZDOMan.instance = null;
            WorldGenerator.instance = null;
        }
    }

    [Fact]
    public void AWholeNetworkWithABridgeSavesAndReloadsItsCrossings()
    {
        // Spawn temple and boss altar on opposite sides of the synthetic
        // river: the only road between them crosses it.
        var world = new SyntheticWorld { HasRiver = true, HasMountain = false };
        WorldGenerator.instance = world;
        var zones = new ZoneSystem();
        foreach ((string name, float x, float z, float radius) in new[] { ("StartTemple", -200f, 0f, 25f), ("Eikthyrnir", 300f, 0f, 10f) })
        {
            zones.Locations.Add(new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new ZoneSystem.ZoneLocation.PrefabEntry { Name = name }, m_exteriorRadius = radius },
                m_position = new Vector3(x, world.GetHeight(x, z), z),
            });
        }
        ZoneSystem.instance = zones;
        ZDOMan.instance = new ZDOMan();
        RoadNetworkGenerator.Reset();
        RoadPathfinder.BridgesEnabled = true;
        try
        {
            RoadNetworkGenerator.GenerateRoads(force: true);
            Assert.True(RoadSpatialGrid.TotalRoadPoints > 0, "no roads generated");
            var crossings = RoadNetworkGenerator.GetRoadCrossings();
            Assert.NotEmpty(crossings);
            Vector2 bank = crossings[0].FromBank;

            RoadNetworkGenerator.SaveGlobalRoadData();
            RoadNetworkGenerator.Reset();
            Assert.Empty(RoadNetworkGenerator.GetRoadCrossings());
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), "saved network did not load back");
            Assert.Equal(crossings.Count, RoadNetworkGenerator.GetRoadCrossings().Count);
            Assert.Equal(bank, RoadNetworkGenerator.GetRoadCrossings()[0].FromBank);
        }
        finally
        {
            RoadPathfinder.BridgesEnabled = false;
            RoadNetworkGenerator.Reset();
            ZDOMan.instance = null;
            ZoneSystem.instance = null;
            WorldGenerator.instance = null;
        }
    }

    // ---- fords ----

    /// <summary>A knee-deep gully: bed at Bed for |x| &lt; HalfWidth, banks at 33.
    /// River core under it, so it is a crossing, not a puddle.</summary>
    private sealed class GullyWorld : WorldGenerator
    {
        public float Bed = 29.5f;
        public float HalfWidth = 12f;
        public Heightmap.Biome Land = Heightmap.Biome.Meadows;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 100f || Mathf.Abs(wy) > 100f) return 20f;
            return Mathf.Abs(wx) < HalfWidth ? Bed : 33f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Land;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Clamp01(1f - Mathf.Abs(wx) / (HalfWidth * 2f));
            width = weight > 0f ? HalfWidth * 4f : 0f;
        }
    }

    [Fact]
    public void ASpannedFordIsALowFootbridgeWithSteps()
    {
        // With bridges on a ford may be spanned: the water stays unpaved and
        // a footbridge on posts carries the road, a step at each end.
        var world = new GullyWorld();
        WorldGenerator.instance = world;
        RoadNetworkGenerator.Reset();
        SetPathfinder(Pathfinder(world, true));
        RoadCrossingDetector.SetFordStyleWeights(0f, 0f, 1f);
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "ford"));
            var crossing = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
            Assert.Equal(CrossingKind.Ford, crossing.Kind);
            Assert.Equal(FordStyle.Span, crossing.Style);
            Assert.Empty(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(crossing.Center.x, 0f, crossing.Center.y), 3f));

            var plan = BridgeLayout.Solve(crossing, world, 1);
            Assert.Contains(plan, p => p.Kind == BridgePieceKind.Deck);
            Assert.All(plan.Where(p => p.Kind == BridgePieceKind.Deck), d => Assert.True(d.Position.y >= 33f + RoadConstants.FordSpanDeckRise - 0.01f));
            Assert.Equal(2, plan.Count(p => p.Kind == BridgePieceKind.Stair && Mathf.Abs(p.Position.y + 1f - 34f) < 0.05f));
            Assert.Empty(Floaters(plan, world));
            Assert.Equal(plan.Count, BridgePlans.TotalPlannedPieces);

            // Without bridges the same site is an ordinary ford, no span offered.
            var fordsOnly = RoadCrossingDetector.Detect(Pathfinder(world, true).FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f))!, world, false);
            Assert.Equal(CrossingKind.Ford, Assert.Single(fordsOnly).Kind);
            Assert.NotEqual(FordStyle.Span, fordsOnly[0].Style);
        }
        finally { TearDownGeneration(); }
    }

    // ---- a crossing the pathfinder commits to ----

    private sealed class BiomeRiverWorld : WorldGenerator
    {
        public float HalfWidth = 40f;           // > 24 m: needs a BRIDGE jump
        public float Bed = 26f;                 // deeper than wading: a bridge crossing
        public Heightmap.Biome EastBiome = Heightmap.Biome.Mistlands;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 220f || Mathf.Abs(wy) > 120f) return 20f;
            float ax = Mathf.Abs(wx);
            if (ax <= HalfWidth) return Bed;
            if (ax >= HalfWidth + 10f) return 33f;
            return Mathf.Lerp(Bed, 33f, (ax - HalfWidth) / 10f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean
            : wx > 0f ? EastBiome : Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Clamp01(1f - Mathf.Abs(wx) / (HalfWidth * 2f));
            width = weight > 0f ? HalfWidth * 4f : 0f;
        }
    }

    [Fact]
    public void ACrossingThePathfinderCommitsToAlwaysGetsADeck()
    {
        // The invariant that keeps a road walkable: if the pathfinder accepts a
        // jump and prices it as a bridge, the layout must return a real deck for
        // it. An empty plan here is a road that leads into deep water with
        // nothing built over it, and nothing downstream would notice -- the
        // route is already committed by the time the pieces are solved. The two
        // sides must agree about every crossing, whatever the banks are made of.
        foreach (var world in new[]
                 {
                     new BiomeRiverWorld(),                                        // steep, jagged far bank
                     new BiomeRiverWorld { EastBiome = Heightmap.Biome.Meadows },   // open far bank
                     new BiomeRiverWorld { EastBiome = Heightmap.Biome.Swamp },     // low, wet far bank
                 })
        {
            var path = Pathfinder(world, true).FindPath(new Vector2(-160f, 0f), new Vector2(160f, 0f));
            Assert.NotNull(path);
            var crossing = Assert.Single(RoadCrossingDetector.Detect(path!, world, true));
            Assert.Equal(CrossingKind.Bridge, crossing.Kind);
            Assert.NotEmpty(BridgeLayout.Solve(crossing, world, 1));
        }

        // A shallow jump is a ford, and a ford builds nothing by design.
        var ford = new BiomeRiverWorld { HalfWidth = 12f, Bed = RoadConstants.SeaLevel - RoadConstants.FordWadeDepth + 0.1f };
        var fordPath = Pathfinder(ford, true).FindPath(new Vector2(-160f, 0f), new Vector2(160f, 0f));
        Assert.NotNull(fordPath);
    }

    // ---- high bridge ----

    /// <summary>A gorge: water |x| &lt; 20, a wet shelf to |x| = 24, then a
    /// 4.5 m cliff up to a plateau at 36 on both sides.</summary>
    private sealed class GorgeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 220f || Mathf.Abs(wy) > 120f) return 20f;
            float ax = Mathf.Abs(wx);
            if (ax < 20f) return 26f;
            if (ax < 24f) return 31.5f;
            if (ax < 28f) return Mathf.Lerp(31.5f, 36f, (ax - 24f) / 4f);
            return 36f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Clamp01(1f - Mathf.Abs(wx) / 48f);
            width = weight > 0f ? 96f : 0f;
        }
    }

    [Fact]
    public void ADeckSpringsFromTheBankTopsWhereTheRoadClimbsACliffOnBothSides()
    {
        var world = new GorgeWorld();
        var path = Pathfinder(world, true).FindPath(new Vector2(-160f, 0f), new Vector2(160f, 0f));
        Assert.NotNull(path);
        var crossing = Assert.Single(RoadCrossingDetector.Detect(path!, world, true));
        Assert.Equal(CrossingKind.Bridge, crossing.Kind);
        foreach (Vector2 bank in new[] { crossing.FromBank, crossing.ToBank })
            Assert.True(world.GetHeight(bank.x, bank.y) >= 35.9f, $"bank {bank} is at {world.GetHeight(bank.x, bank.y):F1}, not on the top");
        Assert.InRange(crossing.Width, 54f, 62f);

        var plan = BridgeLayout.Solve(crossing, world, 3);
        (float fromH, float toH) = BridgeLayout.DeckEndHeights(crossing, world);
        Assert.InRange(fromH, 35.9f, 36.1f);
        Assert.InRange(toH, 35.9f, 36.1f);
        Assert.All(plan.Where(p => p.Kind == BridgePieceKind.Deck), d => Assert.InRange(d.Position.y, 35.9f, 36.1f));
        Assert.Empty(Floaters(plan, world));
        // Piers over the water reach from the deck down to the 26 m bed: at least four segments.
        Assert.True(plan.Count(p => p.Kind == BridgePieceKind.Post && Mathf.Abs(p.Position.x) < 20f) >= 8, "no tall piers over the water");
    }

    // ---- support model (test-side) ----

    private static (float bottom, float top, float reach) Extent(BridgePiece p) => p.Kind switch
    {
        BridgePieceKind.Post => (-BridgeLayout.PostSegment * 0.5f, BridgeLayout.PostSegment * 0.5f, 0.25f),
        BridgePieceKind.Beam => (-0.15f, 0.15f, 1f),
        BridgePieceKind.Deck => (-0.1f, 0f, 1f),
        BridgePieceKind.Stair => (0f, 1f, 1f),      // step: 2 m run, 1 m rise, origin at the foot
        _ => (-0.5f, 0.5f, 1f),
    };

    /// <summary>
    /// Pieces that neither rest on the ground nor connect, through touching
    /// pieces, to one that does. Stricter than the game (it knows nothing of
    /// wood's horizontal reach and demands contact), so a plan that passes
    /// here stands in the game and Valheim's support system has nothing to
    /// knock down when the zone loads.
    /// </summary>
    private static List<int> Floaters(List<BridgePiece> plan, WorldGenerator world)
    {
        const float groundTolerance = 0.15f, contactTolerance = 0.3f;
        int n = plan.Count;
        var ext = plan.Select(Extent).ToArray();
        var supported = new bool[n];
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            Vector3 pos = plan[i].Position;
            if (pos.y + ext[i].bottom <= BiomeBlendedHeight.GetBlendedHeight(pos.x, pos.z, world) + groundTolerance)
            {
                supported[i] = true;
                queue.Enqueue(i);
            }
        }
        while (queue.Count > 0)
        {
            int a = queue.Dequeue();
            for (int b = 0; b < n; b++)
            {
                if (supported[b]) continue;
                float dx = plan[a].Position.x - plan[b].Position.x, dz = plan[a].Position.z - plan[b].Position.z;
                float reach = ext[a].reach + ext[b].reach;
                if (dx * dx + dz * dz > reach * reach) continue;
                float aBottom = plan[a].Position.y + ext[a].bottom, aTop = plan[a].Position.y + ext[a].top;
                float bBottom = plan[b].Position.y + ext[b].bottom, bTop = plan[b].Position.y + ext[b].top;
                if (aBottom <= bTop + contactTolerance && aTop >= bBottom - contactTolerance)
                {
                    supported[b] = true;
                    queue.Enqueue(b);
                }
            }
        }
        return Enumerable.Range(0, n).Where(i => !supported[i]).ToList();
    }
}
