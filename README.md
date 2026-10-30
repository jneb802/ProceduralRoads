# ProceduralRoads

A Valheim mod that generates procedural roads connecting locations across your world.

## Features

- Connects a seeded mix of dungeons, settlements, ruins, bosses and quest sites across selected islands
- Terrain-aware routing that holds a maximum grade and protects the terrain of locations
- River fords and ruined wooden and stone bridges; Dvergr viaducts and bridges in the Mistlands
- Staircases where the ground is too steep for a road
- Bridges and staircases are built from vanilla pieces, so players can repair them
- Server-side: players without the mod see and walk the same roads

## Installation

1. Install [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)
2. Install [Jotunn](https://valheim.thunderstore.io/package/ValheimModding/Jotunn/)
3. Drop `ProceduralRoads.dll` into `BepInEx/plugins/`

On a dedicated server, install it on the server only. Players do not need it, and players who have it are let in whatever version they run.

## Generation

A world's road network is generated once, the first time the world loads, behind the loading screen. **This takes a while:** about 10 minutes on a Ryzen 7 9800X3D using 15 threads, longer on slower machines. It happens only once per world; road quality was given priority over generation time. Use **Cancel and return to menu** to stop it; the next load starts again. Saved networks load without generating. On a dedicated server, players can join once generation has finished.

Roads are best on a fresh world. A world explored before it had roads asks once, on its first load with the mod, what to do with the land explored so far:

- **Build roads through explored land too** (recommended): the world is backed up first (`<world>_backup_<date>`, restored from Manage saves). Zones with your buildings or terrain edits are left exactly as they are, and the roads go round them; use `road_connect` to join your builds to the network.
- **Build roads in new land only**: everything explored so far stays as it is.
- **Skip road building**: no roads, and the world stays as it is. Not asked again; `road_generate` can still build them.
- **Back to the menu**: nothing is decided; the next load asks again.

A dedicated server, which has no screen, takes the recommended answer. `ExistingWorldFirstLoadRoads` gives the answer instead of asking.

## Configuration

Edit `warpalicious.ProceduralRoads.cfg` in `BepInEx/config/`:

| Setting | Default | Description |
|---------|---------|-------------|
| RoadWidth | 4 | Road width in meters (2-10) |
| IslandRoadPercentage | 33 | Percentage of islands selected for roads (0 disables generation) |
| MaxLocationsPerIsland | 48 | Destinations selected per island (2-128) |
| PathfindingMaxIterations | 30000 | Search budget per road; higher finds more roads, slower |
| RockClearing | Carve | Rocks in the road: Off, Remove or Carve |
| CustomLocations | (empty) | Comma-separated list of location names to include in road generation |
| ExcludedRoadBiomes | AshLands, DeepNorth | Biomes with no destinations (roads may still pass through) |
| ExistingWorldFirstLoadRoads | Ask | First load of a world explored before it had roads: Ask, IncludeExplored, NewZonesOnly or Skip |
| AttemptRoadsToHildirQuestSites | false | Roads to Hildir's cave, crypt and tower |

Settings apply when a network is generated, not to a saved one. Not every selected destination gets a road: some cannot be reached within the grade and site rules.

### Custom Locations via Config

Use the `CustomLocations` setting to add locations from other mods (e.g., Expand World Data):

```
CustomLocations = Runestone_Boars,Runestone_Greydwarfs,MerchantCamp
```

## Console Commands

Host or admin only:

| Command | Description |
|---------|-------------|
| `road_progress` | Generation phase and island count |
| `road_generate` | Regenerate the whole network (back up first) |
| `road_regen_island` | Clear all roads and regenerate only the island you are on (back up first) |

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

## Development

Unit tests: `dotnet test ProceduralRoads.Tests`.

## License

MIT License - see LICENSE.md
