using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// One owner of the ways into a place: a place has one way in up a face. Every way into a place, an approach staircase, a
/// road's way onto its landing and a road arriving at it as plain road, is a <see cref="Way"/>: where it sets off toward the
/// place and where it arrives, over its last stretch (<see cref="RoadStairRuns.SiteReach"/> along it, a staircase whole),
/// and whether it climbs to the place, comes down to it or arrives level (by a stair's rise, <see cref="StairLayout.PieceRise"/>).
/// A second way into the same place arriving the same way as one there already, heading within
/// <see cref="SameSideAngle"/> (90 degrees, not the road corridor's 25) of it, over its last stretch or as it steps onto the place
/// (<see cref="Way.Arrival"/>: a staircase's last flight onto its top, wherever its foot and its road lie), and climbing,
/// coming down or level as it does, is that way built
/// twice (two staircases side by side up one face to one landing; a road up beside the staircase): it is not built, and
/// its road ends on the first way's road (<see cref="Joining"/>, RoadNetworkGenerator.ReEndOnAWayIn), at the approach's foot
/// or where that road's arrival passes nearest. Two ways going opposite ways (one up to the landing and one down to it, or
/// in from the other side; two close ones, one up and one down, are two) and ways 90 degrees or more apart (other faces) are other ways in. Off with the corridor snap, as the rest of the
/// corridor rule. The road corridor snap for roads not arriving at a place is its own rule (RoadNetworkGenerator.SnapToNetwork).
/// </summary>
public static class PlaceWaysIn
{
    /// <summary>How far apart, in degrees, two ways climbing the same way onto one place may head and still be one way in
    /// (the same side of it): under it they are twins, at or over it other ways in. Set on the project owner's word on 5 Nov 2026 (the road
    /// corridor's <see cref="RoadNetworkGenerator.CorridorAngle"/>, 25, is too tight for a place: a second staircase 30 to
    /// 60 degrees off the first still looked like two up the same side). An up way and a down way are never twins.</summary>
    public const float SameSideAngle = 90f;

    /// <summary>A way into a place: its line toward the place over its last stretch (outer end first), on its walking
    /// surface; and, for an approach staircase, where its own road meets it.</summary>
    public sealed class Way
    {
        public readonly List<Vector2> Line;
        public readonly List<float> Heights;
        /// <summary>The approach staircase it is, if it is one: joined at its foot (<see cref="RoadStairRuns.RoadEndOf"/>).</summary>
        public readonly StairRun? Approach;
        internal Way(List<Vector2> line, List<float> heights, StairRun? approach) { Line = line; Heights = heights; Approach = approach; }
        public Vector2 Outer => Line[0];
        public Vector2 Inner => Line[Line.Count - 1];
        /// <summary>How it steps onto the place: its heading over its last <see cref="RoadStairRuns.MeetReach"/> along it to
        /// its inner end (a staircase's last flight onto its top). Two staircases up one face to one landing climb onto it
        /// side by side however their feet lie: at MountainCave02 (5696,3584) one set off north from its road and turned west,
        /// the other set off west, their lines 31 degrees apart foot to top and their last flights side by side, 2.5 m apart.</summary>
        public Vector2 Arrival
        {
            get
            {
                float along = 0f;
                for (int i = Line.Count - 2; i >= 0; i--)
                {
                    float d = Vector2.Distance(Line[i], Line[i + 1]);
                    if (along + d >= RoadStairRuns.MeetReach)
                        return Inner - Vector2.Lerp(Line[i + 1], Line[i], (RoadStairRuns.MeetReach - along) / d);
                    along += d;
                }
                return Inner - Outer;
            }
        }
        /// <summary>1 climbing to the place, -1 coming down to it, 0 arriving level.</summary>
        public int Rise
        {
            get
            {
                float rise = Heights[Heights.Count - 1] - Heights[0];
                return rise >= StairLayout.PieceRise ? 1 : rise <= -StairLayout.PieceRise ? -1 : 0;
            }
        }
    }

    private static readonly List<(Vector2 place, Way way, byte island)> s_roads = new();

    /// <summary>Whether the rule is read at all: off where the corridor snap is off (plain roads, tests).</summary>
    internal static bool On => RoadNetworkGenerator.CorridorSnap > RoadNetworkGenerator.RoadSnap;

