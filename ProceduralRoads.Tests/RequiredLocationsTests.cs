using System.Collections.Generic;
using System.Reflection;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A name in the RequiredLocations setting (a port) is a custom location whatever its map icon, a destination past the
/// island's budget, and the one reason to give its island roads. A unique place stays the player's.
/// </summary>
public class RequiredLocationsTests : System.IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope();
    private readonly List<string> _previous = ProceduralRoadsPlugin.ConfigRequiredNames;

    public void Dispose()
    {
        ProceduralRoadsPlugin.ConfigRequiredNames = _previous;
        Call("RegisterConfiguredLocations");
        _world.Dispose();
    }

    private static object? Call(string name, params object[] args) => typeof(RoadNetworkGenerator)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);

    private static void Require(params string[] names)
    {
        ProceduralRoadsPlugin.ConfigRequiredNames = new List<string>(names);
        Call("RegisterConfiguredLocations");
    }

    [Fact]
    public void ARequiredIconedPortIsARequiredDestination()
    {
        Assert.True(PlacedSites.PlayerDecides("MWL_Port1", false, true, false));
        Require("MWL_Port1");
        Assert.True(RoadNetworkGenerator.IsCustomLocation("MWL_Port1"));
        Assert.True(RoadNetworkGenerator.IsConfiguredRequired("MWL_Port1"));
        Assert.False(PlacedSites.PlayerDecides("MWL_Port1", false, true, false));
        Assert.True((bool)Call("IsRequiredLocation", "MWL_Port1")!);
        Assert.False(RoadNetworkGenerator.IsConfiguredRequired("MWL_Port2"));
    }

    [Fact]
    public void AUniquePlaceStaysThePlayersWhenRequired()
    {
        Require("Vendor_Test");
        Assert.True(PlacedSites.PlayerDecides("Vendor_Test", true, false, false));
    }

    [Fact]
    public void ABossIsRequiredButIsNoReasonToAddAnIsland()
    {
        Require();
        Assert.False(RoadNetworkGenerator.IsConfiguredRequired("Eikthyrnir"));
    }
}
