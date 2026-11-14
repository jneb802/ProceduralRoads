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
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithNetwork(server: true);

    /// <summary>A face of 1.2 from x = 24 to 48 (28.8 m up, its rim on a search cell line) in the mountains: too steep for road.</summary>
    private sealed class Scarp : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : 40f + Mathf.Clamp((wx - 24f) * 1.2f, 0f, 28.8f);
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
    /// <summary>A channel 14 m wide and 25 m deep across x = 0 (BridgeFreeSpanTests'): a road along z = 0 bridges it.</summary>
    private sealed class Channel : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Mathf.Abs(x) < 7f ? 6f : 31f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = Mathf.Abs(x) < 7f ? 1f : 0f;
            width = 14f;
        }
        public override int GetSeed() => 80;
    }

    private sealed class Gorge : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : Mathf.Abs(wx) < 12f ? 20f : 36f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 79;
    }

    /// <summary>
    /// Every structure a manual road's append builds reads the ground its checks read (RoadNetworkGenerator.AsWritten): a
    /// bridge over a narrow channel and a staircase on its west bank, the staircase's higher stair standing over a vertex the bridge
    /// saves its ground at, and its lower stair's cut reaching under it. The bridge is built (StructureAppendQueue.Prepare)
    /// inside the append's one walk-floor context with its staircases (RoadTerrainModifier.AsStored), so the ground it saves
    /// there is the ground the checks read, floored under the higher stair (StairStructures.CutFloor). Before, the bridge was
    /// built before that context and saved the batter dug out under the stair.
    /// </summary>
    [Fact]
    public void AManualRoadsBridgeSavesTheGroundItsChecksRead()
    {
        var world = new Channel();
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        var far = new List<Vector2> { new(200f, 200f), new(240f, 200f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(far, far.ConvertAll(_ => 31f), far.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        int before = RoadSpatialGrid.RoadNetworkVersion;
        var across = new List<Vector2> { new(-24f, 0f), new(-16f, 0f), new(-8f, 0f), new(8f, 0f), new(16f, 0f), new(24f, 0f) };
        RoadCrossing c = Assert.Single(RoadCrossingDetector.Detect(across, world, bridges: true, fords: false));
        // A vertex on the west bank the bridge saves its ground at.
        List<StructurePiece> pieces = BridgeStructures.Solve(c, world, world.GetSeed());
        Assert.NotEmpty(pieces);
        GroundPin bank = StructureGround.Measure(pieces, StructureGround.Vertices(world))
            .Where(pin => pin.X < 0 && Mathf.Abs(pin.Height - 31f) < 0.01f).OrderBy(pin => pin.X).First();
        // A staircase there: its higher stair over that vertex, its lower stair 2 m south cut 3 m down.
        var run = new StairRun { From = new Vector2(bank.X, bank.Z - 3f), To = new Vector2(bank.X, bank.Z + 1f), Width = RoadStairRuns.Width, Kit = StairKit.Stone };
        run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = StairKit.Stone.Stair, Position = new Vector3(bank.X, 29f, bank.Z - 2f), YawDegrees = 180f });
        run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = StairKit.Stone.Stair, Position = new Vector3(bank.X, 31.5f, bank.Z), YawDegrees = 180f });
        var road = new List<Vector2>();
        for (float x = -60f; x <= c.FromBank.x + 0.01f; x += 1f) road.Add(new Vector2(x, 0f));
        var piece = new RoadSpatialGrid.PlannedPath(road, road.ConvertAll(_ => 31f), road.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 44f, false);
        var append = new RoadNetworkGenerator.ManualAppend();
        append.WithFills.Add(piece);
        append.Runs.Add((run, new StairPlan(), StairKit.Stone));
        append.StairCuts.Add(new Vector3(bank.X, 28f, bank.Z - 2f));

        float checkedThere;
        using (RoadNetworkGenerator.AsWritten(append))
            checkedThere = StructureGround.Vertices(world)(bank.X, bank.Z);
        Assert.True(checkedThere > 30.9f, $"the checks read {checkedThere:F2} under the higher stair");
        RoadNetworkGenerator.AppendManualRoad(new[] { piece }, new List<RoadCrossing> { c }, Array.Empty<RoadStairSpan>(), road[0], before, append);
        BuiltBridge built = Assert.IsAssignableFrom<BuiltBridge>(BuiltBridges.Of(c));
        GroundPin saved = Assert.Single(built.Ground, pin => pin.X == bank.X && pin.Z == bank.Z);
        Assert.True(Mathf.Abs(saved.Height - checkedThere) < 1e-3f, FormattableString.Invariant($"the bridge saved {saved.Height:F2} at ({bank.X}, {bank.Z}), its checks read {checkedThere:F2}"));
    }

    /// <summary>
    /// A manual road's crossings change the network as a generated road's do (RoadNetworkGenerator.GroundOf), in its one
    /// append: another road on a bridge end's landing is raised to it (to the higher, where both ends raise it), and the ground under its deck
    /// is cut to the deck's underside (BridgeStructures.GroundUnderDeck). The raised road is the append's, so terrain written before it is written again.
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
        float underside = 38f - BridgeStructures.DeckDepth(c, world);
        foreach (float x in new[] { -12f, 12f })
        {
            var under = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(x, 0f, 0f), 0.6f).Where(rp => rp.cutOnly).ToList();
            Assert.True(under.Count > 0, $"no ground cut under the deck's end at x = {x}");
            Assert.All(under, rp => Assert.Equal(underside, rp.h, 3));
        }
    }

    /// <summary>
    /// A manual road's bridge reaches the vegetation of the zones next to the one its deck stands in, as generation reads it
    /// (StructurePlans.WalksNear: a tree rooted in the next zone, over the deck). A zone generated and cleared before the road,
    /// with no road point near it, owes its clearing again once the bridge is in the plans, as the full bake would queue it
    /// (StructurePlans.ZonesWithWalks, limited to the append's structures): held for the bake, owed no road appended, judged
    /// by the walks generation reads there. Before, only the zones of the bridge's pieces and the road's reach were owed, and
    /// the next zone stayed recorded cleared with a tree over the new deck.
    /// </summary>
    [Fact]
    public void AManualRoadsBridgeOwesTheNextZoneItsDecksWalkReachesItsClearingAsGenerationReadsIt()
    {
        using var toWater = StaticOverride.Keep(() => RoadCrossingDetector.ToWater);
        RoadCrossingDetector.ToWater = true;
        var world = new Gorge();
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        int before = RoadSpatialGrid.RoadNetworkVersion;
        // The zone past the far bank, generated and cleared with no structure near it.
        var next = new Vector2s(1, 0);
        ZoneSystem.instance!.Generated.Add(next);
        VegetationClearing.MarkCleared(next, before);
        Assert.True(VegetationClearing.IsCleared(next, before));
        VegetationClearing.TakeReached();

        var c = RoadCrossing.Between(new Vector2(-16f, 0f), new Vector2(16f, 0f), 20f, new Vector2(0f, 0f), 24f, CrossingKind.Bridge);
        c.Kit = BridgeStructures.Kit.DvergrWood;
        c.FromEnd = new StructureEnds.End(c.FromBank, c.Direction, 36f, 0);
        c.ToEnd = new StructureEnds.End(c.ToBank, -c.Direction, 36f, 0);
        var road = new List<Vector2>();
        for (float x = -60f; x <= -16f + StructureEnds.JoinOverlap + 0.01f; x += 0.5f) road.Add(new Vector2(x, 0f));
        var piece = new RoadSpatialGrid.PlannedPath(road, road.ConvertAll(_ => 36f), road.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 44f, false);
        RoadNetworkGenerator.AppendManualRoad(new[] { piece }, new List<RoadCrossing> { c }, Array.Empty<RoadStairSpan>(), road[0], before);

        // No road reaches it, and the bridge stands in the zone next to it: its walk is all it owes.
        Assert.DoesNotContain(next, RoadSpatialGrid.ZonesChangedSince(before)!);
        Assert.DoesNotContain(next, StructurePlans.PlannedZones);
        var generation = RoadClearAreaManager.ForGeneration(next, new List<ZoneSystem.ClearArea>(), judged: true, out var generationRoad, out _);
        Assert.NotEmpty(generation.Walks);
        Assert.Contains(next, StructurePlans.ZonesWithWalks());

        int now = RoadSpatialGrid.RoadNetworkVersion;
        Assert.False(VegetationClearing.IsCleared(next, now), "the next zone stayed recorded cleared without the bridge's walk");
        Assert.Equal(RoadSpatialGrid.AppendCount, VegetationClearing.OwedSince(next));
        Assert.Contains(next, VegetationClearing.TakeReached() ?? new List<Vector2s>());
        var bake = RoadClearAreaManager.ForBake(next, VegetationClearing.OwedSince(next), out var owedRoad);
        // The walk is judged as a road centre is (RoadClearAreaManager.RoadCentresSince): a tree there reaching over it, as generation judges it.
        Assert.NotEmpty(owedRoad);
        Assert.Equal(generation.Walks.ToArray(), bake.Walks.ToArray());
        Assert.Equal(generationRoad.Select(r => (r.Centre, r.HalfWidth, r.Height)), owedRoad.Select(r => (r.Centre, r.HalfWidth, r.Height)));
        Assert.Equal(bake.FirstRoad, bake.FirstStructure);
    }

    /// <summary>
    /// A viaduct's end stairs are cut as a staircase's stairs are (StairLayout.AddCuts, the one stair excavation), on a
    /// manual road as on a generated one (RoadNetworkGenerator.GroundOf): under both lanes of every step, at every point of
    /// its tread's rows, to StairLayout.CutBelowSurface under the walking surface over it, committed StairLayout.CutWidth
    /// wide. Before, one cut a step on the viaduct's line at the step's foot, the deck's 4 m wide: down to the foot under the
    /// tread's high end, a metre under its walk, and nothing at the lanes' outer edges.
    /// </summary>
    [Fact]
    public void AViaductsEndStairsAreCutAsAStaircasesStairsAre()
    {
        using var toWater = StaticOverride.Keep(() => RoadCrossingDetector.ToWater);
        RoadCrossingDetector.ToWater = true;
        var world = new Gorge();
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        int before = RoadSpatialGrid.RoadNetworkVersion;

        var c = RoadCrossing.Between(new Vector2(-16f, 0f), new Vector2(16f, 0f), 20f, new Vector2(0f, 0f), 24f, CrossingKind.Bridge);
        c.Kit = BridgeStructures.Kit.DvergrViaduct;
        c.FromEnd = new StructureEnds.End(c.FromBank, c.Direction, 38f, 2);
        c.ToEnd = new StructureEnds.End(c.ToBank, -c.Direction, 38f, 2);
        var road = new List<Vector2>();
        for (float x = -60f; x <= c.FromEnd.Value.Meet.x + 0.01f; x += 1f) road.Add(new Vector2(x, 0f));
        var piece = new RoadSpatialGrid.PlannedPath(road, road.ConvertAll(_ => 36f), road.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false);
        RoadNetworkGenerator.AppendManualRoad(new[] { piece }, new List<RoadCrossing> { c }, Array.Empty<RoadStairSpan>(), road[0], before);

        Vector2 side = new(-c.Direction.y, c.Direction.x);
        foreach (StructureEnds.End end in new[] { c.FromEnd.Value, c.ToEnd.Value })
        {
            Vector2 right = new(-end.Up.y, end.Up.x);
            for (int k = 0; k < end.Steps; k++)
                foreach (int lane in new[] { -1, 1 })
                {
                    // The step's low edge, outward; its walk rises a rise over its 2 m run toward the deck.
                    Vector2 back = StructureEnds.StepCentre(end.Edge, end.Up, k) + side * (lane * BridgeLayout.LaneOffset) - end.Up * (StairLayout.PieceRun / 2f);
                    float foot = StructureEnds.StepFoot(end.Height, k);
                    for (float along = 0.25f; along < StairLayout.PieceRun; along += StairLayout.CutSpacing)
                        for (float across = -0.75f; across < 1f; across += StairLayout.CutSpacing)
                        {
                            Vector2 p = back + end.Up * along + right * across;
                            float surface = foot + StairLayout.PieceRise * along / StairLayout.PieceRun;
                            var cuts = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(p.x, 0f, p.y), 0.05f).Where(rp => rp.cutOnly).ToList();
                            Assert.True(cuts.Exists(rp => Mathf.Abs(rp.w - StairLayout.CutWidth) < 1e-4f
                                    && Mathf.Abs(rp.h - (surface - StairLayout.CutBelowSurface)) < 1e-3f),
                                FormattableString.Invariant($"step {k} lane {lane} at ({p.x:F2}, {p.y:F2}): surface {surface:F2}, cuts ")
                                + string.Join(", ", cuts.Select(rp => FormattableString.Invariant($"{rp.h:F2} w {rp.w:F2}"))));
                        }
                }
        }
    }

    /// <summary>
    /// A Dvergr wood bridge whose ends are met level, the road run on under each deck's end at its walking surface
    /// (StructureEnds.JoinOverlap), over banks standing at that height: the ground under the deck is cut to the deck's
    /// underside (BridgeStructures.GroundUnderDeck), only the batter's wedge under its first metres standing over it, and
    /// never up to the walking surface; the road before the edge keeps its height. Before, the bank stood level with the planks'
    /// top under each end and showed through them (at one bridge 31.50 m ground under 31.50 m plank tops).
    /// </summary>
    [Fact]
    public void TheGroundUnderADeckMetLevelStaysUnderItsUnderside()
    {
        using var toWater = StaticOverride.Keep(() => RoadCrossingDetector.ToWater);
        RoadCrossingDetector.ToWater = true;
        var world = new Gorge();
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        int before = RoadSpatialGrid.RoadNetworkVersion;

        var c = RoadCrossing.Between(new Vector2(-16f, 0f), new Vector2(16f, 0f), 20f, new Vector2(0f, 0f), 24f, CrossingKind.Bridge);
        c.Kit = BridgeStructures.Kit.DvergrWood;
        c.FromEnd = new StructureEnds.End(c.FromBank, c.Direction, 36f, 0);
        c.ToEnd = new StructureEnds.End(c.ToBank, -c.Direction, 36f, 0);
        // The road to the edge and on under the deck's end at its walking surface, as the generator lays it.
        var road = new List<Vector2>();
        for (float x = -60f; x <= -16f + StructureEnds.JoinOverlap + 0.01f; x += 0.5f) road.Add(new Vector2(x, 0f));
        var piece = new RoadSpatialGrid.PlannedPath(road, road.ConvertAll(_ => 36f), road.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 44f, false);
        RoadNetworkGenerator.AppendManualRoad(new[] { piece }, new List<RoadCrossing> { c }, Array.Empty<RoadStairSpan>(), road[0], before);

        var zone = new Vector2s(0, 0);
        Heightmap hm = _scope.RegisterHeightmap(zone, 64);
        TerrainComp tc = hm.m_terrainComp!;
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, tc);
        float Ground(int x, int z)
        {
            int i = (z + 32) * 65 + (x + 32);
            return BiomeBlendedHeight.GetBlendedHeight(x, z, world) + (tc.m_modifiedHeight[i] ? tc.m_levelDelta[i] + tc.m_smoothDelta[i] : 0f);
        }
        float depth = DvergrWoodBridge.DeckDepth;
        Assert.Equal(depth, BridgeStructures.DeckDepth(c, world));
        foreach (int sign in new[] { -1, 1 })
            for (int along = 1; along <= 4; along++)
                for (int z = -1; z <= 1; z++)
                {
                    // On the line the batter's wedge climbs to the walking surface at the edge; off it the cuts' batter
                    // climbs a little higher (they are round), still well under the walking surface.
                    int x = sign * (16 - along);
                    float g = Ground(x, z), most = 36f - Mathf.Min(along, depth) * (z == 0 ? 1f : 0.5f);
                    Assert.True(g <= most + 1e-3f, $"ground {g:F2} at ({x}, {z}), {along} m under the deck's end: over {most:F2} (deck 36, underside {36f - depth:F2})");
                }
        for (int x = -20; x <= -17; x++)
            Assert.True(Mathf.Abs(Ground(x, 0) - 36f) < 0.05f, $"the road before the deck's edge at x = {x} stands {Ground(x, 0):F2}, not 36");
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

    /// <summary>
    /// A staircase its kit refuses (StairKit.Dress: a walking piece the support rule breaks however held; a test's kit here,
    /// StairStructures.Dressing, refusing every staircase) refuses the road it is on, before anything of the road is stored,
    /// on every path that publishes a road with a staircase, by one reading (RoadNetworkGenerator.StairsRefusal): generation
    /// (its road refused and counted by that reason, no road, cut or staircase stored), a road_connect's plan (the
    /// generator's own, captured: refused for the same reason) and a marked manual road (refused with it, nothing added).
    /// Before, the road and its staircase's cut were stored and the staircase was refused at its first build: a cut with no
    /// stair.
    /// </summary>
    [Fact]
    public void AStaircaseItsKitRefusesRefusesItsRoadGeneratedOrManualAlike()
    {
        var world = new Scarp();
        _scope.WithWorld(world);
        var dressing = StairStructures.Dressing;
        try
        {
            StairStructures.Dressing = (run, w, finished, shortfall, _) =>
            {
                shortfall.Falling.Add(run.Pieces.First(p => p.Kind != StructurePieceKind.StairSupport).Position);
                return new List<StructurePiece>();
            };
            SetPathfinder(RoadNetworkGenerator.NewFinder(world, 4f));
            Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector3(-60f, 0f, 0f), 0f, new Vector3(120f, 0f, 0f), 0f, 4f, "generated"));
            Assert.Equal(StairStructures.RefusedBySupport, RoadSpatialGrid.LastRefusal);
            Assert.Empty(StairStructures.Runs);
            Assert.Empty(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(30f, 0f, 0f), 90f));

            var capture = new RoadNetworkGenerator.RoadCapture();
            using (RoadNetworkGenerator.PlanningWith(RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(4f)), capture))
                Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector3(-60f, 0f, 0f), 0f, new Vector3(120f, 0f, 0f), 0f, 4f, "captured"));
            Assert.Contains(capture.Refusals, r => r.why.Contains(StairStructures.RefusedBySupport));

            SetPathfinder(null);
            RoadNetworkGenerator.Reset();
            RoadNetworkGenerator.MarkLocationsReady();
            var error = Assert.Throws<InvalidOperationException>(() =>
                ManualRoads.Prepare(new[] { new Vector2(-60f, 0f), new Vector2(120f, 0f) }, false, new[] { Everywhere() }));
            Assert.Contains(StairStructures.RefusedBySupport, error.Message);
            Assert.Empty(StairStructures.Runs);
            Assert.Empty(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(30f, 0f, 0f), 90f));
        }
        finally { StairStructures.Dressing = dressing; }
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

    /// <summary>
    /// A river bridge the built road does not allow (BuiltRefusal: road under its deck) refuses its road as a valley viaduct
    /// does, of every kind alike: no bridge or span is kept with road under its deck (decision 17: reject, never protect).
    /// Before, only a valley viaduct's failure refused the road; a river bridge's or a span's was counted and the road built.
    /// </summary>
    [Fact]
    public void ARiverBridgeTheBuiltRoadDoesNotAllowRefusesAManualRoadAsAGeneratedOne()
    {
        var world = new MistRiver { Bank = 34f, Biome = Heightmap.Biome.Meadows };
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaductTests.River();
        Assert.False(c.ValleyJump);
        Vector2 aside = new(0f, 12f);
        // The road in its two pieces either side of the bridge, as it is planned; the far piece leaves the bank sideways.
        var plans = new List<RoadSpatialGrid.PlannedPath>
        {
            RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(-100f, 0f), c.FromBank }, 4f, world)!,
            RoadSpatialGrid.PlanRoadPath(new List<Vector2> { c.ToBank, c.ToBank + aside, new(100f, 12f) }, 4f, world)!,
        };
        Assert.Same(c, RoadNetworkGenerator.BuiltRefusal(plans, new List<RoadCrossing> { c }, out _, out string built));
        string? refused = RoadNetworkGenerator.BuiltRoadRefusal(plans, new List<RoadCrossing> { c }, out _);
        Assert.Equal(built, refused);
    }

    /// <summary>
    /// A river bridge with a road laid before under its deck (3 m under it, across its middle) is not built, by generation
    /// nor by a marked manual road over the same ends: generation searches the road again without that bridge (its crossing
    /// ban, as a valley viaduct the built road does not allow is), and every bridge it then stores has no road under its deck;
    /// a manual road, which has no other route, is refused for the road under the deck or gets a bridge clear of it. Before, the
    /// bridge was built over the road (BuiltRefusal's failure kept for every crossing but a valley viaduct).
    /// </summary>
    [Fact]
    public void ARiverBridgeWithRoadUnderItsDeckIsNotBuiltGeneratedOrManual()
    {
        var world = new River();
        _scope.WithWorld(world);
        Vector2 a = new(-100f, 0f), b = new(100f, 0f);
        // Where generation bridges the river with nothing under the deck.
        SetPathfinder(RoadNetworkGenerator.NewFinder(world, 4f));
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(a.x, 0f, a.y), 0f, new Vector3(b.x, 0f, b.y), 0f, 4f, "clear"));
        RoadCrossing first = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.Kind == CrossingKind.Bridge);
        Assert.False(first.ValleyJump);
        float deck = BridgeStructures.DeckHeight(first, world);
        Vector2 along = (first.ToBank - first.FromBank).normalized, side = new(-along.y, along.x);
        SetPathfinder(null);

        void UnderTheDeck()
        {
            RoadNetworkGenerator.Reset();
            RoadSpatialGrid.Clear();
            StructureStretchTests.LayRoad(first.Center - side * 8f, first.Center + side * 8f, _ => deck - 3f);
        }
        static void Clear(IEnumerable<RoadCrossing> crossings, List<RoadSpatialGrid.PlannedPath> plans, string who)
        {
            foreach (RoadCrossing x in crossings)
                Assert.True(RoadNetworkGenerator.BuiltRefusal(plans, new List<RoadCrossing> { x }, out Vector2 at, out string why) == null,
                    $"{who}: {x.Kind} {x.FromBank} -> {x.ToBank} built with {why} at {at}");
        }

        // Generation: searched again without the bridge, or refused; never stored over the road.
        UnderTheDeck();
        SetPathfinder(RoadNetworkGenerator.NewFinder(world, 4f));
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        RoadNetworkGenerator.PieceCommitted = (plan, _) => roads.Add(plan);
        bool generated = RoadNetworkGenerator.GenerateRoad(new Vector3(a.x, 0f, a.y), 0f, new Vector3(b.x, 0f, b.y), 0f, 4f, "under");
        RoadNetworkGenerator.PieceCommitted = null;
        var (searchedRound, refusedPast) = RoadNetworkGenerator.CrossingsTheBuiltRoadRefused;
        Assert.True(searchedRound + refusedPast > 0, $"the bridge over the road was neither searched round nor refused (built {generated})");
        if (generated) Clear(RoadNetworkGenerator.GetRoadCrossings(), roads, "generated");
        SetPathfinder(null);

        // A marked manual road over the same ends: refused with the reason, or a bridge clear of the road.
        UnderTheDeck();
        RoadNetworkGenerator.MarkLocationsReady();
        ManualRoads.Plan? manual = null;
        string? refused = null;
        try { manual = ManualRoads.Prepare(new[] { a, b }, false, new[] { Everywhere() }); }
        catch (InvalidOperationException e) { refused = e.Message; }
        if (manual == null) Assert.Contains("road under the deck", refused);
        else Clear(manual.NewCrossings, manual.Paths, "manual");
    }

    /// <summary>
    /// A road planned under the deck of a river bridge laid before it is refused as one under a viaduct's deck is, by the one
    /// list every road is judged by (generation's final check, a marked manual road: RoadNetworkGenerator.RoadRefusal, asked
    /// here through BuiltRoadRefusal) and by its deck check itself (UnderALaidDeck), in the session that laid the bridge and
    /// after a reload alike. Before, the network kept only viaducts' decks: a later road under a bridge's or a ford span's
    /// deck was checked by nothing (BuiltRefusal reads only a road's own crossings).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARoadUnderALaidRiverBridgesDeckIsRefusedAsUnderAViaducts(bool reload)
    {
        var world = new River();
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        var across = ManualRoads.Prepare(new[] { new Vector2(-100f, 0f), new Vector2(100f, 0f) }, false, new[] { Everywhere() });
        RoadCrossing bridge = Assert.Single(across.NewCrossings, c => c.Kind == CrossingKind.Bridge);
        Assert.False(bridge.ValleyJump);
        ManualRoads.Commit(across);
        if (reload)
        {
            RoadNetworkGenerator.SaveGlobalRoadData();
            RoadNetworkGenerator.Reset();
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData()); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            RoadNetworkGenerator.MarkLocationsReady();
        }
        // A road across the river bed under the middle of the deck, on its ground (nothing to fill), at right angles to it.
        Vector2 along = (bridge.ToBank - bridge.FromBank).normalized, side = new(-along.y, along.x);
        var points = new List<Vector2> { bridge.Center - side * 20f, bridge.Center, bridge.Center + side * 20f };
        var under = new List<RoadSpatialGrid.PlannedPath>
        {
            new(points, points.ConvertAll(p => world.GetHeight(p.x, p.y)), new List<RoadPointDebugInfo>(), 4f, 40f, false),
        };
        Assert.True(BridgeStructures.DeckHeight(bridge, world) - world.GetHeight(bridge.Center.x, bridge.Center.y) > 2f, "the deck stands over the river bed");
        Assert.Equal("passes under a bridge's deck", RoadNetworkGenerator.UnderALaidDeck(under, out Vector2 at));
        Assert.True(Vector2.Distance(at, bridge.Center) <= 20.5f, $"refused at {at}");
        Assert.Equal("passes under a bridge's deck", RoadNetworkGenerator.BuiltRoadRefusal(under, new List<RoadCrossing>(), out _));
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

    /// <summary>A trunk road committed between <paramref name="a"/> and <paramref name="b"/> by the generator's own search, the
    /// network then loaded as a manual road meets it.</summary>
    private void Trunk(WorldGenerator world, Vector2 a, Vector2 b)
    {
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        SetPathfinder(RoadNetworkGenerator.NewFinder(world, 4f));
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(a.x, 0f, a.y), 0f, new Vector3(b.x, 0f, b.y), 0f, 4f, "trunk"));
        SetPathfinder(null);
        RoadNetworkGenerator.MarkLocationsReady();
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
    }

    /// <summary>The mod's own network snap (the suite's plain roads have none).</summary>
    private static IDisposable Snapping()
    {
        var keep = StaticOverride.Keep(() => RoadNetworkGenerator.RoadSnap).AndKeep(() => RoadNetworkGenerator.CorridorSnap);
        RoadNetworkGenerator.RoadSnap = (float)PlainRoads.ModDefaults["road snap"];
        RoadNetworkGenerator.CorridorSnap = (float)PlainRoads.ModDefaults["corridor snap"];
        return keep;
    }

    /// <summary>
    /// A road marked 8 m beside a trunk that bridges a river is snapped onto the trunk before its crossings and pieces are
    /// planned (RoadNetworkGenerator.PlanSearchedPath), as a generated road is: it takes the trunk's bridge, rides the trunk,
    /// and leaves it for each mark alike, the world and the trunk being the same either side of the river. Before, the marked
    /// road was planned unsnapped, split at the bridge and snapped piece by piece afterwards (RoadSpatialGrid.PlanRoadPathCore):
    /// its far piece left the trunk 20 m before its mark, against 14 m for the near one.
    /// </summary>
    [Fact]
    public void AManualRoadMarkedBesideATrunkRidesItAsFarAsEachMarkAlike()
    {
        using var toWater = StaticOverride.Keep(() => RoadCrossingDetector.ToWater);
        RoadCrossingDetector.ToWater = true;
        using var snapping = Snapping();
        Trunk(new River(), new Vector2(-100f, 0f), new Vector2(100f, 0f));
        ManualRoads.Plan plan = ManualRoads.Prepare(new[] { new Vector2(-100f, 8f), new Vector2(100f, 8f) }, false, new[] { Everywhere() });
        Assert.Empty(plan.NewCrossings);
        float Length(RoadSpatialGrid.PlannedPath p) { float l = 0f; for (int i = 1; i < p.Points.Count; i++) l += Vector2.Distance(p.Points[i - 1], p.Points[i]); return l; }
        Assert.Equal(2, plan.Paths.Count);
        var near = plan.Paths.Single(p => p.Points.Exists(q => q.x < 0f));
        var far = plan.Paths.Single(p => p.Points.Exists(q => q.x > 0f));
        Assert.True(Length(far) <= Length(near) + 0.5f, $"the far stub runs {Length(far):F1} m off the trunk, the near one {Length(near):F1} m");
    }

    /// <summary>
    /// A mark the shared network snap moves the road more than twice its width from is refused with that mark
    /// (RoadNetworkGenerator.WaypointRefusal), and nothing is added: no road, crossing, staircase or queued structure.
    /// </summary>
    [Fact]
    public void AMarkTheNetworkSnapCannotKeepToIsRefusedAndNothingIsAdded()
    {
        using var snapping = Snapping();
        Trunk(new Flat(), new Vector2(0f, -80f), new Vector2(0f, 80f));
        int version = RoadSpatialGrid.RoadNetworkVersion, appends = RoadSpatialGrid.AppendCount, runs = StairStructures.Runs.Count;
        var error = Assert.Throws<InvalidOperationException>(() =>
            ManualRoads.Prepare(new[] { new Vector2(10f, -40f), new Vector2(10f, 0f), new Vector2(10f, 40f) }, false, new[] { Everywhere() }));
        Assert.Contains($"waypoint {new Vector2(10f, 0f)}", error.Message);
        Assert.Contains("move that mark", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
        Assert.Equal(appends, RoadSpatialGrid.AppendCount);
        Assert.Equal(runs, StairStructures.Runs.Count);
        Assert.Empty(RoadNetworkGenerator.GetRoadCrossings());
        Assert.Empty(StructureAppendQueue.Zones);
        Assert.False(ManualRoads.Busy);
    }

    /// <summary>
    /// A road marked beside a trunk that climbs a face on a staircase is judged as the generator's road between the same two
    /// points is (RoadNetworkGenerator.PlanSearchedPath: the snap before the staircases are laid): both walk the trunk's
    /// staircase and are built. Before, the marked road was planned unsnapped, bought its own staircase beside the trunk's
    /// and was refused for a staircase that could not be laid. (With the face's rim mid-cell, both were held to the trunk over
    /// the grade cap and refused for it; that trunk is now refused in a trench.)
    /// </summary>
    [Fact]
    public void AManualRoadMarkedBesideAStaircaseRoadIsJudgedAsTheGeneratedRoadIs()
    {
        using var snapping = Snapping();
        var world = new Scarp();
        Trunk(world, new Vector2(-60f, 0f), new Vector2(120f, 0f));
        Assert.NotEmpty(StairStructures.Runs);
        Vector2 a = new(-60f, 10f), b = new(120f, 10f);
        var capture = new RoadNetworkGenerator.RoadCapture();
        bool generated;
        using (RoadNetworkGenerator.PlanningWith(RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(4f)), capture))
            generated = RoadNetworkGenerator.GenerateRoad(new Vector3(a.x, 0f, a.y), 0f, new Vector3(b.x, 0f, b.y), 0f, 4f, "beside");
        // With the face's rim on a cell line the trunk's flight reaches the top, and the road beside it walks the trunk's
        // staircase and is built; the marked road between the same points is planned too, as generation's was.
        Assert.True(generated, capture.Refusals.Count > 0 ? capture.Refusals[0].why : "refused");
        ManualRoads.Prepare(new[] { a, b }, false, new[] { Everywhere() });
    }

    /// <summary>A strip 8.5 m wide rising at 1.2 from x = 24 to flats at 48 m, a cliff down to 33 m round its foot.</summary>
    private sealed class Notch : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f
            : wx < 24f || (wx < 32f && (wy > 0.5f || wy < -8f)) ? 33f : 40f + Mathf.Clamp((wx - 24f) * 1.2f, 0f, 8f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>
    /// A road down a strip to its foot ends on a staircase, no road at its foot, a cliff round the foot's edge: no edge of
    /// the foot steps off. Its finished connection is one verdict (RoadStairRuns.ConnectionRefusal), and every path that
    /// judges a planned road asks it alike: generation's commit (GenerateRoadCore) refuses the generated road between the two
    /// points for it, and the same road searched and planned by the generator's one entry (PlanSearchedPath, as a marked
    /// manual road and an approach trial are) is refused for it by their check of a road as planned (BuiltRoadRefusal).
    /// Before, only generation's commit asked it (FootLeftOver); a marked manual road and an approach trial passed the road.
    /// (A marked road ending on a staircase longer than its mark's reach is refused for that mark first.)
    /// </summary>
    [Fact]
    public void GenerationAndAPlannedRoadsCheckJudgeAStaircaseItsRoadDoesNotMeetAlike()
    {
        var world = new Notch();
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        Vector2 a = new(100f, 0f), b = new(24f, 0f);
        var capture = new RoadNetworkGenerator.RoadCapture();
        bool generated;
        using (RoadNetworkGenerator.PlanningWith(RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(4f)), capture))
            generated = RoadNetworkGenerator.GenerateRoad(new Vector3(a.x, 0f, a.y), 0f, new Vector3(b.x, 0f, b.y), 0f, 4f, "strip");
        Assert.False(generated, "the generated road was built");
        Assert.True(capture.Refusals.Exists(r => r.why.Contains(RoadStairRuns.NoWayOffItsEnd)), string.Join("; ", capture.Refusals.Select(r => r.why)));

        RoadPathfinder finder = RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(4f));
        List<Vector2> path = finder.FindPath(a, b)!;
        Assert.NotNull(path);
        var planned = RoadNetworkGenerator.PlanSearchedPath(finder, path, path[0], 0f, path[path.Count - 1], 0f, false, false, false, false, 4f, default);
        Assert.True(planned.Plans != null, RoadSpatialGrid.LastRefusal);
        Assert.Contains(planned.Stairs, st => st.NoRoadAtFoot);
        Assert.Equal(RoadStairRuns.NoWayOffItsEnd, RoadNetworkGenerator.BuiltRoadRefusal(planned.Plans!, planned.Crossings, out Vector2 refusedAt, planned.Stairs));
        // Read on the finished ground as the road would stand (RoadNetworkGenerator.AsWritten, StairEnds.GroundOf), the ground
        // the staircase-end audit reads once it is stored: stored anyway, the audit finds the same end with no way off.
        foreach (RoadSpatialGrid.PlannedPath piece in planned.Plans!) RoadSpatialGrid.Commit(piece);
        foreach (RoadStairSpan st in planned.Stairs)
            StairStructures.Add(RoadNetworkGenerator.RunOf(st.FromPoint, st.ToPoint, st.Plan, st.Kit, atLocation: false, span: st, roads: planned.Plans));
        var audit = StairEnds.AuditAll(run => StairEnds.GroundOf(run, world));
        Assert.Contains(audit.lines, l => l.verdict is StairEnds.Verdict.Gap or StairEnds.Verdict.Miss
            && l.line.Contains(FormattableString.Invariant($"({refusedAt.x:F1}, ")) && l.line.Contains(FormattableString.Invariant($", {refusedAt.y:F1})")));
    }

    /// <summary>A strip of flats at 40 m (x under 24) rising at 1.2 from x = 24 to flats at 48 m.</summary>
    private sealed class StepDownToARoad : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : 40f + Mathf.Clamp((wx - 24f) * 1.2f, 0f, 8f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>
    /// A road down a face (StepDownToARoad) to the network: the search's last step comes down the face to the path's end at
    /// x = 24, 6.5 m short of the end of a network road running on west from x = 17.5. Its staircase's foot has no road of its
    /// own past it (the path ends there), and its floors ran on out over the flat onto the other road, the foot standing in it
    /// with no ring of its own there and no road of its own to meet it (in game a foot at 824.1,1704.3 stood in the road it
    /// joined, its end read "not verified: no road recorded at this end (its road's end there not kept)"). The generator's one
    /// planning entry (PlanSearchedPath: generation, a marked manual road and an approach trial are planned through it) does not
    /// lay it so (RoadStairRuns.Lay: its stretch is searched again); every road's final check (RoadStairRuns.NoWayOff, in
    /// RoadNetworkGenerator.RoadRefusal and BuiltRoadRefusal) refuses a foot onto a road not its own, never passes it as not
    /// verified: each end is met by its road.
    /// </summary>
    [Fact]
    public void AStaircaseFootIsNeverLaidInAnotherRoadWithNoRoadOfItsOwn()
    {
        var world = new StepDownToARoad();
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        var path = new List<Vector2> { new(72f, 0f), new(64f, 0f), new(56f, 0f), new(48f, 0f), new(40f, 0f), new(32f, 0f), new(24f, 0f) };
        var step = (from: new Vector2(32f, 0f), to: new Vector2(24f, 0f));
        Func<float, float, float> ground = (x, z) => RoadStairRuns.GroundAt(x, z, world);

        // Laid with no road there yet: its foot finished out over the flat, its own, onto nothing.
        List<RoadStairSpan>? alone = RoadStairRuns.Lay(path, new[] { step }, new List<RoadCrossing>(), world);
        Assert.True(alone != null, $"not laid: {RoadStairRuns.LastRefusal}");
        RoadStairSpan lone = Assert.Single(alone!);
        Assert.True(lone.NoRoadAtFoot);
        Assert.False(RoadStairRuns.OntoAnotherRoad(lone.Plan, ground));

        // The network's road there.
        Assert.True(RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(17.5f, 0f), new(-22.5f, 0f) }, 4f, world));
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();

        // The final check of every road: that staircase, its foot now onto the road, is refused, never passed not verified.
        var run = StairRun.FromPlan(lone.FromPoint, lone.ToPoint, RoadStairRuns.Width, lone.Plan, lone.Kit);
        StairEnds.End foot = StairEnds.Read(run, ground).Single(e => !e.Top);
        Assert.True(foot.OntoAnotherRoad && foot.Verdict == StairEnds.Verdict.NotVerified, StairEnds.Line(foot));
        Assert.NotNull(RoadStairRuns.NoWayOff(new[] { run }, _ => ground, out string why));
        Assert.Equal(RoadStairRuns.FootOntoAnotherRoad, why);

        // The generator's one planning entry, the road there: its staircase is not laid with its foot in that road.
        RoadPathfinder finder = RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(4f));
        finder.LastStairSteps.Add(step);
        var planned = RoadNetworkGenerator.PlanSearchedPath(finder, path, path[0], 0f, path[path.Count - 1], 0f, false, false, false, false, 4f, default);
        string said = string.Join(" | ", planned.Stairs.Select(st => FormattableString.Invariant(
            $"{st.FromPoint}->{st.ToPoint} noRoad {st.NoRoadAtFoot} {(RoadStairRuns.FootEnd(st.Plan, ground) is StairEnds.End e ? StairEnds.Line(e) : "-")}")));
        Assert.DoesNotContain(planned.Stairs, st => st.NoRoadAtFoot && RoadStairRuns.OntoAnotherRoad(st.Plan, ground));
        if (planned.Plans == null)
            Assert.True(planned.Stop == RoadNetworkGenerator.PlanStop.Staircase && RoadStairRuns.LastRefusal == RoadStairRuns.Refusal.OntoAnotherRoad,
                $"{planned.Stop} {RoadStairRuns.LastRefusal}: {said}");
    }

    /// <summary>Flats at 40 m (x under 24) and at 40.2 m past x = 25, a 0.2 m step between.</summary>
    private sealed class LowStep : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : 40f + Mathf.Clamp((wx - 24f) * 0.2f, 0f, 0.2f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>
    /// A stretch the search took as a staircase whose layout holds no stair (a 0.2 m step: laid as two floors, nothing else,
    /// on the road's own level ground) is no staircase: it adds nothing, and the road runs on over the stretch
    /// (RoadStairRuns.Lay), in the generator's one planning entry (PlanSearchedPath: generation, a marked manual road and an
    /// approach trial alike). In game two floors side by side, no stair, stood at a road's end below a Mistlands location and
    /// their foot's end was read not verified.
    /// </summary>
    [Fact]
    public void AStaircaseWithNoStairIsNotLaidAndTheRoadRunsOn()
    {
        var world = new LowStep();
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        var path = new List<Vector2> { new(8f, 0f), new(16f, 0f), new(24f, 0f), new(32f, 0f), new(40f, 0f), new(48f, 0f) };
        var step = (from: new Vector2(24f, 0f), to: new Vector2(32f, 0f));
        List<RoadStairSpan>? laid = RoadStairRuns.Lay(path, new[] { step }, new List<RoadCrossing>(), world);
        Assert.True(laid != null, $"not laid: {RoadStairRuns.LastRefusal}");
        Assert.Empty(laid!);

        RoadPathfinder finder = RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(4f));
        finder.LastStairSteps.Add(step);
        var planned = RoadNetworkGenerator.PlanSearchedPath(finder, path, path[0], 0f, path[path.Count - 1], 0f, false, false, false, false, 4f, default);
        Assert.True(planned.Plans != null, $"{planned.Stop} {RoadSpatialGrid.LastRefusal}");
        Assert.Empty(planned.Stairs);
        Assert.Null(RoadNetworkGenerator.BuiltRoadRefusal(planned.Plans!, planned.Crossings, out _, planned.Stairs));
    }

    /// <summary>
    /// A generated and a manual road between the same two points over a river with a location in it get the same protection
    /// verdict: both bridge the river clear of the location's footprint and clearance, by the search's own location check of the
    /// jump bank to bank (RoadLocationProtection.BlocksRoute, as for every other step and jump). The shore squeeze
    /// (RoadPathfinder.SqueezeCorridor) is another question, about the strip between a location and the water, and is off here so
    /// the location protection is seen alone. Before, the search let a bridge jump over the location (no road piece is planned on a
    /// deck, so no later check read it): the generated road was built over it, and the manual road was refused by a
    /// protection check of its own on its searched line.
    /// </summary>
    [Fact]
    public void AGeneratedAndAManualRoadBridgeARiverClearOfALocationInItAlike()
    {
        using var squeeze = StaticOverride.Keep(() => RoadPathfinder.SqueezeCorridor);
        RoadPathfinder.SqueezeCorridor = 0f;
        var world = new River();
        var location = new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 12f);
        float clearance = RoadLocationProtection.Clearance(4f);
        _scope.WithWorld(world);
        RoadLocationProtection.Set(new[] { location });
        SetPathfinder(RoadNetworkGenerator.NewFinder(world, clearance));
        bool generated = RoadNetworkGenerator.GenerateRoad(new Vector3(-100f, 0f, 0f), 0f, new Vector3(100f, 0f, 0f), 0f, 4f, "generated");
        var crossings = RoadNetworkGenerator.GetRoadCrossings().ToList();
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadLocationProtection.Set(new[] { location });
        ManualRoads.Plan? manual = null;
        string? refused = null;
        try { manual = ManualRoads.Prepare(new[] { new Vector2(-100f, 0f), new Vector2(100f, 0f) }, false, new[] { Everywhere() }); }
        catch (InvalidOperationException e) { refused = e.Message; }

        Assert.True(generated, "the generated road was not built");
        Assert.True(manual != null, refused);
        string Over(RoadCrossing c) => $"{c.Kind} {c.FromBank} -> {c.ToBank} over the location's clearance";
        bool Clear(RoadCrossing c) => !RoadLocationProtection.BlocksSegment(c.FromBank, c.ToBank, clearance, default);
        Assert.Contains(crossings, c => c.Kind == CrossingKind.Bridge);
        Assert.All(crossings, c => Assert.True(Clear(c), Over(c)));
        Assert.Contains(manual!.NewCrossings, c => c.Kind == CrossingKind.Bridge);
        Assert.All(manual.NewCrossings, c => Assert.True(Clear(c), Over(c)));
    }
}
