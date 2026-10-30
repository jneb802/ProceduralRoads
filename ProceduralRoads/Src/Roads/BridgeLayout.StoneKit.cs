using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The stone kit: a bridge or a ford's span laid as a hand-built stone bridge was, in vanilla stone pieces on the hammer's
/// yaw grid, each on its neighbours' snap points. Pure logic. It fills the wooden kits' envelope and changes none of it: the
/// walking surface at their deck height (<see cref="WoodDeckHeight"/>), the deck as long as theirs
/// (<see cref="BuiltLength"/>) and 4 m wide, both ends' stairs laid by <see cref="LayEndStairs"/> (stone_stair's rise and run
/// are the shared stair's: snaps HIGH (+-1, 1, -1), LOW (+-1, 0, 1), origin at the foot). Which crossings get it is
/// <see cref="BridgeStructures.KitFor"/>'s choice, among the crossings it fits (<see cref="StoneFits"/>).
///
/// <para>In the bridge's own frame (along the deck from its near end, across it to the left, height), on the deck's 2 m
/// bays, each bay a pier bay, half an arch, or the open middle of a 6 m opening (<see cref="StoneBays"/>):</para>
/// <list type="bullet">
/// <item><b>deck</b>: two lanes of <see cref="StoneFloorPrefab"/> (2 x 1 x 2 m, origin at its centre) across
/// +-<see cref="LaneOffset"/>, their top the walking surface, a metre off the bays: centred on the joints between bays, so
/// each floor rests half on each of the two bays under it (half on each arch's crown over the middle of an opening, half on
/// a crown and half open over a 6 m one's), and at each end of the deck a 1 m strip of <see cref="StonePierBlock"/>s laid
/// across it on the end pier, a block a lane;</item>
/// <item><b>piers</b>: in a pier bay, courses of four <see cref="StonePierBlock"/> blocks (2 x 1 x 1 m) filling the bay's
/// 2 x 4 m under the deck, the top course level with the arches, stacked down to the ground; bonded, the two blocks laid
/// across the deck on one side and the two laid along it on the other, the sides swapping every course;</item>
/// <item><b>arches</b>: in an arch bay, four <see cref="StoneArchPrefab"/> abreast under the deck, the tall end on the pier
/// beside the bay; two arch bays between piers meet at their crowns, one arch over a 4 m opening, or stand 2 m apart, their
/// crowns either side of an open bay, over a 6 m one;</item>
/// <item><b>railing</b>: centred on each deck edge (across +-<see cref="DeckHalfWidth"/>, half over the deck), a course
/// level with the deck of the bay's own piece (an arch over an arch bay, a block over a pier bay, none over an open bay,
/// where nothing would carry it), and a top course of blocks over it, a metre higher, its joints on the bays' middles, over
/// an open bay resting half on each arch's crown as the floors do;</item>
/// <item><b>ends</b>: the stairs as the road meets the end (<see cref="LayEndStairs"/>), two abreast in
/// <see cref="StoneStairPrefab"/>, each step on a footing of <see cref="StonePillarPrefab"/>s (2 along by 4 across, their
/// tops at the step's foot) stacked down to the ground, and beside each step on each side a railing block from its foot to
/// its top, the railing stepping down the run; the top course's last block reaches half over the first step. With no
/// stairs laid the railing ends at the deck's end.</item>
/// <item><b>over a river</b>: whole openings and the piers between them left open over the ship's way
/// (<see cref="StoneNavigationBays"/>) but for the half arch springing from each pier either side, so the opening reads
/// as a span left open between two arch springings: each half arch's four arches under the deck, the deck over it ending
/// on a strip on its crown, the railing's lower course over it to the crown, its top course a metre short of it, as at a
/// deck end without stairs.</item>
/// </list>
/// <para><b>Nothing is buried</b>: a piece wholly under the ground as the road leaves it (<see cref="RailedGround"/>) is
/// not laid (<see cref="BuriedPieces.Buried"/>), and a pier or a footing ends at the first course or pillar whose foot is in
/// the ground. <b>The ruin</b> is every kit's (<see cref="BridgeRuin"/>, keyed by what it decides), more missing
/// toward mid-span: floors at its deck share, the railing's two courses at its course share, the arches under the deck at
/// its support share, now and then a joint open from side to side (both floors and the top course over them) at its hole
/// share, never two within three joints and never two joints running open, so every gap is 2 m, which a player jumps;
/// every piece is worn as a vanilla location's (<see cref="Ruin.ApplyLocationDamage"/>). The floors and railing over the
/// bank bays, the end strips, the steps, their footing and the railing beside them always stand, and so do the piers: a
/// pier is courses bonded down to the ground, which no ruin of the deck brings down, as vanilla's masonry piers stand. Every piece
/// left is on a neighbour's snap points: a railing piece with no neighbour left in its course has one put back, as a top block
/// with nothing under it has the edge piece under it (and one beside it), so the railing is lost at its course share; a floor with no neighbour
/// has the other lane's floor put back. A floor resting on no pier, no arch and not between two floors has an arch under it
/// put back. The complete plan (<see cref="SolveStoneComplete"/>) is
/// what a player can repair it to.</para>
/// </summary>
public static partial class BridgeLayout
{
    /// <summary>The deck: 2 x 1 x 2 m, origin at its centre, snaps at its eight corners (+-1, +-0.5, +-1).</summary>
    public const string StoneFloorPrefab = "stone_floor_2x2";
    /// <summary>The piers' and the railing's block: 2 m along its local x, 1 m high and deep, origin at its centre, snaps at
    /// its eight corners (+-1, +-0.5, +-0.5).</summary>
    public const string StonePierBlock = "stone_wall_2x1";
    /// <summary>The arch: 2 m along its local x, 1 m deep, its tall end (+x) 1 m high, snaps there top and bottom
    /// (1, +-0.5, +-0.5), its crown (-x) thin, snaps on top (-1, 0.5, +-0.5); origin at the centre of its bounds.</summary>
    public const string StoneArchPrefab = "stone_arch";
    /// <summary>The steps: the shared stair's geometry (<see cref="StairRun"/>, <see cref="StairRise"/>), origin at the
    /// foot, rising toward its local -z; snaps HIGH (+-1, 1, -1), LOW (+-1, 0, 1) and under its top edge (+-1, 0, -1).</summary>
    public const string StoneStairPrefab = "stone_stair";
    /// <summary>The stairs' footing: 1 x 2 x 1 m, origin at its centre, snaps at its eight corners (+-0.5, +-1, +-0.5).</summary>
    public const string StonePillarPrefab = "stone_pillar";
    public const float StonePillarHeight = 2f;

