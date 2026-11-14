using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The one reader of a staircase's ends (assistant-written): for a laid staircase and a ground, each end's last stair, the
/// drop off it by the end rule, what is laid past it and whether a walker can step off what is laid there; and the one reader
/// of the ground off any exit edge the layout decides an end by (StairLayout.FinishTop, RoadStairRuns.StopAtTheLip, ...).
/// <para>The end rule: the last stair ends over the lip. The ground off an exit edge is read in one place: where a floor laid
/// off it would stand, at that floor's two far corners (<see cref="Corners"/>: a floor's own size,
/// <see cref="StairLayout.PieceRun"/>, out from the edge, at either side of the stair's width). A drop no more than a step
/// (<see cref="StairLayout.StepUp"/>) takes a floor off it; up to a ledge (<see cref="StairRailing.LedgeDrop"/>) a stair down
/// onto the ground; past that the stair overshot, the floor laid off the stair before it; ground over the edge by more than a
/// step is short, and the stairs carry on. A floor lands by one rule (<see cref="Exit"/>, the owner's of 12 Nov 2026): one of
/// its free edges, straight on or either side, has both corners within a step of the ground; that edge is its exit. Where it
/// does not, the far corner not within a step gives the case (the gentler of two).</para>
/// <para>A walker steps off an end's last walked pieces where, across a free edge of one, the same rule finds the ground
/// within a step of its walking height: off a floor (laid past the last stair, or a walkway's), both its corners
/// (<see cref="StepsOff"/>); off the last stair, bare, where a floor laid off it would land (<see cref="StairStepsOff"/>); off a
/// stair down laid past it, its low edge's middle, as its finish was accepted (<see cref="MeetsLowEdge"/>). A stair's low edge
/// meets ground up to a rise over it (its slope meets it: a foot is sunk a rise on purpose). An edge met by another staircase's walked piece, or by its
/// road's surface level with it within a step, is joined. A top whose last walked piece is its
/// climbing stair, joined to nothing, has no finish laid: a miss. The ends are found along the walk (each walked piece's
/// neighbours, the pieces its edges step onto), not by distance: a switchback's upper flight passes back by its foot.</para>
/// </summary>
public static class StairEnds
{
    /// <summary>What the end rule chooses from the drop off the last stair.</summary>
    public enum Case { Floor, Reverse, StepBack, Short }

    /// <summary>Whether a walker steps off the end: <see cref="Miss"/>, a top with nothing laid past its last stair;
    /// <see cref="NotVerified"/>, a foot stepped off onto a road whose slope into it cannot be read, its road not known
    /// (<see cref="OntoItsRoad"/>): neither a way off nor a gap.</summary>
    public enum Verdict { Ok, Miss, Gap, NotVerified }

    public sealed class End
    {
        public bool Top;
        /// <summary>The last stair's exit edge's middle and its walking height there (null: no stair found).</summary>
        public Vector3? Edge;
        /// <summary>The drops at the far corners, left and right of the way out (walking height less the ground).</summary>
        public float Left = float.NaN, Right = float.NaN;
        public Case Rule;
        public bool SidesDisagree;
        /// <summary>Floors and stairs laid past the last stair; a turn's fillers between them are neither.</summary>
        public int FloorsPast, StairsPast;
        /// <summary>The best step off the end's last walked pieces: ground less walking height, positive where the ground is
        /// higher; NaN where none was read.</summary>
        public float WalkOff = float.NaN;
        public bool Joined, OntoRoad;
        /// <summary>A foot stepped off onto a road that is not its own: no road of its own recorded at that end
        /// (<see cref="OntoItsRoad"/>'s first case: none planned to it, or its road's end there not kept). Never a way off
        /// (RoadStairRuns.NoWayOff refuses its road: each end is met by its road).</summary>
        public bool OntoAnotherRoad;
        public Verdict Verdict;
        public string Why = "";

        public string Laid => FloorsPast == 0 && StairsPast == 0 ? "bare stair"
            : (FloorsPast > 0 ? $"{FloorsPast} floor{(FloorsPast > 1 ? "s" : "")}" : "") + (FloorsPast > 0 && StairsPast > 0 ? " + " : "")
              + (StairsPast > 0 ? $"{StairsPast} stair{(StairsPast > 1 ? "s" : "")} down" : "");
    }

    /// <summary>The rule's case for one drop (walking height less the ground).</summary>
    public static Case CaseOf(float drop) =>
        StairLayout.Walkable(drop, 0f) ? Case.Floor
        : drop < 0f ? Case.Short
        : drop <= StairRailing.LedgeDrop ? Case.Reverse
        : Case.StepBack;

    /// <summary>The two far corners of the floor an exit edge at <paramref name="edge"/> (walked out on <paramref name="dir"/>,
    /// <paramref name="width"/> wide) would take: a floor's size out, at either side of its width (a piece's at least).
    /// The one place the ground off an exit edge is read. The one owner of staircase ends (docs/OWNERS.md).</summary>
    public static (Vector2 left, Vector2 right) Corners(Vector2 edge, Vector2 dir, float width)
    {
        Vector2 right = new Vector2(dir.y, -dir.x), far = edge + dir * StairLayout.PieceRun;
        float half = Mathf.Max(StairLayout.PieceRun, width) / 2f;
        return (far - right * half, far + right * half);
    }

    /// <summary>The drops at the two far corners (<see cref="Corners"/>) off an exit edge at <paramref name="height"/>: left, right. The one owner of staircase ends (docs/OWNERS.md).</summary>
    public static (float left, float right) Drops(Vector2 edge, Vector2 dir, float height, float width, Func<float, float, float> ground)
    {
        var (l, r) = Corners(edge, dir, width);
        return (height - ground(l.x, l.y), height - ground(r.x, r.y));
    }

