using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road staircase's layout is kept under the call that lays it (RoadStairRuns.Plan, keyed by RoadStairRuns.PlanKey):
/// every input bit for bit, as RowLay and LocationLay key theirs. Two calls that differ in any one input never share a layout,
/// and a road laid from the cache gets exactly the staircases it got laid afresh. Before, a hash of the call rounded its ends
/// to 10 cm, its corridor to a metre and its headings to the search's step and left out its burial cap and landings (with a
/// corridor), its foot height (without one) and its allowance: a call a few centimetres off another was handed the other's
/// layout.
/// </summary>
public class StairPlanKeyTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>A slope climbing east at 0.6 from 40 m at x 0 to 58 m at x 30, level either side.</summary>
    private sealed class SlopeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 40f + 0.6f * Mathf.Clamp(wx, 0f, 30f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 81;
    }

    private readonly SlopeWorld _world = new();

    private sealed class SharedFloorValleyWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => CantileverTests.SharedFloorValleyGround(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 76;
    }

    /// <summary>The road through a valley (StairRowLayTests.ValleyRoad): down to the bottom at index 4, up to index 6.</summary>
    private static readonly List<Vector2> ValleyRoad = new()
    {
        new(4360f, 6448f), new(4368f, 6448f), new(4376f, 6448f), new(4386f, 6448f),
        new(4392f, 6440f),
        new(4400f, 6440f), new(4408f, 6448f), new(4416f, 6448f), new(4424f, 6448f),
    };

    public StairPlanKeyTests()
    {
        _scope.WithWorld(_world);
        RoadNetworkGenerator.Reset();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
    private static float Float(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

    // --- The key -------------------------------------------------------------------------------------------------

    private static readonly ConstructorInfo Ctor = typeof(RoadStairRuns.PlanKey).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();

    /// <summary>Every argument of a PlanKey, each set to a value of its own.</summary>
    private object?[] Arguments() => Ctor.GetParameters().Select(p => p.Name switch
    {
        "a" => (object?)new Vector2(-5.5f, 0.25f),
        "b" => new Vector2(35.5f, 1.25f),
        "kit" => StairKit.Stone,
        "ground" => RoadStairRuns.LayoutGround.Lattice(_world),
        "arriving" => (float?)91.5f,
        "leaving" => (float?)88.5f,
        "corridor" => new List<Vector2> { new(-5.5f, 0.25f), new(14f, 3f), new(35.5f, 1.25f) },
        "maxBuried" => 0.25f,
        "topLandings" => 1,
        "footHeight" => (float?)40.5f,
        "after" => (StairContinuation?)new StairContinuation(new Vector2(-5.5f, 0.25f), 90f, 40.75f, StairPieceKind.Landing),
        "legs" => 25000,
        "total" => 80000,
        "stall" => 2000,
        "relayPads" => true,
        "facing" => (float?)92.5f,
        "topFacing" => (float?)87.5f,
        "apron" => ((Vector2 centre, float radius)?)(new Vector2(40f, 1f), 12.5f),
        "ring" => (RoadEndRing?)new RoadEndRing(new Vector2(-5.5f, 0.25f), 90f, 40.75f),
        "intoLocation" => (Vector2?)new Vector2(40f, 1f),
        "maxSpan" => 250f,
        "refuseLevel" => true,
        "generation" => 7,
        "unpricedLedge" => false,
        "endGuardReach" => 8f,
        _ => throw new InvalidOperationException($"{p.Name}: not covered by this test"),
    }).ToArray();

    private static RoadStairRuns.PlanKey Make(object?[] args) => (RoadStairRuns.PlanKey)Ctor.Invoke(args);

    private static readonly string[] Optional = { "arriving", "leaving", "footHeight", "facing", "topFacing", "intoLocation" };

    /// <summary>Each value an argument could be changed to, one at a time.</summary>
    private IEnumerable<(string what, object? value)> Others(string name, object? value)
    {
        switch (value)
        {
            case bool b: yield return (name, !b); break;
            case int i: yield return (name, i + 1); break;
            case float f: yield return (name, f + 0.25f); yield return (name + " by an ulp", Float(Bits(f) + 1)); break;
            case Vector2 v:
                yield return (name + ".x", new Vector2(v.x + 0.03f, v.y)); yield return (name + ".y", new Vector2(v.x, v.y + 0.03f));
                yield return (name + ".x by an ulp", new Vector2(Float(Bits(v.x) + 1), v.y));
                break;
            case StairContinuation r:
                yield return (name + ".Edge.x", new StairContinuation(new Vector2(r.Edge.x + 0.03f, r.Edge.y), r.Heading, r.Height, r.Kind));
                yield return (name + ".Heading", new StairContinuation(r.Edge, r.Heading + 0.25f, r.Height, r.Kind));
                yield return (name + ".Height", new StairContinuation(r.Edge, r.Heading, r.Height + 0.004f, r.Kind));
                yield return (name + ".Kind", new StairContinuation(r.Edge, r.Heading, r.Height, StairPieceKind.Stair));
                yield return (name + " none", null);
                break;
            case RoadEndRing g:
                yield return (name + ".Centre.x", new RoadEndRing(new Vector2(g.Centre.x + 0.03f, g.Centre.y), g.Travel, g.Height, g.Through));
                yield return (name + ".Travel", new RoadEndRing(g.Centre, g.Travel + 0.25f, g.Height, g.Through));
                yield return (name + ".Height", new RoadEndRing(g.Centre, g.Travel, g.Height + 0.004f, g.Through));
                yield return (name + ".Through", new RoadEndRing(g.Centre, g.Travel, g.Height, !g.Through));
                yield return (name + ".Radius", new RoadEndRing(g.Centre, g.Travel, g.Height, g.Through, RoadNetworkGenerator.RoadWidth + 1f));
                yield return (name + " none", null);
                break;
            case ValueTuple<Vector2, float> ap:
                yield return (name + ".centre", ((Vector2 centre, float radius)?)(new Vector2(ap.Item1.x + 0.03f, ap.Item1.y), ap.Item2));
                yield return (name + ".radius", ((Vector2 centre, float radius)?)(ap.Item1, ap.Item2 + 0.03f));
                yield return (name + " none", null);
                break;
            case StairKit kit:
                foreach (StairKit other in StairKit.All)
                    if (other != kit) yield return (name, other);
                break;
            case RoadStairRuns.LayoutGround:
                yield return (name + ": another world's lattice", RoadStairRuns.LayoutGround.Lattice(new SlopeWorld()));
                yield return (name + ": levelled", RoadStairRuns.LayoutGround.Levelled(_world, new Vector2(-10f, 0f), new Vector2(-5.5f, 0.25f), 40f));
                break;
            case List<Vector2> c:
                yield return (name + " none", null);
                yield return (name + " empty", new List<Vector2>());
                yield return (name + " a point fewer", c.Take(c.Count - 1).ToList());
                yield return (name + " a point more", c.Concat(new[] { new Vector2(40f, 1f) }).ToList());
                for (int i = 0; i < c.Count; i++)
                {
                    var moved = new List<Vector2>(c);
                    moved[i] = new Vector2(c[i].x, c[i].y + 0.4f);
                    yield return ($"{name}[{i}] 0.4 m off", moved);
                }
                break;
            default: throw new InvalidOperationException($"{name}: {value?.GetType().Name ?? "null"} not covered by this test");
        }
        if (Optional.Contains(name))
        {
            yield return (name + " none", null);
            if (value is float h) yield return (name + " within the search's heading step", h + 3f);
        }
    }

    /// <summary>
    /// The key is the call: every argument the layout is laid from (each a field, and nothing else), the ground it is laid on,
    /// and what else it reads as it stands (the ground's generation, the refusals' settings, the ledge price setting, the end
    /// guard's reach). Two calls differing in any one of them, by as little as a float's last bit, a few centimetres or a
    /// few degrees, are two keys.
    /// </summary>
    [Fact]
    public void TwoCallsDifferingInAnyOneInputNeverShareALayout()
    {
        object?[] args = Arguments();
        var key = Make(args);
        ParameterInfo[] ps = Ctor.GetParameters();
        var fields = typeof(RoadStairRuns.PlanKey).GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name.ToLowerInvariant()).OrderBy(n => n);
        Assert.Equal(ps.Select(p => p.Name!.ToLowerInvariant()).OrderBy(n => n), fields);
        var again = Make(Arguments());
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

    // --- The real callers ----------------------------------------------------------------------------------------

    /// <summary>RoadStairRuns.Plan by reflection (as StairAllowanceTests calls it), its later optional arguments left at
    /// their defaults.</summary>
    private StairPlan Plan(Vector2 a, Vector2 b, float? arriving, float? leaving, List<Vector2>? corridor, bool retried = false)
    {
        MethodInfo plan = typeof(RoadStairRuns).GetMethod("Plan", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] given = { a, b, StairKit.Stone, RoadStairRuns.LayoutGround.Lattice(_world), arriving, leaving, corridor,
            StairCover.RoadStaircase, RoadStairRuns.TopLandings, null, null, retried, true };
        var args = new object?[plan.GetParameters().Length];
        for (int i = 0; i < args.Length; i++) args[i] = i < given.Length ? given[i] : Type.Missing;
        return (StairPlan)plan.Invoke(null, args)!;
    }

    private static string Said(List<string> differences) => string.Join("; ", differences);

    /// <summary>
    /// A layout asked for a few centimetres or degrees off one already laid (its start 3 cm on, its corridor 0.4 m over, its
    /// leaving heading 3 degrees round, laid as a retry on half the allowance) is laid for its own call: the one laid afresh
    /// for it once the cache is cleared, field for field. Before, it was handed the first's layout.
    /// </summary>
    [Fact]
    public void ALayoutAskedForJustOffOneLaidIsLaidForItsOwnCall()
    {
        Vector2 a = new(-6f, 0f), b = new(36f, 0f);
        var line = new List<Vector2> { a, new(15f, 0f), b };
        StairPlan first = Plan(a, b, 90f, 90f, line);
        Assert.True(first.Reached, first.Reason);
        var calls = new (string what, Func<StairPlan> call)[]
        {
            ("its start 3 cm on", () => Plan(a + new Vector2(0.03f, 0f), b, 90f, 90f, new List<Vector2> { a + new Vector2(0.03f, 0f), new(15f, 0f), b })),
            ("its corridor 0.4 m over", () => Plan(a, b, 90f, 90f, new List<Vector2> { a, new(15f, 0.4f), b })),
            ("leaving 3 degrees round", () => Plan(a, b, 90f, 93f, line)),
            ("a retry", () => Plan(a, b, 90f, 90f, line, retried: true)),
        };
        int differed = 0;
        foreach (var (what, call) in calls)
        {
            RoadStairRuns.ResetCache();
            Plan(a, b, 90f, 90f, line);
            StairPlan handed = call();
            RoadStairRuns.ResetCache();
            StairPlan fresh = call();
            var differences = StairRowLayTests.Differences(fresh, handed);
            Assert.True(differences.Count == 0, $"{what}: {Said(differences)}");
            if (StairRowLayTests.Differences(first, fresh).Count > 0) differed++;
        }
        // The calls lay layouts of their own: else this test proves nothing.
        Assert.True(differed > 0, "no call off the first laid another layout");
    }

    /// <summary>
    /// A road's staircases laid through the generation's layouts (RoadStairRuns.Lay, as every retry, replan and approach
    /// trial lays them; the valley's first through Plan): laid again, the road is handed copies of them, field for field those
    /// it got laid afresh, as it gets after the cache is cleared.
    /// </summary>
    [Fact]
    public void ARoadsStaircasesLaidFromTheCacheAreThoseLaidAfresh()
    {
        var world = new SharedFloorValleyWorld();
        _scope.WithWorld(world);
        var steps = new List<(Vector2 from, Vector2 to)> { (ValleyRoad[3], ValleyRoad[4]), (ValleyRoad[4], ValleyRoad[5]), (ValleyRoad[5], ValleyRoad[6]) };
        RoadStairRuns.ResetCache();
        List<RoadStairSpan>? fresh = RoadStairRuns.Lay(ValleyRoad, steps, new List<RoadCrossing>(), world);
        Assert.True(fresh != null && fresh.Count == 2, "the valley was not laid as two staircases");
        List<RoadStairSpan>? cached = RoadStairRuns.Lay(ValleyRoad, steps, new List<RoadCrossing>(), world);
        var differences = StairRowLayTests.Differences(fresh, cached);
        Assert.True(differences.Count == 0, Said(differences));
        RoadStairRuns.ResetCache();
        List<RoadStairSpan>? again = RoadStairRuns.Lay(ValleyRoad, steps, new List<RoadCrossing>(), world);
        differences = StairRowLayTests.Differences(fresh, again);
        Assert.True(differences.Count == 0, Said(differences));
    }

    /// <summary>
    /// The last island's later layouts laid ahead on spare workers (RoadStairRuns.LayoutsAhead) are the ones its own worker
    /// lays: a road laid with them gets the staircases it gets laid on one thread, and a layout laid ahead is taken.
    /// </summary>
    [Fact]
    public void ARoadsStaircasesLaidAheadAreThoseItsOwnWorkerLays()
    {
        var world = new SharedFloorValleyWorld();
        _scope.WithWorld(world);
        var steps = new List<(Vector2 from, Vector2 to)> { (ValleyRoad[3], ValleyRoad[4]), (ValleyRoad[4], ValleyRoad[5]), (ValleyRoad[5], ValleyRoad[6]) };
        RoadStairRuns.ResetCache();
        List<RoadStairSpan>? alone = RoadStairRuns.Lay(ValleyRoad, steps, new List<RoadCrossing>(), world);
        Assert.NotNull(alone);
        int configured = RoadParallel.Configured;
        RoadParallel.Configured = 4;
        RoadParallel.Islands(1);
        RoadStairRuns.LayoutsAhead.Start();
        int taken = RoadStairRuns.LayoutsAhead.Taken, laid = RoadStairRuns.LayoutsAhead.Laid;
        List<RoadStairSpan>? ahead;
        try
        {
            RoadStairRuns.ResetCache();
            ahead = RoadStairRuns.Lay(ValleyRoad, steps, new List<RoadCrossing>(), world);
        }
        finally
        {
            RoadStairRuns.LayoutsAhead.Drop();
            RoadStairRuns.LayoutsAhead.Stop();
            RoadParallel.Islands(0);
            RoadParallel.Configured = configured;
        }
        var differences = StairRowLayTests.Differences(alone, ahead);
        Assert.True(differences.Count == 0, Said(differences));
        Assert.True(RoadStairRuns.LayoutsAhead.Laid > laid, "nothing was laid ahead");
        Assert.True(RoadStairRuns.LayoutsAhead.Taken > taken, "no layout laid ahead was taken");
    }
}
