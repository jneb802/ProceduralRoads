# ProceduralRoads

A Valheim mod that generates procedural roads connecting locations across your world.

## Features

- Connects a seeded mix of dungeons, settlements, ruins, bosses and quest sites
- Spreads destinations over selected islands and joins branches to existing roads
- Terrain-aware routing, with fords and ruined wooden bridges across rivers
- Supports destinations from other mods through configuration or the registration API
- Server-side: players without the mod see and walk the same roads

## Installation

1. Install [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)
2. Install [Jotunn](https://valheim.thunderstore.io/package/ValheimModding/Jotunn/)
3. Drop `ProceduralRoads.dll` into `BepInEx/plugins/`

On a dedicated server, install it on the server only. Players do not need it, and players who have it are let in whatever version they run. With More World Locations (MWL), MWL must still be installed on the clients too.

Roads are best on a fresh world. On an existing world, zones already explored before the roads were generated are never changed, so roads only appear in new zones; back up the world first.

## Configuration

Edit `warpalicious.ProceduralRoads.cfg` in `BepInEx/config/`:

| Setting | Default | Description |
|---------|---------|-------------|
| RoadWidth | 4 | Road width in meters (2-10) |
| IslandRoadPercentage | 33 | Percentage of islands selected for roads (0-100); zero disables generation. Boss islands are not automatically included. |
| SelectIslandsByContent | true | Prefer islands with more eligible destinations; false prefers larger islands. |
| MaxLocationsPerIsland | 48 | Destination budget per island (2-128). An island earns eight, plus four for every two square kilometres, up to this cap. Boss/quest sites and selected coastal landings consume slots but may exceed it. |
| TargetSubAreas | 9 | Approximate number of sub-areas used to spread destinations; zero disables spreading. |
| WeightedDestinations | true | Seeded selection by importance and proximity; false uses priority ranking. |
| RoutedConnections | true | Price nearby connections and grow branches toward existing roads; false uses the previous chain/MST choice. |
| PathfindingMaxIterations | 30000 | Build-search budget; pricing probes use half. Higher values may find more connections at greater cost. |
| WalkableIslands | true | Group walkable land and nearby crossable water; false uses the previous coarse base-height detector. |
| CoastalLandings | true | Add possible boat approaches on already-selected islands, about one per 5 km² (at least one where suitable), at least 400 m apart on the same island. Uses WalkableIslands samples; inactive with the legacy detector. No dock is spawned and access is not guaranteed. |
| CustomLocationPriority | 80 | Selection weight for custom locations from config or API. Try 30 for less MWL emphasis. |
| ExcludedRoadBiomes | AshLands, DeepNorth | Exclude destinations in these biomes from every source, including API registrations. Routes can still pass through an excluded biome. |
| CustomLocations | (empty) | Comma-separated list of location names to include in road generation |

### Not every destination gets a road

**Expect roughly one destination in five to be left without one.** Measured over
six frozen terrain dumps with no custom registrations: 1,462 of 1,784 selected destinations ended up next to a road, or
**82%** — between 78% and 85% depending on the world.

That is normal, and it is the same kind of thing as Valheim failing to place
every location it tries during world generation. A destination is dropped when
no route reaches it at all, or when a road to it could not be built inside the
grade, turn-room and site-protection rules. The generation summary reports unreached destinations separately from failed
route attempts; a road built to a neighbour may also reach a destination.

Two practical consequences:

- **The count you set is a budget, not a promise.** `MaxLocationsPerIsland` is
  how many destinations are *selected* per island, not how many get roads.
- **Oversample if you want more roads.** Raising `MaxLocationsPerIsland` puts
  more candidates in play, so more of them succeed. There is no fixed success rate: the outcome depends on the island and terrain.
  More destinations also means a longer generation.

### Road approaches and protected locations

Roads approach the outside of locations while preserving their authored terrain and paint, including locations that are not road destinations. On hillsides, the generator searches for an arrival near the site's elevation and blends the final road profile into the ground. Excavated interiors do not pull an exterior approach down into the excavation.

`Roads / MaxGrade` defaults to `0.35` (35% rise over run). It constrains the planned route and height profile; `0` disables the grade cap. Climbing switchbacks use wider turns and gentler landing profiles. A route that cannot fit the grade, turn or location-clearance constraints can be refused, leaving the location without a road. Actual ground still depends on the game's terrain limits and existing edits.

These routing changes apply when generating a network. They do not remove terrain damage already baked by an older network. Test regeneration on a disposable world or copy first.

Diagnostics: `road_site <x> <z>` describes a nearby location; `road_ends` compares locations with nearby road points. Its height differences are diagnostic measurements, not proof that an entrance or turn is walkable. See [validation and follow-ups](docs/ROAD-FOLLOWUPS.md) and the [offline approach audit](docs/SITE-APPROACH-AUDIT.md).

These defaults apply to a new configuration. Existing config files retain their
saved values: updating does not silently replace `IslandRoadPercentage = 50`,
`MaxLocationsPerIsland = 12` or `PathfindingMaxIterations = 10000`. Set them to
33, 48 and 30000 respectively to try the new defaults.

Existing saved road networks are loaded unchanged. New settings affect fresh
generation or an explicit regeneration, not a world merely being reloaded.
Back up before regenerating. `road_regen_island` replaces the whole saved
network with roads for that island; it does not retain other islands' roads.

Island grouping and route pricing are practical approximations. Selection does
not guarantee a road: difficult terrain can leave a chosen place unconnected.
A boss or quest site is retained within a selected island, but its presence
does not make an otherwise unselected island receive roads.

### Custom Locations via Config

Use the `CustomLocations` setting to add locations from other mods (e.g., Expand World Data):

```
CustomLocations = Runestone_Boars,Runestone_Greydwarfs,MerchantCamp
```

## Console Commands

Host or admin only:

| Command | Description |
|---------|-------------|
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