    /// <summary>A floor's edges a walker may leave it by: straight on (its front, the far edge) or either side. Never its back,
    /// the edge it comes off the stair from.</summary>
    public enum FloorEdge { None, Front, Right, Left }

    /// <summary>The drops at a floor's four corners: the floor laid off an exit edge at <paramref name="edge"/> (walked out on
    /// <paramref name="dir"/>, <paramref name="width"/> wide, a floor's own size deep), or for a floor already laid, from its back
    /// edge: its back corners at <paramref name="edge"/> and its far corners (<see cref="Corners"/>), left and right of the way
    /// out. The one owner of staircase ends (docs/OWNERS.md).</summary>
    public static (float backLeft, float backRight, float farLeft, float farRight) FloorDrops(Vector2 edge, Vector2 dir, float height, float width,
        Func<float, float, float> ground)
    {
        Vector2 right = new Vector2(dir.y, -dir.x);
        float half = Mathf.Max(StairLayout.PieceRun, width) / 2f;
        Vector2 bl = edge - right * half, br = edge + right * half;
        var (fl, fr) = Corners(edge, dir, width);
        return (height - ground(bl.x, bl.y), height - ground(br.x, br.y), height - ground(fl.x, fl.y), height - ground(fr.x, fr.y));
    }

    /// <summary>Whether a drop (walking height less the ground) is within the band a walker steps off at: within a step of the
    /// ground (StairLayout.Walkable), or the ground over it by up to <paramref name="over"/> (a rise at a stair's low edge, whose
    /// slope meets it).</summary>
    private static bool InBand(float d, float over) => StairLayout.Walkable(d, 0f) || d < 0f && -d <= over;

    /// <summary>Whether an edge with these two corners' drops lands within the band (<see cref="InBand"/>): BOTH corners in it,
    /// a corner not read left out, one at least read.</summary>
    private static bool EdgeIn(float a, float b, float over) =>
        !(float.IsNaN(a) && float.IsNaN(b)) && (float.IsNaN(a) || InBand(a, over)) && (float.IsNaN(b) || InBand(b, over));

    /// <summary>
    /// The one rule of whether a floor lands (the owner's decision, 12 Nov 2026): an edge of it is a way off where BOTH its
    /// corners stand within a step of the ground (StairLayout.Walkable; a corner not read is left out). A floor lands where one
    /// of its free edges does (<see cref="Exit"/>). Read at one corner, a road top climbed on at -9424.6,-1413.0 took a floor
    /// with its far corners 0.41 m under and 0.61 m over the ground; by the mean of the two its far edge read level. The one
    /// owner of staircase ends (docs/OWNERS.md).
    /// </summary>
    public static bool EdgeLands(float a, float b) => EdgeIn(a, b, StairLayout.StepUp);

    /// <summary>
    /// A floor's exit (<see cref="EdgeLands"/>, the one rule): the first of its free edges, straight on, its right, its left,
    /// whose two corners stand within a step of the ground; never its back, nor an edge <paramref name="joined"/> to another
    /// walk. None where no free edge does: the floor does not land. In game: an approach's top at 5704.6,3590.9, the ground
    /// under its floor falling away on its left and past its far edge, level on its right: its exit is its right side. The
    /// railing leaves the exit open (StairRailing). The one owner of staircase ends (docs/OWNERS.md).
    /// </summary>
    /// <para>A floor whose edge <paramref name="onward"/> carries the walk on to a stair down laid off it (the flight, a 2x2
    /// landing, then the stair down: the owner's of 25 Oct and 12 Nov 2026) has that stair for its exit: the floor need not land
    /// by its own edges, and the stair lands by its low edge (StairLayout.TerminalExit.StepsDownOnto, <see cref="MeetsLowEdge"/>).</para>
    /// <param name="also">Asked of a landing edge's two corners' drops too, where given (the railing's: its ground built dry).</param>
    public static FloorEdge Exit((float backLeft, float backRight, float farLeft, float farRight) d, Func<FloorEdge, bool>? joined = null,
        FloorEdge onward = FloorEdge.None, Func<float, float, bool>? also = null) =>
        onward != FloorEdge.None ? onward : Exit(d, StairLayout.StepUp, joined, also);

    /// <summary><see cref="Exit(ValueTuple{float, float, float, float}, Func{FloorEdge, bool})"/> with the ground allowed over
    /// the walk by up to <paramref name="over"/> (a rise off a bare stair's low edge, its slope meeting the ground).</summary>
    /// <param name="also">Asked of a landing edge's two corners' drops too (a foot's: its ground built dry), where given.</param>
    internal static FloorEdge Exit((float backLeft, float backRight, float farLeft, float farRight) d, float over, Func<FloorEdge, bool>? joined,
        Func<float, float, bool>? also = null)
    {
        bool Takes(FloorEdge e, float a, float b) => (joined == null || !joined(e)) && EdgeIn(a, b, over) && (also == null || also(a, b));
        foreach (FloorEdge e in new[] { FloorEdge.Front, FloorEdge.Right, FloorEdge.Left })
        {
            var (a, b) = Corners(d, e);
            if (Takes(e, a, b)) return e;
        }
        return FloorEdge.None;
    }

    /// <summary>The drops at edge <paramref name="e"/>'s two corners of a floor's four (<see cref="FloorDrops"/>).</summary>
    internal static (float a, float b) Corners((float backLeft, float backRight, float farLeft, float farRight) d, FloorEdge e) => e switch
    {
        FloorEdge.Front => (d.farLeft, d.farRight),
        FloorEdge.Right => (d.backRight, d.farRight),
        FloorEdge.Left => (d.backLeft, d.farLeft),
        _ => (float.NaN, float.NaN),
    };

