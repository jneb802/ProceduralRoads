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

On a dedicated server, install it on the server; players do not need it
(see [Servers and players without the mod](#servers-and-players-without-the-mod)).
Players who do install it must use the same version as the server.

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
| Fords/WadeWeight, RaiseWeight, SpanWeight | 1 each | Relative odds of each ford style where a site allows it (0 disables a style) |
| Bridges/CostFixed | 60000 | Pathfinding cost of a bridge, fixed part; lower means more bridges |
| Bridges/CostPerMeter | 600 | Pathfinding cost of a bridge per metre of span |

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
unless its network is regenerated (`road_regen_island`). Crossing moves are
considered when the search meets blocked ground, alongside land alternatives.
Raising `PathfindingMaxIterations` can help a difficult search finish, at the
cost of a longer generation wait.

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

## Servers and players without the mod

Roads and bridges reach every player as ordinary game data, so **only the
server needs the mod**: install it on the dedicated server, or on the
hosting player of a listen server. A player joining with no mod at all
sees and walks the same roads. A player who does install the mod must run
the same version as the server; a client with a different version is
turned away at the version check (two builds would disagree about the
roads they write), and a client without the mod never answers that check
and is let in.

### What reaches a client

- **Road terrain.** The server writes each road's height and paint into the
  zone's own terrain compiler, the same saved data a player's hoe or pickaxe
  produces. A client applies that to the terrain it generates for itself,
  with nothing but the game.
- **Bridges** are spawned from vanilla building pieces, which every client
  already has.

No client-side plugin, cheat, asset bundle or console command is part of
this; the console commands below are for the operator.

### When the server does the work

- A zone that has not been generated yet gets its roads when the game
  generates it, whether for the server itself or for a joining player, in
  the same step as the vanilla terrain. Nothing is generated ahead of time:
  the mod does not pre-generate the world.
- A zone that was **already generated without its roads** (the world
  existed before the mod, or before this road network) is queued when the
  network becomes available at startup and written in small slices of each
  frame, from the server's saved terrain data, whether or not anyone is near
  it. Players can join and play while it runs.
- A zone whose compiler is already stamped with the current network is left
  alone on every later start, so a restart writes nothing twice and the
  terrain a player has changed there since is not touched.
- Waiting for an owner is not a failed write and spends no retry attempt.
  A saved compiler waits while its owner is active in the zone. A compiler
  live on the server waits until the foreign ownership is released, even if
  that player has moved away. The mod does not force that live transfer;
  there is no bounded completion time for this wait. `road_bake zone` reports
  `WaitForOwner`, and requeueing does not override ownership.
- An attempted terrain write that reports failure is retried up to three
  times, then given up and reported. An unexpected exception during a zone's
  terrain, bridge or vegetation work stops that zone's queued bake work instead;
  other zones continue. `road_bake` reports the number of zone exceptions and
  the log names each affected zone. Fix the cause and use `road_bake again`
  to retry. A queue-wide error pauses the queue with the same recovery
  instruction; it does not disable generation-time ghost writes.

### Existing worlds

Roads exist only once a network has been generated for the world: a world
that already has saved roads loads them, and a world without any generates
them on the first start. Changing the network afterwards (`road_generate`,
`road_regen_island`, or a build that changes what is generated) rewrites the
road zones of the replacement network, which puts road terrain back over any
terrain change a player made **on the road line** in those zones. Terrain
changes off the road, and everything a player built, are not touched.
Manual additions (`road_connect`, `road_path`, `road_mark build`) use the same
server bake, but only write the new road footprint. Older road terrain and player
edits outside that footprint are retained. An unchanged zone is left alone even
if its saved stamp predates additions elsewhere. Stock clients need no command
plugin: the host/server console can mark their positions by peer ID.

`road_bake again` walks every road zone of the current network once more,
writing only the ones not yet current.

Bridges carry a layout version in the saved network (see above); on a
dedicated server `road_bridges respawn` rebuilds the current plans into every
planned zone the world has already generated, and zones not generated yet
get theirs when they are. Pieces the mod spawned are tagged and only tagged
pieces are ever removed; a bridge a player has smashed stays smashed until an
explicit respawn or regeneration.

Back up the world before installing the mod on it and before any operation
that changes the network.

### Vegetation on the road (existing worlds)

The game keeps vegetation off a road only while it generates a zone. On a
world that already existed before its roads, the zones it had already
generated keep every tree, rock and bush they grew, including the ones now
standing in the middle of a road. The server clears those, once per zone for
each road network version, including a manual addition. The vegetation pass
retains its existing player-build and location protections; unlike terrain, its
clearing record is for the whole network rather than individual additions.

Never removed:

- anything a player built: it is not vegetation, road or no road
- any object carrying a creator, whoever placed it
- any vegetation within 8 metres of something a player built, planted or
  wild, including across a zone border
- saplings and other growing plants
- anything standing inside a location's own footprint

One thing **is** still removed, and is worth knowing before installing this
on a long-played world: a fully grown tree a player planted that stands on
the road line and more than 8 metres from anything they built. The save does
not record who grew a tree; a planted birch and a wild one are the same
object, so it is cleared like any tree the world grew there.

**To look before anything is cleared, the bake has to be off when the server
starts.** Clearing begins on its own once the road network is available,
well before anyone can type a command, and the commands only report; they
do not pause the queue. `PROCEDURALROADS_SERVER_BAKE` is read once at startup,
so it has to be set in the environment the server launches with. On a copy
of the world:

```
PROCEDURALROADS_SERVER_BAKE=off   # in the server's environment, before launching
road_bake vegetation              # road zones holding vegetation on the road, most first
road_bake zone [x z]              # one zone: what is in it and what would happen to it
```

Then restart with the switch unset (the default, on). The switch disables
the background queue, its existing-zone vegetation clearing, and ghost
terrain writes. It does not disable the independent live-zone/terrain-compiler
hooks or generation-time vegetation exclusions. It is not a read-only mode:
use a disposable copy and keep players out while previewing. On a server
already running with the bake on, these commands report what is left, not
what is coming. `road_bake again` does not override a startup-disabled bake.

### Looking at what the server did

- `road_bake`: the running totals of the current network's zones (see
  above) and what is still pending or deferred.
- `road_bake zone [x z]`: one zone, what the server knows about it (generated
  or not, road points, planned bridge pieces, whether its vegetation matches
  the roads, its terrain compilers with their stamp and owner) and what it
  would do next.
- `road_bake server`: the server's own zone machinery, its reference position
  and each connected player's zone.
- `road_zone_report [x z]`: what a zone holds because of the roads, read from
  the saved data: a fingerprint of its terrain compiler, its bridge pieces and
  its vegetation. The same zone written by a modded client and by the server
  reports the same thing.

### Removing the mod

Everything the mod writes is ordinary saved game data, and it stays: road
terrain remains in the zones that carry it, spawned bridge pieces remain as
building pieces, and vegetation that was cleared does not grow back. Zones
generated after the mod is removed get no roads, so a network ends where
generation had reached. Deleting the DLL reverts nothing; restore a backup
from before the installation if that is what is wanted.

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


The network-selection and routed-connection update builds on the parallel
island engine and its walkable-and-crossable detector. All pipeline settings
use that same performance machinery; selecting the older chain/MST strategy
does not turn off the terrain cache or worker safety. The separate performance
release is 1.7.0; the full pipeline update is 1.8.0.
## Player-defined roads

Use `road_connect` to join the network on your island, `road_path` to route through
X,Z coordinates, or `road_mark add/list/undo/build/clear` to collect waypoints while
exploring. Host/admin only. See [manual-road commands](docs/manual-roads.md) for
examples, persistence and compatibility requirements.
