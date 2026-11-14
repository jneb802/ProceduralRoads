using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The one ground every structure is laid on: the finished ground, as the terrain writer leaves it once the network is
/// levelled, not the generator's. The road's earthwork raises the ground under a structure's ends (a landing, a levelled
/// apron, a fill to the road's floor) or cuts it away; a piece laid against the generator's ground is then wholly in the
/// ground (the game breaks it, dropping its resources: <see cref="BuriedPieces"/>), or a column stops short of the ground
/// cut from under it. Every kit's laying reads this: the bridges' and ford spans' columns, piers, footing, end stairs and
/// what is left out as buried (BridgeLayout's kits, <see cref="DvergrWoodBridge"/>, <see cref="DvergrViaduct"/>), and the
/// staircases' settling on the finished ground (<see cref="StairStructures.SettleOnFinishedGround"/>). Under a structure's
/// end the road runs on under its joining piece at the meeting height (<see cref="StructureEnds.End.Joint"/>): that
/// levelling is the writer's own, from the road's points, so it is in these vertices, and nothing past the writer's
/// earthwork is read as cut.
/// <para>A structure saved with the network (a staircase, a bridge whose first zone is built) saves the ground it was laid on
/// (<see cref="Measure"/>), and every terrain write levels to it where the structure stands (<see cref="Held"/>), whatever
/// a later writer would make of the ground there: the structure stands on its ground, neither floating nor buried.</para>
/// <para>Decisions the network is built from (where a structure's ends are, how many steps they take, whether a pier
/// stands) stay on the generator's ground: the road's levelling is made from them.</para>
/// </summary>
public static class StructureGround
{
    /// <summary>
    /// The finished ground at whole-metre vertices (the game's heightmap vertices): the generator's ground with the network's
    /// earthwork and the staircases' cuts as the terrain writer applies them (<see cref="RoadTerrainModifier.FinishedGround"/>),
    /// from the network as it now stands. NaN inside a location whose own levelling is not known. One caller at a time.
    /// </summary>
    public static Func<int, int, float> Vertices(WorldGenerator world) =>
        RoadTerrainModifier.FinishedGround((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));

    /// <summary>Why a structure on ground not known is not laid: the first of its pieces on it, and the location it stands in.</summary>
    internal static string NotKnown(StructurePiece piece)
    {
        var at = new Vector2(piece.Position.x, piece.Position.z);
        string location = RoadLocationProtection.NearestFootprint(at, 0f, out RoadLocationProtection.Footprint f)
            ? FormattableString.Invariant($"{LocationPrefabLevelling.PrefabAt(f.Centre) ?? "a location"} at ({f.Centre.x:F1}, {f.Centre.y:F1})")
            : "a location";
        Vector2s zone = ZoneSystem.GetZone(piece.Position);
        return FormattableString.Invariant(
            $"the finished ground under its piece at ({at.x:F1}, {at.y:F1}) in zone ({zone.x}, {zone.y}) is not known (inside {location}, whose ground is not published)");
    }

    /// <summary>The structures said not laid for ground not known (<see cref="NotLaid"/>), once each.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> s_said = new();

    /// <summary>
    /// A structure that lays nothing because the finished ground under <paramref name="piece"/> is not known
    /// (<see cref="FirstOnUnknownGround"/>), said as an error, once per structure: <paramref name="what"/> names it, and
    /// <see cref="NotKnown"/> the location and the zone. A bug under the preload-list rule: a structure stands only in a location on
    /// the preload list (RoadLocationProtection.StructureEnters, RoadLocationProtection.Enterable), whose ground the preload published.
    /// Nothing waits: no waiting state, nothing laid or saved, and no later publish lays it.
    /// </summary>
    internal static void NotLaid(object structure, string what, StructurePiece piece)
    {
        lock (s_said)
        {
            if (s_said.TryGetValue(structure, out _)) return;
            s_said.Add(structure, structure);
        }
        ProceduralRoadsPlugin.ProceduralRoadsLogger?.LogError($"{what} not laid: {NotKnown(piece)}; a structure stands only in a location on the preload list");
    }

    /// <summary>
    /// The finished ground at any point, as the game's terrain mesh has it: the finished heights at the whole-metre vertices
    /// round it (<see cref="Vertices"/>), spread over the cell's two triangles (<see cref="BuriedPieces.MeshHeight"/>). Where
    /// the writer changes nothing it is the game's mesh over the generator's vertices, not the generator's ground between
    /// them, which the game never shows. NaN where a vertex round it is inside a location whose ground is not known yet: not
    /// known, never the generator's ground in its stead. One caller at a time.
    /// </summary>
    public static Func<Vector2, float> At(WorldGenerator world)
    {
        Func<int, int, float> finished = Vertices(world);
        return p => BuriedPieces.MeshHeight(p.x, p.y, finished);
    }

    /// <summary>How far round a piece's origin, across, the ground it stands on is saved: the vertices under its footprint.</summary>
    public const float HeldReach = 1f;

    // ------------------------------------------------------------- mounds

