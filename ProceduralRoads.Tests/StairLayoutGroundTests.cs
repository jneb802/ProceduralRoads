using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// One path to lay a staircase, its ground named in full. The legs of a way onto a placed location's landing
/// (RoadStairRuns.PlanPlacedLocationApproach) go through the generation's layouts (RoadStairRuns.FromLocation, keyed by
/// RoadStairRuns.LocationLay) as a road's staircases do: two calls differing in any one input never share a layout, and an
/// approach laid from the cache is the one laid afresh. And the ground a layout is laid on is in its key
/// (RoadStairRuns.LayoutGround, with the lattice's generation): a layout laid before a location's ground was published is not
/// handed out after it (RoadStairRuns.Plan's key held neither the ground nor its generation).
/// </summary>
public class StairLayoutGroundTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public StairLayoutGroundTests()
    {
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
    }

    public void Dispose()
    {
        PlacedLocations.Reset();
        RoadLocationProtection.Reset();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>A hillside falling to +x (LocationApproachStairTests.HillsideWorld).</summary>
    private sealed class HillsideWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 50f - 0.8f * Mathf.Clamp(wx, -12f, 14f) - 0.3f * Mathf.Max(wx - 14f, 0f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>A frost cave's floor (MountainCave02, LocationApproachStairTests.CaveFloor).</summary>
    private static readonly IReadOnlyList<LevelOp> CaveFloor = new[] { new LevelOp(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f) };

    /// <summary>The hillside with the cave's floor levelled and smoothed, turned to face +x (LocationApproachStairTests.CaveGround).</summary>
    private static float CaveGround(float x, float z)
    {
        float h = new HillsideWorld().GetHeight(x, z);
        LevelOp op = CaveFloor[0];
        Vector2 floor = LocationLevelling.OffsetAt(op, 90f);
        float d = Vector2.Distance(new Vector2(x, z), floor);
        float level = 50f + op.Height;
        if (d <= op.Radius) h = level;
        if (d <= op.SmoothRadius) h = Mathf.Lerp(h, level, 1f - Mathf.Pow(d / op.SmoothRadius, op.SmoothPower));
        return h;
    }

    private static readonly Func<float, float, float> Cave = CaveGround;

    /// <summary>A float's bits, and back (net48 has no SingleToInt32Bits).</summary>
    private static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
    private static float Float(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

    private static PlacedLocationShape CaveShape() => PlacedLocationShape.Of(new Vector2(0f, 0f), 90f, CaveFloor[0], 0f, null, Cave);

    // --- An approach's legs: the key ---------------------------------------------------------------------------

    private static readonly ConstructorInfo LocationLayCtor = typeof(RoadStairRuns.LocationLay).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();

    /// <summary>Every argument of a LocationLay, each set to a value of its own.</summary>
    private static object?[] LocationLayArguments(RoadStairRuns.ApproachBounds bounds) => LocationLayCtor.GetParameters().Select(p => p.Name switch
    {
        "from" => (object?)new Vector2(24.5f, 0.25f),
        "to" => new Vector2(6.5f, -1.75f),
        "bounds" => bounds,
        "kit" => StairKit.Stone,
        "width" => 2f,
        "legs" => 25000,
        "arriving" => (float?)271.5f,
        "leaving" => (float?)91.5f,
        "corridor" => new List<Vector2> { new(24.5f, 0.25f), new(14f, 3f), new(6.5f, -1.75f) },
        "total" => 80000,
        "stall" => 2000,
        "topLandings" => 1,
        "sunkFoot" => true,
        "maxBuried" => 0.25f,
        "arriveWithin" => (float?)6f,
        "onLanding" => true,
        "raisedArrival" => false,
        "after" => (StairContinuation?)new StairContinuation(new Vector2(24.5f, 0.25f), 270f, 31.75f, StairPieceKind.Landing),
        "facing" => (float?)268.5f,
        "ring" => (RoadEndRing?)new RoadEndRing(new Vector2(24.5f, 0.25f), 270f, 31.75f),
        "intoLocation" => (Vector2?)new Vector2(0f, 0f),
        "generation" => 7,
        "unpricedLedge" => false,
        "endGuardReach" => 8f,
        _ => throw new InvalidOperationException($"{p.Name}: not covered by this test"),
    }).ToArray();

    private static RoadStairRuns.LocationLay MakeLocationLay(object?[] args) => (RoadStairRuns.LocationLay)LocationLayCtor.Invoke(args);

    /// <summary>Each value an argument could be changed to, one at a time.</summary>
    private static IEnumerable<(string what, object? value)> Others(string name, object? value)
    {
        switch (value)
        {
            case bool b: yield return (name, !b); break;
            case int i: yield return (name, i + 1); break;
            case float f:
                yield return (name, f + 0.25f); yield return (name + " by an ulp", Float(Bits(f) + 1));
                if (name is "arriving" or "leaving" or "arriveWithin" or "facing") yield return (name + " none", null);
                break;
            case Vector2 v:
                yield return (name + ".x", new Vector2(v.x + 0.25f, v.y)); yield return (name + ".y", new Vector2(v.x, v.y + 0.25f));
                yield return (name + ".x by an ulp", new Vector2(Float(Bits(v.x) + 1), v.y));
                if (name == "intoLocation") yield return (name + " none", null);
                break;
            case StairContinuation r:
                yield return (name + ".Edge.x", new StairContinuation(new Vector2(r.Edge.x + 0.25f, r.Edge.y), r.Heading, r.Height, r.Kind));
                yield return (name + ".Edge.y", new StairContinuation(new Vector2(r.Edge.x, r.Edge.y + 0.25f), r.Heading, r.Height, r.Kind));
                yield return (name + ".Heading", new StairContinuation(r.Edge, r.Heading + 0.25f, r.Height, r.Kind));
                yield return (name + ".Height", new StairContinuation(r.Edge, r.Heading, r.Height + 0.25f, r.Kind));
                yield return (name + ".Kind", new StairContinuation(r.Edge, r.Heading, r.Height, StairPieceKind.Stair));
                yield return (name + " none", null);
                break;
            case RoadEndRing g:
                yield return (name + ".Centre.x", new RoadEndRing(new Vector2(g.Centre.x + 0.25f, g.Centre.y), g.Travel, g.Height, g.Through));
                yield return (name + ".Travel", new RoadEndRing(g.Centre, g.Travel + 0.25f, g.Height, g.Through));
                yield return (name + ".Height", new RoadEndRing(g.Centre, g.Travel, g.Height + 0.25f, g.Through));
                yield return (name + ".Through", new RoadEndRing(g.Centre, g.Travel, g.Height, !g.Through));
                yield return (name + ".Radius", new RoadEndRing(g.Centre, g.Travel, g.Height, g.Through, RoadNetworkGenerator.RoadWidth + 1f));
                yield return (name + " none", null);
                break;
            case StairKit kit:
                foreach (StairKit other in StairKit.All)
                    if (other != kit) yield return (name, other);
                break;
            case RoadStairRuns.ApproachBounds a:
                yield return (name + ": another location's shape", new RoadStairRuns.ApproachBounds(CaveShape(), a.Ground));
                yield return (name + ": another ground", new RoadStairRuns.ApproachBounds(a.Shape, RoadStairRuns.LayoutGround.Own((x, z) => CaveGround(x, z))));
                break;
            case List<Vector2> c:
                yield return (name + " none", null);
                yield return (name + " empty", new List<Vector2>());
                yield return (name + " a point fewer", c.Take(c.Count - 1).ToList());
                yield return (name + " a point more", c.Concat(new[] { new Vector2(2f, 2f) }).ToList());
                for (int i = 0; i < c.Count; i++)
                {
                    var moved = new List<Vector2>(c);
                    moved[i] = new Vector2(c[i].x, c[i].y + 0.25f);
                    yield return ($"{name}[{i}]", moved);
                }
                break;
            default: throw new InvalidOperationException($"{name}: {value?.GetType().Name ?? "null"} not covered by this test");
        }
    }

    /// <summary>
    /// The key of an approach's leg is the call: every argument the layout takes (each a field it is laid from,
    /// LocationLay.Lay, and nothing else), the bounds it keeps to by the location's shape and its ground (made from those alone:
    /// built again, the same key), and what else it reads as it stands (the ground's generation, the ledge price setting, the
    /// end guard's reach). Two calls differing in any one of them, by as little as a float's last bit, are two keys.
    /// </summary>
    [Fact]
    public void TwoApproachLegsDifferingInAnyOneInputNeverShareALayout()
    {
        PlacedLocationShape shape = CaveShape();
        var ground = RoadStairRuns.LayoutGround.Own(Cave);
        object?[] args = LocationLayArguments(new RoadStairRuns.ApproachBounds(shape, ground));
        var key = MakeLocationLay(args);
        ParameterInfo[] ps = LocationLayCtor.GetParameters();
        var fields = typeof(RoadStairRuns.LocationLay).GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => f.Name.ToLowerInvariant()).OrderBy(n => n);
        Assert.Equal(ps.Select(p => p.Name!.ToLowerInvariant()).OrderBy(n => n), fields);
        // The same call again, its bounds made again for the same location on the same ground, its corridor a list of its own.
        var again = MakeLocationLay(LocationLayArguments(new RoadStairRuns.ApproachBounds(shape, ground)));
        Assert.True(key.Equals(again) && key.GetHashCode() == again.GetHashCode(), "the same call is not the same key");
        int variants = 0;
        for (int i = 0; i < ps.Length; i++)
            foreach (var (what, value) in Others(ps[i].Name!, args[i]))
            {
                object?[] changed = (object?[])args.Clone();
                changed[i] = value;
                Assert.False(key.Equals(MakeLocationLay(changed)), $"a call differing only in {what} shares the key");
                variants++;
            }
        Assert.True(variants > ps.Length, $"{variants} variants");
    }

    // --- An approach's legs: the real caller ---------------------------------------------------------------------

    private static RoadStairSpan? PlanAtCave(PlacedLocationShape shape, out string why)
    {
        Vector2 end = new Vector2(24f, 0f), before = end + end.normalized * 30f;
        var request = new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = CaveGround(end.x, end.y), Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
        return RoadStairRuns.PlanPlacedLocationApproach(request, 90f, CaveFloor, TestLattice.Of(Cave), (x, z) => Heightmap.Biome.Mountain, 77,
            out why, placed: shape);
    }

    /// <summary>
    /// A way up onto a placed cave's landing (PlanPlacedLocationApproach, as RoadNetworkGenerator.PlanEntry lays one on every
    /// trial of its road): its legs laid through the generation's layouts and kept there, and laid again for the same location
    /// on the same ground it is handed copies of them, the staircase field for field the one laid afresh, as it is after the
    /// cache is cleared. Before, every leg was laid afresh each time, none kept.
    /// </summary>
    [Fact]
    public void AnApproachLaidFromTheCacheIsTheOneLaidAfresh()
    {
        PlacedLocationShape shape = CaveShape();
        RoadStairSpan? fresh = PlanAtCave(shape, out string why);
        Assert.True(fresh != null, why);
        int kept = RoadStairRuns.LocationLaysKept;
        Assert.True(kept > 0, "no leg of the approach was kept");
        int steps = RoadStairRuns.LastApproachSteps;
        RoadStairSpan? cached = PlanAtCave(shape, out why);
        Assert.Equal(kept, RoadStairRuns.LocationLaysKept);
        Assert.Equal(steps, RoadStairRuns.LastApproachSteps);
        var differences = StairRowLayTests.Differences(fresh, cached);
        Assert.True(differences.Count == 0, string.Join("; ", differences));
        RoadStairRuns.ResetCache();
        RoadStairSpan? again = PlanAtCave(shape, out why);
        differences = StairRowLayTests.Differences(fresh, again);
        Assert.True(differences.Count == 0, string.Join("; ", differences));
    }

    // --- The ground in the key -----------------------------------------------------------------------------------

    /// <summary>A location's apron at 52 m out to 12 m from the origin, a face of 1.2 below it down to 40 m at 22 m out
    /// (LocationApproachStairTests.ApronWorld).</summary>
    private sealed class ApronWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float r = new Vector2(wx, wy).magnitude;
            return 40f + Mathf.Clamp((22f - r) * 1.2f, 0f, 12f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    private static readonly IReadOnlyList<LevelOp> Apron = new[] { new LevelOp(0f, 0f, 0f, 12f, false) };

    private static RoadStairSpan? LayAtApron(WorldGenerator world, out string why)
    {
        var end = new Vector2(-24f, 0f);
        var path = new List<Vector2> { end + new Vector2(-30f, 0f), end };
        return RoadStairRuns.LayLocationApproach(end, world.GetHeight(end.x, end.y), StairLayout.Bearing(path[0], end),
            new Vector2(0f, 0f), 52f, Apron, false, path, 1, new List<RoadStairSpan>(), world, out why);
    }

    /// <summary>An approach staircase onto a location not on the preload list (its ground not known when its road is planned) is
    /// refused: no piece of it stands in the location (RoadLocationProtection.Enterable, through RoadStairRuns.Blocked); onto the
    /// same location on the list it is laid.</summary>
    [Fact]
    public void AnApproachStaircaseOntoALocationNotOnThePreloadListIsRefused()
    {
        var world = new ApronWorld();
        _scope.WithWorld(world);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 20f) });
        RoadStairSpan? off = LayAtApron(world, out string why);
        Assert.True(off == null && why.Contains("another location"), why);
        RoadStairRuns.ResetCache();
        PlacedLocations.Preload(new Vector2(0f, 0f));
        RoadStairSpan? on = LayAtApron(world, out string onWhy);
        Assert.True(on != null, onWhy);
    }

    /// <summary>
    /// A layout laid before a location's ground is published (PlacedLocations.Publish, which names a new ground generation) is not
    /// handed out once it is:
    /// the approach onto that location is laid again on the ground it now leaves, the one laid afresh on it. Before, the
    /// layout's key (RoadStairRuns.Plan) named neither its ground nor its generation, and the same call was handed the
    /// layout laid on the ground as it had been.
    /// </summary>
    [Fact]
    public void ALayoutLaidBeforeALocationsGroundIsPublishedIsNotHandedOutAfter()
    {
        var world = new ApronWorld();
        _scope.WithWorld(world);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 20f) });
        // A location in the Mountains, placed first (on the preload list), its ground not published yet.
        PlacedLocations.Preload(new Vector2(0f, 0f));
        RoadStairSpan? before = LayAtApron(world, out string why);
        Assert.True(before != null, why);
        // The location's ground published: its face 1.5 m lower than the generator's.
        PlacedLocations.Preload(new Vector2(0f, 0f), (x, z) => world.GetHeight(x, z) - (new Vector2(x, z).magnitude > 12f ? 1.5f : 0f));
        RoadStairSpan? after = LayAtApron(world, out string afterWhy);
        RoadStairRuns.ResetCache();
        RoadStairSpan? fresh = LayAtApron(world, out string freshWhy);
        Assert.Equal(freshWhy, afterWhy);
        // The ground it was laid on before changed it: else this test proves nothing.
        Assert.True(fresh == null || StairRowLayTests.Differences(before, fresh).Count > 0, "the published ground changed nothing");
        var differences = StairRowLayTests.Differences(fresh, after);
        Assert.True(differences.Count == 0, string.Join("; ", differences));
    }

    /// <summary>
    /// Every ground a layout is laid on is named in full (LayoutGround): the lattice by its world; the lattice levelled to a
    /// crossing's end by the end's foot, the staircase's foot and the end's height, bit for bit (before, only the height went
    /// into the key, rounded to a centimetre); a location's expected ground by its centre, the ground under it and its levelling,
    /// read by value; a ground of its own by itself. No two of them are one ground.
    /// </summary>
    [Fact]
    public void EveryGroundALayoutIsLaidOnIsNamedInFull()
    {
        var world = new ApronWorld();
        var other = new ApronWorld();
        var meet = new Vector2(10.5f, -3.25f);
        var foot = new Vector2(20.5f, 4.75f);
        RoadStairRuns.LayoutGround lattice = RoadStairRuns.LayoutGround.Lattice(world);
        RoadStairRuns.LayoutGround levelled = RoadStairRuns.LayoutGround.Levelled(world, meet, foot, 41.5f);
        RoadStairRuns.LayoutGround expected = RoadStairRuns.LayoutGround.Expected(new Vector2(0f, 0f), 52f, Apron, world);
        RoadStairRuns.LayoutGround own = RoadStairRuns.LayoutGround.Own(Cave);
        // Each the same made again (the levelling a list of its own, read by value).
        Assert.Equal(lattice, RoadStairRuns.LayoutGround.Lattice(world));
        Assert.Equal(levelled, RoadStairRuns.LayoutGround.Levelled(world, meet, foot, 41.5f));
        Assert.Equal(expected, RoadStairRuns.LayoutGround.Expected(new Vector2(0f, 0f), 52f, Apron.ToList(), world));
        Assert.Equal(own, RoadStairRuns.LayoutGround.Own(Cave));
        Assert.Equal(levelled.GetHashCode(), RoadStairRuns.LayoutGround.Levelled(world, meet, foot, 41.5f).GetHashCode());
        var grounds = new (string what, RoadStairRuns.LayoutGround ground)[]
        {
            ("lattice", lattice), ("levelled", levelled), ("expected", expected), ("own", own),
            ("another world's lattice", RoadStairRuns.LayoutGround.Lattice(other)),
            ("levelled in another world", RoadStairRuns.LayoutGround.Levelled(other, meet, foot, 41.5f)),
            ("levelled from another end", RoadStairRuns.LayoutGround.Levelled(world, new Vector2(Float(Bits(meet.x) + 1), meet.y), foot, 41.5f)),
            ("levelled to another foot", RoadStairRuns.LayoutGround.Levelled(world, meet, new Vector2(foot.x, foot.y + 0.25f), 41.5f)),
            ("levelled to a height an ulp off", RoadStairRuns.LayoutGround.Levelled(world, meet, foot, Float(Bits(41.5f) + 1))),
            ("expected round another centre", RoadStairRuns.LayoutGround.Expected(new Vector2(0.25f, 0f), 52f, Apron, world)),
            ("expected over another root", RoadStairRuns.LayoutGround.Expected(new Vector2(0f, 0f), 52.25f, Apron, world)),
            ("expected with another levelling", RoadStairRuns.LayoutGround.Expected(new Vector2(0f, 0f), 52f, new[] { new LevelOp(0f, 0f, 0f, 12.25f, false) }, world)),
            ("expected with no levelling", RoadStairRuns.LayoutGround.Expected(new Vector2(0f, 0f), 52f, null, world)),
            ("expected in another world", RoadStairRuns.LayoutGround.Expected(new Vector2(0f, 0f), 52f, Apron, other)),
            ("another ground of its own", RoadStairRuns.LayoutGround.Own((x, z) => CaveGround(x, z))),
        };
        for (int i = 0; i < grounds.Length; i++)
            for (int j = 0; j < grounds.Length; j++)
                if (i != j) Assert.False(grounds[i].ground.Equals(grounds[j].ground), $"{grounds[i].what} is {grounds[j].what}");
        // The levelled ground is the lattice no higher than the end within a road's half width of the end's line.
        _scope.WithWorld(world);
        Assert.Equal(Mathf.Min(lattice.Read(15f, 0.75f), 41.5f), levelled.Read(15f, 0.75f));
        Assert.Equal(lattice.Read(15f, 30f), levelled.Read(15f, 30f));
    }
}
