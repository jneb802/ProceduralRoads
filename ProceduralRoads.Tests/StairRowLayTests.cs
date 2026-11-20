using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// One path to lay a road staircase: the layouts a road's staircases lay straight on from a row already laid (a valley's
/// second off the shared floor and its span, the second over a ridge searched down from the first's top) go through the
/// generation's layouts (RoadStairRuns.FromRow, RoadStairRuns.RowLay) as every other road staircase does (RoadStairRuns.Plan),
/// laid once and handed out as copies. The key is the call: two calls that differ in any one input never share a layout, and
/// a road laid from the cache gets exactly the staircases it got laid afresh.
/// </summary>
public class StairRowLayTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    private sealed class SharedFloorValleyWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => CantileverTests.SharedFloorValleyGround(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 76;
    }

    /// <summary>A sharp ridge along z: faces of 1.2 up to a crest line at x = 0, 24 m over the flats either side.</summary>
    private sealed class SharpRidgeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(24f - Mathf.Abs(wx) * 1.2f, 0f, 24f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    public StairRowLayTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>The road through a valley (StairPlanCacheTests): down to the bottom at index 4, up to index 6.</summary>
    private static readonly List<Vector2> ValleyRoad = new()
    {
        new(4360f, 6448f), new(4368f, 6448f), new(4376f, 6448f), new(4386f, 6448f),
        new(4392f, 6440f),
        new(4400f, 6440f), new(4408f, 6448f), new(4416f, 6448f), new(4424f, 6448f),
    };

    // --- The key -------------------------------------------------------------------------------------------------

    private static readonly ConstructorInfo Ctor = typeof(RoadStairRuns.RowLay).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();

    /// <summary>Every argument of a RowLay, each set to a value of its own.</summary>
    private static object?[] Arguments(WorldGenerator world) => Ctor.GetParameters().Select(p => p.Name switch
    {
        "down" => (object?)false,
        "row" => new StairContinuation(new Vector2(10.5f, -3.25f), 47.5f, 31.75f, StairPieceKind.Landing),
        "to" => new Vector2(30.5f, 12.25f),
        "kit" => StairKit.Stone,
        "world" => world,
        "width" => 2f,
        "legs" => 25000,
        "total" => 80000,
        "stall" => 2000,
        "leaving" => (float?)91.5f,
        "corridor" => new List<Vector2> { new(10.5f, -3.25f), new(20f, 4f), new(30.5f, 12.25f) },
        "topLandings" => 1,
        "sunkFoot" => true,
        "maxBuried" => 0.25f,
        "relayPads" => false,
        "ground" => 7,
        "unpricedLedge" => false,
        "endGuardReach" => 8f,
        _ => throw new InvalidOperationException($"{p.Name}: not covered by this test"),
    }).ToArray();

    /// <summary>A float's bits, and back (net48 has no SingleToInt32Bits).</summary>
    private static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
    private static float Float(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

    private static RoadStairRuns.RowLay Make(object?[] args) => (RoadStairRuns.RowLay)Ctor.Invoke(args);

    /// <summary>Each value an argument could be changed to, one at a time: every part of the row and the corridor too.</summary>
    private static IEnumerable<(string what, object? value)> Others(string name, object? value)
    {
        switch (value)
        {
            case bool b: yield return (name, !b); break;
            case int i: yield return (name, i + 1); break;
            case float f: yield return (name, f + 0.25f); yield return (name + " by an ulp", Float(Bits(f) + 1)); break;
            case Vector2 v: yield return (name + ".x", new Vector2(v.x + 0.25f, v.y)); yield return (name + ".y", new Vector2(v.x, v.y + 0.25f)); break;
            case StairContinuation r:
                yield return (name + ".Edge.x", new StairContinuation(new Vector2(r.Edge.x + 0.25f, r.Edge.y), r.Heading, r.Height, r.Kind));
                yield return (name + ".Edge.y", new StairContinuation(new Vector2(r.Edge.x, r.Edge.y + 0.25f), r.Heading, r.Height, r.Kind));
                yield return (name + ".Heading", new StairContinuation(r.Edge, r.Heading + 0.25f, r.Height, r.Kind));
                yield return (name + ".Height", new StairContinuation(r.Edge, r.Heading, r.Height + 0.25f, r.Kind));
                yield return (name + ".Kind", new StairContinuation(r.Edge, r.Heading, r.Height, StairPieceKind.Stair));
                break;
            case StairKit kit:
                foreach (StairKit other in StairKit.All)
                    if (other != kit) yield return (name, other);
                break;
            case WorldGenerator: yield return (name, new SharpRidgeWorld()); break;
            case List<Vector2> c:
                yield return (name + " none", null);
                yield return (name + " empty", new List<Vector2>());
                yield return (name + " a point fewer", c.Take(c.Count - 1).ToList());
                yield return (name + " a point more", c.Concat(new[] { new Vector2(40f, 20f) }).ToList());
                for (int i = 0; i < c.Count; i++)
                {
                    var moved = new List<Vector2>(c);
                    moved[i] = new Vector2(c[i].x, c[i].y + 0.25f);
                    yield return ($"{name}[{i}]", moved);
                }
                break;
            case null when name == "leaving": yield return (name, 1f); break;
            default: throw new InvalidOperationException($"{name}: {value?.GetType().Name ?? "null"} not covered by this test");
        }
        if (name == "leaving") yield return (name + " none", null);
    }

    /// <summary>
    /// The key is the call: every argument the layout takes (each a field the layout is laid from, RowLay.Lay, and nothing
    /// else), and what else it reads as it stands (the ground's generation, the ledge price setting, the end guard's reach).
    /// Two calls differing in any one of them, by as little as a float's last bit, are two keys: they never share a layout.
    /// </summary>
    [Fact]
    public void TwoCallsDifferingInAnyOneInputNeverShareALayout()
    {
        var world = new SharedFloorValleyWorld();
        object?[] args = Arguments(world);
        var key = Make(args);
        ParameterInfo[] ps = Ctor.GetParameters();
        // Every field of the key is one of the call's arguments, and every argument a field: none read from elsewhere.
        var fields = typeof(RoadStairRuns.RowLay).GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name.ToLowerInvariant()).OrderBy(n => n);
        Assert.Equal(ps.Select(p => p.Name!.ToLowerInvariant()).OrderBy(n => n), fields);
        // The same call again (its corridor a list of its own) is the same key.
        var again = Make(Arguments(world));
        Assert.True(key.Equals(again) && key.GetHashCode() == again.GetHashCode(), "the same call is not the same key");
        int variants = 0;
        for (int i = 0; i < ps.Length; i++)
            foreach (var (what, value) in Others(ps[i].Name!, args[i]))
            {
                object?[] changed = (object?[])args.Clone();
                changed[i] = value;
                Assert.False(key.Equals(Make(changed)), $"a call differing only in {what} shares the key");
                variants++;
            }
        Assert.True(variants > ps.Length, $"{variants} variants");
    }

    /// <summary>What a layout reads as it stands is taken into its key where it is laid: the ground forgotten
    /// (RoadStairRuns.ForgetGround), the ledge price off (StairLayout.UnpricedLedge), the end guard's reach changed.</summary>
    [Fact]
    public void WhatALayoutReadsAsItStandsIsInItsKey()
    {
        var world = new SharedFloorValleyWorld();
        var row = new StairContinuation(new Vector2(4396f, 6440f), 67.5f, 32.5f, StairPieceKind.Landing);
        var corridor = new List<Vector2>(ValleyRoad);
        RoadStairRuns.RowLay Up() => RoadStairRuns.RowLay.Up(row, ValleyRoad[6], StairKit.Stone, world, 90f, corridor);
        RoadStairRuns.RowLay Down() => RoadStairRuns.RowLay.DownFrom(row, ValleyRoad[6], StairKit.Stone, world, corridor);
        RoadStairRuns.RowLay up = Up(), down = Down();
        Assert.Equal(up, Up());
        Assert.NotEqual(up, down);
        bool unpriced = StairLayout.UnpricedLedge;
        try
        {
            StairLayout.UnpricedLedge = !unpriced;
            Assert.NotEqual(up, Up());
            Assert.NotEqual(down, Down());
        }
        finally { StairLayout.UnpricedLedge = unpriced; }
        float reach = StairLayout.EndGuardReach;
        try
        {
            StairLayout.EndGuardReach = reach + 1f;
            Assert.NotEqual(up, Up());
        }
        finally { StairLayout.EndGuardReach = reach; }
        RoadStairRuns.ForgetGround();
        Assert.NotEqual(up, Up());
        Assert.NotEqual(down, Down());
    }

    // --- The real callers ----------------------------------------------------------------------------------------

    /// <summary>Every field of two values, deeply (lists item by item, every field public or not of the plans, pieces and
    /// spans), floats bit for bit; a kit by reference.</summary>
    internal static List<string> Differences(object? a, object? b, string at = "")
    {
        var found = new List<string>();
        void Walk(object? x, object? y, string path, int depth)
        {
            if (found.Count > 20) return;
            if (x == null || y == null) { if (x != null || y != null) found.Add($"{path}: {x ?? "null"} vs {y ?? "null"}"); return; }
            Type t = x.GetType();
            if (t != y.GetType()) { found.Add($"{path}: {t.Name} vs {y.GetType().Name}"); return; }
            if (x is float fx) { if (Bits(fx) != Bits((float)y)) found.Add($"{path}: {fx:R} vs {(float)y:R}"); return; }
            if (t.IsPrimitive || t.IsEnum || x is string) { if (!x.Equals(y)) found.Add($"{path}: {x} vs {y}"); return; }
            if (x is StairKit || x is WorldGenerator) { if (!ReferenceEquals(x, y)) found.Add($"{path}: another {t.Name}"); return; }
            if (x is IList lx)
            {
                var ly = (IList)y;
                if (lx.Count != ly.Count) { found.Add($"{path}: {lx.Count} vs {ly.Count} items"); return; }
                for (int i = 0; i < lx.Count; i++) Walk(lx[i], ly[i], $"{path}[{i}]", depth + 1);
                return;
            }
            if (depth > 8) { found.Add($"{path}: too deep"); return; }
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                Walk(f.GetValue(x), f.GetValue(y), path + "." + f.Name, depth + 1);
        }
        Walk(a, b, at, 0);
        return found;
    }

    private static string Said(List<string> differences) => string.Join("; ", differences);

    /// <summary>
    /// Through a valley the second staircase climbs out off a floor shared at the bottom, laid on from that floor's row
    /// (LayOnce's valley rows, and the span across where it fits): laid through the generation's layouts, kept there, and the
    /// road laid again (as its retry, a replan or an approach trial lays it) is handed copies of them, its staircases field
    /// for field those it got laid afresh, as it gets after the cache is cleared. Before, these were laid afresh every time,
    /// none of them kept.
    /// </summary>
    [Fact]
    public void AValleysStaircasesLaidFromTheCacheAreThoseLaidAfresh()
    {
        var world = new SharedFloorValleyWorld();
        _scope.WithWorld(world);
        var steps = new List<(Vector2 from, Vector2 to)> { (ValleyRoad[3], ValleyRoad[4]), (ValleyRoad[4], ValleyRoad[5]), (ValleyRoad[5], ValleyRoad[6]) };
        RoadStairRuns.ResetCache();
        List<RoadStairSpan>? fresh = RoadStairRuns.Lay(ValleyRoad, steps, new List<RoadCrossing>(), world);
        Assert.True(fresh != null && fresh.Count == 2, "the valley was not laid as two staircases");
        Assert.True(RoadStairRuns.ValleysMet >= 1, "the second did not climb out off a shared floor");
        int kept = RoadStairRuns.RowLaysKept;
        Assert.True(kept > 0, "no layout laid on from a row was kept");
        List<RoadStairSpan>? cached = RoadStairRuns.Lay(ValleyRoad, steps, new List<RoadCrossing>(), world);
        Assert.Equal(kept, RoadStairRuns.RowLaysKept);
        Assert.True(Differences(fresh, cached).Count == 0, Said(Differences(fresh, cached)));
        // And once more, after the cache is cleared: laid afresh again, the same.
        RoadStairRuns.ResetCache();
        List<RoadStairSpan>? again = RoadStairRuns.Lay(ValleyRoad, steps, new List<RoadCrossing>(), world);
        Assert.True(Differences(fresh, again).Count == 0, Said(Differences(fresh, again)));
    }

    /// <summary>
    /// Over a sharp ridge the second staircase is searched down from the first's top (LayOnce's crest rows, and off the
    /// crest's floor, SnapOffCrest): laid through the generation's layouts, kept there, and the road laid again gets its
    /// staircases from the cache field for field as laid afresh.
    /// </summary>
    [Fact]
    public void ARidgesStaircasesLaidFromTheCacheAreThoseLaidAfresh()
    {
        var world = new SharpRidgeWorld();
        _scope.WithWorld(world);
        var finder = new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
        List<Vector2>? path = finder.FindPath(new Vector2(-80f, 0f), new Vector2(80f, 0f));
        Assert.True(path != null && finder.LastStairSteps.Count > 0, "no stairs on the way over the ridge");
        var steps = new List<(Vector2 from, Vector2 to)>(finder.LastStairSteps);
        RoadStairRuns.ResetCache();
        List<RoadStairSpan>? fresh = RoadStairRuns.Lay(path!, steps, new List<RoadCrossing>(), world);
        Assert.True(fresh != null && fresh.Count == 2, $"not laid as two staircases: {fresh?.Count.ToString() ?? "none"}");
        Assert.True(fresh!.Exists(s => s.Plan.BuiltDown), "the second was not searched down from the first's top");
        int kept = RoadStairRuns.RowLaysKept;
        Assert.True(kept > 0, "no layout laid on from a row was kept");
        List<RoadStairSpan>? cached = RoadStairRuns.Lay(path!, steps, new List<RoadCrossing>(), world);
        Assert.Equal(kept, RoadStairRuns.RowLaysKept);
        Assert.True(Differences(fresh, cached).Count == 0, Said(Differences(fresh, cached)));
        RoadStairRuns.ResetCache();
        List<RoadStairSpan>? again = RoadStairRuns.Lay(path!, steps, new List<RoadCrossing>(), world);
        Assert.True(Differences(fresh, again).Count == 0, Said(Differences(fresh, again)));
    }
}
