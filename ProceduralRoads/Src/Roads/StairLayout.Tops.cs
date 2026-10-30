using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A staircase's top in the layout (one owner): it arrives facing its road's departure (priced per step off it); finishing near an
/// area it must meet the ground it leads onto; and it is finished onto that ground (FinishTop): climbed on under rising ground,
/// then a floor out onto it, else a crest, or stairs down onto ground below it.
/// </summary>
public static partial class StairLayout
{
    /// <summary>A piece ending within <see cref="ReachRadius"/> of the top, by how far off the road's departure from the top
    /// it heads (at the top of the 41 m staircase at 5669.6,4239.7 the top should face the other
    /// direction): the flight comes in along the road, as its foot leaves it.</summary>
    private static float TopOffRoadPrice(Vector2 edge, float heading, int goal, Route route) =>
        route.TopFacing.HasValue && goal == route.Last && Vector2.Distance(edge, route.Final) <= ReachRadius
            ? FootOffRoadWeight * Mathf.Round(Mathf.Abs(Wrap(heading - route.TopFacing.Value + 180f) - 180f) / HeadingStep) : 0f;

    /// <summary>
    /// Whether a top finishing anywhere near its aim (an area to reach: a landing, a ledge) leaves the walker on the
    /// ground it leads onto: the ground just past it (<see cref="TopAhead"/> on) stands no more than a rise and a
    /// step-up over it, which a crest (<see cref="AddCrest"/>) or a step meets. Finishing within reach of the aim and up
    /// to <see cref="ArriveBelow"/> under it, two tops stopped under the shoulder they led onto (at the
    /// mountain cave entry -9293.7,706.8 and at 3522.0,-5773.6: the top did not fully make it).
    /// </summary>
    internal static bool TopReachesGround(Vector2 edge, float heading, float height, Func<float, float, float> ground)
    {
        Vector2 ahead = edge + Forward(heading) * TopAhead;
        return ground(ahead.x, ahead.y) - height <= PieceRise + StepUp;
    }

    /// <summary>How far past a top the ground it leads onto is read.</summary>
    public const float TopAhead = 2.5f;

    /// <summary>
    /// Where a staircase's top (<see cref="StairPlan.End"/>, facing <see cref="StairPlan.EndHeading"/>)
    /// stands more than <see cref="StepUp"/> over the ground ahead of it, stairs on down to that
    /// ground, facing back: a ramp from the clearing up onto the top, left buried where it meets
    /// the ground (the 2x2 stone was too high to walk from the landing on
    /// to the block, which could be a set of stairs down into the ground). At most
    /// <paramref name="most"/> rows. Returns how many pieces and clearances were added, so a
    /// caller can take them off again.
    /// </summary>
    public static (int pieces, int clearances) AddDescent(StairPlan plan, Func<float, float, float> ground, float width, int most = 2)
    {
        int pieces = plan.Pieces.Count, clearances = plan.Clearances.Count;
        StairKit kit = StairKit.Named(plan.Kit) ?? StairKit.Stone;
        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        Vector2 dir = Forward(plan.EndHeading), edge = new Vector2(plan.End.x, plan.End.z);
        float top = plan.End.y;
        for (int i = 0; i < most; i++)
        {
            Vector2 low = edge + dir * PieceRun;
            if (top - ground(low.x, low.y) <= StepUp) break;
            AddRow(plan, kit, StairPieceKind.Stair, kit.Stair, low, Wrap(plan.EndHeading + 180f), top - PieceRise, lanes, ground, mark: true, cut: false);
            plan.Stairs += lanes;
            edge = low;
            top -= PieceRise;
        }
        return (plan.Pieces.Count - pieces, plan.Clearances.Count - clearances);
    }

