using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Xunit.Abstractions;

namespace ProceduralRoads.Tests;

/// <summary>
/// The landings of the captured ways onto placed locations' landings, pinned as they are laid today: each approach captured offline
/// as generation laid it (<see cref="ApproachCaptureFile"/>) laid again through the production planner under the mod's defaults,
/// and both of its ends read by the one end reader (StairEnds): its top onto the landing or its lip and its foot onto its road, or
/// the other way round where it comes down. A change to how a crest's two legs meet must leave every one of them where it is.
/// </summary>
public sealed class ApproachLandingPinTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly ITestOutputHelper _out;

    public ApproachLandingPinTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        PlacedLocations.Reset();
        RoadLocationProtection.Reset();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>Each captured approach and both its ends as laid today: the end at the location, then the end at the road (each
    /// StairEnds.Line), and its walked pieces' count. Both are read on the ground the approach was laid out on, with no road stored
    /// (a foot's line reads the bare ground past it, so some read a gap the road meets in game): pinned as they read, not judged.
    /// The way over a crest whose second leg ran over its first stays not laid (ApproachEndsGateTests).</summary>
    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "approach-capture-cabin.txt",
            "[STAIRS] end top at (743.3, 240.33, 2068.7): drop -0.05/-0.21 (sides disagree) -> short, laid 2 floors, walk-off +0.13 OK",
            "[STAIRS] end foot at (748.5, 234.33, 2076.2): drop +0.76/-0.69 (sides disagree) -> short, laid bare stair, walk-off +0.44 OK", 9 },
        new object[] { "approach-capture-cave-down.txt",
            "[STAIRS] end top at (5049.5, 255.32, -5894.6): drop +0.25/+0.23 -> reverse, laid 1 stair down, walk-off +0.76 OK",
            "[STAIRS] end foot at (5026.5, 219.32, -5899.6): drop +1.32/-0.70 (sides disagree) -> short, laid bare stair, walk-off +0.70 OK", 43 },
        new object[] { "approach-capture-cave-floor.txt",
            "[STAIRS] end top at (5050.8, 255.27, -5901.5): drop +0.65/-0.19 (sides disagree) -> floor, laid 1 floor, walk-off +0.19 OK",
            "[STAIRS] end foot at (5040.7, 217.27, -5923.5): drop +0.47/-1.16 (sides disagree) -> reverse, laid bare stair, walk-off -0.47 GAP: no edge steps off (-0.47 m)", 46 },
        new object[] { "approach-capture-over-a-crest.txt",
            "not laid: joined edge (5116.0, 190.1, -5626.0): not reached (the second staircase laid over the first); joined edge (5131.0, 190.2, -5622.0): not reached (down from the crest: no piece fits: every heading is buried, crosses itself or turns too far); level landing (5122.0, 190.6, -5623.0): not reached (the second staircase laid over the first)",
            "", 0 },
        new object[] { "approach-capture-shelf.txt",
            "[STAIRS] end top at (-2881.3, 247.74, -5451.1): drop -0.04/-0.14 -> floor, laid 1 floor, walk-off +0.04 OK",
            "[STAIRS] end foot at (-2867.6, 223.74, -5469.0): drop -2.15/+0.25 (sides disagree) -> reverse, laid bare stair, walk-off +2.15 GAP: no edge steps off (+2.15 m)", 32 },
        new object[] { "approach-capture-tower-corner.txt",
            "[STAIRS] end top at (-829.0, 184.20, -4539.9): drop -0.16/-0.16 -> floor, laid 1 floor, walk-off +0.16 OK",
            "[STAIRS] end foot at (-812.7, 160.20, -4549.4): drop -0.30/+1.21 (sides disagree) -> short, laid bare stair, walk-off +0.30 OK", 27 },
        new object[] { "approach-capture-tower-crest.txt",
            "[STAIRS] end top at (-4790.7, 186.80, 1729.8): drop +0.76/+0.76 -> reverse, laid 1 stair down, walk-off +0.24 OK",
            "[STAIRS] end foot at (-4729.3, 140.80, 1727.3): drop +0.39/-0.04 (sides disagree) -> floor, laid bare stair, walk-off +0.01 OK", 54 },
        new object[] { "approach-capture-two-floors.txt",
            "[STAIRS] end top at (-3189.7, 160.68, 2.7): drop +0.18/+2.09 (sides disagree) -> step back, laid 2 floors, walk-off +0.07 OK",
            "[STAIRS] end foot at (-3170.9, 147.68, -12.1): drop -0.61/+1.01 (sides disagree) -> short, laid bare stair, walk-off -1.01 GAP: no edge steps off (-1.01 m)", 16 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryCapturedApproachKeepsItsLandingAndItsFoot(string fixture, string location, string road, int walked)
    {
        var (atLocation, atRoad, count) = Laid(fixture);
        _out.WriteLine($"{fixture}|{atLocation}|{atRoad}|{count}");
        Assert.Equal(location, atLocation);
        Assert.Equal(road, atRoad);
        Assert.Equal(walked, count);
    }

    /// <summary>The captured approach laid again under the mod's defaults: its two ends' lines (the one further from the road's
    /// end first), and how many walked pieces it has; "not laid: why" where none is.</summary>
    private (string location, string road, int walked) Laid(string fixture)
    {
        ApproachCaptureFile c = ApproachCaptureFile.Load(fixture);
        var world = new ApproachCaptureFile.World(c);
        _scope.WithWorld(world);
        PlacedLocation location = c.Prepare(world);
        RoadStairSpan? span = null;
        string why = "";
        PlainRoads.WithModDefaults(() => span = c.Replay(world, location, out why, joinedAims: true));
        if (span == null) return ("not laid: " + why, "", 0);
        StairRun run = StairRun.FromPlan(span.FromPoint, span.ToPoint, RoadStairRuns.Width, span.Plan);
        List<StairEnds.End> ends = StairEnds.Read(run, RoadStairRuns.LayoutGround.Lattice(world).Read);
        Vector2 roadEnd = c.Request.RoadEnd;
        float Off(StairEnds.End e) => e.Edge is Vector3 p ? Vector2.Distance(new Vector2(p.x, p.z), roadEnd) : -1f;
        var ordered = ends.OrderByDescending(Off).ToList();
        int walked = span.Plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing);
        return (StairEnds.Line(ordered[0]), ordered.Count > 1 ? StairEnds.Line(ordered[1]) : "", walked);
    }
}
