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
| Bridges/CostFixed | 60000 | Pathfinding cost of a bridge, fixed part; lower means more bridges |
| Bridges/CostPerMeter | 600 | Pathfinding cost of a bridge per metre of span |

### Road approaches and protected locations

Roads approach the outside of locations while preserving their authored terrain and paint, including locations that are not road destinations. On hillsides, the generator searches for an arrival near the site's elevation and blends the final road profile into the ground. Excavated interiors do not pull an exterior approach down into the excavation.

`Roads / MaxGrade` defaults to `0.35` (35% rise over run). It constrains the planned route and height profile; `0` disables the grade cap. Climbing switchbacks use wider turns and gentler landing profiles. A route that cannot fit the grade, turn or location-clearance constraints can be refused, leaving the location without a road. Actual ground still depends on the game's terrain limits and existing edits.

These routing changes apply when generating a network. They do not remove terrain damage already baked by an older network. Test regeneration on a disposable world or copy first.

Diagnostics: `road_site <x> <z>` describes a nearby location; `road_ends` compares locations with nearby road points. Its height differences are diagnostic measurements, not proof that an entrance or turn is walkable. See [validation and follow-ups](docs/ROAD-FOLLOWUPS.md) and the [offline approach audit](docs/SITE-APPROACH-AUDIT.md).

### Custom Locations via Config

Use the `CustomLocations` setting to add locations from other mods (e.g., Expand World Data):

```
CustomLocations = Runestone_Boars,Runestone_Greydwarfs,MerchantCamp
```

### River crossings

Roads no longer stop at water. When the pathfinder reaches a river it may
jump it in a straight line from dry ground to dry ground, and the crossing
is built in one of two ways:

- **A ford**, where the water is shallow (no deeper than 0.8 m under the
  jump, up to 48 m wide, banks within 4 m of each other in height). Each
  ford is *waded* (the road is painted through the water at the ground's
  own height, only where the water is ankle deep or in a swamp), *raised*
  (the road is levelled up so it stands 0.75 m above the shallow-water
  line) or *spanned* (a low wooden footbridge). Which style a site gets is
  drawn from the `Fords/*` weights; a weight of 0 disables that style.
- **A bridge**, where the water is too deep or too wide to ford: up to
  128 m from dry ground to dry ground, between banks within 2.5 m of each
  other in height, at the configured pathfinding cost. The cost is high by
  default, so a bridge appears where the way around is long or there is
  none. The water is never levelled or painted; the road runs to the
  water's edge on each side and a ruined wooden bridge is spawned between
  the banks when the zone generates.

Roads that already cross a river are shared by later roads instead of each
finding its own crossing. Crossings are decided when a network is generated
and stored with it, so changing the weights or costs afterwards does not
move the crossings an existing world already has, and a world whose roads
were generated before this feature keeps its roads and gains no crossings
unless its network is regenerated (`road_regen_island`). Bridges need the
pathfinder to exhaust the land routes first, so on large islands raise
`PathfindingMaxIterations` to let it find them.

### Bridges

A bridge is built from ordinary vanilla pieces so that a player can repair
it with the hammer:

- **A 4 m deck, two lanes wide** (the road's own width): two `wood_floor`
  plates side by side on every 2 m bay, on `wood_beam` crossbeams, on pairs
  of `wood_pole2` posts stacked down to the riverbed.
- **A level deck.** The deck sits at one height, at least 0.5 m above the
  water, and the drop to each bank is taken by a run of `wood_stair` steps,
  two abreast, that marches outward until its foot is in the ground. Where
  the banks are unequal, the lower end simply has the longer stair.
- **Headings a hammer can reach.** Every piece is level and stands on a
  multiple of 22.5 degrees of yaw, which is what the vanilla placement
  ghost can be turned to. A crossing whose line is not on that grid is
  turned onto a heading that is, within the same limits routing applied;
  the rare site where no such heading works keeps the road the pathfinder
  priced and reports itself as not placeable in `road_bridge_repairs`.
  Such a bridge is not hand-repairable.

Every bridge ships as a ruin. What stands is decided deterministically from
the world seed and the crossing, so a bridge looks the same on every visit
and to every player: piers survive more than the deck, the two lanes of a
bay are ruined independently (either lane, both or neither may be missing,
with different damage), fallen stations leave stubs and toppled poles, and a
navigation gap over the deepest water is always left open so boats still
pass. Because of the gap **an untouched bridge is not a complete crossing**:
to walk a cart across, a player has to close the gap and the ruined bays
with their own `wood_floor`, `wood_beam`, `wood_pole2` and `wood_stair`
pieces, which snap to the standing ones. `road_bridge_repairs <x> <z>` in
the console lists exactly which pieces the complete bridge nearest that
point has and the shipped one does not, with their coordinates and yaw.
`road_crossings` lists the crossings nearest to you and `road_bridges`
reports the plans; `road_bridges respawn` destroys every spawned bridge
piece and rebuilds the current plans.

Vegetation is kept clear of the deck. A ford may also be *spanned*
(`Fords/SpanWeight`): a short bridge of the same construction, without a
navigation gap.

### Existing worlds and bridge layout changes

The pieces the mod spawns are tagged, and only tagged pieces are ever
destroyed by the mod; pieces a player places are never touched. Bridges
carry a layout version in the saved network. If a world's bridges were
spawned by an earlier layout, the mod replaces them the next time the world
loads: every tagged piece is destroyed and the current layout is spawned as
the zones come alive. Player-placed repairs survive that, but they may no
longer line up with the new layout. Back up a world before loading it with
a build that changes the layout.

The mod must be installed on the server (or the hosting player) and on
every client, as for the roads themselves; this feature does not add a
server-only mode.

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

### Island generation and performance

New networks use an 8 m walkable-and-crossable island scan by default. This
separates open straits while allowing suitable river crossings to join land.
It changes island grouping relative to the old coarse detector, and can therefore
change which islands and roads are selected. It does not guarantee a route.
The scan uses a fixed 8 m spacing. `WalkableIslands=false` retains the legacy
coarse scan. `road_islands` without arguments uses the generation settings.

Islands are built concurrently after location assets and saved heights have been
prepared on the main thread. Each worker owns its pathfinder. Terrain samples
are cached across its searches in a fixed 65,536-entry buffer (about 1.75 MiB per
pathfinder) and released with the generation. Shared road records are guarded.

The default worker count is logical processors minus one, with a minimum of one.
`PROCEDURALROADS_ISLAND_WORKERS=1` selects a serial comparison; an unset variable
uses the automatic count. This is a diagnostic environment setting.

Fresh configs use a 30,000-iteration search budget. Existing configured values
are retained. Island selection still favours size, location selection still uses
priority, and the existing chain/MST strategies are retained in this release.
The broader destination and connection changes are a separate follow-up.
