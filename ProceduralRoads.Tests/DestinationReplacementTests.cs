using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>Replacing a destination that ended unreached (RoadNetworkGenerator.ReplaceUnreached, on by default).</summary>
public partial class DestinationReplacementTests
{
    private static (string name, Vector3 position, float radius) Location(string name, float x, float z)
        => (name, new Vector3(x, 0, z), 5);

    private const float Side = 300f;
    private const float Area = 9 * Side * Side;

    /// <summary>A 3 x 3 island of sub-areas (RoadNetworkSelection.TargetSubAreas on <see cref="Area"/>), four locations in each.</summary>
    private static List<(string name, Vector3 position, float radius)> Grid()
    {
        var locations = new List<(string name, Vector3 position, float radius)>();
        for (int cx = 0; cx < 3; cx++)
            for (int cz = 0; cz < 3; cz++)
                for (int k = 0; k < 4; k++)
                    locations.Add(Location(k % 2 == 0 ? "high" : "low", cx * Side + 40 + k * 60, cz * Side + 50 + k * 45));
        return locations;
    }

    private static int Priority(string name) => name.StartsWith("high") ? 80 : 30;
    private static (int, int) Cell((string name, Vector3 position, float radius) p)
        => (Mathf.FloorToInt(p.position.x / Side), Mathf.FloorToInt(p.position.z / Side));

    private static List<(string name, Vector3 position, float radius)> Choose(List<(string name, Vector3 position, float radius)> locations, int quota)
        => RoadNetworkSelection.Destinations(locations, quota, Area, Priority, _ => false, 7, new RoadNetworkOptions());

    private static List<(string name, Vector3 position, float radius)> Replace(List<(string name, Vector3 position, float radius)> locations,
        IReadOnlyList<(string name, Vector3 position, float radius)> kept, Func<(string name, Vector3 position, float radius), bool> excluded, int count)
        => RoadNetworkSelection.Replacements(locations, kept, excluded, count, Area, Priority, 7, new RoadNetworkOptions());

    [Fact]
    public void WithNothingKeptTheReplacementsAreTheSelectionsOwnDraw()
    {
        var locations = Grid();
        Assert.Equal(Choose(locations, 9), Replace(locations, new List<(string, Vector3, float)>(), _ => false, 9));
    }

    [Fact]
    public void AReplacementGoesToTheSubAreaThatLostItsDestination()
    {
        var locations = Grid();
        var chosen = Choose(locations, 9);
        // One a sub-area: the density rule.
        Assert.Equal(9, chosen.Select(Cell).Distinct().Count());
        foreach (var lost in chosen)
        {
            var kept = chosen.Where(c => !c.Equals(lost)).ToList();
            var replacement = Assert.Single(Replace(locations, kept, c => chosen.Contains(c), 1));
            Assert.Equal(Cell(lost), Cell(replacement));
            Assert.DoesNotContain(replacement, chosen);
        }
    }

    [Fact]
    public void AReplacementIsNeverOneAlreadyTried()
    {
        var locations = Grid();
        var chosen = Choose(locations, 9);
        var lost = chosen[0];
        var kept = chosen.Skip(1).ToList();
        // Every other location in the lost one's sub-area tried already: the replacement comes from another sub-area.
        var tried = new HashSet<(string, Vector3, float)>(chosen.Concat(locations.Where(p => Cell(p) == Cell(lost))));
        var replacement = Assert.Single(Replace(locations, kept, tried.Contains, 1));
        Assert.DoesNotContain(replacement, tried);
        Assert.NotEqual(Cell(lost), Cell(replacement));
    }

    [Fact]
    public void ReplacementsAreBoundedByTheCountAskedFor()
    {
        var locations = Grid();
        var chosen = Choose(locations, 9);
        var kept = chosen.Skip(3).ToList();
        Assert.Equal(3, Replace(locations, kept, chosen.Contains, 3).Count);
        Assert.Empty(Replace(locations, kept, chosen.Contains, 0));
        // Fewer left to draw from than asked: only those.
        Assert.Equal(locations.Count - chosen.Count, Replace(locations, kept, chosen.Contains, 100).Count);
    }