    /// <summary>The deck's depth over a pier's top course and the arches: the floor's metre (the arches and the top course
    /// stand the metre under it).</summary>
    public const float StoneFloorDepth = 1f;

    /// <summary>The top course is rolled lost at this multiple of the course share: the blocks put back under and beside the
    /// ones left return about as much, so the course is lost at its share.</summary>
    private const float RailTopRolled = 1.25f;
    /// <summary>The edge course is rolled lost at the course share times the first of these steps (a quarter more each, 21 of
    /// them) that leaves it, after the pieces put back, nearest the loss its share alone says: the pieces put back under the
    /// top blocks and beside the lone ones return a layout's own part of the rolled loss.</summary>
    private const float RailEdgeRolledStep = 0.25f;
    private const int RailEdgeRolledSteps = 21;

    /// <summary>The stone kit's ruin rolls, apart from every other kit's (<see cref="Ruin"/>).</summary>
    internal const int RoleStoneArch = 29, RoleStoneHole = 30, RoleStoneFloorBack = 31;

    /// <summary>The deck's floors are laid this far along from the bays: centred on the joints between them.</summary>
    public const float StoneFloorOffset = 1f;

    /// <summary>What a 2 m bay of the stone kit's deck stands on.</summary>
    public enum StoneBay
    {
        /// <summary>A pier: its courses down to the ground.</summary>
        Pier,
        /// <summary>The near half of an opening: an arch whose tall end stands on the pier toward the near end.</summary>
        ArchNear,
        /// <summary>The far half of an opening: an arch whose tall end stands on the pier toward the far end.</summary>
        ArchFar,
        /// <summary>The middle of a 6 m opening, between the two arches' crowns: nothing under the deck, the floors either side
        /// resting half on a crown.</summary>
        Open,
    }

    /// <summary>
    /// The stone kit's bays over a deck of <paramref name="bays"/> 2 m bays, near end first, symmetrical about the middle:
    /// a pier bay at each end and one between openings, each opening 4 m (two arches meeting at their crowns) or 6 m (the
    /// two arches 2 m apart, an open bay between them, the floors offset onto their crowns,
    /// <see cref="StoneFloorOffset"/>), so a deck is made up in 2 m steps. The 6 m openings are the ones nearest the middle,
    /// a mirrored pair at a time (or the middle one alone). Where no such layout is symmetrical the piers are widened, as few
    /// bays as can be: each end's pier by one, or the middle pier (between an even number of openings) by one. Of the layouts
    /// with the fewest widened bays, the most openings, then the fewest 6 m ones. Null where none can be laid (2, 4 and
    /// 6 m).
    /// </summary>
    public static StoneBay[]? StoneBays(int bays)
    {
        // bays = 2 x end + middle + 3 x openings - 1 + long openings.
        for (int widen = 0; widen <= bays; widen++)
            for (int openings = bays / 3; openings >= 1; openings--)
                for (int end = 1; 2 * (end - 1) <= widen; end++)
                {
                    int middle = widen - 2 * (end - 1);
                    if (middle > 1 || (middle == 1 && openings % 2 == 1)) continue;
                    int longs = bays - 2 * end - middle - 3 * openings + 1;
                    if (longs < 0 || longs > openings || (openings % 2 == 0 && longs % 2 == 1)) continue;
                    // The long ones nearest the middle: the middle opening (an odd number of openings) when there is an odd
                    // number of them, then mirrored pairs outward.
                    var isLong = new bool[openings];
                    int left = longs;
                    if (openings % 2 == 1 && longs % 2 == 1) { isLong[openings / 2] = true; left--; }
                    for (int near = openings / 2 - 1; near >= 0 && left > 0; near--, left -= 2)
                        isLong[near] = isLong[openings - 1 - near] = true;
                    var layout = new List<StoneBay>();
                    for (int i = 0; i < end; i++) layout.Add(StoneBay.Pier);
                    for (int i = 0; i < openings; i++)
                    {
                        layout.Add(StoneBay.ArchNear);
                        if (isLong[i]) layout.Add(StoneBay.Open);
                        layout.Add(StoneBay.ArchFar);
                        if (i == openings - 1) continue;
                        layout.Add(StoneBay.Pier);
                        if (middle == 1 && i == openings / 2 - 1) layout.Add(StoneBay.Pier);
                    }
                    for (int i = 0; i < end; i++) layout.Add(StoneBay.Pier);
                    return layout.ToArray();
                }
        return null;
    }

