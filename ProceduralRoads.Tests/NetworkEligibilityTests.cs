using System.Linq;
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
    public void ExcludedBiomeFiltersApiAndRequiredLocationsBeforeSelection()
    {
        using var scope = new ValheimWorldScope();
        using var overrides = StaticOverride.Keep(() => RoadNetworkGenerator.NetworkOptions);
        try
        {
            var world = new AshWorld();
            scope.WithWorld(world);
            RoadNetworkGenerator.NetworkOptions = new RoadNetworkOptions();
            RoadNetworkGenerator.RegisterLocation("ApiAshFixture");
            // The API is how a content mod opts in; there is no prefix rule.
            RoadNetworkGenerator.RegisterLocation("MWL_Test");
            var island = new Island { Min=new Vector2(-10,-10), Max=new Vector2(10,10),
                CellSize=128, Cells=new() { new Vector2Int(0,0) } };
            var locations = new List<(string,Vector3,float)> {
                ("ApiAshFixture",Vector3.zero,1), ("MWL_Test",Vector3.zero,1), ("GoblinKing",Vector3.zero,1) };
            int Found() => locations.Count(p => (bool)Call("IsIslandDestination", island, p, world, RoadNetworkGenerator.NetworkOptions));
            Assert.Equal(0, Found());
            RoadNetworkGenerator.NetworkOptions.ExcludedBiomes = 0;
            Assert.Equal(3, Found());
        }
        finally { RoadNetworkGenerator.UnregisterLocation("ApiAshFixture"); RoadNetworkGenerator.UnregisterLocation("MWL_Test"); }
    }
}
