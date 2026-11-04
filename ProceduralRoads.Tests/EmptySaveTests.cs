using System.Collections.Generic;
using ProceduralRoads;
using ProceduralRoads.Tests;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
public class EmptySaveTests : System.IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope();
    public void Dispose()
    {
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear();
        _world.Dispose();
    }
    static void Save() => RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>());
    static bool Load() => RoadNetworkPersistence.TryLoadGlobalRoadData(new List<(Vector2, string)>());
    void Begin()
    {
        _world.WithZdos();
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear();
        RoadNetworkPersistence.EnsureMetadataInstance();
    }
    static void AddRoad()
    {
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(0,0), new(8,0), new(16,0) }, 4f, new SyntheticWorld());
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.True(RoadSpatialGrid.GridCellsWithRoads > 0);
    }
    [Fact] public void EmptyReplacementMustNotResurrectPreviousRoads()
    {
        Begin(); AddRoad(); Save();
        RoadSpatialGrid.Clear(); RoadSpatialGrid.FinalizeRoadNetwork(); Save();
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear();
        Load();
        Assert.Equal(0, RoadSpatialGrid.GridCellsWithRoads);
    }
    [Fact] public void FreshEmptyNetworkMustBeRecognizedAsSaved()
    {
        Begin(); Save(); RoadNetworkPersistence.Reset();
        Assert.True(Load(), "A deliberately empty saved network must load rather than be treated as absent.");
    }
}
