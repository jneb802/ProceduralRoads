using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class NetworkSelectionTests
{
    private static (string name, Vector3 position, float radius) Place(string name, float x, float z = 0)
        => (name, new Vector3(x, 0, z), 5);
    private static List<(string name, Vector3 position, float radius)> Choose(
        List<(string name, Vector3 position, float radius)> places, int quota, int seed = 1,
        float area = 0, RoadNetworkOptions? options = null) => RoadNetworkSelection.Destinations(
            places, quota, area, name => name.StartsWith("high") ? 80 : 30,
            name => name.StartsWith("required"), seed, options ?? new RoadNetworkOptions());


    /// <summary>The draw does not change when the places' positions are read to 0.1 m, as an offline world
    /// carries them: rounded to the metre, 4815.48 (the game's float) and 4815.5 (the dump) drew different
    /// places.</summary>
    [Fact]
    public void TheDrawIsTheSameForPositionsReadToATenthOfAMetre()
    {
        var places = Enumerable.Range(0, 40).Select(i => Place(i % 3 == 0 ? "high" : "low", i * 97f + 0.48f, (i * 53) % 700 + 0.46f)).ToList();
        var dumped = places.Select(p => (p.name, new Vector3(Mathf.Round(p.position.x * 10f) / 10f, 0, Mathf.Round(p.position.z * 10f) / 10f), p.radius)).ToList();
        foreach (int seed in new[] { 1, 301358575 })
            Assert.Equal(Choose(places, 9, seed).Select(p => Mathf.FloorToInt(p.position.x)),
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

    // Eight, plus four for every two square kilometres, capped.
    [Theory]
    [InlineData(0, 48, 8)]
    [InlineData(2_000_000, 48, 12)]
    [InlineData(4_000_000, 48, 16)]
    [InlineData(20_000_000, 48, 48)]
    [InlineData(100_000_000, 48, 48)]
    [InlineData(100_000_000, 12, 12)]
    [InlineData(0, 1, 2)]
    public void AreaBudgetGrowsWithIslandAndStopsAtTheCap(float area, int cap, int expected) =>
        Assert.Equal(expected, RoadNetworkSelection.Quota(area, cap));

    [Fact]
    public void RequiredSitesConsumeSlotsAndMayExceedBudget()
    {
        var places = new List<(string, Vector3, float)> {
            Place("required1",0), Place("required2",100), Place("low",200), Place("high",300) };
        Assert.Equal(2, Choose(places, 1).Count);
        Assert.All(Choose(places, 2), p => Assert.StartsWith("required", p.name));
        Assert.Equal(3, Choose(places, 3).Count);
    }

    [Fact]
    public void RequiredSitesUseTheirSubAreaTurnWithoutGrantingExtraOptionalSlots()
    {
        var places = new List<(string, Vector3, float)> {
            Place("required",10), Place("near",20), Place("far",210) };
        var selected = Choose(places, 2, area: 90000);
        Assert.Equal(new[] { "required", "far" }, selected.Select(p => p.name));
    }

    [Fact]
    public void SpreadingIncludesSparseDistantArea()
    {
        var places = Enumerable.Range(0,20).Select(i => Place("high"+i,i)).ToList();
        places.Add(Place("far",210));
        var selected = Choose(places, 2, area: 90000);
        Assert.Equal(2, selected.Count);
        Assert.Contains(selected, p => p.name == "far");
    }

    [Fact]
    public void DrawIsStableAcrossInputOrderAndDoesNotDuplicate()
    {
        var places = Enumerable.Range(0,30).Select(i => Place("site"+i,i*60)).ToList();
        var chosen = Choose(places, 12, seed: 123, area: 900000);
        places.Reverse();
        Assert.Equal(chosen, Choose(places, 12, seed: 123, area: 900000));
        Assert.Equal(12, chosen.Select(p => p.name).Distinct().Count());
    }

    [Fact]
    public void WeightedDrawCanChooseLowerPriorityAndChangesWithSeed()
    {
        var places = new List<(string, Vector3, float)> { Place("high",0), Place("low",1) };
        var results = Enumerable.Range(0,1000).Select(seed => Choose(places,1,seed)[0].name).ToHashSet();
        Assert.Contains("high", results);
        Assert.Contains("low", results);
    }
}
