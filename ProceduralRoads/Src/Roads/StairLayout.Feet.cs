using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A staircase's foot in the layout (one owner): it sets off facing its road's travel (priced per step off it), or back beside
/// the road for at most MostSetOffBack pieces; its bottom step pays for standing out past a ledge's sharp edge; it is lowered
/// toward a road that cannot climb to it, and an apron is cut where it stands against a bank.
/// </summary>
public static partial class StairLayout
{
    /// <summary>
    /// Per heading step a staircase's first piece sets off from the road's travel into its foot (<see cref="Route.Facing"/>):
    /// straight on with the road where that fits (at MountainCave02 1920,704 the foot set off 45 degrees off
    /// its road, where straight on, moved a bit to the left, lines up better with the road). A piece's length per step: at a stair turn's price (0.5) a start a step off the road still won up a slope
    /// rising a step off it, where straight on fits too.
    /// </summary>
    public const float FootOffRoadWeight = 2f;

    private static float FootOffRoadPrice(float heading, Route route) =>
        route.Facing.HasValue
            ? FootOffRoadWeight * Mathf.Round(Mathf.Abs(Wrap(heading - route.Facing.Value + 180f) - 180f) / HeadingStep) : 0f;

    /// <summary>
    /// A staircase set off freely from a road's end (an approach onto a location's landing) sets off within this many heading
    /// steps (90 degrees) of the way the road arrives: free, one set off back across the road the walker came along, its
    /// foot across the road and the walk doubling back past the flight (at the mountain cave entry's foot at
    /// 1948.1,691.7 a stair set off 180 degrees opposite the direction of road travel blocked the actual road to the
    /// stair start). Where nothing that way reaches, the caller lays it freely.
    /// </summary>
    public const int FacingSteps = 4;

    /// <summary>
    /// Pieces a staircase held to a facing may set off back from its foot, more than <see cref="FacingSteps"/> off the
    /// road's travel, before one faces it: beside the road (no piece but the joining step may lie on it,
    /// RoadStairRuns.JoinsOnly), then turned up the slope. At the foot at Hildir_cave 1664,768, where every heading
    /// within 90 degrees of the road was buried at the foot and the free layout ran back along the road, the difference is split:
    /// staircases back along the road at most about 3 stair lengths.
    /// </summary>
    public const int MostSetOffBack = 3;

    /// <summary>A child's <see cref="Node.BackRun"/> and <see cref="Node.FacedStairs"/> setting off on
    /// <paramref name="heading"/> from <paramref name="node"/>, a <paramref name="kind"/>; null where it may not: back once
    /// too often, or, having set off back, back again before the first flight (<see cref="FirstFlightStairs"/>) has climbed
    /// from the first piece that faced the road (back three, then one across, it could turn back along the road again).</summary>
    private static (int backRun, int faced)? BackRunAfter(Node node, float heading, StairPieceKind kind, Route route)
    {
        if (!route.Facing.HasValue) return (-1, -1);
        bool back = Mathf.Abs(Wrap(heading - route.Facing.Value + 180f) - 180f) > FacingSteps * HeadingStep + 0.01f;
        int stair = kind == StairPieceKind.Stair ? 1 : 0;
        bool root = node.Parent == null;
        if (root || node.FacedStairs < 0)
        {
            // Facing from the foot, it is held no further; turned to face after setting off back, its first flight is.
            if (!back) return (-1, root ? FirstFlightStairs : stair);
            int run = root ? 1 : node.BackRun + 1;
            return run > MostSetOffBack ? null : (run, -1);
        }
        if (node.FacedStairs >= FirstFlightStairs) return (-1, FirstFlightStairs);
        return back ? null : (-1, Math.Min(FirstFlightStairs, node.FacedStairs + stair));
    }

    /// <summary>Most stairs <see cref="LowerFoot"/> adds down from a foot.</summary>
    public const int MostLowered = 2;

    /// <summary>
    /// The one rounding of a foot's drop to whole stairs (docs/OWNERS.md), for a foot over its road's ground
    /// (<see cref="LowerFoot"/>) and a foot over a road held at a height (<see cref="FootToRoad"/>) alike: a foot
    /// <paramref name="over"/> its road by whole stairs and a leftover. A leftover within a walker's step (<see cref="Walkable"/>)
    /// keeps the shorter flight: the foot steps off within a step. One up to half a rise (<see cref="StairFitsFrom"/>) keeps the
    /// shorter flight where the road's landing is raised to the foot by the leftover, so the foot steps off level
    /// (<paramref name="raised"/>, asked with the stairs of the shorter flight and the leftover: the road's earthwork owner's
    /// verdict, null where nothing may raise the road); else, and past half a rise, the stair is added (rounded up, "a stair a
    /// bit sunk is fine", the owner, 12 Nov 2026): rounded up alone, a foot 0.21 m over the road its stairs left took a whole
    /// stair more and its landing was cut 0.8 m into the ground. None for a foot within a step of its road or under it.
    /// </summary>
    internal static int StairsDownTo(float over, Func<int, float, bool>? raised = null)
    {
        if (over <= 0f) return 0;
        int whole = Mathf.FloorToInt(over / PieceRise);
        float left = over - whole * PieceRise;
        if (Walkable(left, 0f)) return whole;
        if (left <= StairFitsFrom && raised != null && raised(whole, left)) return whole;
        return whole + 1;
    }

