using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// What holds a staircase's walking pieces up (one owner): the style each piece is held by, chosen from the natural ground
/// alone (<see cref="Choose"/>), which the search prices and the kit lays at first build (<see cref="Lay"/>), so both agree.
/// Hand-built 2x2 prototypes:
/// <list type="bullet">
/// <item>stone up to <see cref="WallHeight"/> over the ground under its downhill edge: a wall under its downhill half,
/// 1 m courses of stone_wall_2x1 laid along the flight, each course staggered a metre (running bond), 1x1 at the ends;
/// a landing's open end walled across, the courses interlocking at the corner with 2x1s turned along the other wall
/// </item>
/// <item>stone higher: a stone_wall_2x1 under each metre of the piece (both halves of a stair, so the full
/// 2 x 2 stair is held) and a stone_arch under it, the arch's tall 1x1 end sunk into the slope; where that end is
/// short of the slope, a corbel steps out 2 m and down 1 m a course, at most <see cref="ArchCourses"/> courses (about
/// 6 m); further, the piece is refused and its route avoided;</item>
/// <item>black marble: a blackmarble_2x1x1 under each stair's lower half and stepped cornice columns (blackmarble_out_1)
/// under both halves, each course 2 m lower and 1 m further into the slope, down to the ground, the lowest course's 1 m
/// block in it, up to <see cref="CorniceCourses"/> courses and at most <see cref="MostHungCourses"/> hung over the first
/// that touches the ground (deeper, the game's support rule breaks them: refused); under the 2 x 2 floor at a square turn's outer corner, one
/// blackmarble_outcorner a course in their place, each a metre in on both its faces from the one above (<see cref="CornerEnd"/>).</item>
/// </list>
/// No pillars. A piece whose downhill edge lies on the ground needs nothing. Whatever is laid is read with the game's
/// support rule (<see cref="HoldUp"/>: nothing falls).
/// </summary>
public static class StairSupport
{
    /// <summary>Stone stands on a wall up to this height of its underside over the ground under its downhill edge (4 to
    /// 5 m); higher, it is held from the slope.</summary>
    public const float WallHeight = 4.5f;
    /// <summary>Stone's arch, then corbel courses stepping 2 m out each: their ends at 1, 3 and 5 m from the piece's
    /// centre line, within the 6 m given to the corbel.</summary>
    public const int ArchCourses = 3;
    /// <summary>Black marble's cornice courses are 2 m: 13 at most, as deep as the viaduct's support ceiling stood (26 m)
    /// when they were set. A column that touches the slope as it steps down holds at any of them (each course that touches
    /// it is grounded); one hung out from a steeper face holds only <see cref="MostHungCourses"/> over the first that
    /// touches it.</summary>
    public const int CorniceCourses = 13;

    /// <summary>
    /// Most cornice courses a column hangs over the first of its courses that touches the ground, by the game's support rule
    /// (<see cref="SupportGraph"/>): each course keeps 0.708 of the one under it (black marble losing 1/8 a metre, centres
    /// 2.24 m apart: 2 m down, 1 m across), and the walking piece on the column's top 0.68 of it at the least (a stair's lower
    /// half, through its block). Every one of them is to hold at <see cref="DvergrViaduct.HoldMargin"/> times marble's least
    /// (100), the model's over-reading of a cornice taken off (<see cref="ViaductSupport.Overread"/>): six hung courses
    /// leave the top at 189 and the stair over it at 128 by its lower half (149 by its upper); a seventh, the top at 134 and the
    /// stair at 106 (both under the margin; an eighth, 95: the top falls). Seen on
    /// black marble corners (fix/bm-outcorner-line, ACornersColumnHungFromTheGroundHoldsByTheGamesSupportRule): more than
    /// seven hung courses fall. A corner's column of outcorners, stepping in on both faces (centres 2.45 m apart), keeps 0.68
    /// a course: six hung leave its top at 150 and the corner floor on the column alone at 1.20 times its least, with the
    /// staircase round it 1.65 (BlackMarbleCornerTests.ACornersColumnHungFromTheGroundHoldsByTheGamesSupportRule), so the
    /// ceiling holds for corners too. Deeper, the piece is refused and the search holds it otherwise or lays the staircase
    /// another way (StairCorniceDepthTests).
    /// </summary>
    public const int MostHungCourses = 6;
    /// <summary>A wall's courses go on until the lowest is sunk this far into the ground at every corner (as columns were).</summary>
    public const float Bury = 0.25f;
    /// <summary>Most courses a wall cell stands: a wall's height of air under its piece (<see cref="WallHeight"/>) and the
    /// deepest a road's earthwork lowers the ground under it (<see cref="RoadConstants.TerrainDeltaMin"/>), and some.
    /// Stopped at the old wall height's 8, a stair walled 4 m over the slope beside a 5 m road cut ended a metre over it.</summary>
    public static readonly int MostCourses = Mathf.CeilToInt(WallHeight - RoadConstants.TerrainDeltaMin) + 3;

    /// <summary>Walking pieces <see cref="Lay"/> met that nothing within reach holds, left unheld, since the generation began
    /// (<see cref="ResetCounts"/>; each said where its staircase is dressed, <see cref="StairStructures.Dresser"/>).</summary>
    private static int s_unheld;
    internal static int Unheld => s_unheld;

    /// <summary>Wall cells laid that stopped at <see cref="MostCourses"/> short of the finished ground, since the generation
    /// began (as <see cref="Unheld"/>).</summary>
    private static int s_shortWalls;
    internal static int ShortWalls => s_shortWalls;

    /// <summary>Support pieces left out because the game's support rule would break them (<see cref="HoldUp"/>), and walking
    /// pieces it holds under <see cref="DvergrViaduct.HoldMargin"/> times their least, since the generation began (as
    /// <see cref="Unheld"/>).</summary>
    private static int s_fell, s_weak;
    internal static int Fell => s_fell;
    internal static int Weak => s_weak;

    /// <summary>Forget <see cref="Unheld"/>, <see cref="ShortWalls"/>, <see cref="Fell"/> and <see cref="Weak"/>: a generation
    /// begins (RoadStairRuns.ResetCache).</summary>
    internal static void ResetCounts()
    {
        System.Threading.Interlocked.Exchange(ref s_unheld, 0);
        System.Threading.Interlocked.Exchange(ref s_shortWalls, 0);
        System.Threading.Interlocked.Exchange(ref s_fell, 0);
        System.Threading.Interlocked.Exchange(ref s_weak, 0);
    }

    /// <summary>What one <see cref="Lay"/> could not hold up as the search priced it, for its caller to say where: each walking
    /// piece left unheld (the middle of its underside), each wall cell stopped short (its lowest course's top), each support
    /// piece left out because the game's support rule breaks it, and each walking piece the rule holds under
    /// <see cref="DvergrViaduct.HoldMargin"/> times its least (their positions).</summary>
    public sealed class Shortfall
    {
        public readonly List<Vector3> Unheld = new();
        public readonly List<Vector3> ShortWalls = new();
        public readonly List<Vector3> Fell = new();
        public readonly List<Vector3> Weak = new();
        public bool Any => Unheld.Count > 0 || ShortWalls.Count > 0 || Fell.Count > 0 || Weak.Count > 0;
    }

