using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The one owner of a crossing's ends: where the road meets every crossing, of every kind and kit alike (a viaduct over a
/// valley or a river, a bridge or a ford's span in any kit, a waded or raised ford). An end, as the network and the road
/// see it, is a point and a height on the road: the foot of the stairs laid down from the deck to it, the deck's edge
/// where it has none, the bank where the crossing is road. It is decided while the road is searched
/// (<see cref="MeetEnds"/>), among the ways the road can meet it:
/// <list type="number">
/// <item>the whole run's foot, the stairs marched down from the deck until they land (<see cref="Natural"/>; a viaduct's
/// sit nearest the ground the road arrives on, <see cref="Decide"/>);</item>
/// <item>the foot of each row up the flight, the stairs laid only down to it, its ground filled level with it (the landing
/// step a staircase's flight takes too, RoadStairRuns.Land), down to the deck's edge itself, met level with no stairs;</item>
/// <item>each of those again as earthwork, the landing filled over ground under the road's floor.</item>
/// </list>
/// A way is a candidate only where the road reaches it: its foot over the road's floor, the road brought in along its
/// line or from its landing pad on that line, a landing a road's width deep between the foot and a road met from the side
/// (<see cref="Approach"/>: an end never leads away from the road), and the ground from there out within the
/// earthwork cap and the grade cap of a road level with the foot (RoadStairRuns.Land, which raises any other road on the
/// landing to it). Every candidate is priced (<see cref="Price"/>: stairs cost, meeting level costs nothing, earthwork
/// costs less than the stairs it saves, so a higher deck is approached flat; a low deck is met by a step or two,
/// <see cref="Low"/>) and the cheapest is taken and saved with the crossing
/// (<see cref="RoadCrossing.FromEnd"/>); the road runs to its foot on its line, levelled over its last
/// <see cref="ApronLength"/> to its height; every kit lays its stairs down to it (BridgeLayout.LayEndStairs). Two
/// structures' facing ends with too little ground between them for each end's own are met together, on one landing
/// between them (<see cref="Share"/>). A crossing with an end no way reaches is searched round.
/// </summary>
public static class StructureEnds
{
    /// <summary>The road's last stretch before a structure's end that is levelled to the end's height and centred on its
    /// line (extending the earthwork slightly, the last ~6-8 m).</summary>
    public const float ApronLength = 7f;

    /// <summary>
    /// How far the road runs on past its meeting point, under the structure's joining piece: the deck's end module where
    /// the end is flush, the bottom step where it has stairs. The stairs' rule, generalised: a staircase's joining pieces sit ON the road, so the road is
    /// levelled right up under them, and a deck end does the same. The terrain is cut or filled to the meeting height there
    /// and the joining piece stands on it, so no gap is left where the road's earthwork stops.
    /// </summary>
    public const float JoinOverlap = 2f;

    /// <summary>The middle of the end's step <paramref name="k"/> (0 at the edge): its origin, a half run out from the joint above it. The one owner of end steps of bridges, viaducts and stairs meeting a road (docs/OWNERS.md).</summary>
    public static Vector2 StepCentre(Vector2 edge, Vector2 up, int k) => edge - up * (BridgeLayout.StairHalfRun + k * StairLayout.PieceRun);

    /// <summary>The joint <paramref name="steps"/> steps out from the edge: the foot of that many steps (0 is the edge). The one owner of end steps of bridges, viaducts and stairs meeting a road (docs/OWNERS.md).</summary>
    public static Vector2 StepJoint(Vector2 edge, Vector2 up, int steps) => edge - up * (StairLayout.PieceRun * steps);

    /// <summary>The base of the end's step <paramref name="k"/>, <paramref name="height"/> being the walking surface at the edge:
    /// k + 1 rises under it. The one owner of end steps of bridges, viaducts and stairs meeting a road (docs/OWNERS.md).</summary>
    public static float StepBase(float height, int k) => height - StairLayout.PieceRise * (k + 1);

    /// <summary>The base of a step <paramref name="k"/> as the bridge kits lay it: one rise off the deck, then k more. The one owner of end steps of bridges, viaducts and stairs meeting a road (docs/OWNERS.md).</summary>
    public static float StepFoot(float deckHeight, int k) => deckHeight - StairLayout.PieceRise - k * StairLayout.PieceRise;

    /// <summary>One end of a structure as it meets the road.</summary>
    public readonly struct End
    {
        /// <summary>The middle of the structure's end edge: where its walking surface stops, or its stairs leave it.</summary>
        public readonly Vector2 Edge;
        /// <summary>The way onto the structure, along its line.</summary>
        public readonly Vector2 Up;
        /// <summary>The structure's walking surface at the edge.</summary>
        public readonly float Height;
        /// <summary>Stairs down from the edge, each <see cref="StairLayout.PieceRise"/> down and
        /// <see cref="StairLayout.PieceRun"/> out; 0 where the road runs onto the structure flush.</summary>
        public readonly int Steps;
        /// <summary>The crossing is road here (a waded or raised ford, a span too short for a deck): the road runs on over
        /// it at its own height, nothing is laid and nothing held.</summary>
        public readonly bool Road;
        public End(Vector2 edge, Vector2 up, float height, int steps, bool road = false)
        {
            Edge = edge; Up = up; Height = height; Steps = steps; Road = road;
        }
        public float Rise => Steps * StairLayout.PieceRise;
        /// <summary>Where the road ends: the foot of the stairs, or the edge.</summary>
        public Vector2 Meet => StepJoint(Edge, Up, Steps);
        /// <summary>The road's height there.</summary>
        public float MeetHeight => Height - Rise;
        /// <summary>The far end of the road's levelled apron, on the structure's line.</summary>
        public Vector2 Apron => Meet - Up * ApronLength;
        /// <summary>Where the road ends: <see cref="JoinOverlap"/> past the meeting point, under the joining piece.</summary>
        public Vector2 Joint => Meet + Up * JoinOverlap;
        /// <summary>Whether <paramref name="p"/> lies on the stretch the road runs on under the joining piece (from the
        /// meeting point to <see cref="Joint"/>, <paramref name="halfWidth"/> either side of the line).</summary>
        public bool UnderTheJoint(Vector2 p, float halfWidth)
        {
            float along = Vector2.Dot(p - Meet, Up);
            Vector2 across = new(-Up.y, Up.x);
            return along >= -0.25f && along <= JoinOverlap + 0.25f && Mathf.Abs(Vector2.Dot(p - Meet, across)) <= halfWidth;
        }
        /// <summary>Whether <paramref name="p"/> lies on the road past where the paint stops, under the end's stairs (inward
        /// of <see cref="Meet"/>, their foot, up to <see cref="Joint"/>, <paramref name="halfWidth"/> either side of the
        /// line). That road is levelled for the bottom step to stand on but not painted: the paint stops at the stairs' foot.
        /// An end met level has none: its road is painted on under the deck's end like any road, so no grass grows at the
        /// deck's edge or up through its planks (the ground under the deck itself is cut below it, BridgeStructures.GroundUnderDeck).</summary>
        public bool UnderTheStairs(Vector2 p, float halfWidth)
        {
            if (Steps <= 0) return false;
            float along = Vector2.Dot(p - Meet, Up);
            Vector2 across = new(-Up.y, Up.x);
            return along > 0.25f && along <= JoinOverlap + 0.25f && Mathf.Abs(Vector2.Dot(p - Meet, across)) <= halfWidth;
        }
        public bool IsSet => Up != default;
        /// <summary>A structure's end the road is held to: set, and not road.</summary>
        public bool IsStructure => IsSet && !Road;
    }