    /// <summary>
    /// How far off a height must be for one stair to be the nearer fit: half a rise (<see cref="PieceRise"/>). Off by less, no
    /// stair fits and it is the ground's own unevenness; off by this or more, a stair is laid where one may be. The one owner of
    /// a height rounding to whole stairs (docs/OWNERS.md): how far a foot's road may be raised to it (<see cref="StairsDownTo"/>), an end's
    /// stairs (StructureEnds.Decide), and how far over or under a location's landing the network may arrive and still be level with
    /// it (a road always ends level, even at the cost of a POI, and may end in stairs if needed): the reached test, a road's end
    /// at its location, an approach staircase's threshold, the landing's level ground (PlacedLocationShape.Aims) and a staircase that
    /// may finish anywhere near its goal finishing no lower than this under it.
    /// </summary>
    public const float StairFitsFrom = PieceRise / 2f;

    /// <summary>
    /// A staircase whose foot (the first step's low edge, at the ground's height there) stands over its road's ground
    /// further back more than the road may climb to it: more stairs on down from the foot toward the road, up to
    /// <see cref="MostLowered"/>, each a rise lower, cut into the ground, the sunk step under the foot becoming the first of
    /// them (at the foot at -9308.1,690.3 the bottom of the stairs was not walkable, the earthworks too steep for a player
    /// to use to get up to the bottom stair; the road climbed 2.3 m in 5 m to it).
    /// <paramref name="back"/>: the road's ground a few metres back from the foot, <paramref name="run"/> metres away;
    /// <paramref name="cap"/> the road's grade cap. What the road climbs to the foot over them is what it is built to climb
    /// there (RoadPitches.ClimbToFoot): its level landing, not the cap (a foot 1.1 m over its road's ground 5 m back was left
    /// as it was, read as climbed at the cap, and its road's landing stood on 1.2 m of fill). Returns the new foot (low edge, height) or null; null too at a foot the
    /// road was carried on to (<see cref="StairPlan.FootFloorsTrimmed"/>), which starts on its kept floor row with no sunk
    /// step: the road there was judged ending where it is.
    /// The stairs are rounded by <see cref="StairsDownTo"/>: <paramref name="raised"/> says whether the road's landing may be
    /// raised to the foot the shorter flight leaves (its low edge, and the leftover), null where it may not; a foot kept on a
    /// raised landing records the leftover (<see cref="StairPlan.FootRaised"/>).
    /// The one owner of staircase starts (docs/OWNERS.md).
    /// </summary>
    public static Vector3? LowerFoot(StairPlan plan, Func<float, float, float> ground, float width, float back, float run, float cap,
        Func<Vector3, float, bool>? raised = null)
    {
        if (plan.Foot is not Vector3 foot || plan.SunkPieces <= 0 || plan.FootLowered > 0) return null;
        float fill = 0f;
        int down = StairsDownTo(foot.y - back - RoadPitches.ClimbToFoot(run, cap), raised == null ? null : (whole, left) =>
        {
            Vector2 at = new Vector2(foot.x, foot.z) - Forward(plan.StartHeading) * (whole * PieceRun);
            if (!raised(new Vector3(at.x, foot.y - whole * PieceRise, at.y), left)) return false;
            fill = left;
            return true;
        });
        int steps = Mathf.Min(MostLowered, down);
        Vector3? low = steps <= 0 ? null : AddFootStairs(plan, ground, width, steps);
        if (fill > 0f && steps == down && (steps == 0 || low != null)) plan.FootRaised = fill;
        return low;
    }