    /// <summary>Whether a walker steps off a bare stair's exit edge at <paramref name="edge"/> (walked off on
    /// <paramref name="outward"/>, at <paramref name="height"/>) onto the ground: where a floor laid off it would land
    /// (<see cref="Exit"/>, the one rule, its corners within a step under the walk and up to <paramref name="over"/> over it: a
    /// rise at a stair's low edge, whose slope meets the ground; a step at its high edge). The ground the floor would stand on
    /// is the ground the walker steps onto. <paramref name="off"/>: the ground less the walking height at the far corner
    /// furthest from the band, for the line.</summary>
    /// <para>With <paramref name="dry"/> (a foot's: RoadWater.BuiltDry), the exit's ground must be built dry at both its
    /// corners; <paramref name="wet"/> where an edge lands but none of them dry. <paramref name="off"/> is then read at the exit's
    /// lower corner, the ground the walker steps onto there: read off another edge, a foot level and dry on its right side at
    /// 31 m was judged by its far left corner at 29 m, wet.</para>
    internal static bool StairStepsOff(Vector2 edge, Vector2 outward, float height, float width, float over, Func<float, float, float> ground,
        out float off, Func<float, bool>? dry, out bool wet)
    {
        var d = FloorDrops(edge, outward, height, width, ground);
        StepsOff(edge, outward, height, width, over, ground, out off);
        bool Dry(float a, float b) => (float.IsNaN(a) || dry!(height - a)) && (float.IsNaN(b) || dry!(height - b));
        FloorEdge exit = Exit(d, over, null, dry == null ? null : Dry);
        wet = exit == FloorEdge.None && dry != null && Exit(d, over, null) != FloorEdge.None;
        if (exit == FloorEdge.None) return false;
        var (ea, eb) = Corners(d, exit);
        off = -(float.IsNaN(ea) ? eb : float.IsNaN(eb) ? ea : Mathf.Max(ea, eb));
        return true;
    }

    /// <summary><see cref="StairStepsOff(Vector2, Vector2, float, float, float, Func{float, float, float}, out float, Func{float, bool}, out bool)"/>
    /// with no water asked.</summary>
    internal static bool StairStepsOff(Vector2 edge, Vector2 outward, float height, float width, float over, Func<float, float, float> ground, out float off) =>
        StairStepsOff(edge, outward, height, width, over, ground, out off, null, out _);

    /// <summary>The rule's case off an exit edge for a floor's four corners' drops (<see cref="FloorDrops"/>): a floor where it
    /// lands (<see cref="Exit"/>); else, of the far corners not within a step, their case (where two read different cases, the
    /// gentler's, <see cref="Gentler"/>), and with both far corners within a step (or not read), of the back corners not
    /// within one. <paramref name="disagree"/> where the far corners read different cases. NaN corners (no ground read) are
    /// left out. The one owner of staircase ends (docs/OWNERS.md).</summary>
    public static Case Choose((float backLeft, float backRight, float farLeft, float farRight) d, out bool disagree)
    {
        disagree = !float.IsNaN(d.farLeft) && !float.IsNaN(d.farRight) && CaseOf(d.farLeft) != CaseOf(d.farRight);
        if (Exit(d) != FloorEdge.None) return Case.Floor;
        static bool Off(float v) => !float.IsNaN(v) && CaseOf(v) != Case.Floor;
        foreach (var (a, b) in new[] { (d.farLeft, d.farRight), (d.backLeft, d.backRight) })
        {
            if (Off(a) && Off(b)) return CaseOf(Gentler(a, b));
            if (Off(a)) return CaseOf(a);
            if (Off(b)) return CaseOf(b);
        }
        return Case.Floor;
    }

    /// <summary>Of two corners' drops (<see cref="Drops"/>) the gentler: the one nearer level (the first where they are as near),
    /// the other where one is NaN (no ground read), NaN where both are. The one combine of two corners: the rule's case where they
    /// differ (<see cref="Choose"/>), and the ground ahead of a top that gates its finish (<see cref="OverAhead"/>).</summary>
    public static float Gentler(float left, float right) =>
        float.IsNaN(left) ? right : float.IsNaN(right) ? left : Mathf.Abs(left) <= Mathf.Abs(right) ? left : right;

    /// <summary>How far the ground ahead of an exit edge at <paramref name="edge"/> stands over its walking height
    /// <paramref name="height"/>: read at the two far corners of the floor it would take (<see cref="Corners"/>, a floor out),
    /// the gentler of the two (<see cref="Gentler"/>). The one reading of the ground ahead of a top, for the search's arrival
    /// and a road top's finish; each keeps its own band. The one owner of staircase ends (docs/OWNERS.md).</summary>
    public static float OverAhead(Vector2 edge, Vector2 dir, float height, float width, Func<float, float, float> ground)
    {
        var (l, r) = Drops(edge, dir, height, width, ground);
        return -Gentler(l, r);
    }

    /// <summary>The rule's case off an exit edge at <paramref name="edge"/> (<see cref="Drops"/>, <see cref="Choose"/>).</summary>
    public static Case At(Vector2 edge, Vector2 dir, float height, float width, Func<float, float, float> ground) =>
        Choose(FloorDrops(edge, dir, height, width, ground), out _);

