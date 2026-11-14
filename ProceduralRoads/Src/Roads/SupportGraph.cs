using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The game's structural support (<c>WearNTear.UpdateSupport</c>) run offline over a laid structure: what every piece's
/// support settles at, so a layout can be checked against it before a player sees it fall.
///
/// <para>The rule, transcribed from <c>UpdateSupport</c> (Valheim 1.0): a piece whose bounds, grown 0.15 m a side, touch the
/// terrain has its material's most support. Otherwise it takes the best of what the pieces its grown bounds touch offer.
/// From a neighbour of support <c>S</c> whose centre lies <c>d</c> away (plus 0.1 m), always
/// <c>S (1 - horizontalLoss d)</c>; where the neighbour's nearest point to its centre lies under it (or level, within
/// 0.05 m), also <c>S (1 - loss d)</c>, the loss lerped from horizontal to vertical by how steeply that point lies under
/// it, and that point counts as a support point worth <c>S (1 - verticalLoss d)</c>. Two support points whose
/// directions across lie 100 degrees or more apart give the average of their two worths: a piece resting between two
/// is carried at the vertical loss, which is what lets a deck span. The losses are the receiving piece's material's
/// (<see cref="Material"/>).</para>
///
/// <para>Pieces here are boxes, every collider a box, which the viaduct's and a staircase's are near enough: 2 m cubes,
/// slabs and half-cubes. A laid structure's (a staircase's, the viaduct's: <see cref="StairSupport.Holding(IReadOnlyList{StructurePiece}, Func{StructurePiece, SupportGraph.Piece, bool})"/>) are each
/// turned about the vertical (<see cref="Piece(Vector3, Vector3, float, Vector3, Vector3, Material, bool, bool)"/>), from
/// the prefab's measured data (<see cref="BuriedPieces.DataOf"/>).
/// The game settles support frame by frame; this is that same rule's one fixed point (every loss is a fraction lost, so a
/// piece nothing grounded reaches settles at 0, as in the game).</para>
/// </summary>
public static class SupportGraph
{
    /// <summary>A material's support numbers (<c>WearNTear.GetMaterialProperties</c>).</summary>
    public readonly struct Material
    {
        public readonly float Max, Min, HorizontalLoss, VerticalLoss;
        public Material(float max, float min, float horizontalLoss, float verticalLoss)
        {
            Max = max; Min = min; HorizontalLoss = horizontalLoss; VerticalLoss = verticalLoss;
        }
    }

    /// <summary>MaterialType.Wood: the railed bridge kits' posts and decks.</summary>
    public static readonly Material Wood = new(100f, 10f, 0.2f, 0.125f);
    /// <summary>MaterialType.Marble: the black marble pieces.</summary>
    public static readonly Material Marble = new(1500f, 100f, 0.5f, 0.125f);
    /// <summary>MaterialType.Stone: a staircase's stone pieces (stone_stair, stone_floor_2x2, stone_wall_2x1, stone_arch).</summary>
    public static readonly Material Stone = new(1000f, 100f, 1f, 0.125f);
    /// <summary>MaterialType.Iron: iron_floor_2x2, iron_wall_2x2.</summary>
    public static readonly Material Iron = new(1500f, 20f, 1f / 13f, 1f / 13f);

    /// <summary>The 0.1 m WearNTear adds to every distance it charges.</summary>
    public const float DistanceMargin = 0.1f;
    /// <summary>How far WearNTear grows each collider box, a side, for its overlaps (0.3 m a box): what UpdateSupport touches and what the ground contact test meets.</summary>
    public const float Reach = 0.15f;
    /// <summary>A support point counts if it lies no higher than this over the piece's centre.</summary>
    public const float LevelTolerance = 0.05f;
    /// <summary>Two support points this far apart or more, across, carry a piece between them at their average.</summary>
    public const float OppositeDegrees = 100f;

