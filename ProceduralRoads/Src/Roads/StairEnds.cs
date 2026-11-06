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
/// step is short, and the stairs carry on. Where the two corners read different cases, an end with either corner within a
/// step is a floor, else the gentler of the two.</para>
/// <para>A walker steps off an end's last walked pieces where, across a free edge of one, the same reader finds either corner
/// within a step of its walking height: off the last stair, where a floor off it would stand; off what is laid past it (a
/// floor, a stair down), at its own far corners, where the rule read the ground it was laid for. A stair's low edge meets ground up to a rise over
/// it (its slope meets it: a foot is sunk a rise on purpose). An edge met by another staircase's walked piece, or by its
/// road's surface level with it within a step, is joined. A top whose last walked piece is its
/// climbing stair, joined to nothing, has no finish laid: a miss. The ends are found along the walk (each walked piece's
/// neighbours, the pieces its edges step onto), not by distance: a switchback's upper flight passes back by its foot.</para>
/// </summary>
public static class StairEnds
{
    /// <summary>What the end rule chooses from the drop off the last stair.</summary>
    public enum Case { Floor, Reverse, StepBack, Short }

    /// <summary>Whether a walker steps off the end: <see cref="Miss"/>, a top with nothing laid past its last stair.</summary>
    public enum Verdict { Ok, Miss, Gap }

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
        /// <summary>At a foot onto its road: the steepest grade its road climbs or falls at over a level landing's length
        /// from where the foot steps onto it (<see cref="RoadGradeOff"/>); NaN where not read.</summary>
        public float OntoGrade = float.NaN;
        public Verdict Verdict;
        public string Why = "";

