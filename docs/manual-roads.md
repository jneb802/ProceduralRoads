# Manual roads

Host or admin only. These commands add roads to the saved network without regenerating it. A manual road follows the same grade, turn, river-crossing and location rules as a generated one; a request that cannot meet them is refused.

Coordinates are world X,Z; the height is taken from the ground.

```
road_connect
road_connect 120 -450
road_connect player <peer-id>
road_connect force
road_path 120,-450 180,-470 240,-420
```

- `road_connect` joins the nearest road on your island, from where you stand, a point or a connected player.
- `road_path` routes through 2 to 32 points in order, all on one island; it can make an island's first road.

A manual road goes round every zone players changed (something a player built, or a terrain edit in land explored before the roads), and its levelling stays out of them. To build into one on purpose, stand in it for `road_connect` or put a mark in it: the first run builds nothing and names the zones; run the same command again within two minutes to build it there, over whatever stands on its line. `force` at the end (`road_connect force`, `road_path ... force`, `road_mark build force`) builds through any zone at once and says which zones players had changed.

To collect points while exploring, move to each point and add it, then build:

```
road_mark add
road_mark list
road_mark build
```

`road_mark undo` removes the last point and `road_mark clear` empties the draft. A failed build keeps the draft. Drafts last until the world is unloaded. On a dedicated server, use explicit coordinates or `road_mark add player <peer-id>`.

## Saving

A manual road is saved with the network and kept across save and reload. Regenerating the network (`road_generate`, `road_regen_island`) replaces it. Terrain changes only inside the new road's footprint; zones that are not loaded get the road when they load. There is no undo.

Manual roads are refused on a world whose saved network could not be read. Older builds of the mod cannot read a network with manual roads, so back up the world before downgrading.

Players without the mod get the new road like any other: only the server writes roads.
