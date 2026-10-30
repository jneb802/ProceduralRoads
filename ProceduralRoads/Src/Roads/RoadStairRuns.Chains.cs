using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Two staircases of one road that meet over a ridge or through a valley (one owner): over a ridge top to top on a 2x2 at the
/// crest, the second searched down from the first's top edge, never a stub of road between; through a valley at their feet on a
/// shared floor, or higher up, the second laid again across the valley from the first's flight.
/// </summary>
public static partial class RoadStairRuns
{

    /// <summary>Valley meets crossed higher up by a straight span of floors (<see cref="SpanTheValley"/>).</summary>
    public static int ValleysSpanned;

    /// <summary>Longest span of floors across a valley (a hand-built span at 4393,6442: 7.3 m, four floors).</summary>
    public const float ValleySpanReach = 12f;

    /// <summary>Most a span may turn off either flight's line: past it the walk doubles back.</summary>
    public const float ValleySpanTurn = 45f;

    /// <summary>
    /// Two staircases met at a valley's bottom (<paramref name="below"/> down into it, <paramref name="up"/> out of it, on a
    /// shared floor at <paramref name="bottom"/>): where, a whole number of rises higher, a straight run of floors from the
    /// first's stair at that height to the second's crosses the valley within <see cref="ValleySpanReach"/>, turning off
    /// neither flight's line by more than <see cref="ValleySpanTurn"/> and buried nowhere, the first is cut back to that height
    /// and the second laid again by <paramref name="relay"/> from the first's row there, its floors across the valley and its
    /// stairs out of it one search, snapped to each other and to the first as any staircase's pieces are (at 4393,6442: take
    /// off the bottom two stairs of each and the shared floor, and lay floors at 34.5 from one to the other: smoother, with no
    /// 180 degree turn). Of the heights that fit, the one turning least, then the lowest. Returns whether it did:
    /// <paramref name="spanned"/> the second laid again, <paramref name="from"/> its start on the first.
    /// </summary>
    /// <para>A valley within the walkway cap (<paramref name="level"/>: <see cref="StairChain.CrossesLevel"/>) is crossed
    /// level: a walkway at the highest height that fits, up to the lower flight's top edge, standing nowhere higher over the
    /// ground than the cap, however far across, so no stair is left down into it (the chain's one rule, as an approach
    /// crosses a dip).</para>
    internal static bool SpanTheValley(StairPlan below, StairPlan up, StairKit kit, Func<float, float, float> ground, float bottom, float width,
        Func<StairContinuation, StairPlan> relay, Func<StairPlan, Vector2, bool> usable, out StairPlan spanned, out Vector2 from, bool level = false)
    {
        from = default;
        spanned = up;
        // Each flight's stair rows by base height: centre (across its lanes) and the way down it; crossed level, its top
        // stair's upper edge too, a rise over its base, where a floor at the top meets it.
        Dictionary<int, (Vector2 at, float down)> Rows(StairPlan plan, float bottom)
        {
            var rows = new Dictionary<int, (Vector2 at, float down)>();
            foreach (var g in plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair && p.Position.y > bottom - 0.01f)
                .GroupBy(p => Mathf.RoundToInt((p.Position.y - bottom) / StairLayout.PieceRise)))
            {
                var ps = g.ToList();
                Vector2 sum = new Vector2(0f, 0f);
                foreach (StairPiece p in ps) sum += new Vector2(p.Position.x, p.Position.z);
                if (ps.Any(p => Mathf.Abs(p.Position.y - (bottom + g.Key * StairLayout.PieceRise)) > 0.05f)) continue;
                rows[g.Key] = (sum * (1f / ps.Count), ps[0].YawDegrees);
            }
            if (level && rows.Count > 0)
            {
                int top = rows.Keys.Max();
                var (at, down) = rows[top];
                rows[top + 1] = (at - StairLayout.Forward(down) * StairLayout.PieceRun, down);
            }
            return rows;
        }
        float Off(float a, float b) => Mathf.Abs(StairLayout.Wrap(a - b + 180f) - 180f);
        var first = Rows(below, bottom);
        var second = Rows(up, bottom);
        float half = Mathf.Max(StairLayout.PieceRun, width) / 2f - 0.1f;
        (int k, Vector2 l1, Vector2 l2, float down, float turn)? best = null;
        (Vector2 at, float most, float across)? capped = null;
        List<string>? why = RoadNetworkGenerator.RoadsLog ? new List<string>() : null;
        for (int k = 1; k <= 6; k++)
        {
            if (!first.TryGetValue(k, out var f) || !second.TryGetValue(k, out var g)) { why?.Add($"{k}: no stair in {(first.ContainsKey(k) ? "the second" : "the first")}"); continue; }
            float h = bottom + k * StairLayout.PieceRise;
            Vector2 l1 = f.at + StairLayout.Forward(f.down) * (StairLayout.PieceRun / 2f);
            Vector2 l2 = g.at + StairLayout.Forward(g.down) * (StairLayout.PieceRun / 2f);
            float across = Vector2.Distance(l1, l2);
            if (across < StairLayout.PieceRun / 2f || (!level && across > ValleySpanReach)) { why?.Add(FormattableString.Invariant($"{k}: {across:F1} m across")); continue; }
            float heading = StairLayout.SnapHeading(StairLayout.Bearing(l1, l2));
            float off1 = Off(heading, f.down), off2 = Off(heading, StairLayout.Wrap(g.down + 180f));
            if (off1 > ValleySpanTurn + 0.01f || off2 > ValleySpanTurn + 0.01f) { why?.Add(FormattableString.Invariant($"{k}: turns {off1:F1}/{off2:F1}")); continue; }
            // Floors over the valley, on columns: buried nowhere deeper than a staircase's piece may be (their earthwork
            // cuts them out where they meet the slope at either end: at 34.5 off the slope the hand-built floors were in it),
            // and crossed level, nowhere higher over the ground than the walkway cap.
            bool buried = false;
            float most = 0f;
            Vector2 dir = (l2 - l1) * (1f / across), right = new Vector2(dir.y, -dir.x);
            for (float t = 0f; t <= across + 0.01f && !buried; t += 0.5f)
                foreach (float side in new[] { -half, 0f, half })
                {
                    Vector2 q = l1 + dir * t + right * side;
                    most = Mathf.Max(most, h - ground(q.x, q.y));
                    if (ground(q.x, q.y) > h + StairCover.RoadStaircase) { buried = true; why?.Add(FormattableString.Invariant($"{k}: buried {ground(q.x, q.y) - h:F2} m at ({q.x:F1}, {q.y:F1})")); break; }
                }
            if (buried) continue;
            if (level && most > StairKit.WalkwayCap)
            {
                why?.Add(FormattableString.Invariant($"{k}: {most:F2} m over the ground"));
                if (capped == null || most < capped.Value.most) capped = (l1, most, across);
                continue;
            }
            // Crossed level, the highest that fits (k climbs); else the one turning least, then the lowest.
            if (best == null || (level ? k > best.Value.k : off1 + off2 < best.Value.turn - 0.01f)) best = (k, l1, l2, f.down, off1 + off2);
        }
        if (best is not { } span)
        {
            if (capped is { } c) StairChain.LogWalkway(c.at, kit, c.most, c.across, laid: false);
            if (why != null)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                    $"[STAIRS] valley at {(below.Foot is Vector3 f0 ? $"({f0.x:F1}, {f0.z:F1}) " : "")}{bottom:F2}: not spanned ({string.Join("; ", why)})"));
            return false;
        }
        float height = bottom + span.k * StairLayout.PieceRise;
        var keepBelow = new StairLayout.PlanSnapshot(below);
        StairLayout.TrimBelow(below, height, kit);
        // From the first's stair at that height, walked down, or crossed level from the floor at its top.
        bool overTop = !below.Pieces.Any(p => p.Kind == StairPieceKind.Stair);
        StairPlan relaid = relay(new StairContinuation(span.l1, span.down, height, overTop ? StairPieceKind.Landing : StairPieceKind.Stair));
        if (relaid.Reached)
        {
            relaid.Foot = new Vector3(span.l1.x, height, span.l1.y);
            below.Foot = relaid.Foot;
        }
        if (!relaid.Reached || !usable(relaid, span.l1))
        {
            if (RoadNetworkGenerator.RoadsLog)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                    $"[STAIRS] valley span at ({span.l1.x:F1}, {span.l1.y:F1}) -> ({span.l2.x:F1}, {span.l2.y:F1}) at {height:F2}: {(relaid.Reached ? "refused: " + Said(LastRefusal) : "not reached: " + relaid.Reason)}"));
            keepBelow.Restore(below);
            return false;
        }
        if (RoadNetworkGenerator.RoadsLog)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                $"[STAIRS] valley spanned at {height:F2}, {span.k} rise(s) over the bottom: ({span.l1.x:F1}, {span.l1.y:F1}) toward ({span.l2.x:F1}, {span.l2.y:F1}), turning {span.turn:F1}"));
        System.Threading.Interlocked.Increment(ref ValleysSpanned);
        if (level)
        {
            var (most, length) = StairChain.Walkway(relaid, height, kit, ground);
            StairChain.LogWalkway(span.l1, kit, most, length, laid: true);
        }
        spanned = relaid;
        from = span.l1;
        return true;
    }



    /// <summary>Ridge meets snapped off the crest's floor's side or a second floor on (<see cref="SnapOffCrest"/>).</summary>
    public static int RidgesSnappedOffCrest;

    /// <summary>
    /// Where the second staircase over a ridge found no way down straight on from the 2x2 on the crest: searched down from
    /// that floor's sides instead, then from a second floor on, so the two still meet through floors on the crest, snapped,
    /// and no road (at 6678,2440, the second laid up to the crest 0.5 m over its floor, a road between:
    /// the 2x2 at ridge top with stairs snapping the whole way up and down is preferred). The second floor is added to
    /// the first staircase only where its way down is used. Returns the way down, or null.
    /// </summary>
    private static StairPlan? SnapOffCrest(RoadStairSpan up, StairContinuation crest, Vector2 b, StairKit kit, Func<float, float, float> ground, List<Vector2> corridor,
        List<Vector2> path, int start, int to, WorldGenerator world, List<RoadStairSpan> spans, ref Vector2 a, ref Vector2? meet)
    {
        StairPlan first = up.Plan;
        Vector2 fwd = StairLayout.Forward(crest.Heading), centre = crest.Edge - fwd * (StairLayout.PieceRun / 2f);
        var rows = new List<(StairContinuation row, bool second)>();
        foreach (int side in new[] { -1, 1 })
            rows.Add((new StairContinuation(centre + StairLayout.Right(crest.Heading) * (side * StairLayout.PieceRun / 2f),
                StairLayout.Wrap(crest.Heading + side * 90f), crest.Height, StairPieceKind.Landing), false));
        rows.Add((new StairContinuation(crest.Edge + fwd * StairLayout.PieceRun, crest.Heading, crest.Height, StairPieceKind.Landing), true));
        foreach (var (row, second) in rows)
        {
            StairPlan down = StairLayout.BuildDown(row, b, ground, Width, kit, GenerationExpansions, GenerationTotalExpansions, GenerationStall, corridor: corridor);
            if (!down.Reached || !Usable(down, path, start, to, world, spans, row.Edge)) continue;
            // A second floor on is the first's: never on a staircase an earlier road built.
            if (second && (up.Existing || !StairLayout.CarryTopTo(first, row.Edge, crest.Height, ground, Width, kit))) continue;
            up.ToPoint = row.Edge;
            a = row.Edge;
            meet = row.Edge;
            if (RoadNetworkGenerator.RoadsLog)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                    $"[STAIRS] ridge at ({crest.Edge.x:F1}, {crest.Edge.y:F1}): snapped off the crest {(second ? "past a second floor" : "from its floor's side, on " + row.Heading.ToString("F1"))}"));
            return down;
        }
        return null;
    }

    /// <summary>Ridge meets whose two tops were not joined (a road stub between them, or merged or refused) this generation.</summary>
    public static int CrestStubs;

    /// <summary>Furthest two tops over a ridge are joined by a flat run where neither was searched off the other
    /// (short sections of road between two stairs are disallowed, about 10-15 m).</summary>
    public const float CrestStubJoinReach = 10f;




    /// <summary>Ridge meets joined lower down, where the two flights pass close, their tops taken away (<see cref="JoinLow"/>).</summary>
    public static int RidgesJoinedLow;

    /// <summary>Most stairs a ridge's two flights are joined lower down with: three (as by hand at a Mistlands ridge).</summary>
    public const int LowJoinStairs = 3;

    /// <summary>Least each flight's top must stand over the row it is cut back to for a ridge's two to be joined lower down:
    /// two rises. A crest the two meet over back to back, or with a 2x2 between, is joined there already.</summary>
    public const float LowJoinSaves = 2f * StairLayout.PieceRise;

    /// <summary>Chains of two staircases that doubled back alongside themselves over a crest or through a pit
    /// (<see cref="StairChain.BackTracks"/>) this generation: joined where their flights pass, laid as one staircase over
    /// both stretches instead, or refused.</summary>
    public static int BackTracksJoined, BackTracksMerged, BackTracksRefused;

    /// <summary>
    /// Two staircases met top to top over a ridge (<paramref name="up"/> climbing to the crest, <paramref name="down"/> the
    /// second, laid climbing to it from its own foot) joined lower down where their flights pass close, as by hand at a
    /// Mistlands ridge (two flights climbed to tops at 92.9 having passed 3 m apart: taken back to a floor at 90.9 and a
    /// switchback's pad at 87.9, the two were joined by three straight stairs from one down onto the other). From a floor of
    /// either, at least <see cref="LowJoinSaves"/> under its top, a straight flight of up to <see cref="LowJoinStairs"/> stairs
    /// is carried down as the search carries a stair on off a floor (StairLayout.JoinDown), its foot resting on the other's
    /// floor level with it (StairLayout.FootRests), with no ground standing over the higher between (no crest between).
    /// Where the two double back alongside each other (<see cref="StairChain.BackTracks"/>: up to a top and straight back down
    /// beside the way up, as over a knoll at -6960,3370), any two rows of theirs at one height, a rise or more under both tops,
    /// are joined too, by a flat run (<see cref="PassingJoins"/>); so too where the road turns back over the crest at any
    /// width (<paramref name="turnsBack"/>: <see cref="StairChain.TurnsBack"/>). Each is cut back to there (StairLayout.TrimAbove: the floor
    /// and, below, its pad), the join laid into the second, and kept only where it runs into neither flight, leaves the two
    /// doubling back nowhere, and the road allows it, as a valley span is (<see cref="SpanTheValley"/>); else both are put back
    /// as they were. Of the joins that fit, the lowest, then the one turning least. Returns whether it did: <paramref name="at"/>
    /// the first's new top, where the two meet.
    /// </summary>
    internal static bool JoinLow(RoadStairSpan up, StairPlan down, StairKit kit, Func<float, float, float> ground, float width,
        Func<StairPlan, Vector2, bool> usable, out Vector2 at, bool turnsBack = false)
    {
        at = default;
        StairPlan first = up.Plan;
        float Surface(StairPiece p) => p.Kind == StairPieceKind.Stair ? p.Position.y + StairLayout.PieceRise : p.Position.y + kit.FloorTop;
        static bool Walked(StairPiece p) => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing;
        var joins = new List<PassJoin>();
        foreach (var (high, low) in new[] { (first, down), (down, first) })
        {
            if (!high.Pieces.Skip(high.SunkPieces).Any(Walked) || !low.Pieces.Skip(low.SunkPieces).Any(Walked)) continue;
            float top = high.Pieces.Skip(high.SunkPieces).Where(Walked).Max(Surface);
            float lowTop = low.Pieces.Skip(low.SunkPieces).Where(Walked).Max(Surface);
            for (int i = high.SunkPieces; i < high.Pieces.Count; i++)
            {
                StairPiece p = high.Pieces[i];
                float h = Surface(p);
                if (p.Kind != StairPieceKind.Landing || h > top - LowJoinSaves + 0.01f) continue;
                var from = new StairContinuation(new Vector2(p.Position.x, p.Position.z) + StairLayout.Forward(p.YawDegrees) * (StairLayout.PieceRun / 2f),
                    p.YawDegrees, h, StairPieceKind.Landing);
                for (int stairs = 1; stairs <= LowJoinStairs; stairs++)
                {
                    float foot = h - stairs * StairLayout.PieceRise;
                    if (foot > lowTop - LowJoinSaves + 0.01f) continue;
                    var floors = Enumerable.Range(low.SunkPieces, low.Pieces.Count - low.SunkPieces)
                        .Where(j => low.Pieces[j].Kind == StairPieceKind.Landing && Mathf.Abs(Surface(low.Pieces[j]) - foot) < 0.05f).ToList();
                    if (floors.Count == 0) continue;
                    var level = floors.Select(j => low.Pieces[j]).ToList();
                    for (int turn = -StairLayout.MaxJoinTurnSteps; turn <= StairLayout.MaxJoinTurnSteps; turn++)
                    {
                        (Vector2 rest, float climb) = StairLayout.JoinFoot(from, turn, stairs, width);
                        if (!StairLayout.FootRests(rest, climb, width, level)) continue;
                        float apart = Vector2.Distance(from.Edge, rest), over = h + StairCover.RoadStaircase;
                        bool crest = false;
                        for (float t = 0f; t <= apart && !crest; t += 0.5f)
                        {
                            Vector2 q = Vector2.Lerp(from.Edge, rest, apart < 0.01f ? 0f : t / apart);
                            crest = ground(q.x, q.y) > over;
                        }
                        // Cut back to the first of the floors at that height (a climb passes a height once): the rest of its
                        // pad stays with it. The first's top: the floor the flight leaves, or the one its foot rests on.
                        if (crest) continue;
                        int stairCount = stairs, turned = turn;
                        StairPiece restOn = low.Pieces[floors[0]];
                        StairPlan cutHigh = high, cutLow = low;
                        int row = i;
                        joins.Add(new PassJoin(h, Math.Abs(turn), foot,
                            high == first ? from : new StairContinuation(rest, climb, foot, StairPieceKind.Landing),
                            () =>
                            {
                                StairLayout.TrimAbove(cutHigh, row, false, kit);
                                StairLayout.TrimAbove(cutLow, cutLow.Pieces.IndexOf(restOn), true, kit);
                            },
                            () => StairLayout.JoinDown(down, from, turned, stairCount, ground, width, kit),
                            FormattableString.Invariant($"{stairs} stair(s) on {climb:F1} from the floor at {foot:F2} up to the one at {h:F2}")));
                    }
                }
            }
        }
        // Doubling back, alongside each other or round a wider loop: joined where they pass, by a flat run.
        if (turnsBack || StairChain.BackTracks(first.Pieces, down.Pieces, kit).Count > 0) joins.AddRange(PassingJoins(first, down, crest: true, kit, ground, width));
        if (!TryJoins(first, down, joins.OrderBy(j => j.Height).ThenBy(j => j.Turn), crest: true, kit, usable, out at, out PassJoin? made)) return false;
        System.Threading.Interlocked.Increment(ref RidgesJoinedLow);
        if (RoadNetworkGenerator.RoadsLog)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                $"[STAIRS] ridge joined low at ({at.x:F1}, {at.y:F1}): {made!.Said}, the tops over it taken away"));
        return true;
    }

    /// <summary>
    /// Two staircases met at a pit's bottom that double back alongside each other (<see cref="StairChain.BackTracks"/>:
    /// <paramref name="below"/> down into it, <paramref name="up"/> back up beside it) joined where their flights pass: of any
    /// two rows of theirs at one height, a rise or more over both feet, the highest first (<see cref="PassingJoins"/>), both cut
    /// back to there (StairLayout.TrimBelow) and a flat run laid between, kept as <see cref="JoinLow"/> keeps its joins.
    /// Returns whether it did: <paramref name="at"/> the first's new foot, where the two meet.
    /// </summary>
    internal static bool JoinHigh(StairPlan below, StairPlan up, StairKit kit, Func<float, float, float> ground, float width,
        Func<StairPlan, Vector2, bool> usable, out Vector2 at) =>
        TryJoins(below, up, PassingJoins(below, up, crest: false, kit, ground, width).OrderByDescending(j => j.Height).ThenBy(j => j.Turn),
            crest: false, kit, usable, out at, out _);

    /// <summary>A way to join a chain's two staircases: the walking height it joins at, how far it turns, the floors its pieces
    /// may rest on (<see cref="RunsInto"/>), the first's new end there, what it cuts back, what it lays, and what to log.</summary>
    private sealed class PassJoin
    {
        public readonly float Height, Rest;
        public readonly int Turn;
        public readonly StairContinuation FirstEnd;
        public readonly Action Cut;
        public readonly Func<bool> Lay;
        public readonly string Said;

        public PassJoin(float height, int turn, float rest, StairContinuation firstEnd, Action cut, Func<bool> lay, string said)
        {
            Height = height; Turn = turn; Rest = rest; FirstEnd = firstEnd; Cut = cut; Lay = lay; Said = said;
        }
    }

    /// <summary>
    /// Every flat run joining two rows of a chain's staircases at one height, a rise or more inside both of their far ends
    /// (under both tops over a crest, over both feet in a pit): <paramref name="first"/>'s row on to <paramref name="second"/>'s
    /// (or back, where only that fits) as two tops are joined (StairLayout.JoinTops: a floor turned off a stair up to 90 degrees
    /// on its inside corner, the wedge filled, floor over floor onto the other's line, within the walkway cap and
    /// <see cref="CrestStubJoinReach"/>). A stair's row over a crest is its top edge, climbing; in a pit its low edge, walking
    /// down. A floor's is its far edge (its near edge in a pit), save a switchback's pad, whose legs say where it is walked.
    /// The run is laid into <paramref name="second"/>.
    /// </summary>
    private static List<PassJoin> PassingJoins(StairPlan first, StairPlan second, bool crest, StairKit kit, Func<float, float, float> ground, float width)
    {
        static bool Walked(StairPiece p) => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing;
        static Vector2 At(StairPiece p) => new(p.Position.x, p.Position.z);
        List<(int index, StairContinuation row)> Rows(StairPlan plan)
        {
            var rows = new List<(int, StairContinuation)>();
            var walked = plan.Pieces.Skip(plan.SunkPieces).Where(Walked).ToList();
            for (int i = plan.SunkPieces; i < plan.Pieces.Count; i++)
            {
                StairPiece p = plan.Pieces[i];
                if (p.Kind == StairPieceKind.Stair)
                {
                    // A stair's walking height at the row: its top over a crest, its base in a pit.
                    float h = crest ? p.Position.y + StairLayout.PieceRise : p.Position.y;
                    float heading = crest ? StairLayout.Wrap(p.YawDegrees + 180f) : p.YawDegrees;
                    Vector2 edge = At(p) + StairLayout.Forward(heading) * (StairLayout.PieceRun / 2f);
                    // A floor level with that edge carries the row on: it is the row.
                    if (walked.Any(q => q.Kind == StairPieceKind.Landing && Mathf.Abs(q.Position.y + kit.FloorTop - h) < 0.05f
                        && Vector2.Distance(At(q), edge) < StairLayout.PieceRun)) continue;
                    rows.Add((i, new StairContinuation(edge, heading, h, StairPieceKind.Stair)));
                }
                else if (p.Kind == StairPieceKind.Landing)
                {
                    float h = p.Position.y + kit.FloorTop;
                    // A pad's floors are walked across, not along.
                    if (walked.Any(q => q != p && q.Kind == StairPieceKind.Landing && Mathf.Abs(q.Position.y - p.Position.y) < 0.05f
                        && StairChain.YawApart(q.YawDegrees, p.YawDegrees) < 0.1f
                        && Mathf.Abs(Vector2.Dot(At(q) - At(p), StairLayout.Forward(p.YawDegrees))) < 0.1f)) continue;
                    float heading = crest ? p.YawDegrees : StairLayout.Wrap(p.YawDegrees + 180f);
                    rows.Add((i, new StairContinuation(At(p) + StairLayout.Forward(heading) * (StairLayout.PieceRun / 2f), heading, h, StairPieceKind.Landing)));
                }
            }
            return rows;
        }
        var joins = new List<PassJoin>();
        var mine = Rows(first);
        var theirs = Rows(second);
        if (mine.Count == 0 || theirs.Count == 0) return joins;
        // A rise or more inside both far ends: under both tops, over both feet.
        float inside = crest ? Mathf.Min(mine.Max(r => r.row.Height), theirs.Max(r => r.row.Height)) - StairLayout.PieceRise
            : Mathf.Max(mine.Min(r => r.row.Height), theirs.Min(r => r.row.Height)) + StairLayout.PieceRise;
        foreach (var (i, r1) in mine)
            foreach (var (j, r2) in theirs)
            {
                if (Mathf.Abs(r1.Height - r2.Height) > 0.05f || (crest ? r1.Height > inside + 0.01f : r1.Height < inside - 0.01f)
                    || Vector2.Distance(r1.Edge, r2.Edge) > CrestStubJoinReach) continue;
                StairPiece mineAt = first.Pieces[i], theirsAt = second.Pieces[j];
                float h = r1.Height;
                void Cut(StairPlan plan, StairPiece row)
                {
                    if (crest) StairLayout.TrimAbove(plan, plan.Pieces.IndexOf(row), false, kit);
                    else StairLayout.TrimBelow(plan, row.Position.y + (row.Kind == StairPieceKind.Stair ? 0f : kit.FloorTop), kit);
                }
                StairContinuation from = r1, to = r2;
                joins.Add(new PassJoin(h, 0, h, r1,
                    () => { Cut(first, mineAt); Cut(second, theirsAt); },
                    () => StairLayout.JoinTops(second, from, to, ground, width, kit, CrestStubJoinReach).Laid
                        || StairLayout.JoinTops(second, to, from, ground, width, kit, CrestStubJoinReach).Laid,
                    FormattableString.Invariant($"a flat run at {h:F2} from ({from.Edge.x:F1}, {from.Edge.y:F1}) to ({to.Edge.x:F1}, {to.Edge.y:F1})")));
            }
        return joins;
    }

    /// <summary>
    /// <paramref name="joins"/> tried in turn (up to a dozen) on a chain's two staircases: each cuts both back and lays its
    /// join into <paramref name="second"/>, kept where it laid, runs into neither flight, leaves the two doubling back nowhere
    /// (<see cref="StairChain.BackTracks"/>) and <paramref name="usable"/> allows it; else both are put back as they were. The
    /// first's new end (its top over a crest, its foot in a pit) is the second's too: <paramref name="at"/>.
    /// </summary>
    private static bool TryJoins(StairPlan first, StairPlan second, IEnumerable<PassJoin> joins, bool crest, StairKit kit,
        Func<StairPlan, Vector2, bool> usable, out Vector2 at, out PassJoin? made)
    {
        at = default;
        made = null;
        foreach (PassJoin join in joins.Take(12))
        {
            var keepA = new StairLayout.PlanSnapshot(first);
            var keepB = new StairLayout.PlanSnapshot(second);
            (Vector3 endA, float headingA, StairPieceKind kindA, Vector3 endB, Vector3? footA, Vector3? footB)
                = (first.End, first.EndHeading, first.LastRowKind, second.End, first.Foot, second.Foot);
            join.Cut();
            int laid = second.Pieces.Count;
            bool joined = join.Lay();
            Vector3 end = new(join.FirstEnd.Edge.x, join.FirstEnd.Height, join.FirstEnd.Edge.y);
            Vector2 meet = join.FirstEnd.Edge;
            if (crest)
            {
                (first.End, first.EndHeading, first.LastRowKind) = (end, join.FirstEnd.Heading, join.FirstEnd.Kind);
                second.End = end;
            }
            else first.Foot = second.Foot = end;
            if (joined && !RunsInto(second, laid, first, join.Rest) && StairChain.BackTracks(first.Pieces, second.Pieces, kit).Count == 0
                && usable(second, meet))
            {
                at = meet;
                made = join;
                return true;
            }
            keepA.Restore(first);
            keepB.Restore(second);
            (first.End, first.EndHeading, first.LastRowKind, second.End, first.Foot, second.Foot) = (endA, headingA, kindA, endB, footA, footB);
        }
        return false;
    }

    /// <summary>Whether a walked piece of <paramref name="plan"/> from <paramref name="from"/> on stands in one laid before it, of
    /// <paramref name="plan"/> or of <paramref name="other"/> (centres closer than a turn's pieces come, within a step's height),
    /// floors at <paramref name="foot"/> aside (a ridge join's foot rests on them).</summary>
    private static bool RunsInto(StairPlan plan, int from, StairPlan other, float foot)
    {
        bool Walked(StairPiece p) => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing;
        StairKit kit = StairKit.Named(plan.Kit) ?? StairKit.Stone;
        var laid = plan.Pieces.Take(from).Concat(other.Pieces)
            .Where(p => Walked(p) && !(p.Kind == StairPieceKind.Landing && Mathf.Abs(p.Position.y + kit.FloorTop - foot) < 0.05f)).ToList();
        foreach (StairPiece p in plan.Pieces.Skip(from).Where(Walked))
            foreach (StairPiece q in laid)
                if (Vector2.Distance(new Vector2(p.Position.x, p.Position.z), new Vector2(q.Position.x, q.Position.z)) < 1.3f
                    && Mathf.Abs(p.Position.y - q.Position.y) < 1.5f) return true;
        return false;
    }

    /// <summary>Ridge meets closed with a flat run between the two tops this generation (StairLayout.JoinTops).</summary>
    public static int RidgesBridged;

    /// <summary>Ridge meets laid back to back, the second searched down from the first's top edge (StairLayout.BuildDown).</summary>
    public static int RidgesSnapped;


    /// <summary>Valley meets laid from a floor shared at the bottom this generation.</summary>
    public static int ValleysMet;



    /// <summary>Whether <paramref name="up"/> climbs to a crest that <paramref name="down"/>, the next stretch, comes down
    /// from, within <see cref="StoneGap"/> along the road between them: a ridge's two staircases, met top to top.</summary>
    internal static bool RidgePair(List<Vector2> path, (int from, int to) up, (int from, int to) down, Func<Vector2, float> ground)
    {
        if (ground(path[up.to]) <= ground(path[up.from]) || ground(path[down.to]) >= ground(path[down.from])) return false;
        float gap = 0f;
        for (int i = up.to + 1; i <= down.from; i++) gap += Vector2.Distance(path[i - 1], path[i]);
        return gap < StoneGap;
    }

    /// <summary>
    /// Whether the second staircase out of a valley, with the span across it (<see cref="SpanTheValley"/>), may be laid:
    /// judged over the whole stretch the two staircases climb in place of road, from the first's top
    /// (<paramref name="first"/>.FromIndex) to the second's (<paramref name="to"/>). The span's floors start on the first's
    /// flight, over the first's stretch, where no road is built: judged over the second's stretch alone, a floor there was
    /// "on its road" (7 of the 8 valleys tried were refused so; at 2591,-6713 the floor a rise over the bottom,
    /// over the first's last segment).
    /// The rest of its own road and every road in the network still refuse it.
    /// </summary>
    internal static bool SpanUsable(StairPlan up, List<Vector2> path, RoadStairSpan first, int to, WorldGenerator world, List<RoadStairSpan> before, Vector2 meet)
        => Usable(up, path, first.FromIndex, to, world, before, meet);

    /// <summary>How close a laid row's far edge must come to a road's end, across and in height,
    /// for the road to end on that row.</summary>
    public const float TopRowReach = 0.6f;

    /// <summary>The top row of a laid staircase whose far edge is the road's end <paramref name="end"/>,
    /// at <paramref name="height"/>: the road ends on that staircase's top, and a staircase on from
    /// there carries on from that row (<see cref="StairLayout.Build"/>'s after). Null where none ends
    /// there: a road's own end, or a staircase's foot.</summary>
    internal static StairContinuation? TopAt(Vector2 end, float height, IEnumerable<StructurePiece> pieces)
    {
        StairContinuation? best = null;
        float nearest = TopRowReach;
        foreach (StructurePiece p in pieces)
        {
            bool step = p.Kind == StructurePieceKind.StairStep;
            if (!step && p.Kind != StructurePieceKind.StairFloor) continue;
            // A step climbs toward its local -z and walks a rise up from its origin; a floor
            // walks its kit's top above its origin, either way along its yaw (StairLayout.AddPiece).
            float walk = step ? p.Position.y + StairLayout.PieceRise
                : p.Position.y + StairKit.FloorTopOf(p.Prefab);
            if (Mathf.Abs(walk - height) > TopRowReach) continue;
            var at = new Vector2(p.Position.x, p.Position.z);
            foreach (float heading in step ? new[] { StairLayout.Wrap(p.YawDegrees + 180f) } : new[] { p.YawDegrees, StairLayout.Wrap(p.YawDegrees + 180f) })
            {
                Vector2 edge = at + StairLayout.Forward(heading) * (StairLayout.PieceRun / 2f);
                float d = Vector2.Distance(edge, end);
                if (d > nearest) continue;
                nearest = d;
                best = new StairContinuation(edge, heading, walk, step ? StairPieceKind.Stair : StairPieceKind.Landing);
            }
        }
        return best;
    }
}
