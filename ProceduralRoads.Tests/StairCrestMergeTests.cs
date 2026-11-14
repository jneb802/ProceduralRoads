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
/// Over a ridge, a second staircase laid over both its stretch and the next (RoadStairRuns.Lay's merge) still meets the
/// first's top at the crest: joined to it there, as any second laid up to the crest is, or not laid. Laid from the
/// road's point at the crest with no meet, it set off from the ground there: on RoadSeedD the merged one's top floors
/// stood a rise over the first's crest floor, nothing joining the two, and once the ground was cut the first's top was
/// left 0.94 m under them. Fixtures/ridge-merge-crest.txt is a crest at (9104, 72) in a test world generated offline
/// with its locations placed first: the road, its stair steps and its ground as laid there, where the second, laid over
/// both stretches, is snapped off the first's crest floor (SnapOffCrest). Laid there with no meet, its top cannot be
/// joined to the first's (no clip within a walker's step) and the road's stairs are refused. Earlier fixtures,
/// RoadSeedD's crest at (-9776, -528) and a crest at (-1544, 7640), no longer lay: the first once the ground was read
/// as the game's two triangles a cell, the second once a clipped top join steps at most a walker's step.
/// </summary>
[Collection("StairLayout")]
public class StairCrestMergeTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public StairCrestMergeTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); StairStructures.Reset(); }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        _scope.Dispose();
    }

    /// <summary>The fixture's ground: the Mistlands throughout, its whole-metre heights spread bilinearly.</summary>
    private sealed class FixtureWorld : WorldGenerator
    {
        public int X0, Z0, W, H;
        public float[] Heights = Array.Empty<float>();
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            mask = default;
            return GetHeight(wx, wy);
        }
        public override float GetHeight(float wx, float wy)
        {
            float u = Mathf.Clamp(wx - X0, 0f, W - 1.001f), v = Mathf.Clamp(wy - Z0, 0f, H - 1.001f);
            int i = (int)u, j = (int)v;
            float fu = u - i, fv = v - j;
            float At(int a, int b) => Heights[b * W + a];
            return Mathf.Lerp(Mathf.Lerp(At(i, j), At(i + 1, j), fu), Mathf.Lerp(At(i, j + 1), At(i + 1, j + 1), fu), fv);
        }
        public override int GetSeed() => 77;
    }

    private static (FixtureWorld world, List<Vector2> path, List<(Vector2 from, Vector2 to)> steps) Load()
    {
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        var world = new FixtureWorld();
        var path = new List<Vector2>();
        var steps = new List<(Vector2, Vector2)>();
        var rows = new List<float>();
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ridge-merge-crest.txt")))
        {
            if (line.StartsWith("#", StringComparison.Ordinal) || line.Trim() == "") continue;
            string[] f = line.Split(',');
            switch (f[0])
            {
                case "path": path.Add(new Vector2(F(f[1]), F(f[2]))); break;
                case "step": steps.Add((new Vector2(F(f[1]), F(f[2])), new Vector2(F(f[3]), F(f[4])))); break;
                case "grid": world.X0 = (int)F(f[1]); world.Z0 = (int)F(f[2]); world.W = (int)F(f[3]); world.H = (int)F(f[4]); break;
                case "row": rows.AddRange(f.Skip(1).Select(F)); break;
            }
        }
        world.Heights = rows.ToArray();
        Assert.Equal(world.W * world.H, world.Heights.Length);
        return (world, path, steps);
    }

    private static float Surface(StairPiece p) => StairLayout.Footprint(p).height + (p.Kind == StairPieceKind.Stair ? StairLayout.PieceRise : 0f);

    /// <summary>The highest walking height of a plan's walked pieces: its top's.</summary>
    private static float Top(StairPlan plan) =>
        plan.Pieces.Where(p => p.Kind is StairPieceKind.Stair or StairPieceKind.Landing).Max(Surface);

    [Fact]
    public void AStaircaseLaidOverBothStretchesFromARidgesCrestIsJoinedToTheFirstsTop()
    {
        var (world, path, steps) = Load();
        _scope.WithWorld(world);
        List<RoadStairSpan>? spans = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world);
        Assert.NotNull(spans);
        string said = string.Join(" | ", spans!.Select(s => FormattableString.Invariant(
            $"{s.FromIndex}..{s.ToIndex} rev {s.Plan.Reversed} built down {s.Plan.BuiltDown} top {Top(s.Plan):F2}")));
        // Every pair met over the crest (the first climbing to its top, the next coming down to it, no road between): the two
        // tops are one walk, the first's top end stepping onto the second's walked pieces, never left beside them at another height.
        int pairs = 0;
        for (int i = 0; i + 1 < spans.Count; i++)
        {
            RoadStairSpan up = spans[i], down = spans[i + 1];
            if (up.Plan.Reversed || !down.Plan.Reversed || up.ToIndex != down.FromIndex) continue;
            pairs++;
            StairRun first = StairRun.FromPlan(up.FromPoint, up.ToPoint, RoadStairRuns.Width, up.Plan, up.Kit);
            StairRun second = StairRun.FromPlan(down.FromPoint, down.ToPoint, RoadStairRuns.Width, down.Plan, down.Kit);
            Func<float, float, float> ground = (x, z) => RoadStairRuns.GroundAt(x, z, world);
            StairEnds.End top = StairEnds.Read(first, ground, second.Pieces).Single(e => e.Top);
            Assert.True(top.Joined, "the first's top is not joined to the second: " + StairEnds.Line(top) + "; " + said);
        }
        Assert.True(pairs > 0, "no pair met over the crest: " + said);
    }
}
