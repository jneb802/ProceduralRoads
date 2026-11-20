using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class NetworkSelectionTests
{
    private static (string name, Vector3 position, float radius) Location(string name, float x, float z = 0)
        => (name, new Vector3(x, 0, z), 5);
    private static List<(string name, Vector3 position, float radius)> Choose(
        List<(string name, Vector3 position, float radius)> locations, int quota, int seed = 1,
        float area = 0, RoadNetworkOptions? options = null) => RoadNetworkSelection.Destinations(
            locations, quota, area, name => name.StartsWith("high") ? 80 : 30,
            name => name.StartsWith("required"), seed, options ?? new RoadNetworkOptions());


    /// <summary>The draw does not change when the locations' positions are read to 0.1 m, as an offline world
    /// carries them: rounded to the metre, 4815.48 (the game's float) and 4815.5 (the dump) drew different
    /// places.</summary>
    [Fact]
    public void TheDrawIsTheSameForPositionsReadToATenthOfAMetre()
    {
        var locations = Enumerable.Range(0, 40).Select(i => Location(i % 3 == 0 ? "high" : "low", i * 97f + 0.48f, (i * 53) % 700 + 0.46f)).ToList();
        var dumped = locations.Select(p => (p.name, new Vector3(Mathf.Round(p.position.x * 10f) / 10f, 0, Mathf.Round(p.position.z * 10f) / 10f), p.radius)).ToList();
        foreach (int seed in new[] { 1, 301358575 })
            Assert.Equal(Choose(locations, 9, seed).Select(p => Mathf.FloorToInt(p.position.x)),
                Choose(dumped, 9, seed).Select(p => Mathf.FloorToInt(p.position.x)));
    }

    [Fact]
    public void ExclusionsApplyToBiomeFlags()
    {
        var options = new RoadNetworkOptions();
        Assert.False(options.Allows(Heightmap.Biome.AshLands));
        Assert.False(options.Allows(Heightmap.Biome.DeepNorth | Heightmap.Biome.Meadows));
        Assert.True(options.Allows(Heightmap.Biome.Meadows));
        options.ExcludedBiomes = 0;
        Assert.True(options.Allows(Heightmap.Biome.AshLands));
    }

    [Fact]
    public void IslandPercentageIsRespectedAndZeroRemainsOff()
    {
        var islands = new[] {
            new Island { Id=1, ApproxArea=1000 }, new Island { Id=2, ApproxArea=100 },
            new Island { Id=3, ApproxArea=10000 } };
        Assert.Equal(new[] { 1 }, RoadNetworkSelection.Islands(islands,
            i => i.Id == 1 ? 10 : 1, 33).Select(i => i.Id));
        Assert.Empty(RoadNetworkSelection.Islands(islands, _ => 10, 0));
        // Equal content: the larger island.
        Assert.Equal(3, Assert.Single(RoadNetworkSelection.Islands(islands, _ => 1, 33)).Id);
    }

    // The world budget shared by land: an island's part of the selected land, at least eight, capped.
    [Theory]
    [InlineData(36_600_000, 84_900_000, 300, 512, 129)]   // a continent's share
    [InlineData(5_500_000, 60_900_000, 300, 512, 27)]     // a vanilla-size world's largest island
    [InlineData(1_000_000, 60_900_000, 300, 512, 8)]      // under the minimum
    [InlineData(36_600_000, 84_900_000, 300, 48, 48)]     // the cap
    [InlineData(10_000_000, 10_000_000, 300, 512, 300)]   // alone, the whole budget
    [InlineData(10_000_000, 10_000_000, 2000, 512, 512)]  // the whole budget, capped
    public void QuotaSharesTheWorldBudgetByIslandArea(float area, double sharedArea, int budget, int cap, int expected) =>
        Assert.Equal(expected, RoadNetworkSelection.Quota(area, sharedArea, budget, cap));

    [Fact]
    public void AnIslandsCandidatesAreTheLastCeilingOnItsQuota()
    {
        var locations = new List<(string, Vector3, float)> { Location("a",0), Location("b",100), Location("c",200) };
        int quota = RoadNetworkSelection.Quota(10_000_000, 10_000_000, 300, 512);
        Assert.Equal(300, quota);
        Assert.Equal(3, Choose(locations, quota, area: 10_000_000).Count);
    }

    [Fact]
    public void TheSharesSumToTheBudgetWhenNoMinimumOrCapApplies()
    {
        float[] areas = { 7_300_000, 13_100_000, 21_700_000, 57_900_000 };
        double shared = areas.Sum(a => (double)a);
        int total = areas.Sum(a => RoadNetworkSelection.Quota(a, shared, 300, 512));
        // Each share is floored: at most one short per island.
        Assert.InRange(total, 300 - areas.Length, 300);
    }

    // A tiny island still earns the minimum; only a cap below it lowers it, never under two.
    [Theory]
    [InlineData(0, 60_900_000, 512, 8)]
    [InlineData(10_000, 60_900_000, 512, 8)]
    [InlineData(1_000_000, 0, 512, 8)]
    [InlineData(0, 60_900_000, 5, 5)]
    [InlineData(0, 60_900_000, 1, 2)]
    public void QuotaKeepsTheMinimumForTinyIslands(float area, double sharedArea, int cap, int expected) =>
        Assert.Equal(expected, RoadNetworkSelection.Quota(area, sharedArea, 300, cap));

    // One island generated alone shares the budget over the same land as in the whole world.
    [Fact]
    public void AnIslandAloneSharesTheBudgetOverTheWorldSelection()
    {
        var a = new Island { Id = 1, ApproxArea = 4_000_000 };
        var b = new Island { Id = 2, ApproxArea = 6_000_000 };
        var c = new Island { Id = 3, ApproxArea = 1_000_000 };
        var world = new List<Island> { a, b };
        Assert.Equal(10_000_000, RoadNetworkSelection.SharedArea(world, world));
        Assert.Equal(10_000_000, RoadNetworkSelection.SharedArea(world, new[] { b }));
        // An island the world selection left out is counted with it.
        Assert.Equal(11_000_000, RoadNetworkSelection.SharedArea(world, new[] { c }));
    }

    [Fact]
    public void RequiredLocationsConsumeSlotsAndMayExceedBudget()
    {
        var locations = new List<(string, Vector3, float)> {
            Location("required1",0), Location("required2",100), Location("low",200), Location("high",300) };
        Assert.Equal(2, Choose(locations, 1).Count);
        Assert.All(Choose(locations, 2), p => Assert.StartsWith("required", p.name));
        Assert.Equal(3, Choose(locations, 3).Count);
    }

    [Fact]
    public void RequiredLocationsUseTheirSubAreaTurnWithoutGrantingExtraOptionalSlots()
    {
        var locations = new List<(string, Vector3, float)> {
            Location("required",10), Location("near",20), Location("far",210) };
        var selected = Choose(locations, 2, area: 90000);
        Assert.Equal(new[] { "required", "far" }, selected.Select(p => p.name));
    }

    [Fact]
    public void SpreadingIncludesSparseDistantArea()
    {
        var locations = Enumerable.Range(0,20).Select(i => Location("high"+i,i)).ToList();
        locations.Add(Location("far",210));
        var selected = Choose(locations, 2, area: 90000);
        Assert.Equal(2, selected.Count);
        Assert.Contains(selected, p => p.name == "far");
    }

    [Fact]
    public void DrawIsStableAcrossInputOrderAndDoesNotDuplicate()
    {
        var locations = Enumerable.Range(0,30).Select(i => Location("location"+i,i*60)).ToList();
        var chosen = Choose(locations, 12, seed: 123, area: 900000);
        locations.Reverse();
        Assert.Equal(chosen, Choose(locations, 12, seed: 123, area: 900000));
        Assert.Equal(12, chosen.Select(p => p.name).Distinct().Count());
    }

    [Fact]
    public void WeightedDrawCanChooseLowerPriorityAndChangesWithSeed()
    {
        var locations = new List<(string, Vector3, float)> { Location("high",0), Location("low",1) };
        var results = Enumerable.Range(0,1000).Select(seed => Choose(locations,1,seed)[0].name).ToHashSet();
        Assert.Contains("high", results);
        Assert.Contains("low", results);
    }
}
