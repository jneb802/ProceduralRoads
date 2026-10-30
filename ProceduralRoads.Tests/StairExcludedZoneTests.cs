using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// All or nothing: a generated staircase with any piece or cut in a zone explored before the network
/// (NetworkCutoff.Excludes) is not built at all. The writers leave such a zone as it is, so a staircase crossing into one
/// stood in the other zones only.
/// </summary>
public class StairExcludedZoneTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithZoneSystem();

    /// <summary>A scarp of 1.2 from x = 24 to 44, across the zone line at x = 32. Sea past 200 m.</summary>
    private sealed class ScarpWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 40f + Mathf.Clamp((wx - 24f) * 1.2f, 0f, 24f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    public StairExcludedZoneTests()
    {
        NetworkCutoff.Forget();
        RoadNetworkGenerator.Reset();
    }

    public void Dispose()
    {
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        RoadNetworkGenerator.Reset();
        NetworkCutoff.Forget();
        _scope.Dispose();
    }

    [Fact]
    public void AStaircaseReachingAnExploredZoneIsNotBuiltAtAll()
    {
        var world = new ScarpWorld();
        _scope.WithWorld(world);
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        // The zone east of x = 32, where the scarp's upper half and its top are, was explored before the network.
        Vector2s explored = ZoneSystem.GetZone(new Vector3(40f, 0f, 0f));
        NetworkCutoff.SnapshotWorld(new[] { explored });
        NetworkCutoff.UseNewNetwork();

        RoadNetworkGenerator.GenerateRoad(new Vector2(-60f, 0f), 0f, new Vector2(120f, 0f), 0f, 4f, "scarp");

        Assert.DoesNotContain(StairStructures.Runs, r => r.Pieces.Any(p => NetworkCutoff.Excludes(ZoneSystem.GetZone(p.Position))));
    }
}