    /// <summary>
    /// Where the ground just past a staircase's top (<see cref="StairPlan.End"/>, facing
    /// <see cref="StairPlan.EndHeading"/>) stands more than <see cref="StepUp"/> but no more than a rise
    /// over it (and a step-up: the ridge between them is a rise over the top), a crest: one more stair up, then the same stair turned round, back to back, running down
    /// onto that ground with its low edge sunk into it (at the top of a cantilevered staircase
    /// by the mountain cave at 1920,704, which ended 0.8 m under the plateau's lip: two stone steps
    /// in opposite directions at the top of the landing connect it better). Ground anywhere between the top and a rise over it is met without exact heights.
    /// Its <see cref="StairPlan.End"/> stays the top, as with <see cref="AddDescent"/>. Returns the pieces and
    /// clearances added (none where it does not apply), for <see cref="RemoveDescent"/>.
    /// </summary>
    public static (int pieces, int clearances) AddCrest(StairPlan plan, Func<float, float, float> ground, float width, float maxCover)
    {
        if (!plan.Reached) return (0, 0);
        int pieces = plan.Pieces.Count, clearances = plan.Clearances.Count;
        StairKit kit = StairKit.Named(plan.Kit) ?? StairKit.Stone;
        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        Vector2 dir = Forward(plan.EndHeading), edge = new Vector2(plan.End.x, plan.End.z);
        float top = plan.End.y;
        // The ground the walker steps off onto: just past the down stair's low edge.
        Vector2 beyond = edge + dir * (2f * PieceRun + 0.5f);
        float lip = ground(beyond.x, beyond.y) - top;
        // Over the top floor's band (AddTopFloor) and up to a rise and a step-up: the walker at the crest's ridge (a rise
        // over the top) steps onto it.
        if (lip <= TopFloorBand || lip > PieceRise + StepUp) return (0, 0);
        // Neither stair buried deeper than the staircase may be over its walk, but the down stair's low half, sunk on purpose.
        for (float along = 0.5f; along < PieceRun + PieceRun / 2f - 0.01f; along += 0.5f)
        {
            Vector2 p = edge + dir * along;
            float surface = top + PieceRise * (along <= PieceRun ? along : 2f * PieceRun - along) / PieceRun;
            if (ground(p.x, p.y) - surface > maxCover) return (0, 0);
        }
        // And the down stair's low end on that ground across its whole width, not run out past a lip over a drop beside the
        // point read ahead (at the mountain cave at -2560.6,1549.9 the upper staircase overshot:
        // its crest's down stair out over the drop).
        if (!GroundUnderEdge(edge + dir * (2f * PieceRun), -dir, top, width, ground)) return (0, 0);
        AddRow(plan, kit, StairPieceKind.Stair, kit.Stair, edge, plan.EndHeading, top, lanes, ground, mark: true, cut: false);
        AddRow(plan, kit, StairPieceKind.Stair, kit.Stair, edge + dir * (2f * PieceRun), Wrap(plan.EndHeading + 180f), top, lanes, ground, mark: true, cut: false);
        plan.Stairs += 2 * lanes;
        return (plan.Pieces.Count - pieces, plan.Clearances.Count - clearances);
    }

    /// <summary>
    /// Whether ground stands under a stair's low end (<paramref name="low"/> its low edge, <paramref name="dir"/> along it
    /// from there, so half a metre to a metre on), from side to side of its width, no more than <see cref="StepUp"/> under
    /// <paramref name="height"/>, the height it stands at there: a walker steps off it onto the ground and it stands on
    /// it, not run out past a lip (<see cref="AddCrest"/>'s down stair, <see cref="ClimbOn"/>'s stairs).
    /// </summary>
    internal static bool GroundUnderEdge(Vector2 low, Vector2 dir, float height, float width, Func<float, float, float> ground)
    {
        Vector2 across = new Vector2(dir.y, -dir.x);
        float half = Mathf.Max(PieceRun, width) / 2f - 0.1f;
        foreach (float along in new[] { 0.5f, 1f })
            foreach (float side in new[] { -half, 0f, half })
            {
                Vector2 p = low + dir * along + across * side;
                if (ground(p.x, p.y) < height - StepUp) return false;
            }
        return true;
    }

