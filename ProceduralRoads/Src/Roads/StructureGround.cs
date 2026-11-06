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
/// staircases' settling on the finished ground (<see cref="StairStructures.SettleOnFinishedGround"/>).
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
    /// from the network as it now stands. NaN inside a place whose own levelling is not known. One caller at a time.
    /// </summary>
    public static Func<int, int, float> Vertices(WorldGenerator world) =>
        RoadTerrainModifier.FinishedGround((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));

    /// <summary>
    /// The finished ground at any point: the generator's ground there, changed by what the terrain writer changes at the
    /// vertices round it, spread between them as the game's terrain mesh spreads it (<see cref="BuriedPieces.MeshHeight"/>).
    /// Where the writer changes nothing it is the generator's ground exactly. A vertex inside a place whose own levelling is
    /// not known is taken as unchanged. One caller at a time.
    /// </summary>
    public static Func<Vector2, float> At(WorldGenerator world)
    {
        Func<int, int, float> finished = Vertices(world);
        var change = new Dictionary<(int, int), float>();
        float Change(int x, int z)
        {
            if (change.TryGetValue((x, z), out float known)) return known;
            float h = finished(x, z);
            float d = float.IsNaN(h) ? 0f : h - BiomeBlendedHeight.GetBlendedHeight(x, z, world);
            change[(x, z)] = d;
            return d;
        }
        return p => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world) + BuriedPieces.MeshHeight(p.x, p.y, Change);
    }

    /// <summary>
    /// The finished ground under a structure with ends <paramref name="near"/> and <paramref name="far"/>
    /// (<see cref="StructureEnds.Of"/>): <see cref="At"/>, and no higher than an end's meeting height where the road runs on
    /// under its joining piece (<see cref="StructureEnds.End.UnderTheJoint"/>, <paramref name="halfWidth"/> either side of
    /// its line), which the road's levelling cuts the bank to.
    /// </summary>
    public static Func<Vector2, float> Of(WorldGenerator world, StructureEnds.End near, StructureEnds.End far, float halfWidth)
    {
        Func<Vector2, float> at = At(world);
        return p => UnderTheEnds(p, at(p), near, far, halfWidth);
    }

    /// <summary><paramref name="ground"/> at <paramref name="p"/>, no higher than an end's meeting height where the road
    /// runs on under its joining piece.</summary>
    internal static float UnderTheEnds(Vector2 p, float ground, StructureEnds.End near, StructureEnds.End far, float halfWidth)
    {
        foreach (StructureEnds.End end in new[] { near, far })
            if (end.IsSet && end.UnderTheJoint(p, halfWidth)) ground = Mathf.Min(ground, end.MeetHeight);
        return ground;
    }

    /// <summary>How far round a piece's origin, across, the ground it stands on is saved: the vertices under its footprint.</summary>
    public const float HeldReach = 1f;

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
            for (int x = Mathf.FloorToInt(p.x - HeldReach); x <= Mathf.CeilToInt(p.x + HeldReach); x++)
                for (int z = Mathf.FloorToInt(p.z - HeldReach); z <= Mathf.CeilToInt(p.z + HeldReach); z++)
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

    /// <summary>Pins as <see cref="WriteAll"/> writes them, added to <paramref name="into"/>. Malformed data throws.</summary>
    internal static void ReadAll(System.IO.BinaryReader reader, List<GroundPin> into)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > 2_000_000) throw new System.IO.InvalidDataException("Invalid saved ground count.");
        for (int i = 0; i < count; i++)
        {
            int x = reader.ReadInt16(), z = reader.ReadInt16();
            float h = reader.ReadSingle();
            if (!ManualRoadDraft.ValidPoint(new Vector2(x, z)) || !ManualRoadDraft.IsFinite(h))
                throw new System.IO.InvalidDataException("Invalid saved ground.");
            into.Add(new GroundPin(x, z, h));
        }
    }
}
