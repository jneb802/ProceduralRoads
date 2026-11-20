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
/// A road arriving at a staircase's foot is held to the foot's height, and the grade limiter keeps it on the line climbing
/// to it at the cap. Where its ground climbs to the foot faster than that, the road stood on an embankment at the bottom of
/// the climb, the ground lower on both sides (a road to a staircase's foot at -4764.9,1737.7 stood 6 m over a gully on a
/// slope falling sideways). The staircase starts lower instead, where the road can climb to it following its ground
/// (RoadStairRuns.StartLower): more stairs, no ridge.
/// </summary>
public sealed class StairFootApproachTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly IDisposable _cap = StaticOverride.Keep(() => RoadNetworkGenerator.EarthworkCap);

    public StairFootApproachTests() => RoadNetworkGenerator.Reset();

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

    /// <summary>A slope falling 0.6 east (to +x), a shallow gully 1.5 m deep and 8 m wide along x = 0, running out over its last 30 m to z = 0, its floor
    /// level at 150 south of z = -60 and climbing <see cref="Climb"/> from there to the foot of a face of 1 at z = 0, which
    /// climbs 24 m to a flat top; sea past 200 m.</summary>
    private sealed class GullyClimbWorld : WorldGenerator
    {
        public float Climb = 0.45f;

        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float foot = 150f + 60f * Climb;
            float along = wy <= -60f ? 150f : wy <= 0f ? 150f + Climb * (wy + 60f) : wy <= 24f ? foot + wy : foot + 24f;
            float gully = Mathf.Abs(wx) < 4f ? 0.75f * (1f + Mathf.Cos(Mathf.PI * wx / 4f)) * Mathf.Clamp01(-wy / 30f) : 0f;
            return along - 0.6f * Mathf.Clamp(wx, -40f, 40f) - gully;
        }

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 77;
    }

    /// <summary>The road north along the gully's floor (x = 0) from z = -120 to 48, the search's stair steps up the face from
    /// z = 0 to 24 (the climb to the face's foot within the search's own grade), planned through the one pipeline.</summary>
    private RoadNetworkGenerator.RoadPlan PlanUpTheGully(GullyClimbWorld world)
    {
        RoadNetworkGenerator.Reset();
        _scope.WithWorld(world);
        // The mod's earthwork cap (the suite runs with it off, PlainRoads).
        RoadNetworkGenerator.EarthworkCap = 8f;
        var finder = new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
        SetPathfinder(finder);
        var path = new List<Vector2>();
        for (float z = -120f; z <= 48f; z += RoadPathfinder.CellSize) path.Add(new Vector2(0f, z));
        for (float z = 0f; z < 24f; z += RoadPathfinder.CellSize) finder.LastStairSteps.Add((new Vector2(0f, z), new Vector2(0f, z + RoadPathfinder.CellSize)));
        return RoadNetworkGenerator.PlanFromPath(finder, path, path[0], 0f, path[path.Count - 1], 0f, false, false, true, true, 4f);
    }

    /// <summary>How far a road stands over the natural ground beside both its edges before it is on a ridge.</summary>
    private const float OverBothSides = 1f;

    /// <summary>The longest stretch, metres, of <paramref name="plans"/> standing more than <see cref="OverBothSides"/> over
    /// the natural ground a metre out from both its edges, and where it is worst.</summary>
    private static (float length, Vector2 at, float over) LongestRidge(List<RoadSpatialGrid.PlannedPath> plans, WorldGenerator world)
    {
        float Ground(Vector2 p) => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
        float side = RoadNetworkGenerator.RoadWidth / 2f + 1f;
        (float length, Vector2 at, float over) longest = (0f, default, 0f);
        foreach (RoadSpatialGrid.PlannedPath plan in plans)
        {
            if (plan.FollowTerrain) continue;
            float run = 0f, worst = 0f;
            Vector2 worstAt = default;
            for (int i = 0; i < plan.Points.Count; i++)
            {
                Vector2 p = plan.Points[i];
                Vector2 along = (plan.Points[Mathf.Min(i + 1, plan.Points.Count - 1)] - plan.Points[Mathf.Max(0, i - 1)]).normalized;
                Vector2 across = new(-along.y, along.x);
                float over = Mathf.Min(plan.Heights[i] - Ground(p + across * side), plan.Heights[i] - Ground(p - across * side));
                if (over <= OverBothSides) { run = 0f; worst = 0f; continue; }
                if (i > 0) run += Vector2.Distance(plan.Points[i - 1], p);
                if (over > worst) (worst, worstAt) = (over, p);
                if (run > longest.length) longest = (run, worstAt, worst);
            }
        }
        return longest;
    }

    /// <summary>The gully's floor climbing 0.45 for 60 m to the face's foot, within the search's grade but over the road's cap:
    /// held to the foot, the road stood on the line climbing to it at the cap, up to 6 m over the floor at the bottom of the
    /// climb and over the ground on both sides for 25 m. The staircase starts at the bottom of the climb instead, and the road
    /// runs on the gully's floor to it: on no ridge.</summary>
    [Fact]
    public void AStaircaseStartsLowerThanARoadOnAnEmbankmentToItsFoot()
    {
        var world = new GullyClimbWorld();
        var planned = PlanUpTheGully(world);
        Assert.Equal(RoadNetworkGenerator.PlanStop.Planned, planned.Stop);
        Assert.NotNull(planned.Plans);
        var ridge = LongestRidge(planned.Plans!, world);
        Assert.True(ridge.length < 2f, FormattableString.Invariant(
            $"the road to the staircase's foot stands over the ground on both sides for {ridge.length:F1} m, {ridge.over:F1} m at ({ridge.at.x:F1}, {ridge.at.y:F1})"));
        RoadStairSpan stairs = Assert.Single(planned.Stairs);
        Assert.Equal(1, RoadStairRuns.FeetStartedLower);
        // Its foot at the bottom of the climb, on the ground the road arrives on.
        Assert.True(stairs.FromHeight < 150f + 0.45f * 10f, FormattableString.Invariant($"the staircase's foot stands at {stairs.FromHeight:F1}"));
        Assert.True(Mathf.Abs(stairs.FromHeight - BiomeBlendedHeight.GetBlendedHeight(stairs.FromPoint.x, stairs.FromPoint.y, world)) < 0.5f);
    }

    /// <summary>The floor climbing 0.3, within the road's cap: the road climbs to the face's foot on its ground, and the
    /// staircase starts where the search put it.</summary>
    [Fact]
    public void AStaircaseItsRoadClimbsToStartsWhereTheSearchPutIt()
    {
        var world = new GullyClimbWorld { Climb = 0.3f };
        var planned = PlanUpTheGully(world);
        Assert.NotNull(planned.Plans);
        RoadStairSpan stairs = Assert.Single(planned.Stairs);
        Assert.Equal(0, RoadStairRuns.FeetStartedLower);
        Assert.True(stairs.FromPoint.y > -RoadPathfinder.CellSize, $"the staircase starts at {stairs.FromPoint}");
        Assert.True(LongestRidge(planned.Plans!, world).length < 2f);
    }
}
