using System.Collections.Generic;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

public class TurnLimitTests
{
    private sealed class Slope : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f + 0.3f * z;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
    }

    private static float SharpestTurn(List<Vector2> path)
    {
        float worst = 0f;
        for (int i = 1; i < path.Count - 1; i++)
        {
            Vector2 a = (path[i] - path[i - 1]).normalized, b = (path[i + 1] - path[i]).normalized;
            float d = Mathf.Clamp(a.x * b.x + a.y * b.y, -1f, 1f);
            worst = Mathf.Max(worst, (float)System.Math.Acos(d) * Mathf.Rad2Deg);
        }
        return worst;
    }

    [Fact]
    public void WithALimitNoStepTurnsSharperThanIt()
    {
        RoadLocationProtection.Reset(); RoadSpatialGrid.Clear();
        var finder = new RoadPathfinder(new Slope()) { SlopeMultiplier = 150f, TurnLimitDegrees = 100f, MaxGrade = RoadConstants.DefaultMaxRoadGrade };
        var path = finder.FindPath(new Vector2(0, 0), new Vector2(0, 160));
        Assert.NotNull(path);
        // The two end points are the exact start and end, not cell centres;
        // judge the grid steps between them.
        Assert.True(SharpestTurn(path!.GetRange(1, path.Count - 2)) <= 100.5f, $"sharpest {SharpestTurn(path):F0}");
    }

    /// <summary>A river north to south, and west of it a river lake 184 m wide, too wide to bridge, between the road's two ends: the road crosses
    /// the river, runs up its far bank and crosses back.</summary>
    private sealed class RiverAndLake : WorldGenerator
    {
        public override float GetHeight(float x, float z)
        {
            if (Mathf.Abs(x) > 400f || Mathf.Abs(z) > 400f) return 0f;
            if (Mathf.Abs(x) < 12f) return 26f;
            if (Lake(x, z)) return 26f;
            return 33f;
        }
        private static bool Lake(float x, float z) => x < -12f && z > 16f && z < 200f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight = Mathf.Abs(x) < 12f || Lake(x, z) ? 1f : 0f; width = weight > 0f ? 200f : 0f; }
    }

    /// <summary>The turn limit binds a crossing as every other step: crossing a river and back, the road never turns
    /// sharper than the limit at a bank, whether the step after a crossing is a road step or the next crossing.</summary>
    [Fact]
    public void ACrossingTurnsNoSharperThanTheLimit()
    {
        RoadLocationProtection.Reset(); RoadSpatialGrid.Clear();
        var finder = new RoadPathfinder(new RiverAndLake()) { Bridges = true, Fords = true, TurnLimitDegrees = 100f, Meander = 0f };
        var path = finder.FindPath(new Vector2(-48, 0), new Vector2(-24, 216));
        Assert.NotNull(path);
        Assert.Contains(path!, p => p.x > 12f);
        Assert.True(SharpestTurn(path!.GetRange(1, path.Count - 2)) <= 100.5f, $"sharpest {SharpestTurn(path):F0}: {string.Join(";", path)}");
    }

    /// <summary>Mistlands viaducts only (the owner's decision of 13 Nov 2026): a viaduct's jump pays the turn's price but is
    /// not held to the turn limit (RoadPathfinder.ViaductTurnPrice in both searches' viaduct loops); every other move still is. A
    /// road starting on a valley's rim turns 90 degrees onto the deck, past a 60 degree limit, where held to it the search
    /// looped back along the rim to come onto the jump within the limit; no other turn of the road is past the limit.</summary>
    [Fact]
    public void AViaductsJumpPaysTheTurnPriceAndIsNotHeldToTheLimit()
    {
        RoadLocationProtection.Reset(); RoadSpatialGrid.Clear();
        var world = new DvergrViaductTests.MistValley();
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithWorld(world);
        var finder = new RoadPathfinder(world) { Stairs = false, Viaducts = true, Bridges = true, TerrainVarianceThreshold = 1000f, Meander = 0f, TurnLimitDegrees = 60f };
        var path = finder.FindPath(new Vector2(-16f, 32f), new Vector2(100f, 40f));
        Assert.NotNull(path);
        var (from, to) = Assert.Single(finder.LastViaductJumps);
        int i = path!.FindIndex(p => Vector2.Distance(p, from) < 1f);
        Assert.True(i > 0 && i + 1 < path.Count && Vector2.Distance(path[i + 1], to) < 1f, $"jump {from} -> {to} not in {string.Join(";", path)}");
        Vector2 arriving = (path[i] - path[i - 1]).normalized, jump = (to - from).normalized;
        float onto = (float)System.Math.Acos(Mathf.Clamp(Vector2.Dot(arriving, jump), -1f, 1f)) * Mathf.Rad2Deg;
        Assert.True(onto > 60.5f, $"onto the deck at {onto:F0} deg: {string.Join(";", path)}");
        var rest = new List<Vector2>(path.GetRange(i, path.Count - i));
        Assert.True(SharpestTurn(rest) <= 60.5f, $"sharpest after the jump {SharpestTurn(rest):F0}: {string.Join(";", path)}");
        Assert.True(SharpestTurn(path.GetRange(0, i + 1)) <= 60.5f, $"sharpest before the jump {SharpestTurn(path.GetRange(0, i + 1)):F0}: {string.Join(";", path)}");
    }

    [Fact]
    public void NoLimitNoWeightLeavesTheSearchAsItWas()
    {
        RoadLocationProtection.Reset(); RoadSpatialGrid.Clear();
        var a = new RoadPathfinder(new Slope()) { SlopeMultiplier = 150f, MaxGrade = 0.35f }.FindPath(new Vector2(0, 0), new Vector2(40, 120));
        var b = new RoadPathfinder(new Slope()) { SlopeMultiplier = 150f, MaxGrade = 0.35f, TurnLimitDegrees = 0f, TurnWeight = 0f }.FindPath(new Vector2(0, 0), new Vector2(40, 120));
        Assert.Equal(a, b);
    }
}