    /// <summary>The stone railing's top course over the walking surface: a metre up.</summary>
    public const float StoneRailOverDeck = 1f;

    /// <summary>The stone deck from its walking surface down to the arches' feet beside the piers (the floor's metre and
    /// the arch's tall end under it), as the ship's way measures it.</summary>
    public const float StoneDeckDepth = StoneFloorDepth + 1f;

    /// <summary>The ship's way under a stone deck: <see cref="NavigationChannel"/> for its deck (<see cref="WoodDeckHeight"/>,
    /// <see cref="StoneDeckDepth"/>). Null off a river.</summary>
    public static ShipWay? StoneShipWay(RoadCrossing crossing, WorldGenerator world)
    {
        if (crossing == null || world == null || crossing.Kind != CrossingKind.Bridge) return null;
        float deck = WoodDeckHeight(crossing, world);
        return NavigationChannel(crossing, world, deck - StoneDeckDepth, deck, BridgeStructures.Kit.Stone);
    }

    /// <summary>
    /// The bays a stone deck opens over a river from pier face to pier face (<paramref name="first"/>,
    /// <paramref name="count"/>; none with no ship's way), or false where it can open none and the kit does not fit. A stone
    /// deck carries nothing wider than a 6 m opening, so over the ship's way it is open: whole openings and the piers between
    /// them, the fewest that hold the way, the run nearest the way's centre, as a railed kit's bays (<see cref="OpenBays"/>)
    /// on the stone kit's own grid. The run's first and last bays are the half arches springing from the piers either side,
    /// which stand with the deck and railing over them (<see cref="StoneGapHalfArches"/>); the rest is open. The run holds
    /// the way when its span from pier face to pier face holds the way kept free of piers
    /// (<see cref="ShipWay.PierWidth"/>, which the deck's gap is within) and the <see cref="FairwayClearance"/> each side,
    /// and the ship passes the half arches at every height (<see cref="StoneGapHoldsShip"/>). A ship passing under the deck
    /// would need its piers out of a way no stone opening spans, so there the kit does not fit; stone's support never lifts
    /// its deck that high anyway (<see cref="BridgeSupport.StoneMaxPierCourses"/> courses under a deck 0.5 m over the water
    /// at the least).
    /// </summary>
    public static bool StoneNavigationBays(RoadCrossing crossing, WorldGenerator world, StoneBay[] layout, out int first, out int count)
    {
        first = count = 0;
        if (StoneShipWay(crossing, world) is not ShipWay way) return true;
        if (way.DeckWidth <= 0f) return false;
        var openings = new List<(int from, int to)>();
        for (int j = 0; j < layout.Length; j++)
        {
            if (layout[j] == StoneBay.Pier) continue;
            int k = j;
            while (k + 1 < layout.Length && layout[k + 1] != StoneBay.Pier) k++;
            openings.Add((j, k));
            j = k;
        }
        float deck = WoodDeckHeight(crossing, world);
        float lo = way.Centre - way.PierWidth * 0.5f - FairwayClearance, hi = way.Centre + way.PierWidth * 0.5f + FairwayClearance;
        for (int n = 1; n <= openings.Count; n++)
        {
            float best = float.MaxValue;
            for (int a = 0; a + n <= openings.Count; a++)
            {
                float from = openings[a].from * DeckSpan, to = (openings[a + n - 1].to + 1) * DeckSpan;
                if (from > lo || to < hi || !StoneGapHoldsShip(way, from, to, deck - crossing.WaterLevel)) continue;
                float off = Mathf.Abs((from + to) * 0.5f - way.Centre);
                if (off >= best) continue;
                best = off;
                first = openings[a].from;
                count = openings[a + n - 1].to - first + 1;
            }
            if (best < float.MaxValue) return true;
        }
        return false;
    }

    /// <summary>The half arches a stone deck keeps either side of its opening over a river: the first and last of the bays
    /// <see cref="StoneNavigationBays"/> opens, each springing from the pier beside it, a whole arch bay
    /// (<see cref="DeckSpan"/>) reaching into the opening.</summary>
    public const int StoneGapHalfArches = 1;

    /// <summary>
    /// How far a stone_arch reaches out from the face it springs from (its tall end) at a height
    /// <paramref name="overCentre"/> over its origin, taking every height under that: what a ship's part whose top stands
    /// there meets. Read off the prefab's three colliders (ValheimRIP stone_arch): the tall end, a box 0.377 m deep along
    /// the arch, full height; a strut leaning from it (its underside from 0.343 m out at 0.369 m under the origin to 0.815 m
    /// out at 0.247 m over it); and the crown's slab along the whole 2 m, its underside 0.271 m over the origin. Nothing
    /// under the arch's foot (0.5 m under its origin).
    /// </summary>
    public static float StoneArchReach(float overCentre)
    {
        if (overCentre <= -0.5f) return 0f;
        if (overCentre >= 0.271f) return 2f;
        if (overCentre >= 0.247f) return 0.815f;
        if (overCentre <= -0.369f) return 0.377f;
        return Mathf.Max(0.377f, 0.343f + (overCentre + 0.369f) / (0.247f + 0.369f) * (0.815f - 0.343f));
    }