    /// <summary>
    /// Where the ground just past a staircase's top (<see cref="StairPlan.End"/>, facing <see cref="StairPlan.EndHeading"/>)
    /// lies within <see cref="TopFloorBand"/> of its height, a floor level with the top carried out onto it, sunk where the
    /// ground stands higher (at the mountain cave entry at 1932.2,697.1 there was a gap at the top of the stair
    /// run, the plateau 0-0.5 m over the top edge; one stone_floor_2x2 laid out from the top fixed it). Not where the top is a
    /// floor already. Returns the pieces and clearances added, for <see cref="RemoveDescent"/>.
    /// </summary>
    public static (int pieces, int clearances) AddTopFloor(StairPlan plan, Func<float, float, float> ground, float width)
    {
        if (!plan.Reached || plan.LastRowKind == StairPieceKind.Landing && plan.LastRowPieces >= 0) return (0, 0);
        Vector2 dir = Forward(plan.EndHeading), edge = new Vector2(plan.End.x, plan.End.z);
        float top = plan.End.y;
        // Where the walker steps off it (its far half and just past it): no more than the band over the top (higher, a
        // crest), nor more than the band under it (lower, steps down or the road's earthwork).
        float lip = TopLip(plan, ground);
        if (Mathf.Abs(lip - top) > TopFloorBand) return (0, 0);
        // Nor where the ground the walker steps off onto, across the floor's whole far edge, lies more than a step under it:
        // its edge stood proud of the road (at the black marble top at 463.6,6503.4, the floor 0.2-0.7 m over
        // the road past it, high enough that a player can get stuck on it briefly). The road's
        // own earthwork meets the top instead.
        Vector2 across = Right(plan.EndHeading);
        float half = Mathf.Max(PieceRun, width) / 2f - 0.1f, low = float.PositiveInfinity;
        foreach (float side in new[] { -half, 0f, half })
        {
            Vector2 off = edge + dir * (PieceRun + ProudReach) + across * side;
            low = Mathf.Min(low, ground(off.x, off.y));
        }
        if (top - low > StepUp) return (0, 0);
        int pieces = plan.Pieces.Count, clearances = plan.Clearances.Count;
        StairKit kit = StairKit.Named(plan.Kit) ?? StairKit.Stone;
        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        AddRow(plan, kit, StairPieceKind.Landing, kit.Floor, edge, plan.EndHeading, top, lanes, ground, mark: true, cut: false);
        plan.Landings += lanes;
        return (plan.Pieces.Count - pieces, plan.Clearances.Count - clearances);
    }

    /// <summary>The ground past a top within this of its height takes a floor (<see cref="AddTopFloor"/>); higher, a crest.
    /// Two hand-laid floors: at 1932,697 the ground there 0-0.5 m over the top, at 6465,-1522 up to 0.8 m nearby
    /// with the floor flush with or slightly under the snow where the walker steps off.</summary>
    public const float TopFloorBand = 0.6f;

    /// <summary>How far past a top floor's far edge the ground the walker steps off onto is read (<see cref="AddTopFloor"/>).</summary>
    public const float ProudReach = 0.5f;

    /// <summary>The highest ground where the walker steps off a floor laid on from a top: over the floor's far half and
    /// half a metre past it (1-2.5 m on).</summary>
    public static float TopLip(StairPlan plan, Func<float, float, float> ground)
    {
        Vector2 dir = Forward(plan.EndHeading), edge = new Vector2(plan.End.x, plan.End.z);
        float high = float.NegativeInfinity;
        for (float along = PieceRun / 2f; along <= PieceRun + 0.5f + 0.01f; along += 0.5f)
        {
            Vector2 p = edge + dir * along;
            high = Mathf.Max(high, ground(p.x, p.y));
        }
        return high;
    }

    /// <summary>How far a crest's ridge must stand over the ground there to be laid (<see cref="ClimbOn"/>): a crest
    /// at 1920,704 stood 0.18 m over the plateau; at -2560.6,1549.9 0.04 m, which overshot.</summary>
    public const float CrestShows = 0.15f;

    /// <summary>Most stairs <see cref="ClimbOn"/> adds past a top.</summary>
    public const int MostClimbedOn = 3;

