using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Two staircases of one road that meet over a ridge or through a valley (one owner): over a ridge top to top on a 2x2 at the
/// crest, the second searched down from the first's top edge, never a stub of road between; through a valley at their feet on a
/// shared floor, or higher up across a straight span of floors.
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
    /// neither flight's line by more than <see cref="ValleySpanTurn"/> and buried nowhere, both are cut back to that height
    /// and the span laid between them on columns (at 4393,6442: take off the bottom two stairs of each and the shared floor, and lay four floors at 34.5 on 112.5 from one to the other:
    /// smoother, with no 180 degree turn). Of the heights that fit, the one turning least, then the lowest. Returns whether
    /// it did; <paramref name="from"/> the span's start on the first.
    /// </summary>
    /// <para>A shallow valley (<paramref name="level"/>: <see cref="StairChain.CrossesLevel"/>) is crossed level: the span at
    /// the highest height that fits, up to the lower flight's top edge, so no stair is left down into it where the two tops
    /// stand level (the chain's one rule, as an approach crosses a shallow dip).</para>
    internal static bool SpanTheValley(StairPlan below, StairPlan up, StairKit kit, Func<float, float, float> ground, float bottom, float width,
        Func<StairPlan, Vector2, bool> usable, out Vector2 from, bool level = false)
    {
        from = default;
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
        (int k, Vector2 l1, Vector2 l2, float turn)? best = null;
        List<string>? why = RoadNetworkGenerator.RoadsLog ? new List<string>() : null;
        for (int k = 1; k <= 6; k++)
        {
            if (!first.TryGetValue(k, out var f) || !second.TryGetValue(k, out var g)) { why?.Add($"{k}: no stair in {(first.ContainsKey(k) ? "the second" : "the first")}"); continue; }
            float h = bottom + k * StairLayout.PieceRise;
            Vector2 l1 = f.at + StairLayout.Forward(f.down) * (StairLayout.PieceRun / 2f);
            Vector2 l2 = g.at + StairLayout.Forward(g.down) * (StairLayout.PieceRun / 2f);
            float across = Vector2.Distance(l1, l2);
            if (across < StairLayout.PieceRun / 2f || across > ValleySpanReach) { why?.Add(FormattableString.Invariant($"{k}: {across:F1} m across")); continue; }
            float heading = StairLayout.SnapHeading(StairLayout.Bearing(l1, l2));
            float aside = Vector2.Dot(l2 - l1, StairLayout.Right(heading));
            if (Mathf.Abs(aside) > StairLayout.TopJoinAside) { why?.Add(FormattableString.Invariant($"{k}: {aside:F1} m aside")); continue; }
            float off1 = Off(heading, f.down), off2 = Off(heading, StairLayout.Wrap(g.down + 180f));
            if (off1 > ValleySpanTurn + 0.01f || off2 > ValleySpanTurn + 0.01f) { why?.Add(FormattableString.Invariant($"{k}: turns {off1:F1}/{off2:F1}")); continue; }
            // Floors over the valley, on columns: buried nowhere deeper than a staircase's piece may be (their earthwork
            // cuts them out where they meet the slope at either end: at 34.5 off the slope the hand-built floors were in it).
            bool buried = false;
            Vector2 dir = (l2 - l1) * (1f / across), right = new Vector2(dir.y, -dir.x);
            for (float t = 0f; t <= across + 0.01f && !buried; t += 0.5f)
                foreach (float side in new[] { -half, 0f, half })
                {
                    Vector2 q = l1 + dir * t + right * side;
                    if (ground(q.x, q.y) > h + StairCover.RoadStaircase) { buried = true; why?.Add(FormattableString.Invariant($"{k}: buried {ground(q.x, q.y) - h:F2} m at ({q.x:F1}, {q.y:F1})")); break; }
                }
            if (buried) continue;
            // Crossed level, the highest that fits (k climbs); else the one turning least, then the lowest.
            if (best == null || (level ? k > best.Value.k : off1 + off2 < best.Value.turn - 0.01f)) best = (k, l1, l2, off1 + off2);
        }
        if (best is not { } span)
        {
            if (why != null)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                    $"[STAIRS] valley at {(below.Foot is Vector3 f0 ? $"({f0.x:F1}, {f0.z:F1}) " : "")}{bottom:F2}: not spanned ({string.Join("; ", why)})"));
            return false;
        }
        float height = bottom + span.k * StairLayout.PieceRise;
        var keepBelow = new StairLayout.PlanSnapshot(below);
        var keepUp = new StairLayout.PlanSnapshot(up);
        StairLayout.TrimBelow(below, height, kit);
        StairLayout.TrimBelow(up, height, kit);
        float spanHeading = StairLayout.SnapHeading(StairLayout.Bearing(span.l1, span.l2));
        Vector2 fwd = StairLayout.Forward(spanHeading);
        float along = Vector2.Dot(span.l2 - span.l1, fwd), off = Vector2.Dot(span.l2 - span.l1, StairLayout.Right(spanHeading));
        int floors = Mathf.Max(1, Mathf.CeilToInt(along / StairLayout.PieceRun - 0.001f));
        for (int i = 0; i < floors; i++)
        {
            float back = Mathf.Max(0f, Mathf.Min(i * StairLayout.PieceRun, along - StairLayout.PieceRun));
            StairLayout.AddFloorAt(up, kit, span.l1 + fwd * back + (i == floors - 1 ? StairLayout.Right(spanHeading) * off : new Vector2(0f, 0f)),
                spanHeading, height, ground, width);
        }
        up.WalkedLength += along;
        up.Foot = new Vector3(span.l1.x, height, span.l1.y);
        below.Foot = new Vector3(span.l1.x, height, span.l1.y);
        if (!usable(up, span.l1))
        {
            if (RoadNetworkGenerator.RoadsLog)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                    $"[STAIRS] valley span at ({span.l1.x:F1}, {span.l1.y:F1}) -> ({span.l2.x:F1}, {span.l2.y:F1}) at {height:F2}: refused: {Said(LastRefusal)}"));
            keepBelow.Restore(below);
            keepUp.Restore(up);
            return false;
        }
        if (RoadNetworkGenerator.RoadsLog)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                $"[STAIRS] valley spanned at {height:F2}, {span.k} rise(s) over the bottom: ({span.l1.x:F1}, {span.l1.y:F1}) -> ({span.l2.x:F1}, {span.l2.y:F1}) on {spanHeading:F1}, {floors} floor(s), turning {span.turn:F1}"));
        System.Threading.Interlocked.Increment(ref ValleysSpanned);
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

    /// <summary>Ridge meets whose two tops were not joined (a road stub between them, or merged or refused) this generation,
    /// and ones joined by floors across the stub instead.</summary>
    public static int CrestStubs, CrestStubsJoined;

    /// <summary>Furthest two tops over a ridge are joined by floors across where neither was searched off the other
    /// (short sections of road between two stairs are disallowed, about 10-15 m).</summary>
    public const float CrestStubJoinReach = 10f;




    /// <summary>Ridge meets closed with a floor between the two tops this generation (StairLayout.BridgeTopTo).</summary>
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