    /// <summary>
    /// Whether <see cref="Ships.RiverShip"/> passes a stone deck's opening from pier face <paramref name="from"/> to pier
    /// face <paramref name="to"/> (metres along the deck), with the half arch springing from each face into it and the deck
    /// and railing over them, its walking surface <paramref name="deck"/> over the water: at each of the ship's height bands
    /// that meets the structure, the band's width and <see cref="Ships.PassingMargin"/> and <see cref="FairwayClearance"/>
    /// each side lie between what reaches furthest into the opening at those heights. That is the half arch under the deck
    /// (<see cref="StoneArchReach"/>, its origin 1.5 m under the walking surface), the floors, strips and the railing's lower
    /// course over it (2 m, to its crown, from the deck's underside to its surface) and the railing's top course (a metre,
    /// from the surface a metre up). The piers reach no further than their faces.
    /// </summary>
    public static bool StoneGapHoldsShip(ShipWay way, float from, float to, float deck)
    {
        float underside = deck - StoneFloorDepth, archCentre = deck - StoneDeckDepth + 0.5f;
        foreach (Ships.Band b in Ships.RiverShip.Bands)
        {
            if (b.Bottom >= deck + StoneRailOverDeck) continue;   // over the railing: nothing there
            float reach = 0f;
            if (b.Top > deck && b.Bottom < deck + StoneRailOverDeck) reach = Mathf.Max(reach, 1f);
            if (b.Top > underside && b.Bottom < deck) reach = Mathf.Max(reach, StoneGapHalfArches * DeckSpan);
            if (b.Bottom < underside) reach = Mathf.Max(reach, StoneArchReach(Mathf.Min(b.Top, underside) - archCentre));
            float half = b.Width * 0.5f + Ships.PassingMargin + FairwayClearance;
            if (from + reach > way.Centre - half + 1e-3f || to - reach < way.Centre + half - 1e-3f) return false;
        }
        return true;
    }

    /// <summary>Whether a point this far along a stone deck lies in its opening over a river
    /// (<see cref="StoneNavigationBays"/>), strictly between the crowns of the half arches either side, where the deck ends.
    /// The complete plan leaves it open too: its piers there could stand past stone's support.</summary>
    public static bool InStoneNavigationGap(RoadCrossing crossing, WorldGenerator world, float along)
    {
        if (!Railed(crossing) || world == null || StoneBays(Bays(crossing.Width)) is not StoneBay[] layout) return false;
        return StoneNavigationBays(crossing, world, layout, out int first, out int count) && count > 2 * StoneGapHalfArches
            && along > (first + StoneGapHalfArches) * DeckSpan + 1e-3f && along < (first + count - StoneGapHalfArches) * DeckSpan - 1e-3f;
    }

    /// <summary>
    /// Whether the stone kit can be laid over a crossing, as the network saved it: a bridge or a ford's span
    /// (<see cref="Railed"/>) whose deck takes the kit's bays (<see cref="StoneBays"/>), over a river with whole openings
    /// left open over the ship's way (<see cref="StoneNavigationBays"/>), every pier that stands within
    /// <see cref="BridgeSupport.StoneMaxPierCourses"/> and every stair footing within
    /// <see cref="BridgeSupport.StoneMaxFootingPillars"/>. Where it does not, another kit is chosen
    /// (<see cref="BridgeStructures.KitFor"/>).
    /// </summary>
    public static bool StoneFits(RoadCrossing crossing, WorldGenerator world)
    {
        if (!Railed(crossing) || world == null || crossing.Width < DeckSpan) return false;
        int bays = Bays(crossing.Width);
        if (StoneBays(bays) is not StoneBay[] layout) return false;
        if (!StoneNavigationBays(crossing, world, layout, out int open, out int opened)) return false;
        var f = new StoneFrame(crossing, world);
        for (int j = 0; j < bays; j++)
            if (layout[j] == StoneBay.Pier && (j < open || j >= open + opened) && f.PierCourses(j) > BridgeSupport.StoneMaxPierCourses())
                return false;
        foreach (StructureEnds.End end in new[] { f.Near, f.Far })
            for (int k = 0; k < end.Steps; k++)
                foreach (Vector2 p in StoneFooting(end, f.Side, k))
                    if (f.FootingPillars(p, end.Height - StairRise * (k + 1)) > BridgeSupport.StoneMaxFootingPillars()) return false;
        return true;
    }

    /// <summary>The stone bridge as it stands, ruined (the game's plan).</summary>
    public static List<StructurePiece> SolveStone(RoadCrossing crossing, WorldGenerator world, int worldSeed) =>
        SolveStone(crossing, world, worldSeed, ruin: true);

    /// <summary>The stone bridge with nothing missing but its opening over a river: what a player can repair it to.</summary>
    internal static List<StructurePiece> SolveStoneComplete(RoadCrossing crossing, WorldGenerator world, int worldSeed) =>
        SolveStone(crossing, world, worldSeed, ruin: false);

