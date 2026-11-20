using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The server's bake of a saved zone brings the zone's world vegetation standing over its clearable rocks alive with them
/// (ServerBakePlanner.BringsVegetationAlive), so a rock removed whole takes what rests on it as generation's pass does
/// (RoadRockProbe.TakeWhatRestsOn); nothing else is brought.
/// </summary>
public class ServerBakeSavedVegetationTests
{
    private static readonly HashSet<string> Vegetation = new() { "Pinetree_01", "Bush01", "Rock_4" };
    private static readonly Dictionary<string, List<string>> NoneFractured = new();

    // A rock's body as its colliders have it: 10 m across, standing from 30 to 38 m.
    private static readonly IReadOnlyList<Bounds> Rock = new[] { new Bounds(new Vector3(100f, 34f, 200f), new Vector3(10f, 8f, 10f)) };

    private static bool Brought(string prefab, Vector3 at, bool player = false) =>
        ServerBakePlanner.BringsVegetationAlive(prefab, player, Vegetation, NoneFractured, at, Rock);

    [Fact]
    public void ASavedTreeOverARockIsBroughtAliveWithItAndNothingBesideIt()
    {
        // On its top, and under its overhang (judged there as generation judges it): brought.
        Assert.True(Brought("Pinetree_01", new Vector3(102f, 38f, 198f)));
        Assert.True(Brought("Bush01", new Vector3(96f, 29f, 204f)));
        // Beside the rock seen from above: never brought, so it stays exactly as saved.
        Assert.False(Brought("Pinetree_01", new Vector3(106f, 30f, 200f)));
        // A player's, an object not of the world's vegetation, and a clearable rock (brought as a rock): not brought as vegetation.
        Assert.False(Brought("Pinetree_01", new Vector3(100f, 38f, 200f), player: true));
        Assert.False(Brought("piece_chest", new Vector3(100f, 38f, 200f)));
        Assert.False(Brought("Rock_4", new Vector3(100f, 38f, 200f)));
        // No rock brought alive: nothing.
        Assert.False(ServerBakePlanner.BringsVegetationAlive("Pinetree_01", false, Vegetation, NoneFractured, new Vector3(100f, 38f, 200f),
            new List<Bounds>()));
    }
}
