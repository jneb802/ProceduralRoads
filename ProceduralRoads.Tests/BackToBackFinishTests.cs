using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The finish order of a proud top and of a foot (StairLayout.FinishTop, StairLayout.FinishFoot, the one owner): back to back
/// before a landing (the owner's of 19 Nov 2026, replacing 12 Nov's landing first: "floor -> stair down in place (back to
/// back) -> floor + stair down -> step back -> bare"). In game a tower's approach top, its last stair's walk 0.75 and 0.32 m
/// over the ground at the far corners of a floor off it, was finished with a floor and a stair down off it, aimed well onto
/// the landing; a stair down straight off the stair also came down onto the ground there. Where the stair down in place
/// fits it is laid; where it does not, the floor and a stair down still are (assistant-written, 19 Nov 2026).
/// </summary>
[Collection("StairLayout")]
public class BackToBackFinishTests
{
    /// <summary>The tower's top, read in game: four stairs north from (0, 0) at 67.35 m, their walk's top 71.35 m at z = 8;
    /// past it the ground across the floor's far corners at 70.60 m (left) and 71.03 m (right), 0.75 and 0.32 m under the
    /// top (the end rule's stair down, StairEnds.Case.Reverse); far below under the flight.</summary>
    private static float Tower(float x, float z) => z < 8f ? -50f : 70.815f + 0.215f * Mathf.Clamp(x, -1f, 1f);

    private static StairPlan TowerTop() => StairLayout.LayFlight(new Vector2(0f, 0f), 67.35f, new[] { 0f, 0f, 0f, 0f }, StairKit.Stone, Tower);

    private static bool HasFloor(StairPlan p) => p.Pieces.Exists(q => q.Kind == StairPieceKind.Landing);

    [Fact]
    public void AProudTopWhereBothFinishesFitIsFinishedBackToBack()
    {
        Assert.Equal(StairEnds.Case.Reverse, StairLayout.TopCase(TowerTop(), Tower, 2f));
        // The floor and a stair down off it fits here (the finish laid in game).
        StairPlan landing = TowerTop();
        Assert.Equal(StairLayout.TopFinish.FloorThenDown, RoadStairRuns.FinishOverLedge(landing, Tower, 2f, HasFloor));
        // Both fitting, the stair down straight off the last stair is laid, no floor between them.
        StairPlan plan = TowerTop();
        int stairs = plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair);
        Assert.Equal(StairLayout.TopFinish.LandingThenDown, RoadStairRuns.FinishOverLedge(plan, Tower, 2f, _ => true));
        Assert.False(HasFloor(plan));
        Assert.Equal(stairs + 1, plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair));
        StairLayout.TerminalExit down = StairLayout.Terminal(plan)!;
        Assert.True(down.Descending && down.StepsDownOnto(Tower));
        StairEnds.End top = RoadStairRuns.TopEnd(plan, Tower)!;
        Assert.True(top.Verdict == StairEnds.Verdict.Ok && top.FloorsPast == 0 && top.StairsPast == 1 && StairEnds.Unfinished(top) == null, StairEnds.Line(top));
    }

    [Fact]
    public void AProudTopWhereBackToBackDoesNotFitIsFinishedWithTheFloorAndAStairDown()
    {
        // The stair down in place is tried first and refused (here: nothing may stand within 2 m past the top but a floor),
        // then the floor and a stair down off it is laid.
        var seen = new List<bool>();
        bool Fits(StairPlan p) { seen.Add(HasFloor(p)); return !p.Pieces.Exists(q => q.Kind == StairPieceKind.Stair && q.Position.z > 8f && q.Position.z < 10f); }
        StairPlan plan = TowerTop();
        Assert.Equal(StairLayout.TopFinish.FloorThenDown, RoadStairRuns.FinishOverLedge(plan, Tower, 2f, Fits));
        Assert.True(seen.Count >= 2 && !seen[0], "back to back not tried first: " + string.Join(",", seen));
        Assert.True(seen[seen.Count - 1]);
        Assert.Single(plan.Pieces, p => p.Kind == StairPieceKind.Landing);
        StairEnds.End top = RoadStairRuns.TopEnd(plan, Tower)!;
        Assert.True(top.Verdict == StairEnds.Verdict.Ok && top.FloorsPast == 1 && top.StairsPast == 1 && StairEnds.Unfinished(top) == null, StairEnds.Line(top));
    }

    /// <summary>A foot with no road past it at (0, 60, 0), four stairs climbing north from it; out back from it (south) the
    /// ground the tower's top stood over, mirrored: 0.75 and 0.32 m under the foot at a floor's far corners.</summary>
    private static float Foot(float x, float z) => z >= 0f ? 60f + z * 0.5f - 0.05f : 60f - 0.535f + 0.215f * Mathf.Clamp(x, -1f, 1f);

    private static StairPlan FootFlight()
    {
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 60f, new[] { 0f, 0f, 0f, 0f }, StairKit.Stone, Foot);
        plan.Foot ??= new Vector3(0f, 60f, 0f);
        return plan;
    }

    private static bool FloorOut(StairPlan p) => p.Pieces.Exists(q => q.Kind == StairPieceKind.Landing && q.Position.z < 0f);

    [Fact]
    public void AFootWhereBothFinishesFitIsFinishedBackToBack()
    {
        Assert.Contains(StairEnds.Case.Reverse, StairLayout.FootCases(FootFlight(), Foot, RoadStairRuns.Width));
        // The floor and stairs on down off it fits here.
        StairPlan landing = FootFlight();
        Assert.Equal(StairLayout.FootFinish.FloorThenDown, StairLayout.FinishFoot(landing, Foot, RoadStairRuns.Width, FloorOut,
            p => RoadStairRuns.FootFinished(p, Foot)));
        // Both fitting, the stair on down in place.
        StairPlan plan = FootFlight();
        Assert.Equal(StairLayout.FootFinish.DownInPlace, StairLayout.FinishFoot(plan, Foot, RoadStairRuns.Width, _ => true,
            p => RoadStairRuns.FootFinished(p, Foot)));
        Assert.DoesNotContain(plan.Pieces, p => p.Kind == StairPieceKind.Landing);
        Assert.True(plan.Foot!.Value.y < 60f - 0.5f, $"foot at {plan.Foot}");
    }

    [Fact]
    public void AFootWhereBackToBackDoesNotFitIsFinishedWithTheFloorAndAStairDown()
    {
        var seen = new List<bool>();
        bool Fits(StairPlan p) { bool floor = FloorOut(p); seen.Add(floor); return floor; }
        StairPlan plan = FootFlight();
        Assert.Equal(StairLayout.FootFinish.FloorThenDown, StairLayout.FinishFoot(plan, Foot, RoadStairRuns.Width, Fits,
            p => RoadStairRuns.FootFinished(p, Foot)));
        Assert.True(seen.Count >= 2 && !seen[0], "back to back not tried first: " + string.Join(",", seen));
        Assert.Single(plan.Pieces, p => p.Kind == StairPieceKind.Landing);
    }
}