    /// <summary>
    /// A mound: the ground a structure's limiting pier or stair footing stands on raised to <see cref="Top"/>, the least
    /// that brings it within its kit's limit (a viaduct's pier under DvergrViaduct.PierCeiling, DvergrViaduct.Mounds; a stone
    /// pier's courses and a footing's pillars, BridgeLayout.StoneMounds). Real ground, not road: stored with the road as
    /// mound points (RoadSpatialGrid.RoadPoint.mound; RoadNetworkGenerator.CrossingGround.Mounds, the one place a road's
    /// crossings change the ground besides the road), one at each of <see cref="Points"/>, where the kit reads its ground,
    /// <see cref="Width"/> wide; the terrain writer raises it and blends it into the ground as its own fill, unpainted
    /// (RoadTerrainModifier.ModifyVertexHeights, RoadSpatialGrid.RoadPoint.Levelled), and every finished-ground reader reads
    /// it (<see cref="Vertices"/>, <see cref="At"/>): the kit's pieces end on it, and the support rule grounds them on it.
    /// Not walked, not road (RoadSpatialGrid.RoadPoint.Walked, RoadSpatialGrid.RoadPoint.IsRoad): it clears no vegetation and
    /// no rock, is no junction, and reaches no location or mark. Decided after the route is chosen, at the crossing's kind (a
    /// viaduct) or as its road is stored (the stone kit, where it is the kit drawn), never a raise's step
    /// (DvergrViaduct.RaisedSteps).
    /// </summary>
    public readonly struct Mound
    {
        public readonly List<Vector2> Points;
        public readonly float Top;
        public Mound(List<Vector2> points, float top) { Points = points; Top = top; }
        /// <summary>Its road points' width: every whole-metre vertex of the mesh cell round each point flat at
        /// <see cref="Top"/> (the cell's diagonal either side), so the game's mesh (BuriedPieces.MeshHeight) reads the
        /// top there.</summary>
        public static float Width => 2f * Mathf.Sqrt(2f);
        /// <summary>The mound as the points it is stored as (RoadSpatialGrid.RoadPoint.mound), each at <see cref="Top"/>.</summary>
        public RoadSpatialGrid.PlannedPath Plan()
        {
            var heights = new List<float>(Points.Count);
            for (int i = 0; i < Points.Count; i++) heights.Add(Top);
            return new RoadSpatialGrid.PlannedPath(new List<Vector2>(Points), heights, new List<RoadPointDebugInfo>(), Width, 0f, false) { Mound = true };
        }
    }

    /// <summary>
    /// Whether ground at <paramref name="ground"/> may be raised to <paramref name="top"/> as a mound
    /// (<see cref="Mound"/>): by no more than the kit's limit (BridgeSupport.MostMoundOf), within the terrain writer's cap
    /// (RoadNetworkGenerator.WithinTerrainLimit of RoadConstants.TerrainDeltaMax), and under water (RoadWater.Wet) only
    /// with its top still under it (RoadWater.AtOrOverSea: no mound breaks the surface). Ground not known (NaN): no.
    /// </summary>
    public static bool MayRaise(float ground, float top, BridgeStructures.Kit kit) =>
        top - ground <= BridgeSupport.MostMoundOf(kit) && RoadNetworkGenerator.WithinTerrainLimit(top, ground, RoadConstants.TerrainDeltaMax)
        && !(RoadWater.Wet(ground) && RoadWater.AtOrOverSea(top));

    /// <summary>How far above or below a piece's origin the ground is the ground it stands on: a deck high over a river
    /// holds none of the riverbed.</summary>
    public const float HeldDepth = 3f;

    /// <summary>The ground the saved structures were laid on, by whole-metre vertex. Read by every terrain write.</summary>
    private static readonly ConcurrentDictionary<(int, int), float> s_held = new();

    /// <summary>The ground a saved structure was laid on at a whole-metre vertex, null where none stands. Any thread.</summary>
    public static float? Held(Vector2 vertex) =>
        s_held.TryGetValue((Mathf.RoundToInt(vertex.x), Mathf.RoundToInt(vertex.y)), out float h) ? h : null;

    /// <summary>
    /// The ground a structure's pieces stand on, to save with it: the finished ground (<paramref name="vertex"/>) at every
    /// vertex within <see cref="HeldReach"/> across of a piece's origin and <see cref="HeldDepth"/> of its height (its feet,
    /// ends, landings and footings; debris and flair hold none). Each vertex once, in the pieces' order; NaN is not saved.
    /// </summary>
    public static List<GroundPin> Measure(IEnumerable<StructurePiece> pieces, Func<int, int, float> vertex)
    {
        var pins = new List<GroundPin>();
        var measured = new HashSet<(int, int)>();
        foreach (StructurePiece piece in pieces)
        {
            if (piece.Kind == StructurePieceKind.Debris || piece.Kind == StructurePieceKind.Flair) continue;
            Vector3 p = piece.Position;
            foreach ((int x, int z) in VerticesUnder(p))
            {
                if (measured.Contains((x, z))) continue;
                float h = vertex(x, z);
                if (float.IsNaN(h) || Mathf.Abs(h - p.y) > HeldDepth) continue;
                measured.Add((x, z));
                pins.Add(new GroundPin(x, z, h));
            }
        }
        return pins;
    }

