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
/// +-<see cref="LaneOffset"/>, their top the walking surface, a floor a lane on each bay, as the hand build laid them: on a
/// pier's top course, on an arch bay's arches, over a 6 m opening's open bay on the two crowns either side. A deck is whole
/// bays, so it is whole floors from end to end and never needs a part floor (a <see cref="StonePierBlock"/> laid
/// flat);</item>
/// <item><b>piers</b>: in a pier bay, courses of four <see cref="StonePierBlock"/> blocks (2 x 1 x 1 m) filling the bay's
/// 2 x 4 m under the deck, the top course level with the arches, stacked down to the ground; bonded, the two blocks laid
/// across the deck on one side and the two laid along it on the other, the sides swapping every course;</item>
/// <item><b>arches</b>: in an arch bay, four <see cref="StoneArchPrefab"/> abreast under the deck, the tall end on the pier
/// beside the bay; two arch bays between piers meet at their crowns, one arch over a 4 m opening, or stand 2 m apart, their
/// crowns either side of an open bay, over a 6 m one;</item>
/// <item><b>railing</b>: centred on each deck edge (across +-<see cref="DeckHalfWidth"/>, half over the deck), a course
/// level with the deck of the bay's own piece (an arch over an arch bay, a block over a pier bay, none over an open bay,
/// where nothing would carry it), and a top course of blocks over it, a metre higher, its joints on the bays' middles, over
/// an open bay resting half on each arch's crown;</item>
/// <item><b>ends</b>: the stairs as the road meets the end (<see cref="LayEndStairs"/>), two abreast in
/// <see cref="StoneStairPrefab"/>, each step on a footing of <see cref="StonePillarPrefab"/>s (2 along by 4 across, their
/// tops at the step's foot) stacked down to the ground, and beside each step on each side a railing block from its foot to
/// its top, the railing stepping down the run; the top course's last block reaches half over the first step. With no
/// stairs laid the railing ends at the deck's end.</item>
/// <item><b>over a river</b>: whole openings and the piers between them left open over the ship's way
/// (<see cref="StoneNavigationBays"/>) but for the half arch springing from each pier either side, so the opening reads
/// as a span left open between two arch springings: each half arch's four arches under the deck, the deck over it ending
/// with its floors at its crown, the railing's lower course over it to the crown, its top course a metre short of it, as at a
/// deck end without stairs.</item>
/// </list>
/// <para><b>Nothing is buried</b>: a piece wholly under the ground as the road leaves it (<see cref="RailedGround"/>) is
/// not laid (<see cref="BuriedPieces.Buried"/>), and a pier or a footing ends at the first course or pillar whose foot is in
/// the ground. <b>The ruin</b> is every kit's (<see cref="BridgeRuin"/>, keyed by what it decides), more missing
/// toward mid-span: floors at its deck share, the railing's two courses at its course share, the arches under the deck at
/// its support share, now and then a bay open from side to side (both its floors) at its hole share, never two within
/// three bays and never two bays running open, so every gap is 2 m, which a player jumps;
/// every piece is worn as a vanilla location's (<see cref="Ruin.ApplyLocationDamage"/>). The floors and railing over the
/// bank bays, the floors over a half arch the deck ends on, the steps, their footing and the railing beside them always stand, and so do the piers: a
/// pier is courses bonded down to the ground, which no ruin of the deck brings down, as vanilla's masonry piers stand. Every piece
/// left is on a neighbour's snap points: a railing piece with no neighbour left in its course has one put back, as a top block
/// with nothing under it has the edge piece under it (and one beside it), so the railing is lost at its course share; a floor with no neighbour
/// has the other lane's floor put back. A floor resting on no pier, no arch and not between two floors has an arch under it
/// put back (over an open bay, one on each side of it with neither a crown nor a floor). The complete plan (<see cref="SolveStoneComplete"/>) is
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
    /// <summary>The steps: the shared stair's geometry (<see cref="StairLayout.PieceRun"/>, <see cref="StairLayout.PieceRise"/>), origin at the
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

    /// <summary>What a 2 m bay of the stone kit's deck stands on.</summary>
    public enum StoneBay
    {
        /// <summary>A pier: its courses down to the ground.</summary>
        Pier,
        /// <summary>The near half of an opening: an arch whose tall end stands on the pier toward the near end.</summary>
        ArchNear,
        /// <summary>The far half of an opening: an arch whose tall end stands on the pier toward the far end.</summary>
        ArchFar,
        /// <summary>The middle of a 6 m opening, between the two arches' crowns: nothing under the deck, its floors
        /// resting on the two crowns either side.</summary>
        Open,
    }

    /// <summary>
    /// The stone kit's bays over a deck of <paramref name="bays"/> 2 m bays, near end first, symmetrical about the middle:
    /// a pier bay at each end and one between openings, each opening 4 m (two arches meeting at their crowns) or 6 m (the
    /// two arches 2 m apart, an open bay between them, its floors on their crowns), so a deck is made up in 2 m steps. The 6 m openings are the ones nearest the middle,
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
    /// <see cref="StoneDeckDepth"/>). Null over no water a ship sails.</summary>
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
    /// (<see cref="StoneArchReach"/>, its origin 1.5 m under the walking surface), the floors and the railing's lower
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
    /// The tallest a stone deck's walking surface stands over the lowest ground under one of its piers: the floor's metre
    /// (<see cref="StoneFloorDepth"/>) over <see cref="BridgeSupport.StoneMaxPierCourses"/> courses of a metre
    /// (<see cref="StonePierBlock"/>), the last one's foot sunk <see cref="StairSupport.Bury"/>, as a pier's courses are
    /// counted (StoneFrame.Courses) and a mound raises the ground to (<see cref="StoneMounds"/>). Derived from the kit's support
    /// estimate, not a number of its own: 9.75 m with nine courses.
    /// </summary>
    public static float StoneTallestPier => StoneFloorDepth + BridgeSupport.StoneMaxPierCourses() - StairSupport.Bury;

    /// <summary>
    /// How far a bridge's deck stands toward the tallest the stone kit stands over its bed, from 0 to 1: 0 for a deck at the
    /// water line (<see cref="MinDeck"/>, the lowest any deck stands), 1 for one <see cref="StoneTallestPier"/> over the bed
    /// (<see cref="RoadCrossing.RiverbedHeight"/>) and for every deck taller than that: past the kit's limit a deck stands no
    /// nearer it. The deck is the one every kit's is laid at (<see cref="WoodDeckHeight"/>). 0 where even a deck at the water
    /// line stands past the limit over the bed (no height to gain within it), and for anything but a bridge.
    /// The search's height price reads it (RoadPathfinder.BridgeHeightFactor), on the crossing the detector makes of the jump.
    /// </summary>
    public static float StoneHeightReach(RoadCrossing crossing, WorldGenerator world)
    {
        if (crossing == null || world == null || crossing.Kind != CrossingKind.Bridge) return 0f;
        float low = MinDeck(crossing.WaterLevel), top = crossing.RiverbedHeight + StoneTallestPier;
        if (top <= low) return 0f;
        return Mathf.Clamp01((WoodDeckHeight(crossing, world) - low) / (top - low));
    }

    /// <summary>
    /// Whether the stone kit can be laid over a crossing, as the network saved it: a bridge or a ford's span
    /// (<see cref="Railed"/>) whose deck takes the kit's bays (<see cref="StoneBays"/>), over a river with whole openings
    /// left open over the ship's way (<see cref="StoneNavigationBays"/>), every pier that stands within
    /// <see cref="BridgeSupport.StoneMaxPierCourses"/> and every stair footing within
    /// <see cref="BridgeSupport.StoneMaxFootingPillars"/>. Where it does not, another kit is chosen
    /// (<see cref="BridgeStructures.KitFor"/>), as where the support rule breaks a piece of the bridge it admits
    /// (<see cref="BridgeStructures.CheckSupport"/>). An estimate (<see cref="StoneFitMargin"/>).
    /// </summary>
    public static bool StoneFits(RoadCrossing crossing, WorldGenerator world) => StoneFitMargin(crossing, world) is int margin && margin >= 0;

    /// <summary>
    /// The stone kit's cheap support estimate over a crossing (<see cref="StoneFits"/>'s), as a margin: the fewest courses or
    /// pillars to spare under <see cref="BridgeSupport.StoneMaxPierCourses"/> and <see cref="BridgeSupport.StoneMaxFootingPillars"/>
    /// of any pier that stands and any stair footing (negative past them; the estimate refuses the kit); null where the deck
    /// does not take the kit's bays or openings at all. An estimate, derived from the column arithmetic, not the support rule's
    /// reading of the bridge (<see cref="BridgeStructures.CheckSupport"/>).
    /// </summary>
    internal static int? StoneFitMargin(RoadCrossing crossing, WorldGenerator world)
    {
        if (!Railed(crossing) || world == null || crossing.Width < DeckSpan) return null;
        int bays = Bays(crossing.Width);
        if (StoneBays(bays) is not StoneBay[] layout) return null;
        if (!StoneNavigationBays(crossing, world, layout, out int open, out int opened)) return null;
        var f = new StoneFrame(crossing, world);
        int margin = int.MaxValue;
        for (int j = 0; j < bays; j++)
            if (layout[j] == StoneBay.Pier && (j < open || j >= open + opened))
                margin = Mathf.Min(margin, BridgeSupport.StoneMaxPierCourses() - f.PierCourses(j));
        foreach (StructureEnds.End end in new[] { f.Near, f.Far })
            for (int k = 0; k < end.Steps; k++)
                foreach (Vector2 p in StoneFooting(end, f.Side, k))
                    margin = Mathf.Min(margin, BridgeSupport.StoneMaxFootingPillars() - f.FootingPillars(p, StructureEnds.StepBase(end.Height, k)));
        return margin;
    }

    /// <summary>
    /// The mounds that bring the stone kit within its limits over <paramref name="crossing"/> (<see cref="StoneFitMargin"/>'s
    /// piers and stair footings), on the ground the kit reads (<see cref="RailedGround"/>): under each pier standing more
    /// than <see cref="BridgeSupport.StoneMaxPierCourses"/> courses, its bay's ground raised to where that many reach it
    /// (the lowest course's foot sunk <see cref="StairSupport.Bury"/>); under each footing of more than
    /// <see cref="BridgeSupport.StoneMaxFootingPillars"/> pillars, its pillar's ground so; at the very points the kit reads
    /// them (<see cref="StructureGround.Mound"/>). Null where the kit is not laid there, where a mound may not be made under
    /// one (<see cref="StructureGround.MayRaise"/>), or where the terrain writer's reach round one
    /// (RoadTerrainModifier.MaxInfluenceRadius) meets an opening left over a ship's way (<see cref="StoneNavigationBays"/>):
    /// the opening is kept clear. Empty where none is past its limit. Whether the kit is then drawn is the kit's choice
    /// (BridgeStructures.StoneMounds).
    /// </summary>
    internal static List<StructureGround.Mound>? StoneMounds(RoadCrossing crossing, WorldGenerator world)
    {
        if (!Railed(crossing) || world == null || crossing.Width < DeckSpan) return null;
        int bays = Bays(crossing.Width);
        if (StoneBays(bays) is not StoneBay[] layout) return null;
        if (!StoneNavigationBays(crossing, world, layout, out int open, out int opened)) return null;
        var f = new StoneFrame(crossing, world);
        var mounds = new List<StructureGround.Mound>();
        float reach = RoadTerrainModifier.MaxInfluenceRadius(StructureGround.Mound.Width);
        bool Clear(List<Vector2> points)
        {
            if (opened == 0) return true;
            foreach (Vector2 p in points)
            {
                float along = crossing.Along(p);
                if (along > open * DeckSpan - reach && along < (open + opened) * DeckSpan + reach) return false;
            }
            return true;
        }
        int courses = BridgeSupport.StoneMaxPierCourses(), pillars = BridgeSupport.StoneMaxFootingPillars();
        float top = f.DeckH - StoneFloorDepth;
        for (int j = 0; j < bays; j++)
        {
            if (layout[j] != StoneBay.Pier || (j >= open && j < open + opened) || f.PierCourses(j) <= courses) continue;
            float low = f.PierLow(j), raised = top - courses + StairSupport.Bury;
            var points = f.PierPoints(j);
            if (!StructureGround.MayRaise(low, raised, BridgeStructures.Kit.Stone) || StoneFrame.Courses(top, raised) > courses || !Clear(points)) return null;
            mounds.Add(new StructureGround.Mound(points, raised));
        }
        foreach (StructureEnds.End end in new[] { f.Near, f.Far })
            for (int k = 0; k < end.Steps; k++)
            {
                float foot = StructureEnds.StepBase(end.Height, k);
                foreach (Vector2 p in StoneFooting(end, f.Side, k))
                {
                    if (f.FootingPillars(p, foot) <= pillars) continue;
                    float raised = foot - StonePillarHeight * pillars + StairSupport.Bury;
                    var points = new List<Vector2> { p };
                    if (!StructureGround.MayRaise(f.Ground(p), raised, BridgeStructures.Kit.Stone) || StoneFrame.Pillars(foot, raised) > pillars || !Clear(points)) return null;
                    mounds.Add(new StructureGround.Mound(points, raised));
                }
            }
        return mounds;
    }

    /// <summary>The stone bridge as it stands, ruined (the game's plan).</summary>
    public static List<StructurePiece> SolveStone(RoadCrossing crossing, WorldGenerator world, int worldSeed) =>
        SolveStone(crossing, world, worldSeed, ruin: true);

    /// <summary>The stone bridge with nothing missing but its opening over a river: what a player can repair it to, and
    /// the bridge laid whole where bridges are not weathered (<see cref="StructureWear"/>).</summary>
    internal static List<StructurePiece> SolveStoneComplete(RoadCrossing crossing, WorldGenerator world, int worldSeed) =>
        SolveStone(crossing, world, worldSeed, ruin: false);

    /// <summary>
    /// The stone bridge its cheap estimate refuses, laid as it would stand with no pier or footing stopped at the estimate's
    /// limits (<see cref="BridgeSupport.StoneMaxPierCourses"/>, <see cref="BridgeSupport.StoneMaxFootingPillars"/>): the
    /// candidate before its admission, for the support rule to read it in a test's replay of refused candidates (decision 6p's
    /// calibration). Never built.
    /// </summary>
    internal static List<StructurePiece> SolveStoneCandidate(RoadCrossing crossing, WorldGenerator world, int worldSeed, bool ruin) =>
        SolveStone(crossing, world, worldSeed, ruin, capped: false);

    private static List<StructurePiece> SolveStone(RoadCrossing crossing, WorldGenerator world, int worldSeed, bool ruin, bool capped = true)
    {
        var pieces = new List<StructurePiece>();
        if (!Railed(crossing) || world == null || crossing.Width < DeckSpan) return pieces;
        int bays = Bays(crossing.Width);
        if (StoneBays(bays) is not StoneBay[] layout) return pieces;
        if (!StoneNavigationBays(crossing, world, layout, out int gapFirst, out int gapCount)) return pieces;
        // The bays open over a river: the run StoneNavigationBays opens but its half arches either side, which stand with
        // the deck and railing over them.
        int open = gapCount > 0 ? gapFirst + StoneGapHalfArches : 0, opened = Mathf.Max(0, gapCount - 2 * StoneGapHalfArches);
        // A bay open over a river.
        bool Opened(int j) => j >= open && j < open + opened;
        var f = new StoneFrame(crossing, world) { Capped = capped };
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

        // The deck's floors, a whole floor on each bay (lane 0 left, 1 right), as the hand build laid them: on a pier bay on its
        // top course, on an arch bay on its arches' flat tops, on a 6 m opening's open bay between the two crowns either side.
        // A deck of whole bays takes whole floors, so the deck never needs a part floor. Lost toward mid-span, never over a
        // bank's bay, nor over the half arch the deck ends on beside a river's opening.
        bool Kept(int j) => j == 0 || j == bays - 1 || (opened > 0 && (j == open - 1 || j == open + opened));
        var floor = new bool[bays, 2];
        for (int j = 0; j < bays; j++)
            for (int l = 0; l < 2; l++)
                floor[j, l] = !Opened(j) && (!ruin || Kept(j)
                    || r.Roll(j, l == 0 ? Ruin.Left : Ruin.Right, Ruin.RoleDeck) < Survives(j + 0.5f, shares.DeckShare));
        // Now and then a bay open from side to side, both its floors: never a kept one, never two within three bays.
        var hole = new bool[bays];
        if (ruin)
            for (int j = 1, last = -3; j <= bays - 2; j++)
                if (j - last >= 3 && !Kept(j) && !Opened(j) && r.Roll(j, Ruin.Centre, RoleStoneHole) >= Survives(j + 0.5f, shares.HoleShare))
                {
                    hole[j] = true;
                    floor[j, 0] = floor[j, 1] = false;
                    last = j;
                }
        // A way across a player jumps: never two bays running open from side to side; a floor is put back in the one that is
        // not a hole.
        for (int j = 1; j < bays; j++)
            if (!Opened(j) && !Opened(j - 1) && !floor[j, 0] && !floor[j, 1] && !floor[j - 1, 0] && !floor[j - 1, 1])
            {
                int b = hole[j] ? j - 1 : j;
                floor[b, r.Roll(b, Ruin.Centre, RoleStoneFloorBack) < 0.5f ? 0 : 1] = true;
            }

        for (int j = 0; j < bays; j++)
        {
            if (layout[j] != StoneBay.Pier || Opened(j)) continue;
            float u = j * DeckSpan + DeckSpan * 0.5f;
            // Courses down to the ground, the top one level with the arches; bonded, the side whose two blocks lie across the
            // deck swapping every course. Never past the support limit: the kit fitted when it was chosen
            // (StoneFits), but ground lowered since (a road cut under a pier) leaves the courses past the
            // limit, at the foot, out rather than the railing over the arches losing its support; the bridge so laid is read by
            // the support rule before it is (BridgeStructures.KitAmong: a saved kit solved again).
            int courses = Mathf.Min(f.PierCourses(j), f.MostCourses);
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

        // Every floor left on a neighbour's snap points (its corners on the bays' joints): the other lane's floor in its bay,
        // its lane's floors either side, or the top course of the pier it lies on, whose blocks have corners at the bay's
        // joints under both lanes. A floor with none of them has the other lane's put back; one on an arch bay is on its
        // arches' snap points too, which the arches' rule below keeps.
        for (int j = 1; j < bays - 1; j++)
            for (int l = 0; l < 2; l++)
            {
                bool onPier = layout[j] == StoneBay.Pier && pierTop[j];
                if (floor[j, l] && !floor[j, 1 - l] && !floor[j - 1, l] && !floor[j + 1, l] && !onPier) floor[j, 1 - l] = true;
            }

        // The arches under the deck: lost toward mid-span. Each stands on its pier by its tall end, so none is left loose;
        // but a floor that rests on no pier, no arch of its lane and not between two floors of its lane has one of the arches
        // under it put back.
        var arch = new bool[bays, 4];
        for (int j = 0; j < bays; j++)
            if (layout[j] is StoneBay.ArchNear or StoneBay.ArchFar && !Opened(j))
                for (int k = 0; k < 4; k++)
                    arch[j, k] = !ruin || r.Roll(j, k, RoleStoneArch) < Survives(j + 0.5f, shares.SupportShare);
        // A floor on an arch bay resting on no arch of its lane and not between two floors of its lane has one of its arches
        // put back; one on an open bay rests on the crowns either side, and a side with neither a crown nor a floor of its
        // lane has one of that side's arches put back.
        bool Under(int bay, int l) => arch[bay, 2 * l] || arch[bay, 2 * l + 1];
        for (int j = 0; j < bays; j++)
            for (int l = 0; l < 2; l++)
            {
                if (!floor[j, l] || layout[j] == StoneBay.Pier) continue;
                if (layout[j] == StoneBay.Open)
                {
                    foreach (int side in new[] { j - 1, j + 1 })
                        if (!floor[side, l] && !Under(side, l))
                            arch[side, 2 * l + (r.Roll(j, l, RoleStoneArch, side < j ? 1 : 3) < 0.5f ? 0 : 1)] = true;
                    continue;
                }
                if (Under(j, l) || (j > 0 && j < bays - 1 && floor[j - 1, l] && floor[j + 1, l])) continue;
                arch[j, 2 * l + (r.Roll(j, l, RoleStoneArch, 2) < 0.5f ? 0 : 1)] = true;
            }
        for (int j = 0; j < bays; j++)
            for (int k = 0; k < 4; k++)
                if (arch[j, k])
                    f.Lay(pieces, StructurePieceKind.Beam, StoneArchPrefab, j * DeckSpan + DeckSpan * 0.5f, k - 1.5f, underside - 0.5f,
                        ArchYaw(layout[j]), r.Health(j, k, Ruin.RoleBeamHealth));

        // The deck: a floor on each bay.
        for (int j = 0; j < bays; j++)
            for (int l = 0; l < 2; l++)
            {
                if (!floor[j, l]) continue;
                int lane = l == 0 ? Ruin.Left : Ruin.Right;
                f.Lay(pieces, StructurePieceKind.Deck, StoneFloorPrefab, j * DeckSpan + DeckSpan * 0.5f, lane * LaneOffset,
                    deckH - StoneFloorDepth * 0.5f, f.AlongYaw, r.Health(j, lane, Ruin.RoleDeckHealth));
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
        bool CanTop(int i) => i >= 0 && i <= bays && Laid(i) && CanHold(i);
        void Rail(float edgeRolled)
        {
            for (int k = 0; k < 2; k++)
            {
                int s = k == 0 ? Ruin.Left : Ruin.Right;
                for (int j = 0; j < bays; j++)
                    edge[j, k] = CanEdge(j)
                        && (!ruin || j == 0 || j == bays - 1 || r.Roll(j, s, RoleRail) < Survives(j + 0.5f, shares.CourseShare * edgeRolled));
                for (int i = 0; i <= bays; i++)
                    top[i, k] = Laid(i)
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
        // Its pieces wholly in the ground left out where the bridge stands without them (the one owner), before it is ruined.
        LeaveOutBuried(pieces, f.Ground, crossing, world, p => f.Laid.Contains(p) || p.Kind == StructurePieceKind.Stair);
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
            float foot = StructureEnds.StepBase(end.Height, k);
            int n = 0;
            foreach (Vector2 p in StoneFooting(end, f.Side, k))
            {
                // Within the support limit, as a pier's courses: the pillars past it, at the foot, are left out.
                int pillars = Mathf.Min(f.FootingPillars(p, foot), f.MostPillars);
                for (int m = 0; m < pillars; m++)
                    f.LayAt(pieces, StructurePieceKind.Post, StonePillarPrefab, p, foot - StonePillarHeight * (m + 0.5f), f.Heading,
                        r.Health(bayKey, n, Ruin.RolePostHealth, k * 16 + m));
                n++;
            }
            Vector2 c = StructureEnds.StepCentre(end.Edge, end.Up, k);
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
        Vector2 c = StructureEnds.StepCentre(end.Edge, end.Up, k);
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
        /// <summary>Whether its piers and footings stop at the estimate's limits, as every built one does; a calibration
        /// candidate's do not (<see cref="SolveStoneCandidate"/>).</summary>
        public bool Capped = true;
        /// <summary>The most courses a pier stands: <see cref="BridgeSupport.StoneMaxPierCourses"/> where <see cref="Capped"/>.</summary>
        public int MostCourses => Capped ? BridgeSupport.StoneMaxPierCourses() : int.MaxValue;
        /// <summary>The most pillars a footing stacks: <see cref="BridgeSupport.StoneMaxFootingPillars"/> where <see cref="Capped"/>.</summary>
        public int MostPillars => Capped ? BridgeSupport.StoneMaxFootingPillars() : int.MaxValue;

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
            System.Func<Vector2, float> ground = RailedGround(world);
            m_ground = ground;
            m_vertex = (i, k) => ground(new Vector2(i, k));
        }

        public Vector2 At(float along, float across) => From + Dir * along + Side * across;

        /// <summary>The courses a pier bay stands: from the top one, level with the arches, down to the first whose foot is
        /// sunk <see cref="StairSupport.Bury"/> under the lowest ground under the bay's 2 x 4 m, as a staircase's wall is.</summary>
        public int PierCourses(int bay) => Courses(DeckH - StoneFloorDepth, PierLow(bay));

        /// <summary>The courses from a pier's top one at <paramref name="top"/> down to the first whose foot is sunk
        /// <see cref="StairSupport.Bury"/> under <paramref name="low"/> (<see cref="PierCourses"/>).</summary>
        public static int Courses(float top, float low)
        {
            int courses = 1;
            while (top - courses > low - StairSupport.Bury && courses < 256) courses++;
            return courses;
        }

        /// <summary>Where a pier bay's ground is read (<see cref="PierCourses"/>): its 2 x 4 m, every metre along and at its
        /// middle and both edges across.</summary>
        public List<Vector2> PierPoints(int bay)
        {
            var points = new List<Vector2>();
            for (int a = 0; a <= 2; a++)
                for (int s = -1; s <= 1; s++)
                    points.Add(At(bay * DeckSpan + a, s * DeckHalfWidth));
            return points;
        }

        /// <summary>The lowest ground under a pier bay (<see cref="PierPoints"/>).</summary>
        public float PierLow(int bay)
        {
            float low = float.PositiveInfinity;
            foreach (Vector2 p in PierPoints(bay)) low = Mathf.Min(low, m_ground(p));
            return low;
        }

        /// <summary>The pillars of a step's footing at <paramref name="p"/>: from the step's <paramref name="foot"/> down to
        /// the first whose foot is sunk <see cref="StairSupport.Bury"/> under the ground, as a staircase's wall is.</summary>
        public int FootingPillars(Vector2 p, float foot) => Pillars(foot, m_ground(p));

        /// <summary>The pillars from a step's <paramref name="foot"/> down to the first whose foot is sunk
        /// <see cref="StairSupport.Bury"/> under <paramref name="ground"/> (<see cref="FootingPillars"/>).</summary>
        public static int Pillars(float foot, float ground)
        {
            int pillars = 1;
            while (foot - StonePillarHeight * pillars > ground - StairSupport.Bury && pillars < 256) pillars++;
            return pillars;
        }

        /// <summary>A piece in the frame (<see cref="LayAt"/>).</summary>
        public bool Lay(List<StructurePiece> pieces, StructurePieceKind kind, string prefab, float along, float across, float y, float yaw, float health) =>
            LayAt(pieces, kind, prefab, At(along, across), y, yaw, health);

        /// <summary>The pieces laid in the frame (<see cref="LayAt"/>): those left out with the bridge's where wholly in the
        /// ground (BridgeLayout.LeaveOutBuried).</summary>
        public readonly HashSet<StructurePiece> Laid = new();

        /// <summary>A piece at <paramref name="at"/>, laid; one wholly under the ground is left out with the bridge's where the
        /// rest stands without it (BridgeLayout.LeaveOutBuried, BuriedPieces.Omit). Whether it stands out of the ground
        /// (<see cref="BuriedPieces.Buried"/>), which the layout reads (a pier's top course).</summary>
        public bool LayAt(List<StructurePiece> pieces, StructurePieceKind kind, string prefab, Vector2 at, float y, float yaw, float health)
        {
            var position = new Vector3(at.x, y, at.y);
            var piece = new StructurePiece { Kind = kind, Prefab = prefab, Position = position, YawDegrees = yaw, HealthFraction = health };
            pieces.Add(piece);
            Laid.Add(piece);
            return !BuriedPieces.Buried(prefab, position, yaw, m_vertex);
        }
    }
}
