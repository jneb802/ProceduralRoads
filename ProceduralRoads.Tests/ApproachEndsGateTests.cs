using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The staircase-end gate at the mod's own settings, on real ground: ways onto placed locations' landings captured offline as
/// generation laid them (<see cref="ApproachCaptureFile"/>), each laid again whole through the production planner
/// (RoadStairRuns.PlanPlacedLocationApproach with production's arguments: the planning lattice, the locations' preload, the footprints'
/// own test, the location's shape, its real refusals and the finish's way-off check) under the mod's defaults
/// (<see cref="PlainRoads.WithModDefaults"/>). The end at the location has a way off it (StairEnds: no miss, no gap) and is
/// finished as its rule chose (none unfinished), and each approach is laid, reaching its landing, with the finish the owner
/// approved for its top: so an approach refused, or one laid with no finish, fails as surely as a bad end.
/// The network-wide gate (NetworkRoundTripTests) runs one synthetic world on plain roads; these are the tops the owner was
/// shown, and corrected, in game.
/// </summary>
public sealed class ApproachEndsGateTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public void Dispose()
    {
        PlacedLocations.Reset();
        RoadLocationProtection.Reset();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>What the end at the location is finished with: a floor stepped off level (no stair down past it), a stair down
    /// (back to back, off a floor, or a crest's), or more than one floor (a floor laid on past the first).</summary>
    public enum Finish { Floor, Down, Floors }

    /// <summary>The captured approaches, the finish each top has, and why that is the approved one.</summary>
    public static IEnumerable<object[]> Cases() => new[]
    {
        // A cave's approach whose top floor stood 0.6-0.8 m proud of the ground but at one corner (in game): stairs down,
        // the landing first (the top 0.25 m over the ground off it at both corners: the reverse's case).
        new object[] { "approach-capture-cave-down.txt", Finish.Down },
        // The same cave from the road's other end (the staircase the owner walked): a floor, stepped off within a step, as
        // laid before the one rule of whether a floor lands (the owner's, 12 Nov: a free edge with both corners within a
        // step). That floor's corners on the planning ground stood 0.45/-0.10 m at its back and 0.05/-0.39 m at its far
        // edge: no edge of it lands, so the top is crested now, a stair climbed and a stair down off it onto the landing.
        new object[] { "approach-capture-cave-floor.txt", Finish.Down },
        // A cabin's approach whose top walked off 0.3 m (in game): a floor, as laid before the one rule. Its corners stood
        // 0.21/0.88 m at its back and -0.14/0.25 m at its far edge: no edge of it lands, so it is laid otherwise now, its
        // top finished with two floors, the second's far edge within a step.
        new object[] { "approach-capture-cabin.txt", Finish.Floors },
        // A stone tower's raised landing: the top under the landing's lip, climbed on and crested with a stair down onto it
        // (it was laid bare, then refused).
        new object[] { "approach-capture-tower-crest.txt", Finish.Down },
        // A tower's raised landing at its corner, 0.17 m under its level ground: a floor onto it (refused while a surface
        // had to stand within a centimetre of the landing).
        new object[] { "approach-capture-tower-corner.txt", Finish.Floor },
        // Hildir's shelf: the top within a step of the shelf it climbs onto, never a metre under it.
        new object[] { "approach-capture-shelf.txt", Finish.Floor },
        // A frost cave's top over ground falling away on one side (the sides disagree by 1.9 m): a floor does not land there,
        // so level floors are laid on until one does ("1-2 more level 2x2 floors").
        new object[] { "approach-capture-two-floors.txt", Finish.Floors },
        // (Up from the road over a crest and down onto the landing beyond it was laid with its second leg over its first; it is
        // not laid so, AWayOverACrestIsNeverLaidWithItsSecondLegOverItsFirst.)
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryCapturedApproachIsLaidWithItsTopFinishedAsItsRuleChoseAtTheModsDefaults(string fixture, Finish expected)
    {
        var (c, span, end, line) = Lay(fixture, joinedAims: true);
        Finish finish = end.StairsPast > 0 ? Finish.Down : end.FloorsPast > 1 ? Finish.Floors : Finish.Floor;
        Assert.True(finish == expected && (expected == Finish.Down || end.FloorsPast >= 1), $"{line}: finished {finish}, {expected} expected");
    }

    /// <summary>Each captured approach's laid top, finished by the approach's own finish (RoadStairRuns.FinishApproachTop)
    /// through its own gates (its refusals of a finished top and the end reader's way off, handed out by
    /// RoadStairRuns.BeforeTopFinish), takes the finish the owner approved for it: the landing then the stair down off it, a
    /// crest onto a raised landing, a floor, two floors as one more level floor on.</summary>
    public static IEnumerable<object[]> Finishes() => new[]
    {
        new object[] { "approach-capture-cave-down.txt", StairLayout.TopFinish.LandingThenDown },
        new object[] { "approach-capture-tower-crest.txt", StairLayout.TopFinish.Crest },
        // Its floor does not land by the one rule (its corners above): a stair climbed, a floor off it and the stair down off
        // that floor, its exit (crested before the floor then a stair down took the stair down for the floor's exit).
        new object[] { "approach-capture-cave-floor.txt", StairLayout.TopFinish.FloorThenDown },
        new object[] { "approach-capture-two-floors.txt", StairLayout.TopFinish.TopFloor },
    };

    [Theory]
    [MemberData(nameof(Finishes))]
    public void EachCapturedTopTakesItsApprovedFinishThroughTheApproachsOwnGates(string fixture, StairLayout.TopFinish expected)
    {
        var (_, span, finished, _) = LaidTop(fixture);
        Assert.True(finished.finish == expected, $"{fixture}: finished {finished.finish}, {expected} expected");
        Assert.True(StairRun.FromPlan(span.FromPoint, span.ToPoint, RoadStairRuns.Width, span.Plan).Pieces.Count > 0, fixture);
    }

    /// <summary>
    /// The overshoot step back through the approach's own gates: the cave's laid top (its layout climbing onto the landing
    /// from the road's other end) carried on three stairs further up its flight, its last stair 1.5 m and more over the
    /// landing past it (it overshot: more than a ledge). The owner's rule (1-2 fewer stairs, a level 2x2 floor off the stair
    /// before, landing within a step; never a bare top first; "land within StepUp/LedgeDrop"): finished by the approach's own
    /// finish and gates, it steps back, and has a way off it. No floor off a stair back along it lands by the one rule (a free
    /// edge with both corners within a step: the floor it stepped back onto before stood within a step at one far corner
    /// only), so it steps back to the stair a ledge or less over the landing and lays the stair down off it.
    /// </summary>
    [Fact]
    public void AnApproachTopThatOvershootsStepsBackThroughTheApproachsOwnGates()
    {
        var (_, _, _, before) = LaidTop("approach-capture-cave-floor.txt");
        var (plan, fits, ground) = before;
        int stairs = plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair);
        StairLayout.TerminalExit exit = StairLayout.Terminal(plan)!;
        Assert.False(exit.Descending);
        Vector3 on = new Vector3(StairLayout.Forward(plan.EndHeading).x * StairLayout.PieceRun, StairLayout.PieceRise, StairLayout.Forward(plan.EndHeading).y * StairLayout.PieceRun);
        for (int k = 1; k <= 3; k++)
            foreach (StairPiece p in exit.Pieces)
                plan.Pieces.Add(new StairPiece { Kind = p.Kind, Prefab = p.Prefab, Position = p.Position + on * k, YawDegrees = p.YawDegrees });
        plan.End += on * 3f;
        plan.LastRowPieces = -1;
        Assert.Equal(StairEnds.Case.StepBack, StairLayout.TopCase(plan, ground, RoadStairRuns.Width));

        var (finish, bare) = RoadStairRuns.FinishApproachTop(plan, ground, fits, refuseBare: true);
        StairEnds.End top = RoadStairRuns.TopEnd(plan, ground)!;
        Assert.True(finish == StairLayout.TopFinish.StepBack && !bare, $"finished {finish}; {StairEnds.Line(top)}; {StairLayout.LastStepBack}");
        Assert.True(top.Verdict == StairEnds.Verdict.Ok && top.FloorsPast == 0 && top.StairsPast == 1, StairEnds.Line(top));
        Assert.True(StairLayout.Terminal(plan)!.Descending, StairEnds.Line(top));
        Assert.InRange(plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair), stairs - 2 * exit.Pieces.Count, stairs + 2 * exit.Pieces.Count);
        Assert.True(fits(plan), "the stepped-back top is refused by the approach's own gates");
    }

    /// <summary>The captured approach laid again, each top handed out before its finish (RoadStairRuns.BeforeTopFinish), and
    /// the one its laid staircase was finished from: that top, finished again by the approach's own finish and gates, is the
    /// laid staircase. Returns how it was finished, and the top as it stood before (a copy), with those gates and its ground.</summary>
    private (ApproachCaptureFile c, RoadStairSpan span, (StairLayout.TopFinish finish, bool bare) finished,
        (StairPlan plan, Func<StairPlan, bool> fits, Func<float, float, float> ground) before) LaidTop(string fixture)
    {
        var tops = new List<(StairPlan before, StairPlan after, StairLayout.TopFinish finish, bool bare, Func<StairPlan, bool> fits, Func<float, float, float> ground)>();
        RoadStairRuns.BeforeTopFinish = (plan, fits, ground) =>
        {
            StairPlan after = plan.Copy(deepPieces: true);
            var (finish, bare) = RoadStairRuns.FinishApproachTop(after, ground, fits, refuseBare: true);
            tops.Add((plan, after, finish, bare, fits, ground));
        };
        try
        {
            var (c, span, _, _) = Lay(fixture, joinedAims: true);
            int laid = tops.FindLastIndex(t => Same(t.after, span.Plan));
            Assert.True(laid >= 0, $"{fixture}: none of its {tops.Count} tops finished is the laid staircase");
            var t = tops[laid];
            return (c, span, (t.finish, t.bare), (t.before.Copy(deepPieces: true), t.fits, t.ground));
        }
        finally { RoadStairRuns.BeforeTopFinish = null; }
    }

    private static bool Same(StairPlan a, StairPlan b) =>
        a.Pieces.Count == b.Pieces.Count && a.Pieces.Zip(b.Pieces, (p, q) => p.Kind == q.Kind && p.Prefab == q.Prefab
            && Vector3.Distance(p.Position, q.Position) < 0.001f && Mathf.Abs(p.YawDegrees - q.YawDegrees) < 0.01f).All(x => x);

    /// <summary>Lay the captured approach again under the mod's defaults and read the end at its location: laid, onto its
    /// landing, in its kit, the end there with a way off it and finished as its rule chose.</summary>
    private (ApproachCaptureFile c, RoadStairSpan span, StairEnds.End end, string line) Lay(string fixture, bool joinedAims)
    {
        ApproachCaptureFile c = ApproachCaptureFile.Load(fixture);
        var world = new ApproachCaptureFile.World(c);
        _scope.WithWorld(world);
        PlacedLocation location = c.Prepare(world);
        RoadStairSpan? span = null;
        string why = "";
        PlainRoads.WithModDefaults(() => span = c.Replay(world, location, out why, joinedAims));

        // Laid, onto its landing, in the kit generation laid it in: refusing every layout does not pass.
        Assert.True(span != null, $"{fixture}: no approach laid: {why}");
        StairPlan plan = span!.Plan;
        Assert.True(plan.Reached, $"{fixture}: {plan.Reason}");
        Assert.Equal(c.Kit, plan.Kit);
        string laid = string.Join("; ", c.Differences(plan));

        StairEnds.End end = LocationEnd(c, span, world);
        string line = $"{fixture}: {StairEnds.Line(end)}{(laid == "" ? "" : " (laid otherwise than captured: " + laid + ")")}";
        Assert.True(end.Verdict == StairEnds.Verdict.Ok, line);
        Assert.True(StairEnds.Unfinished(end) == null, line + " UNFINISHED: " + StairEnds.Unfinished(end));
        return (c, span, end, line);
    }

    /// <summary>The end of the approach at its location (the one further from the road's end), read by the one end reader on
    /// the ground the approach was laid out on, as its finish judges it (RoadStairRuns.WayOffItsTop). The end at the road's
    /// end is the road's, read with it by the network's gate.</summary>
    internal static StairEnds.End LocationEnd(ApproachCaptureFile c, RoadStairSpan span, WorldGenerator world)
    {
        StairRun run = StairRun.FromPlan(span.FromPoint, span.ToPoint, RoadStairRuns.Width, span.Plan);
        List<StairEnds.End> ends = StairEnds.Read(run, RoadStairRuns.LayoutGround.Lattice(world).Read);
        Vector2 road = c.Request.RoadEnd;
        return ends.OrderByDescending(e => e.Edge is Vector3 p ? Vector2.Distance(new Vector2(p.x, p.z), road) : -1f).First();
    }
    /// <summary>An approach's top climbing onto a stone tower's raised landing, as its layout left it, finished through the
    /// approach's own gates (RoadStairRuns.BeforeTopFinish: its refusals of a finished top, not a copy of them): the stair climbed on with its stair down
    /// off it (the crest) steps down onto the landing, a walker stepping off it; the climb alone, short of the landing's lip, is
    /// refused. The climb is judged with the stair down laid
    /// after it, not refused alone before it, which left the top a bare stair.</summary>
    [Fact]
    public void AnApproachTopUnderARaisedLandingIsCrestedThroughItsOwnGates()
    {
        var (_, _, _, before) = LaidTop("approach-capture-tower-crest.txt");
        var (plan, fits, ground) = before;
        var (finish, refusals) = Finished(plan, ground, fits);
        StairEnds.End top = RoadStairRuns.TopEnd(plan, ground)!;
        Assert.True(finish == StairLayout.TopFinish.Crest, $"finished {finish} ({string.Join(", ", refusals)}); {StairEnds.Line(top)}");
        // The climb alone stops short of the landing's lip: refused alone, kept with its stair down after it.
        Assert.Contains("does not meet the raised landing's lip", refusals);
        Assert.True(StairLayout.Terminal(plan)!.Descending, StairEnds.Line(top));
        Assert.True(top.Verdict == StairEnds.Verdict.Ok && top.StairsPast == 1, StairEnds.Line(top));
        Assert.True(fits(plan));
    }

    /// <summary>An approach's top climbing a cliff to a stone tower's raised landing at its corner, as its layout left it,
    /// finished through the approach's own gates: a floor onto the landing, a step under its level ground, steps up onto it
    /// within a step and is kept: a walking surface may stand up to a step under a raised landing
    /// (RoadStairRuns.RaisedLandingSurfaceAllowed). Held to a centimetre, no floor onto it was clear of the landing.</summary>
    [Fact]
    public void AnApproachTopAtARaisedLandingsCornerTakesAFloorOntoItThroughItsOwnGates()
    {
        var (_, _, _, before) = LaidTop("approach-capture-tower-corner.txt");
        var (plan, fits, ground) = before;
        int floors = plan.Pieces.Count(p => p.Kind == StairPieceKind.Landing);
        var (finish, refusals) = Finished(plan, ground, fits);
        StairEnds.End top = RoadStairRuns.TopEnd(plan, ground)!;
        Assert.True(finish == StairLayout.TopFinish.TopFloor, $"finished {finish} ({string.Join(", ", refusals.Distinct())}); {StairEnds.Line(top)}");
        Assert.True(plan.Pieces.Count(p => p.Kind == StairPieceKind.Landing) > floors, $"floors {floors} -> {plan.Pieces.Count(p => p.Kind == StairPieceKind.Landing)}; refused {string.Join(", ", refusals.Distinct())}");
        Assert.True(top.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(top));
        Assert.True(fits(plan));
    }

    /// <summary>
    /// A way over a crest to a stone tower's landing, as generation laid it before (the captured span): its second leg, searched
    /// down from the crest apart from the first, ran beside and over the first's flight up (in game, nine stairs down laid over
    /// the climb: "stairs all overlap"). The join refuses a second leg over the first, seen from above (RoadStairRuns.Join,
    /// StairLayout.OverlapsItself); laid again through the production planner, the approach has no walked piece over another,
    /// or is not laid.
    /// </summary>
    [Fact]
    public void AWayOverACrestIsNeverLaidWithItsSecondLegOverItsFirst()
    {
        ApproachCaptureFile c = ApproachCaptureFile.Load("approach-capture-over-a-crest.txt");
        var captured = new StairPlan { Kit = c.Kit, Reached = true };
        captured.Pieces.AddRange(c.Laid);
        // The second leg is emitted from the landing up: its first piece the floor on the landing.
        int second = c.Laid.FindIndex(p => p.Kind == StairPieceKind.Landing);
        Assert.True(second > 0);
        Assert.True(StairLayout.OverlapsItself(captured, second), "the captured legs do not overlap: the case is lost");

        var world = new ApproachCaptureFile.World(c);
        _scope.WithWorld(world);
        PlacedLocation location = c.Prepare(world);
        RoadStairSpan? span = null;
        string why = "";
        PlainRoads.WithModDefaults(() => span = c.Replay(world, location, out why, joinedAims: true));
        if (span == null) return;
        StairPlan plan = span.Plan;
        for (int k = 1; k < plan.Pieces.Count; k++)
            Assert.False(StairLayout.OverlapsItself(plan, k), $"piece {k} ({plan.Pieces[k].Kind} at {plan.Pieces[k].Position}) lies over an earlier walked piece");
    }

    /// <summary>A top finished over its ledge (RoadStairRuns.FinishOverLedge) through the approach's own gates, and what those
    /// refused on the way (the refusals its Fits traces).</summary>
    private static (StairLayout.TopFinish finish, List<string> refusals) Finished(StairPlan plan, Func<float, float, float> ground, Func<StairPlan, bool> fits)
    {
        StairLayout.Trace = new List<string>();
        try
        {
            StairLayout.TopFinish finish = RoadStairRuns.FinishOverLedge(plan, ground, RoadStairRuns.Width, fits);
            return (finish, StairLayout.Trace.Where(l => l.StartsWith("refusal: ", StringComparison.Ordinal)).Select(l => l.Substring("refusal: ".Length)).ToList());
        }
        finally { StairLayout.Trace = null; }
    }
}
