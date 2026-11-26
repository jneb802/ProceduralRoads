using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A way onto a placed Location's landing whose straight line in crosses a crest (RoadStairRuns.Turn) takes that straight line
/// where it is allowed; where it is refused, the road's own pathfinder searches a way in (RoadLocationEntry.Plan,
/// RoadNetworkGenerator.SearchWayIn), which goes round the crest as a road where the ground lets it, before any staircase over it.
/// </summary>
public class WayInCrestSearchTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private const float Footprint = 20f, Width = 4f;

    /// <summary>MountainCave02's floor, as in <see cref="LocationEntryRoadTests"/>: a levelled landing in front of the Location.</summary>
    private static readonly LevelOp Floor = new(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f);

    // The ground of a Mistlands guard tower as read in game, turned so its landing faces +x: the road ended at its ring 51.1 m out
    // at 38.22 m, its landing at 39.26 m; between them, 27.2 m out from the landing and on the straight line, a spur whose top
    // stands at 45.5 m; 11.75 m to the side of the line abreast of the top the ground stands at 40.89 m (where it was stood on).
    private const float LandingX = 8f, RoadEndOut = 51.1f, SpurOut = 27.2f;
    private const float LandingHeight = 39.26f, RoadEndHeight = 38.22f, SpurTop = 45.5f, BesideTop = 40.89f, BesideOff = 11.75f;

    /// <summary>A taller spur on the same line, its flank abreast of the top as read (40.89 m 11.75 m out): the straight road
    /// through its top is refused (along a ridge), so the way in has to go round it.</summary>
    private const float TallSpurTop = 66f;

    /// <summary>The spur's top in the running test (set by each test's instance; the class's tests run one at a time).</summary>
    private static float s_top = SpurTop;

    /// <summary>The natural ground: a plane from the landing's height to the road end's, and the spur on it, round (a bell), as high
    /// as read at its top and beside it.</summary>
    private sealed class SpurWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Natural(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 77;
    }

    private static float Plane(float x) => LandingHeight + (RoadEndHeight - LandingHeight) * (x - LandingX) / RoadEndOut;

    private static float Natural(float x, float z)
    {
        if (Mathf.Abs(x) > 400f || Mathf.Abs(z) > 400f) return 0f;
        float spurX = LandingX + SpurOut;
        float rise = s_top - Plane(spurX), beside = BesideTop - Plane(spurX);
        // Round: exp(-off^2 / 2 s^2) = beside / rise at BesideOff.
        float s2 = BesideOff * BesideOff / (2f * (float)Math.Log(rise / beside));
        float d2 = (x - spurX) * (x - spurX) + z * z;
        return Plane(x) + rise * (float)Math.Exp(-d2 / (2f * s2));
    }

    /// <summary>The ground the Location's levelling leaves: its floor dug in front of it, facing +x.</summary>
    private static float Ground(float x, float z)
    {
        float h = Natural(x, z);
        Vector2 floor = LocationLevelling.OffsetAt(Floor, 90f);
        float d = Vector2.Distance(new Vector2(x, z), floor);
        float level = Natural(0f, 0f) + Floor.Height;
        if (d <= Floor.Radius) h = level;
        if (d <= Floor.SmoothRadius) h = Mathf.Lerp(h, level, 1f - Mathf.Pow(d / Floor.SmoothRadius, Floor.SmoothPower));
        return h;
    }

    public WayInCrestSearchTests()
    {
        s_top = SpurTop;
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        PlacedLocations.Preload(new Vector2(0f, 0f));
    }

    public void Dispose()
    {
        s_top = SpurTop;
        PlacedLocations.Reset();
        RoadTerrainModifier.ResetForWorld();
        RoadLocationProtection.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static LocationApproachRequest Request(Vector2 end)
    {
        Vector2 before = end + new Vector2(30f, 0f);
        return new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = Ground(end.x, end.y), Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end }, Reach = RoadNetworkGenerator.PlacedLocationReach,
        };
    }

    /// <summary>The way in onto the Location's landing from the road's end, with this thread's search (as generation plans it) or
    /// with <paramref name="search"/>.</summary>
    private static RoadSpatialGrid.PlannedPath? WayIn(WorldGenerator world, PlacedLocationShape shape, Vector2 end, out string why,
        RoadLocationEntry.WayInSearch? search = null)
    {
        RoadPathfinder finder = RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(Width));
        using (RoadNetworkGenerator.PlanningWith(finder, new RoadNetworkGenerator.RoadCapture()))
            return RoadLocationEntry.Plan(Request(end), shape, Width, Ground, out why, search: search);
    }

    /// <summary>The deepest the road stands below the natural ground anywhere along it.</summary>
    private static float DeepestCut(RoadSpatialGrid.PlannedPath road) =>
        Enumerable.Range(0, road.Points.Count).Max(i => Natural(road.Points[i].x, road.Points[i].y) - road.Heights[i]);

    private PlacedLocationShape Spur(out SpurWorld world, out Vector2 end)
    {
        world = new SpurWorld();
        _scope.WithWorld(world);
        end = new Vector2(LandingX + RoadEndOut, 0f);
        return PlacedLocationShape.Of(new Vector2(0f, 0f), 90f, Floor, 0f, null, Ground);
    }

    [Fact]
    public void AWayInWhoseStraightLineOverASpurIsRefusedIsSearchedRoundItsTopAsARoad()
    {
        s_top = TallSpurTop;
        PlacedLocationShape shape = Spur(out SpurWorld world, out Vector2 end);
        // The straight line in crosses the spur's top: the stairs' crest test says so.
        Assert.True(RoadStairRuns.Turn(end, shape.Landing, Ground) is { crest: true });
        Assert.True(Natural(LandingX + SpurOut, 0f) > TallSpurTop - 0.01f);

        // Straight in alone (no search), the road through the top is refused.
        RoadSpatialGrid.PlannedPath? straight = WayIn(world, shape, end, out string whyStraight, (a, b) => null);
        Assert.True(straight == null, "the straight way in through the tall spur's top was taken");
        Assert.Contains("along a ridge", whyStraight);

        RoadSpatialGrid.PlannedPath? road = WayIn(world, shape, end, out string why);
        Assert.True(road != null, why);
        // Searched, the road leaves the straight line round the spur's flank, well below its top, and ends on the landing.
        Assert.Contains(road!.Points, p => Mathf.Abs(p.y) > RoadPathfinder.CellSize * 0.5f);
        Assert.True(Natural(LandingX + SpurOut, 0f) - road.Points.Max(p => Natural(p.x, p.y)) >= StairChain.CrestRise,
            $"over the top: ground {road.Points.Max(p => Natural(p.x, p.y)):F2} under it");
        Assert.True(Vector2.Distance(road.Points[road.Points.Count - 1], shape.Landing) < Footprint, "it ends off the landing");
    }

    [Fact]
    public void AWayInWhoseStraightLineOverASpurIsAllowedTakesItWithoutASearch()
    {
        PlacedLocationShape shape = Spur(out SpurWorld world, out Vector2 end);
        Assert.True(RoadStairRuns.Turn(end, shape.Landing, Ground) is { crest: true });
        int searches = 0;

        // The spur as read: its straight road through the top, in a cut, is allowed.
        RoadSpatialGrid.PlannedPath? road = WayIn(world, shape, end, out string why, (a, b) => { searches++; return null; });

        Assert.True(road != null, why);
        Assert.All(road!.Points, p => Assert.True(Mathf.Abs(p.y) < 1f, $"the straight way in at {p}"));
        Assert.Equal(0, searches);
    }

    [Fact]
    public void TheStraightLineIsTriedBeforeTheSearchRoundACrest()
    {
        PlacedLocationShape shape = Spur(out _, out Vector2 end);
        var lines = new List<List<Vector2>>();

        RoadLocationEntry.Plan(Request(end), shape, Width, Ground, out string why,
            planner: (List<Vector2> line, float h, out string one) => { lines.Add(line); one = "refused"; return null; },
            search: (a, b) => new List<Vector2> { a, (a + b) * 0.5f + new Vector2(0f, 12f), b });

        // At each end tried: its straight line, and only after it is refused the searched line between the same ends.
        Assert.True(lines.Count >= 2, $"{lines.Count} line(s) tried");
        Assert.Equal(2, lines[0].Count);
        Assert.Equal(3, lines[1].Count);
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Count == 3)
            {
                Assert.True(i > 0 && lines[i - 1].Count == 2, $"line {i} searched without its straight line first");
                Assert.Equal(lines[i - 1][0], lines[i][0]);
                Assert.Equal(lines[i - 1][1], lines[i][2]);
            }
        Assert.Contains("refused; " + RoadLocationEntry.SearchedRefusal + "refused", why);
    }

    [Fact]
    public void WhereTheStraightLineIsRefusedAndTheSearchFindsNoneTheRefusalSaysBoth()
    {
        PlacedLocationShape shape = Spur(out _, out Vector2 end);
        var lines = new List<List<Vector2>>();

        RoadLocationEntry.Plan(Request(end), shape, Width, Ground, out string why,
            planner: (List<Vector2> line, float h, out string one) => { lines.Add(line); one = "refused"; return null; },
            search: (a, b) => null);

        // Every line tried is the straight one, and the refusal says the search found none after it.
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Equal(2, l.Count));
        Assert.Contains("refused; " + RoadLocationEntry.SearchedRefusal + "no path found", why);
    }

    [Fact]
    public void ASearchRoundACrestLeavesTheRoadsOwnSearchAsItWas()
    {
        var world = new SpurWorld();
        _scope.WithWorld(world);
        RoadPathfinder finder = RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(Width));
        finder.LastStairSteps.Add((new Vector2(1f, 2f), new Vector2(3f, 4f)));
        finder.LastViaductJumps.Add((new Vector2(5f, 6f), new Vector2(7f, 8f)));
        bool stairs = finder.Stairs, viaducts = finder.Viaducts;

        List<Vector2>? path = finder.FindRoadAside(new Vector2(LandingX + RoadEndOut, 0f), new Vector2(LandingX, 0f),
            new RoadLocationProtection.Targets(new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f)));

        Assert.NotNull(path);
        Assert.Equal(new[] { (new Vector2(1f, 2f), new Vector2(3f, 4f)) }, finder.LastStairSteps);
        Assert.Equal(new[] { (new Vector2(5f, 6f), new Vector2(7f, 8f)) }, finder.LastViaductJumps);
        Assert.Equal(stairs, finder.Stairs);
        Assert.Equal(viaducts, finder.Viaducts);
        Assert.False(finder.StopsAtItsGoal);
    }
}
