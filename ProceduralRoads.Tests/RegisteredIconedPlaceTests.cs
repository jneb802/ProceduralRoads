using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A place another mod registers (RegisterLocation) or the config lists (CustomLocations) that shows a map icon, as a
/// village or a port does, is a destination: asking for roads to it is the choice. A unique place stays the player's.
/// </summary>
public class RegisteredIconedPlaceTests : System.IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope();
    private readonly List<string> _registered = new();
    private readonly List<string> _previousConfig = ProceduralRoadsPlugin.ConfigLocationNames;

    public void Dispose()
    {
        foreach (string name in _registered) RoadNetworkGenerator.UnregisterLocation(name);
        ProceduralRoadsPlugin.ConfigLocationNames = _previousConfig;
        Call("RegisterConfiguredLocations");
        _world.Dispose();
    }

    private static object? Call(string name, params object[] args) => typeof(RoadNetworkGenerator)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);

    private void Register(string name) { RoadNetworkGenerator.RegisterLocation(name); _registered.Add(name); }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ARegisteredIconedPlaceIsADestination(bool unique, bool iconPlaced, bool iconAlways)
    {
        Assert.True(PlacedSites.PlayerDecides("MP_Village_Test", unique, iconPlaced, iconAlways));
        Register("MP_Village_Test");
        Assert.False(PlacedSites.PlayerDecides("MP_Village_Test", unique, iconPlaced, iconAlways));
    }

    [Fact]
    public void AConfiguredIconedPlaceIsADestination()
    {
        ProceduralRoadsPlugin.ConfigLocationNames = new() { "MWL_Port_Test" };
        Call("RegisterConfiguredLocations");
        Assert.False(PlacedSites.PlayerDecides("MWL_Port_Test", false, true, false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AUniquePlaceStaysThePlayersRegisteredOrNot(bool iconPlaced, bool iconAlways)
    {
        Register("Vendor_BlackForest");
        Assert.True(PlacedSites.PlayerDecides("Vendor_BlackForest", true, iconPlaced, iconAlways));
    }

    [Fact]
    public void AnUnregisteredIconedPlaceStaysThePlayers() =>
        Assert.True(PlacedSites.PlayerDecides("SomeShrine", false, true, false));

    /// <summary>The gathering step, on the game's own location list: the registered village comes through, the
    /// unregistered shrine and the registered trader do not.</summary>
    [Fact]
    public void GatheringKeepsTheRegisteredVillageOnly()
    {
        _world.WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false });
        _world.WithZoneSystem();
        ZoneSystem zones = ZoneSystem.instance!;
        void Add(string name, float x, bool unique, bool iconPlaced) => TestLocations.Add(zones, new ZoneSystem.LocationInstance
        {
            m_location = new ZoneSystem.ZoneLocation
            {
                m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name),
                m_exteriorRadius = 20f, m_unique = unique, m_iconPlaced = iconPlaced,
            },
            m_position = new Vector3(x, 0f, 0f),
        });
        Add("StartTemple", 0f, false, false);
        Add("MP_Village_Test", 256f, false, true);
        Add("SomeShrine", 512f, false, true);
        Add("Vendor_BlackForest", 768f, true, true);
        Register("MP_Village_Test");
        Register("Vendor_BlackForest");

        // The gathering step as a load runs it (RoadNetworkGenerator.PrepareSteps), drained to its end.
        RoadNetworkGenerator.LocationData? data = null;
        System.Action<RoadNetworkGenerator.LocationData?> complete = value => data = value;
        MethodInfo steps = typeof(RoadNetworkGenerator).GetMethod("GatherLocationSteps", BindingFlags.Static | BindingFlags.NonPublic)!;
        using (RoadPreparation preparation = new(p => (IEnumerable<bool>)steps.Invoke(null, new object[] { ZoneSystem.instance!, p, complete })!))
            preparation.Drain();
        Assert.NotNull(data);
        List<(string name, Vector3 position, float radius)> all = data!.Value.AllLocations;
        Assert.Equal(new[] { "MP_Village_Test", "StartTemple" }, all.Select(l => l.name).OrderBy(n => n).ToArray());
    }
}
