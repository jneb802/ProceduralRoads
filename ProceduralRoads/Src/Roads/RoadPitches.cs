using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Climbs as pitches and short easy stretches, with variety, instead of one
/// steady grade the whole way. The shape follows trail-building practice (a
/// sustained grade and how often rests come).
///
/// On each climb an ease of <see cref="EaseMin"/>..<see cref="EaseMax"/>
/// metres at <see cref="EaseGrade"/> comes every
/// <see cref="EaseEveryMin"/>..<see cref="EaseEveryMax"/> metres, lengths
/// varied by world-position noise; every other edge keeps the road's cap
/// (<see cref="RoadGrade.Configured"/>): no edge is ever allowed past it, so
/// the grade limiter holds the eases and pushes their height into the edges
/// between them at the cap at most. A climb with too little room for its eases
/// under the cap keeps them and refuses its road (<see cref="ClimbsTooSteepForRests"/>).
/// The route search pays for climbing above the
/// sustained grade (<see cref="OverSustainedPrice"/>) rather than refusing it,
/// so gentle climbs are preferred and a clearly better steep line can still
/// win. A road that joins another ends on a level landing
/// (<see cref="JunctionLanding"/>). The fields are settable for tests.
///
/// The one exception to the cap is the last resort (<see cref="AsLastResort{T}"/>): a road the normal flow refused for a climb
/// too steep for its rests is planned once more, through the same planner and refusal flow, and there such a climb may take
/// ONE pitch: up to <see cref="PitchLength"/> metres at up to <see cref="PitchGrade"/>, between two of its rests, every rest
/// kept. Pitches are budgeted per world (<see cref="PitchBudget"/>, shared between islands by their destinations:
/// <see cref="BeginIsland"/>) and spread out (<see cref="PitchSpacing"/>), judged on the road as it is committed
/// (<see cref="PitchRefusal"/>) and recorded once it is stored (<see cref="Record"/>). The search never prices one in.
/// </summary>
public static class RoadPitches
{
    /// <summary>Off, a climb is held to the road's cap alone. Settable for tests.</summary>
    internal static bool Enabled = true;
    /// <summary>Climbing above this pays <see cref="PitchOverWeight"/> per metre per unit of grade over it.</summary>
    internal static float Sustained = 0.30f;
    internal static float EaseEveryMin = 12f;
    internal static float EaseEveryMax = 25f;
    internal static float EaseMin = 4f;
    internal static float EaseMax = 8f;
    internal static float EaseGrade = 0.09f;
    /// <summary>A run whose grade over <see cref="Window"/> metres stays above this is a climb.</summary>
    internal static float MinGrade = 0.12f;
    internal static float PitchOverWeight = 5f;
    internal const float Window = 20f;

    /// <summary>A last-resort pitch's steepest grade (the old pitch rule's).</summary>
    internal static float PitchGrade = 0.5f;
    /// <summary>A last-resort pitch's longest run (the old pitch rule's <c>PitchLength</c>).</summary>
    internal static float PitchLength = 8f;
    /// <summary>The metres of pitch a whole world may store (under 1 km per world).</summary>
    internal static float PitchBudget = 1000f;
    /// <summary>The least distance between any two pitches of an island's network.</summary>
    internal static float PitchSpacing = 200f;
    /// <summary>Why a road is refused whose pitches would take its island past its share of the world's pitch budget.</summary>
    internal const string PitchBudgetRefusal = "grade: a pitch past the world's pitch budget";
    /// <summary>Why a road is refused with a pitch nearer than <see cref="PitchSpacing"/> to another.</summary>
    internal const string PitchSpacingRefusal = "grade: a pitch too near another pitch";

    [System.ThreadStatic] private static int t_lastResort;
    [System.ThreadStatic] private static string? t_lastResortFor;
    [System.ThreadStatic] private static string? t_lastPlannedFor;
    [System.ThreadStatic] private static int t_restsRefusals;
    /// <summary>On while a road is planned as the last resort on this thread (<see cref="AsLastResort{T}"/>): a climb too
    /// steep for its rests may take one pitch.</summary>
    internal static bool LastResort => t_lastResort > 0;
    /// <summary>What the last resort running on this thread is for (a structure's end, a destination), for the log.</summary>
    internal static string? LastResortFor => t_lastResortFor;
    /// <summary>Climbs found too steep for their rests on this thread outside the last resort: a road's flow compares it
    /// before and after to know whether the last resort may help it.</summary>
    internal static int RestsRefusals => t_restsRefusals;
    /// <summary>A remembered plan's climbs too steep for their rests (RoadSpatialGrid's remembered pieces), counted as planned.</summary>
    internal static void CountRestsRefusals(int n) => t_restsRefusals += n;

    /// <summary><paramref name="plan"/> run as the last resort (<see cref="LastResort"/>), for <paramref name="what"/>.</summary>
    internal static T AsLastResort<T>(string what, System.Func<T> plan)
    {
        string? was = t_lastResortFor;
        t_lastResort++;
        t_lastResortFor = what;
        try { return plan(); }
        finally { t_lastResort--; t_lastResortFor = was; t_lastPlannedFor = what; }
    }

    /// <summary>What the last resort run last on this thread was for, kept past its end for the road it planned (committed
    /// later, RoadNetworkGenerator.GenerateRoadCore), until <see cref="ForgetLastResort"/> at the next road.</summary>
    internal static string? LastResortRunFor => t_lastResortFor ?? t_lastPlannedFor;
    internal static void ForgetLastResort() => t_lastPlannedFor = null;