    /// <summary>
    /// A way into <paramref name="place"/> along <paramref name="points"/> at <paramref name="heights"/>, in either order:
    /// its last <see cref="RoadStairRuns.SiteReach"/> along it to its end nearer the place, outer end first; the whole of it
    /// where it is <paramref name="whole"/> (a staircase). Null where it has no length.
    /// </summary>
    public static Way? Of(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, Vector2 place, bool whole = false, StairRun? approach = null,
        bool? placeEndIsLast = null)
    {
        if (points.Count < 2) return null;
        // Which end is the place end: a staircase says so itself (placeEndIsLast, from its own ends); a plain road has no better
        // signal than the end nearer the place. Never nearness for a staircase: one that turns, or whose top lands farther from
        // the place's centre than its foot, would read its climb backwards (an up as a down).
        bool towardPlace = placeEndIsLast ?? Vector2.Distance(points[points.Count - 1], place) <= Vector2.Distance(points[0], place);
        var line = new List<Vector2>();
        var hs = new List<float>();
        float along = 0f;
        for (int k = 0; k < points.Count; k++)
        {
            int i = towardPlace ? points.Count - 1 - k : k;
            if (k > 0)
            {
                along += Vector2.Distance(line[line.Count - 1], points[i]);
                if (!whole && along > RoadStairRuns.SiteReach) break;
            }
            line.Add(points[i]);
            hs.Add(heights[i]);
        }
        line.Reverse();
        hs.Reverse();
        return line.Count < 2 || Vector2.Distance(line[0], line[line.Count - 1]) < 1e-3f ? null : new Way(line, hs, approach);
    }