    /// <summary>Whether a floor laid off an exit edge lands (<see cref="Exit"/>, the one rule: one of its free edges with both
    /// corners within a step of the ground). For a floor already laid, from its back edge: its own corners.</summary>
    /// <param name="onward">The edge carrying the walk on to a stair down laid off the floor, its exit (<see cref="Exit"/>).</param>
    public static bool Lands(Vector2 edge, Vector2 dir, float height, float width, Func<float, float, float> ground, FloorEdge onward = FloorEdge.None) =>
        Exit(FloorDrops(edge, dir, height, width, ground), onward: onward) != FloorEdge.None;

    /// <summary>Whether a walker steps off an edge onto the ground by the one rule (<see cref="EdgeLands"/>): BOTH its corners
    /// within a step under it and no more than <paramref name="over"/> over it (a step; a rise at a stair's low edge, whose slope
    /// meets the ground). The corners are <see cref="Drops"/>' from <paramref name="edge"/>: off a stair's edge, where a floor
    /// off it would stand, a floor's size out; for a floor's own edge, the caller passes the edge a floor's size back, so they
    /// are its own two corners. A floor's edge is read at its corners, never at its middle: by the mean of the two, a floor
    /// with its far corners 0.41 m under and 0.61 m over the ground read level; by either corner, a cave's approach's top floor
    /// 0.16 m over the ground at one far corner and 0.6-0.8 m round the rest of it was stepped off at that corner's tip.
    /// <paramref name="off"/>: the ground less the walking height at the corner furthest from the band.</summary>
    public static bool StepsOff(Vector2 edge, Vector2 outward, float height, float length, float over, Func<float, float, float> ground, out float off)
    {
        var (l, r) = Drops(edge, outward, height, length, ground);
        off = float.NaN;
        float worst = -1f;
        foreach (float d in new[] { l, r })
        {
            if (float.IsNaN(d)) continue;
            float miss = InBand(d, over) ? 0f : Mathf.Min(Mathf.Abs(d - StairLayout.StepUp), Mathf.Abs(d + over));
            if (miss > worst) { worst = miss; off = -d; }
        }
        return EdgeIn(l, r, over);
    }

    /// <summary>Whether a stair's low edge at <paramref name="edge"/> (walked off on <paramref name="dir"/>) meets the ground:
    /// its middle, the mean of its two corners' drops (<see cref="Drops"/> from <paramref name="edge"/>, one corner where the
    /// other is not read), within a step under it and up to a rise over it (its slope meets it: a foot is sunk a rise on
    /// purpose). A stair down's low end, as its finish is accepted (StairLayout.TerminalExit.StepsDownOnto) and as the end
    /// reader reads one laid past a top's last stair, one reading for both. <paramref name="off"/>: the ground less the walking
    /// height at the middle.</summary>
    public static bool MeetsLowEdge(Vector2 edge, Vector2 dir, float height, float width, Func<float, float, float> ground) =>
        StairEdgeMeets(edge, dir, height, width, StairLayout.PieceRise, ground, out _);

    /// <summary>A stair's edge read by its middle (<see cref="MeetsLowEdge"/>'s reading), within a step under it and up to
    /// <paramref name="over"/> over it.</summary>
    private static bool StairEdgeMeets(Vector2 edge, Vector2 dir, float height, float width, float over, Func<float, float, float> ground, out float off)
    {
        var (l, r) = Drops(edge, dir, height, width, ground);
        float d = float.IsNaN(l) ? r : float.IsNaN(r) ? l : (l + r) / 2f;
        off = -d;
        return !float.IsNaN(d) && InBand(d, over);
    }

    private static bool IsWalked(StructurePieceKind k) => k is StructurePieceKind.StairStep or StructurePieceKind.StairFloor or StructurePieceKind.StairFiller;

    /// <summary>Whether a walk at <paramref name="height"/> carries on onto a walked piece at <paramref name="q"/>: the one
    /// test of it, for the walk graph (<see cref="Foot.Meets"/>) and for the railing's open edges. The piece is a footprint
    /// (<paramref name="back"/> edge's middle, <paramref name="dir"/>, <paramref name="right"/>, <paramref name="depth"/> along,
    /// <paramref name="across"/>), <paramref name="margin"/> off it at most, and its walking SURFACE at <paramref name="q"/> (a
    /// stair's rises over its run from <paramref name="surfaceAtBack"/>) stands within <see cref="StairLayout.TopFloorBand"/>
    /// of <paramref name="height"/>. The one owner of is this edge walked across (rails, stair ends) (docs/OWNERS.md).</summary>
    internal static bool Walks(Vector2 back, Vector2 dir, Vector2 right, float depth, float across, float surfaceAtBack, bool stair,
        Vector2 q, float height, float margin)
    {
        float along = Vector2.Dot(q - back, dir);
        float surface = surfaceAtBack + (stair ? StairLayout.PieceRise * along / depth : 0f);
        return StairLayout.OnFootprint(back, dir, right, depth, across, q, margin) && Mathf.Abs(surface - height) <= StairLayout.TopFloorBand;
    }

