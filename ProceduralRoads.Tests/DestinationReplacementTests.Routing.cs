using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public partial class DestinationReplacementTests
{
    private sealed class RaisedLandingWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
    }

    private static void WithUnreachableLandings(Action<List<string>> run)
    {
        using var scope = new ValheimWorldScope();
        var world = new RaisedLandingWorld();
        scope.WithWorld(world);
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        LocationLevelling.Source = _ => new[] { new LevelOp(0f, 0f, 15f, 12f, false) };
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(-60f, 0f), 1000f) });
        var finder = typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!;
        finder.SetValue(null, new RoadPathfinder(world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f });
        var log = scope.CaptureLog();
        try { run(log); }
        finally
        {
            finder.SetValue(null, null);
            BepInEx.Logging.ManualLogSource.Captured = null;
            LocationLevelling.Source = null;
            RoadLocationProtection.Reset();
            RoadNetworkGenerator.LogDestinationAttempts = false;
            PlacedLocations.Reset();
            RoadNetworkGenerator.Reset();
        }
    }

    [Theory]
    [InlineData("Mistlands_DvergrBossEntrance1")]
    [InlineData(RoadCoastalLandings.Name)]
    public void ReplaceUnreachedIsOnByDefaultAndReplacesLostBossEntrancesAndCoastLandingsForAtMostTwoRounds(string name)
    {
        WithUnreachableLandings(_ =>
        {
            Assert.True(RoadNetworkGenerator.ReplaceUnreached);
            var destinations = new List<(string name, Vector3 position, float radius)> { Location(name, 0f, 0f) };
            var candidates = destinations.Concat(Enumerable.Range(1, 3).Select(i => Location("Ruin2", i * 70f, 0f))).ToList();
            var work = new RoadNetworkGenerator.GenerationWork(DateTime.Now, WorldGenerator.instance!);
            work.Islands.Add(new RoadNetworkGenerator.IslandWork(new Island { ApproxArea = Area }, destinations,
                new Vector3(-120f, 0f, 0f), 0f, candidates));
            work.Compute();
            Assert.Equal(3, destinations.Count);
            Assert.Equal(3, destinations.Select(d => d.position).Distinct().Count());
            Assert.Equal(2, RoadNetworkGenerator.m_replacementsTried);
            Assert.Equal(0, RoadNetworkGenerator.m_replacementsReached);
        });
    }

    /// <summary>The setting Roads.ReplaceUnreachableLocations (RoadNetworkGenerator.ReplaceUnreached), through the island worker
    /// as generation runs it: off, a destination no road can reach gets no replacement and its island keeps its first pick;
    /// on (the default), the two rounds replace it as before. The panel's totals are the chosen destination either way.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithReplacementsOffAnUnreachableDestinationGetsNoReplacementAndItsIslandKeepsItsFirstPicks(bool replace)
    {
        WithUnreachableLandings(log =>
        {
            using var kept = StaticOverride.Keep(() => RoadNetworkGenerator.ReplaceUnreached);
            Assert.True(RoadNetworkGenerator.DefaultReplaceUnreached);
            RoadNetworkGenerator.ReplaceUnreached = replace;
            var first = Location("Ruin2", 0f, 0f);
            var destinations = new List<(string name, Vector3 position, float radius)> { first };
            var candidates = destinations.Concat(Enumerable.Range(1, 3).Select(i => Location("Ruin2", i * 70f, 0f))).ToList();
            var work = new RoadNetworkGenerator.GenerationWork(DateTime.Now, WorldGenerator.instance!);
            work.Islands.Add(new RoadNetworkGenerator.IslandWork(new Island { ApproxArea = Area }, destinations,
                new Vector3(-120f, 0f, 0f), 0f, candidates));
            var activity = new RoadIslandActivity(1, 1);
            work.Activity = activity;
            work.Compute();
            if (replace)
            {
                Assert.Equal(3, destinations.Count);
                Assert.Equal(2, RoadNetworkGenerator.m_replacementsTried);
                Assert.Contains(log, line => line.StartsWith("Replacements, island "));
            }
            else
            {
                Assert.Equal(new[] { first }, destinations);
                Assert.Equal(0, RoadNetworkGenerator.m_replacementsTried);
                Assert.DoesNotContain(log, line => line.StartsWith("Replacements, island "));
            }
            Assert.Equal(0, RoadNetworkGenerator.m_replacementsReached);
            var row = activity.Snapshot()[0];
            Assert.Equal(1, row.Destinations);
            Assert.Contains("1 of 1 locations · 0 reached · 1 unreached", RoadGenerationPanel.RowText(row));
        });
    }

    /// <summary>A replacement round's locations are paired by the planner's one rule: a reached destination 1000 m off that has the
    /// replacement among its own eight nearest is paired with it, though the replacement's own eight nearest are the eight reached
    /// destinations 50 m round it. Before, the round kept only the replacement's own nearest, so the farther one never reached the
    /// planner.</summary>
    [Fact]
    public void AReplacementIsPairedWithAFartherReachedDestinationThatHasItAmongItsNearest()
    {
        var pick = Location("Ruin2", 0f, 0f);
        var kept = Enumerable.Range(0, 8)
            .Select(k => Location("near" + k, 50f * Mathf.Cos(k * Mathf.PI / 4f), 50f * Mathf.Sin(k * Mathf.PI / 4f))).ToList();
        // Its own nearest, by distance: three of the eight, then the replacement (1000 m), then five more.
        kept.Add(Location("far", 1000f, 0f));
        var start = new RoadConnectionPlanner.Location("Start", new Vector3(-3000f, 0f, 0f), 0f);

        var round = RoadNetworkGenerator.ReplacementRound(start, kept, new[] { pick });
        var priced = new System.Collections.Concurrent.ConcurrentBag<(string, string)>();
        RoadConnectionPlanner.Connect(round, (a, b) => { priced.Add((a.Name, b.Name)); return null; }, (_, _) => false,
            _ => true, _ => 0, (_, _) => { });

        Assert.Contains(priced, p => p == ("far", "Ruin2") || p == ("Ruin2", "far"));
        Assert.DoesNotContain(priced, p => p.Item1 == "far" && p.Item2 == "Start" || p.Item1 == "Start" && p.Item2 == "far");
    }

    [Fact]
    public void LogDestinationAttemptsStaysOffByDefaultAndLogsRealRoutingAttemptsOnlyWhenEnabled()
    {
        WithUnreachableLandings(log =>
        {
            Assert.False(RoadNetworkGenerator.LogDestinationAttempts);
            void Route() => RoadNetworkGenerator.GenerateRoutedRoads(new Vector3(-120f, 0f, 0f), 0f,
                new List<(string name, Vector3 position, float radius)> { Location("Ruin2", 0f, 0f) });
            bool AttemptLine(string line) => line.StartsWith("Destination, island ") || line.StartsWith("[E1] dest island ");
            Route();
            Assert.DoesNotContain(log, AttemptLine);
            RoadNetworkGenerator.LogDestinationAttempts = true;
            log.Clear();
            Route();
            string attempt = Assert.Single(log, AttemptLine);
            Assert.Contains("tries ", attempt);
            Assert.Contains("failed ", attempt);
            Assert.Contains("unreached", attempt);
            Assert.DoesNotContain("tries 0,", attempt);
        });
    }
}