    /// <summary>
    /// The one owner of a staircase's foot meeting a road held at <paramref name="road"/> (the road levelled at a crossing's
    /// end, RoadStairRuns.Lay), and of the stair sunk into that road where the two stand apart: the foot carried on down
    /// toward the road by whole stairs, rounded by the one rounding (<see cref="StairsDownTo"/>) with nothing to raise the road: it is
    /// held at the end's height. Left within a walker's
    /// step of the road (<see cref="Walkable"/>), it meets it there. Left more than a step over it (or under it: then one stair
    /// less, so it stands over it), one stair more is sunk into the road under the foot, as a staircase's own sunk step is laid
    /// (<see cref="SinkStep"/>: an uncut stair a rise down, out along its first heading, buried in the road but for its top):
    /// its slope comes up out of the road, so the walk meets the road on it with no step, the foot under a rise over the road
    /// (<see cref="FootMeets"/>). Nothing is sunk where the foot keeps its own sunk step. Returns the foot (its first stair's low
    /// edge), the height the road meets it at (the foot's, or the road's on the sunk stair), whether it is met on a sunk
    /// stair and whether that stair was laid here; null where the foot is not over the road, or a stair added would not be held up (<see cref="RowHeld"/>).
    /// </summary>
    public static (Vector3 foot, float meets, bool sunk, bool laid)? FootToRoad(StairPlan plan, Func<float, float, float> ground, float width, float road)
    {
        if (plan.Foot is not Vector3 foot) return null;
        int steps = StairsDownTo(foot.y - road);
        bool sink = !Walkable(foot.y - steps * PieceRise, road);
        // Rounded up past the held road by more than a step: the stair added is the one sunk into the road under the foot
        // (SinkStep, its slope coming up out of it), not one cut into the road.
        if (sink && foot.y - steps * PieceRise < road) steps--;
        if (steps < 0) return null;
        if (steps > 0 && AddFootStairs(plan, ground, width, steps) is null) return null;
        Vector3 at = plan.Foot!.Value;
        if (!sink) return (at, at.y, false, false);
        bool laid = plan.SunkPieces <= 0;
        if (laid) SinkStep(plan, StairKit.Of(plan.Kit), new Vector2(at.x, at.z), plan.StartHeading, at.y, Lanes(width), ground);
        if (!SunkStairSpans(at.y, road)) return null;
        plan.SunkStairMeets = road;
        return (at, road, true, laid);
    }

    /// <summary>Whether a staircase's foot at <paramref name="foot"/> (its lowest walked stair's low edge) meets a road at
    /// <paramref name="road"/>: within a walker's step (<see cref="Walkable"/>), or over it by less than a rise on the stair
    /// <see cref="FootToRoad"/> sank for that road (<see cref="StairPlan.SunkStairMeets"/>), whose slope comes up out of it. A
    /// staircase's own sunk step alone is not that: nothing checked it lies over this road.</summary>
    public static bool FootMeets(StairPlan plan, float foot, float road) =>
        Walkable(foot, road)
        || plan.SunkPieces > 0 && plan.SunkStairMeets is float met && Walkable(met, road) && SunkStairSpans(foot, road);

    /// <summary>Whether one stair sunk into the lower of two walking surfaces (<see cref="SunkStair"/>) carries a walk up to the
    /// higher: <paramref name="high"/> over <paramref name="low"/>, by less than a rise, so the stair's slope comes up out of the
    /// lower to the higher. The one judgement of the flex, for a foot over a road (<see cref="FootToRoad"/>, <see cref="FootMeets"/>)
    /// and a clip over a top floor (<see cref="ClipTops"/>); within a step (<see cref="Walkable"/>) none is needed.</summary>
    internal static bool SunkStairSpans(float high, float low) => high > low && high - low < PieceRise;

    /// <summary>The stair sunk under a foot at <paramref name="foot"/> (<paramref name="height"/>, its low edge), the one way one
    /// is laid under a foot (<see cref="SunkStair"/>), first in the plan's lists, as a plan lays its pieces from its foot
    /// (<see cref="StairPlan.SunkPieces"/>).</summary>
    private static void SinkStep(StairPlan plan, StairKit kit, Vector2 foot, float heading, float height, int lanes, Func<float, float, float> ground)
    {
        int pieces = plan.Pieces.Count, clearances = plan.Clearances.Count;
        SunkStair(plan, kit, foot, heading, height, lanes, ground);
        int added = plan.Pieces.Count - pieces, addedClearances = plan.Clearances.Count - clearances;
        FirstInWalkingOrder(plan.Pieces, new List<int> { pieces, plan.Pieces.Count });
        FirstInWalkingOrder(plan.Clearances, new List<int> { clearances, plan.Clearances.Count });
        if (plan.LastRowPieces >= 0) { plan.LastRowPieces += added; plan.LastRowClearances += addedClearances; }
        plan.SunkPieces = added; plan.SunkClearances = addedClearances;
        plan.Foot = new Vector3(foot.x, height, foot.y);
    }