    private static int s_climbsTooSteepForRests;
    /// <summary>Climbs too steep for their rests: the edges between the rests, at the cap, cannot make the climb's height.
    /// A rest is never dropped for height; such a climb refuses its road (<see cref="Landing.TooSteepForRests"/>). Trials count too.</summary>
    public static int ClimbsTooSteepForRests => s_climbsTooSteepForRests;
    public static void ResetCounters()
    {
        s_climbsTooSteepForRests = 0;
        s_pitchClimbs = 0;
        s_shares.Clear();
        lock (s_stored) s_stored.Clear();
    }

    /// <summary>The points of the last climb on this thread too steep for its rests (<see cref="PlacePitches"/>), for the
    /// planner's refusal (RoadSpatialGrid.RefusedAt).</summary>
    [System.ThreadStatic] internal static List<Vector2>? t_tooSteepForRests;
    internal static List<Vector2>? LastTooSteepForRests => t_tooSteepForRests;

    /// <summary>The price of one search step over the sustained grade.</summary>
    public static float OverSustainedPrice(float slope, float distance) =>
        Enabled && PitchOverWeight > 0f && slope > Sustained ? PitchOverWeight * (slope - Sustained) * distance : 0f;

    /// <summary>
    /// The last metres of a road that joins another road are a level landing
    /// at the joined road's height, so the two meet level instead of one
    /// arriving tilted. The landing counts as an ease.
    /// </summary>
    internal static float JunctionLanding = 6f;
    public const float JunctionLandingGrade = 0.05f;

    /// <summary>
    /// How much a road ending at a staircase's foot climbs to it over its last <paramref name="run"/> metres, as it is built
    /// there (RoadSpatialGrid.StairStarts, <see cref="LandEnd"/>): its level landing (<see cref="JunctionLanding"/> at
    /// <see cref="JunctionLandingGrade"/>), then <paramref name="cap"/>; with no landing, <paramref name="cap"/> all the way.
    /// The one prediction of "the road can climb to this foot": a foot's road was read climbing at the cap right up to it,
    /// where its last 6 m rise 0.3 m. No edge is allowed past the cap, beside the landing or anywhere.
    /// The one owner of the road grade cap, pitches, and the landing at a road's end (docs/OWNERS.md).
    /// </summary>
    public static float ClimbToFoot(float run, float cap)
    {
        if (JunctionLanding <= 0f || !RoadGrade.Capped(cap)) return cap * run;
        float landing = Mathf.Min(run, JunctionLanding);
        return JunctionLandingGrade * landing + cap * (run - landing);
    }

    /// <summary>Hold the edges of a joined end's landing to the landing grade: the <paramref name="length"/> metres that
    /// begin <paramref name="startFrom"/> (<paramref name="endFrom"/>) metres along the road from its start (end), where it
    /// leaves the stretch it shares with the road it meets (<see cref="SharedRun"/>; 0, the end itself, where it shares none);
    /// returns the (possibly new) array. The one owner of the road grade cap, pitches, and the landing at a road's end (docs/OWNERS.md).</summary>
    public static float[] ApplyJunctionLanding(IReadOnlyList<Vector2> points, float[]? edgeGrades, float cap,
        bool atStart, bool atEnd, float length, float startFrom = 0f, float endFrom = 0f)
    {
        int n = points.Count;
        var result = edgeGrades ?? Filled(n, cap);
        var s = new float[n];
        for (int i = 1; i < n; i++) s[i] = s[i - 1] + Vector2.Distance(points[i - 1], points[i]);
        for (int i = 1; i < n; i++)
        {
            bool nearStart = atStart && s[i - 1] < startFrom + length && (startFrom <= 0f || s[i] > startFrom);
            bool nearEnd = atEnd && s[n - 1] - s[i] < endFrom + length && (endFrom <= 0f || s[n - 1] - s[i - 1] > endFrom);
            if (nearStart || nearEnd) result[i] = Mathf.Min(result[i], JunctionLandingGrade);
        }
        return result;
    }

    /// <summary>How far along a road, from its start (<paramref name="atStart"/>) or its end, the stretch it shares with a
    /// road it meets runs: its points from that end on, while each lies on that road's own surface (<paramref name="shared"/>,
    /// RoadSpatialGrid.OnMetRoads), to the last of them; 0 where the end is not on one. Those edges are the met road's own, not
    /// this road's: a joined end's landing begins where the road leaves them (<see cref="ApplyJunctionLanding"/>).</summary>
    internal static float SharedRun(IReadOnlyList<Vector2> points, IReadOnlyList<bool>? shared, bool atStart)
    {
        int n = shared == null ? 0 : Mathf.Min(points.Count, shared.Count);
        float run = 0f;
        for (int k = 1; k < n; k++)
        {
            int a = atStart ? k - 1 : n - k, b = atStart ? k : n - 1 - k;
            if (!shared![a] || !shared[b]) break;
            run += Vector2.Distance(points[a], points[b]);
        }
        return run;
    }

    /// <summary>What <see cref="AcceptLanding"/> found for the level landings at a road's ends.</summary>
    internal enum Landing
    {
        /// <summary>Every end asked for ends on its level landing.</summary>
        Landed,
        /// <summary>A staircase's foot has no room for its level landing within the cap: refused (a road never runs its own
        /// grade into a foot).</summary>
        NoFootLanding,
        /// <summary>The ends cannot be joined within the cap with the landings they must have: refused.</summary>
        TooSteep,
        /// <summary>A climb cannot keep its rests within the cap (<see cref="PlacePitches"/>): refused, never built without them.</summary>
        TooSteepForRests,
    }

