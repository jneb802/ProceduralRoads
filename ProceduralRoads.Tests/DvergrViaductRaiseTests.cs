using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using StepValley = ProceduralRoads.Tests.DvergrViaductEndStairTests.StepValley;

namespace ProceduralRoads.Tests;

/// <summary>
/// A viaduct at grade or a step or two up (some at grade, some one or two steps up): per crossing and
/// world seed, its deck either meets a bank at the bank's height (the road runs onto it) or stands one or two stair rises
/// over it, each end then a short flight whose foot the road meets (<see cref="DvergrViaduct.RaisedSteps"/>). The same
/// crossing in the same world is always raised the same; about <see cref="DvergrViaduct.DefaultRaisedShare"/> of viaducts
/// are raised, of those about <see cref="DvergrViaduct.TwoStepShare"/> by two; a raise that would take a pier over the
/// ceiling or an end's stairs over <see cref="DvergrViaduct.MaxEndStairRise"/> is taken lower, down to none; and the
/// search never sees it (its price and whether there is a viaduct are the deck's at grade).
/// </summary>
public sealed class DvergrViaductRaiseTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly float _maxEndStairRise = DvergrViaduct.MaxEndStairRise;
    private readonly float _raisedShare = DvergrViaduct.RaisedShare;

    public DvergrViaductRaiseTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        DvergrViaduct.RaisedShare = DvergrViaduct.DefaultRaisedShare;
    }

    public void Dispose()
    {
        DvergrViaduct.RaisedShare = _raisedShare;
        DvergrViaduct.MaxEndStairRise = _maxEndStairRise;
        RoadNetworkGenerator.PieceCommitted = null;
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    private static RoadPathfinder Finder(WorldGenerator world) =>
        new(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f };

    private static float Price(RoadCrossing c, WorldGenerator world) =>
        DvergrViaduct.Price(c, world, RoadConstants.DefaultViaductCostFixed, RoadConstants.DefaultViaductCostPerMeter, RoadConstants.DefaultViaductEndStairCostPerMeter);

    /// <summary>The valley crossed straight along x at <paramref name="z"/>, rim to rim.</summary>
    private static RoadCrossing Across(StepValley world, float z, float half = 24f)
    {
        RoadCrossing? c = DvergrViaduct.ValleyCrossing(new Vector2(-half, z), new Vector2(half, z), world, out string why);
        Assert.True(c != null, $"no viaduct across at z {z}: {why}");
        return c!;
    }

    /// <summary>The first world seed from 1 on whose draw for <paramref name="c"/> is <paramref name="steps"/>.</summary>
    private static int SeedDrawing(RoadCrossing c, int steps)
    {
        for (int seed = 1; seed < 1000; seed++)
            if (DvergrViaduct.DrawnSteps(c, seed) == steps) return seed;
        throw new InvalidOperationException($"no seed draws {steps} steps");
    }

    [Fact]
    public void TheSameCrossingInTheSameWorldIsAlwaysRaisedTheSame()
    {
        var outcomes = new HashSet<int>();
        for (int seed = 1; seed <= 24; seed++)
        {
            var world = new StepValley { Seed = seed, Depth = _ => 18f };
            _scope.WithWorld(world);
            int first = DvergrViaduct.RaisedSteps(Across(world, 0f), world);
            // The same crossing found again, in a world made again from the same seed: the same raise, every time asked.
            var again = new StepValley { Seed = seed, Depth = _ => 18f };
            _scope.WithWorld(again);
            RoadCrossing c = Across(again, 0f);
            Assert.Equal(first, DvergrViaduct.RaisedSteps(c, again));
            Assert.Equal(first, DvergrViaduct.RaisedSteps(c, again));
            Assert.Equal(DvergrViaduct.AtGrade(c, again) + first * BridgeLayout.StairRise, DvergrViaduct.DeckHeight(c, again), 3);
            var (near, far) = DvergrViaduct.Ends(c, again);
            Assert.Equal((first, first), (near.Steps, far.Steps));
            outcomes.Add(first);
        }
        // Not one answer for every world.
        Assert.Equal(new[] { 0, 1, 2 }, outcomes.OrderBy(k => k));
    }

    [Fact]
    public void AboutHalfOfViaductsAreRaisedAndOfThoseAboutHalfByTwoSteps()
    {
        var world = new StepValley { Depth = _ => 18f };
        _scope.WithWorld(world);
        List<RoadCrossing> crossings = new[] { -60f, -30f, 0f, 30f, 60f }.Select(z => Across(world, z)).ToList();
        int[] Count(float share)
        {
            DvergrViaduct.RaisedShare = share;
            var counts = new int[DvergrViaduct.MostRaisedSteps + 1];
            for (int seed = 1; seed <= 100; seed++)
            {
                world.Seed = seed;
                foreach (RoadCrossing c in crossings) counts[DvergrViaduct.RaisedSteps(c, world)]++;
            }
            return counts;
        }
        int[] counts = Count(DvergrViaduct.DefaultRaisedShare);
        float all = counts.Sum(), raised = counts[1] + counts[2];
        Assert.InRange(raised / all, DvergrViaduct.DefaultRaisedShare - 0.07f, DvergrViaduct.DefaultRaisedShare + 0.07f);
        Assert.InRange(counts[2] / raised, DvergrViaduct.TwoStepShare - 0.1f, DvergrViaduct.TwoStepShare + 0.1f);
        Assert.Equal(0.5f, DvergrViaduct.DefaultRaisedShare);
        Assert.Equal(0.5f, DvergrViaduct.TwoStepShare);
        // The setting is the share: none raised at 0, every one at 1.
        Assert.Equal(500, Count(0f)[0]);
        Assert.Equal(0, Count(1f)[0]);
    }

    /// <summary>The search never sees the raise: whether there is a viaduct and what it costs are the deck's at grade,
    /// whatever the draw (a random raise must not steer the road).</summary>
    [Fact]
    public void TheRaiseIsDecidedAfterTheSearchPricesTheViaduct()
    {
        foreach (float drop in new[] { 0f, 1f })
        {
            var world = new StepValley { Depth = _ => 18f, FarDrop = _ => drop };
            _scope.WithWorld(world);
            RoadCrossing c = Across(world, 0f);
            foreach (int steps in new[] { 1, 2 })
            {
                world.Seed = SeedDrawing(c, steps);
                DvergrViaduct.RaisedShare = 0f;
                float price = Price(c, world), deck = DvergrViaduct.DeckHeight(c, world);
                DvergrViaduct.RaisedShare = DvergrViaduct.DefaultRaisedShare;
                Assert.Equal(steps, DvergrViaduct.RaisedSteps(c, world));
                Assert.Equal(deck + steps * BridgeLayout.StairRise, DvergrViaduct.DeckHeight(c, world), 3);
                Assert.Equal(price, Price(c, world), 3);
                Assert.True(DvergrViaduct.Applies(c, world), DvergrViaduct.Refusal(c, world));
                Assert.Equal(c.Center, Across(world, 0f).Center);
            }
        }
    }

    /// <summary>A raised viaduct a road is generated over: each end a flight of the raise's steps (its banks level), the road
    /// arriving at each foot along the flight's line, levelled at the foot's height over its apron, and the stairs laid.</summary>
    [Fact]
    public void ARaisedViaductsEndsAreAStepOrTwoWhoseFootTheRoadMeets()
    {
        DvergrViaduct.RaisedShare = 1f;
        var seen = new HashSet<int>();
        for (int seed = 1; seed <= 16 && seen.Count < 2; seed++)
        {
            RoadNetworkGenerator.Reset();
            RoadSpatialGrid.Clear();
            var world = new StepValley { Seed = seed, Depth = _ => 18f };
            _scope.WithWorld(world);
            SetPathfinder(Finder(world));
            var roads = new List<RoadSpatialGrid.PlannedPath>();
            RoadNetworkGenerator.PieceCommitted = (plan, _) => roads.Add(plan);
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-110f, 0f, -50f), 0f, new Vector3(110f, 0f, 50f), 0f, 4f, $"raised {seed}"));
            RoadCrossing viaduct = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), c => c.ValleyJump);
            int k = DvergrViaduct.RaisedSteps(viaduct, world);
            Assert.InRange(k, 1, DvergrViaduct.MostRaisedSteps);
            seen.Add(k);
            var (near, far) = DvergrViaduct.Ends(viaduct, world);
            Assert.Equal((k, k), (near.Steps, far.Steps));
            foreach (StructureEnds.End stairs in new[] { near, far })
            {
                Assert.Equal(DvergrViaduct.DeckHeight(viaduct, world), stairs.Height, 3);
                float ToRoad(Vector2 at) => roads.Min(r => Enumerable.Range(1, r.Points.Count - 1).Select(i => ToSegment(at, r.Points[i - 1], r.Points[i])).DefaultIfEmpty(float.MaxValue).Min());
                Assert.True(ToRoad(stairs.Meet) <= 0.75f, $"seed {seed}: the road is {ToRoad(stairs.Meet):F1} m from the stairs' foot {stairs.Meet}");
                for (float s = 0f; s <= StructureEnds.ApronLength; s += 0.5f)
                    Assert.True(ToRoad(stairs.Meet - stairs.Up * s) <= 1f, $"seed {seed}: {s:F1} m out from the foot the road is off the flight's line");
                var (plan, i) = roads.SelectMany(r => new[] { (r, 0), (r, r.Points.Count - 1) }).OrderBy(x => Vector2.Distance(x.r.Points[x.Item2], stairs.Meet)).First();
                Assert.True(StructureEnds.Reachable(plan.Heights[i], stairs), $"seed {seed}: the road at the foot stands {plan.Heights[i]:F2}, the foot {stairs.MeetHeight:F2}");
            }
            Assert.Null(MetEnds.Unmet(roads, new List<RoadCrossing> { viaduct }, world));
            List<StructurePiece> pieces = BridgeStructures.Solve(viaduct, world, world.GetSeed());
            foreach (StructureEnds.End stairs in new[] { near, far })
                Assert.Contains(pieces, p => p.Kind == StructurePieceKind.Stair && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), stairs.Meet) <= BridgeLayout.StairRun);
        }
        Assert.Equal(new[] { 1, 2 }, seen.OrderBy(k => k));
    }

    [Fact]
    public void ARaiseOverTheEndStairCapIsTakenLowerDownToAtGrade()
    {
        // The far flat 2 m lower: 2 m of stairs at the far end at grade.
        var world = new StepValley { Depth = _ => 18f, FarDrop = _ => 2f };
        _scope.WithWorld(world);
        RoadCrossing c = Across(world, 0f);
        int two = SeedDrawing(c, 2), one = SeedDrawing(c, 1);
        // At most 3 m: two steps would make the far end 4 m, so one is taken; one is.
        DvergrViaduct.MaxEndStairRise = 3f;
        foreach (int seed in new[] { two, one })
        {
            world.Seed = seed;
            Assert.Equal(1, DvergrViaduct.RaisedSteps(c, world));
            var (near, far) = DvergrViaduct.Ends(c, world);
            Assert.Equal((1, 3), (near.Steps, far.Steps));
        }
        // At most 2 m: the viaduct is at its cap at grade, so it stays there, whatever the draw.
        DvergrViaduct.MaxEndStairRise = 2f;
        foreach (int seed in new[] { two, one })
        {
            world.Seed = seed;
            Assert.True(DvergrViaduct.Applies(c, world), DvergrViaduct.Refusal(c, world));
            Assert.Equal(0, DvergrViaduct.RaisedSteps(c, world));
            Assert.Equal(DvergrViaduct.AtGrade(c, world), DvergrViaduct.DeckHeight(c, world), 3);
            var (near, far) = DvergrViaduct.Ends(c, world);
            Assert.Equal((0, 2), (near.Steps, far.Steps));
        }
    }

    [Fact]
    public void ARaiseOverThePierCeilingIsTakenLowerDownToAtGrade()
    {
        // The middle pier stands on the valley floor, as tall as the valley is deep.
        foreach (var (depth, drawn, taken) in new[] { (21.5f, 1, 0), (21.5f, 2, 0), (20.5f, 2, 1), (20.5f, 1, 1) })
        {
            var world = new StepValley { Depth = _ => depth };
            _scope.WithWorld(world);
            RoadCrossing c = Across(world, 0f, 8f + depth / 1.2f + 2f);
            Assert.Equal(depth, DvergrViaduct.TallestPier(c, world) - DvergrViaduct.RaisedSteps(c, world) * BridgeLayout.StairRise, 1);
            world.Seed = SeedDrawing(c, drawn);
            Assert.Equal(taken, DvergrViaduct.RaisedSteps(c, world));
            Assert.True(DvergrViaduct.TallestPier(c, world) <= DvergrViaduct.PierCeiling, $"{depth} m deep, drawn {drawn}: a pier {DvergrViaduct.TallestPier(c, world):F1} m");
            Assert.True(DvergrViaduct.Applies(c, world), DvergrViaduct.Refusal(c, world));
        }
    }

    /// <summary>A raised end the road reaches no way of meeting is met a step lower, down to at grade, so the raise never
    /// changes whether a viaduct is built. The road runs on 6 m past the far bank and the earthwork cap is half a metre: at
    /// grade the road meets the deck flush; two steps up, neither a flight's foot nor the edge on its fill is reached.
    /// Before, the viaduct was refused there and the road searched round it.</summary>
    [Fact]
    public void ARaisedEndTheRoadCannotMeetIsMetAStepLower()
    {
        var world = new StepValley { Seed = 3, Depth = _ => 18f };
        _scope.WithWorld(world);
        RoadCrossing? Met(float share, out RoadCrossing c)
        {
            DvergrViaduct.RaisedShare = share;
            c = Across(world, 0f);
            var path = new List<Vector2>();
            for (float x = -60f; x <= c.ToBank.x + 6f + 1e-3f; x += 2f) path.Add(new Vector2(x, 0f));
            RoadCrossing crossing = c;
            c.FromIndex = path.FindIndex(p => p.x >= crossing.FromBank.x - 1e-3f);
            c.ToIndex = path.FindIndex(p => p.x >= crossing.ToBank.x - 1e-3f);
            return StructureEnds.MeetEnds(path, new List<RoadCrossing> { c }, world, 0.5f, null);
        }
        Assert.Null(Met(0f, out RoadCrossing atGrade));
        float grade = DvergrViaduct.DeckHeight(atGrade, world);

        Assert.Null(Met(1f, out RoadCrossing raised));
        Assert.Equal(DvergrViaduct.MostRaisedSteps, DvergrViaduct.DrawnSteps(raised, world.GetSeed()));
        var (near, far) = DvergrViaduct.Ends(raised, world);
        float deck = DvergrViaduct.DeckHeight(raised, world);
        Assert.True(deck < grade + DvergrViaduct.MostRaisedSteps * BridgeLayout.StairRise - 0.5f, $"deck {deck:F1} m, at grade {grade:F1} m");
        Assert.Equal((deck, deck), (near.Height, far.Height));
        Assert.All(DvergrViaduct.SolveComplete(raised, world, world.GetSeed()).FindAll(p => p.Prefab == DvergrViaduct.Floor),
            p => Assert.Equal(deck - DvergrViaduct.FloorDrop, p.Position.y, 3));
    }

    private static float ToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector2.Distance(p, a + ab * t);
    }
}