    /// <summary>
    /// The first of <paramref name="pieces"/> with a vertex of <paramref name="vertex"/> ground not known (NaN: inside a location
    /// whose ground is not read yet) within <see cref="HeldReach"/> across of its origin, the vertices its ground is saved
    /// from (<see cref="Measure"/>); null where all of it is known. Ground not known is never a verdict: a staircase on it
    /// (<see cref="StairStructures.UnknownGround"/>) and a bridge or viaduct whose solve laid a piece on it
    /// (<see cref="BridgeStructures.Solve(RoadCrossing, WorldGenerator, int)"/>) lay nothing, said as an error
    /// (<see cref="NotLaid"/>); a stone bridge's support reading on it decides nothing (<see cref="BridgeStructures.CheckSupport"/>).
    /// </summary>
    public static StructurePiece? FirstOnUnknownGround(IEnumerable<StructurePiece> pieces, Func<int, int, float> vertex)
    {
        foreach ((StructurePiece piece, _, _) in UnknownUnder(pieces, vertex)) return piece;
        return null;
    }

    /// <summary>Every vertex of <paramref name="vertex"/> ground not known under a piece (<see cref="VerticesUnder"/>,
    /// <see cref="FirstOnUnknownGround"/>'s test), with the piece, in the pieces' order.</summary>
    internal static IEnumerable<(StructurePiece piece, int x, int z)> UnknownUnder(IEnumerable<StructurePiece> pieces, Func<int, int, float> vertex)
    {
        foreach (StructurePiece piece in pieces)
            foreach ((int x, int z) in VerticesUnder(piece.Position))
                if (float.IsNaN(vertex(x, z))) yield return (piece, x, z);
    }

    /// <summary>The whole-metre vertices the ground a piece at <paramref name="p"/> stands on is read at: those within
    /// <see cref="HeldReach"/> across of its origin, rounded out. The one set its ground is saved from (<see cref="Measure"/>),
    /// tested for ground not known (<see cref="FirstOnUnknownGround"/>) and kept out of another location by (RoadLocationProtection.StructureEnters).</summary>
    internal static IEnumerable<(int x, int z)> VerticesUnder(Vector3 p)
    {
        for (int x = Mathf.FloorToInt(p.x - HeldReach); x <= Mathf.CeilToInt(p.x + HeldReach); x++)
            for (int z = Mathf.FloorToInt(p.z - HeldReach); z <= Mathf.CeilToInt(p.z + HeldReach); z++)
                yield return (x, z);
    }

    /// <summary>A structure's saved ground held from now on; a vertex held already keeps the height it has.</summary>
    public static void Hold(IEnumerable<GroundPin> pins)
    {
        foreach (GroundPin pin in pins) s_held.TryAdd((pin.X, pin.Z), pin.Height);
    }

    /// <summary>The held ground made again from the saved structures: the staircases' layouts, then the built bridges', then
    /// the built staircases' (their layout's ground and what their kit added).</summary>
    public static void Rebuild()
    {
        s_held.Clear();
        foreach (StairRun run in StairStructures.Runs) Hold(run.Ground);
        foreach (BuiltBridge site in BuiltBridges.Sites) Hold(site.Ground);
        foreach (BuiltStair site in BuiltStairs.Sites) Hold(site.Ground);
    }
}

/// <summary>A height the finished ground must meet at a whole-metre vertex under a saved structure
/// (<see cref="StructureGround.Measure"/>).</summary>
public readonly struct GroundPin
{
    public readonly int X, Z;
    public readonly float Height;

    public GroundPin(int x, int z, float height) { X = x; Z = z; Height = height; }

    /// <summary>Written as two 16-bit vertex coordinates and the height.</summary>
    internal static void WriteAll(System.IO.BinaryWriter writer, IReadOnlyList<GroundPin> pins)
    {
        writer.Write(pins.Count);
        foreach (GroundPin pin in pins)
        {
            writer.Write((short)pin.X); writer.Write((short)pin.Z); writer.Write(pin.Height);
        }
    }

    /// <summary>Pins as <see cref="WriteAll"/> writes them, added to <paramref name="into"/>. Malformed data throws; where
    /// the pins stand is the structure's verdict (<see cref="StructureSites.SpatialFault"/>), which each reader of a record
    /// with pins asks.</summary>
    internal static void ReadAll(System.IO.BinaryReader reader, List<GroundPin> into)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > 2_000_000) throw new System.IO.InvalidDataException("Invalid saved ground count.");
        for (int i = 0; i < count; i++)
        {
            int x = reader.ReadInt16(), z = reader.ReadInt16();
            float h = reader.ReadSingle();
            if (!ManualRoadDraft.IsFinite(h))
                throw new System.IO.InvalidDataException("Invalid saved ground.");
            into.Add(new GroundPin(x, z, h));
        }
    }
}