    /// <summary>
    /// The one acceptance of the level landings at a road's ends (<see cref="JunctionLanding"/>), for a road as it is planned
    /// (RoadSpatialGrid.PlanRoadPathCore) and for one levelled again once a staircase is laid from its end
    /// (<see cref="LandEnd"/>): the same points, heights and constraints get the same verdict whoever asks, early or late.
    /// The constraints are explicit inputs, never read off the road's slopes: <paramref name="required"/> ends land (a join's,
    /// a location's) or the road is <see cref="Landing.TooSteep"/>; <paramref name="feet"/> ends (where a staircase starts)
    /// land too, or the road is <see cref="Landing.NoFootLanding"/>: a road that cannot meet a landing rule is refused, never
    /// kept at its own grade into the foot (BRIEF-RULES 17). <paramref name="planned"/>: the per-edge limits the road is planned under before any landing (its turn
    /// landings), null for none. The pitches are placed on the profile at the cap (<see cref="PlacePitches"/>, the landing
    /// a rest; no edge past the cap), their eases on <paramref name="ground"/>'s flattest stretch where it is given.
    /// <paramref name="placed"/>: <paramref name="planned"/> is the schedule the road was already accepted under (its turn
    /// landings, pitches, eases and the landings it lands, null for the cap on every edge; <see cref="LandEnd"/>), kept as it
    /// is with only the landings asked for added and no pitch placed again: limited on it, a profile already within it is
    /// left as it was, and only what a new landing changes moves.
    /// The two end heights are held, and so is every point <paramref name="held"/> marks (at another road's or a structure's
    /// height): the stretch between two held points is limited on its own, an edge between two of them judged against its
    /// limit where a landing asked for here makes it stricter than <paramref name="planned"/> (where it does not, the road
    /// was already judged on it as it was planned: RoadSpatialGrid.WorstOverLimit); every other point may move. <paramref name="heights"/> is rewritten only on <see cref="Landing.Landed"/>, with <paramref name="grades"/> the kept profile's edge limits and
    /// <paramref name="landed"/> the ends it lands; on a refusal it is left as it was.
    /// <paramref name="shared"/> marks the points on the surface of a road this one meets (RoadSpatialGrid.OnMetRoads): an edge
    /// between two of them is that road's own, at the cap on this road's schedule, never this road's landing or rest; a
    /// required end that runs on along such a road lands where it leaves it, its landing the <see cref="JunctionLanding"/>
    /// metres before the shared stretch (<see cref="SharedRun"/>), so the road lands level before it joins. A road with no
    /// room for that landing within the cap is refused (BRIEF-RULES 17).
    /// The one owner of the road grade cap, pitches, and the landing at a road's end (docs/OWNERS.md).
    /// </summary>
    internal static Landing AcceptLanding(IReadOnlyList<Vector2> points, List<float> heights, (bool start, bool end) required,
        (bool start, bool end) feet, IReadOnlyList<float>? planned, float cap, IReadOnlyList<float>? ground,
        IReadOnlyList<bool>? held, out float[]? grades, out (bool start, bool end) landed, bool placed = false,
        IReadOnlyList<bool>? shared = null)
    {
        int n = Mathf.Min(points.Count, heights.Count);
        // A try whose climb cannot keep its rests within the cap (PlacePitches): that try is refused, never limited without them.
        bool restless = false;
        bool Shared(int i) => shared != null && i < shared.Count && shared[i];
        float[]? Profile(bool atStart, bool atEnd)
        {
            float[]? g = null;
            if (planned != null) { g = new float[planned.Count]; for (int i = 0; i < g.Length; i++) g[i] = planned[i]; }
            // A landing already on the schedule the road was accepted under (placed) stands where it was laid; only a new one
            // is added. A joined end's landing begins where the road leaves the stretch it shares with the road it meets.
            bool newStart = atStart && !(placed && required.start), newEnd = atEnd && !(placed && required.end);
            if (JunctionLanding > 0f && (newStart || newEnd))
                g = ApplyJunctionLanding(points, g, cap, newStart, newEnd, JunctionLanding,
                    required.start ? SharedRun(points, shared, atStart: true) : 0f, required.end ? SharedRun(points, shared, atStart: false) : 0f);
            if (Enabled && !placed)
            {
                // Place the pitches on the profile the road will actually have at the cap, not on
                // the ground-following one: a climb over 50 % ground is spread to the cap by the
                // limiter, so judging its room on the raw rise dropped eases that fit.
                var capped = new List<float>(heights);
                var basis = RoadGrade.Limit(points, capped, cap, g) ? capped : heights;
                var steep = new List<Vector2>();
                g = PlacePitches(points, basis, cap, g, ground: ground, tooSteep: steep) ?? g;
                if (steep.Count > 0) { restless = true; t_tooSteepForRests = steep; return null; }
            }
            // An edge on a met road's own surface (both its ends on it) is that road's, built on its own schedule: neither
            // this road's landing nor its rest, so no limit of this road's is kept on it.
            if (g != null && shared != null)
                for (int i = 1; i < g.Length && i < n; i++)
                    if (Shared(i - 1) && Shared(i)) g[i] = cap;
            return g;
        }
        bool HeldAt(int i) => held != null && i < held.Count && held[i];
        bool Limited(List<float> trial, float[]? g)
        {
            float top = LimitCap(cap, g);
            bool inside = false;
            for (int i = 1; i < n - 1 && !inside; i++) inside = HeldAt(i);
            if (!inside) return RoadGrade.Limit(points, trial, top, g);
            int from = 0;
            for (int i = 1; i < n; i++)
            {
                if (i < n - 1 && !HeldAt(i)) continue;
                // Two held points: nothing moves, but the edge between them meets the landing asked for, or the end does not land.
                bool tightened = i - from == 1
                    && RoadGrade.EdgeLimit(g, i, cap) < RoadGrade.EdgeLimit(planned, i, cap);
                if (i - from > 1 || tightened)
                {
                    var part = new List<Vector2>(i - from + 1);
                    for (int k = from; k <= i; k++) part.Add(points[k]);
                    var partHeights = trial.GetRange(from, i - from + 1);
                    float[]? partGrades = null;
                    if (g != null)
                    {
                        partGrades = new float[i - from + 1];
                        for (int k = from; k <= i; k++) partGrades[k - from] = k < g.Length ? g[k] : cap;
                    }
                    if (!RoadGrade.Limit(part, partHeights, top, partGrades)) return false;
                    for (int k = from; k <= i; k++) trial[k] = partHeights[k - from];
                }
                from = i;
            }
            return true;
        }
        // One try: every end that must land, a staircase's foot included, lands, with every rest kept, or the road is refused.
        grades = null;
        landed = default;
        var (keepStart, keepEnd) = (required.start || feet.start, required.end || feet.end);
        float[]? g = Profile(keepStart, keepEnd);
        if (restless) return Landing.TooSteepForRests;
        var trial = new List<float>(heights);
        // A failed limit leaves the profile as it was (RoadGrade.Limit).
        if (!Limited(trial, g))
            return JunctionLanding > 0f && (feet.start && !required.start || feet.end && !required.end) ? Landing.NoFootLanding : Landing.TooSteep;
        for (int i = 0; i < heights.Count; i++) heights[i] = trial[i];
        grades = g;
        landed = (keepStart, keepEnd);
        return Landing.Landed;
    }

