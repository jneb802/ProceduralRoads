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
            .AndKeep(() => ProceduralRoadsPlugin.WorldDestinationBudget)
            .AndKeep(() => ProceduralRoadsPlugin.PathfindingMaxIterations)
            .AndKeep(() => ProceduralRoadsPlugin.ExcludedRoadBiomes)
            .AndKeep(() => ProceduralRoadsPlugin.AttemptRoadsToHildirQuestLocations)
            .AndKeep(() => RoadNetworkGenerator.RoadWidth)
            .AndKeep(() => RoadNetworkGenerator.IslandRoadPercentage)
            .AndKeep(() => RoadNetworkGenerator.MaxLocationsPerIsland)
            .AndKeep(() => RoadNetworkGenerator.WorldDestinationBudget)
            .AndKeep(() => RoadNetworkGenerator.GenerateOnLoad)
            .AndKeep(() => RoadNetworkGenerator.NetworkOptions)
            .AndKeep(() => RoadPathfinder.MaxIterations)
            .AndKeep(() => RoadParallel.Configured);
        var config = new ConfigFile("generation-configuration.cfg", false) { SaveOnConfigSet = false };
        ProceduralRoadsPlugin.RoadWidth = config.Bind("Roads", "RoadWidth", RoadConstants.DefaultRoadWidth);
        ProceduralRoadsPlugin.IslandRoadPercentage = config.Bind("Roads", "IslandRoadPercentage", RoadNetworkGenerator.DefaultIslandRoadPercentage);
        ProceduralRoadsPlugin.MaxLocationsPerIsland = config.Bind("Roads", "MaxLocationsPerIsland", RoadNetworkGenerator.DefaultMaxLocationsPerIsland);
        ProceduralRoadsPlugin.WorldDestinationBudget = config.Bind("Roads", "WorldDestinationBudget", RoadNetworkGenerator.DefaultWorldDestinationBudget);
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

    /// <summary>Roads.WorldDestinationBudget ships the generator's default, and its value is the budget the quota shares.</summary>
    [Fact]
    public void TheWorldDestinationBudgetSettingIsTheBudgetTheQuotaShares()
    {
        using var kept = StaticOverride.Keep(() => ProceduralRoadsPlugin.RoadWidth)
            .AndKeep(() => ProceduralRoadsPlugin.IslandRoadPercentage)
            .AndKeep(() => ProceduralRoadsPlugin.MaxLocationsPerIsland)
            .AndKeep(() => ProceduralRoadsPlugin.WorldDestinationBudget)
            .AndKeep(() => ProceduralRoadsPlugin.PathfindingMaxIterations)
            .AndKeep(() => ProceduralRoadsPlugin.ExcludedRoadBiomes)
            .AndKeep(() => ProceduralRoadsPlugin.AttemptRoadsToHildirQuestLocations)
            .AndKeep(() => ProceduralRoadsPlugin.LowMemoryGeneration)
            .AndKeep(() => ProceduralRoadsPlugin.ReplaceUnreachableLocations)
            .AndKeep(() => RoadNetworkGenerator.RoadWidth)
            .AndKeep(() => RoadNetworkGenerator.IslandRoadPercentage)
            .AndKeep(() => RoadNetworkGenerator.MaxLocationsPerIsland)
            .AndKeep(() => RoadNetworkGenerator.WorldDestinationBudget)
            .AndKeep(() => RoadNetworkGenerator.ReplaceUnreached)
            .AndKeep(() => RoadNetworkGenerator.GenerateOnLoad)
            .AndKeep(() => RoadNetworkGenerator.NetworkOptions)
            .AndKeep(() => RoadPathfinder.MaxIterations)
            .AndKeep(() => RoadParallel.Configured)
            .AndKeep(() => GenerationMemory.Low);
        var config = new ConfigFile("world-destination-budget.cfg", false) { SaveOnConfigSet = false };
        ProceduralRoadsPlugin.RoadWidth = config.Bind("Roads", "RoadWidth", RoadConstants.DefaultRoadWidth);
        ProceduralRoadsPlugin.IslandRoadPercentage = config.Bind("Roads", "IslandRoadPercentage", RoadNetworkGenerator.DefaultIslandRoadPercentage);
        ProceduralRoadsPlugin.MaxLocationsPerIsland = config.Bind("Roads", "MaxLocationsPerIsland", RoadNetworkGenerator.DefaultMaxLocationsPerIsland);
        ProceduralRoadsPlugin.WorldDestinationBudget = config.Bind("Roads", "WorldDestinationBudget", RoadNetworkGenerator.DefaultWorldDestinationBudget);
        ProceduralRoadsPlugin.PathfindingMaxIterations = config.Bind("Roads", "PathfindingMaxIterations", RoadConstants.PathfindingMaxIterations);
        ProceduralRoadsPlugin.ExcludedRoadBiomes = config.Bind("Locations", "ExcludedRoadBiomes", RoadNetworkOptions.DefaultExcludedBiomes);
        ProceduralRoadsPlugin.AttemptRoadsToHildirQuestLocations = config.Bind("Locations", "AttemptRoadsToHildirQuestLocations", RoadNetworkOptions.DefaultHildirQuestLocations);
        ProceduralRoadsPlugin.BindLowMemoryGeneration(config);
        ProceduralRoadsPlugin.BindReplaceUnreachableLocations(config);
        Assert.Equal(300, ProceduralRoadsPlugin.WorldDestinationBudget.Value);

        ProceduralRoadsPlugin.WorldDestinationBudget.Value = 600;
        typeof(ProceduralRoadsPlugin).GetMethod("ApplyConfiguration", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

        Assert.Equal(600, RoadNetworkGenerator.WorldDestinationBudget);
        // Twice the budget, twice the share: 600 x 10 of 100 km².
        Assert.Equal(60, RoadNetworkSelection.Quota(10_000_000, 100_000_000, RoadNetworkGenerator.WorldDestinationBudget,
            RoadNetworkGenerator.MaxLocationsPerIsland));
    }
}
