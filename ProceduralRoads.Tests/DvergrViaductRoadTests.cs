using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using static ProceduralRoads.Tests.DvergrViaductTests;

namespace ProceduralRoads.Tests;

/// <summary>
/// Bridges over stairs in the Mistlands: a road that would go down into a valley on one
/// staircase and out on another crosses at the high level on a Dvergr viaduct where one can be built, priced under the
/// staircase pair; a climb one way keeps its stairs, as does a valley out of the Mistlands or one a viaduct cannot
/// span (piers over the ceiling, another road under it). Old worlds: no viaduct piece goes into a zone explored before
/// the network.
/// </summary>
public sealed class DvergrViaductRoadTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>One way up: flat at 40 west of x = 0, 40 + 15 east of x = 12.5, a face of 1.2 between.</summary>
    private sealed class MistScarp : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(wx * 1.2f, 0f, 15f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 76;
    }

    public DvergrViaductRoadTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    private static RoadPathfinder Finder(WorldGenerator world, bool viaducts) =>
        new(world) { Stairs = true, Viaducts = viaducts, TerrainVarianceThreshold = 1000f, Meander = 0f };

    /// <summary>Over the ceiling the road goes down (on stairs, or road where it can) to where a viaduct's piers fit, and
    /// crosses there: the rim-level viaduct is never bought.</summary>
    [Fact]
    public void OverTheCeilingTheRoadGoesDownToAViaductWhosePiersFit()
    {
        var deep = new MistValley { Top = 80f, Depth = 34f };
        _scope.WithWorld(deep);
        RoadPathfinder finder = Finder(deep, viaducts: true);
        List<Vector2>? path = finder.FindPath(new Vector2(-100f, 0f), new Vector2(100f, 0f));
        Assert.NotNull(path);
        Assert.Contains(path!, p => deep.GetHeight(p.x, p.y) < deep.Top - 2f);
        foreach (var (from, to) in finder.LastViaductJumps)
        {
            RoadCrossing? lower = DvergrViaduct.ValleyCrossing(from, to, deep, out string refused);
            Assert.True(lower != null, refused);
            Assert.True(DvergrViaduct.TallestPier(lower!, deep) <= DvergrViaduct.PierCeiling);
            Assert.True(DvergrViaduct.DeckHeight(lower!, deep) < deep.Top - 2f, "the deck is below the rim");
        }
    }

    // ------------------------------------------------- bridges over stairs

    [Fact]
    public void TheSearchCrossesAMistlandsValleyOnAViaductNotTwoStaircases()
    {
        var world = new MistValley();
        _scope.WithWorld(world);
        // Without viaducts: a staircase down and one up.
        RoadPathfinder stairsOnly = Finder(world, viaducts: false);
        Assert.NotNull(stairsOnly.FindPath(new Vector2(-100f, 0f), new Vector2(100f, 0f)));
        Assert.NotEmpty(stairsOnly.LastStairSteps);
        Assert.Contains(stairsOnly.LastStairSteps, s => world.GetHeight(s.to.x, s.to.y) < world.GetHeight(s.from.x, s.from.y));
        Assert.Contains(stairsOnly.LastStairSteps, s => world.GetHeight(s.to.x, s.to.y) > world.GetHeight(s.from.x, s.from.y));
        // With them: the valley jumped at the rim's height, and no stairs.
        RoadPathfinder finder = Finder(world, viaducts: true);
        List<Vector2>? path = finder.FindPath(new Vector2(-100f, 0f), new Vector2(100f, 0f));
        Assert.NotNull(path);
        var (from, to) = Assert.Single(finder.LastViaductJumps);
        Assert.Empty(finder.LastStairSteps);
        Assert.True(Mathf.Abs(world.GetHeight(from.x, from.y) - world.GetHeight(to.x, to.y)) <= RoadConstants.MaxBridgeBankDelta);
        Assert.Contains(Enumerable.Range(1, path!.Count - 1), i => path[i - 1] == from && path[i] == to);
        Assert.True(finder.LastPathCost < stairsOnly.LastPathCost, $"viaduct route {finder.LastPathCost:F0} against stairs {stairsOnly.LastPathCost:F0}");
    }


    [Fact]
    public void AClimbOneWayStillGetsStairs()
    {
        var world = new MistScarp();
        _scope.WithWorld(world);
        SetPathfinder(Finder(world, viaducts: true));
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-80f, 0f, 0f), 0f, new Vector3(100f, 0f, 0f), 0f, 4f, "scarp"));
        Assert.DoesNotContain(RoadNetworkGenerator.GetRoadCrossings(), c => c.ValleyJump);
        Assert.NotEmpty(StairStructures.Runs);
    }

    [Fact]
    public void OutsideTheMistlandsAValleyKeepsItsStairs()
    {
        var world = new MistValley { Biome = Heightmap.Biome.Mountain };
        _scope.WithWorld(world);
        RoadPathfinder finder = Finder(world, viaducts: true);
        Assert.NotNull(finder.FindPath(new Vector2(-100f, 0f), new Vector2(100f, 0f)));
        Assert.Empty(finder.LastViaductJumps);
        Assert.NotEmpty(finder.LastStairSteps);
    }

    [Fact]
    public void AViaductIsNotLaidOverAnotherRoad()
    {
        var world = new MistValley();
        _scope.WithWorld(world);
        // A road along the valley's floor, north to south, from sea to sea.
        Assert.True(RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(0f, -198f), new(0f, 198f) }, 4f, world));
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadPathfinder finder = Finder(world, viaducts: true);
        finder.FindPath(new Vector2(-100f, 0f), new Vector2(100f, 0f));
        Assert.Empty(finder.LastViaductJumps);
    }

    [Fact]
    public void ALaterRoadDoesNotPassUnderAViaduct()
    {
        var world = new MistValley();
        _scope.WithWorld(world);
        SetPathfinder(Finder(world, viaducts: true));
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-100f, 0f, 0f), 0f, new Vector3(100f, 0f, 0f), 0f, 4f, "valley"));
        RoadCrossing viaduct = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
        // Every cell under the deck between the banks is closed to road; the banks' cells stay open.
        (int, int) Cell(Vector2 p) => (Mathf.RoundToInt(p.x / RoadPathfinder.CellSize), Mathf.RoundToInt(p.y / RoadPathfinder.CellSize));
        var banks = new[] { Cell(viaduct.FromBank), Cell(viaduct.ToBank) };
        for (float a = 0f; a <= viaduct.Width; a += 1f)
        {
            var (cx, cz) = Cell(viaduct.FromBank + viaduct.Direction * a);
            if (banks.Contains((cx, cz))) continue;
            Assert.True(DvergrViaduct.Standing(cx, cz), $"cell {cx},{cz} under the deck not closed");
        }
        Assert.All(banks, b => Assert.False(DvergrViaduct.Standing(b.Item1, b.Item2), "a bank cell stays open"));
        // A second road across the valley a little to the south is not built under it.
        RoadPathfinder later = Finder(world, viaducts: false);
        List<Vector2>? path = later.FindPath(new Vector2(-100f, viaduct.Center.y - 4f), new Vector2(100f, viaduct.Center.y - 4f));
        if (path != null)
            for (int i = 1; i < path.Count; i++)
            {
                Vector2 m = (path[i - 1] + path[i]) * 0.5f;
                Assert.False(DvergrViaduct.Standing(Mathf.RoundToInt(m.x / RoadPathfinder.CellSize), Mathf.RoundToInt(m.y / RoadPathfinder.CellSize))
                    && DvergrViaduct.Standing(Cell(path[i]).Item1, Cell(path[i]).Item2), $"the later road steps under the viaduct at {path[i]}");
            }
    }

    // -------------------------------------------------------- old worlds

    /// <summary>A viaduct that reaches a zone explored before the network is not built at all, as a bridge is
    /// (NetworkCutoff, through StructurePlacement's one spawn door): neither that zone nor the viaduct's other zone
    /// gets pieces, and the other zone is not marked spawned.</summary>
    [Fact]
    public void AViaductReachingAZoneExploredBeforeTheNetworkIsNotBuiltAtAll()
    {
        // The valley on the zone line at x = 32: the viaduct stands in zones (0, 0) and (1, 0).
        var world = new MistValley { Cx = 32f };
        using var scope = new ValheimWorldScope().WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear();
        NetworkCutoff.Forget();
        try
        {
            SetPathfinder(Finder(world, viaducts: true));
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-68f, 0f, 0f), 0f, new Vector3(132f, 0f, 0f), 0f, 4f, "valley"));
            RoadCrossing viaduct = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
            Assert.True(DvergrViaduct.Applies(viaduct, world));
            var zones = StructurePlans.PlannedZones;
            Assert.True(zones.Count >= 2, "the fixture must put the viaduct in more than one zone");
            foreach (Vector2s z in zones)
                foreach (StructurePiece p in StructurePlans.PlanFor(z)!)
                    if (ZNetScene.instance!.GetPrefab(p.Prefab) == null) ZNetScene.instance.AddPrefab(p.Prefab);
            Vector2s kept = zones[0], other = zones[1];
            NetworkCutoff.SnapshotWorld(new[] { kept });
            NetworkCutoff.UseNewNetwork();
            Assert.Equal(0, StructurePlacement.OnZoneSpawned(kept, ZoneSystem.SpawnMode.Full));
            Assert.Equal(0, StructurePlacement.SpawnGhostInZone(kept));
            Assert.False(StructurePlans.IsSpawned(kept));
            Assert.DoesNotContain(ZDOMan.instance!.Zdos, z => z.GetInt(StructurePlans.MarkerHash) == 1 && z.GetSector() == kept);
            Assert.Equal(0, StructurePlacement.OnZoneSpawned(other, ZoneSystem.SpawnMode.Full));
            Assert.False(StructurePlans.IsSpawned(other));
            Assert.DoesNotContain(ZDOMan.instance.Zdos, z => z.GetInt(StructurePlans.MarkerHash) == 1);
        }
        finally
        {
            NetworkCutoff.Forget();
            SetPathfinder(null);
            RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear();
        }
    }
}
