using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using static ProceduralRoads.Tests.StructureEndTests;

namespace ProceduralRoads.Tests;

/// <summary>
/// Every kit is laid on the finished ground (<see cref="StructureGround"/>): the ground the terrain writer leaves once the
/// road is levelled, not the generator's. A piece wholly under it is not laid (the game breaks it, dropping its
/// resources); a column reaches it, shorter where the road raised the ground, longer where it cut it.
/// </summary>
public sealed class StructureGroundTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public StructureGroundTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); _scope.Dispose(); }

    /// <summary>Road points levelled at <paramref name="height"/> every metre over a box (x0..x1, z0..z1), stored as the
    /// network.</summary>
    private static void Level(float x0, float x1, float z0, float z1, float height)
    {
        for (float z = z0; z <= z1 + 1e-3f; z += 1f)
        {
            var points = new List<Vector2>();
            var heights = new List<float>();
            for (float x = x0; x <= x1 + 1e-3f; x += 1f)
            {
                points.Add(new Vector2(x, z));
                heights.Add(height);
            }
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, heights, new List<RoadPointDebugInfo>(), 4f, x1 - x0, false));
        }
        RoadSpatialGrid.FinalizeRoadNetwork();
    }

    // ------------------------------------------------------------- the stone kit's footing

    /// <summary>A stone bridge 20 m along x over a bed at 28 m, its banks at 33.7 m: one step down at each end from the
    /// deck at 34.83 m, on a footing of stone pillars (their tops at the step's foot, 33.83 m).</summary>
    private sealed class StoneWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            float a = Mathf.Abs(wx);
            if (a <= 7f) return 28f;
            if (a < 8f) return Mathf.Lerp(28f, 31f, a - 7f);
            if (a <= 10f) return 31f;
            return Mathf.Lerp(31f, 33.7f, (a - 10f) * 2f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.BlackForest;
        public override int GetSeed() => 76;
    }

    private RoadCrossing StoneBridge()
    {
        _scope.WithWorld(new StoneWorld());
        var c = RoadCrossing.Between(new Vector2(-10f, 0f), new Vector2(10f, 0f), 28f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = 34.83f - BridgeLayout.DeckFreeboard;
        return c;
    }

    /// <summary>
    /// A stone ford span's north end on a test world: the road's floor levelled at 30.50 m round the end's one step, whose
    /// footing pillars stood 1.2 m lower on the generator's ground, wholly in the finished ground; three of them broke and
    /// left a Stone. Here the road's landing at the far end is raised over the footing's tops: no pillar is laid there, the
    /// step still is (its foot in the landing, so grounded) with its railing beside it, and the near end keeps its footing.
    /// </summary>
    [Fact]
    public void AStairFootingUnderARaisedLandingIsLeftOut()
    {
        RoadCrossing c = StoneBridge();
        WorldGenerator w = WorldGenerator.instance!;
        var before = BridgeLayout.SolveStoneComplete(c, w, 1);
        var (_, far) = BridgeStructures.Ends(c, w);
        Assert.Equal(1, far.Steps);
        static bool FarFooting(StructurePiece p) => p.Prefab == BridgeLayout.StonePillarPrefab && p.Position.x > 10f;
        static bool NearFooting(StructurePiece p) => p.Prefab == BridgeLayout.StonePillarPrefab && p.Position.x < -10f;
        Assert.Equal(8, before.Count(FarFooting));

        Level(10.5f, 20f, -4f, 4f, 34.1f);
        Func<Vector2, float> ground = StructureGround.At(w);
        Assert.True(ground(new Vector2(11f, 1f)) > 34f, $"the landing stands at {ground(new Vector2(11f, 1f)):F2}");
        var after = BridgeLayout.SolveStoneComplete(c, w, 1);
        Assert.DoesNotContain(after, FarFooting);
        Assert.Equal(8, after.Count(NearFooting));
        var steps = after.Where(p => p.Prefab == BridgeLayout.StoneStairPrefab && p.Position.x > 10f).ToList();
        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.True(s.Position.y <= ground(new Vector2(s.Position.x, s.Position.z)), "the step's foot is in the landing"));
        Assert.Equal(2, after.Count(p => p.Prefab == BridgeLayout.StonePierBlock && p.Position.x > 10.5f));
        AssertNothingWhollyBuried(after, w);
    }

    // ------------------------------------------------------------- the viaduct's end blocks

    /// <summary>A Mistlands valley (<see cref="RimValley"/>), its far flat 1 m under the deck, with a hollow
    /// <see cref="HollowDepth"/> deep between <see cref="HollowFrom"/> and <see cref="HollowTo"/> along x.</summary>
    private sealed class HollowValley : WorldGenerator
    {
        public readonly RimValley Valley = new() { Far = _ => -1f };
        public float HollowFrom = float.NaN, HollowTo = float.NaN, HollowDepth = 5f;
        public override float GetHeight(float wx, float wy) =>
            wx >= HollowFrom && wx <= HollowTo && Mathf.Abs(wy) < 8f ? Valley.GetHeight(wx, wy) - HollowDepth : Valley.GetHeight(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 78;
    }

    /// <summary>
    /// A viaduct's NW end on a test world: its step's marble blocks stood 4-6 m under the end's landing (the road levelled
    /// there at 48.50 m over a hollow in the generator's ground), and one broke into a black marble drop. Here the far end's
    /// one step stands over a hollow, carried on blocks down into it; once the road's landing fills the hollow to the
    /// step's foot, no block is laid under it, and nothing of the viaduct lies wholly in the ground.
    /// </summary>
    [Fact]
    public void AViaductEndWhoseLandingIsRaisedOverItsBlocksDropsThem()
    {
        var w = new HollowValley();
        _scope.WithWorld(w);
        float bank = w.Valley.Rim + 1f;
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-bank, 0f), new Vector2(bank, 0f), w, out string why)!;
        Assert.True(c != null, why);
        MetEnds.Set(c!, w, toRow: 1);
        var (_, far) = DvergrViaduct.Ends(c!, w);
        Assert.Equal(1, far.Steps);
        // The hollow under the step (from the deck's edge outward 2 m), its foot 1 m under the deck.
        w.HollowFrom = Mathf.Min(far.Edge.x, far.Meet.x);
        w.HollowTo = Mathf.Max(far.Edge.x, far.Meet.x);
        bool StepBlock(StructurePiece p) => p.Kind == StructurePieceKind.Post && p.Prefab == DvergrViaduct.Block
            && p.Position.x >= w.HollowFrom - 0.5f && p.Position.x <= w.HollowTo + 0.5f;
        var before = DvergrViaduct.SolveComplete(c!, w, 1);
        Assert.True(before.Count(StepBlock) >= 4, $"{before.Count(StepBlock)} block(s) under the step over the hollow");

        Level(w.HollowFrom - 1f, w.HollowTo + 8f, -5f, 5f, far.MeetHeight);
        var after = DvergrViaduct.SolveComplete(c!, w, 1);
        Assert.DoesNotContain(after, StepBlock);
        Assert.Equal(2, after.Count(p => p.Prefab == DvergrViaduct.StepPrefab && p.Position.x > w.HollowFrom - 0.5f));
        AssertNothingWhollyBuried(after, w, c);
    }

    // ------------------------------------------------------------- a cut under a column

    /// <summary>A railed bridge 12 m along x over a flat bed at 28.9 m, banks at the deck's height (34.9 m) past its
    /// ends.</summary>
    private sealed class FlatBedWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) < 6f ? 28.9f : 34.9f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 76;
    }

    /// <summary>
    /// The road's earthwork cuts the bed under a column 1.5 m (a staircase's cut beside it): the column is carried on down
    /// to the cut, its foot in the new ground, where on the generator's ground it stopped 1.5 m over it.
    /// </summary>
    [Fact]
    public void ACutLeavesTheColumnReachingTheNewGround()
    {
        var w = new FlatBedWorld();
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-6f, 0f), new Vector2(6f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = 34.9f - BridgeLayout.DeckFreeboard;
        float Foot(List<StructurePiece> plan) => plan.Where(p => p.Prefab == BridgeLayout.PostPrefab && Mathf.Abs(p.Position.x) < 0.01f)
            .Min(p => p.Position.y) - BridgeLayout.PostSegment * 0.5f;
        var before = BridgeLayout.SolveRailedComplete(c, w, 1, BridgeLayout.Rail);
        Assert.InRange(Foot(before), 28.9f - BridgeLayout.PostSegment, 28.9f + BuriedPieces.Growth);

        var cuts = new List<Vector3>();
        for (float x = -3f; x <= 3f; x += 1f)
            for (float z = -4f; z <= 4f; z += 1f) cuts.Add(new Vector3(x, 27.4f, z));
        Level(100f, 110f, 0f, 0f, 34.9f);   // a road elsewhere: the network stands
        RoadSpatialGrid.CommitCuts(cuts, 2f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.Equal(27.4f, StructureGround.At(w)(new Vector2(0f, BridgeLayout.DeckHalfWidth)), 2);
        var after = BridgeLayout.SolveRailedComplete(c, w, 1, BridgeLayout.Rail);
        Assert.InRange(Foot(after), 27.4f - BridgeLayout.PostSegment, 27.4f + BuriedPieces.Growth);
        Assert.True(Foot(after) < Foot(before) - 1f, $"the column's foot {Foot(after):F2}, on the generator's ground {Foot(before):F2}");
        AssertNothingWhollyBuried(after, w);
    }

    // ------------------------------------------------------------- a fallen column's stub

    /// <summary>A river 60 m wide along x, the water at 30 m: its banks at 32 m, the bed under the bridge at 31.6 m for
    /// 12 m in from each bank (dry, over the water), then falling to 26 m mid-river.</summary>
    private sealed class ShelvedRiverWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            float a = Mathf.Abs(wx);
            if (a >= 30f) return 32f;
            if (a >= 18f) return 31.6f;
            return Mathf.Lerp(26f, 31.6f, (a - 12f) / 6f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 77;
    }

    /// <summary>
    /// A railed bridge's ruin: a fallen column leaves a stub broken just over the water. Where it fell on a shelf of the
    /// bed standing over that break (a wood_pole2 laid at 29.3 m, its top 30.3 m, under ground at 31.7 m, broke and left
    /// its Wood), no stub is laid; in the river a stub stands on the bed. Over many worlds, nothing of the ruin lies
    /// wholly under the ground, and both cases are met.
    /// </summary>
    [Fact]
    public void AFallenColumnsStubIsNeverLaidUnderTheGround()
    {
        var w = new ShelvedRiverWorld();
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-30f, 0f), new Vector2(30f, 0f), 26f, new Vector2(0f, 0f), 60f, CrossingKind.Bridge);
        c.WaterLevel = 30f;
        float deck = BridgeLayout.WoodDeckHeight(c, w), brk = c.WaterLevel + 0.3f;
        static (float, float) Key(StructurePiece p) => (Mathf.Round(p.Position.x * 10f), Mathf.Round(p.Position.z * 10f));
        int dryFallen = 0, stubs = 0;
        foreach (var kit in new[] { BridgeLayout.Rail, BridgeLayout.Braced })
        {
            var columns = BridgeLayout.SolveRailedComplete(c, w, 1, kit).Where(p => p.Prefab == BridgeLayout.PostPrefab).Select(Key).ToHashSet();
            for (int seed = 1; seed <= 40; seed++)
            {
                var ruin = BridgeLayout.SolveRailed(c, w, seed, kit);
                AssertNothingWhollyBuried(ruin.Where(p => p.Kind != StructurePieceKind.Debris).ToList(), w);
                var posts = ruin.Where(p => p.Prefab == BridgeLayout.PostPrefab).ToLookup(Key);
                foreach (var at in columns)
                {
                    if (posts[at].Any(p => p.Position.y + BridgeLayout.PostSegment * 0.5f > deck - 0.01f)) continue;   // standing
                    var stub = posts[at].ToList();
                    float ground = w.GetHeight(at.Item1 / 10f, at.Item2 / 10f);
                    if (ground >= brk) { dryFallen++; Assert.Empty(stub); }
                    else if (stub.Count > 0)
                    {
                        stubs++;
                        Assert.All(stub, p => Assert.True(p.Position.y + BridgeLayout.PostSegment * 0.5f <= brk + 1e-3f, $"a stub at {p.Position} stands over the break"));
                        Assert.InRange(stub.Min(p => p.Position.y) - BridgeLayout.PostSegment * 0.5f, ground - BridgeLayout.PostSegment, ground + BuriedPieces.Growth);
                    }
                }
                // Debris lies on the ground, not under it.
                Assert.All(ruin.Where(p => p.Kind == StructurePieceKind.Debris),
                    p => Assert.Equal(w.GetHeight(p.Position.x, p.Position.z) + 0.2f, p.Position.y, 2));
            }
        }
        Assert.True(dryFallen > 0, "no column fell on the dry shelf: the case does not test the rule");
        Assert.True(stubs > 0, "no stub stands in the river");
    }

    // ------------------------------------------------------------- every kit, the road laid

    /// <summary>A bed 16 m either side of x = 0 at 28 m, the banks rolling between 31 and 34 m: a road brought to a
    /// structure's end levels its apron and the joining stretch, raising the ground here and cutting it there.</summary>
    private sealed class RollingBanks : WorldGenerator
    {
        public Heightmap.Biome Biome = Heightmap.Biome.BlackForest;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float a = Mathf.Abs(wx);
            float bank = 32.5f + 1.5f * Mathf.Sin(wx * 0.9f) * Mathf.Cos(wy * 0.6f) - Mathf.Min(2f, (a - 16f) * 0.15f);
            return a < 16f ? 28f : bank;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => 79;
    }

    /// <summary>The road across <paramref name="c"/> from (-80, 0) to (80, 0), planned as the generator plans it (its ends
    /// met, the apron levelled) and laid as the network.</summary>
    private static void LayRoadAcross(RoadCrossing c, WorldGenerator w, Vector2 from, Vector2 to)
    {
        var path = new List<Vector2> { from, c.FromBank, c.ToBank, to };
        c.FromIndex = 1; c.ToIndex = 2;
        var plans = MetEnds.Plan(path, new List<RoadCrossing> { c }, w);
        Assert.True(plans != null, RoadSpatialGrid.LastRefusal);
        foreach (var plan in plans!) RoadSpatialGrid.Commit(plan);
        RoadSpatialGrid.FinalizeRoadNetwork();
    }

    /// <summary>
    /// Bridges and ford spans in every kit, the road brought to their ends and laid (its apron and joining stretch levelled
    /// over rolling banks, so the finished ground stands over the generator's in places and under it in others): no piece
    /// of any of them lies wholly under the finished ground. Laid on the generator's ground (solved before the road is),
    /// some would.
    /// </summary>
    [Fact]
    public void NoPieceOfAnyKitIsWhollyUnderTheFinishedGround()
    {
        int laid = 0, checkedPieces = 0, buriedOnTheGenerators = 0;
        foreach (bool span in new[] { false, true })
        {
            RoadNetworkGenerator.Reset();
            RoadSpatialGrid.Clear();
            var w = new RollingBanks();
            _scope.WithWorld(w);
            var c = span
                ? RoadCrossing.Between(new Vector2(-16f, 0f), new Vector2(16f, 0f), 28f, new Vector2(0f, 0f), 0f, CrossingKind.Ford, FordStyle.Span)
                : RoadCrossing.Between(new Vector2(-16f, 0f), new Vector2(16f, 0f), 28f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
            c.WaterLevel = 30f;
            List<(string kit, List<StructurePiece> plan)> Kits() => new()
            {
                ("rail", BridgeLayout.SolveRailedComplete(c, w, 1, BridgeLayout.Rail)),
                ("braced", BridgeLayout.SolveRailedComplete(c, w, 1, BridgeLayout.Braced)),
                ("stone", BridgeLayout.SolveStoneComplete(c, w, 1)),
                ("dvergr wood", DvergrWoodBridge.SolveComplete(c, w, 1)),
            };
            LayRoadAcross(c, w, new Vector2(-80f, 0f), new Vector2(80f, 0f));
            // The same ends, laid on the generator's ground: the network set aside, then put back.
            byte[] network = RoadSpatialGrid.SerializeAllRoadPoints()!;
            RoadSpatialGrid.Clear();
            var onTheGenerators = Kits();
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(network));
            RoadSpatialGrid.FinalizeRoadNetwork();
            foreach (var (_, plan) in onTheGenerators) buriedOnTheGenerators += WhollyBuried(plan, w).buried.Count;
            foreach (var (kit, plan) in Kits())
            {
                string what = $"{kit}{(span ? " span" : "")}";
                Assert.True(plan.Count > 0, $"{what}: no pieces");
                laid += plan.Count;
                var (judged, buried) = WhollyBuried(plan, w);
                Assert.True(buried.Count == 0, $"{what}: {string.Join("; ", buried)}");
                checkedPieces += judged;
            }
        }

        // A viaduct, the road down the hillside to both ends from the side, the far end with its stairs.
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        var valley = new RimValley { Far = x => -1f - 0.6f * Mathf.Sin(x) };
        _scope.WithWorld(valley);
        float bank = valley.Rim + 1f;
        RoadCrossing v = DvergrViaduct.ValleyCrossing(new Vector2(-bank, 0f), new Vector2(bank, 0f), valley, out string why)!;
        Assert.True(v != null, why);
        var path = new List<Vector2> { new(-100f, 40f), new(-60f, 40f), new(-bank - 6f, 14f), new(-bank, 0f), new(bank, 0f), new(bank + 6f, 14f), new(60f, 40f), new(100f, 40f) };
        v!.FromIndex = 3; v.ToIndex = 4;
        var plans = MetEnds.Plan(path, new List<RoadCrossing> { v }, valley);
        Assert.True(plans != null, RoadSpatialGrid.LastRefusal);
        var viaductOnTheGenerators = DvergrViaduct.SolveComplete(v, valley, 1);
        foreach (var plan in plans!) RoadSpatialGrid.Commit(plan);
        RoadSpatialGrid.FinalizeRoadNetwork();
        buriedOnTheGenerators += WhollyBuried(viaductOnTheGenerators, valley, v).buried.Count;
        var viaduct = DvergrViaduct.SolveComplete(v, valley, 1);
        laid += viaduct.Count;
        var (viaductJudged, viaductBuried) = WhollyBuried(viaduct, valley, v);
        Assert.True(viaductBuried.Count == 0, $"viaduct: {string.Join("; ", viaductBuried)}");
        checkedPieces += viaductJudged;
        Assert.True(checkedPieces > laid / 3, $"only {checkedPieces} of {laid} pieces could be judged");
        Assert.True(buriedOnTheGenerators > 0, "laid on the generator's ground, no piece would have been buried: the case does not test the rule");
    }

    /// <summary>No piece of <paramref name="plan"/> wholly under the finished ground (<see cref="WhollyBuried"/>).</summary>
    private static void AssertNothingWhollyBuried(List<StructurePiece> plan, WorldGenerator w, RoadCrossing? viaduct = null)
    {
        var (_, buried) = WhollyBuried(plan, w, viaduct);
        Assert.True(buried.Count == 0, string.Join("; ", buried));
    }

    /// <summary>The pieces of <paramref name="plan"/> wholly under the finished ground: a stair kit's piece by its bounds
    /// (<see cref="BuriedPieces.Buried"/>), a viaduct's by its extents (<see cref="DvergrViaduct.WhollyBuried(string, Vector2, Vector2, Vector2, float, Func{Vector2, float})"/>);
    /// and how many pieces were judged (the others' bounds are not known here).</summary>
    private static (int judged, List<string> buried) WhollyBuried(List<StructurePiece> plan, WorldGenerator w, RoadCrossing? viaduct = null)
    {
        Func<Vector2, float> ground = StructureGround.At(w);
        Func<int, int, float> vertex = (x, z) => ground(new Vector2(x, z));
        int judged = 0;
        var buried = new List<string>();
        foreach (StructurePiece p in plan)
        {
            if (p.Kind is StructurePieceKind.Flair or StructurePieceKind.Debris) continue;
            bool under;
            if (BuriedPieces.Known(p.Prefab))
                under = BuriedPieces.Buried(p.Prefab, p.Position, p.YawDegrees, vertex, p.PitchDegrees);
            else if (viaduct != null)
            {
                Vector2 dir = viaduct.Direction, side = new(-dir.y, dir.x);
                under = DvergrViaduct.WhollyBuried(p.Prefab, new Vector2(p.Position.x, p.Position.z), dir, side, p.Position.y, ground);
            }
            else continue;
            judged++;
            if (under) buried.Add($"{p.Prefab} at {p.Position} lies wholly under the finished ground");
        }
        return (judged, buried);
    }
}
