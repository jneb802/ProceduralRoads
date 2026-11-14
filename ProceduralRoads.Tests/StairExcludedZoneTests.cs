using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// All or nothing: a generated staircase with any piece or cut in a zone explored before the network
/// (NetworkCutoff.Avoids) is not planned at all. The writers leave such a zone as it is, so a staircase crossing into one
/// stood in the other zones only.
/// </summary>
public class StairExcludedZoneTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope().WithZoneSystem().WithNetwork(server: true);

    /// <summary>A scarp of 1.2 from x = 24 to 48 (its rim on a search cell line), across the zone line at x = 32. Sea past 200 m.</summary>
    private sealed class ScarpWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 40f + Mathf.Clamp((wx - 24f) * 1.2f, 0f, 28.8f);
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
    public void AZoneRefusedLayoutHasTheSameVerdictAndLabelOnThePlanningPaths()
    {
        var world = new ScarpWorld();
        _scope.WithWorld(world);
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        using var planning = NetworkCutoff.AvoidWhilePlanning(new HashSet<Vector2s> { new(1, 0) }, 0f);
        var plan = new StairPlan
        {
            Reached = true,
            Pieces = { new StairPiece { Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(40f, 60f, 0f) } },
        };
        var path = new List<Vector2> { new(0f, 0f), new(8f, 0f) };
        Assert.NotNull(NetworkGround.StructureRefusal(plan.Pieces.Select(StairRun.LaidPiece), Array.Empty<RoadLocationProtection.Targets>(), out _));
        Assert.NotEqual("", RoadStairRuns.Blocked(plan, world));
        Assert.Equal(RoadStairRuns.ExcludedZone, RoadStairRuns.LocationConflict(plan, new Vector2(-80f, 0f), path, 0,
            (x, z) => world.GetHeight(x, z), world, Array.Empty<RoadStairSpan>()));
        try
        {
            bool usable = (bool)typeof(RoadStairRuns).GetMethod("Usable", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object?[] { plan, path, 0, 1, world, new List<RoadStairSpan>(), null, null })!;
            Assert.False(usable);
            Assert.Equal(RoadStairRuns.Refusal.ExcludedZone, RoadStairRuns.LastRefusal);
            if (RoadNetworkGenerator.RoadsLog)
            {
                Assert.Equal(1, RoadStairRuns.Rejections[RoadStairRuns.ExcludedZone]);
                Assert.False(RoadStairRuns.Rejections.ContainsKey(RoadStairRuns.Said(RoadStairRuns.Refusal.WaterOrLocation)));
            }
        }
        finally { RoadStairRuns.Rejections.Clear(); }
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
        NetworkCutoff.BeginNewNetwork();

        RoadNetworkGenerator.GenerateRoad(new Vector2(-60f, 0f), 0f, new Vector2(120f, 0f), 0f, 4f, "scarp");

        Assert.DoesNotContain(StairStructures.Runs, r => r.Pieces.Any(p => NetworkCutoff.Excludes(ZoneSystem.GetZone(p.Position))));
    }

    [Fact]
    public void AMarkedRoadPlansItsStaircaseIntoExploredLand()
    {
        var world = new ScarpWorld();
        _scope.WithWorld(world).WithZdos();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        Vector2s explored = new(1, 0);
        NetworkCutoff.SnapshotWorld(new[] { explored });
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
        var island = new Island { Id = 1, Min = new Vector2(-200, -200), Max = new Vector2(200, 200), CellSize = 8, WorldOffset = 0 };
        for (int x = -25; x <= 25; x++) for (int z = -25; z <= 25; z++) island.Cells.Add(new Vector2Int(x, z));

        var planned = ManualRoads.Prepare(new[] { new Vector2(-60f, 0f), new Vector2(120f, 0f) }, false, new[] { island });
        Assert.Contains(planned.Append.Runs, r => r.plan.Pieces.Any(p => ZoneSystem.GetZone(p.Position) == explored));
        Assert.True(NetworkCutoff.Excludes(explored));
        ManualRoads.Commit(planned);
        Assert.False(NetworkCutoff.Excludes(explored));
    }

    [Fact]
    public void StairBodiesAndCutsUseThePlanningZonesWhilePlacementKeepsWriterZones()
    {
        var world = new ScarpWorld();
        _scope.WithWorld(world).WithZdos();
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        Vector2s explored = new(1, 0);
        NetworkCutoff.SnapshotWorld(new[] { explored });
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
        var plan = new StairPlan
        {
            Pieces = { new StairPiece { Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(40f, 60f, 0f) } },
            Cuts = { new Vector3(40f, 60f, 0f) },
        };
        using (NetworkCutoff.AvoidWhilePlanning(new HashSet<Vector2s> { explored }, 0f))
        {
            Assert.NotEqual("", RoadStairRuns.Blocked(plan, world));
            Assert.True(RoadStairRuns.OnKeptGround(plan));
        }
        using (NetworkCutoff.AvoidWhilePlanning(new HashSet<Vector2s>(), 0f))
        {
            Assert.Equal("", RoadStairRuns.Blocked(plan, world));
            Assert.False(RoadStairRuns.OnKeptGround(plan));
            Assert.Null(NetworkGround.StructureRefusal(plan.Pieces.Select(StairRun.LaidPiece), Array.Empty<RoadLocationProtection.Targets>(), out _));
            Assert.True(NetworkCutoff.Excludes(explored));
            StairRun run = StairRun.FromPlan(new Vector2(40f, 0f), new Vector2(44f, 0f), 2f, plan);
            StructurePlans.HoldWhole(run.Pieces);
            Assert.Null(StructurePlans.Buildable(run.Pieces));
        }
    }

    /// <summary>
    /// A staircase is held back by its pieces' bodies, not their origins (StructurePlans.ZonesOf, BuriedPieces.BoundsOf turned
    /// as laid): a 2 x 2 stone floor whose origin is 0.5 m inside the zone west of x = 32 reaches 0.5 m over the line into the
    /// explored zone east of it, so the staircase is built in neither zone (no half staircase), and a road's final check reads
    /// that zone as written (ManualRoads.ZonesReached). Turned 45 degrees, one 1.2 m back reaches over too; 1.5 m back it does
    /// not, and it is built. Before, the staircase's pieces held nothing back and a road's check read only their origins.
    /// </summary>
    [Fact]
    public void AStaircaseIsHeldBackByItsPiecesBodiesNotTheirOrigins()
    {
        var world = new ScarpWorld();
        _scope.WithWorld(world).WithZdos();
        Vector2s home = new(0, 0), explored = new(1, 0);
        NetworkCutoff.SnapshotWorld(new[] { explored });
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
        Func<StairRun, WorldGenerator, System.Collections.Generic.List<StructurePiece>> dresser = StairStructures.Dresser;
        try
        {
            foreach ((float back, float yaw, bool held) in new[] { (0.5f, 0f, true), (1.2f, 45f, true), (1.5f, 45f, false) })
            {
                StairStructures.Reset();
                var floor = new StructurePiece
                {
                    Kind = StructurePieceKind.StairFloor, Prefab = "stone_floor_2x2", Position = new Vector3(RoadConstants.HalfZoneSize - back, 40f, 0f), YawDegrees = yaw,
                };
                var run = new StairRun { From = new Vector2(20f, 0f), To = new Vector2(30f, 0f), Width = 2f };
                run.Pieces.Add(floor);
                StairStructures.Dresser = (_, _) => new System.Collections.Generic.List<StructurePiece> { floor };
                StairStructures.Add(run);
                StructurePlans.InvalidatePlans();
                Assert.Equal(held, StructurePlans.BuildablePlanFor(home) == null);
                var append = new RoadNetworkGenerator.ManualAppend();
                append.Runs.Add((run, new StairPlan(), StairKit.Stone));
                Assert.Equal(held, ManualRoads.ZonesReached(append, new System.Collections.Generic.List<RoadCrossing>()).Contains(explored));
            }
        }
        finally { StairStructures.Dresser = dresser; StairStructures.Reset(); StructurePlans.InvalidatePlans(); }
    }

    /// <summary>
    /// A staircase being planned is refused by the bodies the final check holds it back by: the same layout, refused by
    /// RoadStairRuns.OnKeptGround exactly where the run it becomes is withheld (StructurePlans.BuildablePlanFor), a 2 x 2
    /// stone floor 0.5 m inside the zone west of the explored one, one turned 45 degrees 1.2 m back, and as a control one
    /// 1.5 m back that is built. Planning read the pieces' origins, so it planned the first two, which the final check then
    /// withheld whole: a road planned with a staircase that is never built.
    /// </summary>
    [Fact]
    public void StairPlanningRefusesByTheBodiesTheFinalCheckHoldsBack()
    {
        _scope.WithWorld(new ScarpWorld()).WithZdos();
        Vector2s home = new(0, 0), explored = new(1, 0);
        NetworkCutoff.SnapshotWorld(new[] { explored });
        NetworkCutoff.BeginNewNetwork();
        Func<StairRun, WorldGenerator, System.Collections.Generic.List<StructurePiece>> dresser = StairStructures.Dresser;
        try
        {
            foreach ((float back, float yaw, bool held) in new[] { (0.5f, 0f, true), (1.2f, 45f, true), (1.5f, 45f, false) })
            {
                var plan = new StairPlan();
                plan.Pieces.Add(new StairPiece
                {
                    Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(RoadConstants.HalfZoneSize - back, 40f, 0f), YawDegrees = yaw,
                });
                StairStructures.Reset();
                StairRun run = StairRun.FromPlan(new Vector2(20f, 0f), new Vector2(30f, 0f), 2f, plan);
                StairStructures.Dresser = (r, _) => new System.Collections.Generic.List<StructurePiece>(r.Pieces);
                StairStructures.Add(run);
                StructurePlans.InvalidatePlans();
                bool withheld = StructurePlans.BuildablePlanFor(home) == null;
                Assert.Equal(held, withheld);
                Assert.Equal(withheld, RoadStairRuns.OnKeptGround(plan));
            }
        }
        finally { StairStructures.Dresser = dresser; StairStructures.Reset(); StructurePlans.InvalidatePlans(); }
    }

    /// <summary>A staircase's cut reaches the zone as far as the writer's ground does, its batter too (RoadTerrainModifier.StairCutReach,
    /// 3.75 m): a cut 2.5 m from an explored zone, outside the 1.5 m a cut's own width spans, still changes ground in it, so the
    /// staircase is not built. A cut beyond the batter does not.</summary>
    [Fact]
    public void ACutWhoseBatterReachesAnExploredZoneExcludesItsStaircase()
    {
        _scope.WithWorld(new ScarpWorld());
        NetworkCutoff.SnapshotWorld(new[] { ZoneSystem.GetZone(new Vector3(40f, 0f, 0f)) });
        NetworkCutoff.BeginNewNetwork();
        float line = RoadConstants.HalfZoneSize;
        StairPlan Cutting(float away) => new() { Cuts = { new Vector3(line - away, 30f, 0f) } };
        Assert.True(RoadStairRuns.OnKeptGround(Cutting(2.5f)));
        Assert.False(RoadStairRuns.OnKeptGround(Cutting(RoadTerrainModifier.StairCutReach + 0.25f)));
    }
}