    // Search prices, in the search's units (a metre walked costs 2): up to WallHeight, by the mean air under the walking
    // surface as columns were (StairLayout's FloatWeight over the same samples), whatever holds it; higher, held from the
    // slope, at the old cantilever's base price, and more for each corbel or cornice course.
    public const float WallPrice = 3f, ArchPrice = 2f, CorbelCoursePrice = 2f, CorniceCoursePrice = 0.25f;
    /// <summary>A piece over <see cref="WallHeight"/> is priced as held from the slope only on a face this steep or steeper
    /// (rise over run across it, wall side to a metre past its outer edge), and then only where that is cheaper than its
    /// air; on a gentler face, by its air or its holding, whichever is dearer. Priced flat everywhere, the search climbed
    /// ordinary 1:1 faces high off the ground on purpose (as the old cantilevers, which only a face of 2:1 held out).</summary>
    public const float HeldFromGrade = 2f;

    public enum Style { Ground, Wall, Arch, Cornice }

    /// <summary>How one walking piece is held: <see cref="Side"/> its wall side (+1 to its right, -1 left; 0 for a piece with
    /// both edges in the ground), <see cref="Courses"/>
    /// the deepest course any of its metres needs, <see cref="Refused"/> where nothing within reach holds it.</summary>
    public readonly struct Choice
    {
        public readonly Style Style;
        public readonly int Side, Courses;
        public readonly float Price;
        public readonly bool Refused;
        public Choice(Style style, int side, int courses, float price, bool refused = false)
        { Style = style; Side = side; Courses = courses; Price = price; Refused = refused; }
    }

    /// <summary>A walking piece's footprint: its back edge's middle, heading, depth along and width across, and underside.</summary>
    public readonly struct Walk
    {
        public readonly Vector2 Back;
        public readonly float Heading, Depth, Across, Underside;
        public readonly bool Stair;
        /// <summary>Its heading's forward and right, worked out once: <see cref="Choose"/> asks for a point on it a few dozen
        /// times a piece the search tries, each a sine and a cosine when worked out afresh.</summary>
        public readonly Vector2 Dir, Right;
        public Walk(Vector2 back, float heading, float depth, float across, float underside, bool stair)
        {
            Back = back; Heading = heading; Depth = depth; Across = across; Underside = underside; Stair = stair;
            Dir = StairLayout.Forward(heading); Right = StairLayout.Right(heading);
        }
        public Vector2 At(float along, float across) => Back + Dir * along + Right * across;
        /// <summary>The walking surface <paramref name="along"/> it: a stair's rises its metre over its run.</summary>
        public float Surface(float along, StairKit kit) => Stair ? Underside + StairLayout.PieceRise * along / Depth : Underside + kit.FloorThickness;
    }

    /// <summary>The walking pieces of a staircase's layout as footprints, null for any other piece.</summary>
    public static Walk? WalkOf(StructurePiece piece, StairKit kit)
    {
        Vector2 at = new Vector2(piece.Position.x, piece.Position.z);
        switch (piece.Kind)
        {
            case StructurePieceKind.StairStep:
            {
                // Origin at its foot's height, centred; it climbs toward its local -z (StairLayout.AddPiece).
                float heading = StairLayout.Wrap(piece.YawDegrees + 180f);
                return new Walk(at - StairLayout.Forward(heading) * (StairLayout.PieceRun / 2f), heading, StairLayout.PieceRun,
                    StairLayout.PieceRun, piece.Position.y, true);
            }
            case StructurePieceKind.StairFloor:
            case StructurePieceKind.StairFiller:
            {
                FillerKind flat = kit.FlatOf(piece.Prefab);
                float heading = piece.Prefab == kit.Floor ? piece.YawDegrees : StairLayout.Wrap(piece.YawDegrees + 180f);
                float underside = piece.Position.y + kit.FloorTop - kit.FloorThickness;
                return new Walk(at - StairLayout.Forward(heading) * (flat.Depth / 2f), heading, flat.Depth, flat.Across, underside, false);
            }
            default: return null;
        }
    }

    /// <summary>
    /// The one choice of how a piece is held, from the natural <paramref name="ground"/>: the search prices it and the kit lays
    /// it (<see cref="Lay"/>). The wall side is the one whose ground stands higher at its edge and out to the corbels' reach,
    /// the downhill side the other. Black marble is priced by its deepest cornice course whether the piece turns out a square
    /// turn's corner or not (which the pieces after it decide): a corner's column of outcorners, stepping back from its
    /// open end too, goes down to its own block in the ground (CornerCourse), about as deep.
    /// </summary>
    public static Choice Choose(StairKit kit, Walk w, Func<float, float, float> ground)
    {
        float half = w.Across / 2f, right = 0f, left = 0f, rightGap = 0f, leftGap = 0f, meanGap = 0f;
        for (int i = 1; i <= 3; i++)
        {
            float along = w.Depth * i / 4f;
            Vector2 r = w.At(along, half), l = w.At(along, -half);
            rightGap = Mathf.Max(rightGap, w.Underside - ground(r.x, r.y));
            leftGap = Mathf.Max(leftGap, w.Underside - ground(l.x, l.y));
            for (int s = -1; s <= 1; s++)
            {
                Vector2 p = w.At(along, s * half * 0.9f);
                meanGap += Mathf.Max(0f, w.Surface(along, kit) - ground(p.x, p.y));
            }
        }
        meanGap /= 9f;
        float air = WallPrice * meanGap;
        // A piece whose downhill edge is in the ground needs nothing laid, but is priced by the air under its walking surface
        // all the same, as every piece on columns was: free, a stair whose upper steps stand out of the slope cost nothing
        // more than one lying in it, and the search's feet and joins wandered. Both edges in it, whichever is downhill: the
        // search tries most pieces so, and the slope out to the corbels' reach is not read for them.
        if (rightGap <= 0f && leftGap <= 0f) return new Choice(Style.Ground, 0, 0, air);
        for (int i = 1; i <= 3; i++)
        {
            float along = w.Depth * i / 4f;
            for (int c = 0; c < ArchCourses; c++)
            {
                Vector2 rc = w.At(along, half + 2f * c), lc = w.At(along, -half - 2f * c);
                right += ground(rc.x, rc.y); left += ground(lc.x, lc.y);
            }
        }
        int side = right >= left ? 1 : -1; // the wall side
        float face = Face(w, side, ground);
        float height = side > 0 ? leftGap : rightGap;
        if (height <= 0f) return new Choice(Style.Ground, side, 0, air);
        if (kit == StairKit.BlackMarble)
        {
            int most = 0;
            for (int row = 0; row < CorniceRowCount(w); row++)
            {
                var (along, top) = CorniceRow(w, row);
                int c = CorniceCourse(w, along, top, side, ground);
                if (c < 0 || HungCourses(w, along, top, side, ground) > MostHungCourses)
                    return new Choice(Style.Cornice, side, CorniceCourses, 0f, refused: true);
                most = Math.Max(most, c);
            }
            return new Choice(Style.Cornice, side, most + 1, height <= WallHeight ? air : Held(air, ArchPrice + CorniceCoursePrice * (most + 1), face));
        }
        if (height <= WallHeight) return new Choice(Style.Wall, side, 0, air);
        int deepest = 0;
        for (int row = 0; row < ArchRowCount(w); row++)
        {
            int c = ArchCourse(w, ArchRow(row), side, ground);
            if (c < 0) return new Choice(Style.Arch, side, ArchCourses, 0f, refused: true);
            deepest = Math.Max(deepest, c);
        }
        return new Choice(Style.Arch, side, deepest + 1, Held(air, ArchPrice + CorbelCoursePrice * deepest, face));
    }

    private static float Held(float air, float held, float face) => face >= HeldFromGrade ? Mathf.Min(air, held) : Mathf.Max(air, held);

