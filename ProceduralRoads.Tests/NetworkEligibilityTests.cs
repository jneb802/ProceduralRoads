using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public class NetworkEligibilityTests
{
    private static object Call(string name, params object[] args) => typeof(RoadNetworkGenerator)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;

    [Fact]
    public void RemovingConfiguredNameDoesNotLeaveItRegisteredOrRemoveApiNames()
    {
        var previous = ProceduralRoadsPlugin.ConfigLocationNames;
        try
        {
            RoadNetworkGenerator.RegisterLocation("ApiFixture");
            ProceduralRoadsPlugin.ConfigLocationNames = new() { "ConfigFixture" };
            Call("RegisterConfiguredLocations");
            Assert.True((bool)Call("IsRoadLocation", "ConfigFixture"));
            ProceduralRoadsPlugin.ConfigLocationNames = new();
            Call("RegisterConfiguredLocations");
            Assert.False((bool)Call("IsRoadLocation", "ConfigFixture"));
            Assert.True((bool)Call("IsRoadLocation", "ApiFixture"));
        }
        finally
        {
            RoadNetworkGenerator.UnregisterLocation("ApiFixture");
            ProceduralRoadsPlugin.ConfigLocationNames = previous;
            Call("RegisterConfiguredLocations");
        }
    }

    private sealed class AshWorld : WorldGenerator
    {
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.AshLands;
    }

    [Fact]
    public void ExcludedBiomeFiltersApiAndRequiredSitesBeforeSelection()
    {
        using var scope = new ValheimWorldScope();
        using var overrides = StaticOverride.Keep(() => RoadNetworkGenerator.NetworkOptions);
        try
        {
            scope.WithWorld(new AshWorld());
            RoadNetworkGenerator.NetworkOptions = new RoadNetworkOptions();
            RoadNetworkGenerator.RegisterLocation("ApiAshFixture");
            // The API is how a content mod opts in; there is no prefix rule.
            RoadNetworkGenerator.RegisterLocation("MWL_Test");
            var island = new Island { Min=new Vector2(-10,-10), Max=new Vector2(10,10),
                CellSize=128, Cells=new() { new Vector2Int(0,0) } };
            var places = new List<(string,Vector3,float)> {
                ("ApiAshFixture",Vector3.zero,1), ("MWL_Test",Vector3.zero,1), ("GoblinKing",Vector3.zero,1) };
            var found = (List<(string,Vector3,float)>)Call("GetLocationsOnIsland", island, places);
            Assert.Empty(found);
            RoadNetworkGenerator.NetworkOptions.ExcludedBiomes = 0;
            Assert.Equal(3, ((List<(string,Vector3,float)>)Call("GetLocationsOnIsland",island,places)).Count);
        }
        finally { RoadNetworkGenerator.UnregisterLocation("ApiAshFixture"); RoadNetworkGenerator.UnregisterLocation("MWL_Test"); }
    }
}