    /// <summary>
    /// Which of a road's <paramref name="points"/> are stored unpainted (RoadSpatialGrid.RoadPoint.unpainted): those under
    /// the stairs at any of <paramref name="ends"/> (<see cref="End.UnderTheStairs"/>). The road there is levelled like any
    /// other, so the stairs stand on level ground, but its paint stops at the stairs' foot. An end met level paints its road
    /// on under the deck's end. Null where every point is painted.
    /// </summary>
    public static List<bool>? Unpainted(IReadOnlyList<Vector2> points, IReadOnlyList<End> ends, float halfWidth)
    {
        List<bool>? unpainted = null;
        for (int i = 0; i < points.Count; i++)
            foreach (End end in ends)
                if (end.IsSet && end.UnderTheStairs(points[i], halfWidth))
                {
                    unpainted ??= new List<bool>(new bool[points.Count]);
                    unpainted[i] = true;
                    break;
                }
        return unpainted;
    }

    /// <summary>
    /// The height the road arrives at, outward of <paramref name="from"/> along <paramref name="outward"/> over
    /// <see cref="ApronLength"/>: the mean of the ground there, every metre. Where the bank is a steep rim the road comes
    /// to on its top (for example 45.0 m at the bank point, 51.4 m eight metres out), this is the road's height, not
    /// the rim's foot.
    /// </summary>
    public static float Arrival(Vector2 from, Vector2 outward, Func<Vector2, float> ground)
    {
        float sum = 0f;
        int n = 0;
        for (float d = 0f; d <= ApronLength + 1e-3f; d += 1f, n++)
            sum += ground(from + outward * d);
        return sum / n;
    }

    /// <summary>
    /// An end at <paramref name="edge"/> whose walking surface is <paramref name="height"/>: the stairs, 0 to
    /// <paramref name="mostSteps"/> of them, whose foot sits nearest the ground the road arrives on beyond it (the mean
    /// over the apron from the foot on); none where that is within half a rise. Ties go to fewer steps.
    /// </summary>
    public static End Decide(Vector2 edge, Vector2 up, float height, Func<Vector2, float> ground, int mostSteps = BridgeLayout.MaxStairSteps)
    {
        int best = 0;
        float bestOff = float.MaxValue;
        for (int s = 0; s <= mostSteps; s++)
        {
            Vector2 foot = edge - up * (StairLayout.PieceRun * s);
            float off = Mathf.Abs(height - s * StairLayout.PieceRise - Arrival(foot, -up, ground));
            if (off < bestOff - 1e-3f) { best = s; bestOff = off; }
            // Past a flush end that already meets the arriving road, no stairs.
            if (s == 0 && off <= StairLayout.StairFitsFrom) break;
        }
        return new End(edge, up, height, best);
    }

    /// <summary>Whether a road can be laid at <paramref name="end"/>'s meeting height: under the floor every road on land is
    /// built up to (RoadConstants.DryRoadFloor) by less than half a rise (<see cref="StairLayout.StairFitsFrom"/>). The road
    /// stands on its floor there, so the end's bottom stair clips into it by up to that, its slope coming up out of the road
    /// (the flex a staircase's foot has onto a road), rather than the end being met a row up on a landing of fill. A foot
    /// further under it, down a bank into shallow water, is never met by the road.</summary>
    public static bool OverTheFloor(End end) => end.MeetHeight >= RoadConstants.DryRoadFloor - StairLayout.StairFitsFrom;

    /// <summary>
    /// A crossing's two ends as the road meets them, near (from-bank) end first: those decided and saved with it
    /// (<see cref="RoadCrossing.FromEnd"/>, <see cref="MeetEnds"/>), else as the bank takes them (<see cref="Natural"/>).
    /// Unset (default) where the crossing is road (a waded or raised ford). Every kit lays its stairs from these, and the
    /// generator, the paint rule and the checks read them.
    /// </summary>
    public static (End near, End far) Of(RoadCrossing crossing, WorldGenerator world)
    {
        if (crossing == null) return default;
        if (crossing.FromEnd is End near && crossing.ToEnd is End far) return near.IsStructure && far.IsStructure ? (near, far) : default;
        return Natural(crossing, world);
    }

    /// <summary>
    /// A crossing's two ends as the bank takes them, before the road meets them: the stairs from each deck end, near end
    /// first, in the deck the crossing is built with (a viaduct's, <see cref="DvergrViaduct.NaturalEnds"/>, else every other
    /// kit's alike, <see cref="BridgeLayout.NaturalEnds"/>). Unset (default) where the crossing is road: a waded or raised
    /// ford, or a span too short for a deck.
    /// </summary>
    public static (End near, End far) Natural(RoadCrossing crossing, WorldGenerator world)
    {
        if (crossing == null || world == null) return default;
        return BridgeStructures.IsViaduct(crossing, world)
            ? DvergrViaduct.NaturalEnds(crossing, world) : BridgeLayout.NaturalEnds(crossing, world);
    }

    /// <summary>
    /// Decides how the road as searched (<paramref name="path"/>) meets each end of every one of
    /// <paramref name="crossings"/>, records it on the crossing (<see cref="RoadCrossing.FromEnd"/>, saved with it) and brings
    /// the road to it: the path then runs to each end's foot on its line, the crossing's <see cref="RoadCrossing.FromIndex"/>
    /// and <see cref="RoadCrossing.ToIndex"/> at the two feet. A crossing that is road has its banks for ends, the path left
    /// as it is. A crossing shared with a road laid before keeps the ends that road met. Of a structure's end the cheapest
    /// way the road reaches is taken (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float)"/>, each way dry or else as earthwork within <paramref name="cap"/>);
    /// a structure's end facing the previous structure's with too little ground between them shares one landing with it
    /// (<see cref="Share"/>), both decided together. A stair step the search bought (<paramref name="isStep"/>) is never
    /// cut. Returns the first crossing with an end none of
    /// whose ways the road reaches, the path then as it was at that crossing; null where every end is met. A raised viaduct
    /// (DvergrViaduct.RaisedSteps) with an end no way reaches is lowered a step and every end met again from the road as it
    /// came, down to the deck at grade, so a raise never changes whether a viaduct is built.
    /// The one owner of end steps of bridges, viaducts and stairs meeting a road (docs/OWNERS.md).
    /// </summary>
    public static RoadCrossing? MeetEnds(List<Vector2> path, List<RoadCrossing> crossings, WorldGenerator world, float cap,
        Func<Vector2, Vector2, bool>? isStep)
    {
        var road = new List<Vector2>(path);
        var stood = crossings.ConvertAll(c => (c, c.FromIndex, c.ToIndex, c.FromEnd, c.ToEnd));
        while (true)
        {
            RoadCrossing? unmet = MeetEndsOnce(path, crossings, world, cap, isStep);
            if (unmet == null || Adopted(unmet) || !BridgeStructures.IsViaduct(unmet, world)) return unmet;
            int raised = DvergrViaduct.RaisedSteps(unmet, world);
            if (raised == 0) return unmet;
            unmet.RaiseAtMost = raised - 1;
            path.Clear();
            path.AddRange(road);
            foreach (var (c, from, to, fromEnd, toEnd) in stood) (c.FromIndex, c.ToIndex, c.FromEnd, c.ToEnd) = (from, to, fromEnd, toEnd);
        }
    }