    private static List<StructurePiece> SolveStone(RoadCrossing crossing, WorldGenerator world, int worldSeed, bool ruin)
    {
        var pieces = new List<StructurePiece>();
        if (!Railed(crossing) || world == null || crossing.Width < DeckSpan) return pieces;
        int bays = Bays(crossing.Width);
        if (StoneBays(bays) is not StoneBay[] layout) return pieces;
        if (!StoneNavigationBays(crossing, world, layout, out int gapFirst, out int gapCount)) return pieces;
        // The bays open over a river: the run StoneNavigationBays opens but its half arches either side, which stand with
        // the deck and railing over them.
        int open = gapCount > 0 ? gapFirst + StoneGapHalfArches : 0, opened = Mathf.Max(0, gapCount - 2 * StoneGapHalfArches);
        // A bay open over a river, and a joint with no deck over it (strictly inside the opening).
        bool Opened(int j) => j >= open && j < open + opened;
        bool NoDeck(int i) => opened > 0 && i > open && i < open + opened;
        var f = new StoneFrame(crossing, world);
        Ruin r = new(worldSeed, crossing);
        float deckH = f.DeckH;
        float mid = bays * 0.5f;
        float Survival(float at) => SpanSurvival(1f - Mathf.Abs(at - mid) / mid);
        RuinProfile shares = BridgeRuin;
        float Survives(float at, float share) => RuinProfile.Survives(Survival(at), share);
        float ArchYaw(StoneBay bay) => bay == StoneBay.ArchNear ? f.Heading + 90f : f.Heading - 90f;   // its tall end (+x) on its pier
        float underside = deckH - StoneFloorDepth;

        // The pier bays, each with its top course laid or not (wholly buried).
        var pierTop = new bool[bays];

        // The deck's floors on the joints between bays (lane 0 left, 1 right), and a 1 m strip where the deck ends, at each
        // end on its pier and either side of a river's opening on the half arch's crown, centred this far along: lost toward
        // mid-span, never a strip or a floor over a bank's bay.
        var strip = new float?[bays + 1];
        strip[0] = StoneFloorOffset * 0.5f;
        strip[bays] = bays * DeckSpan - StoneFloorOffset * 0.5f;
        if (opened > 0)
        {
            strip[open] = open * DeckSpan - StoneFloorOffset * 0.5f;
            strip[open + opened] = (open + opened) * DeckSpan + StoneFloorOffset * 0.5f;
        }
        var floor = new bool[bays + 1, 2];
        for (int i = 0; i <= bays; i++)
            for (int l = 0; l < 2; l++)
                floor[i, l] = !NoDeck(i) && (!ruin || strip[i] != null || i <= 1 || i >= bays - 1
                    || r.Roll(i, l == 0 ? Ruin.Left : Ruin.Right, Ruin.RoleDeck) < Survives(i, shares.DeckShare));
        // Now and then a joint open from side to side, both floors and the top course over them: never over a bank's bay,
        // never two within three joints.
        var hole = new bool[bays + 1];
        if (ruin)
            for (int i = 2, last = -3; i <= bays - 2; i++)
                if (i - last >= 3 && strip[i] == null && !NoDeck(i) && r.Roll(i, Ruin.Centre, RoleStoneHole) >= Survives(i, shares.HoleShare))
                {
                    hole[i] = true;
                    floor[i, 0] = floor[i, 1] = false;
                    last = i;
                }
        // A way across a player jumps: never two joints running open from side to side; a floor is put back in the one that
        // is not a hole.
        for (int i = 2; i < bays; i++)
            if (!NoDeck(i) && !NoDeck(i - 1) && !floor[i, 0] && !floor[i, 1] && !floor[i - 1, 0] && !floor[i - 1, 1])
            {
                int b = hole[i] ? i - 1 : i;
                floor[b, r.Roll(b, Ruin.Centre, RoleStoneFloorBack) < 0.5f ? 0 : 1] = true;
            }

        for (int j = 0; j < bays; j++)
        {
            if (layout[j] != StoneBay.Pier || Opened(j)) continue;
            float u = j * DeckSpan + DeckSpan * 0.5f;
            // Courses down to the ground, the top one level with the arches; bonded, the side whose two blocks lie across the
            // deck swapping every course. Never past the support limit: the kit fitted when it was chosen
            // (StoneFits), but ground lowered since (a road cut under a pier) leaves the courses past the
            // limit, at the foot, out rather than the railing over the arches losing its support.
            int courses = Mathf.Min(f.PierCourses(j), BridgeSupport.StoneMaxPierCourses());
            for (int c = 0; c < courses; c++)
            {
                float y = underside - 0.5f - c;
                float across = c % 2 == 0 ? -1f : 1f;
                bool a = f.Lay(pieces, StructurePieceKind.Post, StonePierBlock, u - 0.5f, across, y, f.Heading, r.Health(j, c, Ruin.RoleColumnHealth, 0));
                bool b = f.Lay(pieces, StructurePieceKind.Post, StonePierBlock, u + 0.5f, across, y, f.Heading, r.Health(j, c, Ruin.RoleColumnHealth, 1));
                if (c == 0) pierTop[j] = a && b;
                f.Lay(pieces, StructurePieceKind.Post, StonePierBlock, u, -across * 0.5f, y, f.AlongYaw, r.Health(j, c, Ruin.RoleColumnHealth, 2));
                f.Lay(pieces, StructurePieceKind.Post, StonePierBlock, u, -across * 1.5f, y, f.AlongYaw, r.Health(j, c, Ruin.RoleColumnHealth, 3));
            }
        }

        // Every floor left on a neighbour's snap points (its corners on the bays' middles): the other lane's floor at its
        // joint, its lane's floors either side (or an end strip), or, in the left lane, the top course of a pier under it,
        // whose blocks laid across the deck have corners there. A floor with none of them has the other lane's put back.
        for (int i = 2; i <= bays - 2; i++)
            for (int l = 0; l < 2; l++)
            {
                bool onPier = l == 0 && (pierTop[i - 1] || pierTop[i]);
                if (strip[i] == null && floor[i, l] && !floor[i, 1 - l] && !floor[i - 1, l] && !floor[i + 1, l] && !onPier) floor[i, 1 - l] = true;
            }

        // The arches under the deck: lost toward mid-span. Each stands on its pier by its tall end, so none is left loose;
        // but a floor that rests on no pier, no arch of its lane and not between two floors of its lane has one of the arches
        // under it put back.
        var arch = new bool[bays, 4];
        for (int j = 0; j < bays; j++)
            if (layout[j] is StoneBay.ArchNear or StoneBay.ArchFar && !Opened(j))
                for (int k = 0; k < 4; k++)
                    arch[j, k] = !ruin || r.Roll(j, k, RoleStoneArch) < Survives(j + 0.5f, shares.SupportShare);
        // A strip on a half arch's crown over a river rests on one of the two arches of its lane under it: where the rolls
        // took both, one is put back.
        if (opened > 0)
            foreach (var (joint, bay) in new[] { (open, open - 1), (open + opened, open + opened) })
                for (int l = 0; l < 2; l++)
                    if (floor[joint, l] && !arch[bay, 2 * l] && !arch[bay, 2 * l + 1])
                        arch[bay, 2 * l + (r.Roll(joint, l, RoleStoneArch, 3) < 0.5f ? 0 : 1)] = true;
        for (int i = 1; i < bays; i++)
            for (int l = 0; l < 2; l++)
            {
                if (!floor[i, l] || strip[i] != null || layout[i - 1] == StoneBay.Pier || layout[i] == StoneBay.Pier) continue;
                if (arch[i - 1, 2 * l] || arch[i - 1, 2 * l + 1] || arch[i, 2 * l] || arch[i, 2 * l + 1]) continue;
                if (floor[i - 1, l] && floor[i + 1, l]) continue;
                int bay = layout[i - 1] == StoneBay.Open ? i : layout[i] == StoneBay.Open ? i - 1
                    : r.Roll(i, l, RoleStoneArch, 1) < 0.5f ? i - 1 : i;
                arch[bay, 2 * l + (r.Roll(i, l, RoleStoneArch, 2) < 0.5f ? 0 : 1)] = true;
            }
        for (int j = 0; j < bays; j++)
            for (int k = 0; k < 4; k++)
                if (arch[j, k])
                    f.Lay(pieces, StructurePieceKind.Beam, StoneArchPrefab, j * DeckSpan + DeckSpan * 0.5f, k - 1.5f, underside - 0.5f,
                        ArchYaw(layout[j]), r.Health(j, k, Ruin.RoleBeamHealth));

        // The deck: the floors on the joints, the strips (a block laid across each lane) on the piers where the deck ends.
        for (int i = 0; i <= bays; i++)
            for (int l = 0; l < 2; l++)
            {
                if (!floor[i, l]) continue;
                int lane = l == 0 ? Ruin.Left : Ruin.Right;
                if (strip[i] is float at)
                    f.Lay(pieces, StructurePieceKind.Deck, StonePierBlock, at, lane * LaneOffset, deckH - StoneFloorDepth * 0.5f, f.Heading,
                        r.Health(i, lane, Ruin.RoleDeckHealth));
                else
                    f.Lay(pieces, StructurePieceKind.Deck, StoneFloorPrefab, i * DeckSpan, lane * LaneOffset, deckH - StoneFloorDepth * 0.5f,
                        f.AlongYaw, r.Health(i, lane, Ruin.RoleDeckHealth));
            }

        // Both ends as the road meets them: the stairs laid (LayEndStairs), each step on its footing, the railing stepping
        // down beside it; none where the road meets the end level.
        int nearSteps = EmitStoneEnd(pieces, f, f.Near, r, 0);
        int farSteps = EmitStoneEnd(pieces, f, f.Far, r, bays + 1);

        // The railing: the course beside the deck (a piece a bay, none over an open bay) and the top course over it (a block
        // on every bay joint, the end ones half over the first step where there are stairs). In the ruin pieces go missing
        // toward mid-span at the course share, and the top course over a joint open from side to side, never at a bank's
        // bay. A piece left with no neighbour in its course, or a top block with nothing under it, has one put back (never
        // dropped), so every piece left is on its neighbour's snap points as a player's hammer puts it, and the course
        // is lost at about its share.
        var edge = new bool[bays, 2];
        var top = new bool[bays + 1, 2];
        bool CanEdge(int j) => j >= 0 && j < bays && layout[j] != StoneBay.Open && !Opened(j);
        bool Laid(int i) => (i > 0 || nearSteps > 0) && (i < bays || farSteps > 0) && (opened == 0 || i < open || i > open + opened);
        bool CanHold(int i) => (i == 0 && nearSteps > 0) || (i == bays && farSteps > 0) || CanEdge(i - 1) || CanEdge(i);
        bool CanTop(int i) => i >= 0 && i <= bays && Laid(i) && !hole[i] && CanHold(i);
        void Rail(float edgeRolled)
        {
            for (int k = 0; k < 2; k++)
            {
                int s = k == 0 ? Ruin.Left : Ruin.Right;
                for (int j = 0; j < bays; j++)
                    edge[j, k] = CanEdge(j)
                        && (!ruin || j == 0 || j == bays - 1 || r.Roll(j, s, RoleRail) < Survives(j + 0.5f, shares.CourseShare * edgeRolled));
                for (int i = 0; i <= bays; i++)
                    top[i, k] = Laid(i) && !hole[i]
                        && (!ruin || i <= 1 || i >= bays - 1 || r.Roll(i, s, RoleRail, 1) < Survives(i, shares.CourseShare * RailTopRolled));
                if (!ruin) continue;
                int Pick(int low, int high, bool canLow, bool canHigh, int at, int extra) =>
                    canLow && canHigh ? (r.Roll(at, s, RoleRail, extra) < 0.5f ? low : high) : canLow ? low : canHigh ? high : -1;
                for (bool changed = true; changed;)
                {
                    changed = false;
                    for (int j = 0; j < bays; j++)
                    {
                        bool before = (j > 0 && edge[j - 1, k]) || (j == 0 && nearSteps > 0);
                        bool after = (j < bays - 1 && edge[j + 1, k]) || (j == bays - 1 && farSteps > 0);
                        if (!edge[j, k] || before || after) continue;
                        int put = Pick(j - 1, j + 1, CanEdge(j - 1), CanEdge(j + 1), j, 2);
                        if (put >= 0) edge[put, k] = true; else edge[j, k] = false;
                        changed = true;
                    }
                    for (int i = 0; i <= bays; i++)
                    {
                        if (!top[i, k]) continue;
                        bool held = (i > 0 && edge[i - 1, k]) || (i < bays && edge[i, k]) || (i == 0 && nearSteps > 0) || (i == bays && farSteps > 0);
                        if (!held)
                        {
                            int under = Pick(i - 1, i, CanEdge(i - 1), CanEdge(i), i, 3);
                            if (under < 0) { top[i, k] = false; changed = true; continue; }
                            edge[under, k] = true;
                            changed = true;
                        }
                        bool beside = (i > 0 && top[i - 1, k]) || (i < bays && top[i + 1, k]);
                        if (beside) continue;
                        int next = Pick(i - 1, i + 1, CanTop(i - 1), CanTop(i + 1), i, 4);
                        if (next >= 0) top[next, k] = true; else top[i, k] = false;
                        changed = true;
                    }
                }
            }
        }
        Rail(1f);
        if (ruin)
        {
            float wanted = 0f;
            for (int j = 1; j < bays - 1; j++)
                if (CanEdge(j)) wanted += 2f * (1f - Survives(j + 0.5f, shares.CourseShare));
            float Off()
            {
                int lost = 0;
                for (int j = 0; j < bays; j++)
                    for (int k = 0; k < 2; k++)
                        if (CanEdge(j) && !edge[j, k]) lost++;
                return Mathf.Abs(lost - wanted);
            }
            float nearest = Off(), best = 1f;
            for (int n = 1; n < RailEdgeRolledSteps; n++)
            {
                float rolled = 1f + n * RailEdgeRolledStep;
                Rail(rolled);
                float off = Off();
                if (off < nearest) { nearest = off; best = rolled; }
            }
            Rail(best);
        }
        for (int k = 0; k < 2; k++)
        {
            int s = k == 0 ? Ruin.Left : Ruin.Right;
            for (int j = 0; j < bays; j++)
            {
                if (!edge[j, k]) continue;
                float u = j * DeckSpan + DeckSpan * 0.5f;
                bool pier = layout[j] == StoneBay.Pier;
                f.Lay(pieces, StructurePieceKind.Beam, pier ? StonePierBlock : StoneArchPrefab, u, s * DeckHalfWidth, deckH - 0.5f,
                    pier ? f.AlongYaw : ArchYaw(layout[j]), r.Health(j, s, RoleRailHealth));
            }
            for (int i = 0; i <= bays; i++)
                if (top[i, k])
                    f.Lay(pieces, StructurePieceKind.Beam, StonePierBlock, i * DeckSpan, s * DeckHalfWidth, deckH + 0.5f, f.AlongYaw,
                        r.Health(i, s, RoleRailHealth, 1));
        }
        if (ruin) r.ApplyLocationDamage(pieces);   // vanilla's Location.m_applyRandomDamage, as the viaduct's
        return pieces;
    }

