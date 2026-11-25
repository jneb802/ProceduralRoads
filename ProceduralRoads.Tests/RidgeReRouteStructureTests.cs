using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A ridge re-route is held to the same safety as the earthwork re-route (RoadNetworkGenerator.SafetyFaults), the ground
/// under a structure another road planned included (RoadNetworkGenerator.NearAPlannedStructure).
/// </summary>
[Collection("RoadStatics")]
public sealed class RidgeReRouteStructureTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly IDisposable _grade = GradeCap.At(0.35f);
    private readonly float _cap = RoadNetworkGenerator.EarthworkCap;
    private readonly int _retries = RoadNetworkGenerator.RidgeRetries;

    public RidgeReRouteStructureTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        NetworkCutoff.Forget();
    }

    public void Dispose()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.EarthworkCap = _cap;
        RoadNetworkGenerator.RidgeRetries = _retries;
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        NetworkCutoff.Forget();
        _grade.Dispose();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    /// <summary>Flat ground at 60 m; a block 6 m higher beside a gully 4 m wide along x = <see cref="Floor"/> (a road along its
    /// floor is in a trench), its ends ramped over 20 m; a cliff to the west.</summary>
    private sealed class Gully : WorldGenerator
    {
        public const float Floor = 16f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float d = wx - Floor;
            if (d < -4f) return 90f;
            float block = Mathf.Clamp01((30f - Mathf.Abs(wy)) / 20f + 1f) * Mathf.Clamp01((40f - d) / 20f);
            float wall = Mathf.Clamp01((Mathf.Abs(d) - 2f) / 2f);
            return 60f + 6f * block * wall;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 77;
    }

    private List<RoadSpatialGrid.PlannedPath>? Along(int retries, Action? beforeTheRoad = null)
    {
        RoadNetworkGenerator.Reset();
        var world = new Gully();
        _scope.WithWorld(world);
        beforeTheRoad?.Invoke();
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f, Fords = false, Bridges = false, Stairs = false });
        RoadNetworkGenerator.EarthworkCap = RoadNetworkGenerator.DefaultEarthworkCap;
        RoadNetworkGenerator.RidgeRetries = retries;
        var pieces = new List<RoadSpatialGrid.PlannedPath>();
        var hook = RoadNetworkGenerator.PieceCommitted;
        RoadNetworkGenerator.PieceCommitted = (plan, _) => pieces.Add(plan);
        try
        {
            return RoadNetworkGenerator.GenerateRoad(new Vector2(Gully.Floor, -80f), 0f, new Vector2(Gully.Floor, 80f), 0f, 4f, "gully") ? pieces : null;
        }
        finally { RoadNetworkGenerator.PieceCommitted = hook; }
    }

    private static readonly Vector2 Foot = new(36f, -50f);

    private void AStaircaseFootOnTheRamp()
    {
        var world = WorldGenerator.instance!;
        float ground = StructureGround.Vertices(world)((int)Foot.x, (int)Foot.y);
        var run = new StairRun { From = Foot, To = Foot + new Vector2(0f, 2f), Width = 2f };
        run.Pieces.Add(new StructurePiece { Prefab = "StairFoot", Kind = StructurePieceKind.StairStep, Position = new Vector3(Foot.x, ground, Foot.y) });
        StairStructures.Add(run);
    }

    /// <summary>The finished ground at the foot's vertex now, with the network as stored.</summary>
    private static float GroundAtTheFoot()
    {
        RoadSpatialGrid.FinalizeRoadNetwork();
        return StructureGround.Vertices(WorldGenerator.instance!)((int)Foot.x, (int)Foot.y);
    }

    /// <summary>
    /// A ridge re-route (RoadNetworkGenerator.OffRidges) is held to the safety the earthwork re-route is
    /// (RoadNetworkGenerator.SafetyFaults), the planned structure of another road included (NearAPlannedStructure): a gully
    /// with a block beside it that the road climbs onto to leave its trench; with no structure the re-route is taken and its
    /// climb up the block's ramp changes the ground at (36, -50); with a staircase's foot another road planned there, the
    /// re-route is not taken, the road along the gully is refused for its trench, and the ground under the foot is as it was. On the parent the
    /// re-route was taken for its ridge metres alone and the road was then refused for the ground under the foot: a
    /// buildable road lost.
    /// </summary>
    [Fact]
    public void ARidgeReRouteOverTheGroundUnderAnotherRoadsStructureIsNotTaken()
    {
        Assert.NotNull(Along(2));
        Assert.Equal(1, RoadNetworkGenerator.RidgeReroutes);
        float before = BiomeBlendedHeight.GetBlendedHeight(Foot.x, Foot.y, WorldGenerator.instance!);
        float taken = GroundAtTheFoot();
        Assert.True(Mathf.Abs(taken - before) > RoadConstants.MinHeightDeltaThreshold, "the re-route does not change the ground at the foot: the case is not shown");

        Assert.Null(Along(2, AStaircaseFootOnTheRamp));
        Assert.Equal(0, RoadNetworkGenerator.RidgeReroutes);
        Assert.StartsWith(RoadNetworkGenerator.TrenchRoadRefusal, RoadSpatialGrid.LastRefusal ?? "");
        Assert.Equal(before, GroundAtTheFoot(), 3);
    }
}