    /// <summary>
    /// A road already planned ending on the level landing a join ends on (<see cref="JunctionLanding"/>) at its start or its
    /// end, where a staircase is laid from it once the road is planned (RoadNetworkGenerator.LocationApproaches, PlanEntryFrom):
    /// judged by the one acceptance (<see cref="AcceptLanding"/>) on the schedule the road was accepted under
    /// (<paramref name="schedule"/>, RoadSpatialGrid.PlannedPath.Grades: its turn landings, its pitches and eases, and the
    /// landings it already lands, <paramref name="landings"/>; null for the cap on every edge), with its held points, that
    /// end a staircase's foot as it is when the road is planned with one. The schedule is kept: only the landing at that end
    /// is added and no pitch is placed again, so the rest of the road stays as it was accepted and only what the landing
    /// itself changes is limited again, however often or late an end is landed. True where it lands; false where the
    /// landing has no room within the cap (the staircase is then not laid from that end: BRIEF-RULES 17). <paramref name="heights"/>
    /// is rewritten only where true, and <paramref name="grades"/> and <paramref name="landed"/> are then the schedule and the
    /// ends the road lands on, for the caller to keep with the heights (on false, the ones given). The road's slopes are never
    /// read as permission.
    /// The one owner of the road grade cap, pitches, and the landing at a road's end (docs/OWNERS.md).
    /// </summary>
    public static bool LandEnd(IReadOnlyList<Vector2> points, IList<float> heights, bool atStart, float cap,
        out List<float>? grades, out (bool start, bool end) landed, IReadOnlyList<float>? schedule = null,
        IReadOnlyList<bool>? held = null, (bool start, bool end) landings = default)
    {
        grades = schedule == null ? null : new List<float>(schedule);
        landed = landings;
        int n = Mathf.Min(points.Count, heights.Count);
        if (JunctionLanding <= 0f || n < 2 || !RoadGrade.Capped(cap)) return true;
        var trial = new List<float>(n);
        for (int i = 0; i < n; i++) trial.Add(heights[i]);
        Landing verdict = AcceptLanding(points, trial, landings, (atStart && !landings.start, !atStart && !landings.end), schedule, cap,
            null, held, out float[]? kept, out var keptEnds, placed: true);
        if (verdict != Landing.Landed) return false;
        for (int i = 0; i < n; i++) heights[i] = trial[i];
        grades = kept == null ? null : new List<float>(kept);
        landed = keptEnds;
        return true;
    }

    /// <summary>
    /// Whether a road leaves its level landing at its start or its end (<paramref name="atStart"/>, <paramref name="atEnd"/>):
    /// <see cref="LeavesItsLandingAround"/> at either end.
    /// </summary>
    public static bool LeavesItsLanding(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, bool atStart, bool atEnd, float grade)
    {
        int n = Mathf.Min(points.Count, heights.Count);
        if (n < 2) return false;
        float total = 0f;
        for (int i = 1; i < n; i++) total += Vector2.Distance(points[i - 1], points[i]);
        return atStart && LeavesItsLandingAround(points, heights, 0f, grade) || atEnd && LeavesItsLandingAround(points, heights, total, grade);
    }