    /// <summary>
    /// One end: its stairs as the road meets it (<see cref="LayEndStairs"/>, in <see cref="StoneStairPrefab"/>), each step on
    /// its footing (<see cref="StoneFooting"/>: stone_pillars, their tops at the step's foot, stacked down to the ground), and
    /// beside each step on each side a railing block from the step's foot to its top, each on the corner of the one above:
    /// the railing stepping down the run. Returns the steps laid.
    /// </summary>
    private static int EmitStoneEnd(List<StructurePiece> pieces, StoneFrame f, StructureEnds.End end, Ruin r, int bayKey)
    {
        int steps = LayEndStairs(pieces, f.Ground, end, f.Side, r, bayKey, StoneStairPrefab, carry: false);
        for (int k = 0; k < steps; k++)
        {
            float foot = end.Height - StairRise * (k + 1);
            int n = 0;
            foreach (Vector2 p in StoneFooting(end, f.Side, k))
            {
                // Within the support limit, as a pier's courses: the pillars past it, at the foot, are left out.
                int pillars = Mathf.Min(f.FootingPillars(p, foot), BridgeSupport.StoneMaxFootingPillars());
                for (int m = 0; m < pillars; m++)
                    f.LayAt(pieces, StructurePieceKind.Post, StonePillarPrefab, p, foot - StonePillarHeight * (m + 0.5f), f.Heading,
                        r.Health(bayKey, n, Ruin.RolePostHealth, k * 16 + m));
                n++;
            }
            Vector2 c = end.Edge - end.Up * (StairHalfRun + k * StairRun);
            foreach (int s in new[] { Ruin.Left, Ruin.Right })
                f.LayAt(pieces, StructurePieceKind.Beam, StonePierBlock, c + f.Side * (s * DeckHalfWidth), foot + 0.5f, f.AlongYaw,
                    r.Health(bayKey, s, RoleRailHealth, k + 2));
        }
        return steps;
    }

