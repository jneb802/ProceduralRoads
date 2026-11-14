using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// An approach refused in play, laid again on the ground the game wrote for it
/// (<see cref="ApproachGroundFile"/>). Hildir_cave at (-2304, 1536): its last-resort road
/// ended on a 100 m staircase 45 m under the cave, and the approach from its top was refused at
/// every landing, "no nearer the goal in 8000 steps of search".
/// </summary>
public class ApproachReplayTests
{
    /// <summary>Hildir_cave's levelling (road_sites_dump); the file names the six parts this placement kept.</summary>
    private static readonly LevelOp[] HildirCave =
    {
        new(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f), new(0f, 2.25f, -2f, 0f, false, 8f, 3f), new(0.09f, 9.31f, -1f, 0f, false, 8f, 3f),
        new(3.5f, 5.75f, -1f, 0f, false, 4f, 3f), new(-4.31f, 5.89f, -1f, 0f, false, 4f, 3f), new(-0.22f, 7.98f, -1f, 0f, false, 4f, 3f),
        new(-10.23f, 6.62f, -1f, 0f, false, 10f, 3f), new(-15.93f, 2.69f, 0f, 0f, false, 10f, 3f), new(7.91f, 5.3f, -1f, 0f, false, 10f, 3f),
        new(14.89f, 1.74f, 0f, 0f, false, 10f, 3f),
    };

