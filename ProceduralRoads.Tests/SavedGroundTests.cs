using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase saved with the network saves the ground it was settled on (<see cref="StructureGround.Measure"/>), and every
/// terrain write levels to it where the staircase stands (<see cref="StructureGround.Held"/>): a later writer that shapes
/// the ground differently leaves the staircase standing on its ground, neither floating nor buried.
/// </summary>
public sealed class SavedGroundTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithWorld(new FlatWorld());
    private readonly float _batterPerMetre = RoadTerrainModifier.BatterPerMetre, _batterThreshold = RoadTerrainModifier.BatterThreshold;

    public SavedGroundTests()
    {
        RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear();
        RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
    }

    public void Dispose()
    {
        RoadTerrainModifier.BatterPerMetre = _batterPerMetre; RoadTerrainModifier.BatterThreshold = _batterThreshold;
        RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); _scope.Dispose();
    }

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 30f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 31;
    }

    /// <summary>A road 4 m wide along x from -20 to 20, cut 3 m into the flat ground (past the batter's threshold), and a
    /// staircase of pillars standing on the ground beside it, from its edge out over its batter: settled, as generation
    /// settles every staircase.</summary>
    private static StairRun SettledBesideACut()
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (float x = -20f; x <= 20f; x += 1f) { points.Add(new Vector2(x, 0f)); heights.Add(27f); }
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, heights, new List<RoadPointDebugInfo>(), 4f, 40f, false));
        RoadSpatialGrid.FinalizeRoadNetwork();
        var run = new StairRun { From = new Vector2(-6f, 3f), To = new Vector2(6f, 10f), Width = 2f };
        for (int i = 0; i < 8; i++)
            run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = "stone_stair", Position = new Vector3(-6f + i * 1.7f, 30f, 3f + i), YawDegrees = 0f });
        StairStructures.Add(run);
        StairStructures.Settle(run, StructureGround.Vertices(WorldGenerator.instance!));
        return run;
    }

    [Fact]
    public void ASavedStaircaseKeepsItsGroundUnderAChangedWriter()
    {
        StairRun run = SettledBesideACut();
        Assert.NotEmpty(run.Ground);

        // A later build's writer: its batter widens from any cut, four times as fast.
        RoadTerrainModifier.BatterThreshold = 0f;
        RoadTerrainModifier.BatterPerMetre = 4f;
        Func<int, int, float> finished = StructureGround.Vertices(WorldGenerator.instance!);
        Assert.All(run.Ground, pin => Assert.Equal(pin.Height, finished(pin.X, pin.Z)));

        // Saved and loaded, by a build that reads it: the same ground, held.
        byte[] saved = StairStructures.Serialize();
        StairStructures.Reset();
        StairStructures.Load(saved);
        StairRun loaded = Assert.Single(StairStructures.Runs);
        Assert.Equal(run.Ground, loaded.Ground);
        finished = StructureGround.Vertices(WorldGenerator.instance!);
        Assert.All(loaded.Ground, pin => Assert.Equal(pin.Height, finished(pin.X, pin.Z)));

        // Not held, the changed writer leaves other ground under it: the staircase would float or be buried.
        StairStructures.Reset();
        finished = StructureGround.Vertices(WorldGenerator.instance!);
        Assert.Contains(run.Ground, pin => Mathf.Abs(pin.Height - finished(pin.X, pin.Z)) > 0.1f);
    }
}
