using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// Over a hump the search climbed on stairs and came straight down again close by, the road is laid across it on
/// earthwork where that fits the caps (RoadNetworkGenerator.PlanFromPath, RoadStairRuns.Humps): a hump a few cells across
/// reads as a steep step from one 8 m cell to the next, and its two staircases wound up and back down where a cut of a metre
/// or two made a road. A hump too high for the earthwork cap stays stairs.
/// </summary>
public sealed class HumpRoadTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public HumpRoadTests() => RoadNetworkGenerator.Reset();

    private readonly IDisposable _cap = StaticOverride.Keep(() => RoadNetworkGenerator.EarthworkCap);

    public void Dispose()
    {
        _cap.Dispose();
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    /// <summary>A hump across z: the ground along z through <paramref name="profile"/> (z, height), level before its first
    /// point and after its last, the same at every x; sea past 200 m.</summary>
    private sealed class HumpWorld : WorldGenerator
    {
        private readonly (float z, float h)[] m_profile;
        public HumpWorld(params (float z, float h)[] profile) => m_profile = profile;

        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            if (wy <= m_profile[0].z) return m_profile[0].h;
            for (int i = 1; i < m_profile.Length; i++)
                if (wy <= m_profile[i].z)
                    return Mathf.Lerp(m_profile[i - 1].h, m_profile[i].h, (wy - m_profile[i - 1].z) / (m_profile[i].z - m_profile[i - 1].z));
            return m_profile[m_profile.Length - 1].h;
        }

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 77;
    }

    /// <summary>The road along x = 0 from z = -24 to 48 with the search's stair steps up the hump to <paramref name="top"/> and
    /// down from it to <paramref name="foot"/>, planned through the one pipeline.</summary>
    private RoadNetworkGenerator.RoadPlan PlanOver(HumpWorld world, float top, float foot)
    {
        _scope.WithWorld(world);
        // The mod's earthwork cap (the suite runs with it off, PlainRoads).
        RoadNetworkGenerator.EarthworkCap = 8f;
        var finder = new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
        SetPathfinder(finder);
        var path = new List<Vector2>();
        for (float z = -24f; z <= 48f; z += RoadPathfinder.CellSize) path.Add(new Vector2(0f, z));
        for (float z = 0f; z < top; z += RoadPathfinder.CellSize) finder.LastStairSteps.Add((new Vector2(0f, z), new Vector2(0f, z + RoadPathfinder.CellSize)));
        for (float z = top; z < foot; z += RoadPathfinder.CellSize) finder.LastStairSteps.Add((new Vector2(0f, z), new Vector2(0f, z + RoadPathfinder.CellSize)));
        return RoadNetworkGenerator.PlanFromPath(finder, path, path[0], 0f, path[path.Count - 1], 0f, false, false, true, true, 4f);
    }

    /// <summary>The knoll's ground on the straight line between the two road ends, 24 m apart (every 4 m: 32.5, 34, 36, 35.5,
    /// 35.5, 34, 31; here 10 m higher, clear of the sea): a 3.5 m hump, climbed on stairs to the cell at its top and come
    /// down from it. A cut of a couple of metres makes it a road within the grade cap, far inside the earthwork cap: laid as
    /// road, no staircase, cut into the hump.</summary>
    [Fact]
    public void AHumpACutTakesIsLaidAsRoad()
    {
        var world = new HumpWorld((0f, 42.5f), (4f, 44f), (8f, 46f), (12f, 45.5f), (16f, 45.5f), (20f, 44f), (24f, 41f));
        var planned = PlanOver(world, top: 8f, foot: 24f);
        Assert.Equal(RoadNetworkGenerator.PlanStop.Planned, planned.Stop);
        Assert.NotNull(planned.Plans);
        Assert.Empty(planned.Stairs);
        Assert.Equal(1, RoadNetworkGenerator.HumpsLevelled);
        // Cut into the hump where it stands highest, and within the earthwork cap everywhere.
        var points = planned.Plans!.SelectMany(p => p.Points.Select((q, i) => (q, h: p.Heights[i]))).ToList();
        var top = points.OrderBy(t => Mathf.Abs(t.q.y - 8f)).First();
        Assert.True(top.h < 46f - 0.5f, FormattableString.Invariant($"the road at the hump's top stands at {top.h:F2}"));
        Assert.Equal(0, RoadNetworkGenerator.PastTerrainLimit(planned.Plans!, RoadNetworkGenerator.EarthworkCap, new List<Vector2>()));
        // Within the grade cap the planner holds every road to.
        for (int i = 1; i < points.Count; i++)
        {
            float run = Vector2.Distance(points[i - 1].q, points[i].q);
            if (run > 0.5f) Assert.True(Mathf.Abs(points[i].h - points[i - 1].h) / run <= RoadGrade.Configured + 0.02f,
                FormattableString.Invariant($"{(points[i].h - points[i - 1].h) / run:F2} at z {points[i].q.y:F1}"));
        }
    }

    /// <summary>A hump 16 m over ends 32 m apart: a road across it needs a cut of about 10 m, past the earthwork cap. It
    /// stays stairs, straight up and on down over the top: no back-track.</summary>
    [Fact]
    public void AHumpTooHighForTheEarthworkCapStaysStairs()
    {
        var world = new HumpWorld((0f, 42.5f), (16f, 58.5f), (32f, 42.5f));
        var planned = PlanOver(world, top: 16f, foot: 32f);
        Assert.Equal(0, RoadNetworkGenerator.HumpsLevelled);
        Assert.True(planned.Stop == RoadNetworkGenerator.PlanStop.Staircase || planned.Stairs.Count > 0,
            $"laid as road: {planned.Stop}, {planned.Stairs.Count} staircase(s)");
        Assert.Equal(0, RoadStairRuns.BackTracksJoined + RoadStairRuns.BackTracksMerged + RoadStairRuns.BackTracksRefused);
    }
    /// <summary>A hump 7.9 m high whose top is 14 m long, its sides a metre each: the road straight across it is within the
    /// earthwork and grade caps, but runs 12 m in a trench 5 m deep (RoadRidges). It stays stairs.</summary>
    [Fact]
    public void AHumpWhoseRoadRunsInATrenchStaysStairs()
    {
        var world = new HumpWorld((2f, 42.5f), (3f, 50.4f), (17f, 50.4f), (18f, 42.5f));
        var straight = new List<Vector2>();
        for (float z = -24f; z <= 48f; z += 1f) straight.Add(new Vector2(0f, z));
        _scope.WithWorld(world);
        var road = RoadSpatialGrid.PlanRoadPath(straight, 4f, world);
        Assert.NotNull(road);
        Assert.Equal(0, RoadNetworkGenerator.PastTerrainLimit(new List<RoadSpatialGrid.PlannedPath> { road! }, 8f, new List<Vector2>()));
        var trench = Assert.Single(RoadRidges.Find(road!.Points, road.Heights, q => BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world)));
        Assert.Equal(RoadRidges.Kind.Trench, trench.Kind);

        var planned = PlanOver(world, top: 8f, foot: 24f);
        Assert.Equal(0, RoadNetworkGenerator.HumpsLevelled);
        Assert.True(planned.Stop == RoadNetworkGenerator.PlanStop.Staircase || planned.Stairs.Count > 0,
            $"laid as road: {planned.Stop}, {planned.Stairs.Count} staircase(s)");
    }
}