    /// <summary>
    /// Whether a road climbs or falls past <paramref name="grade"/> on any edge within a level landing's length
    /// (<see cref="JunctionLanding"/>) of the place <paramref name="at"/> metres along it, edge by edge: whether it leaves the
    /// level landing there. At <see cref="JunctionLandingGrade"/> it is the end audit's verdict on a foot's road
    /// (StairEnds.OntoItsRoad: a foot's road that does not end on its level landing there is a gap), and the verdict on a
    /// landing laid round a foot on a standing flight (RoadStairRuns.Fill). The one reading of a foot's level landing on a
    /// finished road.
    /// </summary>
    public static bool LeavesItsLandingAround(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, float at, float grade)
    {
        int n = Mathf.Min(points.Count, heights.Count);
        if (n < 2 || !RoadGrade.Capped(grade)) return false;
        float along = 0f;
        for (int i = 1; i < n; i++)
        {
            float before = along, run = Vector2.Distance(points[i - 1], points[i]);
            along += run;
            bool near = Mathf.Min(Mathf.Abs(along - at), Mathf.Abs(before - at)) < JunctionLanding;
            if (near && run > 1e-4f && Mathf.Abs(heights[i] - heights[i - 1]) / run > grade + 1e-3f) return true;
        }
        return false;
    }

    /// <summary>How far along its range a rest's spacing falls at <paramref name="at"/>, in [0, 1]: smooth world-position
    /// noise, so neighbouring rests vary and the same place always gets the same; road rests and staircase rests
    /// (StairLayout) share it.</summary>
    internal static float SpacingNoise(Vector2 at) => RoadEarthworkNoise.Perlin01(at.x * 0.037f + 11.1f, at.y * 0.037f - 7.3f);

    /// <summary>Per-edge grade limits with pitches and eases placed, or null
    /// when no climb takes an ease (the caller's limits stand). A rest is never dropped for height: a climb whose edges
    /// between its rests, at the cap, cannot make its height keeps them all, is counted (<see cref="ClimbsTooSteepForRests"/>)
    /// and its points are added to <paramref name="tooSteep"/>, for the caller to refuse the road. The one owner of the road grade cap, pitches, and the landing at a road's end (docs/OWNERS.md).</summary>
    public static float[]? PlacePitches(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, float cap, float[]? edgeGrades,
        float? everyMin = null, float? everyMax = null,
        float? easeMin = null, float? easeMax = null, float? easeGrade = null, float? minGrade = null,
        IReadOnlyList<float>? ground = null, List<Vector2>? tooSteep = null)
    {
        float eMin = everyMin ?? EaseEveryMin, eMax = everyMax ?? EaseEveryMax;
        float lMin = easeMin ?? EaseMin, lMax = easeMax ?? EaseMax, eg = easeGrade ?? EaseGrade, climbing = minGrade ?? MinGrade;
        int n = Mathf.Min(points.Count, heights.Count);
        if (n < 3 || cap <= 0f) return null;
        var s = new float[n];
        for (int i = 1; i < n; i++) s[i] = s[i - 1] + Vector2.Distance(points[i - 1], points[i]);
        var dir = ClimbDirections(s, heights, n, climbing);
        float[]? result = null;
        int a = 0;
        while (a < n)
        {
            if (dir[a] == 0) { a++; continue; }
            int b = a;
            while (b + 1 < n && dir[b + 1] == dir[a]) b++;
            float rise = Mathf.Abs(heights[b] - heights[a]);
            // A switchback landing (or any limit already at an ease's grade) IS
            // an ease. Collect them first; the next ease is spaced from their end.
            var fixedEases = new List<(float from, float to)>();
            if (edgeGrades != null)
                for (int i = a + 1; i <= b; i++)
                {
                    if (edgeGrades[i] > eg + 0.02f) continue;
                    int j = i; while (j + 1 <= b && edgeGrades[j + 1] <= eg + 0.02f) j++;
                    fixedEases.Add((s[i - 1], s[j])); i = j;
                }
            // Lay eases along the climb, then drop the last until the climb can
            // still make its height: eases at their grade, every other edge at the cap.
            var eases = new List<(float from, float to)>();
            float at = s[a];
            int nextFixed = 0;
            while (true)
            {
                while (nextFixed < fixedEases.Count && fixedEases[nextFixed].to <= at) nextFixed++;
                float n1 = SpacingNoise(points[FindIndex(s, at, a, b)]);
                float every = Mathf.Lerp(eMin, eMax, n1);
                float from = at + every;
                float n2 = RoadEarthworkNoise.Perlin01(points[FindIndex(s, from, a, b)].x * 0.051f - 3.7f, points[FindIndex(s, from, a, b)].y * 0.051f + 19.9f);
                float easeLen = Mathf.Lerp(lMin, lMax, n2);
                // With the ground given, put the ease where the natural ground
                // is flattest within the spacing window, so it sits on a bench
                // instead of being built out of the slope.
                if (ground != null)
                {
                    float best = float.MaxValue, bestFrom = from;
                    for (float c = at + eMin; c <= at + eMax && c + easeLen <= s[b]; c += 1f)
                    {
                        int i0 = FindIndex(s, c, a, b), i1 = FindIndex(s, c + easeLen, a, b);
                        if (i1 <= i0) continue;
                        float g = Mathf.Abs(ground[i1] - ground[i0]) / Mathf.Max(0.1f, s[i1] - s[i0]);
                        if (g < best - 1e-4f) { best = g; bestFrom = c; }
                    }
                    from = bestFrom;
                }
                float to = from + easeLen;
                // A landing comes before this ease would end: it is the ease.
                if (nextFixed < fixedEases.Count && fixedEases[nextFixed].from < to)
                {
                    at = Mathf.Max(at, fixedEases[nextFixed].to); nextFixed++;
                    continue;
                }
                if (to > s[b] - eMin * 0.5f) break;
                eases.Add((from, to));
                at = to;
            }
            List<(float from, float to)> All() { var l = new List<(float, float)>(fixedEases); l.AddRange(eases); return l; }
            float Room()
            {
                float room = 0f; var all = All();
                for (int i = a + 1; i <= b; i++) room += (s[i] - s[i - 1]) * EdgeLimit(s[i - 1], s[i], all, cap, eg);
                return room;
            }
            // A climb keeps every ease, every other edge at the cap (tight, within the slack, but buildable). One that cannot
            // make its height so is too steep for its rests: counted and reported, never given its height by dropping a rest
            // or by an edge past the cap ("if a climb is too steep we shouldn't climb there").
            (float from, float to)? pitch = null;
            if (eases.Count > 0 && rise > Room() + 1e-3f)
            {
                // The last resort (AsLastResort): one pitch between two of its rests, if that makes its height; else, and
                // outside the last resort, the climb is too steep for its rests.
                if (LastResort) pitch = OnePitch(s, a, b, All(), ground ?? heights, rise, Room(), cap, eg);
                if (pitch == null)
                {
                    System.Threading.Interlocked.Increment(ref s_climbsTooSteepForRests);
                    if (!LastResort) t_restsRefusals++;
                    for (int i = a; i <= b; i++) tooSteep?.Add(points[i]);
                }
                else System.Threading.Interlocked.Increment(ref s_pitchClimbs);
            }
            if (eases.Count > 0 || fixedEases.Count > 0)
            {
                result ??= edgeGrades != null ? (float[])edgeGrades.Clone() : Filled(n, cap);
                var all = All();
                for (int i = a + 1; i <= b; i++)
                {
                    // A landing or other limit already below stands; nothing rises above the cap but a last-resort pitch.
                    float lim = EdgeLimit(s[i - 1], s[i], all, cap, eg);
                    if (pitch is (float, float) zone && InPitch(s[i - 1], s[i], zone.Item1, zone.Item2) && lim >= cap && result[i] >= cap - 1e-4f) result[i] = PitchGrade;
                    else result[i] = Mathf.Min(result[i], lim);
                }
            }
            a = b + 1;
        }
        return result;
    }