    /// <summary>The face's grade across a piece: the rise from a metre past its downhill edge to a metre past its wall-side
    /// edge, over that run, the mean along it.</summary>
    private static float Face(Walk w, int side, Func<float, float, float> ground)
    {
        float half = w.Across / 2f, run = w.Across + 2f, rise = 0f;
        for (int i = 1; i <= 3; i++)
        {
            float along = w.Depth * i / 4f;
            Vector2 wall = w.At(along, side * (half + 1f)), drop = w.At(along, -side * (half + 1f));
            rise += ground(wall.x, wall.y) - ground(drop.x, drop.y);
        }
        return rise / 3f / run;
    }

    /// <summary>The metres an arch and its block hold, each with its top at the piece's underside: every metre of it. The
    /// prototype held a stair's lower half only; its upper half is held too. A stair's underside is flat (its
    /// bottom snap points are at its foot's height at both ends), so both blocks meet it at the same height. Counted and
    /// indexed rather than enumerated: the search chooses for every piece and every turn's filler it tries
    /// (<see cref="Choose"/>), and an enumerator a call was garbage on each.</summary>
    private static int ArchRowCount(Walk w) => Mathf.Max(0, Mathf.CeilToInt(w.Depth - 0.5f));

    /// <summary>The <paramref name="row"/>th metre of <see cref="ArchRowCount"/>: its middle, along the piece.</summary>
    private static float ArchRow(int row) => 0.5f + row;

    /// <summary>The first arch course (0 the arch under the piece, then corbels stepping 2 m out and 1 m down) whose tall
    /// 1x1 end is wholly in the slope at the metre <paramref name="along"/>, or -1 where none within reach is.</summary>
    private static int ArchCourse(Walk w, float along, int side, Func<float, float, float> ground)
    {
        for (int c = 0; c < ArchCourses; c++)
        {
            float faceTop = w.Underside - 1f - c, reach = FillerShift(w) + StairLayout.PieceRun / 2f + 2f * c;
            bool sunk = true;
            for (int e = -1; e <= 1; e += 2)
            {
                Vector2 p = w.At(along + e * 0.45f, side * reach);
                if (ground(p.x, p.y) < faceTop) { sunk = false; break; }
            }
            if (sunk) return c;
        }
        return -1;
    }

    /// <summary>A black marble piece's cornice columns: the metre along and the top they hold up. A stair: its upper half at
    /// its underside, its lower half under the block there (a metre lower); a flat piece: each metre at its underside
    /// (<see cref="ArchRow"/>). Counted and indexed, as <see cref="ArchRowCount"/>.</summary>
    private static int CorniceRowCount(Walk w) => w.Stair ? 2 : ArchRowCount(w);

    /// <summary>The <paramref name="row"/>th column of <see cref="CorniceRowCount"/>.</summary>
    private static (float along, float top) CorniceRow(Walk w, int row) =>
        w.Stair ? (row == 0 ? (1.5f, w.Underside) : (0.5f, w.Underside - 1f)) : (ArchRow(row), w.Underside);

    /// <summary>How far a support <paramref name="width"/> across stands toward the wall from a piece's middle line: flush
    /// with the piece's downhill edge, square to it. A 2 m support (a cornice's column, an arch and its block): none under a
    /// 2 m wide piece, whose width it spans; half a metre under a 1 x 1 filler, so it lies under the block and a metre into
    /// the wall side, rather than sticking out half a metre either side (in game: a turn's 1 x 1 fillers, black marble,
    /// then stone at -4786.5,1728.1). A 1 m wall: half a metre downhill under a 2 m piece (the middle of its downhill half);
    /// none under a 1 x 1 filler, right under it rather than a quarter metre off (-1924.8,6713.1).</summary>
    private static float FillerShift(Walk w, float width = StairLayout.PieceRun) => (width - w.Across) / 2f;

    /// <summary>The cornice course (2 m each, 1 m further into the slope each) whose lower 1 m block is wholly in the ground,
    /// or -1 past the support ceiling.</summary>
    private static int CorniceCourse(Walk w, float along, float top, int side, Func<float, float, float> ground)
    {
        for (int c = 0; c < CorniceCourses; c++)
        {
            float blockTop = top - 1f - 2f * c;
            bool sunk = true;
            // The block: a metre square on the wall side of the column's line, c metres into the slope.
            for (int e = -1; e <= 1; e += 2)
                for (int k = 0; k <= 1; k++)
                {
                    Vector2 p = w.At(along + e * 0.45f, side * (c + FillerShift(w) + k * 0.95f + 0.025f));
                    if (ground(p.x, p.y) < blockTop) { sunk = false; break; }
                }
            if (sunk) return c;
        }
        return -1;
    }

    /// <summary>How many of a cornice column's courses hang over the first that touches the ground: its box (2 m across, a
    /// metre along, 2 m tall), grown <see cref="SupportGraph.Reach"/> as the game grows it, meets the ground on its middle
    /// line or its wall-side edge. The column's courses down to its sunk one (<see cref="CorniceCourse"/>) all touch by
    /// then; the slope's face is read at them only.</summary>
    private static int HungCourses(Walk w, float along, float top, int side, Func<float, float, float> ground)
    {
        float r = SupportGraph.Reach;
        for (int c = 0; c < CorniceCourses; c++)
        {
            float bottom = top - 2f - 2f * c, middle = c + FillerShift(w);
            for (int e = -1; e <= 1; e++)
                for (int k = 0; k <= 1; k++)
                {
                    Vector2 p = w.At(along + e * (0.5f + r), side * (middle + k * (1f + r)));
                    if (ground(p.x, p.y) >= bottom - r) return c;
                }
        }
        return CorniceCourses;
    }

    /// <summary>
    /// The pieces that hold a staircase's walking pieces up, laid at its first build: each piece held as <see cref="Choose"/>
    /// chooses on the <paramref name="natural"/> ground; walls stand down to the <paramref name="finished"/> ground (the
    /// game's vertices after the network's earthwork). Straight runs of walled pieces share one wall in running bond, and a
    /// walled floor's open end is walled across, bonded at its corners (<see cref="LayWalls"/>).
    /// A support piece that would stand in a walking piece, or wholly under the ground, is left out. A walking piece nothing
    /// within reach holds (refused, which the search never lays: an older build's saved layout) is left unheld and counted
    /// (<see cref="Unheld"/>), not hung with an arch's first course or every cornice course down to the support ceiling;
    /// it and every wall cell stopped short (<see cref="ShortWalls"/>) are added to <paramref name="shortfall"/>, for the
    /// caller to say where. Last, the whole is read with the game's support rule (<see cref="HoldUp"/>): a support piece it
    /// would break is left out, never laid to fall.
    /// </summary>
    public static List<StructurePiece> Lay(StairKit kit, IReadOnlyList<StructurePiece> walking, Func<float, float, float> natural,
        Func<int, int, float> finished, Shortfall? shortfall = null, List<StructurePiece>? vines = null,
        Func<float, float, Heightmap.Biome>? biomeAt = null)
    {
        var laid = new List<StructurePiece>();
        var hung = vines != null && biomeAt != null ? new List<(StructurePiece host, StructurePiece vine)>() : null;
        var walls = new List<(Walk w, int downhill)>();
        var walks = new List<Walk>();
        foreach (StructurePiece piece in walking)
            if (WalkOf(piece, kit) is Walk w) walks.Add(w);
        foreach (Walk w in walks)
        {
            Choice choice = Choose(kit, w, natural);
            if (choice.Refused)
            {
                System.Threading.Interlocked.Increment(ref s_unheld);
                Vector2 middle = w.At(w.Depth / 2f, 0f);
                shortfall?.Unheld.Add(new Vector3(middle.x, w.Underside, middle.y));
                continue;
            }
            switch (choice.Style)
            {
                case Style.Wall: walls.Add((w, -choice.Side)); break;
                case Style.Arch: LayArches(laid, w, choice, natural); break;
                case Style.Cornice: LayCornices(laid, w, choice, walks, natural, hung); break;
            }
        }
        LayWalls(laid, walls, walks, finished, shortfall);
        var keep = new List<StructurePiece>();
        var seen = new HashSet<(string, float, float, float, float, float, float)>();
        List<StructurePiece> outcorners = laid.FindAll(p => p.Prefab == OutCorner);
        foreach (StructurePiece p in laid)
            if (!InWalking(p, walking, kit) && !BuriedPieces.Buried(p.Prefab, p.Position, p.YawDegrees, finished)
                && !(p.Prefab == Cornice && InOutcorner(p, outcorners)) && seen.Add((p.Prefab, p.Position.x, p.Position.y, p.Position.z, p.PitchDegrees, p.YawDegrees, p.RollDegrees)))
                keep.Add(p);
        HoldUp(walking, keep, natural, finished, shortfall);
        if (hung != null) HangVines(kit, walks, keep, hung, natural, finished, biomeAt!, vines!);
        return keep;
    }