    /// <summary>One piece: its centre (the transform the game measures from), its collider's box, its material, and whether
    /// its grown box touches the terrain. A piece turned about the vertical (a staircase's) carries its own axes, its centre
    /// of mass apart from its transform where the prefab's is (WearNTear.m_comOffset) and its box about its transform;
    /// <see cref="Min"/> and <see cref="Max"/> are then the box's bounds on the world's axes.</summary>
    public readonly struct Piece
    {
        public readonly Vector3 Centre, Min, Max;
        public readonly Material Material;
        public readonly bool Grounded;
        /// <summary>Its transform, which a neighbour may measure from where that is nearer than its centre of mass
        /// (UpdateSupport: the lesser of the two distances).</summary>
        public readonly Vector3 Origin;
        /// <summary>Turned: its box's middle, half its size on its own axes, and its own x and z across the world's x and z.</summary>
        internal readonly bool Turned;
        /// <summary>Whether it overlaps for what it touches (the terrain, its neighbours) with its bounds on the world's axes,
        /// <see cref="Min"/> to <see cref="Max"/>, as the game does for a collider that is not a box (a convex mesh:
        /// WearNTear.SetupColliders takes its Collider.bounds, unturned); a neighbour still meets its own box.</summary>
        public readonly bool WorldBounds;
        internal readonly Vector3 Box, Half;
        internal readonly Vector2 X, Z;
        /// <summary>Its box colliders where they are pitched inside its prefab (<see cref="BuriedPieces.PitchedBox"/>), on the
        /// world's axes; null for a piece that is one box turned only about the vertical. <see cref="Min"/> and
        /// <see cref="Max"/> are then the box round them.</summary>
        internal readonly Part[]? Parts;

        public Piece(Vector3 centre, Vector3 min, Vector3 max, Material material, bool grounded)
        {
            Centre = centre; Min = min; Max = max; Material = material; Grounded = grounded;
            Origin = centre; Turned = false; WorldBounds = false; Box = (min + max) * 0.5f; Half = (max - min) * 0.5f; X = new Vector2(1f, 0f); Z = new Vector2(0f, 1f);
            Parts = null;
        }

        /// <summary>A piece at <paramref name="origin"/> turned <paramref name="yawDegrees"/> about the vertical (the game's
        /// Quaternion.Euler(0, yaw, 0)), its box <paramref name="localMin"/>..<paramref name="localMax"/> and its centre of
        /// mass <paramref name="comOffset"/> on its own axes; <paramref name="worldBounds"/> for a collider that is not a box
        /// (<see cref="WorldBounds"/>).</summary>
        public Piece(Vector3 origin, Vector3 comOffset, float yawDegrees, Vector3 localMin, Vector3 localMax, Material material, bool grounded,
            bool worldBounds = false)
        {
            Vector2 x = StairLayout.Right(yawDegrees), z = StairLayout.Forward(yawDegrees);
            Vector3 box = At(origin, x, z, (localMin + localMax) * 0.5f), half = (localMax - localMin) * 0.5f;
            float hx = Mathf.Abs(x.x) * half.x + Mathf.Abs(z.x) * half.z, hz = Mathf.Abs(x.y) * half.x + Mathf.Abs(z.y) * half.z;
            Material = material; Grounded = grounded; Origin = origin; Turned = true; WorldBounds = worldBounds;
            X = x; Z = z; Box = box; Half = half;
            Centre = At(origin, x, z, comOffset);
            Parts = null;
            Min = new Vector3(box.x - hx, box.y - half.y, box.z - hz);
            Max = new Vector3(box.x + hx, box.y + half.y, box.z + hz);
        }

        /// <summary>A piece at <paramref name="origin"/> turned <paramref name="yawDegrees"/> about the vertical, made of the box
        /// colliders <paramref name="pitched"/>, each pitched inside its prefab (<see cref="BuriedPieces.PitchedBox"/>), its
        /// centre of mass <paramref name="comOffset"/> on its own axes.</summary>
        public Piece(Vector3 origin, Vector3 comOffset, float yawDegrees, IReadOnlyList<BuriedPieces.PitchedBox> pitched, Material material, bool grounded)
        {
            Vector2 x = StairLayout.Right(yawDegrees), z = StairLayout.Forward(yawDegrees);
            Material = material; Grounded = grounded; Origin = origin; Turned = true; WorldBounds = false;
            X = x; Z = z;
            Centre = At(origin, x, z, comOffset);
            Parts = new Part[pitched.Count];
            Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), max = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < pitched.Count; i++)
            {
                BuriedPieces.PitchedBox b = pitched[i];
                float turn = b.PitchDegrees * Mathf.Deg2Rad, cos = Mathf.Cos(turn), sin = Mathf.Sin(turn);
                Vector3 Way(Vector3 local) => new(x.x * local.x + z.x * local.z, local.y, x.y * local.x + z.y * local.z);
                var part = new Part(At(origin, x, z, b.Centre), b.Half, Way(new Vector3(cos, sin, 0f)), Way(new Vector3(-sin, cos, 0f)),
                    Way(new Vector3(0f, 0f, 1f)));
                Parts[i] = part;
                min = new Vector3(Mathf.Min(min.x, part.Min.x), Mathf.Min(min.y, part.Min.y), Mathf.Min(min.z, part.Min.z));
                max = new Vector3(Mathf.Max(max.x, part.Max.x), Mathf.Max(max.y, part.Max.y), Mathf.Max(max.z, part.Max.z));
            }
            Min = min; Max = max;
            Box = (min + max) * 0.5f; Half = (max - min) * 0.5f;
        }

        private static Vector3 At(Vector3 origin, Vector2 x, Vector2 z, Vector3 local) =>
            new(origin.x + x.x * local.x + z.x * local.z, origin.y + local.y, origin.z + x.y * local.x + z.y * local.z);
    }

    /// <summary>One box of a piece on the world's axes, its middle, half its size along each of its own axes
    /// (<see cref="U"/>, <see cref="V"/>, <see cref="W"/>, unit and square to each other), and the box round it.</summary>
    internal readonly struct Part
    {
        public readonly Vector3 Centre, Half, U, V, W, Min, Max;
        public Part(Vector3 centre, Vector3 half, Vector3 u, Vector3 v, Vector3 w)
        {
            Centre = centre; Half = half; U = u; V = v; W = w;
            Vector3 reach = new(
                Mathf.Abs(u.x) * half.x + Mathf.Abs(v.x) * half.y + Mathf.Abs(w.x) * half.z,
                Mathf.Abs(u.y) * half.x + Mathf.Abs(v.y) * half.y + Mathf.Abs(w.y) * half.z,
                Mathf.Abs(u.z) * half.x + Mathf.Abs(v.z) * half.y + Mathf.Abs(w.z) * half.z);
            Min = centre - reach; Max = centre + reach;
        }
        /// <summary>The same box grown <paramref name="by"/> on every side, on its own axes (WearNTear.SetupColliders).</summary>
        public Part Grown(float by) => new(Centre, Half + new Vector3(by, by, by), U, V, W);
        public float Along(int axis, Vector3 p) => Dot(p - Centre, Axis(axis));
        public Vector3 Axis(int axis) => axis == 0 ? U : axis == 1 ? V : W;
        public float HalfOn(int axis) => axis == 0 ? Half.x : axis == 1 ? Half.y : Half.z;
        /// <summary>Its corner <paramref name="i"/> (0 to 7).</summary>
        public Vector3 Corner(int i) => Centre + U * ((i & 1) == 0 ? -Half.x : Half.x) + V * ((i & 2) == 0 ? -Half.y : Half.y)
            + W * ((i & 4) == 0 ? -Half.z : Half.z);
    }

    /// <summary>A piece's boxes on the world's axes: its pitched ones (<see cref="Piece.Parts"/>), or its one box: on the
    /// world's axes where it is not turned, or where it <paramref name="overlaps"/> with its bounds on them
    /// (<see cref="Piece.WorldBounds"/>: a neighbour still meets its own box), else turned about the vertical.</summary>
    private static Part[] PartsOf(in Piece p, bool overlaps)
    {
        if (p.Parts != null) return p.Parts;
        if (!p.Turned || (overlaps && p.WorldBounds))
            return new[] { new Part((p.Min + p.Max) * 0.5f, (p.Max - p.Min) * 0.5f, new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f)) };
        return new[] { new Part(p.Box, new Vector3(p.Half.x, p.Half.y, p.Half.z), new Vector3(p.X.x, 0f, p.X.y), new Vector3(0f, 1f, 0f), new Vector3(p.Z.x, 0f, p.Z.y)) };
    }

    /// <summary>Where a piece's box, grown <see cref="Reach"/> a side, stands against the terrain (<see cref="Against"/>).</summary>
    public enum Terrain
    {
        /// <summary>Some of the heights read are not known.</summary>
        Unknown,
        /// <summary>Wholly over the terrain: its grown box touches none of it.</summary>
        Over,
        /// <summary>The terrain passes through its grown box: the game grounds it.</summary>
        Meets,
        /// <summary>Wholly in the ground: the terrain's mesh is over its grown box everywhere, which touches none of the mesh's
        /// triangles (a non-convex mesh is met only by its triangles), so the game does not ground it.</summary>
        Under,
    }

    /// <summary>
    /// The one owner of terrain contact (docs/OWNERS.md): where a piece whose box runs from <paramref name="bottom"/> to
    /// <paramref name="top"/>, grown <see cref="Reach"/> as the game grows it (WearNTear.SetupColliders, UpdateSupport's
    /// OverlapBox), stands against terrain whose heights over its footprint (grown too) run from <paramref name="lowest"/> to
    /// <paramref name="highest"/>: over it, meeting it (the surface is continuous over a convex footprint, so it passes
    /// through the grown box wherever its range overlaps the box's), or wholly in the ground; unknown where any of them is NaN.
    /// A point or an edge is read as a footprint of its own (a step's foot at its carrier's point, a stair's exit edge: its
    /// lowest for both, the face at its height for both bottom and top).
    /// </summary>
    public static Terrain Against(float lowest, float highest, float bottom, float top)
    {
        if (float.IsNaN(lowest) || float.IsNaN(highest) || float.IsNaN(bottom) || float.IsNaN(top)) return Terrain.Unknown;
        if (highest < bottom - Reach) return Terrain.Over;
        if (lowest > top + Reach) return Terrain.Under;
        return Terrain.Meets;
    }

    /// <summary>
    /// The sloped contact test, the one terrain contact for a box pitched off the level (<see cref="Part"/>, already grown as
    /// the game grows it): where it stands against the game's terrain mesh on <paramref name="vertex"/>'s whole-metre
    /// vertices, read exactly, as <see cref="Against(float, float, float, float)"/> reads a level box: wholly over it where the
    /// ground lies under every point of the box, wholly in the ground where it lies over every point, else meeting it (the box
    /// is convex and the surface continuous). The ground less the height is flat over each of the mesh's triangles and falls
    /// with height, so it is highest and lowest at a corner of the box, where an edge of the box crosses one of the mesh's
    /// upright cell planes (x = n, z = n, x + z = n), or at the box's top or bottom over one of the mesh's vertices. Ground
    /// not known is left out of the reading where <paramref name="skipUnknown"/> (none known: <see cref="Terrain.Unknown"/>),
    /// else any read unknown is <see cref="Terrain.Unknown"/>.
    /// </summary>
    internal static Terrain Against(in Part box, Func<int, int, float> vertex, bool skipUnknown)
    {
        float low = float.PositiveInfinity, high = float.NegativeInfinity;
        bool unknown = false;
        void Take(float x, float y, float z) => Over(BuriedPieces.MeshHeight(x, z, vertex), y);
        void Over(float ground, float y)
        {
            float d = ground - y;
            if (float.IsNaN(d)) { unknown = true; return; }
            if (d < low) low = d;
            if (d > high) high = d;
        }
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++) { corners[i] = box.Corner(i); Take(corners[i].x, corners[i].y, corners[i].z); }
        for (int i = 0; i < 8; i++)
            for (int bit = 1; bit < 8; bit <<= 1)
            {
                if ((i & bit) != 0) continue;
                Vector3 a = corners[i], d = corners[i | bit] - a;
                Cross(a, d, a.x, d.x);
                Cross(a, d, a.z, d.z);
                Cross(a, d, a.x + a.z, d.x + d.z);
            }
        void Cross(Vector3 a, Vector3 d, float start, float change)
        {
            if (Mathf.Abs(change) < 1e-6f) return;
            float end = start + change;
            for (int n = Mathf.CeilToInt(Mathf.Min(start, end)); n <= Mathf.FloorToInt(Mathf.Max(start, end)); n++)
            {
                float t = (n - start) / change;
                if (t <= 0f || t >= 1f) continue;
                Vector3 p = a + d * t;
                Take(p.x, p.y, p.z);
            }
        }
        for (int i = Mathf.CeilToInt(box.Min.x); i <= Mathf.FloorToInt(box.Max.x); i++)
            for (int k = Mathf.CeilToInt(box.Min.z); k <= Mathf.FloorToInt(box.Max.z); k++)
                if (Upright(box, i, k, out float bottom, out float top))
                {
                    // A vertex is read as itself (the mesh between vertices reads a cell's other corners too).
                    float h = vertex(i, k);
                    Over(h, bottom);
                    Over(h, top);
                }
        if (unknown && !skipUnknown) return Terrain.Unknown;
        if (low > high) return Terrain.Unknown;
        // As a level box (Against(lowest, highest, bottom, top)): over where the ground is under its whole grown box, in
        // the ground where it is over it.
        if (high < 0f) return Terrain.Over;
        if (low > 0f) return Terrain.Under;
        return Terrain.Meets;
    }

    /// <summary>Where the upright line through (<paramref name="x"/>, <paramref name="z"/>) runs inside <paramref name="box"/>,
    /// from <paramref name="bottom"/> to <paramref name="top"/>; false where it misses it.</summary>
    private static bool Upright(in Part box, float x, float z, out float bottom, out float top)
    {
        bottom = float.NegativeInfinity; top = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 n = box.Axis(axis);
            float half = box.HalfOn(axis);
            // (x - c.x) n.x + (y - c.y) n.y + (z - c.z) n.z within +-half.
            float across = (x - box.Centre.x) * n.x + (z - box.Centre.z) * n.z;
            if (Mathf.Abs(n.y) < 1e-6f)
            {
                if (Mathf.Abs(across) > half) return false;
                continue;
            }
            float y0 = box.Centre.y + (-half - across) / n.y, y1 = box.Centre.y + (half - across) / n.y;
            bottom = Mathf.Max(bottom, Mathf.Min(y0, y1));
            top = Mathf.Min(top, Mathf.Max(y0, y1));
        }
        return bottom <= top;
    }

    /// <summary>
    /// Where a support-rule piece stands against the terrain (<see cref="Against"/>): its own overlap box (its box on its own
    /// axes, or its bounds on the world's axes where it overlaps with them, <see cref="Piece.WorldBounds"/>: the box it meets
    /// its neighbours with), its footprint grown <see cref="Reach"/>, over the game's terrain mesh on
    /// <paramref name="vertex"/>'s whole-metre vertices, read exactly (<see cref="BuriedPieces.RangeOver"/>). Ground not known
    /// is left out of the reading (a piece meets the ground that is known; none known: <see cref="Terrain.Unknown"/>): a
    /// structure on ground not known lays nothing before it is read (StructureGround.FirstOnUnknownGround, StructureGround.NotLaid).
    /// </summary>
    public static Terrain OnTerrain(in Piece piece, Func<int, int, float> vertex) => OnTerrain(piece, vertex, skipUnknown: true);

    /// <summary><see cref="OnTerrain(in Piece, Func{int, int, float})"/>, ground not known left out of the reading where
    /// <paramref name="skipUnknown"/> (else any read unknown is <see cref="Terrain.Unknown"/>). A piece of pitched boxes
    /// (<see cref="Piece.Parts"/>) meets the terrain where any of them, grown <see cref="Reach"/> on its own axes, does, and
    /// is wholly in the ground where every one is (<see cref="Against(in Part, Func{int, int, float}, bool)"/>, the sloped
    /// contact test); one level box is read as before.</summary>
    internal static Terrain OnTerrain(in Piece piece, Func<int, int, float> vertex, bool skipUnknown)
    {
        if (piece.Parts != null)
        {
            bool unknown = false, over = false;
            foreach (Part part in piece.Parts)
            {
                Terrain t = Against(part.Grown(Reach), vertex, skipUnknown);
                if (t == Terrain.Meets) return Terrain.Meets;
                if (t == Terrain.Unknown) unknown = true;
                else if (t == Terrain.Over) over = true;
            }
            return unknown ? Terrain.Unknown : over ? Terrain.Over : Terrain.Under;
        }
        Vector2[] footprint;
        if (!piece.Turned || piece.WorldBounds)
        {
            float x0 = piece.Min.x - Reach, x1 = piece.Max.x + Reach, z0 = piece.Min.z - Reach, z1 = piece.Max.z + Reach;
            footprint = new[] { new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x0, z1) };
        }
        else
        {
            Vector2 at = new(piece.Box.x, piece.Box.z), x = piece.X * (piece.Half.x + Reach), z = piece.Z * (piece.Half.z + Reach);
            footprint = new[] { at - x - z, at + x - z, at + x + z, at - x + z };
        }
        var (lowest, highest) = BuriedPieces.RangeOver(footprint, vertex, skipUnknown);
        return Against(lowest, highest, piece.Min.y, piece.Max.y);
    }

    /// <summary>Whether <paramref name="a"/>'s box grown <see cref="Reach"/> a side (what UpdateSupport overlaps) meets
    /// <paramref name="b"/>'s: the one test of what a piece touches (<see cref="Settle"/>,
    /// <see cref="ViaductSupport.Held.ThroughUnread"/>).</summary>
    internal static bool Touches(in Piece a, in Piece b) => Touch(a, b);

    private static bool Touch(in Piece a, in Piece b)
    {
        if (a.Parts != null || b.Parts != null)
        {
            foreach (Part q in PartsOf(b, overlaps: false))
                if (Meets(a, q)) return true;
            return false;
        }
        if (!a.Turned && !b.Turned) return TouchUnturned(a, b);
        if (a.Box.y - a.Half.y - Reach > b.Box.y + b.Half.y || b.Box.y - b.Half.y > a.Box.y + a.Half.y + Reach) return false;
        // Two boxes turned about the vertical: apart where some axis of either, across, separates them. One that overlaps with
        // its world bounds is that box, unturned.
        Vector2 ax = a.WorldBounds ? new Vector2(1f, 0f) : a.X, az = a.WorldBounds ? new Vector2(0f, 1f) : a.Z;
        Vector3 half = a.WorldBounds ? (a.Max - a.Min) * 0.5f : a.Half;
        return !Apart(a.Box, half, ax, az, b, ax) && !Apart(a.Box, half, ax, az, b, az)
            && !Apart(a.Box, half, ax, az, b, b.X) && !Apart(a.Box, half, ax, az, b, b.Z);
    }

    /// <summary>Whether <paramref name="n"/>, across, separates a grown box (its middle <paramref name="at"/>, half its size
    /// <paramref name="half"/> on its axes <paramref name="ax"/>, <paramref name="az"/>) from <paramref name="b"/>'s.</summary>
    private static bool Apart(Vector3 at, Vector3 half, Vector2 ax, Vector2 az, in Piece b, Vector2 n)
    {
        float ra = (half.x + Reach) * Mathf.Abs(Vector2.Dot(ax, n)) + (half.z + Reach) * Mathf.Abs(Vector2.Dot(az, n));
        float rb = b.Half.x * Mathf.Abs(Vector2.Dot(b.X, n)) + b.Half.z * Mathf.Abs(Vector2.Dot(b.Z, n));
        return Mathf.Abs((b.Box.x - at.x) * n.x + (b.Box.z - at.z) * n.y) > ra + rb + 1e-5f;
    }

    /// <summary>Whether any of <paramref name="a"/>'s boxes, grown <see cref="Reach"/> on its own axes (what UpdateSupport
    /// overlaps), meets the box <paramref name="b"/>: apart where some axis of either, or a cross of one of each, separates
    /// them.</summary>
    private static bool Meets(in Piece a, in Part b)
    {
        foreach (Part own in PartsOf(a, overlaps: true))
            if (Overlap(own.Grown(Reach), b)) return true;
        return false;
    }

    private static bool Overlap(in Part a, in Part b)
    {
        if (a.Min.x > b.Max.x || b.Min.x > a.Max.x || a.Min.y > b.Max.y || b.Min.y > a.Max.y || a.Min.z > b.Max.z || b.Min.z > a.Max.z)
            return false;
        Vector3 t = b.Centre - a.Centre;
        for (int i = 0; i < 3; i++)
        {
            if (ApartOn(a, b, t, a.Axis(i))) return false;
            if (ApartOn(a, b, t, b.Axis(i))) return false;
        }
        for (int i = 0; i < 3; i++)
            for (int k = 0; k < 3; k++)
            {
                Vector3 u = a.Axis(i), v = b.Axis(k);
                var n = new Vector3(u.y * v.z - u.z * v.y, u.z * v.x - u.x * v.z, u.x * v.y - u.y * v.x);
                float length = Mathf.Sqrt(Dot(n, n));
                if (length < 1e-5f) continue;
                if (ApartOn(a, b, t, n * (1f / length))) return false;
            }
        return true;
    }

    private static bool ApartOn(in Part a, in Part b, Vector3 t, Vector3 n)
    {
        float ra = a.Half.x * Mathf.Abs(Dot(a.U, n)) + a.Half.y * Mathf.Abs(Dot(a.V, n)) + a.Half.z * Mathf.Abs(Dot(a.W, n));
        float rb = b.Half.x * Mathf.Abs(Dot(b.U, n)) + b.Half.y * Mathf.Abs(Dot(b.V, n)) + b.Half.z * Mathf.Abs(Dot(b.W, n));
        return Mathf.Abs(Dot(t, n)) > ra + rb + 1e-5f;
    }

    private static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

    /// <summary>The point of <paramref name="box"/> nearest <paramref name="p"/> (Collider.ClosestPoint).</summary>
    private static Vector3 Closest(in Part box, Vector3 p)
    {
        Vector3 at = box.Centre;
        for (int axis = 0; axis < 3; axis++)
            at += box.Axis(axis) * Mathf.Clamp(box.Along(axis, p), -box.HalfOn(axis), box.HalfOn(axis));
        return at;
    }

    private static bool TouchUnturned(in Piece a, in Piece b) =>
        a.Min.x - Reach <= b.Max.x && b.Min.x <= a.Max.x + Reach
        && a.Min.y - Reach <= b.Max.y && b.Min.y <= a.Max.y + Reach
        && a.Min.z - Reach <= b.Max.z && b.Min.z <= a.Max.z + Reach;

    /// <summary>The point of <paramref name="box"/> nearest <paramref name="p"/> (Collider.ClosestPoint).</summary>
    private static Vector3 Closest(in Piece box, Vector3 p)
    {
        if (!box.Turned)
            return new(Mathf.Clamp(p.x, box.Min.x, box.Max.x), Mathf.Clamp(p.y, box.Min.y, box.Max.y), Mathf.Clamp(p.z, box.Min.z, box.Max.z));
        Vector2 off = new(p.x - box.Box.x, p.z - box.Box.z);
        float x = Mathf.Clamp(Vector2.Dot(off, box.X), -box.Half.x, box.Half.x), z = Mathf.Clamp(Vector2.Dot(off, box.Z), -box.Half.z, box.Half.z);
        return new Vector3(box.Box.x + box.X.x * x + box.Z.x * z, Mathf.Clamp(p.y, box.Min.y, box.Max.y), box.Box.z + box.X.y * x + box.Z.y * z);
    }

    /// <summary>What one neighbour offers a piece, per unit of the neighbour's support.</summary>
    private readonly struct Offer
    {
        public readonly int From;
        /// <summary>The best of the horizontal offer and the angled one, a fraction of the neighbour's support.</summary>
        public readonly float Best;
        /// <summary>Its worth as a support point (vertical loss), or NaN when it is no support point.</summary>
        public readonly float Point;
        /// <summary>Where the support point lies across from the piece's centre (y dropped).</summary>
        public readonly Vector3 Across;
        public Offer(int from, float best, float point, Vector3 across) { From = from; Best = best; Point = point; Across = across; }
    }

    /// <summary>Vector3.Angle's degrees between two directions across, 0 when either is (near) zero, as Unity's.</summary>
    private static float Angle(Vector3 a, Vector3 b)
    {
        double den = Math.Sqrt((double)(a.x * a.x + a.y * a.y + a.z * a.z) * (b.x * b.x + b.y * b.y + b.z * b.z));
        if (den < 1e-15) return 0f;
        double cos = Math.Max(-1.0, Math.Min(1.0, (a.x * b.x + a.y * b.y + a.z * b.z) / den));
        return (float)(Math.Acos(cos) * 180.0 / Math.PI);
    }

    /// <summary>The support each piece settles at, in the order given. The one owner of Supports, foundations and burial (docs/OWNERS.md).</summary>
    public static float[] Settle(IReadOnlyList<Piece> pieces)
    {
        int n = pieces.Count;
        var offers = new List<Offer>[n];
        var pairs = new List<(int a, int b)>[n];
        var heldBy = new List<int>[n];   // the pieces each one offers support to
        for (int i = 0; i < n; i++) { offers[i] = new List<Offer>(); pairs[i] = new List<(int a, int b)>(); heldBy[i] = new List<int>(); }
        // What touches what: sorted along x, each piece against those whose x range can still meet its grown one.
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => pieces[a].Min.x.CompareTo(pieces[b].Min.x));
        for (int oi = 0; oi < n; oi++)
        {
            int i = order[oi];
            for (int oj = oi + 1; oj < n; oj++)
            {
                int j = order[oj];
                if (pieces[j].Min.x > pieces[i].Max.x + 2f * Reach) break;
                // Each overlaps its own grown box: for two unturned boxes the same either way.
                Offers(pieces, i, j, offers[i], heldBy[j]);
                Offers(pieces, j, i, offers[j], heldBy[i]);
            }
        }
        for (int i = 0; i < n; i++)
        {
            List<Offer> list = offers[i];
            for (int a = 0; a < list.Count; a++)
            {
                if (float.IsNaN(list[a].Point)) continue;
                for (int b = a + 1; b < list.Count; b++)
                {
                    if (float.IsNaN(list[b].Point)) continue;
                    // Vector3.Angle is 0 for a zero vector: a point straight under the centre pairs with nothing.
                    if (Angle(list[a].Across, list[b].Across) >= OppositeDegrees) pairs[i].Add((a, b));
                }
            }
        }

        // Every offer is a fraction of a neighbour's support, so support only grows from the grounded pieces out to its one
        // fixed point: a piece is looked at again whenever one it rests on gains.
        var support = new float[n];
        var queued = new bool[n];
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (!pieces[i].Grounded) continue;
            support[i] = pieces[i].Material.Max;
            foreach (int k in heldBy[i]) if (!queued[k] && !pieces[k].Grounded) { queued[k] = true; queue.Enqueue(k); }
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            queued[i] = false;
            List<Offer> list = offers[i];
            float s = 0f;
            foreach (Offer o in list) s = Mathf.Max(s, support[o.From] * o.Best);
            foreach (var (a, b) in pairs[i])
            {
                float avg = (support[list[a].From] * list[a].Point + support[list[b].From] * list[b].Point) * 0.5f;
                if (avg > s) s = avg;
            }
            s = Mathf.Min(s, pieces[i].Material.Max);
            if (s <= support[i] + 1e-3f) continue;
            support[i] = s;
            foreach (int k in heldBy[i]) if (!queued[k] && !pieces[k].Grounded) { queued[k] = true; queue.Enqueue(k); }
        }
        return support;
    }

    /// <summary>What piece <paramref name="from"/> offers piece <paramref name="to"/> where <paramref name="to"/>'s grown boxes
    /// touch it: once, or where either is made of pitched boxes (<see cref="Piece.Parts"/>), once for each of
    /// <paramref name="from"/>'s boxes touched, each with its own support point, as UpdateSupport takes one for each collider
    /// its overlap finds.</summary>
    private static void Offers(IReadOnlyList<Piece> pieces, int to, int from, List<Offer> offers, List<int> heldBy)
    {
        Piece me = pieces[to], other = pieces[from];
        if (me.Parts == null && other.Parts == null)
        {
            if (Touch(me, other)) AddOffer(pieces, to, from, offers, heldBy, null);
            return;
        }
        foreach (Part q in PartsOf(other, overlaps: false))
            if (Meets(me, q)) AddOffer(pieces, to, from, offers, heldBy, q);
    }

    /// <summary>What piece <paramref name="from"/> offers piece <paramref name="to"/>, which it touches (at its box
    /// <paramref name="touched"/> where given).</summary>
    private static void AddOffer(IReadOnlyList<Piece> pieces, int to, int from, List<Offer> offers, List<int> heldBy, Part? touched)
    {
        Piece me = pieces[to], other = pieces[from];
        // UpdateSupport measures to the neighbour's centre of mass or its transform, whichever is nearer.
        float d = Mathf.Min(Vector3.Distance(me.Centre, other.Centre), Vector3.Distance(me.Centre, other.Origin)) + DistanceMargin;
        float best = 1f - me.Material.HorizontalLoss * d;
        float point = float.NaN;
        Vector3 across = Vector3.zero;
        Vector3 at = touched is Part q ? Closest(q, me.Centre) : Closest(other, me.Centre);
        if (at.y < me.Centre.y + LevelTolerance)
        {
            Vector3 off = at - me.Centre;
            float length = Mathf.Sqrt(off.x * off.x + off.y * off.y + off.z * off.z);
            float downY = length > 1e-5f ? off.y / length : 0f;
            if (downY < 0f)
            {
                float t = (float)Math.Acos(1f - Mathf.Abs(downY)) / (Mathf.PI / 2f);
                best = Mathf.Max(best, 1f - Mathf.Lerp(me.Material.HorizontalLoss, me.Material.VerticalLoss, t) * d);
            }
            point = 1f - me.Material.VerticalLoss * d;
            across = new Vector3(at.x - me.Centre.x, 0f, at.z - me.Centre.z);
        }
        offers.Add(new Offer(from, best, point, across));
        heldBy.Add(to);
    }
}
