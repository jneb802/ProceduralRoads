using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// One rule for where a saved or built thing may stand (<see cref="RoadConstants.InWorld"/>), and one verdict over a whole
/// structure (<see cref="StructureSites.SpatialFault"/>): a crossing and its built bridge, a staircase's layout and its built
/// pieces, and the network's road points are read and built by the same rule, so nothing is built and saved that a load
/// refuses. The coordinates near the edge are test points, not limits of their own.
/// </summary>
public sealed class StructureSpatialValidityTests : IDisposable
{
    public StructureSpatialValidityTests() => RoadNetworkGenerator.Reset();

    public void Dispose() => RoadNetworkGenerator.Reset();

    /// <summary>A crossing and its built bridge, with the bank, the end and the piece at <paramref name="x"/>: the crossing
    /// reader, the built bridge reader and the build (<see cref="BuiltSites{T}.Keep"/>) give one verdict.</summary>
    [Theory]
    [InlineData(128f, true)]
    [InlineData(9999f, true)]
    [InlineData(10000f, false)]
    [InlineData(10001f, false)]
    [InlineData(10500f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void ACrossingAndItsBuiltBridgeAreReadAndBuiltByOneRule(float x, bool inWorld)
    {
        var from = new Vector2(x - 24f, 0f);
        var to = new Vector2(x, 0f);
        RoadCrossing crossing = Crossing(from, to, toEdge: to);
        StructurePiece piece = Piece(x, StructurePieceKind.Deck);
        byte[] saved = SavedSite(from, to, BridgeLayout.LayoutVersion, piece.Position);

        Assert.Equal(inWorld, ReadsCrossing(crossing));
        Assert.Equal(inWorld, Reads(() => BuiltBridges.Parse(saved)));
        Assert.Equal(inWorld, BuiltBridges.Store.Keep(from, to, 0, new[] { piece }, Array.Empty<GroundPin>()) != null);
        if (!inWorld) { Assert.Empty(BuiltBridges.Sites); return; }
        // The record written here by hand is the one the build saves.
        Assert.Equal(saved, BuiltBridges.Serialize());
        BuiltBridge read = Assert.Single(BuiltBridges.Parse(saved));
        Assert.True(BuiltBridges.IsOneOf(crossing, new[] { read }));
    }

    /// <summary>A bridge whose banks are in the world but one piece, or the ground one piece holds, is not: the build keeps
    /// none of it and a saved record of it is refused whole. The crossing is a record of its own and reads.</summary>
    [Fact]
    public void ABridgeWithAPieceOrItsGroundOutsideTheWorldIsRefusedWhole()
    {
        var from = new Vector2(9970f, 0f);
        var to = new Vector2(9994f, 0f);
        Assert.True(ReadsCrossing(Crossing(from, to, toEdge: to)));

        StructurePiece outside = Piece(10001f, StructurePieceKind.Deck);
        Assert.Null(BuiltBridges.Store.Keep(from, to, 0, new[] { Piece(9980f, StructurePieceKind.Deck), outside }, Array.Empty<GroundPin>()));
        Assert.False(Reads(() => BuiltBridges.Parse(SavedSite(from, to, BridgeLayout.LayoutVersion, outside.Position))));

        StructurePiece inside = Piece(9998f, StructurePieceKind.Deck);
        var pin = new GroundPin(10000, 0, 34f);
        Assert.Null(BuiltBridges.Store.Keep(from, to, 0, new[] { inside }, new[] { pin }));
        Assert.False(Reads(() => BuiltBridges.Parse(SavedSite(from, to, BridgeLayout.LayoutVersion, inside.Position, pin))));

        Assert.Empty(BuiltBridges.Sites);
        Assert.Null(StructureGround.Held(new Vector2(pin.X, pin.Z)));
    }

    /// <summary>A crossing whose banks are in the world and whose end steps off past the edge is refused: its end is one of
    /// its points.</summary>
    [Fact]
    public void ACrossingWithAnEndOutsideTheWorldIsRefused()
    {
        var from = new Vector2(9975f, 0f);
        var to = new Vector2(9999f, 0f);
        Assert.True(ReadsCrossing(Crossing(from, to, toEdge: to)));
        Assert.False(ReadsCrossing(Crossing(from, to, toEdge: new Vector2(10010f, 0f))));
    }

    /// <summary>A staircase with its ends, or one of its pieces, at <paramref name="x"/>: the layout reader, the built
    /// staircase reader and the build give one verdict.</summary>
    [Theory]
    [InlineData(128f, true)]
    [InlineData(9999f, true)]
    [InlineData(10000f, false)]
    [InlineData(10001f, false)]
    [InlineData(10500f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void AStaircaseIsReadAndBuiltByOneRule(float x, bool inWorld)
    {
        // Its ends at x, its piece and its meeting points in the world.
        var ends = new StairRun { From = new Vector2(x - 4f, 0f), To = new Vector2(x, 0f), Width = 4f, FromMeet = new Vector2(100f, 0f), ToMeet = new Vector2(104f, 0f) };
        ends.Pieces.Add(Piece(102f, StructurePieceKind.StairSupport));
        // Its piece at x, its ends in the world.
        var piece = new StairRun { From = new Vector2(100f, 0f), To = new Vector2(104f, 0f), Width = 4f };
        piece.Pieces.Add(Piece(x, StructurePieceKind.StairSupport));

        foreach (StairRun run in new[] { ends, piece })
        {
            RoadNetworkGenerator.Reset();
            StairStructures.Add(run);
            byte[] layout = StairStructures.Serialize();
            byte[] built = SavedSite(run.From, run.To, StairKit.DressingVersion, run.Pieces[0].Position);
            Assert.Equal(inWorld, Reads(() => StairStructures.Parse(layout)));
            Assert.Equal(inWorld, Reads(() => BuiltStairs.Parse(built)));
            Assert.Equal(inWorld, BuiltStairs.Keep(run, StairStructures.KitOf(run), run.Pieces, world: null) != null);
            if (inWorld) Assert.Equal(run.From, Assert.Single(StairStructures.Parse(layout)).From);
            else Assert.Empty(BuiltStairs.Sites);
        }
    }

    /// <summary>A network saved in this build's format with a road point at <paramref name="x"/> is read by the same rule.</summary>
    [Theory]
    [InlineData(128f, true)]
    [InlineData(9999f, true)]
    [InlineData(10000f, false)]
    [InlineData(10001f, false)]
    [InlineData(10500f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void ACurrentSaveReadsRoadPointsByTheSameRule(float x, bool inWorld)
    {
        bool finite = !float.IsNaN(x) && !float.IsInfinity(x);
        const float interior = 128f;
        RoadSpatialGrid.CommitLevelled(new[] { new Vector2(finite ? x : interior, 0f) }, new[] { 34f }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        byte[] roadData = Assert.IsType<byte[]>(RoadSpatialGrid.SerializeAllRoadPoints());
        if (!finite)
        {
            // The first point's x in a one-cell, one-point save of this build's format.
            const int firstPointX = 6 * sizeof(int);
            Assert.Equal(interior, BitConverter.ToSingle(roadData, firstPointX));
            BitConverter.GetBytes(x).CopyTo(roadData, firstPointX);
        }

        RoadSpatialGrid.Clear();
        Assert.Equal(inWorld, RoadSpatialGrid.DeserializeAllRoadPoints(roadData));
        if (inWorld)
            Assert.Equal(x, Assert.Single(RoadSpatialGrid.SnapshotAllRoadPoints()).p.x);
        else
        {
            Assert.Empty(RoadSpatialGrid.SnapshotAllRoadPoints());
            Assert.Contains(finite ? "outside the world" : "a non-finite value", RoadSpatialGrid.LastReadRefusal);
        }
    }

    /// <summary>Level ground at 10 m in the mountains.</summary>
    private sealed class LevelWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 10f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 81;
    }

    /// <summary>The stone kit's reference crossing (StoneBridgeTests.ReferenceWorld) moved east by <see cref="East"/>.</summary>
    private sealed class EastWorld : WorldGenerator
    {
        public const float East = 9989f;
        private readonly StoneBridgeTests.ReferenceWorld _w = new() { Biome = Heightmap.Biome.Meadows };
        public override float GetHeight(float wx, float wy) => _w.GetHeight(wx - East, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => _w.Biome;
        public override int GetSeed() => _w.Seed;
    }

    /// <summary>
    /// One verdict for laying and saving (StructureSites.Refuses, the point rule's SpatialFault over a structure's ends,
    /// pieces and the ground its record would hold): a staircase in the world but for the ground under its last stair, whose
    /// vertices reach the edge, is refused at its first build, so its plan lays none of it in any zone (StairStructures.PiecesOf)
    /// and nothing is saved or held. Before, the build refused to save it and the plan laid it anyway, zone by zone. The same
    /// staircase 2 m in from the edge is laid and saved.
    /// </summary>
    [Fact]
    public void AStaircaseRefusedAtItsBuildIsNotLaidEither()
    {
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope();
        var world = new LevelWorld();
        scope.WithWorld(world);
        foreach ((float x, bool inWorld) in new[] { (9998.5f, false), (9996.5f, true) })
        {
            RoadNetworkGenerator.Reset();
            var run = new StairRun { From = new Vector2(x, 0f), To = new Vector2(x, 2f), Width = RoadStairRuns.Width, Kit = StairKit.Stone };
            run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = StairKit.Stone.Stair, Position = new Vector3(x, 10f, 1f), YawDegrees = 180f });
            StairStructures.Add(run);
            List<StructurePiece> plan = StairStructures.PiecesOf(run, world);
            Assert.Equal(inWorld, plan.Count > 0);
            Assert.Equal(inWorld, StairStructures.Build(run, world).Count > 0);
            Assert.Equal(inWorld, BuiltStairs.Of(run) != null);
            Assert.Equal(inWorld, new StairStructures().Plan(world, world.GetSeed()).Any());
        }
    }

    /// <summary>
    /// A bridge whose banks are in the world but whose far end reaches the edge (a piece or the ground it holds) is refused
    /// at its first build by the one verdict (StructureSites.Refuses, asked where its pieces are solved, BridgeStructures.Solve):
    /// its plan lays none of it in any zone (BridgeStructures.PiecesOf, the plan's), nothing is saved and the refusal is said
    /// once.
    /// Before, the build refused to save it and the plan laid it from its pieces anyway, zone by zone.
    /// </summary>
    [Fact]
    public void ABridgeRefusedAtItsBuildIsNotLaidEither()
    {
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope();
        var world = new EastWorld();
        scope.WithWorld(world);
        var c = RoadCrossing.Between(new Vector2(EastWorld.East - 10f, 0f), new Vector2(EastWorld.East + 10f, 0f), 28f, new Vector2(EastWorld.East, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = 34.83f - BridgeLayout.DeckFreeboard;
        Assert.True(ReadsCrossing(c));
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try
        {
            Assert.Empty(BridgeStructures.PiecesOf(c, world, world.GetSeed()));
            Assert.Empty(BridgeStructures.Build(c, world, world.GetSeed()));
            Assert.Null(BuiltBridges.Of(c));
            Assert.Empty(BridgeStructures.PiecesOf(c, world, world.GetSeed()));
            Assert.Single(lines, l => l.Contains("refused, nothing of it laid or saved: ") && l.Contains("outside the world"));
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; BuiltBridges.Reset(); }
    }

    /// <summary>A network a released build saved keeps its own reading: a point past the edge loads as it did.</summary>
    [Fact]
    public void AReleasedSaveKeepsAPointPastTheEdge()
    {
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(Released144.Blob(164, 0, (10500f, 0f, 4f, 34f))));
        Assert.Equal(10500f, Assert.Single(RoadSpatialGrid.SnapshotAllRoadPoints()).p.x);
        Assert.True(RoadSpatialGrid.LoadedFromReleasedFormat);
    }

    private static RoadCrossing Crossing(Vector2 from, Vector2 to, Vector2 toEdge)
    {
        var crossing = RoadCrossing.Between(from, to, 25f, (from + to) * 0.5f, 12f, CrossingKind.Bridge);
        crossing.FromEnd = new StructureEnds.End(from, new Vector2(1f, 0f), 34f, 0);
        crossing.ToEnd = new StructureEnds.End(toEdge, new Vector2(-1f, 0f), 34f, 0);
        return crossing;
    }

    /// <summary>Whether the crossings reader takes this crossing back as it was written; the save, judging by the same rule
    /// (RoadNetworkPersistence.CrossingRecordFault), writes it exactly when the reader takes it.</summary>
    private static bool ReadsCrossing(RoadCrossing crossing)
    {
        byte[] data = RoadNetworkPersistence.EncodeRoadCrossings(new[] { crossing });
        var loaded = new List<RoadCrossing>();
        bool reads = RoadNetworkPersistence.DeserializeRoadCrossings(data, loaded);
        if (!reads) Assert.Empty(loaded);
        else Assert.Equal(data, RoadNetworkPersistence.SerializeRoadCrossings(loaded));
        var saved = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(RoadNetworkPersistence.SerializeRoadCrossings(new[] { crossing }), saved));
        Assert.Equal(reads ? 1 : 0, saved.Count);
        return reads;
    }

    /// <summary>Whether a reader takes its record; a refusal is malformed data.</summary>
    private static bool Reads(Action read)
    {
        Exception? refusal = Record.Exception(read);
        if (refusal != null) Assert.IsType<InvalidDataException>(refusal);
        return refusal == null;
    }

    /// <summary>A built site's record (<see cref="BuiltSites{T}.Serialize"/>'s format 2) of one site with no kit, one deck
    /// piece and this ground, written by hand so a record the build refuses can be read.</summary>
    private static byte[] SavedSite(Vector2 from, Vector2 to, int layout, Vector3 piece, params GroundPin[] ground)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2); writer.Write(1);
        writer.Write(from.x); writer.Write(from.y); writer.Write(to.x); writer.Write(to.y);
        writer.Write(0); writer.Write(layout);
        writer.Write(1);
        writer.Write((int)StructurePieceKind.Deck); writer.Write("wood_floor");
        writer.Write(piece.x); writer.Write(piece.y); writer.Write(piece.z);
        writer.Write(0f); writer.Write(45f); writer.Write(0f); writer.Write(1f);
        writer.Write(ground.Length);
        foreach (GroundPin pin in ground) { writer.Write((short)pin.X); writer.Write((short)pin.Z); writer.Write(pin.Height); }
        return stream.ToArray();
    }

    private static StructurePiece Piece(float x, StructurePieceKind kind) => new()
    {
        Kind = kind,
        Prefab = kind == StructurePieceKind.Deck ? "wood_floor" : "wood_pole",
        Position = new Vector3(x, 34f, 0f),
        YawDegrees = 45f,
    };
}
