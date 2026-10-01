using System.Collections.Generic;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

public class TurnLimitTests
{
    private sealed class Slope : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f + 0.3f * z;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
    }

    private static float SharpestTurn(List<Vector2> path)
    {
        float worst = 0f;
        for (int i = 1; i < path.Count - 1; i++)
        {
            Vector2 a = (path[i] - path[i - 1]).normalized, b = (path[i + 1] - path[i]).normalized;
            float d = Mathf.Clamp(a.x * b.x + a.y * b.y, -1f, 1f);
            worst = Mathf.Max(worst, (float)System.Math.Acos(d) * 57.29578f);
        }
        return worst;
    }

    [Fact]
    public void WithALimitNoStepTurnsSharperThanIt()
    {
        RoadSiteProtection.Reset(); RoadSpatialGrid.Clear();
        var finder = new RoadPathfinder(new Slope()) { SlopeMultiplier = 150f, TurnLimitDegrees = 100f, MaxGrade = 0.35f };
        var path = finder.FindPath(new Vector2(0, 0), new Vector2(0, 160));
        Assert.NotNull(path);
        // The two end points are the exact start and end, not cell centres;
        // judge the grid steps between them.
        Assert.True(SharpestTurn(path!.GetRange(1, path.Count - 2)) <= 100.5f, $"sharpest {SharpestTurn(path):F0}");
    }

    [Fact]
    public void NoLimitNoWeightLeavesTheSearchAsItWas()
    {
        RoadSiteProtection.Reset(); RoadSpatialGrid.Clear();
        var a = new RoadPathfinder(new Slope()) { SlopeMultiplier = 150f, MaxGrade = 0.35f }.FindPath(new Vector2(0, 0), new Vector2(40, 120));
        var b = new RoadPathfinder(new Slope()) { SlopeMultiplier = 150f, MaxGrade = 0.35f, TurnLimitDegrees = 0f, TurnWeight = 0f }.FindPath(new Vector2(0, 0), new Vector2(40, 120));
        Assert.Equal(a, b);
    }
}