    /// <summary>Where the pillars under an end's step <paramref name="k"/> stand: 2 along the step by 4 across the deck,
    /// each a metre square.</summary>
    private static IEnumerable<Vector2> StoneFooting(StructureEnds.End end, Vector2 side, int k)
    {
        Vector2 c = end.Edge - end.Up * (StairHalfRun + k * StairRun);
        foreach (float along in new[] { -0.5f, 0.5f })
            foreach (float across in new[] { -1.5f, -0.5f, 0.5f, 1.5f })
                yield return c + end.Up * along + side * across;
    }

    /// <summary>A crossing in the stone kit's frame: its deck, its ends as met, and the ground its piers and footings stand
    /// on (<see cref="RailedGround"/>).</summary>
    private sealed class StoneFrame
    {
        public readonly WorldGenerator World;
        public readonly Vector2 From, Dir, Side;
        /// <summary>The deck's heading; <see cref="AlongYaw"/> turns a piece's local x along the deck.</summary>
        public readonly float Heading, AlongYaw, DeckH;
        public readonly StructureEnds.End Near, Far;
        private readonly System.Func<Vector2, float> m_ground;
        private readonly System.Func<int, int, float> m_vertex;
        /// <summary>The ground the bridge is laid on (<see cref="RailedGround"/>).</summary>
        public System.Func<Vector2, float> Ground => m_ground;