    /// <summary>The one place a stair is sunk into a lower walking surface: a whole stair a rise down from
    /// <paramref name="height"/> at <paramref name="edge"/> (its top edge's middle), out back along <paramref name="heading"/> (the
    /// way it climbs), so the walk comes up out of the lower surface rather than meeting a lip (one extra stair clipped down
    /// into the earth makes it connect better). Under a foot it is sunk into the road (<see cref="SinkStep"/>); under a clip
    /// into the lower top's floor (<see cref="ClipTops"/>). Buried on purpose: no earthwork uncovers it (uncut, not counted as
    /// a stair of the flight), marked <see cref="StairPiece.Sunk"/>.</summary>
    private static void SunkStair(StairPlan plan, StairKit kit, Vector2 edge, float heading, float height, int lanes, Func<float, float, float> ground)
    {
        int pieces = plan.Pieces.Count;
        AddRow(plan, kit, StairPieceKind.Stair, kit.Stair, edge - Forward(heading) * PieceRun, heading, height - PieceRise, lanes, ground, mark: false, cut: false);
        for (int i = pieces; i < plan.Pieces.Count; i++) plan.Pieces[i].Sunk = true;
    }

    /// <summary><paramref name="steps"/> stairs added on down from a plan's foot along its first heading, the sunk step
    /// under the foot taken away; the new foot. None, and null, where something within reach would not hold one of them up.</summary>
    private static Vector3? AddFootStairs(StairPlan plan, Func<float, float, float> ground, float width, int steps) =>
        AddFootRows(plan, ground, width, 0, steps);

    /// <summary><paramref name="floors"/> level floor rows at the foot's height, then <paramref name="steps"/> stairs on down
    /// from the last of them, out from a plan's foot along its first heading (back down the way it climbs), the sunk step
    /// under the foot taken away; the new foot (the last row's low edge). The one way rows are added below a foot
    /// (<see cref="LowerFoot"/>, <see cref="FinishFoot"/>). None, and null, where something within reach would not hold one of
    /// them up (<see cref="RowHeld"/>).</summary>
    private static Vector3? AddFootRows(StairPlan plan, Func<float, float, float> ground, float width, int floors, int steps)
    {
        Vector3 foot = plan.Foot!.Value;
        StairKit kit = StairKit.Of(plan.Kit);
        int lanes = Lanes(width);
        Vector2 dir = Forward(plan.StartHeading), at = new Vector2(foot.x, foot.z);
        // None where something within reach would not hold one up (RowHeld).
        for (int i = 1; i <= floors + steps; i++)
        {
            bool floor = i <= floors;
            if (!RowHeld(kit, floor ? kit.Floor : kit.Stair, at - dir * (i * PieceRun), plan.StartHeading,
                    floor ? foot.y : foot.y - (i - floors) * PieceRise, lanes, ground)) return null;
        }
        // The sunk step goes: the first stair down takes its location, uncovered.
        DropSunkFoot(plan);
        float base_ = foot.y;
        int pieces = plan.Pieces.Count, clearances = plan.Clearances.Count;
        var pieceRows = new List<int>(floors + steps + 1) { pieces };
        var clearanceRows = new List<int>(floors + steps + 1) { clearances };
        for (int i = 0; i < floors + steps; i++)
        {
            at -= dir * PieceRun;
            if (i < floors)
            {
                AddRow(plan, kit, StairPieceKind.Landing, kit.Floor, at, plan.StartHeading, base_, lanes, ground, mark: true);
                plan.Landings += lanes;
            }
            else
            {
                base_ -= PieceRise;
                AddRow(plan, kit, StairPieceKind.Stair, kit.Stair, at, plan.StartHeading, base_, lanes, ground, mark: true);
                plan.Stairs += lanes;
            }
            pieceRows.Add(plan.Pieces.Count);
            clearanceRows.Add(plan.Clearances.Count);
        }
        // First in the lists, as a plan lays its pieces from its foot (the sunk step's place): what reads a foot's walked
        // pieces in order (RoadEndRing.PadUnder) found the stair above them first, a rise over the road's end, laid no start
        // pad, and the new bottom stair, standing in the bank, was dropped as buried (at -6464.2,-2888.5 the foot was left
        // 1 m over its road).
        FirstInWalkingOrder(plan.Pieces, pieceRows);
        FirstInWalkingOrder(plan.Clearances, clearanceRows);
        if (plan.LastRowPieces >= 0) { plan.LastRowPieces += plan.Pieces.Count - pieces; plan.LastRowClearances += plan.Clearances.Count - clearances; }
        plan.FootLowered += floors + steps;
        plan.Rise += steps * PieceRise;
        plan.WalkedLength += (floors + steps) * PieceRun;
        plan.Foot = new Vector3(at.x, base_, at.y);
        return plan.Foot;
    }