        public string Laid => FloorsPast == 0 && StairsPast == 0 ? "bare stair"
            : (FloorsPast > 0 ? $"{FloorsPast} floor{(FloorsPast > 1 ? "s" : "")}" : "") + (FloorsPast > 0 && StairsPast > 0 ? " + " : "")
              + (StairsPast > 0 ? $"{StairsPast} stair{(StairsPast > 1 ? "s" : "")} down" : "");
    }

    /// <summary>The rule's case for one drop (walking height less the ground).</summary>
    public static Case CaseOf(float drop) =>
        drop < -StairLayout.StepUp ? Case.Short
        : drop <= StairLayout.StepUp ? Case.Floor
        : drop <= StairRailing.LedgeDrop ? Case.Reverse
        : Case.StepBack;

    /// <summary>The two far corners of the floor an exit edge at <paramref name="edge"/> (walked out on <paramref name="dir"/>,
    /// <paramref name="width"/> wide) would take: a floor's size out, at either side of its width (a piece's at least).
    /// The one place the ground off an exit edge is read.</summary>
    public static (Vector2 left, Vector2 right) Corners(Vector2 edge, Vector2 dir, float width)
    {
        Vector2 right = new Vector2(dir.y, -dir.x), far = edge + dir * StairLayout.PieceRun;
        float half = Mathf.Max(StairLayout.PieceRun, width) / 2f;
        return (far - right * half, far + right * half);
    }

    /// <summary>The drops at the two far corners (<see cref="Corners"/>) off an exit edge at <paramref name="height"/>: left, right.</summary>
    public static (float left, float right) Drops(Vector2 edge, Vector2 dir, float height, float width, Func<float, float, float> ground)
    {
        var (l, r) = Corners(edge, dir, width);
        return (height - ground(l.x, l.y), height - ground(r.x, r.y));
    }

    /// <summary>The case for two corners' drops (<see cref="Drops"/>): the same case, else a floor where either is within
    /// a step, else the gentler; <paramref name="disagree"/> where they differ. NaN corners (no ground read) are left out.</summary>
    public static Case Choose(float left, float right, out bool disagree)
    {
        disagree = false;
        if (float.IsNaN(left)) return CaseOf(right);
        if (float.IsNaN(right)) return CaseOf(left);
        Case a = CaseOf(left), b = CaseOf(right);
        if (a == b) return a;
        disagree = true;
        if (Mathf.Abs(left) <= StairLayout.StepUp || Mathf.Abs(right) <= StairLayout.StepUp) return Case.Floor;
        return CaseOf(Gentler(left, right));
    }

    /// <summary>Of two corners' drops (<see cref="Drops"/>) the gentler: the one nearer level (the first where they are as near),
    /// the other where one is NaN (no ground read), NaN where both are. The one combine of two corners: the rule's case where they
    /// differ (<see cref="Choose"/>), and the ground ahead of a top that gates its finish (<see cref="OverAhead"/>).</summary>
    public static float Gentler(float left, float right) =>
        float.IsNaN(left) ? right : float.IsNaN(right) ? left : Mathf.Abs(left) <= Mathf.Abs(right) ? left : right;

    /// <summary>How far the ground ahead of an exit edge at <paramref name="edge"/> stands over its walking height
    /// <paramref name="height"/>: read at the two far corners of the floor it would take (<see cref="Corners"/>, a floor out),
    /// the gentler of the two (<see cref="Gentler"/>). The one reading of the ground ahead of a top, for the search's arrival
    /// and a road top's finish; each keeps its own band.</summary>
    public static float OverAhead(Vector2 edge, Vector2 dir, float height, float width, Func<float, float, float> ground)
    {
        var (l, r) = Drops(edge, dir, height, width, ground);
        return -Gentler(l, r);
    }

    /// <summary>The rule's case off an exit edge at <paramref name="edge"/> (<see cref="Drops"/>, <see cref="Choose"/>).</summary>
    public static Case At(Vector2 edge, Vector2 dir, float height, float width, Func<float, float, float> ground)
    {
        var (l, r) = Drops(edge, dir, height, width, ground);
        return Choose(l, r, out _);
    }

    /// <summary>Whether a floor laid off an exit edge lands (<see cref="At"/>: the rule's floor). For a floor already laid, its
    /// back edge: its own far corners.</summary>
    public static bool Lands(Vector2 edge, Vector2 dir, float height, float width, Func<float, float, float> ground) =>
        At(edge, dir, height, width, ground) == Case.Floor;

    /// <summary>Whether a walker steps off an edge onto the ground by the one reader (<see cref="Drops"/>): either corner no
    /// more than a step under it and no more than <paramref name="over"/> over it (a step; a rise at a stair's low edge, whose
    /// slope meets the ground: <see cref="MeetsLowEdge"/>). <paramref name="off"/>: the corner's ground less the walking height nearest that band.</summary>
    public static bool StepsOff(Vector2 edge, Vector2 outward, float height, float length, float over, Func<float, float, float> ground, out float off)
    {
        var (l, r) = Drops(edge, outward, height, length, ground);
        off = float.NaN;
        float best = float.PositiveInfinity;
        foreach (float d in new[] { l, r })
        {
            if (float.IsNaN(d)) continue;
            float score = d <= StairLayout.StepUp && d >= -over ? 0f : Mathf.Min(Mathf.Abs(d - StairLayout.StepUp), Mathf.Abs(d + over));
            if (score < best) { best = score; off = -d; }
        }
        return best == 0f;
    }

    /// <summary>Whether a stair's low edge at <paramref name="edge"/> (walked off on <paramref name="dir"/>) meets the ground
    /// (<see cref="StepsOff"/>: up to a rise over it, its slope meeting it, or a step under it): a stair down's low end.</summary>
    public static bool MeetsLowEdge(Vector2 edge, Vector2 dir, float height, float width, Func<float, float, float> ground) =>
        StepsOff(edge, dir, height, width, StairLayout.PieceRise, ground, out _);

    private static bool IsWalked(StructurePieceKind k) => k is StructurePieceKind.StairStep or StructurePieceKind.StairFloor or StructurePieceKind.StairFiller;

    /// <summary>Whether a walk at <paramref name="height"/> carries on onto a walked piece at <paramref name="q"/>: the one
    /// test of it, for the walk graph (<see cref="Foot.Meets"/>) and for the railing's open edges. The piece is a footprint
    /// (<paramref name="back"/> edge's middle, <paramref name="dir"/>, <paramref name="right"/>, <paramref name="depth"/> along,
    /// <paramref name="across"/>), <paramref name="margin"/> off it at most, and its walking SURFACE at <paramref name="q"/> (a
    /// stair's rises over its run from <paramref name="surfaceAtBack"/>) stands within <see cref="StairLayout.TopFloorBand"/>
    /// of <paramref name="height"/>.</summary>
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
                // road, was read off the stair above it, a rise over the road.
                while (seq.Count > 1 && seq[0].Stair)
                {
                    Vector2 hi = seq[0].At(seq[0].Depth, 0f), lo = seq[0].At(0f, 0f);
                    float g = ground(hi.x, hi.y), gl = ground(lo.x, lo.y);
                    if (float.IsNaN(g) || seq[0].Walk(seq[0].Depth) > g + StairLayout.StepUp) break;
                    if (!float.IsNaN(gl) && gl - seq[0].Walk(0f) <= StairLayout.StepUp) break;
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
                (end.Left, end.Right) = Drops(edge, outward, s.Walk(along), Mathf.Max(run.Width, s.Across), ground);
                end.Rule = Choose(end.Left, end.Right, out end.SidesDisagree);
            }
            // The walk off: the end's last walked piece and the two before it where floors or stairs are laid past the stair,
            // across each free edge (not stepped onto another of its walked pieces) by the one reader.
            float best = float.PositiveInfinity, walkOff = float.NaN;
            bool steps = false;
            var tried = new List<Foot> { seq[0] };
            if (end.FloorsPast + end.StairsPast > 1)
                for (int i = 1; i <= 2 && i < (last < 0 ? seq.Count : last); i++) tried.Add(seq[i]);
            foreach (Foot p in tried)
            {
                // Laid past the last stair (a floor, a stair down, a turn's filler), it is the finish the rule laid where a
                // floor off that stair would stand: read at its own corners there. The last stair itself, and a run of
                // floors with no stair (a walkway), off its edge.
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
                    if (onto >= 0)
                    {
                        end.Joined = true; end.OntoRoad = true; steps = true; walkOff = 0f;
                        if (!end.Top) end.OntoGrade = RoadGradeOff(at[onto] + o * StairLayout.EndReach);
                        break;
                    }
                    Vector2 middle = at[1] + o * StairLayout.EndReach;
                    if (walked.Any(w => !ReferenceEquals(w, p) && !buried.Contains(w) && w.Covers(middle, hs[1]))) continue;
                    float over = p.Stair && e == 1 ? StairLayout.PieceRise : StairLayout.StepUp;
                    // Over a foot's stairs sunk in the ground, the walker steps off its low edge onto the ground over them
                    // there, at the edge (their high edge), as off what is laid past: read a floor's size out, the ground at
                    // the sunk stairs' own low end (a road falling away, the sunk slope) was read instead.
                    bool overSunk = buried.Count > 0 && p.Stair && e == 1 && last >= 0 && ReferenceEquals(p, seq[last]);
                    Vector2 from = laidPast || overSunk ? at[1] - o * StairLayout.PieceRun : at[1];
                    if (StepsOff(from, o, hs[1], length, over, ground, out float off)) { steps = true; best = 0f; walkOff = off; }
                    else if (!float.IsNaN(off) && !steps)
                    {
                        float score = Mathf.Min(Mathf.Abs(off + StairLayout.StepUp), Mathf.Abs(off - over));
                        if (score < best) { best = score; walkOff = off; }
                    }
                }
                if (end.Joined) break;
            }
            end.WalkOff = walkOff;
            // A foot onto its road is walked off onto a road within its grade cap past its start pad (a road ends on a level
            // landing where a staircase starts, RoadSpatialGrid.StairStarts): a foot onto a road falling away from it at a
            // pitch's grade, steeper live, read "onto its road".
            if (end.Joined && end.OntoRoad && end.OntoGrade > RoadGrade.Configured && RoadGrade.Capped(RoadGrade.Configured))
            {
                end.Verdict = Verdict.Gap;
                end.Why = FormattableString.Invariant($"onto its road at {end.OntoGrade:P0} over {RoadPitches.JunctionLanding:F0} m, past its {RoadGrade.Configured:P0} cap");
            }
            else if (end.Joined) { end.Verdict = Verdict.Ok; end.Why = end.OntoRoad ? "onto its road" : "joined to the next staircase"; }
            else if (end.Top && end.FloorsPast == 0 && end.StairsPast == 0 && last >= 0) { end.Verdict = Verdict.Miss; end.Why = "no finish laid past its last stair"; }
            else if (steps) end.Verdict = Verdict.Ok;
            else { end.Verdict = Verdict.Gap; end.Why = float.IsNaN(end.WalkOff) ? "no free edge read" : FormattableString.Invariant($"no edge steps off ({end.WalkOff:+0.00;-0.00} m)"); }
            ends.Add(end);
        }
        return ends;
    }

    /// <summary>One line for an end: what the rule chose, the drops, what is laid and the walk off.</summary>
    public static string Line(End e)
    {
        string at = e.Edge is Vector3 p ? FormattableString.Invariant($"({p.x:F1}, {p.y:F2}, {p.z:F1})") : "(no stair)";
        string rule = e.Rule switch { Case.Floor => "floor", Case.Reverse => "reverse", Case.StepBack => "step back", _ => "short" };
        return FormattableString.Invariant(
            $"[STAIRS] end {(e.Top ? "top" : "foot")} at {at}: drop {e.Left:+0.00;-0.00}/{e.Right:+0.00;-0.00}{(e.SidesDisagree ? " (sides disagree)" : "")} -> {rule}, laid {e.Laid}, walk-off {(float.IsNaN(e.WalkOff) ? "-" : e.WalkOff.ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture))} {e.Verdict.ToString().ToUpperInvariant()}{(e.Why != "" ? ": " + e.Why : "")}");
    }

    /// <summary>The ground an audit reads a laid staircase's ends on: the finished ground its walls stand down to
    /// (RoadStairRuns.FinishedGround), where unknown the ground it was laid out on (RoadStairRuns.PlanningGround), and a road's
    /// surface where one is (<see cref="WithRoads"/>).</summary>
    public static Func<float, float, float> GroundOf(StairRun run, WorldGenerator world)
    {
        Func<int, int, float> finished = RoadStairRuns.FinishedGround(run, world);
        Func<float, float, float> planning = RoadStairRuns.PlanningGround(run, world);
        float Vertex(int x, int z) { float h = finished(x, z); return float.IsNaN(h) ? planning(x, z) : h; }
        return WithRoads((x, z) => BuriedPieces.MeshHeight(x, z, Vertex));
    }

    /// <summary>Whether a road's surface stands at <paramref name="at"/> (within its half width) within a step of
    /// <paramref name="height"/>.</summary>
    private static bool OnRoadLevel(Vector2 at, float height) =>
        RoadAt(at, out float road) && Mathf.Abs(road - height) <= StairLayout.StepUp;

    /// <summary>
    /// The steepest grade a walker stepping onto a road's surface at <paramref name="at"/> climbs or falls at along it over a
    /// level landing's length (RoadPitches.JunctionLanding, what a road's end where a staircase starts is): walked from there
    /// on each of the hammer's sixteen headings a metre at a time, on the road's surface (within its half width) all the way,
    /// at its level (a step of more than RoadStairRuns.JunctionHeight is onto another road, as a switchback's other leg
    /// beside it). 0 where no walk stays on a road that far.
    /// </summary>
    public static float RoadGradeOff(Vector2 at)
    {
        float length = RoadPitches.JunctionLanding, steepest = 0f;
        if (length <= 0f || !RoadAt(at, out float from)) return 0f;
        for (int k = 0; k < 16; k++)
        {
            Vector2 dir = StairLayout.Forward(k * StairLayout.HeadingStep);
            float height = from;
            bool onRoad = true;
            int steps = Mathf.CeilToInt(length);
            for (int i = 1; i <= steps && onRoad; i++)
            {
                onRoad = RoadAt(at + dir * Mathf.Min(i, length), out float next) && Mathf.Abs(next - height) <= RoadStairRuns.JunctionHeight;
                height = next;
            }
            if (onRoad) steepest = Mathf.Max(steepest, Mathf.Abs(height - from) / length);
        }
        return steepest;
    }

    /// <summary>The road's surface at <paramref name="at"/>, within its half width.</summary>
    private static bool RoadAt(Vector2 at, out float height) =>
        RoadSpatialGrid.TryGetRoadHeightWithin(at, RoadNetworkGenerator.RoadWidth, out height, out float distance, out float width)
        && distance <= width / 2f;

    /// <summary><paramref name="ground"/> with the network's roads on it: where a road's surface is within its half width
    /// (RoadSpatialGrid.TryGetRoadHeightWithin), a walker stands on it, higher than the ground under a road raised over
    /// water (a staircase in a shallow sea meeting its road's raised end, the road's surface level with its last floor,
    /// the sea floor 0.3 m under it).</summary>
    public static Func<float, float, float> WithRoads(Func<float, float, float> ground) => (x, z) =>
    {
        float g = ground(x, z);
        return RoadAt(new Vector2(x, z), out float road) && !(road <= g) ? road : g;
    };

    /// <summary>Every end of every staircase in the network (StairStructures.Runs), each on <paramref name="groundOf"/>'s ground
    /// for its run (offline and in game: <see cref="GroundOf"/>): one line per end, and the counts.</summary>
    public static (List<(Verdict verdict, string line)> lines, int ok, int miss, int gap) AuditAll(Func<StairRun, Func<float, float, float>> groundOf)
    {
        var lines = new List<(Verdict, string)>();
        int ok = 0, miss = 0, gap = 0;
        foreach (StairRun run in StairStructures.Runs)
            foreach (End e in Read(run, groundOf(run), StairStructures.MetAtEnds(run)))
            {
                lines.Add((e.Verdict, Line(e)));
                if (e.Verdict == Verdict.Ok) ok++; else if (e.Verdict == Verdict.Miss) miss++; else gap++;
            }
        return (lines, ok, miss, gap);
    }
}