    /// <summary><see cref="MeetEnds"/> with every crossing as it stands.</summary>
    private static RoadCrossing? MeetEndsOnce(List<Vector2> path, List<RoadCrossing> crossings, WorldGenerator world, float cap,
        Func<Vector2, Vector2, bool>? isStep)
    {
        float G(Vector2 p) => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
        var order = crossings.OrderBy(c => c.FromIndex).ToList();
        // A crossing's far end met on a landing shared with the next crossing's near end, decided with it (Share).
        var sharedFar = new Dictionary<RoadCrossing, Met>();
        // Last first, far end before near, so the path is changed only where no crossing still to do stands.
        for (int n = order.Count - 1; n >= 0; n--)
        {
            RoadCrossing c = order[n];
            c.FromRaised = c.ToRaised = null;
            c.FromEarthwork = c.ToEarthwork = c.FromPad = c.ToPad = c.FromStands = c.ToStands = c.FromShared = c.ToShared = false;
            bool adopt = Adopted(c);
            // Its ends decided afresh, as the bank takes them: a deck is built to ends once recorded (DvergrViaduct.DeckHeight).
            if (!adopt) c.FromEnd = c.ToEnd = null;
            var (near, far) = Natural(c, world);
            if (!near.IsSet)
            {
                c.FromEnd = new End(c.FromBank, c.Direction, G(c.FromBank), 0, road: true);
                c.ToEnd = new End(c.ToBank, -c.Direction, G(c.ToBank), 0, road: true);
                continue;
            }
            if (!sharedFar.TryGetValue(c, out Met farMet))
            {
                int after = n + 1 < order.Count ? order[n + 1].FromIndex : path.Count - 1;
                bool roadAfter = n + 1 < order.Count && !Natural(order[n + 1], world).near.IsSet;
                if (AtSite(adopt ? c.ToEnd!.Value : far, adopt, new Road(path, c.ToIndex, +1, Mathf.Max(after, c.ToIndex), roadAfter, isStep), G, cap) is not Met chosen)
                    return c;
                farMet = chosen;
                Shift(order, n + 1, Bring(path, farMet.Way, c.ToIndex, farMet.K, +1, out _));
            }
            RoadCrossing? prev = n > 0 ? order[n - 1] : null;
            End prevFar = prev != null ? Natural(prev, world).far : default;
            if (prev != null && !adopt && prevFar.IsSet && !Adopted(prev)
                && Share(path, prev.ToIndex, c.FromIndex, prevFar, near, G, cap, isStep) is var (landedFar, landedNear))
            {
                // Both ends on one landing: the road between the two crossings is the landing, from one foot to the other.
                int a = prev.ToIndex, b = c.FromIndex;
                path.RemoveRange(a, b - a + 1);
                path.InsertRange(a, new[] { landedFar.End.Meet, landedNear.End.Meet });
                int delta = 2 - (b - a + 1);
                c.FromIndex = a + 1;
                c.ToIndex += delta;
                Shift(order, n + 1, delta);
                sharedFar[prev] = landedFar;
                SetEnds(c, landedNear, farMet);
                continue;
            }
            int before = n > 0 ? order[n - 1].ToIndex : 0;
            bool roadBefore = n > 0 && !Natural(order[n - 1], world).near.IsSet;
            if (AtSite(adopt ? c.FromEnd!.Value : near, adopt, new Road(path, c.FromIndex, -1, Mathf.Min(before, c.FromIndex), roadBefore, isStep), G, cap) is not Met nearMet)
                return c;
            int nearDelta = Bring(path, nearMet.Way, c.FromIndex, nearMet.K, -1, out int foot);
            c.ToIndex += nearDelta;
            c.FromIndex = foot;
            Shift(order, n + 1, nearDelta);
            SetEnds(c, nearMet, farMet);
        }
        return null;
    }

    /// <summary>An end as its site alone meets it (<see cref="AtSite(End, Vector2, Func{Vector2, float}, float)"/>): the
    /// way taken, its price (<see cref="Price"/>), and whether it was met from its landing pad or by earthwork over
    /// water.</summary>
    public readonly record struct SiteEnd(End End, float Price, bool Pad, bool Earthwork);

    /// <summary>
    /// How an end is met by its site alone, with no road searched yet: the end as the bank takes it
    /// (<paramref name="natural"/>), its crossing's <paramref name="bank"/> and the ground beyond it along the end's line,
    /// the road come straight in along that line at the search's cell spacing (RoadPathfinder.CellSize). The one owner of
    /// the end rule (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float)"/>, which <see cref="MeetEnds"/> calls
    /// on a searched road), on that approach: its way and price; null where no way of meeting it reaches. A road searched later that comes in along the
    /// line meets it so; one come from the side may meet it another way (from its pad), or share a landing with the
    /// next structure (<see cref="Share"/>), which only a searched road decides.
    /// </summary>
    public static SiteEnd? AtSite(End natural, Vector2 bank, Func<Vector2, float> ground, float cap)
    {
        if (!natural.IsStructure) return null;
        Vector2 outward = -natural.Up;
        float reach = Vector2.Distance(natural.Edge, bank) + natural.Steps * StairLayout.PieceRun + ApronLength * 1.5f
            // The ease at the cap written inline, not RoadGrade.EaseLength: the game's Mono keeps the sum wider than float and
            // rounds once at the store, as this line always has.
            + RoadNetworkGenerator.Shaped(cap) / RoadGrade.Floored + 2f * PadLength;
        var path = new List<Vector2> { bank };
        for (float d = RoadPathfinder.CellSize; d <= reach + RoadPathfinder.CellSize; d += RoadPathfinder.CellSize)
            path.Add(bank + outward * d);
        return AtSite(natural, false, new Road(path, 0, +1, path.Count - 1, false, null), ground, cap) is Met m
            ? new SiteEnd(m.End, m.Price, m.Pad, m.Earthwork) : null;
    }