    /// <summary>
    /// Where the ground past a staircase's top (<see cref="StairPlan.End"/>, facing <see cref="StairPlan.EndHeading"/>)
    /// keeps rising past where a crest from it could land, more stairs straight on, up to <see cref="MostClimbedOn"/>,
    /// until a crest would (AddCrest; or nothing is needed). The search finishes
    /// within reach of its aim and up to <see cref="ArriveBelow"/> under it, and two tops stopped under the shoulder they
    /// led onto (the mountain cave entry at -9293.7,706.8, where the top did not fully make it, 1.1 m under the
    /// shoulder 3 m on; and at 3522.0,-5773.6, 1-3 m under the knoll). Each stair added keeps its walk no deeper than
    /// <paramref name="maxCover"/> in the ground. Moves <see cref="StairPlan.End"/> to the new top. Returns the pieces
    /// and clearances added, for <see cref="RemoveDescent"/>; <paramref name="stairs"/> how many rows.
    /// </summary>
    public static (int pieces, int clearances) ClimbOn(StairPlan plan, Func<float, float, float> ground, float width, float maxCover, out int stairs)
    {
        stairs = 0;
        int pieces = plan.Pieces.Count, clearances = plan.Clearances.Count;
        if (!plan.Reached) return (0, 0);
        StairKit kit = StairKit.Named(plan.Kit) ?? StairKit.Stone;
        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        Vector2 dir = Forward(plan.EndHeading), edge = new Vector2(plan.End.x, plan.End.z);
        float top = plan.End.y;
        bool onToGround = false;
        while (stairs < MostClimbedOn && !onToGround)
        {
            // Until a crest from here would land on it (AddCrest reads the same point); but one more where a crest's ridge
            // would not stand out of the ground (within CrestShows of it): its down stair would be buried whole, an up stair
            // and a hump past the top (at the mountain cave at -2560.6,1549.9, the ground at the ridge 0.04 m
            // under it and rising to 158.5 past it, the upper staircase overshot). The top then finishes on
            // the ground's level, on a floor (AddTopFloor), not a crest.
            Vector2 beyond = edge + dir * (2f * PieceRun + 0.5f);
            if (ground(beyond.x, beyond.y) - top <= PieceRise + StepUp)
            {
                Vector2 ridge = edge + dir * PieceRun;
                if (ground(ridge.x, ridge.y) < top + PieceRise - CrestShows) break;
                onToGround = true;
            }
            bool clear = true;
            for (float along = 0.5f; along < PieceRun; along += 0.5f)
            {
                Vector2 p = edge + dir * along;
                if (ground(p.x, p.y) - (top + PieceRise * along / PieceRun) > maxCover) { clear = false; break; }
            }
            if (!clear) break;
            // Standing on the ground at its low end, not run on past a lip.
            if (!GroundUnderEdge(edge, dir, top, width, ground)) break;
            AddRow(plan, kit, StairPieceKind.Stair, kit.Stair, edge, plan.EndHeading, top, lanes, ground, mark: true, cut: false);
            stairs++;
            edge += dir * PieceRun;
            top += PieceRise;
        }
        if (stairs == 0) return (0, 0);
        plan.Stairs += stairs * lanes;
        plan.Rise += stairs * PieceRise;
        plan.WalkedLength += stairs * PieceRun;
        plan.End = new Vector3(edge.x, top, edge.y);
        return (plan.Pieces.Count - pieces, plan.Clearances.Count - clearances);
    }

    /// <summary>How <see cref="FinishTop"/> finished a top onto the ground past it.</summary>
    public enum TopFinish { None, TopFloor, Crest }

