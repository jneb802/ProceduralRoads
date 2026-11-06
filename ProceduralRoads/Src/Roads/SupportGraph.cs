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
/// slabs and half-cubes. A viaduct's are on one frame (its along, across and up), turned square to the deck; a
/// staircase's are each turned about the vertical (<see cref="Piece(Vector3, Vector3, float, Vector3, Vector3, Material, bool)"/>).
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

    /// <summary>MaterialType.Marble: the black marble pieces.</summary>
    public static readonly Material Marble = new(1500f, 100f, 0.5f, 0.125f);
    /// <summary>MaterialType.Stone: a staircase's stone pieces (stone_stair, stone_floor_2x2, stone_wall_2x1, stone_arch).</summary>
    public static readonly Material Stone = new(1000f, 100f, 1f, 0.125f);
    /// <summary>MaterialType.Iron: iron_floor_2x2, iron_wall_2x2.</summary>
    public static readonly Material Iron = new(1500f, 20f, 1f / 13f, 1f / 13f);

    /// <summary>The 0.1 m WearNTear adds to every distance it charges.</summary>
    public const float DistanceMargin = 0.1f;
    /// <summary>What UpdateSupport grows a piece's bounds by, a side, to find what it touches (0.3 m a box).</summary>
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

        public Piece(Vector3 centre, Vector3 min, Vector3 max, Material material, bool grounded)
        {
            Centre = centre; Min = min; Max = max; Material = material; Grounded = grounded;
            Origin = centre; Turned = false; WorldBounds = false; Box = (min + max) * 0.5f; Half = (max - min) * 0.5f; X = new Vector2(1f, 0f); Z = new Vector2(0f, 1f);
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
            Min = new Vector3(box.x - hx, box.y - half.y, box.z - hz);
            Max = new Vector3(box.x + hx, box.y + half.y, box.z + hz);
        }

        private static Vector3 At(Vector3 origin, Vector2 x, Vector2 z, Vector3 local) =>
            new(origin.x + x.x * local.x + z.x * local.z, origin.y + local.y, origin.z + x.y * local.x + z.y * local.z);
    }

    /// <summary>Whether <paramref name="a"/>'s box grown <see cref="Reach"/> a side (what UpdateSupport overlaps) meets
    /// <paramref name="b"/>'s.</summary>
    private static bool Touch(in Piece a, in Piece b)
    {
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

    /// <summary>The support each piece settles at, in the order given.</summary>
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
                if (Touch(pieces[i], pieces[j])) AddOffer(pieces, i, j, offers[i], heldBy[j]);
                if (Touch(pieces[j], pieces[i])) AddOffer(pieces, j, i, offers[j], heldBy[i]);
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

    /// <summary>What piece <paramref name="from"/> offers piece <paramref name="to"/>, which it touches.</summary>
    private static void AddOffer(IReadOnlyList<Piece> pieces, int to, int from, List<Offer> offers, List<int> heldBy)
    {
        Piece me = pieces[to], other = pieces[from];
        // UpdateSupport measures to the neighbour's centre of mass or its transform, whichever is nearer.
        float d = Mathf.Min(Vector3.Distance(me.Centre, other.Centre), Vector3.Distance(me.Centre, other.Origin)) + DistanceMargin;
        float best = 1f - me.Material.HorizontalLoss * d;
        float point = float.NaN;
        Vector3 across = Vector3.zero;
        Vector3 at = Closest(other, me.Centre);
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