    /// <summary>How <see cref="FinishFoot"/> finished a foot onto the ground past it.</summary>
    public enum FootFinish { None, Floor, FloorThenDown, DownInPlace, FloorsOn }

    /// <summary>The end rule's cases off a plan's foot (StairEnds, the one reader: the drops at the far corners of the floor its
    /// exit edge, the first stair's low edge, would take, out back along its first heading): the rule's own, and where the
    /// two corners read different cases, the other corner's after it, as a top's are read (<see cref="TopCases"/>).</summary>
    internal static StairEnds.Case[] FootCases(StairPlan plan, Func<float, float, float> ground, float width)
    {
        Vector3 foot = plan.Foot!.Value;
        return Cases(StairEnds.FloorDrops(new Vector2(foot.x, foot.z), -Forward(plan.StartHeading), foot.y, width, ground));
    }

    /// <summary>
    /// The one owner of a staircase's own foot finished onto the ground past it where no road carries on past it
    /// (RoadStairRuns.FinishFootOntoNothing), in the order a top is finished (<see cref="FinishTop"/>, the owner's of 12 Nov
    /// 2026: "landing first, then a stair down, then step back, bare last", for feet as for tops): the end rule read off the
    /// foot (<see cref="FootCases"/>), each case in turn. A floor's case, or a short one (the ground over its low edge: sunk),
    /// is finished as it is, its bare stair stepped off. A stair down's case or a step back's (over the ground by more than a
    /// ledge), landing first (the owner's, 12 Nov: "floor -> floor + stair down -> stair down in place (only where the landing doesn't
    /// fit) -> step back -> bare"): a floor at the foot's height out from it (<see cref="FootFinish.Floor"/>); the floor and
    /// stairs on down off its far edge (<see cref="FootFinish.FloorThenDown"/>); stairs on down in place
    /// (<see cref="FootFinish.DownInPlace"/>), each one stair, then up to <see cref="MostLowered"/>; then more level floors
    /// out, up to <see cref="MoreTopFloors"/> more (<see cref="FootFinish.FloorsOn"/>). A foot has no stairs past it to step
    /// back from (taking stairs off a foot only raises it): no step back is laid at a foot. Each kept only where <paramref name="finished"/> (the end reader on it: a way off, finished as
    /// its rule chose) and <paramref name="fits"/> say so; with none the plan is as it was (bare, and the audit reads it
    /// unfinished). The one owner of staircase ends (docs/OWNERS.md).
    /// </summary>
    public static FootFinish FinishFoot(StairPlan plan, Func<float, float, float> ground, float width, Func<StairPlan, bool> fits,
        Func<StairPlan, bool> finished)
    {
        if (plan.Foot == null || !plan.Reached) return FootFinish.None;
        StairPlan start = plan.Copy(deepPieces: true);
        foreach (StairEnds.Case rule in FootCases(plan, ground, width))
        {
            // A floor's or a short case: as it is.
            if (rule is StairEnds.Case.Floor or StairEnds.Case.Short) return FootFinish.None;
            var order = new List<(FootFinish finish, int floors, int steps)> { (FootFinish.Floor, 1, 0) };
            for (int s = 1; s <= MostLowered; s++) order.Add((FootFinish.FloorThenDown, 1, s));
            for (int s = 1; s <= MostLowered; s++) order.Add((FootFinish.DownInPlace, 0, s));
            for (int f = 2; f <= MoreTopFloors + 1; f++) order.Add((FootFinish.FloorsOn, f, 0));
            foreach (var (finish, floors, steps) in order)
            {
                plan.Restore(start);
                if (AddFootRows(plan, ground, width, floors, steps) != null && finished(plan) && fits(plan)) return finish;
            }
            plan.Restore(start);
        }
        return FootFinish.None;
    }

    /// <summary>The rows appended to <paramref name="list"/> (each from one of <paramref name="bounds"/> to the next), each laid
    /// a rise under the one before, moved to its front in walking order: the lowest row first.</summary>
    private static void FirstInWalkingOrder<T>(List<T> list, List<int> bounds)
    {
        int from = bounds[0], added = list.Count - from;
        if (added <= 0) return;
        var moved = new List<T>(added);
        for (int r = bounds.Count - 2; r >= 0; r--) moved.AddRange(list.GetRange(bounds[r], bounds[r + 1] - bounds[r]));
        list.RemoveRange(from, added);
        list.InsertRange(0, moved);
    }

    /// <summary>How many stairs from a flight's foot pay for standing out past a ledge's edge (<see cref="HangsOverEdge"/>): the bottom step
    /// (just the bottom step of stairs).</summary>
    public const int EdgeStairs = 1;

