using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// What the game's support rule (<see cref="SupportGraph"/>) holds of a laid Dvergr viaduct (<see cref="DvergrViaduct"/>):
/// every piece's support as it settles, on the finished ground the layout stood its piers on, each piece as the prefab's
/// measured data has it (<see cref="BuriedPieces.DataOf"/>), as a staircase's are.
/// </summary>
public static class ViaductSupport
{
    /// <summary>
    /// How far over the game the model may read a piece, by prefab: what readings in game show
    /// (ViaductSupportTests, the fixture). blackmarble_out_1 up to 19.4 % over: a corbel taken as its whole 2 x 1 x 2 box
    /// meets a diagonal neighbour level with its centre, which the game's corbel does not, and averages it in.
    /// blackmarble_arch up to 4.9 % over: its colliders are not one box. Every other prefab reads at or under the game.
    /// <see cref="Held.Share"/> takes it off, so a margin over the share is a margin over the game.
    /// </summary>
    public static float Overread(string prefab) => prefab switch
    {
        DvergrViaduct.Corbel => 1.2f,
        DvergrViaduct.Arch => 1.05f,
        _ => 1f,
    };

    /// <summary>One laid piece's support and its material's least (the game breaks it under that); support NaN for a piece
    /// that is not stone or iron.</summary>
    public readonly struct Held
    {
        public readonly StructurePiece Piece;
        public readonly float Support, Min;
        /// <summary>Whether it touches a laid piece the rule does not read (measured bounds but no material,
        /// <see cref="BuriedPieces.Measured.Material"/>: a wooden stair, whose colliders are not measured as boxes), which
        /// may carry it in the game: its support here is then no more than the game's, and its
        /// falling no verdict (<see cref="BridgeStructures.Read"/>).</summary>
        public readonly bool ThroughUnread;
        public Held(StructurePiece piece, float support, float min, bool throughUnread = false)
        {
            Piece = piece; Support = support; Min = min; ThroughUnread = throughUnread;
        }
        /// <summary>Its support over its material's least, the model's over-reading taken off (<see cref="Overread"/>):
        /// under 1 it falls.</summary>
        public float Share => Support / (Min * Overread(Piece.Prefab));
        /// <summary>Whether the rule reads it as falling: its <see cref="Share"/> under 1.</summary>
        public bool Falls => Share < 1f;
    }

    /// <summary>
    /// The finished ground one viaduct is laid on, and whether its laid pieces meet it, kept across the support passes of one
    /// layout's solve: the ground is read once per vertex and once per piece (it is most of a pass's cost). A piece is the
    /// same piece only with the same prefab, position and turns, exactly: one laid again by a later pass at the same place
    /// reads the same. Its vertices are the finished ground's own (<see cref="StructureGround.Vertices"/>), read exactly: a
    /// vertex known is known whatever its neighbours are, the one reading of ground not known that the bridge's solve makes
    /// too (<see cref="StructureGround.FirstOnUnknownGround"/>).
    /// </summary>
    public sealed class Grounding
    {
        private readonly Func<int, int, float> _ground;
        private readonly Dictionary<(int, int), float> _vertices = new();
        private readonly Dictionary<(string, float, float, float, float, float, float), bool> _known = new();

        /// <summary>On the whole-metre vertices <paramref name="vertices"/>, which do not change while this is read.</summary>
        public Grounding(Func<int, int, float> vertices) { _ground = vertices; }

        /// <summary>The ground at a whole-metre vertex: what the game's terrain mesh is made of.</summary>
        internal float Vertex(int x, int z)
        {
            if (_vertices.TryGetValue((x, z), out float h)) return h;
            return _vertices[(x, z)] = _ground(x, z);
        }

        /// <summary>Whether <paramref name="p"/>, the support rule's piece <paramref name="box"/>, meets the ground
        /// (<see cref="SupportGraph.OnTerrain"/>, on the ground's mesh).</summary>
        internal bool Of(StructurePiece p, SupportGraph.Piece box)
        {
            var key = (p.Prefab, p.Position.x, p.Position.y, p.Position.z, p.YawDegrees, p.PitchDegrees, p.RollDegrees);
            if (_known.TryGetValue(key, out bool grounded)) return grounded;
            return _known[key] = SupportGraph.OnTerrain(box, Vertex) == SupportGraph.Terrain.Meets;
        }
    }

    /// <summary>The support every piece of <paramref name="pieces"/> (a viaduct of <paramref name="crossing"/>, as
    /// DvergrViaduct.Solve laid it) settles at.</summary>
    public static List<Held> Of(RoadCrossing crossing, WorldGenerator world, IReadOnlyList<StructurePiece> pieces) =>
        Of(new Grounding(StructureGround.Vertices(world)), pieces);

    /// <summary>The support every piece of <paramref name="pieces"/> settles at, on <paramref name="ground"/>'s ground: each
    /// piece put to the support rule as a staircase's is, from its measured data at its own position and turn
    /// (<see cref="StairSupport.Holding(IReadOnlyList{StructurePiece}, Func{StructurePiece, SupportGraph.Piece, bool})"/>).</summary>
    public static List<Held> Of(Grounding ground, IReadOnlyList<StructurePiece> pieces) => StairSupport.Holding(pieces, ground.Of);
}