    /// <summary>
    /// The one owner of a staircase's top finished onto the ground past it: under ground that keeps
    /// rising, more stairs straight on (<see cref="ClimbOn"/>, its walk no deeper than <paramref name="cover"/> in the
    /// ground); then a floor level with the top out onto ground within its band (<see cref="AddTopFloor"/>); else a crest,
    /// up one rise and back down onto the lip (<see cref="AddCrest"/>, as deep as <paramref name="cover"/>). Each is kept
    /// only where <paramref name="fits"/> says the plan may stand so (an approach: out of its place's pit and rock); the
    /// climb is kept where it fits whatever follows it (<paramref name="climbed"/>). Returns the finish laid, if any.
    /// </summary>
    public static TopFinish FinishTop(StairPlan plan, Func<float, float, float> ground, float width, float cover, Func<StairPlan, bool> fits,
        out bool climbed)
    {
        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        Vector3 end = plan.End;
        float rise = plan.Rise, walked = plan.WalkedLength;
        int cuts = plan.Cuts.Count;
        var climb = ClimbOn(plan, ground, width, cover, out int rows);
        climbed = climb.pieces > 0;
        if (climbed && !fits(plan))
        {
            RemoveDescent(plan, climb);
            plan.Stairs -= rows * lanes;
            plan.End = end; plan.Rise = rise; plan.WalkedLength = walked;
            plan.Cuts.RemoveRange(cuts, plan.Cuts.Count - cuts);
            climbed = false;
        }
        // The ground past the top within the band of it: a floor level with the top, out onto it.
        var floor = AddTopFloor(plan, ground, width);
        if (floor.pieces > 0)
        {
            if (fits(plan)) return TopFinish.TopFloor;
            RemoveDescent(plan, floor);
            plan.Landings -= lanes;
        }
        // Under the lip by less than a rise: up one more and back down onto it, back to back.
        var crest = AddCrest(plan, ground, width, cover);
        if (crest.pieces > 0)
        {
            if (fits(plan)) return TopFinish.Crest;
            RemoveDescent(plan, crest);
            plan.Stairs -= 2 * lanes; // its two stair rows
        }
        return TopFinish.None;
    }

    /// <summary>
    /// Where a staircase ends on a floor whose surface stands more than <see cref="StepUp"/> over
    /// the ground just past it, that floor becomes a stair down, in its own place: its back edge
    /// at the floor's height, its front a rise lower, buried where it meets the ground. Nothing
    /// is laid further on, so it cannot reach a place's pit or rock (at Hildir's cave the last 2x2 is probably not
    /// needed, or is replaced with stairs going down into the landing). Returns whether it did. Only for the last row emitted (nothing after it).
    /// </summary>
    public static bool StepDownTop(StairPlan plan, Func<float, float, float> ground, float width)
    {
        if (plan.LastRowPieces < 0 || plan.LastRowKind != StairPieceKind.Landing) return false;
        Vector2 dir = Forward(plan.LastRowHeading);
        Vector2 front = plan.LastRowStart + dir * PieceRun, past = front + dir * 1f;
        if (plan.LastRowHeight - ground(past.x, past.y) <= StepUp) return false;
        StairKit kit = StairKit.Named(plan.Kit) ?? StairKit.Stone;
        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        // Only the row's own pieces (and its columns): supports sunk later for other pieces stay.
        plan.Pieces.RemoveRange(plan.LastRowPieces, plan.LastRowPieceCount);
        plan.Clearances.RemoveRange(plan.LastRowClearances, plan.LastRowClearanceCount);
        AddRow(plan, kit, StairPieceKind.Stair, kit.Stair, front, Wrap(plan.LastRowHeading + 180f), plan.LastRowHeight - PieceRise, lanes, ground, mark: true, cut: false);
        plan.Landings -= lanes; plan.Stairs += lanes;
        plan.LastRowKind = StairPieceKind.Stair;
        plan.End = new Vector3(front.x, plan.LastRowHeight - PieceRise, front.y);
        return true;
    }

    /// <summary>Take off what <see cref="AddDescent"/> added.</summary>
    public static void RemoveDescent(StairPlan plan, (int pieces, int clearances) added)
    {
        plan.Pieces.RemoveRange(plan.Pieces.Count - added.pieces, added.pieces);
        plan.Clearances.RemoveRange(plan.Clearances.Count - added.clearances, added.clearances);
    }
}
