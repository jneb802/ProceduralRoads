using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// One stretch that cannot get a staircase no longer strips its road of every other staircase (no
/// collateral). Each failing stretch is closed to stairs in turn and the road searched again with stairs open everywhere
/// else; it is never searched again without stairs.
/// </summary>
public class StairCollateralTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>A scarp of 1.2 from 40 to 52 (no road climbs it), a 50 m bench, then a long slope of 0.6 to 76 a road
    /// climbs in switchbacks. Sea past 200 m.</summary>
    private sealed class TwoFaceWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(wx * 1.2f, 0f, 12f) + Mathf.Clamp((wx - 60f) * 0.6f, 0f, 24f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    public StairCollateralTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        RoadStairRuns.MaxStairSpan = 250f;
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    [Fact]
    public void AStretchThatCannotGetStairsLeavesTheRoadItsOtherStaircases()
    {
        var world = new TwoFaceWorld();
        _scope.WithWorld(world);
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        // The long slope's staircase is never laid (longer than a staircase is laid); the scarp's is.
        RoadStairRuns.MaxStairSpan = 24f;

        bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(-60f, 0f), 0f, new Vector2(140f, 0f), 0f, 4f, "two faces");

        Assert.True(built, "not built");
        Assert.Contains(StairStructures.Runs, r => r.Pieces.Any(p => p.Position.x > 0f && p.Position.x < 12f));
        Assert.DoesNotContain(StairStructures.Runs, r => r.Pieces.Any(p => p.Position.x > 62f));
    }

    /// <summary>A long slope of 0.5 a road climbs in switchbacks, flats either side. Sea past 200 m.</summary>
    private sealed class SlopeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 40f + Mathf.Clamp(wx * 0.5f, 0f, 24f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>Where no stretch can get its staircase, the road, after closing them in turn, is searched once as a plain
    /// road before it is refused (a last-resort plain road): a routing fallback, within the grade cap.</summary>
    [Fact]
    public void ARoadWhoseStaircasesNeverLayIsSearchedOnceAsAPlainRoad()
    {
        var world = new SlopeWorld();
        _scope.WithWorld(world);
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f, StairCostFixed = 0f, StairCostPerMeterClimbed = 0f });
        // Stairs cost the search nothing, so it buys them across the whole slope; none is ever laid.
        RoadStairRuns.MaxStairSpan = 1f;

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-60f, 0f), 0f, new Vector2(100f, 0f), 0f, 4f, "slope"), "not built");
        Assert.Empty(StairStructures.Runs);
    }
}
