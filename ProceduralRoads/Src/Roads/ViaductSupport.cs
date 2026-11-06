using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// What the game's support rule (<see cref="SupportGraph"/>) holds of a laid Dvergr viaduct (<see cref="DvergrViaduct"/>):
/// every piece's support as it settles, on the finished ground the layout stood its piers on.
/// </summary>
public static class ViaductSupport
{
    /// <summary>What a laid piece is made of: the black marble pieces marble, the floors and screens iron, a staircase's
    /// stone pieces stone; null for a piece the support rule is not read for here (a vine, a wooden rail).</summary>
    public static SupportGraph.Material? MaterialOf(string prefab) =>
        prefab.StartsWith("blackmarble_", StringComparison.Ordinal) ? SupportGraph.Marble
        : prefab.StartsWith("iron_", StringComparison.Ordinal) ? SupportGraph.Iron
        : prefab.StartsWith("stone_", StringComparison.Ordinal) ? SupportGraph.Stone
        : null;

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
        public Held(StructurePiece piece, float support, float min) { Piece = piece; Support = support; Min = min; }
        /// <summary>Its support over its material's least, the model's over-reading taken off (<see cref="Overread"/>):
        /// under 1 it falls.</summary>
        public float Share => Support / (Min * Overread(Piece.Prefab));
    }

    /// <summary>Whether laid pieces meet the ground, kept across the support passes of one layout's solve: the ground is
    /// read once per piece where it lies (it is most of a pass's cost).</summary>
    public sealed class Grounding
    {
        private readonly Dictionary<(string, int, int, int), bool> _known = new();
        internal bool Of(StructurePiece p, Func<bool> read)
        {
            var key = (p.Prefab, Mathf.RoundToInt(p.Position.x * 100f), Mathf.RoundToInt(p.Position.y * 100f), Mathf.RoundToInt(p.Position.z * 100f));
            if (_known.TryGetValue(key, out bool grounded)) return grounded;
            return _known[key] = read();
        }
    }

    /// <summary>The support every piece of <paramref name="pieces"/> (a viaduct of <paramref name="crossing"/>, as
    /// DvergrViaduct.Solve laid it) settles at.</summary>
    public static List<Held> Of(RoadCrossing crossing, WorldGenerator world, IReadOnlyList<StructurePiece> pieces) =>
        Of(crossing, DvergrViaduct.FinishedGround(crossing, world), pieces);

    /// <summary><see cref="Of(RoadCrossing, WorldGenerator, IReadOnlyList{StructurePiece})"/> on <paramref name="ground"/>,
    /// what meets it kept in <paramref name="grounding"/> where given.</summary>
    public static List<Held> Of(RoadCrossing crossing, Func<Vector2, float> ground, IReadOnlyList<StructurePiece> pieces,
        Grounding? grounding = null)
    {
        Vector2 dir = crossing.Direction, side = new(-dir.y, dir.x);
        var graph = new List<SupportGraph.Piece>();
        var laid = new List<StructurePiece>();
        foreach (StructurePiece p in pieces)
        {
            if (MaterialOf(p.Prefab) is not SupportGraph.Material material) continue;
            Vector2 rel = new Vector2(p.Position.x, p.Position.z) - crossing.FromBank;
            float u = Vector2.Dot(rel, dir), v = Vector2.Dot(rel, side);
            var (hu, hv, hy) = DvergrViaduct.Extents(p.Prefab);
            // On the structure's own axes: x along, y up, z across. A step's origin is at its foot (y 0..1).
            float y0 = p.Position.y - hy, y1 = p.Position.y + hy;
            if (p.Prefab == DvergrViaduct.StepPrefab) { y0 = p.Position.y; y1 = p.Position.y + 1f; }
            var centre = new Vector3(u, p.Position.y, v);
            bool Read() => Grounded(crossing.FromBank, dir, side, u - hu, u + hu, v - hv, v + hv, y0, y1, ground);
            bool grounded = grounding != null ? grounding.Of(p, Read) : Read();
            graph.Add(new SupportGraph.Piece(centre, new Vector3(u - hu, y0, v - hv), new Vector3(u + hu, y1, v + hv), material, grounded));
            laid.Add(p);
        }
        float[] support = SupportGraph.Settle(graph);
        var held = new List<Held>(laid.Count);
        for (int i = 0; i < laid.Count; i++) held.Add(new Held(laid[i], support[i], graph[i].Material.Min));
        return held;
    }

    /// <summary>Whether a box's bounds, grown by <see cref="SupportGraph.Reach"/>, meet the ground's surface: the ground
    /// over its footprint (grown too) reaches its bottom somewhere and is under its top somewhere.</summary>
    private static bool Grounded(Vector2 origin, Vector2 dir, Vector2 side, float u0, float u1, float v0, float v1, float y0, float y1,
        Func<Vector2, float> ground)
    {
        float r = SupportGraph.Reach, lo = float.MaxValue, hi = float.MinValue;
        int nu = Mathf.Max(2, Mathf.CeilToInt((u1 - u0 + 2f * r) / 0.5f)), nv = Mathf.Max(2, Mathf.CeilToInt((v1 - v0 + 2f * r) / 0.5f));
        for (int i = 0; i <= nu; i++)
            for (int j = 0; j <= nv; j++)
            {
                float g = ground(origin + dir * Mathf.Lerp(u0 - r, u1 + r, i / (float)nu) + side * Mathf.Lerp(v0 - r, v1 + r, j / (float)nv));
                lo = Mathf.Min(lo, g);
                hi = Mathf.Max(hi, g);
            }
        return hi >= y0 - r && lo <= y1 + r;
    }
}