    /// <summary>The file predates the root being written: 296.45 is the root at which the replay picks the
    /// landings the game logged at the shelf's front (-2302,1542 at 294.5 kept, -2302,1541 at 294.4 not).</summary>
    private static ApproachGroundFile HildirCaveGround() => ApproachGroundFile.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "approach-hildircave.csv")),
        HildirCave, root: 296.45f, rockRadius: 15f, keepSmallRocks: true, footprintRadius: 26.155f);

    [Fact]
    public void TheGroundFileIsTheRecordedGround()
    {
        ApproachGroundFile file = HildirCaveGround();
        Assert.Equal(6, file.Ops.Count);
        Assert.Null(file.Top);
        // cli_ground_height, from the staircase's top up to the landing.
        foreach (var (x, z, game) in new[] { (-2309f, 1566f, 252.5f), (-2308f, 1562f, 262.7f), (-2307f, 1558f, 267.7f), (-2306f, 1554f, 273.3f),
                     (-2305f, 1550f, 285.5f), (-2304f, 1546f, 293.6f), (-2302f, 1542f, 294.5f), (-2316f, 1560f, 266.2f), (-2296f, 1560f, 266.6f) })
            Assert.InRange(file.Ground(x, z), game - 0.06f, game + 0.06f);
    }

    /// <summary>The file's rocks are boxes (their real tops are not written): the three great ice blocks' boxes
    /// cover the slope west of the shelf, at (-2317, 293, 1537), open ground.</summary>
    private static ApproachGroundFile HildirCaveGroundWithoutIceBoxes()
    {
        ApproachGroundFile file = HildirCaveGround();
        file.Rocks.RemoveAll(k => k.Name == "ice_rock1");
        return file;
    }

    /// <summary>A point in Hildir_cave's own frame (x across its front, z out of it) for the cave at -2304,1536 turned to <paramref name="yaw"/>.</summary>
    private static Vector2 InCaveFrame(Vector2 world, float yaw)
    {
        float r = yaw * Mathf.Deg2Rad;
        Vector2 d = world - new Vector2(-2304f, 1536f);
        return new Vector2(d.x * Mathf.Cos(r) - d.y * Mathf.Sin(r), d.x * Mathf.Sin(r) + d.y * Mathf.Cos(r));
    }

    /// <summary>A hand-built finish of the frost cave's landing (Hildir_cave at -2304,1536 turned
    /// 315: the generated stairs' top 2x2 at -2316.3,293.3,1533.1; one more stair and two more 2x2s laid by hand ended at
    /// -2311.8,294.3,1535.4), in the cave's own frame: the same in every placement.</summary>
    private static readonly Vector2 HandFinishedLanding = InCaveFrame(new Vector2(-2311.797f, 1535.405f), 315f);

    /// <summary>
    /// The approach aims first at the edge where the slope meets the shelf, then at its level landings. On the game's ground
    /// (two triangles a cell, <see cref="BuriedPieces.BetweenVertices"/>) that edge is the east one. The west, where the
    /// landing was finished by hand, is not joined: the lip cell nearest the hand-built finish, (-2311, 1538), looks out to the
    /// south west, and 2 m out its ground is 2.70 m lower (1.35 in 1, past <see cref="PlacedLocationShape.JoinGrade"/>). That point
    /// lies past the diagonal of a cell whose low corner, (-2313, 1537) at 291.1, sits on the diagonal. Read bilinearly (before
    /// 6 Nov 2026), the four corners were averaged, the drop was 2.49, and the edge was joined by 1 cm. Every other sample the
    /// rule reads is at a vertex, so no other edge moved.
    /// </summary>
    [Fact]
    public void AnApproachAimsAtTheShelfsLevelEdges()
    {
        // If the landing is a big overhang that part of it cannot be reached, but coming up where the
        // landing connects to the mountain it likely can be. The shelf's front overhangs the face (it falls
        // 2 to 2.6 in 1 within 8 m); on its sides the slope meets it.
        ApproachGroundFile file = HildirCaveGroundWithoutIceBoxes();
        var aims = file.Aims();
        var landings = file.Landings();
        Assert.Equal("joined edge", aims[0].name);
        Assert.All(aims.GetRange(1, aims.Count - 1), a => Assert.Equal("level landing", a.name));
        // Every aim is level with a landing (StairLayout.StairFitsFrom): an approach that reaches it ends level.
        Assert.All(aims, a => Assert.Contains(landings, l => Mathf.Abs(file.Ground(a.at.x, a.at.y) - l.height) <= StairLayout.StairFitsFrom));
        // The joined edge is on the shelf's east side, beside its front, not over it: in the cave's frame (turned 337.5 here)
        // 15.8 m across and 4.3 m out.
        Vector2 east = InCaveFrame(aims[0].at, file.Yaw);
        Assert.InRange(east.x, 14f, 18f);
        Assert.InRange(east.y, 2f, 6f);
        // The west lip cell nearest the hand-finished landing falls too steeply in its first JoinStep to be joined.
        PlacedLocationShape shape = file.Shape();
        var (at, outward) = shape.ConnectedLanding(file.Ground, 1f).Lip
            .OrderBy(l => Vector2.Distance(InCaveFrame(l.at, file.Yaw), HandFinishedLanding)).First();
        Assert.Equal(new Vector2(-2311f, 1538f), at);
        Vector2 out2 = at + outward * PlacedLocationShape.JoinStep;
        float drop = file.Ground(at.x, at.y) - file.Ground(out2.x, out2.y);
        Assert.InRange(drop, 2.65f, 2.75f);
        Assert.True(drop > PlacedLocationShape.JoinGrade * PlacedLocationShape.JoinStep);
        Assert.DoesNotContain(aims, a => a.name == "joined edge" && Vector2.Distance(InCaveFrame(a.at, file.Yaw), HandFinishedLanding) <= 4f);
    }

    [Fact]
    public void AimedAtTheJoinedEdgeARoadsStaircaseAllowanceClimbsOntoTheShelf()
    {
        ApproachGroundFile file = HildirCaveGroundWithoutIceBoxes();
        int total = RoadNetworkGenerator.EntryTotalExpansions, stall = RoadNetworkGenerator.EntryStall;
        // The landing entry built with the roads: a road staircase's allowance (stall 4000), no approach allowance.
        RoadStairSpan? joined = file.Replay(total, stall, RoadStairRuns.GenerationExpansions, out string why);
        Assert.True(joined != null, why);
        Assert.True(joined!.Plan.Rise >= 42f, $"rose {joined.Plan.Rise}");
        // Its top on the shelf's level ground, level with its landings (StairLayout.StairFitsFrom): the edge it
        // aimed at is joined to them by level ground (AnApproachAimsAtTheShelfsLevelEdges).
        Vector2 top = new(joined.Plan.End.x, joined.Plan.End.z);
        Assert.Contains(file.Aims(), a => Vector2.Distance(a.at, top) <= RoadStairRuns.LedgeReach + RoadStairRuns.FinishReach);
        Assert.Contains(file.Landings(), l => Mathf.Abs(l.height - joined.Plan.End.y) <= StairLayout.StairFitsFrom);
        // 8046 steps, laid at the east edge on its first try (21757 with the ground read bilinearly: the east edge's first layout
        // had no way off its top, and the west edge was searched as well).
        Assert.True(file.Steps <= 25000, $"{file.Steps} steps");
        // Aimed at the landings in front only, the same allowance searches the face under the overhang: once stalled there,
        // aimed now at the lip nearest each (LandingRegion.AtLip), it climbs on round to the lip in several times the steps
        // (about 43600 against 14600 once the search refuses wedge fillers and floors nothing holds up, 2 Nov 2026).
        int joinedSteps = file.Steps;
        RoadStairSpan? front = file.Replay(total, stall, RoadStairRuns.GenerationExpansions, out _, joinedAims: false);
        Assert.True(front == null || file.Steps > 2 * joinedSteps, $"aimed in front only, laid in {file.Steps} steps against {joinedSteps}");
    }
}