        public StoneFrame(RoadCrossing crossing, WorldGenerator world)
        {
            World = world;
            From = crossing.FromBank;
            Dir = crossing.Direction;
            Side = new Vector2(-Dir.y, Dir.x);
            Heading = YawDegrees(Dir);
            AlongYaw = Heading + 90f;
            DeckH = WoodDeckHeight(crossing, world);
            (Near, Far) = StructureEnds.Of(crossing, world);
            System.Func<Vector2, float> ground = RailedGround(Near, Far, world);
            m_ground = ground;
            m_vertex = (i, k) => ground(new Vector2(i, k));
        }

        public Vector2 At(float along, float across) => From + Dir * along + Side * across;

        /// <summary>The courses a pier bay stands: from the top one, level with the arches, down to the first whose foot is
        /// in the lowest ground under the bay's 2 x 4 m.</summary>
        public int PierCourses(int bay)
        {
            float low = float.PositiveInfinity;
            for (int a = 0; a <= 2; a++)
                for (int s = -1; s <= 1; s++)
                    low = Mathf.Min(low, m_ground(At(bay * DeckSpan + a, s * DeckHalfWidth)));
            float top = DeckH - StoneFloorDepth;
            int courses = 1;
            while (top - courses > low + BuriedPieces.Growth && courses < 256) courses++;
            return courses;
        }

        /// <summary>The pillars of a step's footing at <paramref name="p"/>: from the step's <paramref name="foot"/> down to
        /// the first whose foot is in the ground.</summary>
        public int FootingPillars(Vector2 p, float foot)
        {
            float ground = m_ground(p);
            int pillars = 1;
            while (foot - StonePillarHeight * pillars > ground + BuriedPieces.Growth && pillars < 256) pillars++;
            return pillars;
        }

        /// <summary>A piece in the frame (<see cref="LayAt"/>).</summary>
        public bool Lay(List<StructurePiece> pieces, StructurePieceKind kind, string prefab, float along, float across, float y, float yaw, float health) =>
            LayAt(pieces, kind, prefab, At(along, across), y, yaw, health);

        /// <summary>A piece at <paramref name="at"/>, unless it lies wholly under the ground (<see cref="BuriedPieces.Buried"/>);
        /// whether it was laid.</summary>
        public bool LayAt(List<StructurePiece> pieces, StructurePieceKind kind, string prefab, Vector2 at, float y, float yaw, float health)
        {
            var position = new Vector3(at.x, y, at.y);
            if (BuriedPieces.Buried(prefab, position, yaw, m_vertex)) return false;
            pieces.Add(new StructurePiece { Kind = kind, Prefab = prefab, Position = position, YawDegrees = yaw, HealthFraction = health });
            return true;
        }
    }
}
