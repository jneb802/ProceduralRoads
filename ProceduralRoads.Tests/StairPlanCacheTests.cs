using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road's staircases are laid on copies of the cached layouts: what a road edits (a valley's span cutting a flight back,
/// a foot dropped into a shared floor, a crest's floor, a top carried out) changes the cache only when the road is accepted
/// and its staircases laid. Edited in place, a refused road left its edits for its retry
/// and every later road over the same cells, which then, for one, no longer met in the valley.
/// </summary>
public class StairPlanCacheTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    private sealed class SharedFloorValleyWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => CantileverTests.SharedFloorValleyGround(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 76;
    }

    private readonly SharedFloorValleyWorld _world = new();

    public StairPlanCacheTests()
    {
        _scope.WithWorld(_world);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>The road through a valley (ValleySpanRoadTests): down to the bottom at index 4, up to index 6.</summary>
    private static readonly List<Vector2> Road = new()
    {
        new(4360f, 6448f), new(4368f, 6448f), new(4376f, 6448f), new(4386f, 6448f),
        new(4392f, 6440f),
        new(4400f, 6440f), new(4408f, 6448f), new(4416f, 6448f), new(4424f, 6448f),
    };

    private static string Describe(List<RoadStairSpan>? spans) => spans == null ? "not laid" : string.Join(" | ", spans.Select(s =>
        $"{s.FromIndex}..{s.ToIndex} {s.FromPoint}->{s.ToPoint}: " + string.Join(" ", s.Plan.Pieces.Select(p => FormattableString.Invariant($"{p.Kind.ToString()[0]}({p.Position.x:F2},{p.Position.y:F2},{p.Position.z:F2})")))));

    [Fact]
    public void ARoadLaidAgainOverTheSameCellsGetsTheSameStaircasesWhenTheFirstWasNotAccepted()
    {
        var steps = new List<(Vector2 from, Vector2 to)> { (Road[3], Road[4]), (Road[4], Road[5]), (Road[5], Road[6]) };
        string first = Describe(RoadStairRuns.Lay(Road, steps, new List<RoadCrossing>(), _world));
        // Not accepted: nothing laid, nothing committed. The same road again, as its retry or a later road would lay it.
        string again = Describe(RoadStairRuns.Lay(Road, steps, new List<RoadCrossing>(), _world));
        Assert.True(first.Contains("|"), "the valley was not laid as two staircases: " + first);
        Assert.Equal(first, again);
    }

    /// <summary>A copy carries every field of the plan it copies, and lists of its own.</summary>
    [Fact]
    public void ACopyCarriesEveryFieldAndListsOfItsOwn()
    {
        var plan = new StairPlan();
        int n = 0;
        foreach (var f in typeof(StairPlan).GetFields())
        {
            n++;
            if (f.IsInitOnly) continue;
            object value = f.FieldType == typeof(string) ? "kit" + n : f.FieldType == typeof(bool) ? true : f.FieldType == typeof(int) ? 100 + n
                : f.FieldType == typeof(float) ? 0.5f + n : f.FieldType == typeof(Vector3) || f.FieldType == typeof(Vector3?) ? new Vector3(n, n, n)
                : f.FieldType == typeof(Vector2) ? new Vector2(n, n) : f.FieldType == typeof(StairPieceKind) ? StairPieceKind.Landing
                : throw new InvalidOperationException($"{f.Name}: {f.FieldType} not covered by this test");
            f.SetValue(plan, value);
        }
        plan.Pieces.Add(new StairPiece());
        plan.Clearances.Add(new StairClearance(new Vector3(1f, 2f, 3f), new Vector3(1f, 1f, 1f), 0f, 2f));
        plan.Cuts.Add(new Vector3(1f, 2f, 3f));

        StairPlan copy = plan.Copy();

        foreach (var f in typeof(StairPlan).GetFields())
            if (!f.IsInitOnly) Assert.Equal(f.GetValue(plan), f.GetValue(copy));
        Assert.NotSame(plan.Pieces, copy.Pieces);
        Assert.Equal(plan.Pieces, copy.Pieces);
        Assert.NotSame(plan.Clearances, copy.Clearances);
        Assert.Equal(plan.Clearances, copy.Clearances);
        Assert.NotSame(plan.Cuts, copy.Cuts);
        Assert.Equal(plan.Cuts, copy.Cuts);
    }
}
