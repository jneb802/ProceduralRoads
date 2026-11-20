using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The way onto a placed Location's landing planned as the road's own last stretch: its line (RoadLocationEntry.Route, to
/// RoadLocationEntry.WayInEnd, PlacedLocationShape.Ends) appended to the road and planned from its last structure on by the road's own planner
/// (<see cref="PlanRoadPathWithCrossings"/>, <see cref="PlanFromItsLastStructure"/>). Its water is found and treated as the road's is (RoadCrossingDetector: a wade, a
/// raised ford or a bridge, as the terrain gives), its earthwork and its ridges judged as the road's are (the earthwork cap,
/// RoadRidges), and the joint is smoothed with the road (one piece, one spline). Only its entering the Location is its own: the
/// Location's footprint is judged by its shape (RoadLocationProtection.Targets.Into, RoadLocationEntry.Plan).
/// </summary>
public static partial class RoadNetworkGenerator
{
    /// <summary>
    /// A road's pieces in travel order, for a judge that reads them as one line (RoadRidges, <see cref="AlongARidge"/>): its
    /// <paramref name="plans"/>, and each of <paramref name="extras"/> (its ways onto landings, its ends carried into their
    /// locations) at the end it meets, turned by where it meets it: the end of an extra that meets the road (the nearer of its two
    /// ends to the road's start or end, within RoadRidges.Link: Find's own joining distance) is the one that joins it. One that
    /// meets the road's start goes ahead of the pieces, run toward it (that end last); any other after them, run from the road's
    /// end where it meets it (that end first). Read out of order, a trench across a way in at the road's start was judged as two
    /// short stretches, neither long enough to count; turned by its first point alone, a way in at the start no longer than
    /// RoadRidges.Link (its landing as near the start as its joint) was read folded back on itself.
    /// </summary>
    internal static List<RoadSpatialGrid.PlannedPath> InTravelOrder(List<RoadSpatialGrid.PlannedPath> plans, IEnumerable<RoadSpatialGrid.PlannedPath> extras)
    {
        var before = new List<RoadSpatialGrid.PlannedPath>();
        var after = new List<RoadSpatialGrid.PlannedPath>();
        Vector2? start = plans.Count > 0 && plans[0].Points.Count > 0 ? plans[0].Points[0] : null;
        RoadSpatialGrid.PlannedPath? lastPiece = plans.Count > 0 ? plans[plans.Count - 1] : null;
        Vector2? end = lastPiece != null && lastPiece.Points.Count > 0 ? lastPiece.Points[lastPiece.Points.Count - 1] : null;
        float From(Vector2? at, Vector2 p) => at is Vector2 a ? Vector2.Distance(a, p) : float.PositiveInfinity;
        foreach (RoadSpatialGrid.PlannedPath extra in extras)
        {
            if (extra.Points.Count == 0) continue;
            Vector2 first = extra.Points[0], last = extra.Points[extra.Points.Count - 1];
            float startFirst = From(start, first), startLast = From(start, last), endFirst = From(end, first), endLast = From(end, last);
            float toStart = Mathf.Min(startFirst, startLast), toEnd = Mathf.Min(endFirst, endLast);
            if (toStart <= RoadRidges.Link && toStart <= toEnd) before.Insert(0, startFirst < startLast ? extra.Reversed() : extra);
            else after.Add(toEnd <= RoadRidges.Link && endLast < endFirst ? extra.Reversed() : extra);
        }
        if (before.Count == 0 && after.Count == 0) return plans;
        var all = new List<RoadSpatialGrid.PlannedPath>(before);
        all.AddRange(plans);
        all.AddRange(after);
        return all;
    }

    /// <summary>
    /// The road a way onto a placed Location's landing is planned on as its last stretch (<see cref="PlanWayIn"/>): its path,
    /// pieces, staircases and crossings as they stand, the waypoints <see cref="Shift"/> cut from its start since its staircases
    /// and crossings were indexed, which end is at the Location, and the ground its other end was planned to meet. A road of
    /// its end alone (<see cref="Alone"/>) where the way in is planned with no road (a way in asked on its own).
    /// </summary>
    internal sealed class WayInRoad
    {
        public List<Vector2> Path = null!;
        public List<RoadSpatialGrid.PlannedPath> Plans = null!;
        public List<RoadStairSpan> Stairs = null!;
        public List<RoadCrossing> Crossings = null!;
        public bool AtStart;
        public int Shift;
        public float? FarGround;
        /// <summary>The locations its pieces were planned between (its first piece's start, its last piece's end).</summary>
        public RoadLocationProtection.Targets Locations;

        public static WayInRoad Alone(LocationApproachRequest request) => new()
        {
            Path = new List<Vector2> { request.RoadEnd }, Plans = new List<RoadSpatialGrid.PlannedPath>(), Stairs = new List<RoadStairSpan>(),
            Crossings = new List<RoadCrossing>(), FarGround = request.RoadHeight,
        };

        public static WayInRoad Of(bool atStart, List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs,
            List<RoadCrossing> crossings, int shift, float? farGround) => new()
        {
            Path = path, Plans = plans, Stairs = stairs, Crossings = crossings, AtStart = atStart, Shift = shift, FarGround = farGround,
            Locations = plans.Count > 0 ? new RoadLocationProtection.Targets(plans[0].Locations.Start, plans[plans.Count - 1].Locations.End) : default,
        };
    }

    /// <summary>
    /// The way onto a placed Location's landing searched by the road's own pathfinder (this thread's, <see cref="Finder"/>), from
    /// the road's end at <paramref name="from"/> to its end on the landing at <paramref name="to"/>, as a plain road
    /// (RoadPathfinder.FindRoadAside: no staircase, no viaduct, the road's search left as it was), into <paramref name="location"/>
    /// alone: the line RoadLocationEntry.Plan tries first where the straight line in crosses a crest or a valley. Null with no
    /// search on this thread, or none found.
    /// </summary>
    internal static List<Vector2>? SearchWayIn(Vector2 from, Vector2 to, Vector2 location) =>
        Finder?.FindRoadAside(from, to, new RoadLocationProtection.Targets(location, location, location));

    /// <summary>
    /// The road with its way onto the landing planned as its last stretch (<see cref="PlanWayIn"/>), not yet taken: its new path,
    /// pieces (the way in not among them: <see cref="Entry"/>) and crossings, and the index moves its staircases and kept
    /// crossings take with it (<see cref="Apply"/>).
    /// </summary>
    internal sealed class WayInPlan
    {
        public RoadSpatialGrid.PlannedPath Entry = null!;
        /// <summary>The road's piece the way in was split from, as far as it is the road's (null where the way in is that whole piece).</summary>
        public RoadSpatialGrid.PlannedPath? Head;
        public List<Vector2> Path = null!;
        public List<RoadSpatialGrid.PlannedPath> Plans = null!;
        public List<RoadCrossing> Crossings = null!;
        internal List<RoadCrossing> Kept = null!, Found = null!;
        internal int KeptBy, FoundBy;

        /// <summary>The road's end at its Location, where the way in starts.</summary>
        public Vector2 End(bool atStart) => Path[atStart ? 0 : Path.Count - 1];

        /// <summary>Take it: <paramref name="path"/>, <paramref name="plans"/> and <paramref name="crossings"/> replaced, the
        /// staircases' and the kept crossings' indices moved onto the new path.</summary>
        public void Apply(List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs, List<RoadCrossing> crossings)
        {
            foreach (RoadStairSpan s in stairs) { s.FromIndex += KeptBy; s.ToIndex += KeptBy; }
            foreach (RoadCrossing c in Kept) { c.FromIndex += KeptBy; c.ToIndex += KeptBy; }
            foreach (RoadCrossing c in Found) { c.FromIndex += FoundBy; c.ToIndex += FoundBy; }
            var newPath = new List<Vector2>(Path);
            var newPlans = new List<RoadSpatialGrid.PlannedPath>(Plans);
            var newCrossings = new List<RoadCrossing>(Crossings);
            path.Clear(); path.AddRange(newPath);
            plans.Clear(); plans.AddRange(newPlans);
            crossings.Clear(); crossings.AddRange(newCrossings);
        }
    }

    /// <summary>
    /// The way onto a placed Location's landing along <paramref name="line"/> (from the road's end, RoadLocationEntry.Route) as the
    /// road's own last stretch, ending held at <paramref name="landing"/>: the line appended to <paramref name="road"/>'s path a
    /// search cell a step (RoadPathfinder.CellSize), its crossings found on it by the road's own detector (RoadCrossingDetector.Detect,
    /// the road's wade or ford that ran to its end found again from its far bank, on to the landing), snapped and their ends met
    /// as the road's are, and the road planned again by its own planner from its last structure on (<see cref="PlanFromItsLastStructure"/>: before it
    /// the road is kept as it was planned); into <paramref name="location"/>'s footprint the Location's shape judges it
    /// (RoadLocationProtection.Targets.Into), every other location's as any road's. Refused, null with <paramref name="why"/>,
    /// where it is not planned or its crossings may not be built, or its earthwork passes the cap. The way in returned is the
    /// road's last planned stretch back as far as the line is long (all of its last piece where that is shorter: a way in past
    /// its own ford), its road (<paramref name="planned"/>) the rest. Nothing of <paramref name="road"/> is changed until
    /// <see cref="WayInPlan.Apply"/>.
    /// </summary>
    internal static RoadSpatialGrid.PlannedPath? PlanWayIn(WayInRoad road, List<Vector2> line, float landing, Vector2 location, float width,
        out string why, out WayInPlan? planned)
    {
        why = "";
        planned = null;
        bool atStart = road.AtStart;
        List<Vector2> path = road.Path;
        int n = path.Count;
        // The way's points a search cell apart, its first after the road's end.
        var way = new List<Vector2>();
        float wayLength = 0f;
        for (int k = 1; k < line.Count; k++)
        {
            float d = Vector2.Distance(line[k - 1], line[k]);
            wayLength += d;
            int cells = Mathf.Max(1, Mathf.CeilToInt(d / RoadPathfinder.CellSize));
            for (int c = 1; c <= cells; c++) way.Add(Vector2.Lerp(line[k - 1], line[k], c / (float)cells));
        }
        if (way.Count == 0) { why = "no way in"; return null; }
        // The road's crossings that run to its end at the Location (a wade to its ring end) are found again with the way, on the
        // road's stretch since its last other structure (so a crossing found on the way has the road before it to meet its end
        // on). Every other crossing, and every staircase, is kept as it was met.
        int Index(int i) => i - road.Shift;
        var tail = road.Crossings.FindAll(c => atStart ? Index(c.FromIndex) <= 0 : Index(c.ToIndex) >= n - 1);
        var kept = road.Crossings.FindAll(c => !tail.Contains(c));
        int from = atStart ? n - 1 : 0;
        foreach (RoadCrossing c in kept) from = atStart ? Mathf.Min(from, Index(c.FromIndex)) : Mathf.Max(from, Index(c.ToIndex));
        foreach (RoadStairSpan s in road.Stairs) from = atStart ? Mathf.Min(from, Index(s.FromIndex)) : Mathf.Max(from, Index(s.ToIndex));
        from = Mathf.Clamp(from, 0, n - 1);
        // The stretch its crossings are found on: from that point of the road onto the landing, in the road's direction.
        List<Vector2> stretch;
        if (atStart)
        {
            stretch = new List<Vector2>(way);
            stretch.Reverse();
            stretch.AddRange(path.GetRange(0, from + 1));
        }
        else
        {
            stretch = path.GetRange(from, n - from);
            stretch.AddRange(way);
        }
        // Its water found, met and planned as a road's is, through generation's own treatment loop (EachTreatment, PlanCandidate's):
        // the road's detector with the search's viaduct jumps (DetectCrossings), snapped, every end met with the search's stair
        // steps and its moved-viaduct test (MeetEnds), and a failed shallow pool's treatment refused for the next eligible one. Each
        // attempt starts from the stretch as found: a refused treatment's aprons and moved banks are not the next one's input.
        RoadPathfinder? finder = Finder;
        List<Vector2> searched = stretch;
        var found = new List<RoadCrossing>();
        var joined = new List<Vector2>();
        var crossings = new List<RoadCrossing>();
        int keptBy = 0, foundBy = 0;
        List<RoadSpatialGrid.PlannedPath>? plans = null;
        string? refusal = null;
        // For the log: the first round's refusal, which a later round's (a refused treatment's next, often the shallow water's)
        // would hide.
        string? firstRefusal = null;
        // Whether the found crossings' indices stand moved onto the joined path: put back on every refusal past that (Back).
        bool foundMoved = false;
        RoadSpatialGrid.PlannedPath? Back()
        {
            if (foundMoved) foreach (RoadCrossing c in found) { c.FromIndex -= foundBy; c.ToIndex -= foundBy; }
            foundMoved = false;
            return null;
        }
        void Move(int by) { foreach (RoadStairSpan s in road.Stairs) { s.FromIndex += by; s.ToIndex += by; } foreach (RoadCrossing c in kept) { c.FromIndex += by; c.ToIndex += by; } }
        EachTreatment((refused, failed) =>
        {
            firstRefusal ??= refusal;
            plans = null;
            foundMoved = false;
            stretch = new List<Vector2>(searched);
            found = DetectCrossings(finder, stretch, refused);
            SnapToExistingCrossings(found);
            // A crossing the road keeps found again (the same site, SameSite): its own, met as the road was planned and built so,
            // never met again here. From a kept crossing's end standing over its water (an end met by earthwork) the detector
            // measures that pool whole, back across the crossing to its other bank, off this stretch: met again there, its end
            // had no road before it and the way in was refused for a crossing outside its stretch (in game 1 km back).
            found.RemoveAll(c => kept.Exists(k => SameSite(k, c)));
            if (refused.Count > 0 && found.Exists(c => c.Shallow && c.Invalid)) { refusal = ShallowWaterRefusal; return false; }
            if (found.Count > 0 && MeetEnds(finder, stretch, found, width) is RoadCrossing unmet)
            {
                failed.Add(unmet);
                refusal = FormattableString.Invariant($"{EndNotMet} at ({unmet.FromBank.x:F1}, {unmet.FromBank.y:F1})");
                return true;
            }
            if (InvalidCrossings(found, null) > 0) { refusal = found.Exists(c => c.Invalid && c.Shallow) ? ShallowWaterRefusal : DeckThroughGroundRefusal; return false; }
            // The joined path, and how far the kept structures' and the found crossings' indices move onto it.
            joined = new List<Vector2>();
            if (atStart)
            {
                joined.AddRange(stretch);
                joined.AddRange(path.GetRange(from + 1, n - from - 1));
                keptBy = stretch.Count - 1 - from - road.Shift;
                foundBy = 0;
            }
            else
            {
                joined.AddRange(path.GetRange(0, from));
                joined.AddRange(stretch);
                keptBy = -road.Shift;
                foundBy = from;
            }
            foreach (RoadCrossing c in found) { c.FromIndex += foundBy; c.ToIndex += foundBy; }
            foundMoved = true;
            crossings = new List<RoadCrossing>(kept);
            crossings.AddRange(found);
            foreach (RoadCrossing c in found) c.Searched = SearchedMoves(joined, c);
            Move(keptBy);
            // Sorted once every index stands on the joined path (the kept crossings moved by Move, the found by foundBy).
            crossings.Sort((a, b) => a.FromIndex.CompareTo(b.FromIndex));
            try
            {
                plans = RoadGrade.Trial(() => PlanFromItsLastStructure(road, joined, crossings, kept, width, landing, location, failed));
            }
            finally { Move(-keptBy); }
            // Only the way's own crossings are treated again: the road's kept ones were met as it was planned.
            failed.RemoveAll(c => !found.Contains(c));
            if (plans == null || plans.Count == 0) { plans = null; refusal = RoadSpatialGrid.LastRefusal ?? "cannot be built"; return true; }
            refusal = null;
            return false;
        });
        if (refusal != null || plans == null)
        {
            why = refusal ?? "cannot be built";
            if (firstRefusal != null && firstRefusal != why) why += " (first round: " + firstRefusal + ")";
            return Back();
        }
        // The way in: the road's last planned stretch back as far as the way is long, from its end at the Location.
        RoadSpatialGrid.PlannedPath last = atStart ? plans[0] : plans[plans.Count - 1];
        int m = last.Points.Count;
        if (m < 2) { why = "no way in"; return Back(); }
        int split = atStart ? 0 : m - 1;
        float walked = 0f;
        if (atStart) while (split < m - 1 && walked < wayLength) { walked += Vector2.Distance(last.Points[split], last.Points[split + 1]); split++; }
        else while (split > 0 && walked < wayLength) { walked += Vector2.Distance(last.Points[split - 1], last.Points[split]); split--; }
        // And back to where it enters the Location's footprint, where that is further: everything of it in the footprint is its
        // way in, judged by the Location's shape and stored into the Location; the road before it keeps out of the footprint.
        var own = new RoadLocationProtection.Targets(null, location);
        bool Inside(Vector2 p) => own.Holding(p) is Vector2 h && h == location;
        if (atStart) while (split < m - 1 && Inside(last.Points[split + 1])) split++;
        else while (split > 0 && Inside(last.Points[split - 1])) split--;
        // A wade that runs onto the landing is the last piece (the bed's own paint, no structure): it is the way in whole, the road
        // ending at its near bank and the crossing its way in's.
        if (last.FollowTerrain)
        {
            RoadCrossing? onto = crossings.Find(c => atStart ? c.FromIndex <= 0 : c.ToIndex >= joined.Count - 1);
            if (onto == null || onto.Kind != CrossingKind.Ford || onto.Style != FordStyle.Wade)
            { why = "a way in ending on a crossing that is no wade"; return Back(); }
            split = atStart ? m - 1 : 0;
            crossings.Remove(onto);
            kept.Remove(onto);
            found.Remove(onto);
        }
        RoadSpatialGrid.PlannedPath entry = atStart ? last.Slice(0, split) : last.Slice(split, m - 1);
        RoadSpatialGrid.PlannedPath? head = atStart ? (split < m - 1 ? last.Slice(split, m - 1) : null) : (split > 0 ? last.Slice(0, split) : null);
        var roadPlans = new List<RoadSpatialGrid.PlannedPath>(plans);
        roadPlans.RemoveAt(atStart ? 0 : roadPlans.Count - 1);
        if (head != null) roadPlans.Insert(atStart ? 0 : roadPlans.Count, head);
        bool alone = road.Plans.Count == 0;
        if (roadPlans.Count == 0 && !alone) { why = "no road before its way in"; return Back(); }
        // The road before it keeps out of the footprint, but for the piece it goes on from where that runs on into the footprint up
        // to it (a ford of the road's own onto the landing, its bank inside the footprint): that run, as the way in, is judged by the
        // Location's shape (RoadLocationEntry.PlanTo, on the road within the lead-round ring).
        RoadSpatialGrid.PlannedPath? goesOnFrom = roadPlans.Count > 0 ? roadPlans[atStart ? 0 : roadPlans.Count - 1] : null;
        Vector2 entryStart = entry.Points[atStart ? entry.Points.Count - 1 : 0];
        foreach (RoadSpatialGrid.PlannedPath p in roadPlans)
        {
            if (p.FollowTerrain) continue;
            int inside = p.Points.FindIndex(Inside);
            if (inside < 0) continue;
            bool runsOnto = ReferenceEquals(p, goesOnFrom) && Vector2.Distance(p.Points[atStart ? 0 : p.Points.Count - 1], entryStart) <= RoadRidges.Link
                && (atStart ? p.Points.Take(p.Points.FindLastIndex(Inside) + 1).All(Inside) : p.Points.Skip(inside).All(Inside));
            if (!runsOnto)
            { why = FormattableString.Invariant($"into its Location before its way in at ({p.Points[inside].x:F1}, {p.Points[inside].y:F1})"); return Back(); }
        }
        // The road's path ends at the joint: its waypoint nearest it past every structure, put there.
        Vector2 joint = last.Points[split];
        int structures = atStart ? joined.Count - 1 : 0;
        // Every index in the joined path's: the kept crossings and the staircases moved by keptBy, the found crossings already on it.
        foreach (RoadCrossing c in crossings)
        {
            int by = kept.Contains(c) ? keptBy : 0;
            structures = atStart ? Mathf.Min(structures, c.FromIndex + by) : Mathf.Max(structures, c.ToIndex + by);
        }
        foreach (RoadStairSpan s in road.Stairs) structures = atStart ? Mathf.Min(structures, s.FromIndex + keptBy) : Mathf.Max(structures, s.ToIndex + keptBy);
        int at = structures;
        for (int i = atStart ? 0 : structures; atStart ? i <= structures : i < joined.Count; i++)
            if ((joined[i] - joint).sqrMagnitude < (joined[at] - joint).sqrMagnitude) at = i;
        List<Vector2> newPath = atStart ? joined.GetRange(at, joined.Count - at) : joined.GetRange(0, at + 1);
        newPath[atStart ? 0 : newPath.Count - 1] = joint;
        if (newPath.Count < 2 && !alone) { why = "no road before its way in"; return Back(); }
        // The road and its way in judged by the one list's own assembly of a road's shape (ShapeRefusal, as the final check asks it:
        // its earthwork, its crossings, its junction ridges, a ridge or trench along it and a step no one walks up, the road's
        // pieces and its way in read in travel order): a way in from a start in a trench, or with a fold or joint the final check
        // would refuse, moves on to the next start instead of refusing the road. The road's ends carried into flatless Locations
        // are planned only after its ways in (RoadNetworkGenerator.PlanLocationEnds): the final check judges them with it.
        if (ShapeRefusal(roadPlans, new[] { entry }, crossings, null, LinesAtEnds(roadPlans, crossings, road.Stairs)) is Refusal shaped)
        {
            string said = shaped.Reason == WalkStepRefusal ? shaped.Said : shaped.Reason;
            why = shaped.Reason == JunctionRidgeRefusal || shaped.Reason == ShallowWaterRefusal || shaped.Reason == DeckThroughGroundRefusal ? said
                : FormattableString.Invariant($"{said} at ({shaped.At.x:F1}, {shaped.At.y:F1})");
            return Back();
        }
        int cut = atStart ? at : 0;
        planned = new WayInPlan
        {
            Entry = entry, Head = head, Path = newPath, Plans = roadPlans, Crossings = crossings, Kept = kept, Found = found,
            KeptBy = keptBy - cut, FoundBy = -cut,
        };
        // The found crossings' indices back on the stretch until taken (Apply moves them on).
        foreach (RoadCrossing c in found) { c.FromIndex -= foundBy; c.ToIndex -= foundBy; }
        planned.FoundBy += foundBy;
        return entry;
    }

    /// <summary>
    /// The road with its way in (<paramref name="joined"/>, its <paramref name="crossings"/> and <paramref name="road"/>'s staircases
    /// indexed on it) planned again only from its last structure on: the crossing or staircase nearest its end at the Location, the
    /// one the stretch its crossings are found on starts from (<see cref="PlanWayIn"/>). That structure and everything past it to
    /// the landing is planned by the road's own planner (<see cref="PlanRoadPathWithCrossings"/>), the structure holding the joint
    /// where it always does (its end's foot, height and line, StructureEnds); every piece before it is the road's as it was
    /// planned (told apart by the pieces' own marks, RoadSpatialGrid.PlannedPath.CrossingAtStart, CrossingAtEnd, StairAtStart,
    /// StairAtEnd). A road with no structure, or whose pieces do not show where it is met, is planned again whole, its other end
    /// held as it was planned. Re-planned whole, a road far from its Location was changed by its way in (a trench at a bridge's
    /// end 370 m away, a protected location crossed elsewhere).
    /// </summary>
    private static List<RoadSpatialGrid.PlannedPath>? PlanFromItsLastStructure(WayInRoad road, List<Vector2> joined, List<RoadCrossing> crossings, List<RoadCrossing> kept,
        float width, float landing, Vector2 location, ICollection<RoadCrossing>? failed = null)
    {
        bool atStart = road.AtStart;
        List<RoadSpatialGrid.PlannedPath>? Whole() => PlanRoadPathWithCrossings(joined, crossings, width, atStart ? landing : road.FarGround,
            atStart ? road.FarGround : landing, road.Stairs, default,
            atStart ? new RoadLocationProtection.Targets(location, road.Locations.End, location) : new RoadLocationProtection.Targets(road.Locations.Start, location, location), failed);
        // The structure nearest the Location end: the road's own crossings kept as they were met, and its staircases.
        RoadCrossing? lastCrossing = null;
        RoadStairSpan? lastStair = null;
        int from = 0, to = 0;
        bool found = false;
        void Consider(int a, int b, RoadCrossing? c, RoadStairSpan? s)
        {
            if (found && (atStart ? a >= from : b <= to)) return;
            (from, to, lastCrossing, lastStair, found) = (a, b, c, s, true);
        }
        foreach (RoadCrossing c in crossings)
            if (kept.Contains(c)) Consider(c.FromIndex, c.ToIndex, c, null);
        foreach (RoadStairSpan s in road.Stairs) Consider(s.FromIndex, s.ToIndex, null, s);
        if (!found || road.Plans.Count == 0) return Whole();
        // A structure that reaches across it toward the road (two fords whose banks overlap) is planned again with it, and the
        // road is kept from that one on.
        bool Across(int a, int b) => atStart ? a < to && b > to : b > from && a < from;
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (RoadCrossing c in crossings)
                if (kept.Contains(c) && Across(c.FromIndex, c.ToIndex)) { (from, to, lastCrossing, lastStair, grew) = (atStart ? from : c.FromIndex, atStart ? c.ToIndex : to, c, null, true); }
            foreach (RoadStairSpan s in road.Stairs)
                if (Across(s.FromIndex, s.ToIndex)) { (from, to, lastCrossing, lastStair, grew) = (atStart ? from : s.FromIndex, atStart ? s.ToIndex : to, null, s, true); }
        }
        bool Past(int a, int b) => atStart ? b <= to : a >= from;
        // The road's pieces up to the structure: those that meet it on the road's side (a ford's own stretch goes with what is
        // planned again).
        bool MeetsIt(RoadSpatialGrid.PlannedPath p, bool atItsStart) => lastCrossing != null
            ? (atItsStart ? p.CrossingAtStart == lastCrossing && p.CrossingAtEnd != lastCrossing : p.CrossingAtEnd == lastCrossing && p.CrossingAtStart != lastCrossing)
            : (atItsStart ? p.StairAtStart?.Stair : p.StairAtEnd?.Stair) == lastStair;
        int keep = atStart ? road.Plans.FindIndex(p => MeetsIt(p, true)) : road.Plans.FindLastIndex(p => MeetsIt(p, false));
        if (keep < 0) return Whole();
        // The structure and everything past it, its indices on that stretch.
        int start = atStart ? 0 : from;
        List<Vector2> stretch = atStart ? joined.GetRange(0, to + 1) : joined.GetRange(from, joined.Count - from);
        var onIt = crossings.FindAll(c => Past(c.FromIndex, c.ToIndex));
        var stairsOnIt = road.Stairs.FindAll(s => Past(s.FromIndex, s.ToIndex));
        void Move(int by) { foreach (RoadCrossing c in onIt) { c.FromIndex += by; c.ToIndex += by; } foreach (RoadStairSpan s in stairsOnIt) { s.FromIndex += by; s.ToIndex += by; } }
        List<RoadSpatialGrid.PlannedPath>? again;
        Move(-start);
        // A stretch that runs to the road's other end (its last structure the road's first) keeps the Location that end is at, as the
        // road's own piece there was planned with it (PlanPieces, as Whole above): its ring end inside that Location's clearance.
        Vector2? other = atStart ? (to >= joined.Count - 1 ? road.Locations.End : null) : (start == 0 ? road.Locations.Start : null);
        try
        {
            again = PlanRoadPathWithCrossings(stretch, onIt, width, atStart ? landing : null, atStart ? null : landing, stairsOnIt, default,
                atStart ? new RoadLocationProtection.Targets(location, other, location) : new RoadLocationProtection.Targets(other, location, location), failed);
        }
        finally { Move(start); }
        if (again == null || again.Count == 0) return null;
        // Of it, the structure's own stretch and what is past it: from where it meets the structure on the Location's side.
        int on = atStart
            ? again.FindLastIndex(p => lastCrossing != null ? p.CrossingAtEnd == lastCrossing : p.StairAtEnd?.Stair == lastStair)
            : again.FindIndex(p => lastCrossing != null ? p.CrossingAtStart == lastCrossing : p.StairAtStart?.Stair == lastStair);
        if (on < 0) return Whole();
        var plans = new List<RoadSpatialGrid.PlannedPath>();
        if (atStart)
        {
            plans.AddRange(again.GetRange(0, on + 1));
            plans.AddRange(road.Plans.GetRange(keep, road.Plans.Count - keep));
        }
        else
        {
            plans.AddRange(road.Plans.GetRange(0, keep + 1));
            plans.AddRange(again.GetRange(on, again.Count - on));
        }
        // The seam this re-plan makes, the road's kept piece against the stretch planned again (a ford's waded or raised stretch
        // planned again from its bank, its end's height its own): judged as PlanPieces judges the seams of one plan (FordSeamMeets).
        // Where they do not meet, the road is planned again whole, the two pieces in one plan, which judges that seam itself.
        int seam = atStart ? on + 1 : keep + 1;
        if (!FordSeamMeets(plans[seam - 1], plans[seam], out float rise))
        {
            if (RoadsLog) Log.LogDebug(FormattableString.Invariant(
                $"[WAYIN] a road kept to its last structure meets its stretch planned again {rise:F2} m apart at ({plans[seam].Points[0].x:F1}, {plans[seam].Points[0].y:F1}): planned again whole"));
            return Whole();
        }
        return plans;
    }
}
