# ProceduralRoads

A Valheim mod that generates procedural roads connecting locations across your world.

## Features

- Automatically generates roads from spawn to nearby points of interest
- Terrain-aware pathfinding that follows natural contours
- Configurable road width, length, and count

## Installation

1. Install [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)
2. Install [Jotunn](https://valheim.thunderstore.io/package/ValheimModding/Jotunn/)
3. Drop `ProceduralRoads.dll` into `BepInEx/plugins/`

## Configuration

Edit `warpalicious.ProceduralRoads.cfg` in `BepInEx/config/`:

| Setting | Default | Description |
|---------|---------|-------------|
| RoadWidth | 4 | Road width in meters (2-10) |
| IslandRoadPercentage | 50 | Percentage of islands that will have roads (0-100). Largest islands selected first. |
| CustomLocations | (empty) | Comma-separated list of location names to include in road generation |
| Fords/Enabled | false | **Prototype.** Let roads ford knee-deep rivers (see below) |
| Fords/WadeWeight, RaiseWeight | 1 each | Relative odds of each ford style where a site allows it (0 disables a style) |

### Custom Locations via Config

Use the `CustomLocations` setting to add locations from other mods (e.g., Expand World Data):

```
CustomLocations = Runestone_Boars,Runestone_Greydwarfs,MerchantCamp
```

### Fords (prototype, off by default)

With `Fords/Enabled = true` a road may cross a knee-deep river instead of
stopping at it. The pathfinder can jump a river in a straight line from dry
ground to dry ground, up to 48 m, when the water under the jump is no
deeper than 0.8 m and the banks are near level; swamp roads wade their
shallows. Each ford is *waded* (the road is painted through the water at
the ground's own height, only where the water is ankle deep or in a swamp)
or *raised* (the road is leveled up so it stands 0.75 m above the
shallow-water line), picked per site by the `Fords/*` weights. Roads that
already ford a river are shared by later roads instead of each finding its
own crossing. Deeper or wider water still blocks; bridges are a separate
feature. The setting decides how a network is generated; the crossings are
stored with it. `road_crossings` in the console lists the crossings
nearest to you.

## API for Mod Authors

Other mods can register locations for road generation programmatically.

### Direct Reference (if embedding or referencing the DLL)

```csharp
using ProceduralRoads;

// Register a location
RoadNetworkGenerator.RegisterLocation("MyCustomLocation");

// Unregister if needed
RoadNetworkGenerator.UnregisterLocation("MyCustomLocation");

// Get all registered locations
IReadOnlyCollection<string> locations = RoadNetworkGenerator.GetRegisteredLocations();
```

### Reflection (soft dependency, no DLL reference required)

```csharp
private static void RegisterRoadLocation(string locationName)
{
    var assembly = AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => a.GetName().Name == "ProceduralRoads");

    if (assembly == null) return;

    var generatorType = assembly.GetType("ProceduralRoads.RoadNetworkGenerator");
    var method = generatorType?.GetMethod("RegisterLocation", 
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

    method?.Invoke(null, new object[] { locationName });
}
```

### Available API Methods

| Method | Signature | Description |
|--------|-----------|-------------|
| `RegisterLocation` | `void RegisterLocation(string locationName)` | Add a location to road generation |
| `UnregisterLocation` | `void UnregisterLocation(string locationName)` | Remove a location from road generation |
| `GetRegisteredLocations` | `IReadOnlyCollection<string> GetRegisteredLocations()` | Get all registered location names |

### Notes

- Register locations during mod initialization (Awake/Start)
- Location names must match the prefab name exactly (e.g., `Runestone_Boars`, not `Runestone Boars`)
- Both API registrations and config entries are merged at generation time

## License

MIT License - see LICENSE.md
