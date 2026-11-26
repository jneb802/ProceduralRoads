using System;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public class GenerationConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMemoryOrReplacementSettingChangeAppliesBeforeTheNextGeneration(bool duringGeneration)
    {
        using var scope = new ValheimWorldScope().WithWorld(new WorldGenerator()).WithZoneSystem();
        using var kept = StaticOverride.Keep(() => GenerationMemory.Low)
            .AndKeep(() => ProceduralRoadsPlugin.LowMemoryGeneration)
            .AndKeep(() => ProceduralRoadsPlugin.ReplaceUnreachableLocations)
            .AndKeep(() => RoadNetworkGenerator.ReplaceUnreached)
            .AndKeep(() => ProceduralRoadsPlugin.RoadWidth)
            .AndKeep(() => ProceduralRoadsPlugin.IslandRoadPercentage)
            .AndKeep(() => ProceduralRoadsPlugin.MaxLocationsPerIsland)
            .AndKeep(() => ProceduralRoadsPlugin.PathfindingMaxIterations)
            .AndKeep(() => ProceduralRoadsPlugin.ExcludedRoadBiomes)
            .AndKeep(() => ProceduralRoadsPlugin.AttemptRoadsToHildirQuestLocations)
            .AndKeep(() => RoadNetworkGenerator.RoadWidth)
            .AndKeep(() => RoadNetworkGenerator.IslandRoadPercentage)
            .AndKeep(() => RoadNetworkGenerator.MaxLocationsPerIsland)
            .AndKeep(() => RoadNetworkGenerator.GenerateOnLoad)
            .AndKeep(() => RoadNetworkGenerator.NetworkOptions)
            .AndKeep(() => RoadPathfinder.MaxIterations)
            .AndKeep(() => RoadParallel.Configured);
        var config = new ConfigFile("generation-configuration.cfg", false) { SaveOnConfigSet = false };
        ProceduralRoadsPlugin.RoadWidth = config.Bind("Roads", "RoadWidth", RoadConstants.DefaultRoadWidth);
        ProceduralRoadsPlugin.IslandRoadPercentage = config.Bind("Roads", "IslandRoadPercentage", RoadNetworkGenerator.DefaultIslandRoadPercentage);
        ProceduralRoadsPlugin.MaxLocationsPerIsland = config.Bind("Roads", "MaxLocationsPerIsland", RoadNetworkGenerator.DefaultMaxLocationsPerIsland);
        ProceduralRoadsPlugin.PathfindingMaxIterations = config.Bind("Roads", "PathfindingMaxIterations", RoadConstants.PathfindingMaxIterations);
        ProceduralRoadsPlugin.ExcludedRoadBiomes = config.Bind("Locations", "ExcludedRoadBiomes", RoadNetworkOptions.DefaultExcludedBiomes);
        ProceduralRoadsPlugin.AttemptRoadsToHildirQuestLocations = config.Bind("Locations", "AttemptRoadsToHildirQuestLocations", RoadNetworkOptions.DefaultHildirQuestLocations);
        ProceduralRoadsPlugin.BindLowMemoryGeneration(config);
        ProceduralRoadsPlugin.BindReplaceUnreachableLocations(config);
        Assert.Equal(RoadNetworkGenerator.DefaultReplaceUnreached, ProceduralRoadsPlugin.ReplaceUnreachableLocations.Value);
        GenerationMemory.Low = false;
        RoadNetworkGenerator.ReplaceUnreached = true;
        FieldInfo active = typeof(RoadLifecycleManager).GetField("m_generation", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Null(active.GetValue(null));
        object run = Activator.CreateInstance(active.FieldType, BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object[] { WorldGenerator.instance!, ZoneSystem.instance!, new RoadLifecycleManager.GenerationRequest() }, null)!;
        try
        {
            if (duringGeneration) active.SetValue(null, run);
            ProceduralRoadsPlugin.LowMemoryGeneration.Value = true;
            ProceduralRoadsPlugin.ReplaceUnreachableLocations.Value = false;
            if (duringGeneration)
            {
                // A generation reads one value of each: the change waits for it to end.
                Assert.False(GenerationMemory.Low);
                Assert.True(RoadNetworkGenerator.ReplaceUnreached);
                ProceduralRoadsPlugin.ApplyPendingConfiguration();
                Assert.False(GenerationMemory.Low);
                Assert.True(RoadNetworkGenerator.ReplaceUnreached);
                active.SetValue(null, null);
            }
            ProceduralRoadsPlugin.ApplyPendingConfiguration();
            Assert.True(GenerationMemory.Low);
            Assert.False(RoadNetworkGenerator.ReplaceUnreached);
            Assert.False(GenerationMemory.SharesTerrain);
            Assert.False(GenerationMemory.LaysAhead);
            // Changing back follows the same event path, without reloading the config file.
            ProceduralRoadsPlugin.LowMemoryGeneration.Value = false;
            Assert.False(GenerationMemory.Low);
            ProceduralRoadsPlugin.ReplaceUnreachableLocations.Value = true;
            Assert.True(RoadNetworkGenerator.ReplaceUnreached);
        }
        finally
        {
            active.SetValue(null, null);
            ((System.Threading.CancellationTokenSource)active.FieldType.GetField("Cancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(run)!).Dispose();
            ProceduralRoadsPlugin.ApplyPendingConfiguration();
        }
    }

    /// <summary>The locations the CustomLocations config names are known from the configuration's apply (at load, on a reload),
    /// not only once a generation starts: on a loaded world no generation runs, and a manual road there must treat them as one
    /// would.</summary>
    [Fact]
    public void ConfiguredLocationsAreKnownBeforeAnyGeneration()
    {
        using var scope = new ValheimWorldScope().WithWorld(new WorldGenerator()).WithZoneSystem();
        using var kept = StaticOverride.Keep(() => GenerationMemory.Low)
            .AndKeep(() => ProceduralRoadsPlugin.LowMemoryGeneration)
            .AndKeep(() => ProceduralRoadsPlugin.ReplaceUnreachableLocations)
            .AndKeep(() => RoadNetworkGenerator.ReplaceUnreached)
            .AndKeep(() => ProceduralRoadsPlugin.RoadWidth)
            .AndKeep(() => ProceduralRoadsPlugin.IslandRoadPercentage)
            .AndKeep(() => ProceduralRoadsPlugin.MaxLocationsPerIsland)
            .AndKeep(() => ProceduralRoadsPlugin.PathfindingMaxIterations)
            .AndKeep(() => ProceduralRoadsPlugin.ExcludedRoadBiomes)
            .AndKeep(() => ProceduralRoadsPlugin.AttemptRoadsToHildirQuestLocations)
            .AndKeep(() => ProceduralRoadsPlugin.ConfigLocationNames)
            .AndKeep(() => RoadNetworkGenerator.RoadWidth)
            .AndKeep(() => RoadNetworkGenerator.IslandRoadPercentage)
            .AndKeep(() => RoadNetworkGenerator.MaxLocationsPerIsland)
            .AndKeep(() => RoadNetworkGenerator.GenerateOnLoad)
            .AndKeep(() => RoadNetworkGenerator.NetworkOptions)
            .AndKeep(() => RoadPathfinder.MaxIterations)
            .AndKeep(() => RoadParallel.Configured);
        var config = new ConfigFile("configured-locations.cfg", false) { SaveOnConfigSet = false };
        ProceduralRoadsPlugin.RoadWidth = config.Bind("Roads", "RoadWidth", RoadConstants.DefaultRoadWidth);
        ProceduralRoadsPlugin.IslandRoadPercentage = config.Bind("Roads", "IslandRoadPercentage", RoadNetworkGenerator.DefaultIslandRoadPercentage);
        ProceduralRoadsPlugin.MaxLocationsPerIsland = config.Bind("Roads", "MaxLocationsPerIsland", RoadNetworkGenerator.DefaultMaxLocationsPerIsland);
        ProceduralRoadsPlugin.PathfindingMaxIterations = config.Bind("Roads", "PathfindingMaxIterations", RoadConstants.PathfindingMaxIterations);
        ProceduralRoadsPlugin.ExcludedRoadBiomes = config.Bind("Locations", "ExcludedRoadBiomes", RoadNetworkOptions.DefaultExcludedBiomes);
        ProceduralRoadsPlugin.AttemptRoadsToHildirQuestLocations = config.Bind("Locations", "AttemptRoadsToHildirQuestLocations", RoadNetworkOptions.DefaultHildirQuestLocations);
        ProceduralRoadsPlugin.BindLowMemoryGeneration(config);
        ProceduralRoadsPlugin.BindReplaceUnreachableLocations(config);
        try
        {
            ProceduralRoadsPlugin.ConfigLocationNames = new() { "ConfiguredCamp" };
            Assert.DoesNotContain("ConfiguredCamp", RoadNetworkGenerator.GetRegisteredLocations());
            // The configuration applied (here by a setting's change, as the load and a config reload apply it): no generation ran.
            ProceduralRoadsPlugin.LowMemoryGeneration.Value = !ProceduralRoadsPlugin.LowMemoryGeneration.Value;
            Assert.False(RoadLifecycleManager.IsGenerating);
            Assert.Contains("ConfiguredCamp", RoadNetworkGenerator.GetRegisteredLocations());
        }
        finally
        {
            ProceduralRoadsPlugin.ConfigLocationNames = new();
            RoadNetworkGenerator.RegisterConfiguredLocations();
        }
    }
}