    /// <summary>
    /// A crossing's two ends as its site alone meets them (<see cref="AtSite(End, Vector2, Func{Vector2, float}, float)"/>),
    /// near end first, and whether both can be met: a crossing that is road (a waded or raised ford, a span too short for a
    /// deck) is met at its banks for nothing.
    /// </summary>
    public static (bool met, SiteEnd? near, SiteEnd? far) AtSite(RoadCrossing crossing, WorldGenerator world, float cap)
    {
        var (near, far) = Natural(crossing, world);
        if (!near.IsSet) return (true, null, null);
        float G(Vector2 p) => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
        SiteEnd? n = AtSite(near, crossing.FromBank, G, cap), f = AtSite(far, crossing.ToBank, G, cap);
        return (n != null && f != null, n, f);
    }

    /// <summary>Whether <paramref name="c"/> is shared with a road laid before that met its ends: it keeps them.</summary>
    private static bool Adopted(RoadCrossing c) => c.Shared && c.FromEnd is End { IsStructure: true } && c.ToEnd is End { IsStructure: true };

    /// <summary>Records <paramref name="near"/> and <paramref name="far"/>, as the road meets them, on
    /// <paramref name="c"/>.</summary>
    private static void SetEnds(RoadCrossing c, Met near, Met far)
    {
        (c.FromEnd, c.FromRaised, c.FromEarthwork, c.FromPad, c.FromStands, c.FromShared) = (near.End, near.Raised, near.Earthwork, near.Pad, near.Stands, near.Shared);
        (c.ToEnd, c.ToRaised, c.ToEarthwork, c.ToPad, c.ToStands, c.ToShared) = (far.End, far.Raised, far.Earthwork, far.Pad, far.Stands, far.Shared);
    }

    /// <summary>Moves the path indices of <paramref name="order"/>'s crossings from <paramref name="from"/> on by
    /// <paramref name="delta"/>.</summary>
    private static void Shift(List<RoadCrossing> order, int from, int delta)
    {
        if (delta == 0) return;
        for (int k = from; k < order.Count; k++) { order[k].FromIndex += delta; order[k].ToIndex += delta; }
    }

    /// <summary>
    /// The one owner of the end rule: how an end is met from <paramref name="road"/>, a searched road
    /// (<see cref="MeetEnds"/>) or the site's own straight approach (<see cref="AtSite(End, Vector2, Func{Vector2, float}, float)"/>).
    /// The cheapest of an end's ways (<see cref="Ways"/>, or only <paramref name="natural"/> where it is
    /// <paramref name="adopt"/>ed from the road that laid the crossing) the road reaches from its point <c>At</c> outward
    /// (<c>Step</c>) no further than <c>Bound</c> (a ford's bank where <c>RoadAtBound</c>): its foot over the road's floor (<see cref="OverTheFloor"/>), the road
    /// brought in along its line (<see cref="Approach"/>) and its landing within the caps (<see cref="Lands"/>), on dry
    /// ground or else as earthwork over water; priced by <see cref="Price"/>, ties to the earlier way. Null where none is.
    /// </summary>
    private static Met? AtSite(End natural, bool adopt, Road road, Func<Vector2, float> ground, float cap)
    {
        var (path, at, step, bound, roadAtBound, isStep) = road;
        float arrival = ArrivalOnPath(path, natural, at, step, bound, ground);
        bool low = !adopt && Low(natural, Mathf.Min(arrival, Mathf.Max(Arrival(natural.Edge, -natural.Up, ground), RoadConstants.DryRoadFloor)));
        var ways = adopt ? new List<(End, bool)> { (natural, false), (natural, true) } : Ways(natural, ground, low).ToList();
        float before = Walked(path, at, step, bound, natural.Edge, out var walked);
        // Each way the road is brought to, with the least it can cost (its stairs, climb, turn and detour, no earthwork):
        // landed cheapest first, so a way whose least already costs more than one landed is never landed.
        var reached = new List<(int order, End end, bool pad, List<Vector2> way, int k, bool stair, bool clear, float climb, float detour, float least)>();
        for (int w = 0; w < ways.Count; w++)
        {
            var (end, pad) = ways[w];
            if (!OverTheFloor(end) || Approach(path, end, at, step, bound, roadAtBound, pad, isStep) is not var (way, k, stair, clear)) continue;
            float climb = Mathf.Abs(end.MeetHeight - arrival);
            float detour = Vector2.Distance(end.Edge, end.Meet) + Length(way) - walked[k] - before;
            reached.Add((w, end, pad, way, k, stair, clear, climb, detour, Price(end.Steps, 0f, climb, pad || !clear, detour, low)));
        }
        Met? best = null;
        int bestOrder = int.MaxValue;
        foreach (var r in reached.OrderBy(r => r.least).ThenBy(r => r.order))
        {
            if (best is Met b && r.least > b.Price) break;
            bool overWater = false;
            if (Lands(path, r.way, r.stair ? -1 : r.k, step, r.end, natural, ground, cap, false, out float earth) is not { } raised)
            {
                overWater = true;
                if ((raised = Lands(path, r.way, r.stair ? -1 : r.k, step, r.end, natural, ground, cap, true, out earth)) == null) continue;
            }
            float price = Price(r.end.Steps, earth, r.climb, r.pad || !r.clear, r.detour, low);
            // Ties go to the earlier way.
            if (best is not Met better || price < better.Price - 1e-4f || (price < better.Price + 1e-4f && r.order < bestOrder))
            {
                best = new Met(r.end, r.way, r.k, raised, overWater, r.pad, !r.clear && !r.stair, price);
                bestOrder = r.order;
            }
        }
        return best;
    }

    /// <summary>The road an end is met from (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float)"/>): its
    /// points, the crossing's point on it (<c>At</c>), the way outward (<c>Step</c>), how far out it may be taken
    /// (<c>Bound</c>, a ford's bank where <c>RoadAtBound</c>), and the stair steps the search bought (<c>IsStep</c>),
    /// never cut. A searched road (<see cref="MeetEnds"/>), or a site's own straight approach along the end's line.</summary>
    private readonly record struct Road(List<Vector2> Path, int At, int Step, int Bound, bool RoadAtBound, Func<Vector2, Vector2, bool>? IsStep);

    /// <summary>An end as the road meets it (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float)"/>, <see cref="Share"/>): the end, the road brought to it
    /// (foot first) to the path's point <c>K</c>, the other roads its landing raises, whether it was met by earthwork over
    /// water, from its landing pad, where the road's last point stands (no point clear of it, <see cref="Approach"/>), or
    /// on a landing shared with the next structure's end, and its price.</summary>
    private readonly record struct Met(End End, List<Vector2> Way, int K, List<(RoadSpatialGrid.RoadPoint at, float to)> Raised,
        bool Earthwork, bool Pad, bool Stands, float Price, bool Shared = false);

    // ------------------------------------------------------- the price of a way

    /// <summary>What one stair step laid at an end costs: the unit every other cost is said in.</summary>
    public const float StepPrice = 1f;

    /// <summary>What earthwork costs, per step's worth: a metre of fill or cut under the road over an apron's length
    /// (<see cref="ApronLength"/>), the ground a step's 1 m rise would otherwise take. A quarter of a step: the road
    /// approaching a higher deck flat, on its fill or cut within the caps, is cheaper than the stairs down to it.</summary>
    public const float EarthworkPrice = 0.25f;

