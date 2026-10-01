using System.Collections.Generic;
using ProceduralRoads;
using UnityEngine;
using Xunit;
public class EmptySaveTests : System.IDisposable
{
    public void Dispose()
    {
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear();
        ZDOMan.instance = null;
    }
    static void Save() => RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());
    static bool Load() => RoadNetworkPersistence.TryLoadGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());
    static void Begin()
    {
        ZDOMan.instance = new ZDOMan();
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear();
        RoadNetworkPersistence.EnsureMetadataInstance();
    }
    static void AddRoad()
    {
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(0,0), new(8,0), new(16,0) }, new List<float> {40,40,40}, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.True(RoadSpatialGrid.GridCellsWithRoads > 0);
    }
    [Fact] public void NonemptySaveReloadControl()
    {
        Begin(); AddRoad(); Save(); int count = RoadSpatialGrid.GridCellsWithRoads;
        RoadSpatialGrid.Clear(); RoadNetworkPersistence.Reset();
        Assert.True(Load()); Assert.Equal(count, RoadSpatialGrid.GridCellsWithRoads);
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