    /// <summary>What a foot stair standing out past a ledge's edge costs the search, a metre of the drop beside it: priced,
    /// not refused, so a foot on a road's bench along a slope (its edge as sharp) is still laid where nothing else is.</summary>
    public const float EdgeWeight = 4f;

    /// <summary>Ground more than this under a stair's base, past a ledge's edge, is a drop it stands out over.</summary>
    public const float EdgeDrop = 0.5f;

    /// <summary>A ledge: across a stair's half-width, the ground under it falls no more than this a metre.</summary>
    public const float LedgeFlat = 0.35f;

    /// <summary>An edge: across the other half-width the ground falls at least this a metre.</summary>
    public const float EdgeSharp = 1f;

    /// <summary>How far under a stair's base the ground at its middle may lie and the stair still rest on it, for
    /// <see cref="HangsOverEdge"/>'s ledge price: ground contact, not a player's step (0.3 m, the step's value when the
    /// step was 0.3 m; kept when the step went to 0.2 m, so the search's pricing of ledges does not move with it).</summary>
    internal const float LedgeContact = 0.3f;

    /// <summary>
    /// How far a stair from <paramref name="start"/> along <paramref name="dir"/>, its base at <paramref name="baseHeight"/>,
    /// stands out over the drop past a ledge's sharp edge (0 where it does not): read across its width at its low and high halves, the
    /// ground under its middle within <see cref="LedgeContact"/> of its base (or over it), flat toward one side (falling no more
    /// than <see cref="LedgeFlat"/> a metre) and falling at least <see cref="EdgeSharp"/> a metre toward the other, to more
    /// than <see cref="EdgeDrop"/> under its base. A steady slope crossed is no edge: a stair at -2551.95,1550.05
    /// stood over 0.5 a metre across, side to side.
    /// </summary>
    internal static float HangsOverEdge(Vector2 start, Vector2 dir, Vector2 right, float half, float baseHeight, Func<float, float, float> ground)
    {
        if (UnpricedLedge) return 0f;
        float drop = 0f;
        float side = ExitHalf(2f * half);
        foreach (float along in new[] { PieceRun / 4f, PieceRun * 3f / 4f })
        {
            Vector2 mid = start + dir * along;
            float g0 = ground(mid.x, mid.y);
            if (g0 < baseHeight - LedgeContact) continue;
            float gr = ground(mid.x + right.x * side, mid.y + right.y * side), gl = ground(mid.x - right.x * side, mid.y - right.y * side);
            float fallR = (g0 - gr) / side, fallL = (g0 - gl) / side;
            if (fallR >= EdgeSharp && gr < baseHeight - EdgeDrop && fallL <= LedgeFlat) drop = Mathf.Max(drop, baseHeight - gr);
            if (fallL >= EdgeSharp && gl < baseHeight - EdgeDrop && fallR <= LedgeFlat) drop = Mathf.Max(drop, baseHeight - gl);
        }
        return drop;
    }

    /// <summary>Takes away the step <see cref="Emit"/> sank under a foot (and its supports); the foot itself stays.</summary>
    public static void DropSunkFoot(StairPlan plan)
    {
        if (plan.SunkPieces <= 0) return;
        plan.Pieces.RemoveRange(0, plan.SunkPieces);
        plan.Clearances.RemoveRange(0, plan.SunkClearances);
        if (plan.LastRowPieces >= 0) { plan.LastRowPieces -= plan.SunkPieces; plan.LastRowClearances -= plan.SunkClearances; }
        plan.SunkPieces = plan.SunkClearances = 0;
    }

    /// <summary>The floor rows <paramref name="plan"/> lays at one end, before its first real stair (<paramref name="top"/>
    /// false) or after its last, each a row of floors as wide as the staircase: the rows <see cref="TrimEndFloors"/> counts
    /// its keep in. 0 where it has no real stair.</summary>
    public static int EndFloorRows(StairPlan plan, bool top, float width)
    {
        int lanes = Lanes(width);
        int first = -1, last = -1;
        for (int i = 0; i < plan.Pieces.Count; i++)
            if (plan.Pieces[i].Kind == StairPieceKind.Stair && !plan.Pieces[i].Sunk) { if (first < 0) first = i; last = i; }
        if (first < 0) return 0;
        int floors = 0;
        for (int i = top ? last + 1 : 0; i < (top ? plan.Pieces.Count : first); i++)
            if (plan.Pieces[i].Kind == StairPieceKind.Landing) floors++;
        return floors / lanes;
    }

