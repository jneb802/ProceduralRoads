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
    /// A staircase set off freely from a road's end (an approach onto a place's landing) sets off within this many heading
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
    /// A staircase whose foot (the first step's low edge, at the ground's height there) stands over its road's ground
    /// further back more than the road may climb to it: more stairs on down from the foot toward the road, up to
    /// <see cref="MostLowered"/>, each a rise lower, cut into the ground, the sunk step under the foot becoming the first of
    /// them (at the foot at -9308.1,690.3 the bottom of the stairs was not walkable, the earthworks too steep for a player
    /// to use to get up to the bottom stair; the road climbed 2.3 m in 5 m to it).
    /// <paramref name="back"/>: the road's ground a few metres back from the foot, <paramref name="run"/> metres away;
    /// <paramref name="cap"/> the road's grade cap. Returns the new foot (low edge, height) or null.
    /// </summary>
    public static Vector3? LowerFoot(StairPlan plan, Func<float, float, float> ground, float width, float back, float run, float cap)
    {
        if (plan.Foot is not Vector3 foot || plan.SunkPieces <= 0 || plan.FootLowered > 0) return null;
        float over = foot.y - back - cap * run;
        if (over <= StepUp) return null;
        int steps = Mathf.Min(MostLowered, Mathf.CeilToInt((over - StepUp) / PieceRise));
        return AddFootStairs(plan, ground, width, steps);
    }

    /// <summary>
    /// A staircase's foot carried on down to the road at <paramref name="height"/>: as many stairs on down from the foot
    /// along its first heading as bring its low edge to that height within half a rise, the sunk step under the foot
    /// becoming the first of them (a staircase whose foot is a short stretch of road from a crossing's end, the road there
    /// level at the end's height). Returns the new foot (low edge, height), or null where the foot is within half a rise
    /// already or has none.
    /// </summary>
    public static Vector3? LowerFootTo(StairPlan plan, Func<float, float, float> ground, float width, float height)
    {
        if (plan.Foot is not Vector3 foot) return null;
        int steps = Mathf.RoundToInt((foot.y - height) / PieceRise);
        return steps <= 0 ? null : AddFootStairs(plan, ground, width, steps);
    }

    /// <summary><paramref name="steps"/> stairs added on down from a plan's foot along its first heading, the sunk step
    /// under the foot taken away; the new foot.</summary>
    private static Vector3 AddFootStairs(StairPlan plan, Func<float, float, float> ground, float width, int steps)
    {
        Vector3 foot = plan.Foot!.Value;
        StairKit kit = StairKit.Named(plan.Kit) ?? StairKit.Stone;
        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        Vector2 dir = Forward(plan.StartHeading), at = new Vector2(foot.x, foot.z);
        // The sunk step goes: the first stair down takes its place, uncovered.
        DropSunkFoot(plan);
        float base_ = foot.y;
        for (int i = 0; i < steps; i++)
        {
            at -= dir * PieceRun;
            base_ -= PieceRise;
            AddRow(plan, kit, StairPieceKind.Stair, kit.Stair, at, plan.StartHeading, base_, lanes, ground, mark: true);
            plan.Stairs += lanes;
        }
        plan.FootLowered += steps;
        plan.Rise += steps * PieceRise;
        plan.WalkedLength += steps * PieceRun;
        plan.Foot = new Vector3(at.x, base_, at.y);
        return plan.Foot.Value;
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

    /// <summary>
    /// How far a stair from <paramref name="start"/> along <paramref name="dir"/>, its base at <paramref name="baseHeight"/>,
    /// stands out over the drop past a ledge's sharp edge (0 where it does not): read across its width at its low and high halves, the
    /// ground under its middle within <see cref="StepUp"/> of its base (or over it), flat toward one side (falling no more
    /// than <see cref="LedgeFlat"/> a metre) and falling at least <see cref="EdgeSharp"/> a metre toward the other, to more
    /// than <see cref="EdgeDrop"/> under its base. A steady slope crossed is no edge: a stair at -2551.95,1550.05
    /// stood over 0.5 a metre across, side to side.
    /// </summary>
    internal static float HangsOverEdge(Vector2 start, Vector2 dir, Vector2 right, float half, float baseHeight, Func<float, float, float> ground)
    {
        if (UnpricedLedge) return 0f;
        float drop = 0f;
        float side = Mathf.Max(half, PieceRun / 2f) - 0.1f;
        foreach (float along in new[] { PieceRun / 4f, PieceRun * 3f / 4f })
        {
            Vector2 mid = start + dir * along;
            float g0 = ground(mid.x, mid.y);
            if (g0 < baseHeight - StepUp) continue;
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

    /// <summary>
    /// Where the ground a walker comes onto a staircase's foot from rises into a bank (straight back from its first stair and
    /// 45 degrees either side, more than a step over the foot plus what a road climbs per metre), a small level apron cut
    /// there at the foot's height by the road's earthwork: <see cref="ApronDepth"/> back and the foot's width plus a metre
    /// either side, blended into the bank, and only where no cut is deeper than <see cref="ApronMaxCut"/> (foot
    /// at -8018,-660 against a bank: minor earthworks give it space). Returns the
    /// cut points added; none for a foot that opens onto flat ground.
    /// </summary>
    public static int CutFootApron(StairPlan plan, Func<float, float, float> ground, float width)
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
                if (ground(p.x, p.y) > height + StepUp + ApronBankGrade * d) bank = true;
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
        if (cuts.Count == 0 || deepest > ApronMaxCut) return 0;
        plan.Cuts.AddRange(cuts);
        plan.MaxCut = Mathf.Max(plan.MaxCut, deepest);
        plan.FootApron = cuts.Count;
        return cuts.Count;
    }

    /// <summary>How far back from a foot its apron is cut (<see cref="CutFootApron"/>).</summary>
    public const float ApronDepth = 3f;

    /// <summary>The deepest an apron may cut: the road's own earthwork cap.</summary>
    public const float ApronMaxCut = 8f;

    /// <summary>What a road climbs per metre off a foot (the default grade cap): ground behind it steeper than this over a step is a bank.</summary>
    public const float ApronBankGrade = RoadConstants.DefaultMaxRoadGrade;
}
