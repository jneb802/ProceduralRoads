using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The earthwork cap measures a road against what the terrain writer will write (RoadNetworkGenerator.EarthworkAcross): at
/// the heightmap's vertices the road's surface covers, the surface as the writer fits it (RoadTerrainModifier.WrittenAt,
/// the line along the road), not a level slab at each point's own height read at its centre and both edges. Over a crest
/// the writer's line runs under the crest's points, so on a sidehill it cuts the uphill edge deeper than the slab says.
/// </summary>
public sealed class EarthworkWriterSurfaceTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public EarthworkWriterSurfaceTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>A Meadows world whose ground is <see cref="Height"/>.</summary>
    private sealed class Sidehill : WorldGenerator
    {
        public Func<float, float, float> Height = (_, _) => 60f;
        public override float GetHeight(float wx, float wy) => Height(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 29;
    }

    private const float Width = RoadConstants.DefaultRoadWidth, Grade = 0.3f, Crest = 60f;

    /// <summary>The road's height over a crest at x = 0, along z = 0: up at <see cref="Grade"/>, then down.</summary>
    private static float Road(float x) => Crest - Grade * Mathf.Abs(x);

    /// <summary>A road along z = 0 from x = -20 to 20 over the crest, a point every metre (on the vertices).</summary>
    private static RoadSpatialGrid.PlannedPath Plan()
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (int x = -20; x <= 20; x++) { points.Add(new Vector2(x, 0f)); heights.Add(Road(x)); }
        return new RoadSpatialGrid.PlannedPath(points, heights, new List<RoadPointDebugInfo>(), Width, 40f, false);
    }

    /// <summary>
    /// A crest on a sidehill: the ground follows the road's profile and rises across it, its uphill edge (z = 2) standing
    /// 0.1 m inside <paramref name="cap"/> over the road's points at every point. Level at each point's height, the road
    /// is within the cap everywhere; the writer's surface over the crest is its line along the road through the points that
    /// reach the vertex, which runs under the crest's points, so at the crest the uphill edge is cut past the cap. The
    /// measurement says what the writer's own model of the ground it leaves says (RoadTerrainModifier.ModelledHeight).
    /// </summary>
    [Fact]
    public void ACrestOnASidehillIsCutPastTheCapAtItsUphillEdgeWhereItsPointsLevelAreNot()
    {
        const float cap = 3f;
        float across = (cap - 0.1f) / (Width * 0.5f);
        var world = new Sidehill { Height = (x, z) => Road(x) + across * z };
        _scope.WithWorld(world);
        RoadSpatialGrid.PlannedPath plan = Plan();
        Func<Vector2, float> ground = RoadNetworkGenerator.GroundBeforeRoads(world);

        // Each point level across at its own height: the uphill edge 0.1 m inside the cap.
        for (int i = 0; i < plan.Points.Count; i++)
            Assert.True(ground(plan.Points[i] + new Vector2(0f, Width * 0.5f)) - plan.Heights[i] < cap);

        // The writer's own model of the ground it leaves, at every vertex within half the road's width of a point.
        var stored = new List<RoadSpatialGrid.RoadPoint>();
        for (int i = 0; i < plan.Points.Count; i++) stored.Add(new RoadSpatialGrid.RoadPoint(plan.Points[i], Width, plan.Heights[i]));
        float deepest = 0f;
        Vector2 deepestAt = default;
        foreach (Vector2 p in plan.Points)
            for (int x = Mathf.CeilToInt(p.x - Width * 0.5f); x <= Mathf.FloorToInt(p.x + Width * 0.5f); x++)
                for (int z = -2; z <= 2; z++)
                {
                    var v = new Vector2(x, z);
                    if ((v - p).sqrMagnitude > Width * Width * 0.25f) continue;
                    float g = ground(v), cut = g - RoadTerrainModifier.ModelledHeight(stored, v, g, null);
                    if (cut > deepest) { deepest = cut; deepestAt = v; }
                }
        Assert.True(deepest > cap, $"the writer cuts {deepest:F2} m at {deepestAt}");

        var where = new List<Vector2>();
        int past = RoadNetworkGenerator.PastTerrainLimit(new List<RoadSpatialGrid.PlannedPath> { plan }, cap, where);
        Assert.True(past > 0, $"the writer cuts {deepest:F2} m at {deepestAt}, the measurement found nothing past {cap} m");
        Assert.All(where, p => Assert.InRange(p.x, -8f, 8f));
        RoadNetworkGenerator.Earthwork earth = RoadNetworkGenerator.EarthworkAcross(plan, ground, cap);
        Assert.Equal(deepest, earth.Worst, 3);
    }

    /// <summary>The same road over ground the slab and the writer agree on (a straight grade, ground rising across it):
    /// within the cap by 0.1 m both ways, the edge read at the writer's vertices.</summary>
    [Fact]
    public void AStraightGradeOnASidehillIsMeasuredAsItsPointsLevelAcross()
    {
        const float cap = 3f;
        float across = (cap - 0.1f) / (Width * 0.5f);
        var world = new Sidehill { Height = (x, z) => Crest + Grade * x + across * z };
        _scope.WithWorld(world);
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (int x = -20; x <= 20; x++) { points.Add(new Vector2(x, 0f)); heights.Add(Crest + Grade * x); }
        var plan = new RoadSpatialGrid.PlannedPath(points, heights, new List<RoadPointDebugInfo>(), Width, 40f, false);

        RoadNetworkGenerator.Earthwork earth = RoadNetworkGenerator.EarthworkAcross(plan, RoadNetworkGenerator.GroundBeforeRoads(world), cap);

        Assert.Equal(0, earth.Past);
        Assert.InRange(earth.Worst, cap - 0.11f, cap - 0.09f);
    }
}