    /// <summary>
    /// A copy of <paramref name="plan"/> with the level floors at one end taken away but the <paramref name="keep"/> floor rows
    /// next to its stair (RoadStairRuns.TopLandings: one landing row at each end), for the road to carry on over to that row
    /// (RoadStairRuns.CarryRoadOntoStairs judges whether the road can): at its foot (<paramref name="top"/> false) every floor
    /// and wedge filler before the first row kept, the foot then starting on that row, which stays as laid; at its top every
    /// floor and filler past the last row kept, level with its end, the top then ending on that row's far edge. Their
    /// clearances and cuts go with them, and the turns counted with them leave its stats; a foot's ring is the caller's to
    /// set. Never through the kept rows: null where <paramref name="keep"/> is under one, where that end has no floor past
    /// them (<see cref="EndFloorRows"/> no more than <paramref name="keep"/>), or is not as a plain layout leaves it (a sunk
    /// step already under floors, a foot lowered, a floor off the end's height).
    /// </summary>
    public static StairPlan? TrimEndFloors(StairPlan plan, bool top, int keep, float width)
    {
        if (keep < 1 || !plan.Reached || plan.Pieces.Count == 0) return null;
        int lanes = Lanes(width);
        static bool Walked(StairPiece p) => p.Kind != StairPieceKind.Support;
        // The walked pieces' clearances follow them in order (AddClearance): a stair's two stretches, a floor's or filler's one.
        static int Stretches(StairPiece p) => p.Kind == StairPieceKind.Stair ? ClearanceStretches : p.Kind == StairPieceKind.Support ? 0 : 1;
        int first = -1, last = -1;
        for (int i = 0; i < plan.Pieces.Count; i++)
            if (plan.Pieces[i].Kind == StairPieceKind.Stair && !plan.Pieces[i].Sunk) { if (first < 0) first = i; last = i; }
        if (first < 0) return null;
        // The pieces that go: [from, to).
        int from, to;
        float level;
        if (!top)
        {
            if (plan.SunkPieces > 0 || plan.FootLowered > 0) return null;
            // All but the last keep rows of floors: the foot starts on the first row kept (one of the floors, as laid).
            int remove = EndFloorRows(plan, top: false, width) - keep, rows = 0, inRow = 0;
            if (remove <= 0) return null;
            from = 0; to = -1;
            for (int i = 0; i < first && to < 0; i++)
                if (plan.Pieces[i].Kind == StairPieceKind.Landing && ++inRow == lanes) { inRow = 0; if (++rows == remove) to = i + 1; }
            if (to < 0 || to >= first || plan.Pieces[to].Kind != StairPieceKind.Landing) return null;
            level = Footprint(plan.Pieces[to]).height;
        }
        else
        {
            level = plan.End.y;
            int rows = 0, inRow = 0;
            from = -1;
            for (int i = last + 1; i < plan.Pieces.Count && from < 0; i++)
            {
                if (plan.Pieces[i].Kind != StairPieceKind.Landing) continue;
                if (++inRow == lanes) { inRow = 0; if (++rows == keep) from = i + 1; }
            }
            if (from < 0) return null;
            to = plan.Pieces.Count;
        }
        int floors = 0;
        for (int i = from; i < to; i++)
        {
            StairPiece p = plan.Pieces[i];
            if (!Walked(p)) continue;
            if (p.Kind == StairPieceKind.Stair || Mathf.Abs(Footprint(p).height - level) > 0.05f) return null;
            if (p.Kind == StairPieceKind.Landing) floors++;
        }
        if (floors == 0) return null;
        StairPlan c = plan.Copy();
        int clearancesFrom = 0, clearancesTo = 0;
        for (int i = 0; i < to; i++) { if (i < from) clearancesFrom += Stretches(plan.Pieces[i]); clearancesTo += Stretches(plan.Pieces[i]); }
        if (top) clearancesTo = c.Clearances.Count;
        var gone = new List<StairPiece>();
        for (int i = from; i < to; i++) { if (Walked(plan.Pieces[i])) gone.Add(plan.Pieces[i]); Untally(c, plan.Pieces[i]); }
        c.Pieces.RemoveRange(from, to - from);
        c.Clearances.RemoveRange(clearancesFrom, clearancesTo - clearancesFrom);
        // Their cuts: those within a footprint of a piece that went, at its walking height.
        bool Under(Vector3 cut)
        {
            foreach (StairPiece p in gone)
            {
                var (back, heading, across, depth, height) = Footprint(p);
                Vector2 d = new Vector2(cut.x, cut.z) - back;
                float along = Vector2.Dot(d, Forward(heading)), side = Vector2.Dot(d, Right(heading));
                if (along >= -0.01f && along <= depth + 0.01f && Mathf.Abs(side) <= across / 2f + 0.01f && cut.y <= height + 0.01f) return true;
            }
            return false;
        }
        c.Cuts.RemoveAll(Under);
        c.Landings = Mathf.Max(0, c.Landings - floors);
        c.WalkedLength = Mathf.Max(0f, c.WalkedLength - floors / (float)lanes * PieceRun);
        if (!top)
        {
            if (c.LastRowPieces >= 0) { c.LastRowPieces -= to - from; c.LastRowClearances -= clearancesTo - clearancesFrom; }
            // The first row kept: its back edge is the foot, and it is the heading it is laid on.
            float heading = c.Pieces[0].YawDegrees;
            Vector2 back = RowCentre(c.Pieces, 0, lanes) - Forward(heading) * (PieceRun / 2f);
            c.Foot = new Vector3(back.x, level, back.y);
            c.StartHeading = heading;
            c.FootFloorsTrimmed = floors;
        }
        else
        {
            // The last floor row kept is its last row: its end its far edge.
            int row = from - lanes, rowClearances = 0;
            for (int i = 0; i < row; i++) rowClearances += Stretches(c.Pieces[i]);
            StairPiece floor = c.Pieces[row];
            Vector2 centre = RowCentre(c.Pieces, row, lanes);
            float heading = floor.YawDegrees;
            Vector2 edge = centre + Forward(heading) * (PieceRun / 2f);
            c.End = new Vector3(edge.x, level, edge.y);
            c.EndHeading = heading;
            c.LastRowPieces = row; c.LastRowPieceCount = lanes;
            c.LastRowClearances = rowClearances; c.LastRowClearanceCount = lanes;
            c.LastRowKind = StairPieceKind.Landing; c.LastRowStart = centre - Forward(heading) * (PieceRun / 2f);
            c.LastRowHeading = heading; c.LastRowHeight = level;
            c.TopFloorsTrimmed = floors;
        }
        return c;
    }