    /// <summary>What the road climbing or falling costs, per metre, from the end's foot to the height it arrives at
    /// (<see cref="ArrivalOnPath"/>): stairs laid down a bank the road then climbs back up are paid for twice.</summary>
    public const float ClimbPrice = 0.25f;

    /// <summary>What a road leaving an end off its line costs (from its landing pad, or where its last point stands): a
    /// little, so an end met on its line is taken where both are otherwise the same.</summary>
    public const float OffLinePrice = 0.25f;

    /// <summary>What each metre of road the way adds to the path costs: twenty metres for a step.</summary>
    public const float DetourPrice = 0.05f;

    /// <summary>How far at most a low structure's deck stands over the height the road arrives at (<see cref="Low"/>):
    /// about two and a half steps, a ford's span or a low bridge, met by one step or two.</summary>
    public const float LowDeckRise = 2.5f;

    /// <summary>How many steps up onto a low structure cost nothing (<see cref="StairPrice"/>): one or two, whichever
    /// brings the foot nearer the road arriving.</summary>
    public const int LowSteps = 2;

    /// <summary>What meeting a low structure with no step costs (<see cref="Low"/>): more than a step or two and their
    /// earthwork, so a low structure keeps its steps.</summary>
    public const float LowFlushPrice = 2f;

    /// <summary>Whether an end the bank takes as <paramref name="natural"/> is low: it has stairs, and its deck stands
    /// within <see cref="LowDeckRise"/> over <paramref name="arrival"/> (and not under it by more than a step's height,
    /// <see cref="StairLayout.StepUp"/>), the lower of the bank under the end (the ground along its line over an apron,
    /// <see cref="Arrival"/>) and the road arriving (<see cref="ArrivalOnPath"/>): a road coming down a slope to a deck
    /// lying low on its bank comes to a low structure. A low bridge or a ford's span looks best met by a step or two up
    /// onto it.</summary>
    public static bool Low(End natural, float arrival) =>
        natural.Steps >= 1 && natural.Height - arrival <= LowDeckRise && natural.Height - arrival >= -StairLayout.StepUp;

    /// <summary>
    /// The price of meeting an end one way, the one rule every way is chosen by (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float)"/>,
    /// <see cref="Share"/>). Stairs: each step laid costs <see cref="StepPrice"/>, meeting level with the deck nothing;
    /// but at a <paramref name="low"/> end (<see cref="Low"/>) the first <see cref="LowSteps"/> steps cost nothing and
    /// meeting it with none <see cref="LowFlushPrice"/>, so a low bridge or a ford's span is met by a step or two up onto
    /// it (the road's earthwork and climb choosing between them), while a higher deck is met by the road approaching it
    /// flat. Earthwork costs <see cref="EarthworkPrice"/> for each metre of fill or cut
    /// under the road over an apron's length (<paramref name="earthwork"/>, in square metres of the road's profile); the
    /// road climbing or falling <paramref name="climb"/> metres from the foot to where it arrives costs
    /// <see cref="ClimbPrice"/> a metre; leaving off the line costs <see cref="OffLinePrice"/>; and each metre the way adds
    /// to the road (<paramref name="detour"/>) <see cref="DetourPrice"/>.
    /// </summary>
    public static float Price(int steps, float earthwork, float climb, bool offLine, float detour, bool low = false) =>
        StairPrice(steps, low) + earthwork / ApronLength * EarthworkPrice + climb * ClimbPrice + (offLine ? OffLinePrice : 0f) + detour * DetourPrice;

    /// <summary>What <paramref name="steps"/> stairs laid at an end cost (<see cref="Price"/>), at a
    /// <paramref name="low"/> end or not.</summary>
    public static float StairPrice(int steps, bool low) =>
        low ? (steps == 0 ? LowFlushPrice : Mathf.Max(0, steps - LowSteps) * StepPrice) : steps * StepPrice;

    /// <summary>The height the road arrives at an end from: the ground (or the road's floor over water) at the first point
    /// of <paramref name="path"/> from <paramref name="at"/> outward (<paramref name="step"/>, no further than
    /// <paramref name="bound"/>) clear of every way of meeting <paramref name="natural"/>: past its whole run and the
    /// road's approach along its line.</summary>
    private static float ArrivalOnPath(List<Vector2> path, End natural, int at, int step, int bound, Func<Vector2, float> ground)
    {
        float clear = natural.Steps * StairLayout.PieceRun + ApronLength * 1.5f + 1f;
        Vector2 p = natural.Meet;
        for (int i = at; i >= 0 && i < path.Count && (step > 0 ? i <= bound : i >= bound); i += step)
        {
            p = path[i];
            if (Vector2.Distance(p, natural.Edge) >= clear) break;
        }
        float g = ground(p);
        return RoadWater.BuiltDry(g) ? g : RoadWater.AtLeastDryFloor(g);
    }

    /// <summary>The length of <paramref name="path"/> walked from <paramref name="at"/> outward (<paramref name="step"/>,
    /// no further than <paramref name="bound"/>), up to each index (<paramref name="walked"/>), and returned, the way from
    /// <paramref name="from"/> to its point <paramref name="at"/>.</summary>
    private static float Walked(List<Vector2> path, int at, int step, int bound, Vector2 from, out Dictionary<int, float> walked)
    {
        walked = new Dictionary<int, float> { [at] = 0f };
        float d = 0f;
        for (int i = at + step; i >= 0 && i < path.Count && (step > 0 ? i <= bound : i >= bound); i += step)
            walked[i] = d += Vector2.Distance(path[i - step], path[i]);
        return Vector2.Distance(from, path[at]);
    }

    private static float Length(List<Vector2> points)
    {
        float d = 0f;
        for (int i = 1; i < points.Count; i++) d += Vector2.Distance(points[i - 1], points[i]);
        return d;
    }

    // ------------------------------------------------------- one landing between two structures

    /// <summary>The shortest landing two structures' ends share (<see cref="Share"/>): from one foot to the other, about a
    /// stride either side of a player standing between them.</summary>
    public const float SharedLandingLength = 2f;

    /// <summary>The ground an end needs to itself for the road to come to it alone: its whole run, and a landing pad and
    /// the room to leave it (<see cref="PadLength"/> each).</summary>
    private static float OwnGround(End natural) => natural.Steps * StairLayout.PieceRun + 2f * PadLength;

