using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;
using static ProceduralRoads.Tests.DvergrViaductTests;

namespace ProceduralRoads.Tests;

/// <summary>
/// A manual road is one road of the generator's: the same search, planning pipeline and refusals (RoadNetworkGenerator.PlanFromPath,
/// BuiltRoadRefusal), only its route's ends are the operator's marks. Over a river it gets the generated road's bridge, up a
/// steep face its staircase, across a deep Mistlands valley its viaduct, and where the generated road is refused it is refused
/// too.
/// </summary>
[Collection("RoadStatics")]
public sealed class ManualRoadPipelineTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>A face of 1.2 from x = 24 to 44 (24 m up) in the mountains: too steep for road.</summary>
    private sealed class Scarp : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : 40f + Mathf.Clamp((wx - 24f) * 1.2f, 0f, 24f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>Meadows at 33 m with a river 40 m wide and 11 m deep across x = 0.</summary>
    private sealed class River : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : Mathf.Abs(wx) < 20f ? 22f : 33f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Abs(wx) < 40f ? Mathf.Clamp01(1f - Mathf.Abs(wx) / 40f) : 0f;
            width = weight > 0f ? 80f : 0f;
        }
        public override int GetSeed() => 78;
    }

    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : 40f;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    /// <summary>Meadows at 36 m with a dry-banked river 24 m wide and 16 m deep across x = 0.</summary>
    private sealed class Gorge : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : Mathf.Abs(wx) < 12f ? 20f : 36f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 79;
    }

    /// <summary>
    /// A manual road's crossings change the network as a generated road's do (RoadNetworkGenerator.GroundOf), in its one
    /// append: another road on a bridge end's landing is raised to it (to the higher, where both ends raise it), and the ground under each deck end with stairs is
    /// carried in under the deck, unpainted. The raised road is the append's, so terrain written before it is written again.
    /// </summary>
    [Fact]
    public void AManualRoadsBridgeRaisesTheRoadOnItsLandingAndGroundsItsDeckEnds()
    {
        using var toWater = StaticOverride.Keep(() => RoadCrossingDetector.ToWater);
        RoadCrossingDetector.ToWater = true;
        var world = new Gorge();
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        var older = new List<Vector2>();
        for (float z = -60f; z <= -20f + 0.01f; z += 2f) older.Add(new Vector2(-24f, z));
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(older, older.ConvertAll(_ => 36f), older.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        int before = RoadSpatialGrid.RoadNetworkVersion;
        RoadSpatialGrid.RoadPoint onLanding = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(-24f, 0f, -20f), 0.1f).First(rp => rp.IsRoad);

        var c = RoadCrossing.Between(new Vector2(-16f, 0f), new Vector2(16f, 0f), 20f, new Vector2(0f, 0f), 24f, CrossingKind.Bridge);
        c.FromEnd = new StructureEnds.End(c.FromBank, c.Direction, 38f, 2);
        c.ToEnd = new StructureEnds.End(c.ToBank, -c.Direction, 38f, 2);
        // Both ends raise the same point (their landings overlap on it): it goes to the higher.
        c.FromRaised = new List<(RoadSpatialGrid.RoadPoint at, float to)> { (onLanding, 36.5f) };
        c.ToRaised = new List<(RoadSpatialGrid.RoadPoint at, float to)> { (onLanding, 37f) };
        var road = new List<Vector2>();
        for (float x = -60f; x <= -16f + 0.01f; x += 1f) road.Add(new Vector2(x, 0f));
        var piece = new RoadSpatialGrid.PlannedPath(road, road.ConvertAll(_ => 36f), road.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 44f, false);
        RoadNetworkGenerator.AppendManualRoad(new[] { piece }, new List<RoadCrossing> { c }, Array.Empty<RoadStairSpan>(), road[0], before);

        var raised = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(-24f, 0f, -20f), 0.1f).Where(rp => rp.IsRoad).ToList();
        Assert.NotEmpty(raised);
        Assert.All(raised, rp => Assert.Equal(37f, rp.h, 3));
        Assert.Contains(RoadSpatialGrid.PendingPoints(RoadSpatialGrid.GetRoadPointsInZone(ZoneSystem.GetZone(new Vector3(-24f, 0f, -20f))), before),
            rp => rp.IsRoad && Vector2.Distance(rp.p, onLanding.p) < 0.01f);
        foreach (float x in new[] { -14.5f, 14.5f })
        {
            var under = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(x, 0f, 0f), 0.6f).Where(rp => rp.IsRoad && rp.unpainted).ToList();
            Assert.True(under.Count > 0, $"no ground carried in under the deck's end at x = {x}");
        }
    }

    public ManualRoadPipelineTests()
    {
        RoadNetworkGenerator.Reset();
        // One search for both roads, with stairs and viaducts (the test suite's defaults are plain roads), the ground's
        // roughness not priced.
        RoadNetworkGenerator.ConfigureFinders(f => { f.Stairs = true; f.Viaducts = true; f.TerrainVarianceThreshold = 1000f; f.Meander = 0f; });
    }

    public void Dispose()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.ConfigureFinders(null);
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    private static Island Everywhere()
    {
        var i = new Island { Id = 1, CellSize = 8, WorldOffset = 0, Min = new Vector2(-280, -280), Max = new Vector2(280, 280) };
        for (int x = -35; x <= 35; x++) for (int z = -35; z <= 35; z++) i.Cells.Add(new Vector2Int(x, z));
        return i;
    }

    /// <summary>The generated road between the two ends, with the generator's own search, then forgotten; and the manual road's plan.</summary>
    private (List<RoadCrossing> crossings, int stairs, ManualRoads.Plan? manual, string? refused) Both(WorldGenerator world, Vector2 a, Vector2 b)
    {
        _scope.WithWorld(world);
        SetPathfinder(RoadNetworkGenerator.NewFinder(world, 4f));
        RoadNetworkGenerator.GenerateRoad(new Vector3(a.x, 0f, a.y), 0f, new Vector3(b.x, 0f, b.y), 0f, 4f, "generated");
        var crossings = RoadNetworkGenerator.GetRoadCrossings().ToList();
        int stairs = StairStructures.Runs.Count;
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadNetworkGenerator.MarkLocationsReady();
        try { return (crossings, stairs, ManualRoads.Prepare(new[] { a, b }, false, new[] { Everywhere() }), null); }
        catch (InvalidOperationException e) { return (crossings, stairs, null, e.Message); }
    }

    [Fact]
    public void OverARiverAManualRoadGetsTheGeneratedRoadsBridge()
    {
        var (crossings, _, manual, refused) = Both(new River(), new Vector2(-100f, 0f), new Vector2(100f, 0f));
        Assert.True(manual != null, refused);
        RoadCrossing generated = Assert.Single(crossings, c => c.Kind == CrossingKind.Bridge);
        RoadCrossing built = Assert.Single(manual!.NewCrossings, c => c.Kind == CrossingKind.Bridge);
        Assert.True(RoadCrossing.SameBanks(generated, built), $"{generated.FromBank}->{generated.ToBank} against {built.FromBank}->{built.ToBank}");
    }

    [Fact]
    public void UpASteepFaceAManualRoadGetsTheGeneratedRoadsStaircase()
    {
        var (_, stairs, manual, refused) = Both(new Scarp(), new Vector2(-60f, 0f), new Vector2(120f, 0f));
        Assert.True(stairs > 0, "the generated road climbs the face on a staircase");
        Assert.True(manual != null, refused);
        Assert.Equal(stairs, manual!.Stairs.Count);
    }

    /// <summary>Whether a run's walked pieces close their cells to later roads (StairStructures.MarkStanding).</summary>
    private static bool Stands(StairRun run) => run.Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport)
        .Select(p => (Mathf.RoundToInt(p.Position.x / RoadPathfinder.CellSize), Mathf.RoundToInt(p.Position.z / RoadPathfinder.CellSize)))
        .Any(c => StairStructures.Standing(c.Item1, c.Item2));

    [Fact]
    public void AManualRoadsStaircasesAreAddedInOneAppendWithItAndSurviveASaveAndLoad()
    {
        // The road and its staircases' earthwork are one append (both or neither); the staircases are the network's,
        // standing for later roads, queued for the zones already spawned, and saved with their kit.
        _scope.WithWorld(new Scarp()).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        ManualRoads.Plan plan = ManualRoads.Prepare(new[] { new Vector2(-60f, 0f), new Vector2(120f, 0f) }, false, new[] { Everywhere() });
        Assert.NotEmpty(plan.Stairs);
        ManualRoads.Commit(plan);
        Assert.Equal(1, RoadSpatialGrid.AppendCount);
        Assert.Equal(plan.Stairs.Count, StairStructures.Runs.Count);
        Assert.All(StairStructures.Runs, run => Assert.True(Stands(run), "a later road's search would lay over it"));
        var kits = StairStructures.Runs.Select(r => r.Kit?.Name).ToList();
        Assert.All(kits, k => Assert.False(string.IsNullOrEmpty(k)));

        RoadLifecycleManager.OnPrepareSave(); RoadNetworkGenerator.Reset();
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData()); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        Assert.Equal(kits, StairStructures.Runs.Select(r => r.Kit?.Name).ToList());
        Assert.All(StairStructures.Runs, run => Assert.True(Stands(run), "a loaded staircase stands for later roads"));
    }

    [Theory]
    [InlineData(8f, 14f, 1.2f)]
    public void AcrossADeepMistlandsValleyAManualRoadGetsTheGeneratedRoadsViaduct(float floorHalf, float depth, float face)
    {
        var (crossings, stairs, manual, refused) = Both(new MistValley { FloorHalf = floorHalf, Depth = depth, Face = face }, new Vector2(-100f, 0f), new Vector2(100f, 0f));
        Assert.True(manual != null, refused);
        var generated = crossings.FindAll(c => c.ValleyJump);
        var built = manual!.NewCrossings.Where(c => c.ValleyJump).ToList();
        Assert.Equal(generated.Count, built.Count);
        foreach (RoadCrossing g in generated) Assert.Contains(built, c => RoadCrossing.SameBanks(g, c));
        Assert.Equal(stairs, manual.Stairs.Count);
        Assert.True(generated.Count > 0, $"no viaduct generated (floor {floorHalf}, depth {depth}): {stairs} staircases");
    }

    [Fact]
    public void ARiverBridgesUnmetEndRefusesAManualRoadNoMoreThanAGeneratedOne()
    {
        // Generation builds a road whose river bridge end it cannot meet (counted, not refused); only a valley viaduct's
        // refuses a road. A manual road is held to the same rule.
        var world = new MistRiver { Bank = 34f, Biome = Heightmap.Biome.Meadows };
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaductTests.River();
        Vector2 aside = new(0f, 12f);
        // The road in its two pieces either side of the bridge, as it is planned; the far piece leaves the bank sideways.
        var plans = new List<RoadSpatialGrid.PlannedPath>
        {
            RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(-100f, 0f), c.FromBank }, 4f, world)!,
            RoadSpatialGrid.PlanRoadPath(new List<Vector2> { c.ToBank, c.ToBank + aside, new(100f, 12f) }, 4f, world)!,
        };
        Assert.NotNull(RoadNetworkGenerator.BuiltRefusal(plans, new List<RoadCrossing> { c }, out _, out _));
        string? refused = RoadNetworkGenerator.BuiltRoadRefusal(plans, new List<RoadCrossing> { c }, out Vector2 at);
        Assert.True(refused == null, $"{refused} at {at}");
    }

    [Fact]
    public void AValleyViaductsUnmetEndRefusesAManualRoadAsItDoesAGeneratedOne()
    {
        var world = new MistValley();
        _scope.WithWorld(world);
        RoadCrossing? c = DvergrViaduct.ValleyCrossing(new Vector2(-20f, 0f), new Vector2(20f, 0f), world, out string why);
        Assert.True(c != null, why);
        Assert.True(c!.ValleyJump);
        var plans = new List<RoadSpatialGrid.PlannedPath>
        {
            RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(-100f, 0f), c.FromBank }, 4f, world)!,
            RoadSpatialGrid.PlanRoadPath(new List<Vector2> { c.ToBank, c.ToBank + new Vector2(0f, 12f), new(100f, 12f) }, 4f, world)!,
        };
        Assert.NotNull(RoadNetworkGenerator.BuiltRefusal(plans, new List<RoadCrossing> { c }, out _, out string built));
        Assert.Equal(built, RoadNetworkGenerator.BuiltRoadRefusal(plans, new List<RoadCrossing> { c }, out _));
    }

    [Fact]
    public void AManualRoadMarkedBesideAnotherIsNeverBuiltBesideIt()
    {
        var world = new Flat();
        _scope.WithWorld(world).WithZdos().WithZoneSystem();
        SetPathfinder(RoadNetworkGenerator.NewFinder(world, 4f));
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(0f, 0f, -80f), 0f, new Vector3(0f, 0f, 80f), 0f, 4f, "trunk"));
        RoadNetworkGenerator.MarkLocationsReady();
        SetPathfinder(null);
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        var lens = new List<RoadSpatialGrid.PlannedPath> { RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(4.5f, -40f), new(4.5f, 40f) }, 4f, world)! };
        Assert.NotNull(RoadNetworkGenerator.BuiltRoadRefusal(lens, new List<RoadCrossing>(), out _));
        // Marked beside the trunk, the manual road is the generator's road: refused, or riding the trunk and storing
        // nothing of its own beside it; never a second road 4.5 m off it.
        ManualRoads.Plan? plan = null;
        try { plan = ManualRoads.Prepare(new[] { new Vector2(4.5f, -40f), new Vector2(4.5f, 40f) }, false, new[] { Everywhere() }); }
        catch (InvalidOperationException) { }
        if (plan != null)
            Assert.DoesNotContain(plan.Paths.SelectMany(p => p.Points), q => Mathf.Abs(q.y) < 30f && Mathf.Abs(q.x) > 1f && Mathf.Abs(q.x) < 6f);
    }
}