    /// <summary>The centre of the row of <paramref name="count"/> pieces from <paramref name="from"/>.</summary>
    private static Vector2 RowCentre(List<StairPiece> pieces, int from, int count)
    {
        float x = 0f, z = 0f;
        for (int i = from; i < from + count; i++) { x += pieces[i].Position.x; z += pieces[i].Position.z; }
        return new Vector2(x / count, z / count);
    }

    /// <summary>
    /// Where the ground a walker comes onto a staircase's foot from rises into a bank (straight back from its first stair and
    /// 45 degrees either side, more than a step over the foot plus what its road climbs to it there, RoadPitches.ClimbToFoot:
    /// its level landing), a small level apron cut
    /// there at the foot's height by the road's earthwork: <see cref="ApronDepth"/> back and the foot's width plus a metre
    /// either side, blended into the bank, and only where no cut is deeper than <see cref="RoadNetworkGenerator.ShapedLimit"/> (foot
    /// at -8018,-660 against a bank: minor earthworks give it space). Returns the
    /// cut points added; none for a foot that opens onto flat ground.
    /// </summary>
    public static int CutFootApron(StairPlan plan, Func<float, float, float> ground, float width, float cap)
    {
        if (plan.Foot is not Vector3 foot || !plan.Reached) return 0;
        Vector2 at = new Vector2(foot.x, foot.z), back = Forward(Wrap(plan.StartHeading + 180f)), across = Right(plan.StartHeading);
        float height = foot.y;
        bool bank = false;
        foreach (float side in new[] { -45f, 0f, 45f })
        {
            Vector2 dir = Forward(Wrap(plan.StartHeading + 180f + side));
            foreach (float d in new[] { 1f, 2f, 4f })
            {
                Vector2 p = at + dir * d;
                if (ground(p.x, p.y) > height + StepUp + RoadPitches.ClimbToFoot(d, cap)) bank = true;
            }
        }
        if (!bank) return 0;
        var cuts = new List<Vector3>();
        float half = width / 2f + 1f, target = height - CutBelowSurface, deepest = 0f;
        for (float d = CutSpacing / 2f; d < ApronDepth; d += CutSpacing)
            for (float side = -half + CutSpacing / 2f; side < half; side += CutSpacing)
            {
                Vector2 p = at + back * d + across * side;
                float g = ground(p.x, p.y);
                if (g <= target) continue;
                deepest = Mathf.Max(deepest, g - target);
                cuts.Add(new Vector3(p.x, target, p.y));
            }
        if (cuts.Count == 0 || deepest > RoadNetworkGenerator.ShapedLimit) return 0;
        plan.Cuts.AddRange(cuts);
        plan.MaxCut = Mathf.Max(plan.MaxCut, deepest);
        plan.FootApron = cuts.Count;
        return cuts.Count;
    }

    /// <summary>How far back from a foot its apron is cut (<see cref="CutFootApron"/>).</summary>
    public const float ApronDepth = 3f;
}