    /// <summary>
    /// One landing two structures' facing ends share, where the ground between them is too short for each end's own run-up
    /// or pad (their edges nearer than both ends' <see cref="OwnGround"/>): <paramref name="far"/>, the end of the crossing
    /// the road leaves at <paramref name="path"/>'s point <paramref name="from"/>, and <paramref name="near"/>, the end of the
    /// next it comes to at <paramref name="to"/>, each as the bank takes it. Every pair of rows up their flights whose
    /// feet share it (<see cref="SharesLanding"/>: each within a walker's step of their common height), at least
    /// <see cref="SharedLandingLength"/> apart, each foot opening onto the other along its line (within a turn of 60
    /// degrees), is a candidate: the road between the two feet levelled at their common height, within the earthwork cap,
    /// dry or else as earthwork over water, every other road on it raised to it (RoadStairRuns.Land). The cheapest
    /// (<see cref="Price"/>: the steps both ends lay, the landing's earthwork, the step between the two feet as a climb) is
    /// taken (each end's stairs priced low or not over the ground between them, <see cref="Low"/>), the far end first; null
    /// where none fits, or a stair step the search bought (<paramref name="isStep"/>) lies
    /// between them.
    /// </summary>
    private static (Met far, Met near)? Share(List<Vector2> path, int from, int to, End far, End near, Func<Vector2, float> ground, float cap,
        Func<Vector2, Vector2, bool>? isStep)
    {
        if (!far.IsStructure || !near.IsStructure || to < from) return null;
        if (Vector2.Distance(far.Edge, near.Edge) >= OwnGround(far) + OwnGround(near)) return null;
        for (int i = from + 1; i <= to; i++)
            if (isStep?.Invoke(path[i - 1], path[i]) ?? false) return null;
        // Each end low or not over the ground between them, where the road arrives (Low).
        float between = 0.5f * (ground(far.Edge - far.Up * PadLength) + ground(near.Edge - near.Up * PadLength));
        bool lowFar = Low(far, between), lowNear = Low(near, between);
        (Met, Met)? best = null;
        float bestPrice = float.MaxValue;
        for (int rf = 0; rf <= far.Steps; rf++)
            for (int rn = 0; rn <= near.Steps; rn++)
            {
                var f = new End(far.Edge, far.Up, far.Height, rf);
                var m = new End(near.Edge, near.Up, near.Height, rn);
                float step = Mathf.Abs(f.MeetHeight - m.MeetHeight), height = LandingHeight(f.MeetHeight, m.MeetHeight);
                if (!SharesLanding(f.MeetHeight, m.MeetHeight) || height < RoadConstants.DryRoadFloor - StairLayout.StairFitsFrom) continue;
                Vector2 across = m.Meet - f.Meet;
                float length = across.magnitude;
                if (length < SharedLandingLength) continue;
                Vector2 dir = across * (1f / length);
                if (Vector2.Dot(dir, -f.Up) < 0.5f || Vector2.Dot(-dir, -m.Up) < 0.5f) continue;
                float stairs = StairPrice(rf, lowFar) + StairPrice(rn, lowNear);
                if (stairs + Price(0, 0f, step, false, 0f) >= bestPrice) continue;
                var points = new List<Vector2> { f.Meet };
                for (float d = 1f; d < length; d += 1f) points.Add(Vector2.Lerp(f.Meet, m.Meet, d / length));
                points.Add(m.Meet);
                var removed = new List<Vector2>(points) { f.Joint, m.Joint };
                foreach (var (e, whole) in new[] { (f, far), (m, near) })
                    for (int k = e.Steps; k < whole.Steps; k++)
                        removed.Add(StepCentre(whole.Edge, whole.Up, k));
                var heights = points.Select(ground).ToList();
                bool overWater = false;
                float[]? raised = RoadStairRuns.Land(points, heights, removed, f.Meet, height, (x, z) => ground(new Vector2(x, z)), cap, out var others);
                if (raised == null)
                {
                    overWater = true;
                    raised = RoadStairRuns.Land(points, heights, removed, f.Meet, height, (x, z) => ground(new Vector2(x, z)), cap, out others, overWater: true);
                }
                if (raised == null) continue;
                float price = stairs + Price(0, Earthwork(points, heights, raised), step, false, 0f);
                if (price >= bestPrice) continue;
                bestPrice = price;
                var none = new List<(RoadSpatialGrid.RoadPoint at, float to)>();
                best = (new Met(f, new List<Vector2> { f.Meet, m.Meet }, from, none, overWater, false, false, price, Shared: true),
                    new Met(m, new List<Vector2> { m.Meet, f.Meet }, to, others!, overWater, false, false, price, Shared: true));
            }
        return best;
    }

    /// <summary>
    /// The ways an end whose stairs the bank takes as <paramref name="natural"/> can be met, each with whether the road
    /// leaves it from its landing pad at any heading (<see cref="PadLength"/>) rather than along its line, in order: the
    /// whole run's foot along the line, then from its pad; the foot of each row up the flight, along the line, then from
    /// its pad; at a <paramref name="low"/> end (<see cref="Low"/>), each row past the whole run up to
    /// <see cref="LowSteps"/>, the road cut down to its foot, along the line, then from its pad; and the deck's edge, no
    /// stairs, along its line, then from its pad. A road come from the side meets every one of them from its pad, so a
    /// landing lies between the end and the road (<see cref="PadTurn"/>), never the road's middle at the deck's edge or the
    /// bottom step. Each is priced (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float)"/>); the order only breaks ties.
    /// </summary>
    internal static IEnumerable<(End end, bool pad)> Ways(End natural, Func<Vector2, float> ground, bool low = false)
    {
        yield return (natural, false);
        yield return (natural, true);
        if (natural.Steps == 0) yield break;
        for (int row = natural.Steps - 1; row >= 1; row--)
        {
            var landed = new End(natural.Edge, natural.Up, natural.Height, row);
            yield return (landed, false);
            yield return (landed, true);
        }
        // Past the whole run only where its foot stands on the ground: the next row's ground is cut down under the road
        // (End.UnderTheJoint), the run's own steps stand as the bank takes them.
        bool footDown = ground(natural.Meet) <= natural.MeetHeight + StairLayout.StepUp;
        for (int row = natural.Steps + 1; low && footDown && row <= LowSteps; row++)
        {
            var cut = new End(natural.Edge, natural.Up, natural.Height, row);
            yield return (cut, false);
            yield return (cut, true);
        }
        var edge = new End(natural.Edge, natural.Up, natural.Height, 0);
        yield return (edge, false);
        yield return (edge, true);
    }

    /// <summary>How far the landing an end opens onto runs out from its foot, on its line: about a road's width, the
    /// road's own clearance. Past it, the road brought to the end from its pad (<see cref="Approach"/>) may leave at any
    /// heading, square to the line or at an angle.</summary>
    public const float PadLength = 4f;

    /// <summary>How far out from an end's foot (the bottom step, or the deck's edge where it has none), on its line, the
    /// road brought to it from its landing pad turns (<see cref="Approach"/>): a road's width, so the road's centreline
    /// across the pad passes this far beyond the foot and its edge a half-width beyond it, a landing showing between the
    /// end and a road met from the side.</summary>
    public const float PadTurn = 4f;

    /// <summary>The nearest the road's centreline comes to an end's foot as it leaves the landing pad from its turn
    /// (<see cref="Approach"/>): its edge a metre beyond the foot, at any heading, even back along the bank past the end.</summary>
    public const float PadLeave = 3f;