    /// <summary>
    /// Black marble's vines, as the Dvergr viaduct hangs them on its corbels (<see cref="DvergrViaduct.CorbelVine"/>): a
    /// curtain down the downhill face of one cornice column under a walking piece (its first, <see cref="CorniceRow"/> 0),
    /// there at the viaduct's <see cref="DvergrViaduct.VineCurtainChance"/>, a vine on each course; ornaments only, hung
    /// after the support rule has read the staircase (<see cref="HoldUp"/>) and never read by it. A vine is hung only on a
    /// cornice still laid, with its origin at or over the finished ground (none inside it), and with no part of it in the
    /// headroom over any walked surface (<see cref="RoadRockPolicy.ClearanceHeight"/>). Its leaves may reach down to the
    /// ground and lie on it, as vanilla's Mistlands Dvergr structures drape theirs to the grass at the foot of the stone:
    /// the viaduct's <see cref="DvergrViaduct.VineGroundClear"/> is not read here. A vine grows only where the game's biome at
    /// its own place is one vanilla's vines grow in (<see cref="DvergrViaduct.VineBiomes"/>): a staircase climbing out of the
    /// Mistlands into the Mountains is bare up there. With no biome to read (<paramref name="biomeAt"/> null), none is hung.
    /// </summary>
    private static void HangVines(StairKit kit, IReadOnlyList<Walk> walks, List<StructurePiece> keep,
        List<(StructurePiece host, StructurePiece vine)> hung, Func<float, float, float> natural, Func<int, int, float> finished,
        Func<float, float, Heightmap.Biome> biomeAt, List<StructurePiece> vines)
    {
        var kept = new HashSet<StructurePiece>(keep);
        foreach (var (host, vine) in hung)
        {
            if (!kept.Contains(host)) continue;
            float g = BuriedPieces.MeshHeight(vine.Position.x, vine.Position.z, finished);
            if (float.IsNaN(g)) g = natural(vine.Position.x, vine.Position.z);
            if (vine.Position.y < g || (biomeAt(vine.Position.x, vine.Position.z) & DvergrViaduct.VineBiomes) == 0
                || InHeadroom(kit, walks, vine)) continue;
            vines.Add(vine);
        }
    }

    /// <summary>The vines' leaves about their origin (the meshes, <see cref="DvergrViaduct.Vine"/>): 2.3 m across their
    /// local x, from 1.17 under to 1.26 over on their local y, on the face of their box (local z -0.15).</summary>
    private const float VineHalfAcross = 1.15f, VineBelow = 1.17f, VineAbove = 1.26f, VineFront = 0.15f;

    /// <summary>Whether any part of a vine's leaves (a grid over them) stands over a walked piece's footprint between its
    /// walking surface and <see cref="RoadRockPolicy.ClearanceHeight"/> over it. Only the walks the leaves can reach are read:
    /// a walk whose footprint lies farther from the vine than its leaves reach (each of their half-extents added, a bound past
    /// any of their points) and its own half-extents (and <see cref="Over"/>'s slack) holds none of them.</summary>
    private static bool InHeadroom(StairKit kit, IReadOnlyList<Walk> walks, StructurePiece vine)
    {
        var origin = new Vector2(vine.Position.x, vine.Position.z);
        float leaves = VineHalfAcross + Mathf.Max(VineBelow, VineAbove) + VineFront;
        List<Walk>? near = null;
        foreach (Walk w in walks)
        {
            float reach = leaves + (w.Depth + w.Across) / 2f + 0.02f;
            if ((w.At(w.Depth / 2f, 0f) - origin).sqrMagnitude <= reach * reach) (near ??= new List<Walk>()).Add(w);
        }
        if (near == null) return false;
        Quaternion turn = StructurePlacement.Rotation(vine);
        for (int i = 0; i <= 4; i++)
            for (int j = 0; j <= 4; j++)
            {
                var local = new Vector3(Mathf.Lerp(-VineHalfAcross, VineHalfAcross, i / 4f), Mathf.Lerp(-VineBelow, VineAbove, j / 4f), -VineFront);
                Vector3 q = vine.Position + turn * local;
                var at = new Vector2(q.x, q.z);
                foreach (Walk w in near)
                {
                    if (!Over(w, at)) continue;
                    float surface = w.Surface(Mathf.Clamp(Vector2.Dot(at - w.Back, w.Dir), 0f, w.Depth), kit);
                    if (q.y > surface - 0.01f && q.y < surface + RoadRockPolicy.ClearanceHeight) return true;
                }
            }
        return false;
    }

    /// <summary>
    /// The laid staircase as the game's support rule holds it (<see cref="Holding"/>): every support piece of
    /// <paramref name="supports"/> under its material's least (the game breaks it, dropping its stone) left out, again until
    /// none is, as the game breaks one after another; each counted (<see cref="Fell"/>). Then every walking piece held under
    /// <see cref="DvergrViaduct.HoldMargin"/> times its least is counted (<see cref="Weak"/>) and said, but laid: walking
    /// pieces are never left out. The search keeps both from happening (<see cref="MostHungCourses"/>); this is what holds a
    /// layout the search did not (an older build's, or a road's cut under a wall the search could not see).
    /// </summary>
    private static void HoldUp(IReadOnlyList<StructurePiece> walking, List<StructurePiece> supports, Func<float, float, float> natural,
        Func<int, int, float> finished, Shortfall? shortfall)
    {
        if (supports.Count == 0) return;
        var mine = new HashSet<StructurePiece>(supports);
        for (int round = 0; round < 64; round++)
        {
            var all = new List<StructurePiece>(walking);
            all.AddRange(supports);
            var fell = new HashSet<StructurePiece>();
            var weak = new List<StructurePiece>();
            foreach (ViaductSupport.Held h in Holding(all, natural, finished))
            {
                if (mine.Contains(h.Piece)) { if (h.Support < h.Min) fell.Add(h.Piece); }
                else if (h.Share < DvergrViaduct.HoldMargin) weak.Add(h.Piece);
            }
            if (fell.Count > 0)
            {
                foreach (StructurePiece p in fell)
                {
                    System.Threading.Interlocked.Increment(ref s_fell);
                    shortfall?.Fell.Add(p.Position);
                }
                supports.RemoveAll(fell.Contains);
                continue;
            }
            foreach (StructurePiece p in weak)
            {
                System.Threading.Interlocked.Increment(ref s_weak);
                shortfall?.Weak.Add(p.Position);
            }
            return;
        }
    }