    private static int s_pitchClimbs;
    /// <summary>Climbs given a last-resort pitch as they were planned (trials and refused roads too).</summary>
    public static int PitchClimbs => s_pitchClimbs;

    private static bool InPitch(float s0, float s1, float from, float to) => s0 >= from - 1e-3f && s1 <= to + 1e-3f;

    /// <summary>The last resort's one pitch on the climb from <paramref name="a"/> to <paramref name="b"/> too steep for its
    /// rests (<paramref name="eases"/>): at most <see cref="PitchLength"/> metres between two consecutive rests, where
    /// <paramref name="ground"/> is steepest over that length (the first such place on a tie), its edges at up to
    /// <see cref="PitchGrade"/>; null where none lies between two rests or the climb cannot make <paramref name="rise"/> even so.</summary>
    private static (float from, float to)? OnePitch(float[] s, int a, int b, List<(float from, float to)> eases,
        IReadOnlyList<float> ground, float rise, float room, float cap, float ease)
    {
        if (!Capped(cap) || PitchGrade <= cap || PitchLength <= 0f) return null;
        var rests = new List<(float from, float to)>(eases);
        rests.Sort((x, y) => x.from.CompareTo(y.from));
        (float from, float to)? best = null;
        float steepest = float.MinValue;
        for (int k = 0; k + 1 < rests.Count; k++)
        {
            float gapFrom = rests[k].to, gapTo = rests[k + 1].from;
            if (gapTo - gapFrom < 1f) continue;
            float len = Mathf.Min(PitchLength, gapTo - gapFrom);
            for (float c = gapFrom; c + len <= gapTo + 1e-3f; c += 1f)
            {
                int i0 = FindIndex(s, c, a, b), i1 = FindIndex(s, c + len, a, b);
                if (i1 <= i0) continue;
                float g = Mathf.Abs(ground[i1] - ground[i0]) / Mathf.Max(0.1f, s[i1] - s[i0]);
                if (g > steepest + 1e-4f) { steepest = g; best = (c, c + len); }
            }
        }
        if (best == null) return null;
        var (from, to) = best.Value;
        float gained = 0f;
        for (int i = a + 1; i <= b; i++)
            if (InPitch(s[i - 1], s[i], from, to) && EdgeLimit(s[i - 1], s[i], eases, cap, ease) >= cap)
                gained += (s[i] - s[i - 1]) * (PitchGrade - cap);
        return rise <= room + gained + 1e-3f ? best : null;
    }

    private static bool Capped(float cap) => RoadGrade.Capped(cap);

    /// <summary>The cap to hand the grade limiter and the judge with these edge limits: the steepest last-resort pitch among
    /// them (<see cref="PitchGrade"/> at most), else <paramref name="cap"/>. Only a last-resort pitch (<see cref="OnePitch"/>)
    /// sets an edge's limit above the cap, so a road planned without one is limited and judged at the cap as before.</summary>
    public static float LimitCap(float cap, IReadOnlyList<float>? edgeGrades)
    {
        if (edgeGrades == null || !Capped(cap)) return cap;
        float top = cap;
        foreach (var g in edgeGrades) if (g > top) top = g;
        return Mathf.Min(top, Mathf.Max(cap, PitchGrade));
    }

    /// <summary>One stored pitch: the points of its edges over the cap, their length and the steepest of them.</summary>
    internal readonly struct Pitch
    {
        public readonly List<Vector2> Points;
        public readonly float Length, Steepest;
        public Pitch(List<Vector2> points, float length, float steepest) { Points = points; Length = length; Steepest = steepest; }
        public Vector2 Middle => Points[Points.Count / 2];
    }