    /// <summary>A staircase's walked pieces (steps and floors) as a way into <paramref name="place"/>, whole. Its place end is
    /// the plan's own: where the layout ends (<see cref="StairPlan.End"/>) unless it was laid out from the place
    /// (<see cref="StairPlan.Reversed"/>, as RoadStairRuns.Span reads it).</summary>
    public static Way? Of(StairPlan plan, Vector2 place)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        foreach (StairPiece p in plan.Pieces)
            if (p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing)
            {
                points.Add(new Vector2(p.Position.x, p.Position.z));
                heights.Add(p.Kind == StairPieceKind.Stair ? p.Position.y + StairLayout.PieceRise / 2f : p.Position.y + StairKit.FloorTopOf(p.Prefab));
            }
        if (points.Count < 2) return null;
        var end = new Vector2(plan.End.x, plan.End.z);
        bool layoutEndIsLast = Vector2.Distance(points[points.Count - 1], end) <= Vector2.Distance(points[0], end);
        return Of(points, heights, place, whole: true, placeEndIsLast: layoutEndIsLast != plan.Reversed);
    }

    /// <summary>A laid approach staircase as a way into <paramref name="place"/>, whole. Its place end is the run's own
    /// <see cref="StairRun.To"/> end, not the end nearer the place's centre.</summary>
    public static Way? Of(StairRun run, Vector2 place)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        foreach (StructurePiece p in run.Pieces)
            if (p.Kind is StructurePieceKind.StairStep or StructurePieceKind.StairFloor)
            {
                points.Add(new Vector2(p.Position.x, p.Position.z));
                heights.Add(RoadStairRuns.Surface(p));
            }
        if (points.Count < 2) return null;
        bool toIsLast = Vector2.Distance(points[points.Count - 1], run.To) <= Vector2.Distance(points[0], run.To);
        return Of(points, heights, place, whole: true, approach: run, placeEndIsLast: toIsLast);
    }

    /// <summary>Whether <paramref name="mine"/> arrives the same way as <paramref name="theirs"/>: heading within
    /// <see cref="SameSideAngle"/> of it toward the place, over its last stretch (outer end to inner) or
    /// as it steps onto the place (<see cref="Way.Arrival"/>), climbing, coming down or level as it does; not where one
    /// carries on from the other (the foot of one at the other's top, within <see cref="RoadStairRuns.MeetReach"/>). Judged by
    /// the ways themselves, never by where their roads go on from their outer ends.</summary>
    public static bool SameWay(Way mine, Way theirs)
    {
        if (mine.Rise != theirs.Rise) return false;
        static bool Heads(Vector2 a, Vector2 b) =>
            a.sqrMagnitude >= 1e-4f && b.sqrMagnitude >= 1e-4f
            && Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector2.Dot(a.normalized, b.normalized)))) * 180.0 / Math.PI < SameSideAngle - 1e-3f;
        if (!Heads(mine.Inner - mine.Outer, theirs.Inner - theirs.Outer) && !Heads(mine.Arrival, theirs.Arrival)) return false;
        bool Meet(Vector2 p, float hp, Vector2 q, float hq) =>
            Vector2.Distance(p, q) <= RoadStairRuns.MeetReach && Mathf.Abs(hp - hq) <= StairLayout.PieceRise;
        return !Meet(mine.Outer, mine.Heights[0], theirs.Inner, theirs.Heights[theirs.Heights.Count - 1])
            && !Meet(mine.Inner, mine.Heights[mine.Heights.Count - 1], theirs.Outer, theirs.Heights[0]);
    }

    /// <summary>The ways into <paramref name="place"/> there already: its approach staircases (<see cref="RoadStairRuns.PlaceOf"/>)
    /// and the roads that arrive at it as road (<see cref="Record"/>), as this island sees them (CrossIslandReads).</summary>
    public static List<Way> There(Vector2 place)
    {
        var ways = new List<Way>();
        Vector2 into = RoadStairRuns.IntoThePlace(place);
        float reach = RoadNetworkGenerator.PlacedSiteReach;
        foreach (StairRun run in StairStructures.ApproachesNear(place - new Vector2(reach, reach), place + new Vector2(reach, reach), 0f))
            if (RoadStairRuns.PlaceOf(run) is Vector2 p && (p - into).sqrMagnitude < 1f && Of(run, place) is Way w) ways.Add(w);
        int me = CrossIslandReads.Me;
        lock (s_roads)
            foreach (var (at, way, island) in s_roads)
                if ((at - place).sqrMagnitude < 1f && CrossIslandReads.Sees(me, island)) ways.Add(way);
        return ways;
    }

    /// <summary>The way into <paramref name="place"/> there already that <paramref name="mine"/> would build twice
    /// (<see cref="SameWay"/>), and where its road takes the second instead (<paramref name="joinAt"/>): an approach's foot,
    /// where its own road meets it, or the point of a road's arrival nearest where the second sets off toward the place.
    /// Null where none (and always where the rule is off).</summary>
    public static Way? Joining(Vector2 place, Way? mine, out Vector2 joinAt)
    {
        joinAt = default;
        if (!On || mine == null) return null;
        foreach (Way theirs in There(place))
        {
            if (!SameWay(mine, theirs)) continue;
            if (theirs.Approach is StairRun run) joinAt = RoadStairRuns.RoadEndOf(run, place);
            else
            {
                float best = float.MaxValue;
                foreach (Vector2 q in theirs.Line)
                    if (Vector2.Distance(q, mine.Outer) is float d && d < best) { best = d; joinAt = q; }
            }
            return theirs;
        }
        return null;
    }

    /// <summary>A road arriving at <paramref name="place"/> as road (no staircase at that end), recorded as it is laid.</summary>
    public static void Record(Vector2 place, Way way)
    {
        lock (s_roads) s_roads.Add((place, way, CrossIslandReads.Tag));
    }

    /// <summary>
    /// The whole network's twins: every place with two ways in that arrive the same way (<see cref="SameWay"/>, the rule's own
    /// test), each pair once, with a line naming the place and both ways by their ends. Read over the network as it stands
    /// (its approach staircases, <see cref="RoadStairRuns.PlaceOf"/>, and the roads recorded arriving as road), whether or
    /// not the rule was on: the invariant beside the staircase-end audit (StairEnds), which a gate holds at none.
    /// </summary>
    public static List<(Vector2 place, Way first, Way second, string line)> Twins()
    {
        var byPlace = new List<(Vector2 into, List<Way> ways)>();
        List<Way> At(Vector2 into)
        {
            foreach (var (at, ways) in byPlace) if ((at - into).sqrMagnitude < 1f) return ways;
            var added = new List<Way>();
            byPlace.Add((into, added));
            return added;
        }
        foreach (StairRun run in StairStructures.Runs)
            if (RoadStairRuns.PlaceOf(run) is Vector2 into && Of(run, into) is Way w) At(into).Add(w);
        lock (s_roads)
            foreach (var (place, way, _) in s_roads) At(RoadStairRuns.IntoThePlace(place)).Add(way);
        var twins = new List<(Vector2, Way, Way, string)>();
        foreach (var (into, ways) in byPlace)
            for (int i = 0; i < ways.Count; i++)
                for (int j = i + 1; j < ways.Count; j++)
                    if (SameWay(ways[j], ways[i]))
                        twins.Add((into, ways[i], ways[j], FormattableString.Invariant(
                            $"[STAIRS] twin ways in at ({into.x:F1}, {into.y:F1}): {Name(ways[i])} and {Name(ways[j])} arrive the same way")));
        return twins;
    }

    private static string Name(Way way) => FormattableString.Invariant(
        $"{(way.Approach != null ? "staircase" : "road")} ({way.Outer.x:F1}, {way.Outer.y:F1}) -> ({way.Inner.x:F1}, {way.Inner.y:F1})");

    /// <summary>No roads arriving anywhere: a new network.</summary>
    public static void Reset()
    {
        lock (s_roads) s_roads.Clear();
    }
}