    /// <summary>
    /// The road from <paramref name="end"/>'s foot outward as it is brought to it, or null where it cannot be: from
    /// <paramref name="path"/>'s point <paramref name="at"/> outward (<paramref name="step"/>, no further than
    /// <paramref name="bound"/>, the next crossing's foot or bank, or the road's end) every point up to the first one
    /// clear of the end is dropped, and the road runs from there to the foot. Along the line (<paramref name="pad"/>
    /// false), clear is half an apron past the apron: the road turns onto the line there, then runs down it over the apron;
    /// the points, foot first, are the foot, the apron's middle and far end, the turn where the clear point is off the
    /// line, and that point (<c>k</c>, its index). From the end's landing pad (<paramref name="pad"/>), clear is a pad's
    /// length from the turn, in front of the foot or well to its side (a road along a bank slanting past the end): the road turns on the pad <see cref="PadTurn"/> out from the foot and
    /// leaves it at any heading, its centreline never nearer the foot than <see cref="PadLeave"/>, so a landing lies
    /// between the foot and the road's edge; the points are the foot, the turn and that point. Where no point is clear, the last one is taken where it stands as far out
    /// (an apron, or the pad), the way laid up to it; or, where it is the bank of a crossing that is road
    /// (<paramref name="roadAtBound"/>: a waded or raised ford the road runs on over), wherever it stands out past the
    /// foot. A stair step the search bought (<paramref name="isStep"/>) is never cut: where one starts first, the road runs
    /// from the foot to its start (<c>stair</c>), which must stand out past the foot (along the line, within a turn of it),
    /// and the staircase is laid from there (a short stretch between the two is one level landing, or the road is refused,
    /// RoadNetworkGenerator.PlanPieces). Null where the road cannot be brought to the foot so: it ends, or the next
    /// structure stands, too near, a ford or a staircase starts under the flight, or a staircase off the line.
    /// </summary>
    internal static (List<Vector2> way, int k, bool stair, bool clear)? Approach(List<Vector2> path, End end, int at, int step, int bound, bool roadAtBound,
        bool pad, Func<Vector2, Vector2, bool>? isStep)
    {
        Vector2 outward = -end.Up, across = new(-outward.y, outward.x);
        float Out(Vector2 p) => Vector2.Dot(p - end.Meet, outward);
        float Across(Vector2 p) => Mathf.Abs(Vector2.Dot(p - end.Meet, across));
        Vector2 padEdge = end.Meet + outward * PadLength, padTurn = end.Meet + outward * PadTurn;
        float further = ApronLength * 1.5f;
        // Off the pad: a road's length from its turn, in front of the foot or else well to its side (a road along a bank
        // slanting past the end), the road from the turn to it nowhere nearer the foot than PadLeave.
        bool OffThePad(Vector2 p)
        {
            Vector2 d = p - padTurn;
            float t = Mathf.Clamp01(Vector2.Dot(end.Meet - padTurn, d) / Mathf.Max(d.sqrMagnitude, 1e-6f));
            return (Out(p) >= -1f || Across(p) >= 1.5f * PadLength) && d.magnitude >= PadLength && Vector2.Distance(padTurn + d * t, end.Meet) >= PadLeave - 1e-3f;
        }
        bool Clear(Vector2 p) => pad ? OffThePad(p) : Out(p) >= further + 1f;
        int k = at;
        bool clear = false, stair = false;
        for (int i = at; i >= 0 && i < path.Count && (step > 0 ? i <= bound : i >= bound); i += step)
        {
            if (i != at && (isStep?.Invoke(path[i - step], path[i]) ?? false)) { stair = true; break; }
            k = i;
            if (Clear(path[i])) { clear = true; break; }
        }
        float reach = Out(path[k]), room = pad ? PadLength : ApronLength - 0.5f;
        if ((reach <= 0.25f && !(pad && clear)) || (pad && !clear && reach < PadTurn - 1e-3f) || (stair ? !pad && Across(path[k]) > reach : !clear && !(roadAtBound && k == bound) && reach < room)) return null;
        var way = new List<Vector2> { end.Meet };
        if (pad)
        {
            if (Vector2.Distance(path[k], padTurn) > 0.5f) way.Add(padTurn);
        }
        else
        {
            for (float d = ApronLength * 0.5f; d <= ApronLength + 1e-3f; d += ApronLength * 0.5f)
                if (d < reach - 0.25f) way.Add(end.Meet + outward * d);
            if (clear && Across(path[k]) > 1f) way.Add(end.Meet + outward * further);
        }
        way.Add(path[k]);
        return (way, k, stair, clear);
    }

    /// <summary>
    /// Whether the road brought to <paramref name="end"/> (<paramref name="way"/>, then <paramref name="path"/> on from
    /// <paramref name="k"/>, -1 where a staircase starts there) lands on it: the road, every metre as far as its ease can
    /// reach, levelled at the foot over its last <see cref="ApronLength"/> as it comes and the ground the stairs not laid
    /// would stand on (those of <paramref name="natural"/> below the end's), and
    /// eased to the ground at the road grade, every height it changes within <paramref name="cap"/> of the ground, dry
    /// (over water, <paramref name="overWater"/>: filled over ground under the road's floor), outside every place, and
    /// every other road on that ground raised to it (RoadStairRuns.Land). The other roads raised, and the earthwork the road
    /// takes (<paramref name="earthwork"/>: its height off the ground, every metre, in square metres of its profile); null
    /// where it does not.
    /// </summary>
    private static List<(RoadSpatialGrid.RoadPoint at, float to)>? Lands(List<Vector2> path, List<Vector2> way, int k, int step, End end, End natural,
        Func<Vector2, float> ground, float cap, bool overWater, out float earthwork)
    {
        earthwork = 0f;
        // The ease inline, not RoadGrade.EaseLength: one rounding of the whole sum, as on the game's Mono.
        float reach = natural.Steps * StairLayout.PieceRun + ApronLength * 1.5f + RoadNetworkGenerator.Shaped(cap) / RoadGrade.Floored;
        var points = new List<Vector2> { way[0] };
        void To(Vector2 b)
        {
            Vector2 a = points[points.Count - 1];
            float length = Vector2.Distance(a, b);
            for (float d = 1f; d < length; d += 1f) points.Add(Vector2.Lerp(a, b, d / length));
            points.Add(b);
        }
        for (int i = 1; i < way.Count; i++) To(way[i]);
        for (int i = k + step; k >= 0 && i >= 0 && i < path.Count && Vector2.Distance(points[points.Count - 1], end.Meet) <= reach; i += step) To(path[i]);
        var removed = new List<Vector2>();
        for (int s = end.Up == natural.Up ? end.Steps : 0; s < natural.Steps; s++)
            removed.Add(StepCentre(natural.Edge, natural.Up, s));
        // The road's last ApronLength to the foot, along the road as it comes, and on under the joining piece: levelled at
        // the foot's height (RoadSpatialGrid.LevelledEnds).
        removed.Add(end.Joint);
        float along = 0f;
        for (int i = 0; i < points.Count && along <= ApronLength + 1e-3f; i++)
        {
            if (i > 0) along += Vector2.Distance(points[i - 1], points[i]);
            if (along <= ApronLength + 1e-3f) removed.Add(points[i]);
        }
        var heights = points.Select(ground).ToList();
        if (RoadStairRuns.Land(points, heights, removed, end.Meet, end.MeetHeight, (x, z) => ground(new Vector2(x, z)), cap, out var others, overWater) is not float[] raised) return null;
        earthwork = Earthwork(points, heights, raised);
        return others!;
    }