    /// <summary>A walked piece's footprint and its walking height along it.</summary>
    private sealed class Foot
    {
        public readonly StairPiece Piece;
        public readonly Vector2 Back, Dir, Right;
        public readonly float Across, Depth, Height;
        public readonly bool Stair, Floor;
        public Foot(StairPiece piece)
        {
            Piece = piece;
            var (back, heading, across, depth, height) = StairLayout.Footprint(piece);
            Back = back; Dir = StairLayout.Forward(heading); Right = StairLayout.Right(heading);
            Across = across; Depth = depth; Height = height; Stair = piece.Kind == StairPieceKind.Stair; Floor = piece.Kind == StairPieceKind.Landing;
        }
        public Vector2 Centre => new(Piece.Position.x, Piece.Position.z);
        public Vector2 At(float along, float side) => Back + Dir * along + Right * side;
        public float Walk(float along) => Height + (Stair ? StairLayout.PieceRise * along / Depth : 0f);
        public (float along, float side) Local(Vector2 q) { Vector2 d = q - Back; return (Vector2.Dot(d, Dir), Vector2.Dot(d, Right)); }
        public bool Covers(Vector2 q, float height) => Walks(Back, Dir, Right, Depth, Across, Height, Stair, q, height, 0f);
        /// <summary>Three points along an edge (0 front, 1 back, 2 left, 3 right), its way out, the walking heights there and
        /// its length.</summary>
        public (Vector2[] at, Vector2 outward, float[] heights, float length) Edge(int e)
        {
            float[] t = { -0.8f, 0f, 0.8f };
            if (e <= 1)
            {
                float along = e == 0 ? Depth : 0f;
                Foot me = this;
                return (t.Select(k => me.At(along, k * me.Across / 2f)).ToArray(), e == 0 ? Dir : -Dir, new[] { Walk(along), Walk(along), Walk(along) }, Across);
            }
            float sd = e == 3 ? 1f : -1f;
            float[] al = { 0.1f * Depth, 0.5f * Depth, 0.9f * Depth };
            Foot f = this;
            return (al.Select(a => f.At(a, sd * f.Across / 2f)).ToArray(), Right * sd, al.Select(a => f.Walk(a)).ToArray(), Depth);
        }
        /// <summary>Whether a walker on this piece steps across one of its edges onto <paramref name="other"/>.</summary>
        public bool Meets(Foot other)
        {
            for (int e = 0; e < 4; e++)
            {
                var (at, o, hs, _) = Edge(e);
                for (int i = 0; i < at.Length; i++)
                    if (other.Covers(at[i] + o * StairLayout.EndReach, hs[i])) return true;
            }
            return false;
        }
    }