    /// <summary>The pitches of these pieces as they stand: each run of edges a last-resort pitch allowed above the cap
    /// (<see cref="RoadSpatialGrid.PlannedPath.Grades"/> over <paramref name="cap"/>) whose stored grade is over the cap. A
    /// piece with no such limit has none, whatever its heights.</summary>
    internal static List<Pitch> PitchesOf(IEnumerable<RoadSpatialGrid.PlannedPath> pieces, float cap)
    {
        var found = new List<Pitch>();
        foreach (var piece in pieces)
        {
            var g = piece.Grades;
            if (g == null || LimitCap(cap, g) <= cap) continue;
            int n = Mathf.Min(piece.Points.Count, Mathf.Min(piece.Heights.Count, g.Count));
            List<Vector2>? run = null;
            float length = 0f, steepest = 0f;
            void Close() { if (run != null && length > 0f) found.Add(new Pitch(run, length, steepest)); run = null; length = 0f; steepest = 0f; }
            for (int i = 1; i < n; i++)
            {
                if (g[i] <= cap + 1e-4f) { Close(); continue; }
                float edge = Vector2.Distance(piece.Points[i - 1], piece.Points[i]);
                float grade = edge > 1e-4f ? Mathf.Abs(piece.Heights[i] - piece.Heights[i - 1]) / edge : 0f;
                if (grade <= cap + 1e-3f) continue;
                run ??= new List<Vector2> { piece.Points[i - 1] };
                run.Add(piece.Points[i]);
                length += edge;
                steepest = Mathf.Max(steepest, grade);
            }
            Close();
        }
        return found;
    }

    /// <summary>Every pitch the world stores, with the island worker that stored it (<see cref="CrossIslandReads.Tag"/>, 0 off
    /// an island worker and for a pitch read from a save) and what its last resort was for: the one ledger of the world's
    /// pitches. Generation records its roads' pitches as it commits them, a manual road as it is appended
    /// (RoadNetworkGenerator.AppendManualRoad), and a load reads them back (<see cref="Load"/>): stored road points keep
    /// their position, width and height, not their edge's limit nor which point follows them along their road, so the pitches
    /// cannot be told from them and are saved as their own record (<see cref="Serialize"/>).</summary>
    private static readonly List<(int island, Pitch pitch, string? what)> s_stored = new();
    /// <summary>Each island worker's share of the world's pitch budget (<see cref="BeginIsland"/>), by its number.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, float> s_shares = new();

    /// <summary>Each island's share of the world's pitch budget, by its chosen destinations (<paramref name="destinations"/>,
    /// in the islands' order): <see cref="PitchBudget"/> times its destinations over all the islands'. The shares sum to the
    /// budget. Fixed before any island starts (RoadNetworkGenerator.GenerationWork), so they do not depend on which finishes first.</summary>
    internal static float[] Shares(IReadOnlyList<int> destinations)
    {
        int all = 0;
        foreach (int d in destinations) all += d;
        var shares = new float[destinations.Count];
        for (int i = 0; i < shares.Length; i++) shares[i] = all > 0 ? PitchBudget * destinations[i] / all : 0f;
        return shares;
    }

    /// <summary>Island worker <paramref name="island"/>'s share of the world's pitch budget, set before its roads are planned
    /// (<see cref="Shares"/>). Each island's roads are planned in one order on one worker and islands are separate networks
    /// (CrossIslandReads), so its budget and spacing are decided the same way whichever island finishes first.</summary>
    internal static void BeginIsland(int island, float share) => s_shares[island] = share;

    /// <summary>What this thread's road is judged against: the pitches it sees (<see cref="CrossIslandReads.Sees(int, int)"/>:
    /// an island worker its own island's and untagged ones, everything off an island worker) and the metres it may store in
    /// all: an island worker its share (<see cref="BeginIsland"/>); off an island worker (a manual road, a test) the
    /// world's whole budget, against every pitch the world stores, generated, appended and loaded alike.</summary>
    private static (float share, float used, List<Pitch> seen) Ledger()
    {
        int me = CrossIslandReads.Me;
        float share = me > 0 && s_shares.TryGetValue(me, out float mine) ? mine : PitchBudget;
        float used = 0f;
        var seen = new List<Pitch>();
        lock (s_stored)
            foreach (var (island, p, _) in s_stored)
                if (CrossIslandReads.Sees(me, island)) { used += p.Length; seen.Add(p); }
        return (share, used, seen);
    }

    /// <summary>Why these pieces may not be stored for their pitches (<see cref="PitchesOf"/>), and where; null when they may
    /// (and always for pieces with no pitch): the pitch metres stored stay within the budget of this thread's
    /// <see cref="Ledger"/>, and no two pitches it sees, theirs among them, lie nearer than <see cref="PitchSpacing"/>. Asked by
    /// the one list of a road's refusals (RoadNetworkGenerator.RoadRefusal), which keeps <paramref name="pitches"/> for the
    /// road's commit to record (<see cref="Record"/>).</summary>
    internal static string? PitchRefusal(IEnumerable<RoadSpatialGrid.PlannedPath> pieces, float cap, out Vector2 at, out List<Pitch> pitches)
    {
        at = default;
        pitches = PitchesOf(pieces, cap);
        if (pitches.Count == 0) return null;
        var (share, used, seen) = Ledger();
        for (int k = 0; k < pitches.Count; k++)
        {
            Pitch p = pitches[k];
            at = p.Middle;
            foreach (var other in seen) if (Apart(p, other) < PitchSpacing) return PitchSpacingRefusal;
            for (int o = 0; o < k; o++) if (Apart(p, pitches[o]) < PitchSpacing) return PitchSpacingRefusal;
            used += p.Length;
            if (used > share + 1e-3f) return PitchBudgetRefusal;
        }
        return null;
    }