    private static void LayArches(List<StructurePiece> laid, Walk w, Choice choice, Func<float, float, float> natural)
    {
        float yaw = choice.Side > 0 ? w.Heading : StairLayout.Wrap(w.Heading + 180f); // the arch's tall end (+x) to the wall
        for (int row = 0; row < ArchRowCount(w); row++)
        {
            float along = ArchRow(row);
            int last = ArchCourse(w, along, choice.Side, natural);
            for (int c = 0; c <= last; c++)
            {
                Vector2 at = w.At(along, choice.Side * (2f * c + FillerShift(w)));
                laid.Add(Support("stone_wall_2x1", at, w.Underside - 0.5f - c, w.Heading));
                laid.Add(Support("stone_arch", at, w.Underside - 1.5f - c, yaw));
            }
        }
    }

    private static void LayCornices(List<StructurePiece> laid, Walk w, Choice choice, IReadOnlyList<Walk> walks,
        Func<float, float, float> natural, List<(StructurePiece host, StructurePiece vine)>? hung = null)
    {
        // The cornice's lower block is on its local -x: turned so that faces the wall.
        float yaw = choice.Side > 0 ? StairLayout.Wrap(w.Heading + 180f) : w.Heading;
        if (w.Stair) laid.Add(Support("blackmarble_2x1x1", w.At(0.5f, 0f), w.Underside - 0.5f, w.Heading));
        int end = CornerEnd(w, choice.Side, walks);
        if (end != 0)
        {
            // A square turn's outer corner: an outcorner a course, the first centred under the floor. Its block is on its
            // local -x and -z, its 2 m top overhanging +x and +z: turned so those face the open downhill side and the open
            // end (in game: blackmarble_outcorner at -9322.984,104.275,-789.888 turned 292.5, under a floor turned 292.5).
            // Each course below steps a metre in on both faces, into the slope and back from the open end, so its top's outer
            // edges stand flush under the block of the one above, as a straight column's do on its one face (in game, a
            // staircase at 1576,-6160 stepped into the slope only, its lower outcorner half a block or so too far
            // out, not lined up with the bottom of the one above it). It goes down to its own block in the ground, the
            // courses Choose priced where none within the support ceiling is.
            Vector2 outward = w.Right * -choice.Side + w.Dir * end;
            float cornerYaw = Mathf.Atan2(outward.x, outward.y) * Mathf.Rad2Deg - 45f;
            int last = CornerCourse(w, choice.Side, end, natural);
            if (last < 0) last = choice.Courses - 1;
            for (int c = 0; c <= last; c++)
                laid.Add(Support(OutCorner, CornerAt(w, choice.Side, end, c), w.Underside - 1f - 2f * c, cornerYaw));
            return;
        }
        // The viaduct's curtains: one column of a piece, there at its chance (HangVines).
        bool curtain = hung != null && StairWear.Roll(new Vector3(w.Back.x, w.Underside, w.Back.y), StairWear.RoleVine) < DvergrViaduct.VineCurtainChance;
        for (int row = 0; row < CorniceRowCount(w); row++)
        {
            var (along, top) = CorniceRow(w, row);
            int last = CorniceCourse(w, along, top, choice.Side, natural);
            for (int c = 0; c <= last; c++)
            {
                StructurePiece cornice = Support(Cornice, w.At(along, choice.Side * (c + FillerShift(w))), top - 1f - 2f * c, yaw);
                laid.Add(cornice);
                if (curtain && row == 0) hung!.Add((cornice, DvergrViaduct.CorbelVine(cornice)));
            }
        }
    }

    /// <summary>Black marble's corner cornice: a 2 x 2 top over a 1 m block in one quarter, 2 m tall (the rip: its top
    /// snap points at the four corners of a 2 m square, its bottom ones round the 1 m square on its local -x, -z).</summary>
    private const string OutCorner = "blackmarble_outcorner";
    /// <summary>Black marble's straight cornice: a 2 m top across over a 1 m block on its local -x, a metre along.</summary>
    private const string Cornice = "blackmarble_out_1";

    /// <summary>The middle of a corner floor's <paramref name="course"/>th outcorner: under the floor's middle, then a metre
    /// toward the wall side and a metre back from the open <paramref name="end"/> a course (<see cref="CornerEnd"/>).</summary>
    private static Vector2 CornerAt(Walk w, int side, int end, int course) => w.At(w.Depth / 2f - end * course, side * course);

    /// <summary>The first course of a corner floor's outcorners (<see cref="CornerAt"/>) whose block, the metre square
    /// quarter toward the wall side and away from the open end, is wholly in the ground, or -1 past the support ceiling.
    /// </summary>
    private static int CornerCourse(Walk w, int side, int end, Func<float, float, float> ground)
    {
        for (int c = 0; c < CorniceCourses; c++)
        {
            float blockTop = w.Underside - 2f - 2f * c, along = w.Depth / 2f - end * c, across = side * c;
            bool sunk = true;
            for (int a = 0; a <= 1 && sunk; a++)
                for (int k = 0; k <= 1; k++)
                {
                    Vector2 p = w.At(along - end * (a * 0.95f + 0.025f), across + side * (k * 0.95f + 0.025f));
                    if (ground(p.x, p.y) < blockTop) { sunk = false; break; }
                }
            if (sunk) return c;
        }
        return -1;
    }

