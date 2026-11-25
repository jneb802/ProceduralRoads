using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A later road never changes the ground under a structure another road planned (RoadNetworkGenerator.UnderAPlannedStructure,
/// docs/OWNERS.md): a bridge or staircase is planned when its road is stored but laid only when its first zone is built, and a
/// road that cut or filled under it meanwhile left it to be built on changed ground. A marked manual road whose end's levelling
/// reaches the ground an earlier road's bridge end or staircase stands on is refused for it (RoadNetworkGenerator.BuiltRoadRefusal),
/// before and after a reload; generation between the same points refuses it the same way (GenerateRoadCore), and leaves that
/// ground as it was. A road ending just beyond that reach is built. On the parent every one of these roads was built, over the
/// ground under the structure.
/// </summary>
[Collection("RoadStatics")]
public sealed class PlannedGroundKeepClearTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>Meadows at 33 m with a river 40 m wide and 11 m deep across x = 0: a road along y = 0 bridges it.</summary>
    private sealed class River : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : Mathf.Abs(wx) < 20f ? 22f : 33f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            // The river's weight ends at its banks: a weight fading out over the banks priced the cells beside the water,
            // and the road jumped them on a knight's heading, its bridge turned to 22.5 degrees and its west end no longer
            // where the later roads reach it.
            weight = Mathf.Abs(wx) < 20f ? 1f : 0f;
            width = weight > 0f ? 40f : 0f;
        }
        public override int GetSeed() => 78;
    }

    /// <summary>A face of 1.2 from x = 24 to 48 (28.8 m up, its rim on a search cell line) in the mountains: a road along y = 0 climbs it on a staircase.</summary>
    private sealed class Scarp : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : 40f + Mathf.Clamp((wx - 24f) * 1.2f, 0f, 28.8f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>One case: the world, the earlier road, a later road whose end's levelling reaches the ground under the
    /// earlier road's structure (a bridge's west end, the end 2 m north of the earlier road where it runs onto the bridge;
    /// the staircase's foot on the face), and a later road ending just beyond it. A searched path ends at its last mark
    /// (RoadPathfinder.ReconstructPath), so the bridge's mark is where the road's end is.</summary>
    private sealed record Case(Func<WorldGenerator> World, Vector2[] Earlier, Vector2[] Under, Vector2[] Beside);

    private static Case Of(string structure) => structure == "bridge"
        ? new Case(() => new River(), new[] { new Vector2(-100f, 0f), new Vector2(100f, 0f) },
            new[] { new Vector2(-26f, -80f), new Vector2(-24f, -6f) }, new[] { new Vector2(-40f, -80f), new Vector2(-40f, -10f) })
        : new Case(() => new Scarp(), new[] { new Vector2(-60f, 0f), new Vector2(120f, 0f) },
            new[] { new Vector2(10f, -80f), new Vector2(21f, -22f) }, new[] { new Vector2(10f, -80f), new Vector2(18f, -26f) });

    public PlannedGroundKeepClearTests()
    {
        RoadNetworkGenerator.Reset();
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

    private WorldGenerator Begin(Case c)
    {
        WorldGenerator world = c.World();
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        return world;
    }

    private static void Reload()
    {
        RoadNetworkGenerator.SaveGlobalRoadData();
        RoadNetworkGenerator.Reset();
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData()); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        RoadNetworkGenerator.MarkLocationsReady();
    }

    /// <summary>The earlier road's structure: its one bridge with a deck, else its one staircase, and the ground it stands on
    /// (StructureGround.Measure over the pieces it may be laid with), with the finished ground there now.</summary>
    private static Dictionary<(int, int), float> TheStructure(WorldGenerator world, string structure)
    {
        List<StructurePiece> pieces = structure == "bridge"
            ? BridgeStructures.EveryPieceItMayLay(Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), BridgeStructures.HasDeck), world).ToList()
            : Assert.Single(StairStructures.Runs).Pieces;
        var finished = StructureGround.Vertices(world);
        var ground = StructureGround.Measure(pieces, finished).ToDictionary(pin => (pin.X, pin.Z), pin => finished(pin.X, pin.Z));
        Assert.NotEmpty(ground);
        return ground;
    }

    /// <summary>
    /// A marked manual road whose end's levelling reaches the ground under an earlier road's wooden bridge end or staircase is
    /// refused for it, with nothing added, in the session that laid the structure and after a save and reload alike: the
    /// ground each structure stands on is read from the network as it stands (a reload's saved crossings and staircases
    /// included). A road ending just beyond it is planned. On the parent the refused road was planned over that ground.
    /// </summary>
    [Theory]
    [InlineData("bridge", false)]
    [InlineData("bridge", true)]
    [InlineData("staircase", false)]
    [InlineData("staircase", true)]
    public void AMarkedRoadIsKeptOffTheGroundUnderAnEarlierRoadsStructure(string structure, bool reload)
    {
        Case c = Of(structure);
        Begin(c);
        ManualRoads.Commit(ManualRoads.Prepare(c.Earlier, false, new[] { Everywhere() }));
        if (structure == "bridge") Assert.Contains(RoadNetworkGenerator.GetRoadCrossings(), BridgeStructures.HasDeck);
        else Assert.Single(StairStructures.Runs);
        if (reload) Reload();
        int appended = RoadSpatialGrid.AppendCount;

        var refused = Assert.Throws<InvalidOperationException>(() => ManualRoads.Prepare(c.Under, false, new[] { Everywhere() }));
        Assert.Contains(RoadNetworkGenerator.GroundUnderAStructure, refused.Message);
        Assert.Contains($"the {structure} (", refused.Message);
        Assert.Equal(appended, RoadSpatialGrid.AppendCount);

        var beside = ManualRoads.Prepare(c.Beside, false, new[] { Everywhere() });
        Assert.NotEmpty(beside.Paths);
    }

    /// <summary>
    /// Parity (docs/OWNERS.md, the keep-clear of the ground under the network's structures): over a bridge and a staircase an
    /// earlier generated road planned and did not lay, a marked manual road (BuiltRoadRefusal) and generation between the same
    /// two points (GenerateRoadCore, its searches round it and its final check) give the same verdict: refused, for the one
    /// reason, and the ground the structure stands on is the ground it was. The road ending just beyond it is built by both.
    /// On the parent both built the road into that ground.
    /// </summary>
    [Theory]
    [InlineData("bridge")]
    [InlineData("staircase")]
    public void GenerationAndAMarkedRoadKeepTheSameGroundClear(string structure)
    {
        Case c = Of(structure);
        WorldGenerator world = Begin(c);
        SetPathfinder(RoadNetworkGenerator.NewFinder(world, 4f));
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(c.Earlier[0].x, 0f, c.Earlier[0].y), 0f,
            new Vector3(c.Earlier[1].x, 0f, c.Earlier[1].y), 0f, 4f, "earlier"), RoadSpatialGrid.LastRefusal);
        var before = TheStructure(world, structure);

        var refused = Assert.Throws<InvalidOperationException>(() => ManualRoads.Prepare(c.Under, false, new[] { Everywhere() }));
        Assert.Contains(RoadNetworkGenerator.GroundUnderAStructure, refused.Message);
        Assert.NotEmpty(ManualRoads.Prepare(c.Beside, false, new[] { Everywhere() }).Paths);

        SetPathfinder(RoadNetworkGenerator.NewFinder(world, 4f));
        bool built = RoadNetworkGenerator.GenerateRoad(new Vector3(c.Under[0].x, 0f, c.Under[0].y), 0f,
            new Vector3(c.Under[1].x, 0f, c.Under[1].y), 0f, 4f, "later");
        Assert.False(built, "generation built the road the marked road was refused");
        Assert.Equal(RoadNetworkGenerator.GroundUnderAStructure, RoadSpatialGrid.LastRefusal);
        var finished = StructureGround.Vertices(world);
        foreach (var pin in before)
        {
            var (x, z) = pin.Key;
            float h = pin.Value;
            Assert.True(finished(x, z) == h, $"the ground under the {structure} at ({x}, {z}) moved from {h:F3} to {finished(x, z):F3}");
        }

        SetPathfinder(RoadNetworkGenerator.NewFinder(world, 4f));
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(c.Beside[0].x, 0f, c.Beside[0].y), 0f,
            new Vector3(c.Beside[1].x, 0f, c.Beside[1].y), 0f, 4f, "beside"), RoadSpatialGrid.LastRefusal);
    }
    /// <summary>Meadows level at 10 m.</summary>
    private sealed class Level : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 10f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 79;
    }

    /// <summary>
    /// A road that would write ground not known under a structure another road planned is refused, never passed as no change
    /// (RoadNetworkGenerator.UnderAPlannedStructure, in the one list): a staircase laid over a location whose ground is not read
    /// yet, and a road considered while it is not, carried on into that location over the staircase's unknown ground. It is
    /// refused for writing ground not known (RoadNetworkGenerator.UnknownGroundUnderAStructure), and said as an error naming
    /// the staircase, the location and the road: roads stop at the edge of locations not placed first, so it should never happen. A
    /// road that writes nothing there (one not into that location) is not refused. Before, the unknown vertices were left out and
    /// the road passed.
    /// </summary>
    [Fact]
    public void ARoadWritingAPlannedStructuresUnknownGroundIsRefusedWithAnError()
    {
        _scope.WithWorld(new Level());
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
        try
        {
            var location = new Vector2(0f, 0f);
            RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(location, 6f) });
            // On the preload list, its ground not published: the state the rule makes a bug.
            PlacedLocations.Preload(location);
            var run = new StairRun { From = new Vector2(-1f, -1f), To = new Vector2(-1f, 1f), Width = RoadStairRuns.Width, Kit = StairKit.Stone };
            run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = StairKit.Stone.Stair, Position = new Vector3(-1f, 10f, 0f), YawDegrees = 180f });
            StairStructures.Add(run);
            RoadNetworkGenerator.ManualAppend Into(bool intoLocation)
            {
                var points = new List<Vector2> { new(12f, 0f), new(8f, 0f), new(4f, 0f), new(0f, 0f) };
                var path = new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => 10f), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 12f, false)
                    { Locations = new RoadLocationProtection.Targets(null, intoLocation ? location : null) };
                var written = new RoadNetworkGenerator.ManualAppend();
                written.IntoLocation.Add(path);
                return written;
            }
            var none = new List<RoadCrossing>();
            var noStairs = new List<RoadStairSpan>();

            RoadNetworkGenerator.ManualAppend into = Into(intoLocation: true);
            var lines = new List<string>();
            BepInEx.Logging.ManualLogSource.Captured = lines;
            string? refused;
            string reason;
            try { refused = RoadNetworkGenerator.UnderAPlannedStructure(into, none, noStairs, out _, out reason, null, "(12.0, 0.0) -> (0.0, 0.0)"); }
            finally { BepInEx.Logging.ManualLogSource.Captured = null; }
            Assert.Equal(RoadNetworkGenerator.UnknownGroundUnderAStructure, reason);
            Assert.True(refused != null && refused.Contains("staircase") && refused.Contains("location at (0.0, 0.0)"), refused ?? "not refused");
            // The test log double captures the message, not its level (LogError in the source).
            Assert.True(lines.Exists(l => l.Contains("ground not known") && l.Contains("staircase")
                && l.Contains("(0.0, 0.0)") && l.Contains("(12.0, 0.0) -> (0.0, 0.0)")), string.Join(" | ", lines));

            RoadNetworkGenerator.ManualAppend notInto = Into(intoLocation: false);
            Assert.Null(RoadNetworkGenerator.UnderAPlannedStructure(notInto, none, noStairs, out _));
        }
        finally
        {
            RoadLocationProtection.Reset();
            PlacedLocations.Reset();
            StairStructures.Reset();
            RoadNetworkGenerator.Reset();
            RoadSpatialGrid.Clear();
        }
    }
}