    /// <summary><see cref="PitchRefusal(IEnumerable{RoadSpatialGrid.PlannedPath}, float, out Vector2, out List{Pitch})"/>, its pitches not kept.</summary>
    internal static string? PitchRefusal(IEnumerable<RoadSpatialGrid.PlannedPath> pieces, float cap, out Vector2 at) =>
        PitchRefusal(pieces, cap, out at, out _);

    private static float Apart(Pitch a, Pitch b)
    {
        float least = float.MaxValue;
        foreach (var p in a.Points) foreach (var q in b.Points) least = Mathf.Min(least, (p - q).sqrMagnitude);
        return Mathf.Sqrt(least);
    }

    /// <summary>The pitches of a road just stored, as its refusals judged them (<see cref="PitchRefusal(IEnumerable{RoadSpatialGrid.PlannedPath}, float, out Vector2, out List{Pitch})"/>),
    /// recorded in the world's ledger with this thread's island (<see cref="CrossIslandReads.Tag"/>); returns them for the log.</summary>
    internal static IReadOnlyList<Pitch> Record(IReadOnlyList<Pitch>? pitches)
    {
        if (pitches == null || pitches.Count == 0) return System.Array.Empty<Pitch>();
        int tag = CrossIslandReads.Tag;
        string? what = LastResortRunFor;
        lock (s_stored) foreach (var p in pitches) s_stored.Add((tag, p, what));
        return pitches;
    }

    /// <summary>The pitches of these pieces (<see cref="PitchesOf"/>) recorded (<see cref="Record(IReadOnlyList{Pitch})"/>).</summary>
    internal static IReadOnlyList<Pitch> Record(IEnumerable<RoadSpatialGrid.PlannedPath> pieces, float cap) => Record(PitchesOf(pieces, cap));

    private const int PitchesFormat = 1;

    /// <summary>The world's pitches as a save keeps them (RoadNetworkPersistence): each one's length, steepest grade and points.</summary>
    internal static byte[] Serialize()
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(ms))
        {
            writer.Write(PitchesFormat);
            lock (s_stored)
            {
                writer.Write(s_stored.Count);
                foreach (var (_, p, _) in s_stored)
                {
                    writer.Write(p.Length);
                    writer.Write(p.Steepest);
                    writer.Write(p.Points.Count);
                    foreach (Vector2 q in p.Points) { writer.Write(q.x); writer.Write(q.y); }
                }
            }
        }
        return ms.ToArray();
    }

    /// <summary>The world's pitches read from a save (<see cref="Serialize"/>), in place of any held: untagged, as the network
    /// they belong to is once loaded (CrossIslandReads). None for no record.</summary>
    internal static void Load(byte[]? data)
    {
        var read = new List<(int, Pitch, string?)>();
        if (data != null && data.Length > 0)
        {
            using var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
            int format = reader.ReadInt32();
            if (format != PitchesFormat) throw new System.IO.InvalidDataException($"pitch record format {format}, this build reads {PitchesFormat}");
            int count = reader.ReadInt32();
            if (count < 0) throw new System.IO.InvalidDataException($"{count} pitches");
            for (int i = 0; i < count; i++)
            {
                float length = reader.ReadSingle(), steepest = reader.ReadSingle();
                int n = reader.ReadInt32();
                if (n < 1) throw new System.IO.InvalidDataException($"a pitch of {n} points");
                var points = new List<Vector2>(n);
                for (int k = 0; k < n; k++) points.Add(new Vector2(reader.ReadSingle(), reader.ReadSingle()));
                read.Add((0, new Pitch(points, length, steepest), null));
            }
        }
        lock (s_stored) { s_stored.Clear(); s_stored.AddRange(read); }
    }

    /// <summary>None of the world's pitches held (a save's record not read).</summary>
    internal static void ForgetStored() { lock (s_stored) s_stored.Clear(); }

    /// <summary>Every pitch the world stores (count, metres, longest), for the generation summary.</summary>
    internal static (int count, float metres, float longest) Stored()
    {
        lock (s_stored)
        {
            float metres = 0f, longest = 0f;
            foreach (var (_, p, _) in s_stored) { metres += p.Length; longest = Mathf.Max(longest, p.Length); }
            return (s_stored.Count, metres, longest);
        }
    }

    private static float EdgeLimit(float s0, float s1, List<(float from, float to)> eases, float cap, float ease)
    {
        foreach (var (from, to) in eases)
            if (s1 > from && s0 < to) return ease;
        return cap;
    }

    private static int FindIndex(float[] s, float at, int a, int b)
    {
        int i = a;
        while (i < b && s[i] < at) i++;
        return i;
    }

    private static int[] ClimbDirections(float[] s, IReadOnlyList<float> heights, int n, float climbing)
    {
        var dir = new int[n];
        int lo = 0, hi = 0;
        for (int i = 0; i < n; i++)
        {
            while (lo < i && s[i] - s[lo] > Window * 0.5f) lo++;
            while (hi < n - 1 && s[hi + 1] - s[i] <= Window * 0.5f) hi++;
            float run = s[hi] - s[lo];
            if (run < Window * 0.5f) continue;
            float g = (heights[hi] - heights[lo]) / run;
            dir[i] = g > climbing ? 1 : g < -climbing ? -1 : 0;
        }
        return dir;
    }

    private static float[] Filled(int n, float value)
    {
        var f = new float[n];
        for (int i = 0; i < n; i++) f[i] = value;
        return f;
    }
}
