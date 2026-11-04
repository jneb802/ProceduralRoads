using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// How a built structure's end meets the road: the one owner, so stairs and roads meet each other consistently. Every
/// crossing structure with ends asks it alike (BridgeStructures.Ends: a viaduct, a wooden or Dvergr wood bridge, a ford's
/// spanned footbridge): its end stairs decided the same way (<see cref="Decide"/>), the road put through their foot on their
/// line (<see cref="MeetTheRoad"/>) and levelled to it. A
/// staircase's foot and top keep their own rule (RoadStairRuns.Feet / Tops), with the same 8 m reach of the
/// road's travel (RoadStairRuns.TravelReach).
/// <para>It decides, for each end:</para>
/// <list type="number">
/// <item>its height from the ground the road arrives on (<see cref="Arrival"/>), over the last <see cref="ApronLength"/>
/// of road before it, never the bank point's own ground, which may stand off the road that comes to it;</item>
/// <item>a flush join, or stairs down to the road (<see cref="Decide"/>): their foot on the road, centred, the road brought
/// in along the flight's line (<see cref="MeetTheRoad"/>);</item>
/// <item>the earthwork that levels the road's last <see cref="ApronLength"/> to the end's height and centres it on the
/// structure's line (RoadSpatialGrid.LevelledEnds, the seam at a bank);</item>
/// <item>the final check that a player walks from the road onto it (<see cref="Reachable"/>): where the road as planned does
/// not meet it, the structure is not built and the road is searched again without it (RoadNetworkGenerator).</item>
/// </list>
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

    /// <summary>How far the road where it meets an end may stand off the end's height for a player to walk on: about a
    /// step a player takes without a jump.</summary>
    public const float StepTolerance = 0.5f;

    /// <summary>One end of a structure as it meets the road.</summary>
    public readonly struct End
    {
        /// <summary>The middle of the structure's end edge: where its walking surface stops, or its stairs leave it.</summary>
        public readonly Vector2 Edge;
        /// <summary>The way onto the structure, along its line.</summary>
        public readonly Vector2 Up;
        /// <summary>The structure's walking surface at the edge.</summary>
        public readonly float Height;
        /// <summary>Stairs down from the edge, each <see cref="BridgeLayout.StairRise"/> down and
        /// <see cref="BridgeLayout.StairRun"/> out; 0 where the road runs onto the structure flush.</summary>
        public readonly int Steps;
        public End(Vector2 edge, Vector2 up, float height, int steps)
        {
            Edge = edge; Up = up; Height = height; Steps = steps;
        }
        public float Rise => Steps * BridgeLayout.StairRise;
        /// <summary>Where the road ends: the foot of the stairs, or the edge.</summary>
        public Vector2 Meet => Edge - Up * (BridgeLayout.StairRun * Steps);
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
        public bool IsSet => Up != default;
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
            Vector2 foot = edge - up * (BridgeLayout.StairRun * s);
            float off = Mathf.Abs(height - s * BridgeLayout.StairRise - Arrival(foot, -up, ground));
            if (off < bestOff - 1e-3f) { best = s; bestOff = off; }
            // Past a flush end that already meets the arriving road, no stairs.
            if (s == 0 && off <= BridgeLayout.StairRise * 0.5f) break;
        }
        return new End(edge, up, height, best);
    }

    /// <summary>
    /// The finished ground at <paramref name="p"/> near a structure's ends: the generator's, cut to the meeting height where
    /// the road runs on under a joining piece (<see cref="End.UnderTheJoint"/>). A structure judges which of its pieces lie
    /// wholly under the ground on this, so the deck's end module is not dropped as buried under a bank the road then cuts
    /// away (the deck was one module short of the levelled road).
    /// </summary>
    public static float FinishedGround(Vector2 p, float generator, End near, End far, float halfWidth)
    {
        foreach (End end in new[] { near, far })
            if (end.IsSet && end.UnderTheJoint(p, halfWidth)) generator = Mathf.Min(generator, end.MeetHeight);
        return generator;
    }

    /// <summary>Road between two structures shorter than this is a stub (a tiny bit of road between them, about 6-8 m): it is one level landing at the joining height, or not laid.</summary>
    public const float StubLength = 8f;

    /// <summary>
    /// Why the road <paramref name="points"/> between two structures, held at <paramref name="from"/> and
    /// <paramref name="to"/> at its ends, is a stub: shorter than <see cref="StubLength"/> (the stretch under a viaduct's
    /// joining piece apart) and its ends further apart in height than a step (<see cref="StepTolerance"/>), so it is not one
    /// level landing. Null where it is a landing, or long enough to be road.
    /// </summary>
    public static string? IsStub(IReadOnlyList<Vector2> points, float from, float to)
    {
        float length = 0f;
        for (int i = 1; i < points.Count; i++) length += Vector2.Distance(points[i - 1], points[i]);
        if (length >= StubLength + JoinOverlap - 1e-3f) return null;
        if (Mathf.Abs(from - to) <= StepTolerance + 1e-3f) return null;
        return FormattableString.Invariant($"a stub of road {length:F1} m long between two structures {Mathf.Abs(from - to):F1} m apart in height");
    }

    /// <summary>Whether a player walks from road at <paramref name="roadHeight"/> where the road meets
    /// <paramref name="end"/> onto it.</summary>
    public static bool Reachable(float roadHeight, End end) => Mathf.Abs(roadHeight - end.MeetHeight) <= StepTolerance;
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
    /// piece at its meeting height.
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
            if (end.UnderTheJoint(p, halfWidth + w * 0.5f) && Mathf.Abs(h - end.MeetHeight) <= StepTolerance) return false;
        return true;
    }

    /// <summary>
    /// Puts <paramref name="path"/> through the foot of each end's stairs of every viaduct on it, so the road arrives
    /// at the foot along the flight's own line: the in-line case of the staircases' foot rule, the foot centred on the
    /// road. The search buys a viaduct from whatever side its road comes to the rim (a rim is often a crest the road
    /// climbs to on stairs), so the path is turned onto the flight's line here, after the search; the
    /// planner then holds the foot (RoadSpatialGrid.HeldPoints) so the pull and the sway keep it.
    /// <para>From each bank (<see cref="RoadCrossing.FromIndex"/> back, <see cref="RoadCrossing.ToIndex"/> on) the road
    /// is walked away from the deck. Its points beside the flight (nearer the bank than the foot, off the flight's
    /// line) are dropped; the foot goes in before the first point at or behind it, which turns the road onto the line
    /// where that point is off it, and pins the road's curve to the foot where it is on it. Where the road ends beside
    /// or under the flight, or the stretch is a stair step the search bought (<paramref name="isStep"/>: a staircase
    /// is laid from its two ends), the path is left and the end counts in <paramref name="unmet"/>.</para>
    /// Returns how many ends were turned onto the flight's line; <paramref name="pinned"/> counts the feet put in on a
    /// road already on it. The caller finds the crossings on the path again when either is above 0.
    /// </summary>
    public static int MeetTheRoad(List<Vector2> path, IReadOnlyList<RoadCrossing> viaducts, WorldGenerator world,
        out int unmet, out int pinned, Func<Vector2, Vector2, bool>? isStep = null, List<Vector2>? unmetFeet = null)
    {
        int changed = 0;
        unmet = pinned = 0;
        // Last first, far end before near, so a change does not move an index still to do.
        foreach (RoadCrossing crossing in viaducts.OrderByDescending(c => c.FromIndex))
        {
            var (near, far) = BridgeStructures.Ends(crossing, world);
            if (!near.IsSet) continue;
            foreach (var (end, bank, start, step) in new[] { (far, crossing.ToBank, crossing.ToIndex, +1), (near, crossing.FromBank, crossing.FromIndex, -1) })
            {
                int before = path.Count;
                bool? met = Meet(path, end, bank, start, step, isStep);
                if (met == null) { unmet++; unmetFeet?.Add(end.Meet); }
                else if (met == false) changed++;
                else if (path.Count != before) pinned++;
            }
        }
        return changed;
    }

    /// <summary>One end of <see cref="MeetTheRoad"/>: true where the road already ran up the flight's line (the foot
    /// pinned on it), false where the path was turned onto it, null where it cannot be (the path left as it was).</summary>
    private static bool? Meet(List<Vector2> path, StructureEnds.End structureEnd, Vector2 bank, int start, int step, Func<Vector2, Vector2, bool>? isStep)
    {
        // The road comes in along the structure's line over its levelled apron (StructureEnds.ApronLength): its far end is
        // the point the path is put through, and what lies between it and the bank stays only on the line.
        var end = (Up: structureEnd.Up, Foot: structureEnd.Apron);
        Vector2 back = -end.Up, across = new(-back.y, back.x);
        var beside = new List<int>();
        int previous = -1;
        for (int i = start; i >= 0 && i < path.Count; i += step)
        {
            Vector2 q = path[i];
            if (Vector2.Distance(q, bank) < 0.5f) { previous = i; continue; }
            float along = Vector2.Dot(q - end.Foot, back);
            bool onLine = Mathf.Abs(Vector2.Dot(q - end.Foot, across)) <= 1f + Mathf.Max(0f, along) * 0.0875f;   // tan 5 degrees
            bool roadsEnd = i == 0 || i == path.Count - 1;
            if (along < -0.5f)
            {
                // Beside or under the flight: on its line the road runs up it; off it, the point is dropped.
                if (roadsEnd) return null;
                if (!onLine) beside.Add(i);
                if (isStep?.Invoke(path[previous < 0 ? i : previous], q) ?? false) return null;
                previous = i;
                continue;
            }
            if (isStep?.Invoke(path[previous < 0 ? i : previous], q) ?? false) return onLine && beside.Count == 0 ? true : null;
            // Drop the points beside the flight, then put the foot in between this point and the next one in: off the
            // line it turns the road onto it; on it, it pins the road's curve to the foot, which a corner a few metres
            // on otherwise bows off the line (0.6 m at the foot on a test valley).
            foreach (int k in beside.OrderByDescending(k => k)) path.RemoveAt(k);
            int at = step > 0 ? i - beside.Count : i + 1;
            if (Vector2.Distance(path[step > 0 ? at : at - 1], end.Foot) > 0.5f) path.Insert(at, end.Foot);
            return onLine && beside.Count == 0;
        }
        return null;   // the road ends under the flight
    }
}