    /// <summary>
    /// Whether a straight cornice lies wholly in an outcorner of the same course: its top in the outcorner's 2 x 2 top and
    /// its block in the outcorner's block. A corner's column, stepping back from its open end, stands a course down where
    /// the cornices of the piece behind it stand; laid twice, their faces would flicker.
    /// </summary>
    private static bool InOutcorner(StructurePiece cornice, IReadOnlyList<StructurePiece> outcorners)
    {
        Vector2 at = new Vector2(cornice.Position.x, cornice.Position.z);
        Vector2 cx = StairLayout.Right(cornice.YawDegrees), cz = StairLayout.Forward(cornice.YawDegrees);
        foreach (StructurePiece o in outcorners)
        {
            if (Mathf.Abs(o.Position.y - cornice.Position.y) > 0.01f) continue;
            Vector2 oat = new Vector2(o.Position.x, o.Position.z), ox = StairLayout.Right(o.YawDegrees), oz = StairLayout.Forward(o.YawDegrees);
            // A corner of the cornice (its local x, z) within the outcorner's -1..most on both its local axes: 1 its top, 0
            // its block.
            bool Within(float x, float z, float most)
            {
                Vector2 d = at + cx * x + cz * z - oat;
                float lx = Vector2.Dot(d, ox), lz = Vector2.Dot(d, oz);
                return lx >= -1.02f && lx <= most + 0.02f && lz >= -1.02f && lz <= most + 0.02f;
            }
            bool top = true, block = true;
            for (int j = -1; j <= 1; j += 2)
            {
                top &= Within(-1f, 0.5f * j, 1f) && Within(1f, 0.5f * j, 1f);
                block &= Within(-1f, 0.5f * j, 0f) && Within(0f, 0.5f * j, 0f);
            }
            if (top && block) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a black marble 2 x 2 floor is a square turn's outer corner, and which end: +1 its far end, -1 its near end,
    /// where the way comes on at one end and goes off at its wall side (a walked piece adjoins each), and its other end and
    /// its downhill side are open; 0 for any other piece. A turn whose outer corner is on the wall side is held as a straight
    /// piece: the piece on its downhill side holds it. A square turn laid by hand: the stair arrives at the corner floor's
    /// near end, the switchback's other floor beside it on the wall side, its far end and downhill side open.
    /// </summary>
    private static int CornerEnd(Walk w, int side, IReadOnlyList<Walk> walks)
    {
        if (!IsFloor(w)) return 0;
        float past = 0.25f, half = w.Across / 2f;
        bool Side(int s) => Adjoined(w, walks, w.At(w.Depth / 4f, s * (half + past)), w.At(w.Depth * 3f / 4f, s * (half + past)));
        if (Side(-side) || !Side(side)) return 0;
        bool far = EndAdjoined(w, walks, 1), near = EndAdjoined(w, walks, -1);
        return far == near ? 0 : far ? -1 : 1;
    }

    /// <summary>A 2 x 2 floor (a landing, a rest, a turn's wedge), not a stair or a narrower filler.</summary>
    private static bool IsFloor(Walk w) => !w.Stair && w.Across >= StairLayout.PieceRun && w.Depth >= StairLayout.PieceRun;

    /// <summary>Whether a walked piece adjoins a piece's far (<paramref name="end"/> +1) or near (-1) end.</summary>
    private static bool EndAdjoined(Walk w, IReadOnlyList<Walk> walks, int end)
    {
        float along = end > 0 ? w.Depth + 0.25f : -0.25f;
        return Adjoined(w, walks, w.At(along, -w.Across / 4f), w.At(along, w.Across / 4f));
    }

    /// <summary>Whether either point lies on a walked piece next to <paramref name="w"/>: one whose underside is within a
    /// rise of its own (a stair arriving, one leaving from its walking surface, a floor or filler level with it).</summary>
    private static bool Adjoined(Walk w, IReadOnlyList<Walk> walks, Vector2 a, Vector2 b)
    {
        foreach (Walk o in walks)
            if (Mathf.Abs(o.Underside - w.Underside) <= StairLayout.PieceRise + 0.01f && (Over(o, a) || Over(o, b))) return true;
        return false;
    }

    /// <summary>Walled pieces in straight runs: one heading, the same downhill side, each starting where the last ended.</summary>
    private static List<List<(Walk w, int downhill)>> StraightRuns(List<(Walk w, int downhill)> walls)
    {
        var runs = new List<List<(Walk w, int downhill)>>();
        var used = new bool[walls.Count];
        for (int i = 0; i < walls.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var run = new List<(Walk w, int downhill)> { walls[i] };
            for (bool grew = true; grew;)
            {
                grew = false;
                for (int j = 0; j < walls.Count; j++)
                {
                    if (used[j] || walls[j].downhill != walls[i].downhill || !SameLine(run[0].w, walls[j].w)) continue;
                    if (Vector2.Distance(run[run.Count - 1].w.At(run[run.Count - 1].w.Depth, 0f), walls[j].w.Back) < 0.05f) run.Add(walls[j]);
                    else if (Vector2.Distance(walls[j].w.At(walls[j].w.Depth, 0f), run[0].w.Back) < 0.05f) run.Insert(0, walls[j]);
                    else continue;
                    used[j] = true; grew = true;
                }
            }
            runs.Add(run);
        }
        return runs;
    }

    private static bool SameLine(Walk a, Walk b) =>
        Mathf.Abs(StairLayout.Wrap(a.Heading - b.Heading + 180f) - 180f) < 0.5f && Mathf.Abs(a.Across - b.Across) < 0.01f;

    /// <summary>
    /// The walls (one owner): one flush with the downhill edge of each straight run, under its downhill half or the whole of
    /// a 1 x 1 filler (<see cref="FillerShift"/>), and one across each walled floor's open end (a
    /// landing's end, a square turn's wedge), under both its halves, side by side floors' ends one wall. Each is a line of
    /// metre cells, each from the underside of the piece over it down in 1 m courses until the lowest is sunk
    /// <see cref="Bury"/> into the finished ground at every corner, never a course wholly under it; each course laid in 2x1
    /// stretchers along the line, staggered a metre course to course, a 1x1 where a stretcher does not fit.
    /// <para>Where a wall across an end meets a run's wall (their shared cell, the corner) the courses interlock: on a course
    /// where the run's stretcher runs through the corner the end wall starts past it; on one where the run would close at
    /// the corner with a 1x1, the end wall takes the corner with a 2x1 turned along it, and the run's course stops short.
    /// From there the end wall lays its stretchers on along the end, and any other run's corner a stretcher of it covers,
    /// that run leaves. Of the switchback at -1902,6630, as seen in game: the 90 degree wall corner had missing
    /// blocks, and the 1x1 there was replaced by hand with a 2x1 laid the perpendicular way.</para>
    /// </summary>
    private static void LayWalls(List<StructurePiece> laid, List<(Walk w, int downhill)> walls, IReadOnlyList<Walk> walks,
        Func<int, int, float> finished, Shortfall? shortfall)
    {
        var runs = new List<WallLine>();
        foreach (List<(Walk w, int downhill)> run in StraightRuns(walls)) runs.Add(WallLine.Along(run));
        List<WallLine> ends = EndWalls(walls, walks);
        var lines = new List<WallLine>(runs);
        lines.AddRange(ends);
        foreach (WallLine line in lines) line.Course(finished, shortfall);
        // Each end wall's cells that are a run's too: the corners.
        var corners = new Dictionary<(WallLine, int), (WallLine run, int cell)>();
        foreach (WallLine end in ends)
            for (int i = 0; i < end.Tops.Count; i++)
                foreach (WallLine run in runs)
                    for (int j = 0; j < run.Tops.Count; j++)
                        if (Vector2.Distance(end.Cell(i), run.Cell(j)) < 0.05f && Mathf.Abs(end.Tops[i] - run.Tops[j]) < 0.01f)
                            corners[(end, i)] = (run, j);
        // Course by course at world heights, so the walls meeting at a corner are laid together.
        var courseTops = new List<float>();
        foreach (WallLine line in lines)
            for (float top = line.Highest; top > line.Lowest + 0.01f; top -= 1f)
                if (!courseTops.Exists(t => Mathf.Abs(t - top) < 0.01f)) courseTops.Add(top);
        courseTops.Sort((a, b) => b.CompareTo(a));
        var blocks = new Dictionary<WallLine, List<StructurePiece>>();
        foreach (WallLine line in lines) blocks[line] = new List<StructurePiece>();
        foreach (float courseTop in courseTops)
        {
            // The corners each run leaves to an end wall this course.
            var taken = new Dictionary<WallLine, HashSet<int>>();
            foreach (WallLine run in runs) taken[run] = new HashSet<int>();
            bool Runs((WallLine run, int cell) c) => c.run.OnCourse(courseTop) && c.run.InCourse(c.cell, courseTop);
            bool RunsThrough((WallLine run, int cell) c) =>
                Runs(c) && Bond(c.run, courseTop, taken[c.run]).Exists(b => b.cells == 2 && (b.i == c.cell || b.i + 1 == c.cell));
            foreach (WallLine end in ends)
            {
                if (!end.OnCourse(courseTop)) continue;
                for (int i = 0; i < end.Tops.Count;)
                {
                    if (!end.InCourse(i, courseTop)) { i++; continue; }
                    bool corner = corners.TryGetValue((end, i), out var here);
                    if (corner && RunsThrough(here)) { i++; continue; }
                    if (i + 1 < end.Tops.Count && end.InCourse(i + 1, courseTop))
                    {
                        blocks[end].Add(end.Block(i, 2, courseTop));
                        if (corner) taken[here.run].Add(here.cell);
                        if (corners.TryGetValue((end, i + 1), out var next)) taken[next.run].Add(next.cell);
                        i += 2;
                        continue;
                    }
                    if (!corner || !Runs(here)) blocks[end].Add(end.Block(i, 1, courseTop)); // a corner's lone cell is the run's own
                    i++;
                }
            }
            foreach (WallLine run in runs)
                if (run.OnCourse(courseTop))
                    foreach (var (i, cells) in Bond(run, courseTop, taken[run])) blocks[run].Add(run.Block(i, cells, courseTop));
        }
        foreach (WallLine line in lines) laid.AddRange(blocks[line]);
    }

    /// <summary>A run's blocks in one course, in running bond: a 2x1 over a cell and the next where both are in it and the
    /// course's stagger starts one there, a 1x1 elsewhere; the cells an end wall took at a corner left out.</summary>
    private static List<(int i, int cells)> Bond(WallLine run, float courseTop, HashSet<int> taken)
    {
        int level = Mathf.RoundToInt(run.Highest - courseTop);
        bool Free(int i) => i < run.Tops.Count && run.InCourse(i, courseTop) && !taken.Contains(i);
        var bond = new List<(int i, int cells)>();
        for (int i = 0; i < run.Tops.Count;)
        {
            if (!Free(i)) { i++; continue; }
            bool pair = Free(i + 1) && (i + level) % 2 == 0;
            bond.Add((i, pair ? 2 : 1));
            i += pair ? 2 : 1;
        }
        return bond;
    }

    /// <summary>The walls across walled floors' open ends: each from the corner under its downhill edge across both its
    /// halves, the ends of floors side by side (a switchback's landing) joined into one.</summary>
    private static List<WallLine> EndWalls(List<(Walk w, int downhill)> walls, IReadOnlyList<Walk> walks)
    {
        var ends = new List<WallLine>();
        foreach (var (w, downhill) in walls)
        {
            if (!IsFloor(w)) continue;
            for (int end = -1; end <= 1; end += 2)
                if (!EndAdjoined(w, walks, end))
                    ends.Add(WallLine.Across(w, downhill, end));
        }
        for (bool joined = true; joined;)
        {
            joined = false;
            for (int a = 0; a < ends.Count && !joined; a++)
                for (int b = 0; b < ends.Count && !joined; b++)
                {
                    if (a == b || Mathf.Abs(Vector2.Dot(ends[a].Dir, ends[b].Dir)) < 0.999f) continue;
                    WallLine next = Vector2.Dot(ends[a].Dir, ends[b].Dir) > 0f ? ends[b] : ends[b].Reversed();
                    if (Vector2.Distance(ends[a].At(ends[a].Tops.Count), next.Start) > 0.05f) continue;
                    ends[a].Tops.AddRange(next.Tops);
                    ends.RemoveAt(b);
                    joined = true;
                }
        }
        return ends;
    }

    /// <summary>A wall's line of 1 m cells: cell i's middle <see cref="Start"/> plus i + 0.5 m along
    /// <see cref="Heading"/>, its top the underside of the piece over it, its courses down to <see cref="Bottom"/>.</summary>
    private sealed class WallLine
    {
        public Vector2 Start;
        public float Heading;
        public readonly List<float> Tops = new();
        public int[] Bottom = Array.Empty<int>();
        public float Highest = float.NegativeInfinity, Lowest = float.PositiveInfinity;

        public Vector2 Dir => StairLayout.Forward(Heading);
        public Vector2 At(float along) => Start + Dir * along;
        public Vector2 Cell(int i) => At(i + 0.5f);

        /// <summary>A straight run's wall, flush with its downhill edge.</summary>
        public static WallLine Along(List<(Walk w, int downhill)> run)
        {
            Walk first = run[0].w;
            var line = new WallLine { Start = first.At(0f, -run[0].downhill * FillerShift(first, 1f)), Heading = first.Heading };
            foreach (var (w, _) in run)
                for (int m = 0; m < Mathf.RoundToInt(w.Depth); m++) line.Tops.Add(w.Underside);
            return line;
        }

        /// <summary>The wall across a floor's far (<paramref name="end"/> +1) or near (-1) metre, from its corner with the
        /// run's wall under the <paramref name="downhill"/> edge across to the other.</summary>
        public static WallLine Across(Walk w, int downhill, int end)
        {
            var line = new WallLine
            {
                Start = w.At(end > 0 ? w.Depth - 0.5f : 0.5f, downhill * w.Across / 2f),
                Heading = StairLayout.Wrap(w.Heading - downhill * 90f),
            };
            for (int m = 0; m < Mathf.RoundToInt(w.Across); m++) line.Tops.Add(w.Underside);
            return line;
        }

        public WallLine Reversed()
        {
            var line = new WallLine { Start = At(Tops.Count), Heading = StairLayout.Wrap(Heading + 180f) };
            for (int i = Tops.Count - 1; i >= 0; i--) line.Tops.Add(Tops[i]);
            return line;
        }

        /// <summary>Each cell's courses, from its top down: the deepest it needs. None where the finished ground under it is
        /// not known (inside a place's footprint whose levelling is not known: <see cref="RoadTerrainModifier.FinishedGround"/>),
        /// as columns were carried down: walled to a guess, a wall stood 8 m down through the place's own ground. A cell
        /// that would need more than <see cref="MostCourses"/> stops there, counted in <see cref="ShortWalls"/>.</summary>
        public void Course(Func<int, int, float> finished, Shortfall? shortfall)
        {
            Bottom = new int[Tops.Count];
            for (int i = 0; i < Tops.Count; i++)
            {
                Bottom[i] = -1;
                float low = LowestUnder(i, finished);
                if (float.IsNaN(low)) continue;
                for (int c = 0; ; c++)
                {
                    float top = Tops[i] - c;
                    if (top <= low) break; // wholly under the ground: the course above is sunk already
                    Bottom[i] = c;
                    if (top - 1f <= low - Bury) break;
                    if (c + 1 == MostCourses)
                    {
                        System.Threading.Interlocked.Increment(ref s_shortWalls);
                        Vector2 cell = Cell(i);
                        shortfall?.ShortWalls.Add(new Vector3(cell.x, top - 1f, cell.y));
                        break;
                    }
                }
                if (Bottom[i] < 0) continue;
                Highest = Mathf.Max(Highest, Tops[i]);
                Lowest = Mathf.Min(Lowest, Tops[i] - Bottom[i] - 1f);
            }
        }

        /// <summary>Whether the course with its top at <paramref name="courseTop"/> is one of this wall's (its courses are
        /// a whole number of metres under its highest).</summary>
        public bool OnCourse(float courseTop)
        {
            float under = Highest - courseTop;
            return under > -0.01f && Mathf.Abs(under - Mathf.Round(under)) < 0.01f;
        }

        public bool InCourse(int i, float courseTop) =>
            Bottom[i] >= 0 && courseTop <= Tops[i] + 0.01f && courseTop >= Tops[i] - Bottom[i] - 0.01f;

        /// <summary>A 2x1 stretcher over cell i and the next, or a 1x1 over cell i, in the course.</summary>
        public StructurePiece Block(int i, int cells, float courseTop) => cells == 2
            ? Support("stone_wall_2x1", At(i + 1f), courseTop - 0.5f, StairLayout.Wrap(Heading - 90f))
            : Support("stone_wall_1x1", Cell(i), courseTop - 0.5f, Heading);

        /// <summary>The lowest finished ground under cell <paramref name="i"/> (<see cref="BuriedPieces.LowestOver"/>): NaN
        /// where any of it is not known.</summary>
        private float LowestUnder(int i, Func<int, int, float> finished)
        {
            Vector2 right = StairLayout.Right(Heading), near = At(i + 0.05f), far = At(i + 0.95f);
            return BuriedPieces.LowestOver(new[] { near - right * 0.45f, far - right * 0.45f, far + right * 0.45f, near + right * 0.45f }, finished);
        }
    }

    /// <summary>
    /// Whether a support piece stands in any walking piece of its staircase, seen from above and in height. One wholly under
    /// a walking piece (a block, arch or cornice under its own stair) never does: whatever it shares there is stone in stone
    /// under the walk. A turn's 2 x 1 filler, level with the stair before it, reaches under the back corner of the stair past
    /// the turn, and the block under that stair's lower half shares that corner; left out, stone's arch and black marble's
    /// cornice hung on nothing (in game: stone at -4787.5,1727.7, where it was laid by hand; black marble on the
    /// staircase from 1768,6960 to 1712,6920).
    /// </summary>
    private static bool InWalking(StructurePiece support, IReadOnlyList<StructurePiece> walking, StairKit kit)
    {
        if (!BuriedPieces.BoundsOf(support.Prefab, out Vector3 smin, out Vector3 smax)) return false;
        Vector2 right = StairLayout.Right(support.YawDegrees), forward = StairLayout.Forward(support.YawDegrees);
        Vector2 sc = new Vector2(support.Position.x, support.Position.z) + right * ((smin.x + smax.x) / 2f) + forward * ((smin.z + smax.z) / 2f);
        Vector2 hx = right * ((smax.x - smin.x) / 2f), hz = forward * ((smax.z - smin.z) / 2f);
        var corners = new[] { sc - hx - hz, sc + hx - hz, sc + hx + hz, sc - hx + hz };
        foreach (StructurePiece p in walking)
            if (WalkOf(p, kit) is Walk w && support.Position.y + smax.y <= w.Underside + 0.01f && Array.TrueForAll(corners, c => Over(w, c)))
                return false;
        foreach (StructurePiece p in walking)
        {
            if (WalkOf(p, kit) is not Walk w) continue;
            float wTop = w.Underside + (w.Stair ? StairLayout.PieceRise : kit.FloorThickness);
            if (support.Position.y + smax.y <= w.Underside + 0.01f || support.Position.y + smin.y >= wTop - 0.01f) continue;
            if (StairLayout.Footprints(sc, support.YawDegrees, smax.x - smin.x, smax.z - smin.z,
                    w.At(w.Depth / 2f, 0f), w.Heading, w.Across, w.Depth))
                return true;
        }
        return false;
    }

    /// <summary>Whether a point lies within a walking piece's footprint, seen from above.</summary>
    private static bool Over(Walk w, Vector2 point)
    {
        float along = Vector2.Dot(point - w.Back, w.Dir), across = Vector2.Dot(point - w.Back, w.Right);
        return along >= -0.01f && along <= w.Depth + 0.01f && Mathf.Abs(across) <= w.Across / 2f + 0.01f;
    }

    /// <summary>A staircase piece's centre of mass on its own axes (WearNTear.m_comOffset, read off the game's prefabs): a
    /// stair's half a metre over its foot, stone's arch 0.6 m toward its tall end; every other piece's at its transform.</summary>
    private static Vector3 ComOffset(string prefab) => prefab switch
    {
        "blackmarble_stair" or "stone_stair" => new Vector3(0f, 0.5f, 0f),
        "stone_arch" => new Vector3(0.6f, 0f, 0f),
        _ => Vector3.zero,
    };

    /// <summary>
    /// What the game's support rule (<see cref="SupportGraph"/>) holds of a dressed staircase: the support every stone or
    /// black marble piece of <paramref name="pieces"/> (its walking pieces and their supports, as <see cref="StairKit.Dress"/>
    /// lays them) settles at, each piece its collider's box turned as it is laid, grounded where that box grown
    /// <see cref="SupportGraph.Reach"/> meets the game's terrain: the <paramref name="finished"/> ground's mesh
    /// (<see cref="BuriedPieces.MeshHeight"/>), the <paramref name="natural"/> ground where that is not known. Its railing
    /// (wood) is not read.
    /// </summary>
    public static List<ViaductSupport.Held> Holding(IReadOnlyList<StructurePiece> pieces, Func<float, float, float> natural,
        Func<int, int, float> finished)
    {
        var graph = new List<SupportGraph.Piece>();
        var laid = new List<StructurePiece>();
        foreach (StructurePiece p in pieces)
        {
            if (ViaductSupport.MaterialOf(p.Prefab) is not SupportGraph.Material material || p.PitchDegrees != 0f || p.RollDegrees != 0f
                || !BuriedPieces.BoundsOf(p.Prefab, out Vector3 min, out Vector3 max))
                continue;
            bool mesh = MeshCollider(p.Prefab);
            graph.Add(new SupportGraph.Piece(p.Position, ComOffset(p.Prefab), p.YawDegrees, min, max, material,
                Grounded(p, min, max, mesh, natural, finished), mesh));
            laid.Add(p);
        }
        float[] support = SupportGraph.Settle(graph);
        var held = new List<ViaductSupport.Held>(laid.Count);
        for (int i = 0; i < laid.Count; i++) held.Add(new ViaductSupport.Held(laid[i], support[i], graph[i].Material.Min));
        return held;
    }

    /// <summary>A staircase piece whose collider is a convex mesh, not a box (the game's prefabs): the game overlaps its
    /// bounds on the world's axes (<see cref="SupportGraph.Piece.WorldBounds"/>).</summary>
    private static bool MeshCollider(string prefab) =>
        prefab is "blackmarble_stair" or "stone_stair" or "blackmarble_out_1" or "blackmarble_outcorner";

    /// <summary>Whether a laid piece's box, grown <see cref="SupportGraph.Reach"/>, meets the ground's surface: the ground over
    /// its footprint (grown too), on a half-metre grid, reaches its bottom somewhere and is under its top somewhere. A
    /// <paramref name="mesh"/> collider's footprint is its box's bounds on the world's axes.</summary>
    private static bool Grounded(StructurePiece p, Vector3 min, Vector3 max, bool mesh, Func<float, float, float> natural,
        Func<int, int, float> finished)
    {
        float r = SupportGraph.Reach, lo = float.MaxValue, hi = float.MinValue;
        Vector2 at = new(p.Position.x, p.Position.z), right = StairLayout.Right(p.YawDegrees), forward = StairLayout.Forward(p.YawDegrees);
        if (mesh)
        {
            // The turned box's bounds on the world's axes, about its middle.
            Vector2 mid = at + right * ((min.x + max.x) * 0.5f) + forward * ((min.z + max.z) * 0.5f);
            float hx = (max.x - min.x) * 0.5f, hz = (max.z - min.z) * 0.5f;
            float ex = Mathf.Abs(right.x) * hx + Mathf.Abs(forward.x) * hz, ez = Mathf.Abs(right.y) * hx + Mathf.Abs(forward.y) * hz;
            at = mid; right = new Vector2(1f, 0f); forward = new Vector2(0f, 1f);
            min = new Vector3(-ex, min.y, -ez); max = new Vector3(ex, max.y, ez);
        }
        float x0 = min.x - r, x1 = max.x + r, z0 = min.z - r, z1 = max.z + r;
        int nx = Mathf.Max(2, Mathf.CeilToInt((x1 - x0) / 0.5f)), nz = Mathf.Max(2, Mathf.CeilToInt((z1 - z0) / 0.5f));
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= nz; j++)
            {
                Vector2 q = at + right * Mathf.Lerp(x0, x1, i / (float)nx) + forward * Mathf.Lerp(z0, z1, j / (float)nz);
                float g = BuriedPieces.MeshHeight(q.x, q.y, finished);
                if (float.IsNaN(g)) g = natural(q.x, q.y);
                lo = Mathf.Min(lo, g);
                hi = Mathf.Max(hi, g);
            }
        return hi >= p.Position.y + min.y - r && lo <= p.Position.y + max.y + r;
    }

    private static StructurePiece Support(string prefab, Vector2 at, float y, float yaw) => new()
    {
        Kind = StructurePieceKind.StairSupport, Prefab = prefab, Position = new Vector3(at.x, y, at.y), YawDegrees = StairLayout.Wrap(yaw),
    };
}
