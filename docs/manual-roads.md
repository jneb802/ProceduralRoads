# Build your own roads

These host/admin commands add terrain-aware roads without regenerating the existing
network. They follow the usual grade, turn-space, river-crossing and POI-protection
rules. A request that cannot meet those rules is refused.

Use horizontal world **X,Z** coordinates; Y is elevation and is determined from
the ground. Flying above a site does not create a raised road.

```text
road_connect
road_connect 120 -450
road_connect player <peer-id>
road_path 120,-450 180,-470 240,-420
```

`road_connect` joins an existing road on your island. It reports an error if the
island has no road network, and does nothing if you are already on a road.
`road_path` can create an island's first road. Supply two to 32 points in order.
All points must be on the same island; ordinary river crossings are supported.

To collect points while exploring:

```text
road_mark add
road_mark add
road_mark add
road_mark list
road_mark build
```

Move to each desired point before `add`. Use `road_mark undo` to remove the last
point or `road_mark clear` to empty the draft. These commands never remove roads
or undo terrain changes. `list` also prints the equivalent `road_path` command.
A failed build retains the draft; success clears it. Drafts are session-only and
are cleared on world unload. Waypoints guide the route; turn shaping can move it
by up to two road widths around a mark. A larger deviation is refused.

On a dedicated server, use explicit coordinates or `road_mark add player <peer-id>`
to sample a connected character. The dedicated console has one shared draft.
This is a local host/server-console interface: remote clients cannot submit road
mutations. Normal console cheat/admin access is still required.

The first manual request may need an island scan; later requests reuse it until
world unload. Planning is synchronous and bounded by the configured search
budget per leg. Long lists can pause the host. No extra worker pool is started.

## Saving and terrain

Successful commands append to the saved road network. They retain existing bridge
objects and do not force old road terrain to be applied again. Additions are
tracked separately so a compiler that missed several additions can catch up after
restart, without repainting old roads elsewhere in the same zone. Terrain inside
the *new* road footprint is intentionally changed; unrelated player and POI
terrain is preserved.

“Road added” means the plan has entered the network, not that every zone has
finished applying it. Loaded terrain applies when its compiler is ready and
owned; unloaded zones apply on arrival. Pending bridge additions are saved and
retried in small batches. Save normally before shutting down. If application
reports an error after the road was added, do not repeat the build: fix the error
and allow application to retry, or save/reload.

Normal save/reload preserves manual roads. Explicit full-network regeneration
replaces them along with the rest of the network. There is no terrain undo or
manual-route replay in this version.

## Compatibility and validation boundary

Install this build on the host or dedicated server. Players without the mod receive
the added road terrain and bridge pieces as ordinary saved world objects. Matching
modded clients also receive read-only network snapshots; no client can send a
route for the host to execute. Use the server console with explicit coordinates or
`player <peer-id>` when marking for a player who does not have the mod.

The server bake picks up additions automatically. It waits for a foreign terrain
owner where necessary; "Road added" does not promise an immediate terrain update
beneath a connected player. An addition writes its new footprint and preserves
edits on older roads outside that footprint. Full regeneration remains a separate,
explicit operation that reapplies the entire network. Vegetation clearing still
runs once per network version, including additions, with the existing protections
for player builds and location footprints; isolated grown player-planted trees
cannot be distinguished from wild ones.

Once a manual road is added, road data uses format 3 (older formats remain
readable). Older builds cannot load format 3; do not downgrade a world with manual
roads without restoring its pre-feature save.