    /// <summary>How many steps along the walk each walked piece lies from <paramref name="start"/> (its neighbours, the pieces
    /// its edges step onto, <see cref="Foot.Meets"/>); -1 where the walk does not reach it.</summary>
    private static int[] Hops(List<Foot> walked, List<int>[] next, int start)
    {
        var hops = Enumerable.Repeat(-1, walked.Count).ToArray();
        var queue = new Queue<int>();
        hops[start] = 0; queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            foreach (int j in next[i])
                if (hops[j] < 0) { hops[j] = hops[i] + 1; queue.Enqueue(j); }
        }
        return hops;
    }

    /// <summary>
    /// Both ends of <paramref name="run"/> (From's, then To's) read on <paramref name="ground"/>; <paramref name="others"/>, the
    /// walked pieces of the staircases meeting it end to end (StairStructures.MetAtEnds).
    /// </summary>
    public static List<End> Read(StairRun run, Func<float, float, float> ground, IReadOnlyList<StructurePiece>? others = null)
    {
        var ends = new List<End>();
        var walked = run.Pieces.Where(p => IsWalked(p.Kind)).Select(p => new Foot(StairRun.PieceOf(p))).ToList();
        if (walked.Count == 0) return ends;
        var beside = (others ?? Array.Empty<StructurePiece>()).Where(p => IsWalked(p.Kind)).Select(p => new Foot(StairRun.PieceOf(p))).ToList();
        StairEnd[] meets = run.Ends;
        // The walk: each piece's neighbours, and each end's first piece (the one nearest where the end was recorded).
        var next = new List<int>[walked.Count];
        for (int i = 0; i < walked.Count; i++)
        {
            next[i] = new List<int>();
            for (int j = 0; j < walked.Count; j++)
                if (i != j && Vector2.Distance(walked[i].Centre, walked[j].Centre) <= 2f * StairLayout.PieceRun
                    && (walked[i].Meets(walked[j]) || walked[j].Meets(walked[i]))) next[i].Add(j);
        }
        int[] first = meets.Select(m => Enumerable.Range(0, walked.Count).OrderBy(i => Vector2.Distance(walked[i].Centre, m.Meet)).First()).ToArray();
        int[][] hops = first.Select(s => Hops(walked, next, s)).ToArray();
        // From each end inward: the pieces furthest along the walk from the other end first (a crest's two stairs or a stair
        // down laid past the point the end was recorded at come before the stair they are laid off); where the walk does not
        // join the two ends, by distance from where the end was recorded, as laid.
        bool joined = hops[0][first[1]] >= 0;
        List<Foot> Order(int n) => joined
            ? Enumerable.Range(0, walked.Count).Where(i => hops[1 - n][i] >= 0)
                .OrderByDescending(i => hops[1 - n][i]).ThenBy(i => Vector2.Distance(walked[i].Centre, meets[n].Meet)).Select(i => walked[i]).ToList()
            : walked.OrderBy(f => Vector2.Distance(f.Centre, meets[n].Meet)).ToList();
        var order = new[] { Order(0), Order(1) };
        float Mean(List<Foot> seq) => seq.Take(3).Average(f => f.Piece.Position.y);
        for (int n = 0; n < 2; n++)
        {
            List<Foot> seq = order[n];
            // The higher end is the top; level (a run of a stair or two), To's.
            float mine = Mean(seq), other = Mean(order[1 - n]);
            var end = new End { Top = mine > other || mine == other && n == 1 };
            var buried = new List<Foot>();
            if (!end.Top)
                // A foot's stairs sunk under the ground on purpose: their high edge at or under it (a road's surface where its
                // road comes onto it, WithRoads), within a step, and their low edge under it by more than a step (their slope
                // in the ground). A walker steps off onto the ground over them. A stair on ground cut to just under its walk
                // (StairStructures.CutFloor) is walked, not sunk: read as sunk, the foot at -3088.6,920.6, level with its
                // road, was read off the stair above it, a rise over the road. Sunk is under the ground across its low edge,
                // its middle and both sides (where its edges are read, Foot.Edge): a stair cut into a bank on one side, the
                // ground falling away on the other, is walked off that side, not sunk (read at its middle alone, a foot's
                // stair down cut into a side slope was read sunk and the foot read off the stair above it, a gap).
                while (seq.Count > 1 && seq[0].Stair)
                {
                    Vector2 hi = seq[0].At(seq[0].Depth, 0f);
                    float g = ground(hi.x, hi.y);
                    if (float.IsNaN(g) || seq[0].Walk(seq[0].Depth) > g + StairLayout.StepUp) break;
                    bool sunk = true;
                    foreach (float side in new[] { 0f, -0.8f, 0.8f })
                    {
                        Vector2 lo = seq[0].At(0f, side * seq[0].Across / 2f);
                        float gl = ground(lo.x, lo.y);
                        if (!float.IsNaN(gl) && gl - seq[0].Walk(0f) <= StairLayout.StepUp) { sunk = false; break; }
                    }
                    if (!sunk) break;
                    buried.Add(seq[0]);
                    seq = seq.Skip(1).ToList();
                }
            // The last stair: from the end inward, the first stair whose exit edge (its high edge at a top, its low edge at a
            // foot) faces out of the staircase; every floor and stair before it is laid past it.
            int last = -1;
            for (int k = 0; k < seq.Count && last < 0; k++)
            {
                Foot p = seq[k];
                if (!p.Stair) { if (p.Floor) end.FloorsPast++; continue; }
                bool outFront = k + 1 >= seq.Count || Vector2.Dot(p.Centre - seq[k + 1].Centre, p.Dir) > 0f;
                if (end.Top ? outFront : !outFront) last = k;
                else end.StairsPast++;
            }
            if (last >= 0)
            {
                Foot s = seq[last];
                float along = end.Top ? s.Depth : 0f;
                Vector2 edge = s.At(along, 0f), outward = end.Top ? s.Dir : -s.Dir;
                end.Edge = new Vector3(edge.x, s.Walk(along), edge.y);
                var drops = FloorDrops(edge, outward, s.Walk(along), Mathf.Max(run.Width, s.Across), ground);
                (end.Left, end.Right) = (drops.farLeft, drops.farRight);
                end.Rule = Choose(drops, out end.SidesDisagree);
            }
            // The walk off: the end's last walked piece and the two before it where floors or stairs are laid past the stair,
            // across each free edge (not stepped onto another of its walked pieces) by the one reader.
            float best = float.PositiveInfinity, walkOff = float.NaN;
            bool steps = false, wet = false;
            var tried = new List<Foot> { seq[0] };
            if (end.FloorsPast + end.StairsPast > 1)
                for (int i = 1; i <= 2 && i < (last < 0 ? seq.Count : last); i++) tried.Add(seq[i]);
            foreach (Foot p in tried)
            {
                // Laid past the last stair (a floor, a stair down, a turn's filler), it is the finish the rule laid where a
                // floor off that stair would stand: read at its own corners there. The last stair itself off its edge.
                bool laidPast = last >= 0 && !ReferenceEquals(p, seq[last]);
                foreach (int e in p.Stair ? new[] { 0, 1 } : new[] { 0, 1, 2, 3 })
                {
                    var (at, o, hs, length) = p.Edge(e);
                    if (at.Select((q, i) => (q, i)).Any(x => beside.Any(b => b.Covers(x.q + o * StairLayout.EndReach, hs[x.i]))))
                    { end.Joined = true; steps = true; walkOff = 0f; break; }
                    // Met by its road's surface level with it, within a step, just past the edge: the walker steps onto the
                    // road, as onto the next staircase (a staircase whose road climbs steeply off its end, its start pad not
                    // laid in water, read a floor's size out on the climbing road).
                    int onto = -1;
                    for (int c = 0; c < at.Length && onto < 0; c++) if (OnRoadLevel(at[c] + o * StairLayout.EndReach, hs[c])) onto = c;
                    // A foot's landing stands above the water (the owner's, 11 Nov: a staircase landing above water, even if it means
                    // rejecting a route): a foot onto a road under the built-dry floor there, or onto ground under it, has no way
                    // off (RoadWater.BuiltDry, the owner's line of 12 Nov: the water surface left a landing at 30.35 m standing in
                    // the shallows).
                    if (onto >= 0 && !end.Top && RoadAt(at[onto] + o * StairLayout.EndReach, out float roadThere) && !RoadWater.BuiltDry(roadThere))
                    { wet = true; continue; }
                    if (onto >= 0)
                    {
                        end.Joined = true; end.OntoRoad = true; steps = true; walkOff = 0f;
                        break;
                    }
                    Vector2 middle = at[1] + o * StairLayout.EndReach;
                    if (walked.Any(w => !ReferenceEquals(w, p) && !buried.Contains(w) && w.Covers(middle, hs[1]))) continue;
                    float over = p.Stair && e == 1 ? StairLayout.PieceRise : StairLayout.StepUp;
                    // Over a foot's stairs sunk in the ground, the walker steps off its low edge onto the ground over them
                    // there, at the edge (their high edge), as off what is laid past: read a floor's size out, the ground at
                    // the sunk stairs' own low end (a road falling away, the sunk slope) was read instead.
                    bool overSunk = buried.Count > 0 && p.Stair && e == 1 && last >= 0 && ReferenceEquals(p, seq[last]);
                    // A floor (laid past the last stair, or a walkway's) by the one rule at its own two corners (EdgeLands); a stair
                    // down laid past by its low edge's middle, as its finish was accepted (MeetsLowEdge); the last stair itself, bare,
                    // where a floor off it would land, the one rule (StairStepsOff; over sunk stairs, laid off their high edge).
                    Vector2 back = at[1] - o * StairLayout.PieceRun;
                    float off;
                    bool bare = p.Stair && !laidPast, wetExit = false;
                    // Off a bare stair, its exit's own ground is the one asked to stand dry (a foot's).
                    bool stepped = !p.Stair ? StepsOff(back, o, hs[1], length, StairLayout.StepUp, ground, out off)
                        : laidPast ? StairEdgeMeets(back, o, hs[1], length, over, ground, out off)
                        : StairStepsOff(overSunk ? back : at[1], o, hs[1], length, over, ground, out off, end.Top ? null : RoadWater.BuiltDry, out wetExit);
                    if (wetExit) wet = true;
                    if (stepped && !bare && !end.Top && !RoadWater.BuiltDry(hs[1] + off)) { wet = true; stepped = false; }
                    if (stepped) { steps = true; best = 0f; walkOff = off; }
                    else if (!float.IsNaN(off) && !steps)
                    {
                        float score = Mathf.Min(Mathf.Abs(off + StairLayout.StepUp), Mathf.Abs(off - over));
                        if (score < best) { best = score; walkOff = off; }
                    }
                }
                if (end.Joined) break;
            }
            end.WalkOff = walkOff;
            // A foot onto its road is walked off onto a road its planning kept within the grade cap into it (a road ends on a
            // level landing where a staircase starts, RoadSpatialGrid.StairStarts): asked of the road that meets this end
            // (OntoItsRoad); a foot onto a road falling away from it at a pitch's grade, steeper live, was read "onto its road".
            if (end.Joined && end.OntoRoad && !end.Top)
            {
                StairRoad? itsRoad = n == 0 ? run.FromRoad : run.ToRoad;
                end.OntoAnotherRoad = itsRoad == null;
                (end.Verdict, end.Why) = OntoItsRoad(itsRoad);
            }
            else if (end.Joined) { end.Verdict = Verdict.Ok; end.Why = end.OntoRoad ? "onto its road" : "joined to the next staircase"; }
            // The ground at its edge not known (a location placed after its roads, its ground not read yet): not verified, never
            // a way off nor a gap read off a stand-in.
            else if (last >= 0 && (float.IsNaN(end.Left) || float.IsNaN(end.Right)))
            { end.Verdict = Verdict.NotVerified; end.Why = "the finished ground at its edge is not known (a location placed after its roads, its ground not read yet)"; }
            else if (end.Top && end.FloorsPast == 0 && end.StairsPast == 0 && last >= 0) { end.Verdict = Verdict.Miss; end.Why = "no finish laid past its last stair"; }
            else if (steps) end.Verdict = Verdict.Ok;
            else if (wet) { end.Verdict = Verdict.Gap; end.Why = "its landing in the water"; }
            else { end.Verdict = Verdict.Gap; end.Why = float.IsNaN(end.WalkOff) ? "no free edge read" : FormattableString.Invariant($"no edge steps off ({end.WalkOff:+0.00;-0.00} m)"); }
            ends.Add(end);
        }
        return ends;
    }

    /// <summary>
    /// A foot stepped off onto its road (<paramref name="road"/>: the road that meets that end, StairRun.FromRoad or ToRoad):
    /// a way off where the road ends on the foot's level landing, the rule its planning keeps it to (RoadPitches.AcceptLanding;
    /// RoadPitches.LeavesItsLanding on its own points in order, at RoadPitches.JunctionLandingGrade), read at its heights as the network stores
    /// them now (StairRoad.Stored: a landing's raise, an append's); a gap where it is pitched past it there. The road not known
    /// (none was planned to that end, or its end was not kept), or no longer stored as recorded: not verified, never a way
    /// off. The end audit's one reading of a road's slope into a foot; it decides no slope itself.
    /// </summary>
    internal static (Verdict verdict, string why) OntoItsRoad(StairRoad? road)
    {
        if (road == null) return (Verdict.NotVerified, "onto a road, slope not verified: no road recorded at this end (its road's end there not kept)");
        if (road.Stored() is not RoadSpatialGrid.RoadPoint[] stored) return (Verdict.NotVerified, "onto a road, slope not verified: its road is not stored as recorded");
        var heights = new float[stored.Length];
        for (int i = 0; i < stored.Length; i++) heights[i] = stored[i].h;
        if (RoadPitches.LeavesItsLanding(road.Points, heights, atStart: true, atEnd: false, RoadPitches.JunctionLandingGrade))
            return (Verdict.Gap, FormattableString.Invariant($"onto its road, not on its level landing ({RoadPitches.JunctionLandingGrade:P0}) within {RoadPitches.JunctionLanding:F0} m of it"));
        return (Verdict.Ok, "onto its road");
    }

    /// <summary>One line for an end: what the rule chose, the drops, what is laid and the walk off.</summary>
    public static string Line(End e)
    {
        string at = e.Edge is Vector3 p ? FormattableString.Invariant($"({p.x:F1}, {p.y:F2}, {p.z:F1})") : "(no stair)";
        string rule = e.Rule switch { Case.Floor => "floor", Case.Reverse => "reverse", Case.StepBack => "step back", _ => "short" };
        return FormattableString.Invariant(
            $"[STAIRS] end {(e.Top ? "top" : "foot")} at {at}: drop {e.Left:+0.00;-0.00}/{e.Right:+0.00;-0.00}{(e.SidesDisagree ? " (sides disagree)" : "")} -> {rule}, laid {e.Laid}, walk-off {(float.IsNaN(e.WalkOff) ? "-" : e.WalkOff.ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture))} {(e.Verdict == Verdict.NotVerified ? "NOT VERIFIED" : e.Verdict.ToString().ToUpperInvariant())}{(e.Why != "" ? ": " + e.Why : "")}");
    }

    /// <summary>The ground an audit reads a laid staircase's ends on: the finished ground its pieces stand on (the game's
    /// mesh over <see cref="StructureGround.Vertices"/>), NaN where it is not known (an end read on it is not verified,
    /// <see cref="Read"/>; never the ground its layout was laid out on in its stead), and a road's surface where one is
    /// (<see cref="WithRoads"/>). The same for every staircase (<paramref name="run"/> as the audit asks it).</summary>
    public static Func<float, float, float> GroundOf(StairRun run, WorldGenerator world)
    {
        Func<int, int, float> finished = StructureGround.Vertices(world);
        return WithRoads((x, z) => BuriedPieces.MeshHeight(x, z, finished));
    }

    /// <summary>Whether a road's surface stands at <paramref name="at"/> (within its half width) within a step of
    /// <paramref name="height"/>.</summary>
    private static bool OnRoadLevel(Vector2 at, float height) =>
        RoadAt(at, out float road) && StairLayout.Walkable(road, height);

    /// <summary>The road's surface at <paramref name="at"/>, within its half width: the network's as it stands, and before a
    /// road is stored, inside RoadTerrainModifier.AsStored, as it will stand with it (RoadTerrainModifier.RoadHeightNear).</summary>
    private static bool RoadAt(Vector2 at, out float height) =>
        RoadTerrainModifier.RoadHeightNear(at, RoadNetworkGenerator.RoadWidth, out height, out float distance, out float width)
        && distance <= width / 2f;

    /// <summary><paramref name="ground"/> with the network's roads on it: where a road's surface is within its half width
    /// (<see cref="RoadAt"/>, the road being stored among them inside RoadTerrainModifier.AsStored), a walker stands on it, higher than the ground under a road raised over
    /// water (a staircase in a shallow sea meeting its road's raised end, the road's surface level with its last floor,
    /// the sea floor 0.3 m under it).</summary>
    public static Func<float, float, float> WithRoads(Func<float, float, float> ground) => (x, z) =>
    {
        float g = ground(x, z);
        return RoadAt(new Vector2(x, z), out float road) && !(road <= g) ? road : g;
    };

    /// <summary>Why an end a walker steps off is not finished as the rule chose for it (its case, <see cref="Choose"/>, read on
    /// the ground it is judged on), or null: a floor's case with no floor laid past its last stair or a stair down laid instead
    /// (a crest where the ground is level with it), a reverse's with no stair down, a step back's with no floor. A short end
    /// (ground over it; at a top the stairs climb on, at a foot it is sunk) and an end joined to its road or the next
    /// staircase are finished as they are; a foot's floor case is its bare stair stepped off. Only an end with a way off is
    /// asked (a miss or a gap is its own verdict). The audit counts them beside the verdicts (<see cref="AuditAll"/>): a lost
    /// finish fails the gate, not only a missing way off.</summary>
    public static string? Unfinished(End e)
    {
        if (e.Verdict != Verdict.Ok || e.Joined || e.Edge == null) return null;
        bool floors = e.FloorsPast > 0, down = e.StairsPast > 0;
        string? why = e.Rule switch
        {
            Case.Floor when e.Top && (!floors || down) => "a floor's case",
            Case.Floor when !e.Top && down => "a floor's case",
            Case.Reverse when !down => "a reverse's case (a stair down)",
            Case.StepBack when !floors => "a step back's case (a floor off the stair before)",
            _ => null,
        };
        return why == null ? null : $"{why}, laid {e.Laid}";
    }

    /// <summary>Whether an end is finished, the verdict the end audit gives it: a way off it (not a miss nor a gap) and
    /// finished as its rule chose (<see cref="Unfinished"/>). A staircase's top and foot as they are laid both ask it
    /// (RoadStairRuns.TopFinished, RoadStairRuns.FootFinished).</summary>
    public static bool Finished(End e) => e.Verdict == Verdict.Ok && Unfinished(e) == null;

    /// <summary>Every end of every staircase in the network (StairStructures.Runs), each on <paramref name="groundOf"/>'s ground
    /// for its run (offline and in game: <see cref="GroundOf"/>): one line per end, and the counts (an end not verified is
    /// none of ok, miss or gap); beside them the ends with a way off not finished as the rule chose (<see cref="Unfinished"/>,
    /// their lines marked UNFINISHED), counted in <c>unfinished</c> and among <c>ok</c> too.</summary>
    public static (List<(Verdict verdict, string line)> lines, int ok, int miss, int gap, int unverified, int unfinished) AuditAll(Func<StairRun, Func<float, float, float>> groundOf)
    {
        var lines = new List<(Verdict, string)>();
        int ok = 0, miss = 0, gap = 0, unverified = 0, unfinished = 0;
        foreach (StairRun run in StairStructures.Runs)
            foreach (End e in Read(run, groundOf(run), StairStructures.MetAtEnds(run)))
            {
                string? left = Unfinished(e);
                lines.Add((e.Verdict, Line(e) + (left != null ? " UNFINISHED: " + left : "")));
                if (left != null) unfinished++;
                if (e.Verdict == Verdict.Ok) ok++; else if (e.Verdict == Verdict.Miss) miss++; else if (e.Verdict == Verdict.Gap) gap++; else unverified++;
            }
        return (lines, ok, miss, gap, unverified, unfinished);
    }
}
