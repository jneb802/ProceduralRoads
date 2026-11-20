using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using static ProceduralRoads.Tests.StructureEndTests;

namespace ProceduralRoads.Tests;

/// <summary>
/// Ground not known is never a success, for a bridge or a viaduct as for a staircase: where a crossing's solve reads the
/// finished ground inside a location placed after its roads whose ground is not read yet (NaN, <see cref="StructureGround"/>)
/// under a piece it would lay (<see cref="StructureGround.FirstOnUnknownGround"/>), it lays and saves nothing, said as an
/// error (<see cref="StructureGround.NotLaid"/>: a bug under the preload-list rule), never a column or a pier stacked down
/// towards NaN without end. Nothing waits for that ground: published later (<see cref="PlacedLocations.Preload"/>) it builds
/// nothing by itself; the crossing's next build reads it as any build does.
/// </summary>
public sealed class BridgeUnknownGroundTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    // The fixtures here are kits' structure cases whose water only raises a deck (standing it just under the deck, over ground
    // above the sea) or lies under a valley deepened past the sea: none is water a ship is meant to sail, so no lane off a
    // river here (BridgeLayout.DeepWaterLanes; ShipGapTests has the lanes, river or not).
    public BridgeUnknownGroundTests()
    {
        BridgeLayout.DeepWaterLanes = false;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Reset();
        PlacedLocations.Reset();
        BuiltBridges.Reset();
    }

    public void Dispose()
    {
        BridgeLayout.DeepWaterLanes = true;
        PlacedLocations.Reset();
        BuiltBridges.Reset();
        RoadLocationProtection.Reset();
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>A railed bridge 12 m along x over a flat bed at 28.9 m, banks at the deck's height (34.9 m) past its ends.</summary>
    private sealed class FlatBedWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) < 6f ? 28.9f : 34.9f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 76;
    }

    /// <summary>A Mistlands valley along z, its far rim a metre lower: a viaduct's.</summary>
    private sealed class Valley : WorldGenerator
    {
        public readonly RimValley Rim = new() { Far = _ => -1f };
        public override float GetHeight(float wx, float wy) => Rim.GetHeight(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 78;
    }

    /// <summary>A crossing put in the network's records, as generation stores it.</summary>
    private static void Store(RoadCrossing crossing)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        object gate = typeof(RoadNetworkGenerator).GetField("m_recordGate", flags)!.GetValue(null)!;
        var records = (List<RoadCrossing>)typeof(RoadNetworkGenerator).GetField("m_roadCrossings", flags)!.GetValue(null)!;
        lock (gate) records.Add(crossing);
    }

    /// <summary>
    /// The case for one crossing: solved on ground all known; then a location placed after its roads over
    /// <paramref name="centre"/> (<paramref name="radius"/> round it), its ground not read: the crossing's solve finishes,
    /// and the crossing lays and saves nothing, said as an error; the kit's own solve lays nothing over NaN.
    /// </summary>
    private static void IsNotLaid(RoadCrossing crossing, WorldGenerator world, Vector2 centre, float radius,
        Func<List<StructurePiece>> kitSolve)
    {
        int seed = world.GetSeed();
        List<string> known = BridgeStructures.Solve(crossing, world, seed).Select(StructureAppendQueue.Key).ToList();
        Assert.NotEmpty(known);
        Store(crossing);

        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(centre, radius) });
        Assert.True(float.IsNaN(StructureGround.At(world)(centre)));
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try { Assert.Empty(BridgeStructures.Build(crossing, world, seed)); }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
        Assert.Null(BuiltBridges.Of(crossing));
        Assert.True(lines.Exists(l => l.Contains("not laid") && l.Contains("not known")), string.Join(" | ", lines));
        Assert.Empty(BridgeStructures.PiecesOf(crossing, world, seed));
        // Fail closed in the kit itself: no piece over or towards NaN, whatever reads it.
        Assert.All(kitSolve(), p => Assert.False(float.IsNaN(p.Position.y), $"{p.Prefab} at {p.Position}"));
        Assert.Null(BuiltBridges.Of(crossing));
    }

    [Fact]
    public void ARailedBridgeOverALocationsGroundNotKnownIsNotLaidAndSaysSo()
    {
        var world = new FlatBedWorld();
        _scope.WithWorld(world);
        var crossing = RoadCrossing.Between(new Vector2(-6f, 0f), new Vector2(6f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        crossing.WaterLevel = 34.9f - BridgeLayout.DeckFreeboard;
        Assert.NotNull(BridgeStructures.RailedKitOf(BridgeStructures.KitFor(crossing, world)));
        // Its middle column stands at x 0 on known ground.
        Assert.Contains(BridgeLayout.SolveRailedComplete(crossing, world, 1, BridgeLayout.Rail),
            p => p.Prefab == BridgeLayout.PostPrefab && Mathf.Abs(p.Position.x) < 0.01f);
        IsNotLaid(crossing, world, new Vector2(0f, 0f), 3f, () => BridgeLayout.SolveRailed(crossing, world, 1, BridgeLayout.Rail));
    }

    [Fact]
    public void AViaductOverALocationsGroundNotKnownIsNotLaidAndSaysSo()
    {
        var world = new Valley();
        _scope.WithWorld(world);
        float bank = world.Rim.Rim + 1f;
        RoadCrossing crossing = DvergrViaduct.ValleyCrossing(new Vector2(-bank, 0f), new Vector2(bank, 0f), world, out string why)!;
        Assert.True(crossing != null, why);
        Assert.Equal(BridgeStructures.Kit.DvergrViaduct, BridgeStructures.KitFor(crossing!, world));
        // Its piers stand on the valley's floor round x 0.
        Assert.Contains(DvergrViaduct.Solve(crossing!, world, 1), p => p.Kind == StructurePieceKind.ViaductPier && Mathf.Abs(p.Position.x) < 6f);
        IsNotLaid(crossing!, world, new Vector2(0f, 0f), 6f, () => DvergrViaduct.Solve(crossing!, world, 1));
    }

    /// <summary>
    /// The ground under a structure another road planned is kept clear on what its pieces stand on once all of it is known
    /// (RoadNetworkGenerator.UnderAPlannedStructure): a stone bridge (the stone kit's reference crossing, its kit saved)
    /// whose pier at x 3 stands in a location's ground not read yet is measured without that pier (its kit lays nothing towards
    /// ground not known), so a road filling a metre over the bed beside the pier's foot changes nothing it protects; once the
    /// location's ground is read the pier is measured, and the same road is refused for the ground under it. On the parent the
    /// first measure was kept for good, and the pier's ground was never protected.
    /// </summary>
    [Fact]
    public void AStructureMeasuredOnGroundNotKnownIsMeasuredAgainOnceItIsRead()
    {
        var world = new StoneBridgeTests.ReferenceWorld();
        _scope.WithWorld(world);
        var crossing = RoadCrossing.Between(new Vector2(-10f, 0f), new Vector2(10f, 0f), 28f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        crossing.WaterLevel = 34.83f - BridgeLayout.DeckFreeboard;
        crossing.Kit = BridgeStructures.Kit.Stone;
        Assert.Contains(BridgeStructures.EveryPieceItMayLay(crossing, world), p => p.Kind == StructurePieceKind.Post && Mathf.Abs(p.Position.x - 3f) < 0.6f);
        Store(crossing);
        var centre = new Vector2(3f, 0f);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(centre, 0.5f) });
        Assert.True(float.IsNaN(StructureGround.Vertices(world)(3, 0)));
        // A road 2 m wide along z at x 3, south of the bridge, a metre over the bed, ending 4 m short of its middle.
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (float z = -12f; z <= -4f; z += 1f) { points.Add(new Vector2(3f, z)); heights.Add(29f); }
        var plans = new List<RoadSpatialGrid.PlannedPath> { new(points, heights, new List<RoadPointDebugInfo>(), 2f, 8f, false) };
        string? beside = RoadNetworkGenerator.BuiltRoadRefusal(plans, new List<RoadCrossing>(), out Vector2 first);
        Assert.True(beside == null || !beside.StartsWith(RoadNetworkGenerator.GroundUnderAStructure), $"{beside} at {first}");

        PlacedLocations.Preload(centre, (x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));
        string? refused = RoadNetworkGenerator.BuiltRoadRefusal(plans, new List<RoadCrossing>(), out Vector2 at);
        Assert.True(refused != null && refused.StartsWith(RoadNetworkGenerator.GroundUnderAStructure), refused ?? "not refused");
        Assert.True(Mathf.Abs(at.x - 3f) <= 2f, $"refused at {at}, not beside the pier at x 3");
    }
}
