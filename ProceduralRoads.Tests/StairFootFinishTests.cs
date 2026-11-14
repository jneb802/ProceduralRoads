using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase's own foot with no road past it (RoadStairRuns.Lay: finished onto nothing) is finished by the end rule onto the
/// ground past it, in the order a top is (StairLayout.FinishFoot: landing first, then a stair down, bare last), and the end
/// audit reads it finished (assistant-written, 11 Nov 2026). Before, such a foot was left bare where its rule chose a stair
/// down, and the audit counted it unfinished.
/// </summary>
[Collection("StairLayout")]
public class StairFootFinishTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public StairFootFinishTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); StairStructures.Reset(); }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        _scope.Dispose();
    }

    /// <summary>The ground a fixture was laid out on, its whole-metre vertices and the game's mesh between them; off them, high
    /// dry ground.</summary>
    private sealed class CapturedWorld : WorldGenerator
    {
        public readonly Dictionary<(int, int), float> Vertices = new();
        public float Vertex(int i, int k) => Vertices.TryGetValue((i, k), out float h) ? h : 60f;
        public override float GetHeight(float wx, float wy) => BuriedPieces.MeshHeight(wx, wy, Vertex);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 91;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Fixtures/stair-foot-no-road-bank.txt: the plan as laid before its foot was finished, and its world.</summary>
    private static (StairPlan plan, CapturedWorld world) Load()
    {
        var plan = new StairPlan { Reached = true };
        var world = new CapturedWorld();
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stair-foot-no-road-bank.txt")))
        {
            if (line.StartsWith("#", StringComparison.Ordinal) || line.Trim() == "") continue;
            string[] f = line.Split(',');
            switch (f[0])
            {
                case "plan":
                    plan.Foot = new Vector3(F(f[1]), F(f[2]), F(f[3]));
                    plan.StartHeading = F(f[4]);
                    plan.SunkPieces = int.Parse(f[5], CultureInfo.InvariantCulture);
                    plan.Kit = f[7];
                    plan.Reversed = f[8] == "True";
                    plan.End = new Vector3(F(f[9]), F(f[10]), F(f[11]));
                    plan.EndHeading = F(f[12]);
                    break;
                case "plpiece":
                    plan.Pieces.Add(new StairPiece
                    {
                        Kind = (StairPieceKind)Enum.Parse(typeof(StairPieceKind), f[1]), Prefab = f[2],
                        Position = new Vector3(F(f[3]), F(f[4]), F(f[5])), YawDegrees = F(f[6]), Sunk = f[7] == "True",
                    });
                    break;
                case "cut": plan.Cuts.Add(new Vector3(F(f[1]), F(f[2]), F(f[3]))); break;
                case "ground": world.Vertices[((int)F(f[1]), (int)F(f[2]))] = F(f[3]); break;
            }
        }
        return (plan, world);
    }

    /// <summary>The ground as the plan's own cuts leave it, as the end audit's mesh reads it.</summary>
    private static Func<float, float, float> Finished(StairPlan plan, CapturedWorld world) => (x, z) =>
        BuriedPieces.MeshHeight(x, z, (i, k) => RoadTerrainModifier.GroundAfterCuts(world.Vertex(i, k), plan.Cuts, i, k));

    /// <summary>
    /// A road staircase set off at its road's first point on a side slope (offline, Fixtures/stair-foot-no-road-bank.txt): its
    /// foot's left corner 1.5 m under a bank, its right 0.65 m over the ground falling away: the rule's stair down. Laid bare,
    /// the audit reads it a gap. Finished by the end rule, landing first: a floor at the foot's height out from it, cut
    /// into the bank, its far corners within a step: the audit reads it finished, with a way off. A stair on down in its place
    /// (into the bank, its low edge met at a side) is laid only where the floor and the floor then a stair down are not.
    /// </summary>
    [Fact]
    public void AFootWithNoRoadPastItIsFinishedByTheEndRuleLandingFirst()
    {
        var (plan, world) = Load();
        _scope.WithWorld(world);
        Func<float, float, float> ground = (x, z) => RoadStairRuns.GroundAt(x, z, world);
        StairEnds.End bare = RoadStairRuns.FootEnd(plan, Finished(plan, world))!;
        // Bare, it is not finished: no way off it by the one rule (a floor laid off it would not land; read at either corner,
        // it was walked off at the one but unfinished).
        Assert.True(bare.Rule == StairEnds.Case.Reverse && bare.Verdict == StairEnds.Verdict.Gap, "laid bare: " + StairEnds.Line(bare));

        StairPlan alone = plan.Copy(deepPieces: true);
        var path = new List<Vector2> { new(plan.Foot!.Value.x, plan.Foot.Value.z), new(plan.End.x, plan.End.z) };
        Vector3? foot = RoadStairRuns.FinishFootOntoNothing(plan, path, 0, 1, world, new List<RoadStairSpan>(), null);
        Assert.True(foot != null, "not finished: " + StairEnds.Line(bare));
        StairEnds.End end = RoadStairRuns.FootEnd(plan, Finished(plan, world))!;
        Assert.True(end.Verdict == StairEnds.Verdict.Ok && StairEnds.Unfinished(end) == null, StairEnds.Line(end));
        Assert.True(end.FloorsPast == 1 && end.StairsPast == 0, "not its landing: " + StairEnds.Line(end));
        Assert.True(RoadStairRuns.FootFinished(plan, ground));
        // Once finished, never again (the plan is cached as laid, finished).
        Assert.Null(RoadStairRuns.FinishFootOntoNothing(plan, path, 0, 1, world, new List<RoadStairSpan>(), null));

        // No floor kept: the stair down in place, its new low edge the foot.
        StairLayout.FootFinish down = StairLayout.FinishFoot(alone, ground, RoadStairRuns.Width, _ => true,
            p => p.Pieces.First(q => q.Kind != StairPieceKind.Support).Kind == StairPieceKind.Stair && RoadStairRuns.FootFinished(p, ground));
        Assert.Equal(StairLayout.FootFinish.DownInPlace, down);
        StairEnds.End stair = RoadStairRuns.FootEnd(alone, Finished(alone, world))!;
        Assert.True(stair.Verdict == StairEnds.Verdict.Ok && StairEnds.Unfinished(stair) == null, StairEnds.Line(stair));
        Assert.True(stair.Edge!.Value.y < plan.Foot!.Value.y - 0.5f, StairEnds.Line(stair));
    }
    /// <summary>The run a plan's road would store (as the end reader reads a foot, RoadStairRuns.FootEnd).</summary>
    private static StairRun RunOf(StairPlan plan)
    {
        StairPiece foot = plan.Pieces.Find(p => p.Kind != StairPieceKind.Support)!;
        StairLayout.TerminalExit? exit = StairLayout.Terminal(plan);
        Vector2 top = exit != null ? new Vector2(exit.Edge.x, exit.Edge.z) : new Vector2(plan.End.x, plan.End.z);
        return StairRun.FromPlan(new Vector2(foot.Position.x, foot.Position.z), top, RoadStairRuns.Width, plan);
    }

    /// <summary>
    /// A foot no finish fits refuses its road (the owner, 12 Nov 2026: refuse and reroute, as a top it cannot finish; not left
    /// for the end audit). The captured foot with no road past it, laid bare, has no way off by the one floor-lands rule (a
    /// floor laid off it would not land; read at either corner, it was a way off but unfinished): the one finished-connection
    /// verdict every path asks of a road (RoadStairRuns.ConnectionRefusal, from generation's commit and from a planned road's
    /// check, BuiltRoadRefusal, for marked manual roads and approach trials) refuses it, on the finished ground the end audit
    /// reads; finished by the end rule, it passes.
    /// </summary>
    [Fact]
    public void AFootNoFinishFitsRefusesItsRoad()
    {
        var (plan, world) = Load();
        _scope.WithWorld(world);
        var span = new RoadStairSpan { Plan = plan, NoRoadAtFoot = true };
        var roads = new List<RoadSpatialGrid.PlannedPath>();
        Assert.Equal(RoadStairRuns.NoWayOffItsEnd, RoadStairRuns.ConnectionRefusal(new[] { span }, roads, out Vector2 at,
            new List<StairRun> { RunOf(plan) }, _ => Finished(plan, world)));
        Assert.True(Vector2.Distance(at, new Vector2(plan.Foot!.Value.x, plan.Foot.Value.z)) < 3f, $"refused at {at}");

        var path = new List<Vector2> { new(plan.Foot!.Value.x, plan.Foot.Value.z), new(plan.End.x, plan.End.z) };
        Assert.NotNull(RoadStairRuns.FinishFootOntoNothing(plan, path, 0, 1, world, new List<RoadStairSpan>(), null));
        Assert.Null(RoadStairRuns.ConnectionRefusal(new[] { span }, roads, out _, new List<StairRun> { RunOf(plan) }, _ => Finished(plan, world)));
    }
}
