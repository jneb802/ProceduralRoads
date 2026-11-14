using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using static ProceduralRoads.Tests.DvergrViaductTests;

namespace ProceduralRoads.Tests;

/// <summary>
/// The approach discount (RoadPathfinder.ApproachAfterJump, ApproachToJump): the search's own steps within
/// RoadConstants.DefaultViaductApproachReach (50 m) of a viaduct's ends, before its near bank and after its far bank, cost
/// RoadConstants.DefaultViaductApproachFactor (half) of their price. A price only: the same route, the same viaduct.
/// </summary>
public sealed class ViaductApproachTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public ViaductApproachTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static RoadPathfinder Finder(WorldGenerator world, float reach) =>
        new(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f, ViaductApproachReach = reach };

    [Fact]
    public void TheApproachDiscountIsHalfPriceWithinFiftyMetresOfAViaductsEnds()
    {
        Assert.Equal(50f, RoadConstants.DefaultViaductApproachReach);
        Assert.Equal(0.5f, RoadConstants.DefaultViaductApproachFactor);
        var world = new MistValley();
        _scope.WithWorld(world);
        RoadPathfinder plain = Finder(world, 0f), discounted = Finder(world, RoadConstants.DefaultViaductApproachReach);
        Assert.Equal(RoadConstants.DefaultViaductApproachFactor, discounted.ViaductApproachFactor);
        Assert.NotNull(plain.FindPath(new Vector2(-100f, 0f), new Vector2(100f, 0f)));
        Assert.NotNull(discounted.FindPath(new Vector2(-100f, 0f), new Vector2(100f, 0f)));
        // The same viaduct either way, across the flat-topped valley.
        var jump = Assert.Single(plain.LastViaductJumps);
        Assert.Equal(jump, Assert.Single(discounted.LastViaductJumps));
        // Flat ground on both sides: about half of 50 m of road off each side, less a cell's partial reach at each end.
        float saved = plain.LastPathCost - discounted.LastPathCost;
        Assert.InRange(saved, 2f * 0.5f * (50f - 8f), 2f * 0.5f * (50f + 8f));
    }

    /// <summary>A Mistlands valley along z through x = 0 (flats at <see cref="Top"/>, a floor <see cref="Depth"/> below
    /// them), a river across the road at <see cref="RiverX"/> and, east of <see cref="CorridorFrom"/>, land only along
    /// z = 0. Sea past |x|, |z| = 200.</summary>
    private sealed class ValleyRiver : WorldGenerator
    {
        public float Top = 45f, Depth = 12f, FloorHalf = 8f, Face = 1.2f, RiverX = 1000f, RiverHalf = 6f, RiverBed = 26f, CorridorFrom = 1000f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f || wx > CorridorFrom && Mathf.Abs(wy) > 4f) return 0f;
            if (Mathf.Abs(wx - RiverX) <= RiverHalf) return RiverBed;
            return Top - Mathf.Clamp(Depth - (Mathf.Abs(wx) - FloorHalf) * Face, 0f, Depth);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            Heightmap.Biome.Mistlands;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Abs(wx - RiverX) < RiverHalf ? 1f : 0f;
            width = 2f * RiverHalf;
        }
        public override int GetSeed() => 76;
    }

    private static readonly Vector2 West = new(-100f, 0f), East = new(100f, 0f);

    /// <summary>What the discount takes off the road across the valley (the viaduct from x = -24 to x = 16, flat road
    /// on either side), and the one viaduct both searches take.</summary>
    private float Saved(ValleyRiver world)
    {
        _scope.WithWorld(world);
        RoadPathfinder plain = Finder(world, 0f), discounted = Finder(world, RoadConstants.DefaultViaductApproachReach);
        Assert.NotNull(plain.FindPath(West, East));
        Assert.NotNull(discounted.FindPath(West, East));
        var jump = Assert.Single(plain.LastViaductJumps);
        Assert.Equal(jump, Assert.Single(discounted.LastViaductJumps));
        Assert.Equal(new Vector2(-24f, 0f), jump.Item1);
        Assert.Equal(new Vector2(16f, 0f), jump.Item2);
        return plain.LastPathCost - discounted.LastPathCost;
    }

    [Fact]
    public void TheDiscountReachesFiftyMetresOfTheRoadsOwnStepsEachSideTheStepAcrossItsEndInPart()
    {
        // Half of 50 m off each side: six whole steps and a quarter of the seventh; the 0.45 more is the far bank's first
        // step, up the valley's face, dearer than flat road. (Measured as the crow flies, with whole steps after the far bank
        // and arrivals kept by cell alone, it came to 44.)
        Assert.InRange(Saved(new ValleyRiver()), 50f, 51f);
    }

    [Fact]
    public void ARiverRightAfterAViaductEndsTheDiscountAndIsNotDiscounted()
    {
        // The far bank at x = 16, two steps (16 m) to the river's near bank at x = 32, the crossing to x = 48: 25 off
        // before the near bank, 8 off the two steps, nothing off the crossing or past it. (The crossing used to spend one
        // step of the reach and leave the rest to the road past the river: 46.6 off.)
        Assert.InRange(Saved(new ValleyRiver { RiverX = 40f }), 33f, 34f);
    }

    [Fact]
    public void ADearerArrivalWithTheDiscountStillAheadIsNotLostToACheaperPlainOne()
    {
        // East of x = 40 the only land is the road's line, so every way east passes the cell at x = 48, 32 m past the far
        // bank with 18 m of the discount left. A second start there, priced a little under the viaduct's road to it: the
        // search's cheapest arrival there is that start's, plain, but the road over the viaduct still costs less to the end
        // (18 m of the 52 left at half price) and is the road found.
        var world = new ValleyRiver { CorridorFrom = 40f };
        _scope.WithWorld(world);
        var at = new Vector2(48f, 0f);
        RoadPathfinder finder = Finder(world, RoadConstants.DefaultViaductApproachReach);
        Assert.NotNull(finder.FindPath(West, at));
        float toThere = finder.LastPathCost;
        Assert.NotNull(finder.FindPath(West, East));
        float over = finder.LastPathCost;
        Assert.NotNull(finder.FindPathBetween(new[] { (West, 0f), (at, toThere - 1f) }, new[] { (East, 0f) }, null, null));
        Assert.Equal(0, finder.LastStartIndex);
        Assert.Single(finder.LastViaductJumps);
        Assert.Equal(over, finder.LastPathCost);
        Assert.True(over < toThere - 1f + Vector2.Distance(at, East));
    }

    /// <summary>The jump's credit for the steps before its near bank (RoadPathfinder.ApproachToJump), on a path given as
    /// cells, each step priced at its length (flat road).</summary>
    private float Credit(params (int x, int y)[] cells)
    {
        var world = new ValleyRiver();
        _scope.WithWorld(world);
        RoadPathfinder finder = Finder(world, RoadConstants.DefaultViaductApproachReach);
        var cameFrom = new Dictionary<Vector2i, Vector2i>(RoadGridComparer.Instance);
        var gCosts = new Dictionary<Vector2i, float>(RoadGridComparer.Instance);
        float g = 0f;
        gCosts[new Vector2i(cells[0].x, cells[0].y)] = 0f;
        for (int i = 1; i < cells.Length; i++)
        {
            Vector2i a = new(cells[i - 1].x, cells[i - 1].y), b = new(cells[i].x, cells[i].y);
            g += Vector2.Distance(RoadPathfinder.CellCentre(a), RoadPathfinder.CellCentre(b));
            cameFrom[b] = a;
            gCosts[b] = g;
        }
        var from = new Vector2i(cells[cells.Length - 1].x, cells[cells.Length - 1].y);
        const float jumpCost = 1000f;
        return jumpCost - finder.ApproachToJump(from, new Vector2i(from.x + 5, from.y), jumpCost, cameFrom, gCosts,
            new HashSet<Vector2i>(RoadGridComparer.Instance));
    }

    [Fact]
    public void AWindingApproachIsCreditedFiftyMetresOfItsOwnStepsNotWhatLiesWithinFiftyMetres()
    {
        // A zigzag of 8 m steps, east and north in turn: eight of them (64 m of road) end within 50 m of
        // the near bank as the crow flies.
        var zigzag = new List<(int, int)> { (0, 0) };
        for (int i = 0; i < 16; i++) zigzag.Add(i % 2 == 0 ? (zigzag[i].Item1 + 1, zigzag[i].Item2) : (zigzag[i].Item1, zigzag[i].Item2 + 1));
        Assert.Equal(0.5f * 50f, Credit(zigzag.ToArray()), 3);
    }

    [Fact]
    public void TheCreditBeforeANearBankStopsAtACrossing()
    {
        // Two 8 m steps back from the near bank, then a crossing three cells long, then road: only the two steps.
        Assert.Equal(0.5f * 16f, Credit((-9, 0), (-8, 0), (-7, 0), (-6, 0), (-3, 0), (-2, 0), (-1, 0)), 3);
    }

    /// <summary>Mistlands valleys every 80 m along x (flats at 60, floors 14 m down, 16 m wide), so every cell lies within
    /// 50 m of a far bank; sea past |x|, |z| = 200.</summary>
    private sealed class Valleys : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float x = ((wx + 400f) % 80f) - 40f;
            return 60f - Mathf.Clamp(14f - (Mathf.Abs(x) - 8f) * 1.2f, 0f, 14f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            Heightmap.Biome.Mistlands;
        public override int GetSeed() => 76;
    }

    private static int Iterations(RoadPathfinder finder) => (int)typeof(RoadPathfinder)
        .GetField("m_lastIterations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(finder)!;

    /// <summary>The discounted stretch after a far bank is one search state a cell beside the plain one, its reach left on its
    /// label, never one state for each mix of steps that reaches the cell: a search over land where every cell is within 50 m
    /// of a far bank takes at most two states a cell, so it floods the island in at most twice the plain search's iterations,
    /// and across four valleys it finds its road with its four viaducts, cheaper for the discount. Kept apart by the steps
    /// taken, the states ran many times the cells and the search across the valleys spent its whole budget and found nothing
    /// (the live Mistlands viaducts lost with their roads).</summary>
    [Fact]
    public void TheStretchAfterAFarBankIsOneStateACellSoASearchAcrossValleysKeepsWithinItsBudget()
    {
        var world = new Valleys();
        _scope.WithWorld(world);
        RoadPathfinder plain = Finder(world, 0f), discounted = Finder(world, RoadConstants.DefaultViaductApproachReach);
        // Into the sea: no road, so each search takes every state it can reach.
        Assert.Null(plain.FindPath(new Vector2(-150f, 0f), new Vector2(500f, 0f)));
        Assert.Null(discounted.FindPath(new Vector2(-150f, 0f), new Vector2(500f, 0f)));
        Assert.True(Iterations(discounted) <= 2 * Iterations(plain), $"{Iterations(discounted)} states against {Iterations(plain)} cells");
        Assert.True(Iterations(discounted) < RoadPathfinder.MaxIterations);
        // Across the four valleys.
        Assert.NotNull(plain.FindPath(new Vector2(-150f, 0f), new Vector2(150f, 0f)));
        Assert.NotNull(discounted.FindPath(new Vector2(-150f, 0f), new Vector2(150f, 0f)));
        Assert.Equal(4, plain.LastViaductJumps.Count);
        Assert.Equal(plain.LastViaductJumps, discounted.LastViaductJumps);
        Assert.True(discounted.LastPathCost < plain.LastPathCost);
    }
}
