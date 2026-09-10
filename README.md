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
| IslandRoadPercentage | 50 | Percentage of eligible islands that will have roads (0-100). Islands are selected across inner, middle, and outer world rings, with larger islands preferred inside each ring. |
| CustomLocations | (empty) | Comma-separated list of location names to include in road generation |

### Custom Locations via Config

Use the `CustomLocations` setting to add locations from other mods (e.g., Expand World Data):

```
CustomLocations = Runestone_Boars,Runestone_Greydwarfs,MerchantCamp
```

## API for Mod Authors

Other mods can register locations for road generation programmatically.

### Direct Reference (if embedding or referencing the DLL)

```csharp
using ProceduralRoads;

// Register a location with the default custom priority
ProceduralRoadsAPI.RegisterLocation("MyCustomLocation");

// Register a location with an explicit priority (0-100)
ProceduralRoadsAPI.RegisterLocation("MyImportantLocation", 90);

// Unregister if needed
ProceduralRoadsAPI.UnregisterLocation("MyCustomLocation");

// Get all registered locations and priorities
IReadOnlyDictionary<string, int> locations = ProceduralRoadsAPI.GetRegisteredLocationPriorities();
```

### Reflection (soft dependency, no DLL reference required)

```csharp
private static void RegisterRoadLocation(string locationName)
{
    var assembly = AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => a.GetName().Name == "ProceduralRoads");

    if (assembly == null) return;

    var apiType = assembly.GetType("ProceduralRoads.ProceduralRoadsAPI");
    var method = apiType?.GetMethod(
        "RegisterLocation",
        new[] { typeof(string), typeof(int) });

    method?.Invoke(null, new object[] { locationName, 80 });
}
```

### Available API Methods

| Method | Signature | Description |
|--------|-----------|-------------|
| `RegisterLocation` | `void RegisterLocation(string locationName)` | Add a location to road generation with the default custom priority |
| `RegisterLocation` | `void RegisterLocation(string locationName, int priority)` | Add a location to road generation with an explicit priority from 0-100 |
| `RegisterLocations` | `void RegisterLocations(IEnumerable<string> locationNames, int priority = 80)` | Add multiple locations to road generation with a shared priority |
| `UnregisterLocation` | `void UnregisterLocation(string locationName)` | Remove a location from road generation |
| `GetRegisteredLocationPriorities` | `IReadOnlyDictionary<string, int> GetRegisteredLocationPriorities()` | Get all registered location names and priorities |

### Notes

- Register locations during mod initialization (Awake/Start)
- Location names must match the prefab name exactly (e.g., `Runestone_Boars`, not `Runestone Boars`)
- Both API registrations and config entries are merged at generation time

## License

MIT License - see LICENSE.md
