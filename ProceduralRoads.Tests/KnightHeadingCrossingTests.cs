using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using static ProceduralRoads.Tests.DvergrViaductTests;

namespace ProceduralRoads.Tests;

/// <summary>
/// Crossings on every heading the search walks: a river and a Mistlands valley lying square to a knight's heading (2, 1)
/// are crossed on that heading by a generated road, and built on the hammer's nearest heading (22.5 degrees), each
/// structure's line turned about its middle (BridgeLayout.TurnedOntoPlaceableHeading). Before, the search jumped water
/// and valleys on the grid's eight headings only, so such a site was crossed slantwise on a longer deck or not at all.
/// </summary>
public sealed class KnightHeadingCrossingTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public KnightHeadingCrossingTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }

    public void Dispose()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    /// <summary>The knight's heading (2, 1), and the distance of a point along it.</summary>
    private static readonly Vector2 Knight = new Vector2(2f, 1f).normalized;
    private static float Along(float x, float z) => x * Knight.x + z * Knight.y;

    /// <summary>A river 48 m wide square to the knight's heading through the origin, land at 33 either side.</summary>
    private sealed class SlantRiver : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f) return 20f;
            return Mathf.Abs(Along(wx, wy)) < 24f ? 26f : 33f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Abs(Along(wx, wy)) < 24f ? 1f : 0f;
            width = weight > 0f ? 48f : 0f;
        }
    }

    /// <summary>A Mistlands valley 18 m deep with faces of 3 in 1, square to the knight's heading, its rims 35 m apart: two
    /// knight's steps (35.8 m) span it, a grid heading only on 48 m.</summary>
    private sealed class SlantMistValley : WorldGenerator
    {
        public float Top = 60f, Depth = 18f, FloorHalf = 11.5f, Face = 3f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return Top - Mathf.Clamp(Depth - (Mathf.Abs(Along(wx, wy)) - FloorHalf) * Face, 0f, Depth);
        }
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width) { weight = 0f; width = 0f; }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 76;
    }

    /// <summary>Degrees between a line's direction and the knight's heading, either way along it.</summary>
    private static float OffKnight(Vector2 direction)
    {
        float cos = Mathf.Abs(Vector2.Dot(direction.normalized, Knight));
        return (float)Math.Acos(Mathf.Clamp01(cos)) * Mathf.Rad2Deg;
    }

    [Fact]
    public void ARiverSquareToAKnightsHeadingIsBridgedOnItAndBuiltOnTheHammersNearest()
    {
        var world = new SlantRiver();
        _scope.WithWorld(world);
        var finder = new RoadPathfinder(world) { Bridges = true, Fords = false, Meander = 0f };
        SetPathfinder(finder);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-96f, 0f, -48f), 0f, new Vector3(96f, 0f, 48f), 0f, 4f, "knight river"));
        RoadCrossing bridge = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
        Assert.Equal(CrossingKind.Bridge, bridge.Kind);
        float heading = BridgeLayout.YawDegrees(bridge.Direction);
        Assert.True(BridgeLayout.HeadingIsPlaceable(heading), $"built at {heading:F2} deg");
        // On the hammer's nearest heading to the knight's, not slantwise on a grid heading 18 degrees further round.
        Assert.True(OffKnight(bridge.Direction) < BridgeLayout.PlaceableHeadingStep * 0.5f, $"built at {heading:F2} deg");
        Assert.True(bridge.Width < 64f, $"a {bridge.Width:F1} m deck over a 48 m river");
        Assert.NotEmpty(BridgeStructures.Solve(bridge, world, 1));
    }

    /// <summary>Mistlands viaducts only: a valley viaduct is offered on the grid's eight headings, water crossings on all
    /// sixteen (RoadPathfinder.ViaductDirections). A valley square to a knight's heading, which a generated road crosses beside
    /// the knight's river of the test above, is crossed on a grid heading's viaduct, met at both ends and built of the kit.
    /// Before, the cheaper knight's viaduct was bought first, at every valley.</summary>
    [Fact]
    public void AValleySquareToAKnightsHeadingIsCrossedOnAGridHeadingsViaduct()
    {
        var world = new SlantMistValley();
        _scope.WithWorld(world);
        var finder = new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
        SetPathfinder(finder);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-96f, 0f, -48f), 0f, new Vector3(96f, 0f, 48f), 0f, 4f, "knight valley"));
        var all = RoadNetworkGenerator.GetRoadCrossings();
        Assert.True(all.Count == 1, $"{all.Count} crossings; jumps {string.Join(";", finder.LastViaductJumps)}; stairs {StairStructures.Runs.Count}");
        RoadCrossing viaduct = all[0];
        Assert.True(viaduct.ValleyJump);
        Assert.False(RoadNetworkGenerator.OnAKnightsHeading(viaduct), $"{viaduct}");
        Assert.True(BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(viaduct.Direction), 0.5f));
        Assert.True(DvergrViaduct.Applies(viaduct, world));
        Assert.All(BridgeStructures.Solve(viaduct, world, world.GetSeed()), p => Assert.True(IsDvergr(p), p.Prefab));
        Assert.True(RoadSpatialGrid.TryGetRoadWithin(viaduct.FromBank, 3f, out _), $"no road at {viaduct.FromBank}");
        Assert.True(RoadSpatialGrid.TryGetRoadWithin(viaduct.ToBank, 3f, out _), $"no road at {viaduct.ToBank}");
    }

    /// <summary>A site kept to the grid's headings (RoadPathfinder.KeepToGridHeadingsAt, where a knight's crossing had an end not
    /// met) is crossed there on a grid heading, the river and the valley alike; elsewhere the knight's heading is still taken.
    /// The generator's verdict on which crossings were bought on a knight's heading (OnAKnightsHeading) reads the moves.</summary>
    [Fact]
    public void ASiteKeptToTheGridIsCrossedOnAGridHeading()
    {
        var world = new SlantRiver();
        _scope.WithWorld(world);
        Vector2 a = new(-96f, -48f), b = new(96f, 48f);
        var free = new RoadPathfinder(world) { Bridges = true, Fords = false, Meander = 0f }.FindPath(a, b);
        var kept = new RoadPathfinder(world) { Bridges = true, Fords = false, Meander = 0f };
        kept.KeepToGridHeadingsAt(new Vector2(0f, 0f), 1000f);
        var square = kept.FindPath(a, b);
        Assert.NotNull(free);
        Assert.NotNull(square);
        RoadCrossing onKnight = Assert.Single(RoadCrossingDetector.Detect(free!, world, bridges: true, fords: false));
        RoadCrossing onGrid = Assert.Single(RoadCrossingDetector.Detect(square!, world, bridges: true, fords: false));
        onKnight.Searched = RoadNetworkGenerator.SearchedMoves(free!, onKnight);
        onGrid.Searched = RoadNetworkGenerator.SearchedMoves(square!, onGrid);
        Assert.True(RoadNetworkGenerator.OnAKnightsHeading(onKnight), $"{onKnight}");
        Assert.False(RoadNetworkGenerator.OnAKnightsHeading(onGrid), $"{onGrid}");
        float off = Mathf.Abs(BridgeLayout.YawDegrees(onGrid.Direction)) % 45f;
        Assert.True(Mathf.Min(off, 45f - off) < 0.5f, $"built at {BridgeLayout.YawDegrees(onGrid.Direction):F1} deg");
        kept.ClearNoStairs();
        Assert.Equal(free, kept.FindPath(a, b));
    }

    /// <summary>A knight's crossing refused for an end not met keeps its site to the grid's headings and bans nothing
    /// (RoadNetworkGenerator.BanForTheNextSearch): the next search crosses the same river on a grid heading, the square
    /// crossing the retry is for. Banned first, as before, the site's square crossing fell under the knight's ban too (a ban
    /// matches any crossing within a cell of its banks). With no fallback left it is banned.</summary>
    [Fact]
    public void AKnightsUnmetCrossingIsRetriedOnTheGridBeforeItsSiteIsBanned()
    {
        var world = new SlantRiver();
        _scope.WithWorld(world);
        Vector2 a = new(-96f, -48f), b = new(96f, 48f);
        var finder = new RoadPathfinder(world) { Bridges = true, Fords = false, Meander = 0f };
        var first = finder.FindPath(a, b)!;
        RoadCrossing knight = Assert.Single(RoadCrossingDetector.Detect(first, world, bridges: true, fords: false));
        knight.Searched = RoadNetworkGenerator.SearchedMoves(first, knight);
        Assert.True(RoadNetworkGenerator.OnAKnightsHeading(knight));
        Assert.True(RoadNetworkGenerator.BanForTheNextSearch(finder, new List<RoadCrossing> { knight }, knight, fallbackLeft: true));
        var again = finder.FindPath(a, b);
        Assert.NotNull(again);
        // Within the site only a grid heading crosses now; the knight's jump itself is not banned (nothing is), so a knight's
        // crossing outside the site may still be taken.
        foreach (RoadCrossing c in RoadCrossingDetector.Detect(again!, world, bridges: true, fords: false))
        {
            c.Searched = RoadNetworkGenerator.SearchedMoves(again!, c);
            if (Vector2.Distance(c.Center, knight.Center) <= knight.Width * 0.5f + RoadPathfinder.CellSize)
                Assert.False(RoadNetworkGenerator.OnAKnightsHeading(c), $"{c} at the knight's site");
        }
        // The site's square crossing is open: kept to the grid on the whole river, the search crosses square, at the site.
        var square = new RoadPathfinder(world) { Bridges = true, Fords = false, Meander = 0f };
        Assert.True(RoadNetworkGenerator.BanForTheNextSearch(square, new List<RoadCrossing> { knight }, knight, fallbackLeft: true));
        square.KeepToGridHeadingsAt(new Vector2(0f, 0f), 1000f);
        RoadCrossing onGrid = Assert.Single(RoadCrossingDetector.Detect(square.FindPath(a, b)!, world, bridges: true, fords: false));
        Assert.True(Vector2.Distance(onGrid.Center, knight.Center) <= knight.Width * 0.5f + RoadPathfinder.CellSize, $"{onGrid} not at {knight}");
        // No fallback left: the knight's crossing is banned as before.
        var spent = new RoadPathfinder(world) { Bridges = true, Fords = false, Meander = 0f };
        Assert.False(RoadNetworkGenerator.BanForTheNextSearch(spent, new List<RoadCrossing> { knight }, knight, fallbackLeft: false));
    }

    /// <summary>A knight's heading is any heading but the grid's eight, read from the heading owner (BridgeLayout): the grid's
    /// are the hammer's even steps, a knight's turned jump an odd step, and an unturned knight's line no step at all.</summary>
    [Theory]
    [InlineData(1f, 0f, false)]
    [InlineData(1f, 1f, false)]
    [InlineData(0f, -1f, false)]
    [InlineData(2f, 1f, true)]
    [InlineData(-1f, 2f, true)]
    [InlineData(0.9238795f, 0.3826834f, true)]
    [InlineData(-0.3826834f, -0.9238795f, true)]
    public void AKnightsHeadingIsReadFromTheHammersSteps(float x, float z, bool knight)
    {
        var c = RoadCrossing.Between(new Vector2(0f, 0f), new Vector2(x * 40f, z * 40f), 20f, new Vector2(x * 20f, z * 20f), 10f, CrossingKind.Bridge);
        Assert.Equal(knight, RoadNetworkGenerator.OnAKnightsHeading(c));
    }
}
