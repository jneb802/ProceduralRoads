using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Vegetation is cleared only where it stands in walking space: a road's surface (a waded ford's too), a staircase's walk.
/// A staircase's or crossing's cut lowers the ground but is not walked on, so it clears nothing, and a point where two
/// walks meet keeps each one's width and height whichever was stored first. Every case is judged through the clear areas
/// and road points RoadClearAreaManager builds, by generation (RoadClearAreaManager.OnRoad) and by the server bake
/// (VegetationClearing.Decide), which must agree.
/// </summary>
public sealed class VegetationWalkingSpaceTests : IDisposable
{
    private const string Bush = "Bush01_0";
    private const float RoadWidth = RoadConstants.DefaultRoadWidth;
    private const float CutWidth = 12f;
    private static readonly Vector2 Centre = new(0f, 0f);
    // A point-sized shrub off a 4 m road but inside a 12 m cut's width.
    private static readonly Vector3 Shrub = new(3f, 34f, 0f);
    private readonly ValheimWorldScope scope = new();

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 34f;
    }

    /// <summary>A narrow, knee-deep channel with walkable 12 m ramps, as the waded crossings' fixtures.</summary>
    private sealed class WadeWorld : WorldGenerator
    {
        private const float HalfWidth = 12f;
        public override float GetHeight(float x, float z)
        {
            if (Mathf.Abs(x) > 100f || Mathf.Abs(z) > 100f)
                return 20f;
            return Mathf.Lerp(RoadConstants.SeaLevel - 0.5f, 33f, Mathf.Clamp01((Mathf.Abs(x) - HalfWidth) / 12f));
        }

        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            GetHeight(x, z) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Heightmap.Biome.Meadows;

        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = Mathf.Clamp01(1f - Mathf.Abs(x) / (HalfWidth * 2f));
            width = weight > 0f ? HalfWidth * 4f : 0f;
        }
    }

    public VegetationWalkingSpaceTests()
    {
        scope.WithWorld(new FlatWorld());
        RoadNetworkGenerator.Reset();
        RoadClearAreaManager.ClearCache();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadClearAreaManager.ClearCache();
        scope.Dispose();
    }

    /// <summary>A cut alone, a cut stored before or after the road through it, or a cut appended after the road: the
    /// shrub beside the road but over the cut stays, and no road point the judge reads carries the cut's width.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void ACutClearsNoVegetation(bool road, bool cutFirst, bool appended)
    {
        var cut = new Vector3(Centre.x, 31f, Centre.y);
        if (road && cutFirst)
            RoadSpatialGrid.CommitCuts(new[] { cut }, CutWidth);
        if (road)
            Assert.True(RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(0f, -8f), new(0f, 8f) }, RoadWidth, WorldGenerator.instance));
        if (!cutFirst && !appended)
            RoadSpatialGrid.CommitCuts(new[] { cut }, CutWidth);
        RoadSpatialGrid.FinalizeRoadNetwork();
        if (appended)
            RoadSpatialGrid.CommitAppendCuts(new[] { cut }, CutWidth, RoadSpatialGrid.RoadNetworkVersion);
        var stored = RoadSpatialGrid.GetRoadPointsInZone(ZoneSystem.GetZone(Shrub)).Where(p => p.p == Centre).ToArray();
        Assert.Contains(stored, p => p.cutOnly);
        Assert.Equal(road, stored.Any(p => !p.cutOnly));

        // The bake owes an appended cut's zone only the append's points.
        int since = appended ? 0 : -1;
        var (verdict, centres) = Judge(Shrub, since);
        Assert.Equal(VegetationClearing.Lying.Clear, verdict);
        Assert.All(centres, c => Assert.Equal(RoadWidth * 0.5f, c.HalfWidth));
        if (road && !appended)
            Assert.Equal(34f, Assert.Single(centres, c => c.Centre == Centre).Height);
    }

    /// <summary>A mound under a pier (StructureGround.Mound) is ground, not walking space: its fill gives no clearing
    /// permission. Stored as generation stores it (RoadNetworkGenerator.CrossingGround.CommitGround) or as a manual road's
    /// append does (RoadNetworkGenerator.Footprint's WithFills, RoadSpatialGrid.CommitAppend), the shrub on it stays: no
    /// clear area and no road centre come from it, and it owes no zone clearing. Before, its points were walked road.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMoundClearsNoVegetation(bool manual)
    {
        const float top = 34.5f;
        var ground = new RoadNetworkGenerator.CrossingGround();
        ground.Mounds.Add(new StructureGround.Mound(new List<Vector2> { new(2f, 0f), new(Shrub.x, Shrub.z), new(4f, 0f) }, top));
        if (manual)
        {
            RoadSpatialGrid.FinalizeRoadNetwork();
            var append = RoadNetworkGenerator.Footprint(new List<RoadSpatialGrid.PlannedPath>(), Array.Empty<RoadStairSpan>(), null, ground,
                Enumerable.Empty<RoadSpatialGrid.PlannedPath>(), Enumerable.Empty<RoadSpatialGrid.PlannedPath>());
            RoadSpatialGrid.CommitAppend(append.WithFills, RoadSpatialGrid.RoadNetworkVersion);
        }
        else
        {
            ground.CommitGround();
            RoadSpatialGrid.FinalizeRoadNetwork();
        }
        var zone = ZoneSystem.GetZone(Shrub);
        var (verdict, centres) = Judge(new Vector3(Shrub.x, top, Shrub.z), manual ? 0 : -1);
        Assert.Equal(VegetationClearing.Lying.Clear, verdict);
        Assert.Empty(centres);
        Assert.Empty(manual ? RoadClearAreaManager.ClearAreasSince(zone, 0) : RoadClearAreaManager.GetOrCreateClearAreas(zone));
        Assert.Empty(RoadClearAreaManager.ZonesReached(manual ? 0 : -1));
        var stored = RoadSpatialGrid.GetRoadPointsInZone(zone).Where(p => p.p == new Vector2(Shrub.x, Shrub.z)).ToArray();
        Assert.NotEmpty(stored);
        Assert.All(stored, p => Assert.False(p.IsRoad, $"the mound at {p.p} is stored as road"));
    }

    /// <summary>A narrow road and a wider unpainted walk (a structure's end) through one point: the shrub within the wider
    /// one's clear area goes whichever was stored first, and the judge reads both walks.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoWalksThroughOnePointKeepEachOnesWidthAndHeight(bool wideFirst)
    {
        const float wide = 2f * RoadWidth;
        void Narrow() => RoadSpatialGrid.CommitLevelled(new[] { Centre }, new[] { 34f }, RoadWidth);
        void Wide() => RoadSpatialGrid.CommitLevelled(new[] { Centre }, new[] { 34.5f }, wide, unpainted: true);
        if (wideFirst) Wide();
        Narrow();
        if (!wideFirst) Wide();
        RoadSpatialGrid.FinalizeRoadNetwork();

        var (verdict, centres) = Judge(Shrub);
        Assert.Equal(VegetationClearing.Lying.Remove, verdict);
        var atCentre = centres.Where(c => c.Centre == Centre).Select(c => (c.HalfWidth, c.Height)).OrderBy(c => c.HalfWidth).ToArray();
        Assert.Equal(new[] { (RoadWidth * 0.5f, 34f), (wide * 0.5f, 34.5f) }, atCentre);
    }

    /// <summary>A waded ford's points are paint only, yet walked: vegetation on them still goes.</summary>
    [Fact]
    public void AWadedFordIsWalkedAndClearedAsARoad()
    {
        var world = new WadeWorld();
        using var wadeScope = new ValheimWorldScope().WithWorld(world);
        var pathfinderField = typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? previousPathfinder = pathfinderField.GetValue(null);
        var weights = (RoadCrossingDetector.ConfiguredWadeWeight, RoadCrossingDetector.ConfiguredRaiseWeight,
            RoadCrossingDetector.ConfiguredSpanWeight);
        pathfinderField.SetValue(null, new RoadPathfinder(world) { Fords = true, Bridges = false });
        RoadCrossingDetector.SetFordStyleWeights(1f, 0f, 0f);
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, RoadWidth, "wade"));
            RoadCrossing crossing = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
            Assert.Equal(CrossingKind.Ford, crossing.Kind);
            Assert.Equal(FordStyle.Wade, crossing.Style);
            RoadSpatialGrid.FinalizeRoadNetwork();
            var walkingPoints = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(crossing.Center.x, 0f, crossing.Center.y), 3f);
            Assert.NotEmpty(walkingPoints);
            Assert.All(walkingPoints, p => Assert.True(p.paintOnly));
            var point = walkingPoints[0];
            Assert.Equal(VegetationClearing.Lying.Remove, Judge(new Vector3(point.p.x, point.h, point.p.y)).Verdict);
        }
        finally
        {
            pathfinderField.SetValue(null, previousPathfinder);
            RoadCrossingDetector.SetFordStyleWeights(weights.Item1, weights.Item2, weights.Item3);
        }
    }

    /// <summary>A waded ford's walk is walking space for rock as for vegetation (RoadSpatialGrid.RoadPoint.Walked, the one
    /// test): a rock a metre over the riverbed in its walk is in a clearance box (RockClearances.Near, which the probe, the
    /// carve and the bake all read), standing on the bed at a road's headroom (RockClearances.Bottom, RockClearances.Top),
    /// and a shrub there goes; a rock a metre past the box's edge beside it is in none. Before, the walk's paint-only points
    /// gave no box (RockClearances.Near asked RoadSpatialGrid.RoadPoint.IsRoad) while its vegetation went.</summary>
    [Fact]
    public void AWadedFordsWalkIsClearedOfRockAsOfVegetation()
    {
        var world = new WadeWorld();
        using var wadeScope = new ValheimWorldScope().WithWorld(world);
        var pathfinderField = typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? previousPathfinder = pathfinderField.GetValue(null);
        var weights = (RoadCrossingDetector.ConfiguredWadeWeight, RoadCrossingDetector.ConfiguredRaiseWeight,
            RoadCrossingDetector.ConfiguredSpanWeight);
        pathfinderField.SetValue(null, new RoadPathfinder(world) { Fords = true, Bridges = false });
        RoadCrossingDetector.SetFordStyleWeights(1f, 0f, 0f);
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, RoadWidth, "wade"));
            RoadCrossing crossing = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
            Assert.Equal(FordStyle.Wade, crossing.Style);
            RoadSpatialGrid.FinalizeRoadNetwork();
            var walk = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(crossing.Center.x, 0f, crossing.Center.y), 3f);
            Assert.NotEmpty(walk);
            Assert.All(walk, p => Assert.True(p.paintOnly));
            RoadSpatialGrid.RoadPoint point = walk[0];
            // The ground the carve reads there: the riverbed as the writer leaves it (a waded ford levels nothing).
            bool Bed(Vector3 at, out float height) { height = world.GetHeight(at.x, at.z); return true; }
            float bed = world.GetHeight(point.p.x, point.p.y);
            Assert.True(bed < RoadConstants.SeaLevel, "the ford's walk is under water");

            var rock = new Vector3(point.p.x, bed + 1f, point.p.y);
            List<RockClearance> boxes = RockClearances.Near(rock, 4f, Bed, out int misses);
            Assert.Equal(0, misses);
            RockClearance box = Assert.Single(boxes, b => Inside(b, rock) && Mathf.Abs(b.Centre.x - point.p.x) < 1e-4f && Mathf.Abs(b.Centre.z - point.p.y) < 1e-4f);
            Assert.Equal(RockClearances.Bottom(bed), box.Centre.y - box.Half.y, 3);
            Assert.Equal(RockClearances.Top(bed), box.Top, 3);
            Assert.False(box.WholeOnly);
            Assert.Equal(VegetationClearing.Lying.Remove, Judge(new Vector3(point.p.x, point.h, point.p.y)).Verdict);

            // Beside the walk, a metre past the box's edge across the road (the road runs along x here).
            var beside = new Vector3(point.p.x, bed + 1f, point.p.y + RoadProfile.PaintEdge(point.w) + 1f);
            Assert.DoesNotContain(RockClearances.Near(beside, 4f, Bed, out _), b => Inside(b, beside));
        }
        finally
        {
            pathfinderField.SetValue(null, previousPathfinder);
            RoadCrossingDetector.SetFordStyleWeights(weights.Item1, weights.Item2, weights.Item3);
        }
    }

    private static bool Inside(RockClearance box, Vector3 p)
    {
        Vector3 d = Quaternion.Inverse(box.Turn) * (p - box.Centre);
        return Mathf.Abs(d.x) <= box.Half.x + 1e-4f && Mathf.Abs(d.y) <= box.Half.y + 1e-4f && Mathf.Abs(d.z) <= box.Half.z + 1e-4f;
    }

    /// <summary>The shrub at <paramref name="point"/> judged as generation and the bake judge it, with the clear areas and road
    /// points RoadClearAreaManager builds for its zone (those after <paramref name="since"/> appends, as the bake asks for
    /// an append; -1: every road's). The three verdicts agree; returns the shared one and the road points read.</summary>
    private static (VegetationClearing.Lying Verdict, List<VegetationClearing.RoadCentre> Centres) Judge(Vector3 point, int since = -1)
    {
        var zone = ZoneSystem.GetZone(point);
        var areas = RoadClearAreaManager.AddRoadAreas(zone, new List<ZoneSystem.ClearArea>(), since);
        var centres = since >= 0 ? RoadClearAreaManager.RoadCentresSince(zone, since) : RoadClearAreaManager.GetOrCreateRoadCentres(zone);
        int prefab = Bush.GetStableHashCode();
        Func<Vector3, Heightmap.Biome> meadows = _ => Heightmap.Biome.Meadows;
        var judged = VegetationClearing.Judge(prefab, point, 1f, 0f, null, areas, centres, meadows, out _);
        float yaw = 0f;
        bool notPlaced = RoadClearAreaManager.OnRoad(areas, centres, point, Bush, 1f, meadows, ref yaw);
        var baked = VegetationClearing.Decide(prefab, new Vector2(point.x, point.z), false, new HashSet<int> { prefab }, areas,
            new List<VegetationClearing.Footprint>(), new List<VegetationClearing.Footprint>(), centres, 1f, meadows, 0f, out _);
        Assert.Equal(judged, baked);
        Assert.Equal(judged == VegetationClearing.Lying.Remove, notPlaced);
        return (judged, centres);
    }
    // ---- the spawn and bake adapters' seams (RoadClearAreaManager.ForGeneration, ForBake) ----

    /// <summary>
    /// What generation hands PlaceVegetation for a zone (ZoneSystem_PlaceVegetation_Patch's prefix, through
    /// RoadClearAreaManager.ForGeneration) and what the server bake judges the zone's saved vegetation by (its pass and
    /// road_bake, through RoadClearAreaManager.ForBake) are the same clear areas, walks and road centres, the game's own
    /// first (a placed location's clear area, which the game adds as it places it and the bake reads off the placement),
    /// so each object gets one verdict (RoadClearAreaManager.OnRoad, VegetationClearing.Decide). The patch and the bake
    /// need Harmony's and the game's runtime and are not compiled here: these are the seams they call.
    /// </summary>
    [Fact]
    public void GenerationAndTheBakeReadAZonesClearingAlike()
    {
        scope.WithZoneSystem();
        Assert.True(RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-40f, 0f), new(40f, 0f) }, RoadWidth, WorldGenerator.instance));
        RoadSpatialGrid.FinalizeRoadNetwork();
        var zone = new Vector2s(0, 0);
        var location = new Vector3(10f, 34f, 20f);
        ZoneSystem.instance!.m_locationInstances[zone] = new ZoneSystem.LocationInstance
        {
            m_position = location, m_placed = true,
            m_location = new ZoneSystem.ZoneLocation { m_clearArea = true, m_exteriorRadius = 6f },
        };
        // The game's own list as PlaceLocations leaves it for generation: the placed location's square.
        var game = new List<ZoneSystem.ClearArea> { new(location, 6f) };
        var generation = RoadClearAreaManager.ForGeneration(zone, game, judged: true, out var generationRoad, out int clearedFor);
        var bake = RoadClearAreaManager.ForBake(zone, -1, out var bakeRoad);
        Assert.Equal(RoadSpatialGrid.RoadNetworkVersion, clearedFor);
        Assert.Equal(generation.FirstRoad, bake.FirstRoad);
        Assert.Equal(generation.FirstStructure, bake.FirstStructure);
        Assert.Equal(generation.All.Select(a => (a.m_center, a.m_radius)), bake.All.Select(a => (a.m_center, a.m_radius)));
        Assert.Equal(generation.Walks.Count, bake.Walks.Count);
        Assert.Equal(generationRoad.Select(c => (c.Centre, c.HalfWidth, c.Height)), bakeRoad.Select(c => (c.Centre, c.HalfWidth, c.Height)));
        Func<Vector3, Heightmap.Biome> meadows = _ => Heightmap.Biome.Meadows;
        foreach (var (prefab, at) in new[] { (Bush, new Vector2(0f, 1f)), ("Beech1", new Vector2(5f, 3f)), ("Beech1", new Vector2(20f, 12f)),
                     (Bush, new Vector2(10f, 20f)), ("FirTree_oldLog", new Vector2(15f, 4f)) })
        {
            float yaw = 0f;
            bool generated = RoadClearAreaManager.OnRoad(generation, generationRoad, new Vector3(at.x, 34f, at.y), prefab, 1f, meadows, ref yaw);
            int hash = prefab.GetStableHashCode();
            bool baked = VegetationClearing.Decide(hash, at, false, new HashSet<int> { hash }, bake, new List<VegetationClearing.Footprint>(),
                new List<VegetationClearing.Footprint>(), bakeRoad, 1f, meadows, 0f, out _) == VegetationClearing.Lying.Remove;
            // The game keeps its own clear area free at generation; the bake keeps it as ground, never clearing it.
            if (VegetationClearing.InGameArea(generation, at)) { Assert.False(baked, $"{prefab} at {at}"); continue; }
            Assert.Equal(generated, baked);
        }
    }

    /// <summary>
    /// A zone the network protects (NetworkCutoff.Excludes) is owed no clearing at its generation, as the bake owes it none
    /// (ServerBakePlanner.VegetationOwed, the one rule): generation hands PlaceVegetation only the game's own clear areas and
    /// no road to test its trees against, and does not record it cleared; the bake reads it as protected. The next zone, the
    /// network's, is cleared at its generation as before. Before, generation cleared a protected zone by the road through it.
    /// </summary>
    [Fact]
    public void AZoneTheNetworkProtectsIsClearedNeitherAtItsGenerationNorByTheBake()
    {
        scope.WithZoneSystem();
        Assert.True(RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-40f, 0f), new(100f, 0f) }, RoadWidth, WorldGenerator.instance));
        RoadSpatialGrid.FinalizeRoadNetwork();
        Vector2s kept = new(0, 0), open = new(1, 0);
        NetworkCutoff.Forget();
        try
        {
            NetworkCutoff.SnapshotWorld(new[] { kept });
            NetworkCutoff.UseLoadedNetwork(RoadSpatialGrid.RoadNetworkVersion, null);
            Assert.True(NetworkCutoff.Excludes(kept));
            var game = new List<ZoneSystem.ClearArea> { new(new Vector3(5f, 34f, 20f), 4f) };
            VegetationClearing.ZoneAreas areas = RoadClearAreaManager.ForGeneration(kept, game, judged: true, out var road, out int clearedFor);
            Assert.Empty(road);
            Assert.False(areas.AnyRoadOrStructure, "a protected zone was handed the roads' clear areas");
            Assert.Equal(game.Select(a => (a.m_center, a.m_radius)), areas.All.Select(a => (a.m_center, a.m_radius)));
            Assert.Equal(0, clearedFor);
            Assert.Equal(ServerBakePlanner.Clearing.Protected, ServerBakePlanner.AssessClearing(false, true,
                VegetationClearing.IsCleared(kept, RoadSpatialGrid.RoadNetworkVersion), NetworkCutoff.Excludes(kept), () => true, () => false));
            RoadClearAreaManager.ForGeneration(open, new List<ZoneSystem.ClearArea>(), judged: true, out var openRoad, out int openCleared);
            Assert.NotEmpty(openRoad);
            Assert.Equal(RoadSpatialGrid.RoadNetworkVersion, openCleared);
        }
        finally { NetworkCutoff.Forget(); }
    }

    /// <summary>
    /// A zone generated without the road's own test (the patch's transpiler did not apply: ForGeneration's judged is false)
    /// is not recorded cleared, so the server bake owes it its clearing, by the same rule, as for a zone generated before
    /// its roads. Before, the prefix recorded it cleared whatever the transpiler did, and nothing off a bridge's walk, no
    /// trunk and no log reaching the road was ever cleared there.
    /// </summary>
    [Fact]
    public void AZoneGeneratedWithoutTheRoadsTestIsLeftToTheBake()
    {
        scope.WithZoneSystem();
        Assert.True(RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-40f, 0f), new(40f, 0f) }, RoadWidth, WorldGenerator.instance));
        RoadSpatialGrid.FinalizeRoadNetwork();
        var zone = new Vector2s(0, 0);
        RoadClearAreaManager.ForGeneration(zone, new List<ZoneSystem.ClearArea>(), judged: false, out var road, out int clearedFor);
        Assert.NotEmpty(road);
        Assert.Equal(0, clearedFor);
        Assert.False(VegetationClearing.IsCleared(zone, RoadSpatialGrid.RoadNetworkVersion));
        VegetationClearing.ZoneAreas areas = RoadClearAreaManager.ForBake(zone, VegetationClearing.OwedSince(zone), out var owedRoad);
        Assert.Equal(ServerBakePlanner.Clearing.Owed, ServerBakePlanner.AssessClearing(false, true,
            VegetationClearing.IsCleared(zone, RoadSpatialGrid.RoadNetworkVersion), false,
            () => areas.AnyRoadOrStructure || owedRoad.Count > 0, () => false));
    }
}