    /// <summary>The earthwork a road at <paramref name="raised"/> over ground at <paramref name="heights"/> takes along
    /// <paramref name="points"/>: its height off the ground, cut or fill, over the length it runs, in square metres of
    /// its profile.</summary>
    private static float Earthwork(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, IReadOnlyList<float> raised)
    {
        float area = 0f;
        for (int i = 1; i < points.Count; i++)
            area += 0.5f * (Mathf.Abs(raised[i - 1] - heights[i - 1]) + Mathf.Abs(raised[i] - heights[i])) * Vector2.Distance(points[i - 1], points[i]);
        return area;
    }

    /// <summary>
    /// Puts the road brought to an end (<paramref name="way"/>, foot first, from <see cref="Approach"/>) into
    /// <paramref name="path"/> in place of its points from <paramref name="at"/> outward (<paramref name="step"/>) up to
    /// <paramref name="k"/>: the foot is then the crossing's point on the path (<paramref name="foot"/>). Returns how many
    /// points the path gained.
    /// </summary>
    private static int Bring(List<Vector2> path, List<Vector2> way, int at, int k, int step, out int foot)
    {
        var inner = way.GetRange(0, way.Count - 1);
        if (step > 0)
        {
            path.RemoveRange(at, k - at);
            path.InsertRange(at, inner);
            foot = at;
            return inner.Count - (k - at);
        }
        inner.Reverse();
        path.RemoveRange(k + 1, at - k);
        path.InsertRange(k + 1, inner);
        foot = k + inner.Count;
        return inner.Count - (at - k);
    }

    /// <summary>Road between two structures shorter than this is a stub (a tiny bit of road between them, about 6-8 m): it is one level landing at the joining height, or not laid.</summary>
    public const float StubLength = 8f;

    /// <summary>
    /// Why the road <paramref name="points"/> between two structures, held at <paramref name="from"/> and
    /// <paramref name="to"/> at its ends, is a stub: shorter than <see cref="StubLength"/> (the stretch under a viaduct's
    /// joining piece apart) and its ends too far apart in height to share one level landing (<see cref="SharesLanding"/>: each
    /// within a walker's step of their common height). Null where it is a landing, or long enough to be road.
    /// </summary>
    public static string? IsStub(IReadOnlyList<Vector2> points, float from, float to)
    {
        float length = 0f;
        for (int i = 1; i < points.Count; i++) length += Vector2.Distance(points[i - 1], points[i]);
        if (length >= StubLength + JoinOverlap - 1e-3f) return null;
        if (SharesLanding(from, to)) return null;
        return FormattableString.Invariant($"a stub of road {length:F1} m long between two structures {Mathf.Abs(from - to):F1} m apart in height");
    }

    /// <summary>The height one level landing between two ends held at <paramref name="a"/> and <paramref name="b"/> is levelled at:
    /// their common height (<see cref="SharesLanding"/>).</summary>
    public static float LandingHeight(float a, float b) => 0.5f * (a + b);

    /// <summary>Whether two ends held at <paramref name="a"/> and <paramref name="b"/> (two structures' ends, a structure's end
    /// and a staircase's foot or another held end) may share one level landing: levelled at their common height
    /// (<see cref="LandingHeight"/>), each within a walker's step of it (StairLayout.Walkable), so up to two steps apart. The one
    /// rule for <see cref="Share"/>, <see cref="IsStub"/> and levelling that landing (RoadSpatialGrid.LevelledOn). The one owner of
    /// a walker's step (docs/OWNERS.md).</summary>
    public static bool SharesLanding(float a, float b)
    {
        float landing = LandingHeight(a, b);
        return StairLayout.Walkable(a, landing) && StairLayout.Walkable(b, landing);
    }

    // ------------------------------------------------------- the built road

    /// <summary>
    /// The road as built near <paramref name="at"/>, within <paramref name="reach"/>: every road point laid before
    /// (RoadSpatialGrid, the network so far) and this road's own planned pieces, walked every metre. The built-result
    /// checks judge a structure on this, not on the road as one search planned it (a check that looked only at
    /// the ends of this road's own pieces never saw another road under a deck, or a fill under a pier).
    /// </summary>
    internal static IEnumerable<(Vector2 p, float h, float w)> BuiltRoadNear(Vector2 at, float reach, IReadOnlyList<RoadSpatialGrid.PlannedPath>? plans)
    {
        foreach (RoadSpatialGrid.RoadPoint rp in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(at.x, 0f, at.y), reach))
            if (rp.IsRoad) yield return (rp.p, rp.h, rp.w);
        if (plans == null) yield break;
        float reach2 = reach * reach;
        foreach (RoadSpatialGrid.PlannedPath plan in plans)
        {
            int n = plan.Points.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = plan.Points[i];
                float ha = plan.Heights[i];
                if ((a - at).sqrMagnitude <= reach2) yield return (a, ha, plan.Width);
                if (i + 1 >= n) continue;
                Vector2 b = plan.Points[i + 1];
                float len = Vector2.Distance(a, b);
                for (float d = 1f; d < len; d += 1f)
                {
                    Vector2 q = Vector2.Lerp(a, b, d / len);
                    if ((q - at).sqrMagnitude <= reach2) yield return (q, Mathf.Lerp(ha, plan.Heights[i + 1], d / len), plan.Width);
                }
            }
        }
    }
    /// <summary>
    /// Whether road at <paramref name="p"/>, its surface at <paramref name="h"/> and <paramref name="w"/> wide, lies under
    /// a deck at <paramref name="deck"/> between the ends <paramref name="near"/> and <paramref name="far"/>, out to
    /// <paramref name="halfWidth"/> either side of its line: lower than the deck, and not the road run on under a joining
    /// piece at its meeting height (a walker's step of it, <see cref="StairLayout.Walkable"/>: the road a shared landing levels
    /// at its ends' common height is that).
    /// </summary>
    public static bool UnderTheDeck(Vector2 p, float h, float w, End near, End far, float deck, float halfWidth)
    {
        if (!near.IsSet || !far.IsSet) return false;
        Vector2 line = far.Edge - near.Edge;
        float length = line.magnitude;
        if (length < 1e-3f) return false;
        Vector2 dir = line * (1f / length), side = new(-dir.y, dir.x);
        float along = Vector2.Dot(p - near.Edge, dir);
        if (along <= 0.25f || along >= length - 0.25f) return false;
        if (Mathf.Abs(Vector2.Dot(p - near.Edge, side)) > halfWidth + w * 0.5f) return false;
        if (h >= deck - 0.1f) return false;
        foreach (End end in new[] { near, far })
            if (end.UnderTheJoint(p, halfWidth + w * 0.5f) && StairLayout.Walkable(h, end.MeetHeight)) return false;
        return true;
    }
}
