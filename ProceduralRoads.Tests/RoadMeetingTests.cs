using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// How a generated road may meet the roads already built: at a junction, at its
/// ends, or along a trunk it shares; never across one, nor beside one at its height.
/// </summary>
public class RoadMeetingTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public RoadMeetingTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    /// <summary>A flat world: a road built north-south, then one east-west across it. The second would cross the first at
    /// grade and is not built (roads that cross are disallowed even if that rejects a POI); one that
    /// ends on the first (a T) is.</summary>
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 40f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 77;
    }

    [Fact]
    public void ARoadDoesNotCrossAnotherButMayMeetIt()
    {
        bool Cross(out bool tee)
        {
            RoadNetworkGenerator.Reset();
            var world = new FlatWorld();
            _scope.WithWorld(world);
            SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "trunk"));
            tee = RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 10f), 0f, new Vector2(0f, 10f), 0f, 4f, "tee");
            return RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, -30f), 0f, new Vector2(80f, -30f), 0f, 4f, "across");
        }
        Assert.False(Cross(out bool tee), "the road across was built");
        Assert.True(tee, "the road ending on the trunk was refused");
    }

    /// <summary>A road at 40 m, the trunk's height, from z = -60 to 60, its x given by <paramref name="x"/>.</summary>
    private static RoadSpatialGrid.PlannedPath Road(Func<float, float> x)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (float z = -60f; z <= 60f; z += 1f) { points.Add(new Vector2(x(z), z)); heights.Add(40f); }
        return new RoadSpatialGrid.PlannedPath(points, heights, points.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 120f, false);
    }

    /// <summary>Beside a built road at its height: a road off
    /// the trunk and back onto it (a lens) or beside it all the way braids and is refused; one coming onto the trunk and
    /// staying (a Y) is not.</summary>
    [Fact]
    public void ARoadDoesNotRunBesideAnotherAtItsHeight()
    {
        RoadNetworkGenerator.Reset();
        var world = new FlatWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "trunk"));
        // On the trunk, 3 m off it from z -20 to 20, back on it.
        var lens = new List<RoadSpatialGrid.PlannedPath> { Road(z => Mathf.Abs(z) < 20f ? 3f : 0f) };
        // 3 m beside it all the way.
        var duplicate = new List<RoadSpatialGrid.PlannedPath> { Road(z => 3f) };
        // In from 20 m off, onto it from z = 0 on.
        var y = new List<RoadSpatialGrid.PlannedPath> { Road(z => z < 0f ? Mathf.Min(20f, -z * 0.6f + 0.5f) : 0f) };
        Assert.True(RoadNetworkGenerator.BraidsNetwork(lens, out _), "the lens");
        Assert.True(RoadNetworkGenerator.BraidsNetwork(duplicate, out _), "the duplicate");
        Assert.False(RoadNetworkGenerator.BraidsNetwork(y, out _), "the Y");
    }

    /// <summary>A lens 4.6 to 5.3 m off the trunk for 25 m, at its height, back on it at both ends: two 4 m roads that far apart still read as one smeared road, so it braids and is refused, however its
    /// distance wavers about 5 m.</summary>
    [Fact]
    public void ALensWaveringAboutFiveMetresOffTheTrunkIsRefused()
    {
        RoadNetworkGenerator.Reset();
        var world = new FlatWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "trunk"));
        var lens = new List<RoadSpatialGrid.PlannedPath> { Road(z => Mathf.Abs(z) < 12.5f ? 4.95f + 0.35f * Mathf.Sin(z * 1.3f) : 0f) };
        Assert.True(RoadNetworkGenerator.BraidsNetwork(lens, out _), "the wavering lens");
        // Clear of it (a verge of grass between the two roads): not beside it.
        var apart = new List<RoadSpatialGrid.PlannedPath> { Road(z => 8f) };
        Assert.False(RoadNetworkGenerator.BraidsNetwork(apart, out _), "8 m off");
    }

    /// <summary>A road that comes in beside the trunk at its height and ends on it (a Y) may run beside it only up to the
    /// cap every parallel run has (RoadBeside.MostRun, away from its own ends): within it, it is a Y; past it, it is refused
    /// like a lens or a duplicate. Judged with its end on the trunk cut off, a short shallow Y read as a duplicate; then any Y passed, however
    /// long it ran in beside the trunk.</summary>
    [Fact]
    public void AYRunsBesideTheTrunkOnlyUpToTheCapEveryParallelRunHas()
    {
        RoadNetworkGenerator.Reset();
        var world = new FlatWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "trunk"));
        // In at a shallow angle, 3 m beside the trunk from z 47 to 54 (7 m away from its end), onto it over its last 6 m.
        var shortY = new List<RoadSpatialGrid.PlannedPath> { Road(z => z < 50f ? Mathf.Min(20f, 3f + (50f - z) * 0.6f) : z < 54f ? 3f : 3f * (60f - z) / 6f) };
        Assert.False(RoadNetworkGenerator.BraidsNetwork(shortY, out _), "a Y within the cap");
        // The same Y with 74 m beside the trunk before it joins: past the cap.
        var longY = new List<RoadSpatialGrid.PlannedPath> { Road(z => z < -20f ? Mathf.Min(20f, 3f + (-20f - z) * 0.6f) : z < 54f ? 3f : 3f * (60f - z) / 6f) };
        Assert.True(RoadNetworkGenerator.BraidsNetwork(longY, out _), "a Y past the cap");
        // Beside it to its end without joining it: a duplicate, as before.
        var besideToTheEnd = new List<RoadSpatialGrid.PlannedPath> { Road(z => z < -20f ? Mathf.Min(20f, 3f + (-20f - z) * 0.6f) : 3f) };
        Assert.True(RoadNetworkGenerator.BraidsNetwork(besideToTheEnd, out _), "beside it to its end, not joining");
    }
}