    [Fact]
    public void AnIslandTakesOneRoundForItsUnreachedAndOneMoreForUnreachedReplacements()
    {
        var pool = Enumerable.Range(0, 20).Select(i => Location("p" + i, 1000 + i * 70, 0)).ToList();
        var locations = new List<(string name, Vector3 position, float radius)> { Location("a", 0, 0), Location("b", 70, 0), Location("c", 140, 0) };
        var reached = new HashSet<string> { "a" };
        var asked = new List<int>();
        var seen = new List<HashSet<(float, float)>>();
        var rounds = new List<int>();
        RoadNetworkGenerator.Replacer replace = (kept, tried, count) =>
        {
            asked.Add(count);
            seen.Add(new HashSet<(float, float)>(tried));
            // Offers one already chosen first, and more than asked: neither is taken.
            return new[] { locations[1] }.Concat(pool.Where(p => !tried.Contains(RoadNetworkGenerator.LocationKey(new Vector2(p.position.x, p.position.z))))).Take(count + 2).ToList();
        };
        var (tried, made) = RoadNetworkGenerator.ReplaceUnreachedRounds(locations, d => reached.Contains(d.name), replace,
            (picks, kept, number) => { rounds.Add(number); Assert.Equal(new[] { "a" }, kept.Select(k => k.name)); });
        // Two unreached: two replacements; both unreached: two more, then no third round.
        Assert.Equal(new[] { 2, 2 }, asked);
        Assert.Equal(new[] { 1, 2 }, rounds);
        Assert.Equal(new[] { 2, 2 }, tried);
        Assert.Equal(new[] { 0, 0 }, made);
        Assert.Equal(3 + 4, locations.Count);
        Assert.Equal(locations.Count, locations.Select(l => l.position).Distinct().Count());
        // The second round knows the first round's replacements as tried.
        Assert.Equal(5, seen[1].Count);
    }

    [Fact]
    public void ReachedReplacementsNeedNoSecondRound()
    {
        var pool = Enumerable.Range(0, 5).Select(i => Location("p" + i, 1000 + i * 70, 0)).ToList();
        var locations = new List<(string name, Vector3 position, float radius)> { Location("a", 0, 0), Location("b", 70, 0) };
        var reached = new HashSet<string> { "a" };
        int calls = 0;
        var (tried, made) = RoadNetworkGenerator.ReplaceUnreachedRounds(locations, d => reached.Contains(d.name),
            (kept, t, count) => { calls++; return pool.Take(count).ToList(); },
            (picks, kept, number) => { foreach (var p in picks) reached.Add(p.name); });
        Assert.Equal(1, calls);
        Assert.Equal(new[] { 1, 0 }, tried);
        Assert.Equal(new[] { 1, 0 }, made);
    }

    private static RoadConnectionPlanner.Location P(string name, float x) => new(name, new Vector3(x, 0, 0), 0);

    [Fact]
    public void DropAfterFailuresIsRemovedAndEveryUnpricedEdgeIsTried()
    {
        // "boss" has no priced edge: its join fails, then every unpriced edge is built for it, nearest first ("a", then "start").
        var locations = new[] { P("start", 0), P("a", 50), P("boss", 100) };
        float? Price(RoadConnectionPlanner.Location x, RoadConnectionPlanner.Location y) => x.Name == "start" && y.Name == "a" ? 1 : null;
        var built = new List<string>(); var attempts = new List<(int, bool)>();
        RoadConnectionPlanner.Connect(locations, Price, (x, y) => { built.Add(x.Name + "-" + y.Name); return y.Name == "a"; }, _ => false, _ => 1,
            (_, _) => { }, attempted: (i, ok) => attempts.Add((i, ok)));
        Assert.Equal(new[] { "start-a", "a-boss", "start-boss" }, built);
        Assert.Equal(3, attempts.Count(a => a == (2, false)));
        Assert.Null(typeof(RoadNetworkGenerator).GetField("DropAfterFailures", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
        Assert.DoesNotContain(typeof(RoadConnectionPlanner).GetMethod("Connect")!.GetParameters(), p => p.Name == "dropAfter" || p.Name == "dropped");
    }
}
