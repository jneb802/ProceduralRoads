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
| Fords/WadeWeight, RaiseWeight, SpanWeight | 1 each | Relative odds of each ford style where a site allows it (0 disables a style) |
| Bridges/CostFixed | 60000 | Pathfinding cost of a bridge, fixed part; lower = more bridges |
| Bridges/CostPerMeter | 600 | Pathfinding cost of a bridge per metre of span |

### Custom Locations via Config

Use the `CustomLocations` setting to add locations from other mods (e.g., Expand World Data):

```
CustomLocations = Runestone_Boars,Runestone_Greydwarfs,MerchantCamp
```

### Fords

A road may cross a knee-deep river instead of
stopping at it. The pathfinder can jump a river in a straight line from dry
ground to dry ground, up to 48 m, when the water under the jump is no
deeper than 0.8 m and the banks are near level; swamp roads wade their
shallows. Each ford is *waded* (the road is painted through the water at
the ground's own height, only where the water is ankle deep or in a swamp)
or *raised* (the road is leveled up so it stands 0.75 m above the
shallow-water line), picked per site by the `Fords/*` weights. Roads that
already ford a river are shared by later roads instead of each finding its
own crossing. Water too deep or too wide to ford is bridged instead (below).
Crossings are decided when a network is generated and stored with it.
`road_crossings` in the console lists the crossings nearest to you.

### Bridges

A road may also cross water too wide or too deep to ford, up to 128 m from dry ground to dry ground and only between
near-level banks, at the configured cost; the cost is high, so a bridge
appears where the way around is long or there is none. The water under the
jump is never leveled or painted: the road runs down to the water's edge on
each side (or, where the road climbs a cliff on both sides, the deck springs
from the bank tops) and a ruined wooden bridge is spawned between the banks
from vanilla pieces (`wood_pole2` post pairs stacked down to the riverbed,
`wood_beam` crossbeams, a `wood_floor` plank deck, a `wood_stair` at each
end) when the zone generates. The bridge is a ruin: piers survive more than
the deck, fallen stations leave stubs and toppled poles, and a navigation
gap around the deepest water is always left open so boats still pass. A
ford may now also be *spanned* (`Fords/SpanWeight`): a low footbridge with a
step at each end. Pieces are ordinary persistent objects with damage
states, so unmodded clients see them too; only the server creates them.

Crossings are decided when a network is generated and stored with it, so
changing the cost levers afterwards does not move the bridges an
existing world already has. Bridges need the pathfinder to exhaust the
land routes first, so on large islands raise `PathfindingMaxIterations`
to let it find them. `road_bridges` in the console reports the plans;
`road_bridges respawn` rebuilds the spawned pieces from the current
plans.

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
