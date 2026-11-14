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
/// <see cref="Low"/>) and the cheapest is taken, a low structure's two ends as a pair (<see cref="Pair"/>), and saved with the crossing
/// (<see cref="RoadCrossing.FromEnd"/>); the road runs to its foot on its line, levelled over its last
/// <see cref="ApronLength"/> to its height; every kit lays its stairs down to it (BridgeLayout.LayEndStairs). Two
/// structures' facing ends with too little ground between them for each end's own are met together, on one landing
/// between them (<see cref="Share"/>). A crossing with an end no way reaches is searched round.
/// </summary>
public static partial class StructureEnds
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
    /// way the road reaches is taken (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float, int)"/>, each way dry or else as earthwork within <paramref name="cap"/>),
    /// a low structure's two ends chosen as a pair (<see cref="Pair"/>: both stepped or both flush over one of each);
    /// a structure's end facing the previous structure's with too little ground between them shares one landing with it
    /// (<see cref="Share"/>), both decided together. A stair step the search bought (<paramref name="isStep"/>) is never
    /// cut. Returns the first crossing with an end none of
    /// whose ways the road reaches, the path then as it was at that crossing; null where every end is met. A raised viaduct
    /// (DvergrViaduct.RaisedSteps) with an end no way reaches is lowered a step and every end met again from the road as it
    /// came, down to the deck at grade, so a raise never changes whether a viaduct is built. A valley viaduct still with such an
    /// end has its banks moved along its line (DvergrViaduct.MovedBanks) where <paramref name="mayMove"/> leaves the moved line
    /// allowed (the search's own test of a viaduct's line, RoadPathfinder.MovedViaductAllowed; null: no move), a bank that is a
    /// bought stair step's top never moved (<paramref name="isStep"/>), every end met again
    /// from the road as it came, the first met kept. An end's look (flush or stepped, as
    /// the bank takes it, <see cref="Natural"/>) is a preference, not a rule: where none of its ways fits, more steps are
    /// tried, within its structure's most (<see cref="MostSteps"/>), before the crossing is given up (<see cref="Flex"/>); and
    /// a stepped end is always met flush too where that fits (<see cref="Ways"/>). A way fits only where its road is within
    /// <paramref name="cap"/> as every road's earthwork is measured, across the road's <paramref name="width"/>
    /// (<see cref="WithinCapAcross"/>).
    /// The one owner of end steps of bridges, viaducts and stairs meeting a road (docs/OWNERS.md).
    /// </summary>
    public static RoadCrossing? MeetEnds(List<Vector2> path, List<RoadCrossing> crossings, WorldGenerator world, float cap,
        Func<Vector2, Vector2, bool>? isStep, float width = RoadConstants.DefaultRoadWidth, Func<RoadCrossing, RoadCrossing, bool>? mayMove = null)
    {
        var road = new List<Vector2>(path);
        var given = crossings.ConvertAll(CrossingFields.Of);
        RoadCrossing? unmet = MeetEndsLowered(path, crossings, world, cap, isStep, width, out bool nearUnmet);
        if (mayMove == null || unmet == null || !unmet.ValleyJump || Adopted(unmet) || !BridgeStructures.IsViaduct(unmet, world)) return unmet;
        // A valley viaduct with an end no way reaches: its banks moved along its line (DvergrViaduct.MovedBanks, the one owner of
        // where they may stand), each where the network and the locations leave its line clear (mayMove, the search's own
        // test of a viaduct's line), and every end met again from the road as it came; the first met is kept, its banks the
        // road's. None met: the road and its crossings as the banks the search bought left them.
        int index = crossings.IndexOf(unmet);
        var left = (path: path.ToArray(), crossings: crossings.ConvertAll(CrossingFields.Of));
        var bought = new RoadCrossing();
        given[index].Put(bought);
        // A bank that is the top of a stair step the search bought (isStep, the road's own test of its steps: the road climbs a
        // staircase onto the rim and jumps from it, or lands and climbs on) stays where it was bought: a move takes the other end
        // only, so the staircase is still on the road for its steps to be laid (RoadStairRuns.Lay) and is never cut.
        Vector2? heldFrom = bought.FromIndex > 0 && isStep != null && isStep(road[bought.FromIndex - 1], road[bought.FromIndex]) ? road[bought.FromIndex - 1] : null;
        Vector2? heldTo = bought.ToIndex + 1 < road.Count && isStep != null && isStep(road[bought.ToIndex], road[bought.ToIndex + 1]) ? road[bought.ToIndex + 1] : null;
        t_movingBanks = true;
        try
        {
            foreach (RoadCrossing moved in DvergrViaduct.MovedBanks(bought, world, nearUnmet))
            {
                if (heldFrom is Vector2 below && !isStep!(below, moved.FromBank)) continue;
                if (heldTo is Vector2 above && !isStep!(moved.ToBank, above)) continue;
                if (!mayMove(bought, moved)) continue;
                path.Clear();
                path.AddRange(road);
                for (int i = 0; i < crossings.Count; i++) given[i].Put(crossings[i]);
                // The same crossing on the moved banks (RoadCrossing.SnapTo): its place in the road, its kit, island and the
                // search's moves it lies on (its bans) are the bought one's.
                unmet.SnapTo(moved);
                unmet.Kit = bought.Kit;
                path[unmet.FromIndex] = unmet.FromBank;
                path[unmet.ToIndex] = unmet.ToBank;
                RoadCrossing? again = MeetEndsLowered(path, crossings, world, cap, isStep, width, out _);
                if (!ReferenceEquals(again, unmet)) return again;
            }
        }
        finally { t_movingBanks = false; }
        path.Clear();
        path.AddRange(left.path);
        for (int i = 0; i < crossings.Count; i++) left.crossings[i].Put(crossings[i]);
        return unmet;
    }

    /// <summary>Set while <see cref="MeetEnds"/> tries a viaduct's moved banks: their unmet ends are not logged (the banks the
    /// search bought were).</summary>
    [ThreadStatic] private static bool t_movingBanks;

    /// <summary><see cref="MeetEnds"/> on the crossings' banks as they stand: a raised viaduct with an end no way reaches lowered
    /// a step and every end met again, down to the deck at grade. <paramref name="nearUnmet"/>: whether the end not met is the
    /// unmet crossing's near end.</summary>
    private static RoadCrossing? MeetEndsLowered(List<Vector2> path, List<RoadCrossing> crossings, WorldGenerator world, float cap,
        Func<Vector2, Vector2, bool>? isStep, float width, out bool nearUnmet)
    {
        var road = new List<Vector2>(path);
        var stood = crossings.ConvertAll(c => (c, c.FromIndex, c.ToIndex, c.FromEnd, c.ToEnd));
        while (true)
        {
            RoadCrossing? unmet = MeetEndsOnce(path, crossings, world, cap, isStep, width, out nearUnmet);
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
        Func<Vector2, Vector2, bool>? isStep, float width, out bool nearUnmet)
    {
        nearUnmet = false;
        float G(Vector2 p) => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
        // The ground a road's earthwork is measured from (RoadNetworkGenerator.EarthworkAcross, as the finished road is).
        Func<Vector2, float> earth = RoadNetworkGenerator.GroundBeforeRoads(world);
        var order = crossings.OrderBy(c => c.FromIndex).ToList();
        // A crossing's far end met on a landing shared with the next crossing's near end, decided with it (Share).
        var sharedFar = new Dictionary<RoadCrossing, Met>();
        // How many points before a crossing's near foot its near end's way joins the road (the way's own points, the pad
        // turn or the apron): the previous crossing's far end is met only up to that join, never over the way.
        var nearWay = new Dictionary<RoadCrossing, int>();
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
            // The far end's ways, unless it shares a landing with the next crossing's near end (decided with it).
            bool farShared = sharedFar.TryGetValue(c, out Met farFixed);
            Options farWays;
            if (farShared) farWays = Options.Of(farFixed);
            else
            {
                int after = n + 1 < order.Count ? order[n + 1].FromIndex - (nearWay.TryGetValue(order[n + 1], out int way) ? way : 0) : path.Count - 1;
                bool roadAfter = n + 1 < order.Count && !Natural(order[n + 1], world).near.IsSet;
                farWays = AtSite(adopt ? c.ToEnd!.Value : far, adopt, new Road(path, c.ToIndex, +1, Mathf.Max(after, c.ToIndex), roadAfter, isStep, width, earth), G, cap, MostSteps(c, world));
                if (farWays.Best is null) { LogUnmet(c, "far", far, cap); return c; }
            }
            // The near end's: on a landing shared with the previous crossing's far end, or its own. Neither reads the path
            // past this crossing's far foot, which bringing the road to the far end changes. A crossing's far end stops where
            // the next crossing's near way joins the road (nearWay), so it never cuts that end's pad or apron; where no ground
            // is left between, it comes back unmet and the road is searched again without it.
            RoadCrossing? prev = n > 0 ? order[n - 1] : null;
            End prevFar = prev != null ? Natural(prev, world).far : default;
            var shared = prev != null && !adopt && prevFar.IsSet && !Adopted(prev)
                ? Share(path, prev.ToIndex, c.FromIndex, prevFar, near, G, cap, isStep, width, earth) : null;
            Options nearWays;
            if (shared is var (_, sharedNear)) nearWays = Options.Of(sharedNear);
            else
            {
                int before = n > 0 ? order[n - 1].ToIndex : 0;
                bool roadBefore = n > 0 && !Natural(order[n - 1], world).near.IsSet;
                nearWays = AtSite(adopt ? c.FromEnd!.Value : near, adopt, new Road(path, c.FromIndex, -1, Mathf.Min(before, c.FromIndex), roadBefore, isStep, width, earth), G, cap, MostSteps(c, world));
            }
            // The two ends chosen as a pair (Pair); the far end alone where the near end is not met.
            var (nearMet, farMet) = nearWays.Best is null ? (default(Met), farWays.Best!.Value) : Pair(nearWays, farWays);
            if (!farShared) Shift(order, n + 1, Bring(path, farMet.Way, c.ToIndex, farMet.K, +1, out _));
            if (nearWays.Best is null) { nearUnmet = true; LogUnmet(c, "near", near, cap); return c; }
            if (shared is var (landedFar, landedNear))
            {
                // Both ends on one landing: the road between the two crossings is the landing, from one foot to the other.
                int a = prev!.ToIndex, b = c.FromIndex;
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
            int nearDelta = Bring(path, nearMet.Way, c.FromIndex, nearMet.K, -1, out int foot);
            c.ToIndex += nearDelta;
            c.FromIndex = foot;
            nearWay[c] = nearMet.Way.Count - 1;
            Shift(order, n + 1, nearDelta);
            SetEnds(c, nearMet, farMet);
        }
        return null;
    }

    /// <summary>An end as its site alone meets it (<see cref="AtSite(End, Vector2, Func{Vector2, float}, float, int)"/>): the
    /// way taken, its price (<see cref="Price"/>), and whether it was met from its landing pad or by earthwork over
    /// water.</summary>
    public readonly record struct SiteEnd(End End, float Price, bool Pad, bool Earthwork);

    /// <summary>
    /// How an end is met by its site alone, with no road searched yet: the end as the bank takes it
    /// (<paramref name="natural"/>), its crossing's <paramref name="bank"/> and the ground beyond it along the end's line,
    /// the road come straight in along that line at the search's cell spacing (RoadPathfinder.CellSize). The one owner of
    /// the end rule (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float, int)"/>, which <see cref="MeetEnds"/> calls
    /// on a searched road), on that approach: its way and price; null where no way of meeting it reaches. A road searched later that comes in along the
    /// line meets it so; one come from the side may meet it another way (from its pad), or share a landing with the
    /// next structure (<see cref="Share"/>), which only a searched road decides.
    /// </summary>
    public static SiteEnd? AtSite(End natural, Vector2 bank, Func<Vector2, float> ground, float cap, int mostSteps = BridgeLayout.MaxStairSteps)
    {
        return SiteWays(natural, bank, ground, cap, mostSteps).Best is Met m ? Site(m) : null;
    }

    /// <summary>The ways an end is met by its site alone (<see cref="AtSite(End, Vector2, Func{Vector2, float}, float, int)"/>),
    /// the cheapest of each kind (<see cref="Options"/>).</summary>
    private static Options SiteWays(End natural, Vector2 bank, Func<Vector2, float> ground, float cap, int mostSteps)
    {
        if (!natural.IsStructure) return default;
        Vector2 outward = -natural.Up;
        float reach = Vector2.Distance(natural.Edge, bank) + natural.Steps * StairLayout.PieceRun + ApronLength * 1.5f
            // The ease at the cap written inline, not RoadGrade.EaseLength: the game's Mono keeps the sum wider than float and
            // rounds once at the store, as this line always has.
            + RoadNetworkGenerator.Shaped(cap) / RoadGrade.Floored + 2f * PadLength;
        var path = new List<Vector2> { bank };
        for (float d = RoadPathfinder.CellSize; d <= reach + RoadPathfinder.CellSize; d += RoadPathfinder.CellSize)
            path.Add(bank + outward * d);
        return AtSite(natural, false, new Road(path, 0, +1, path.Count - 1, false, null, RoadNetworkGenerator.RoadWidth, ground), ground, cap, mostSteps);
    }

    private static SiteEnd Site(Met m) => new(m.End, m.Price, m.Pad, m.Earthwork);

    /// <summary>The ways the last <see cref="AtSite(End, bool, Road, Func{Vector2, float}, float, int)"/> on this thread offered,
    /// reached by an approach from the road, and tried to land within the cap: what an unmet end's line reports (log only).</summary>
    [ThreadStatic] private static int t_waysOffered, t_waysReached, t_waysLanded;

    /// <summary>Why an end was not met (roads log, debug only): which end, where, and how far its ways got.</summary>
    private static void LogUnmet(RoadCrossing c, string which, End end, float cap)
    {
        if (!RoadNetworkGenerator.RoadsLog || t_movingBanks) return;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
            $"[ENDS] {which} end of {c} not met at ({end.Edge.x:F1}, {end.Edge.y:F1}) height {end.MeetHeight:F2}: {t_waysOffered} way(s) offered, {t_waysReached} reached from the road, {t_waysLanded} tried to land, none within the {cap:F1} m cap"));
    }

    /// <summary>
    /// A crossing's two ends as its site alone meets them (<see cref="AtSite(End, Vector2, Func{Vector2, float}, float, int)"/>),
    /// chosen as a pair (<see cref="Pair"/>), near end first, and whether both can be met: a crossing that is road (a waded
    /// or raised ford, a span too short for a deck) is met at its banks for nothing.
    /// </summary>
    public static (bool met, SiteEnd? near, SiteEnd? far) AtSite(RoadCrossing crossing, WorldGenerator world, float cap)
    {
        var (near, far) = Natural(crossing, world);
        if (!near.IsSet) return (true, null, null);
        float G(Vector2 p) => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
        int most = MostSteps(crossing, world);
        Options n = SiteWays(near, crossing.FromBank, G, cap, most), f = SiteWays(far, crossing.ToBank, G, cap, most);
        if (n.Best is null || f.Best is null) return (false, n.Best is Met a ? Site(a) : null, f.Best is Met b ? Site(b) : null);
        var (pn, pf) = Pair(n, f);
        return (true, Site(pn), Site(pf));
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
    /// (<see cref="MeetEnds"/>) or the site's own straight approach (<see cref="AtSite(End, Vector2, Func{Vector2, float}, float, int)"/>).
    /// The cheapest of an end's ways (<see cref="Ways"/>, or only <paramref name="natural"/> where it is
    /// <paramref name="adopt"/>ed from the road that laid the crossing) the road reaches from its point <c>At</c> outward
    /// (<c>Step</c>) no further than <c>Bound</c> (a ford's bank where <c>RoadAtBound</c>): its foot over the road's floor (<see cref="OverTheFloor"/>), the road
    /// brought in along its line (<see cref="Approach"/>) and its landing within the caps (<see cref="Lands"/>), on dry
    /// ground or else as earthwork over water; priced by <see cref="Price"/>, ties to the earlier way: the cheapest met flush
    /// and the cheapest met by steps (<see cref="Options"/>, for <see cref="Pair"/>), each null where none is. The look is
    /// a preference, not a rule: where none of those ways fits, the rows past them up to <paramref name="most"/> steps are
    /// tried the same way (<see cref="Flex"/>) before the end is left unmet.
    /// </summary>
    private static Options AtSite(End natural, bool adopt, Road road, Func<Vector2, float> ground, float cap, int most)
    {
        var (path, at, step, bound, roadAtBound, isStep, width, earthGround) = road;
        t_waysOffered = t_waysReached = t_waysLanded = 0;
        float arrival = ArrivalOnPath(path, natural, at, step, bound, ground);
        float under = Mathf.Min(arrival, Mathf.Max(Arrival(natural.Edge, -natural.Up, ground), RoadConstants.DryRoadFloor));
        bool low = !adopt && Low(natural, under);
        // An end the bank takes at grade, in reach of the road: a step up onto it for looks is a way too (Ways).
        bool look = !adopt && natural.Steps == 0 && InReach(natural, under);
        float before = Walked(path, at, step, bound, natural.Edge, out var walked);
        Options Cheapest(List<(End end, bool pad)> ways)
        {
            // Each way the road is brought to, with the least it can cost (its stairs, climb, turn and detour, no earthwork):
            // landed cheapest first, so a way whose least already costs more than one landed is never landed.
            var reached = new List<(int order, End end, bool pad, List<Vector2> way, int k, bool stair, bool clear, float climb, float detour, float least)>();
            t_waysOffered += ways.Count;
            for (int w = 0; w < ways.Count; w++)
            {
                var (end, pad) = ways[w];
                if (!OverTheFloor(end) || Approach(path, end, at, step, bound, roadAtBound, pad, isStep) is not var (way, k, stair, clear)) continue;
                float climb = Mathf.Abs(end.MeetHeight - arrival);
                float detour = Vector2.Distance(end.Edge, end.Meet) + Length(way) - walked[k] - before;
                reached.Add((w, end, pad, way, k, stair, clear, climb, detour, Price(end.Steps, 0f, climb, pad || !clear, detour, low)));
                t_waysReached++;
            }
            // The cheapest of each kind, flush [0] and stepped [1]: a way whose least already costs more than the one landed of
            // its kind is never landed.
            var best = new Met?[2];
            var bestOrder = new[] { int.MaxValue, int.MaxValue };
            foreach (var r in reached.OrderBy(r => r.least).ThenBy(r => r.order))
            {
                int kind = r.end.Steps > 0 ? 1 : 0;
                if (best[kind] is Met b && r.least > b.Price)
                {
                    if (best[1 - kind] is Met o && r.least > o.Price) break;
                    continue;
                }
                bool overWater = false;
                t_waysLanded++;
                if (Lands(path, r.way, r.stair ? -1 : r.k, step, r.end, natural, ground, cap, false, width, earthGround, out float earth) is not { } raised)
                {
                    overWater = true;
                    if ((raised = Lands(path, r.way, r.stair ? -1 : r.k, step, r.end, natural, ground, cap, true, width, earthGround, out earth)) == null) continue;
                }
                float price = Price(r.end.Steps, earth, r.climb, r.pad || !r.clear, r.detour, low);
                // Ties go to the earlier way.
                if (best[kind] is not Met better || price < better.Price - 1e-4f || (price < better.Price + 1e-4f && r.order < bestOrder[kind]))
                {
                    best[kind] = new Met(r.end, r.way, r.k, raised, overWater, r.pad, !r.clear && !r.stair, price);
                    bestOrder[kind] = r.order;
                }
            }
            return new Options(best[0], bestOrder[0], best[1], bestOrder[1], low);
        }
        if (adopt) return Cheapest(new List<(End, bool)> { (natural, false), (natural, true) });
        Options met = Cheapest(Ways(natural, ground, low, look).ToList());
        if (met.Best is not null) return met;
        // None of the look's ways fits: more steps, the road cut down to their foot, within the structure's most (Flex).
        var flex = Flex(natural, ground, low, look, most).ToList();
        return flex.Count > 0 ? Cheapest(flex) : met;
    }

    /// <summary>An end's ways as the road meets them (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float, int)"/>):
    /// the cheapest met flush and the cheapest met by steps, each with its way's order (ties go to the earlier way), and
    /// whether the end is low (<see cref="Low"/>).</summary>
    private readonly record struct Options(Met? Flush, int FlushOrder, Met? Stepped, int SteppedOrder, bool Low)
    {
        /// <summary>The cheaper of the two, ties to the earlier way; null where neither is.</summary>
        public Met? Best => Flush is not Met f ? Stepped : Stepped is not Met s ? f
            : s.Price < f.Price - 1e-4f || (s.Price < f.Price + 1e-4f && SteppedOrder < FlushOrder) ? s : f;

        /// <summary>One way, already decided (a landing shared with the next structure's end, <see cref="Share"/>).</summary>
        public static Options Of(Met met) => met.End.Steps > 0 ? new(null, int.MaxValue, met, 0, false) : new(met, 0, null, int.MaxValue, false);
    }

    /// <summary>What a low structure met by steps at one end and flush at the other costs on top of its two ends' prices
    /// (<see cref="Pair"/>): a structure looks best a bit symmetrical, both ends stepped (their counts may differ) or both
    /// flush. Twice <see cref="LowFlushPrice"/>, so that where the other end cannot take steps the low end is met flush too,
    /// its own <see cref="LowFlushPrice"/> and the fill to its deck paid, rather than one end stepped and one flush.</summary>
    public const float OneSidedPrice = 2f * LowFlushPrice;

    /// <summary>
    /// A structure's two ends chosen as a pair (<see cref="MeetEnds"/>, the site alone's <see cref="AtSite(RoadCrossing, WorldGenerator, float)"/>):
    /// each end's cheapest way (<see cref="Options.Best"/>), but at a low structure (either end <see cref="Low"/>) one end
    /// stepped and the other flush costs <see cref="OneSidedPrice"/> more, so both ends stepped (an end the bank takes at
    /// grade by one step up for looks, <see cref="Ways"/>) or both flush are taken where they cost less. A preference, not a
    /// rule: where neither fits, the cheapest stands.
    /// </summary>
    private static (Met near, Met far) Pair(Options near, Options far)
    {
        Met n = near.Best!.Value, f = far.Best!.Value;
        if (!near.Low && !far.Low) return (n, f);
        static float Cost(Met a, Met b) => a.Price + b.Price + ((a.End.Steps > 0) != (b.End.Steps > 0) ? OneSidedPrice : 0f);
        var best = (n, f);
        float bestCost = Cost(n, f);
        foreach (Met? a in new[] { near.Flush, near.Stepped })
            foreach (Met? b in new[] { far.Flush, far.Stepped })
                if (a is Met x && b is Met y && Cost(x, y) < bestCost - 1e-4f)
                {
                    best = (x, y);
                    bestCost = Cost(x, y);
                }
        return best;
    }

    /// <summary>The road an end is met from (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float, int)"/>): its
    /// points, the crossing's point on it (<c>At</c>), the way outward (<c>Step</c>), how far out it may be taken
    /// (<c>Bound</c>, a ford's bank where <c>RoadAtBound</c>), and the stair steps the search bought (<c>IsStep</c>),
    /// never cut. A searched road (<see cref="MeetEnds"/>), or a site's own straight approach along the end's line. Its
    /// <c>Width</c> and the ground its earthwork is measured from (<c>Earth</c>) are what a way's road is judged across
    /// (<see cref="WithinCapAcross"/>).</summary>
    private readonly record struct Road(List<Vector2> Path, int At, int Step, int Bound, bool RoadAtBound, Func<Vector2, Vector2, bool>? IsStep,
        float Width, Func<Vector2, float> Earth);

    /// <summary>An end as the road meets it (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float, int)"/>, <see cref="Share"/>): the end, the road brought to it
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
    /// within <see cref="LowDeckRise"/> over <paramref name="arrival"/> (and not under it by more than half a rise,
    /// <see cref="StairLayout.StairFitsFrom"/>: a pricing band, not a walk), the lower of the bank under the end (the ground along its line over an apron,
    /// <see cref="Arrival"/>) and the road arriving (<see cref="ArrivalOnPath"/>): a road coming down a slope to a deck
    /// lying low on its bank comes to a low structure. A low bridge or a ford's span looks best met by a step or two up
    /// onto it.</summary>
    public static bool Low(End natural, float arrival) => natural.Steps >= 1 && InReach(natural, arrival);

    /// <summary>Whether the deck at <paramref name="natural"/> stands within reach of the road arriving at
    /// <paramref name="arrival"/>: within <see cref="LowDeckRise"/> over it and not under it by more than half a rise
    /// (<see cref="StairLayout.StairFitsFrom"/>). With stairs, a low end (<see cref="Low"/>); at grade, an end a step up for
    /// looks may meet (<see cref="Ways"/>).</summary>
    private static bool InReach(End natural, float arrival) =>
        natural.Height - arrival <= LowDeckRise && natural.Height - arrival >= -StairLayout.StairFitsFrom;

    /// <summary>
    /// The price of meeting an end one way, the one rule every way is chosen by (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float, int)"/>,
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
        Func<Vector2, Vector2, bool>? isStep, float width, Func<Vector2, float> earth)
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
                float[]? raised = RoadStairRuns.Land(points, heights, removed, f.Meet, height, (x, z) => ground(new Vector2(x, z)), earth, cap, out var others, landedAcross: true);
                if (raised == null)
                {
                    overWater = true;
                    raised = RoadStairRuns.Land(points, heights, removed, f.Meet, height, (x, z) => ground(new Vector2(x, z)), earth, cap, out others, overWater: true, landedAcross: true);
                }
                if (raised == null || !WithinCapAcross(points, heights, raised, points.Count, f, m, width, earth, cap)) continue;
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
    /// stairs, along its line, then from its pad. An end the bank takes at grade (no stairs) in reach of the road
    /// (<paramref name="look"/>, <see cref="InReach"/>) may also be met by one step up for looks where the deck stands on
    /// its ground (sunk in it by less than half a rise): the road cut down a rise to the step's foot, along the line, then
    /// from its pad; the deck then stands a rise over the road, within a rise and a half (1.5 m, the band one step meets,
    /// <see cref="Decide"/>) of the road arriving. A road come from the side meets every one of them from its pad, so a
    /// landing lies between the end and the road (<see cref="PadTurn"/>), never the road's middle at the deck's edge or the
    /// bottom step. Each is priced (<see cref="AtSite(End, bool, Road, Func{Vector2, float}, float, int)"/>); the order only breaks ties.
    /// </summary>
    internal static IEnumerable<(End end, bool pad)> Ways(End natural, Func<Vector2, float> ground, bool low = false, bool look = false)
    {
        yield return (natural, false);
        yield return (natural, true);
        bool footDown = FootDown(natural, ground);
        if (natural.Steps == 0)
        {
            if (!look || !footDown) yield break;
            var up = new End(natural.Edge, natural.Up, natural.Height, 1);
            yield return (up, false);
            yield return (up, true);
            yield break;
        }
        for (int row = natural.Steps - 1; row >= 1; row--)
        {
            var landed = new End(natural.Edge, natural.Up, natural.Height, row);
            yield return (landed, false);
            yield return (landed, true);
        }
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

    /// <summary>Whether rows past an end's whole run may be laid (<see cref="Ways"/>, <see cref="Flex"/>): only where its foot
    /// stands on the ground, sunk in it by less than half a rise (StairLayout.StairFitsFrom, as the run's own foot lands).
    /// The next row's ground is cut down under the road (End.UnderTheJoint), the run's own steps stand as the bank takes
    /// them.</summary>
    private static bool FootDown(End natural, Func<Vector2, float> ground) =>
        ground(natural.Meet) <= natural.MeetHeight + StairLayout.StairFitsFrom;

    /// <summary>
    /// The ways an end is met by when none of its look's (<see cref="Ways"/>) fits: steps are a look, not a rule, so the
    /// rows past those up to <paramref name="most"/> (<see cref="MostSteps"/>), each with the road cut down to its foot
    /// where the run's foot stands on the ground (<see cref="FootDown"/>), along the line, then from its pad: the same rows a
    /// low end or a step up for looks takes (<see cref="Ways"/>), further down. The other way round needs no flex: a stepped
    /// end is always met flush too where that fits (its deck's edge, <see cref="Ways"/>), and a raised viaduct is lowered
    /// where no way of an end fits (<see cref="MeetEnds"/>).
    /// </summary>
    internal static IEnumerable<(End end, bool pad)> Flex(End natural, Func<Vector2, float> ground, bool low, bool look, int most)
    {
        if (!FootDown(natural, ground)) yield break;
        // The rows Ways already took: a step up for looks at an end at grade; at a low end, up to LowSteps.
        int from = natural.Steps == 0 ? (look ? 2 : 1) : Mathf.Max(natural.Steps + 1, low ? LowSteps + 1 : 0);
        for (int row = from; row <= most; row++)
        {
            var cut = new End(natural.Edge, natural.Up, natural.Height, row);
            yield return (cut, false);
            yield return (cut, true);
        }
    }

    /// <summary>The most steps an end of <paramref name="crossing"/> may be met by: a viaduct's within its end stairs' most
    /// (DvergrViaduct.EndStairsPastMost, the viaduct's own limit), every other kit's the most a run marches
    /// (BridgeLayout.MaxStairSteps).</summary>
    internal static int MostSteps(RoadCrossing crossing, WorldGenerator world)
    {
        if (!BridgeStructures.IsViaduct(crossing, world)) return BridgeLayout.MaxStairSteps;
        int most = 0;
        while (most < BridgeLayout.MaxStairSteps && !DvergrViaduct.EndStairsPastMost((most + 1) * StairLayout.PieceRise)) most++;
        return most;
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
    /// <paramref name="bound"/>: where the next crossing's near way joins the road, its bank, or the road's end) every point
    /// up to the first one clear of the end is dropped, and the road runs from there to the foot. Along the line (<paramref name="pad"/>
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
    /// eased to the ground at the road grade, every height it changes dry
    /// (over water, <paramref name="overWater"/>: filled over ground under the road's floor), outside every location, and
    /// every other road on that ground raised to it (RoadStairRuns.Land). The other roads raised, and the earthwork the road
    /// takes (<paramref name="earthwork"/>: its height off the ground, every metre, in square metres of its profile); null
    /// where it does not, or where the road it lands, run on under the joining piece, is past <paramref name="cap"/> as every
    /// road's earthwork is measured, at its centre and both edges, <paramref name="width"/> wide over
    /// <paramref name="earth"/> (<see cref="WithinCapAcross"/>).
    /// </summary>
    private static List<(RoadSpatialGrid.RoadPoint at, float to)>? Lands(List<Vector2> path, List<Vector2> way, int k, int step, End end, End natural,
        Func<Vector2, float> ground, float cap, bool overWater, float width, Func<Vector2, float> earth, out float earthwork)
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
        int apron = 0;
        for (int i = 0; i < points.Count && along <= ApronLength + 1e-3f; i++)
        {
            if (i > 0) along += Vector2.Distance(points[i - 1], points[i]);
            if (along <= ApronLength + 1e-3f) { removed.Add(points[i]); apron = i + 1; }
        }
        var heights = points.Select(ground).ToList();
        if (RoadStairRuns.Land(points, heights, removed, end.Meet, end.MeetHeight, (x, z) => ground(new Vector2(x, z)), earth, cap, out var others, overWater, landedAcross: true) is not float[] raised) return null;
        if (!WithinCapAcross(points, heights, raised, apron, end, null, width, earth, cap)) return null;
        earthwork = Earthwork(points, heights, raised);
        return others!;
    }

    /// <summary>
    /// Whether the road a way lands (<paramref name="points"/>, from the ground at <paramref name="heights"/> to
    /// <paramref name="raised"/>, foot first) is within <paramref name="cap"/> as every road's earthwork is measured
    /// (RoadNetworkGenerator.EarthworkAcross, the one measurement: at its centre and both edges, half its
    /// <paramref name="width"/> to each side, over <paramref name="earth"/>, the ground before roads): the stretch run on
    /// under the joining piece at the meeting height (<see cref="End.Joint"/>, of <paramref name="first"/>, and of
    /// <paramref name="last"/> at the far end of a shared landing), the first <paramref name="levelled"/> points (the apron
    /// levelled at the meeting height, every one stored so), and every other point the landing changes. The landing leaves
    /// its own road's earthwork to this (RoadStairRuns.Land, landedAcross) and measures the roads it raises the same way; the
    /// road is stored so and refused as finished past the cap (RoadNetworkGenerator.EarthworkRefusal), so a way past it does
    /// not fit and another is tried.
    /// </summary>
    private static bool WithinCapAcross(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, IReadOnlyList<float> raised, int levelled,
        End first, End? last, float width, Func<Vector2, float> earth, float cap)
    {
        var road = new List<Vector2>();
        var at = new List<float>();
        var measured = new HashSet<Vector2>();
        void Joint(End e, bool into)
        {
            // The joint to the meeting point (into: the other way), every metre, the meeting point itself apart.
            var stretch = new List<Vector2>();
            for (float d = JoinOverlap; d > 0.5f; d -= 1f) stretch.Add(e.Meet + e.Up * d);
            if (into) stretch.Reverse();
            foreach (Vector2 p in stretch) { road.Add(p); at.Add(e.MeetHeight); measured.Add(p); }
        }
        Joint(first, false);
        for (int i = 0; i < points.Count; i++)
        {
            road.Add(points[i]);
            at.Add(raised[i]);
            if (i < levelled || Mathf.Abs(raised[i] - heights[i]) >= 0.001f) measured.Add(points[i]);
        }
        if (last is End l) Joint(l, true);
        var plan = new RoadSpatialGrid.PlannedPath(road, at, new List<RoadPointDebugInfo>(), width, Length(road), false);
        bool within = true;
        RoadNetworkGenerator.EarthworkAcross(plan, earth, cap, p => { if (measured.Contains(p)) within = false; });
        return within;
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
    /// (RoadSpatialGrid, the network so far; within RoadTerrainModifier.AsStored, with the road not stored yet as it would
    /// store it, <see cref="RoadTerrainModifier.PointsNear"/>) and this road's own planned pieces, walked every metre: road
    /// only (RoadSpatialGrid.RoadPoint.IsRoad), never a mound under a structure's pier, which is ground and not road. The built-result
    /// checks judge a structure on this, not on the road as one search planned it (a check that looked only at
    /// the ends of this road's own pieces never saw another road under a deck, or a fill under a pier).
    /// </summary>
    internal static IEnumerable<(Vector2 p, float h, float w)> BuiltRoadNear(Vector2 at, float reach, IReadOnlyList<RoadSpatialGrid.PlannedPath>? plans)
    {
        foreach (RoadSpatialGrid.RoadPoint rp in RoadTerrainModifier.PointsNear(new Vector3(at.x, 0f, at.y), reach))
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
