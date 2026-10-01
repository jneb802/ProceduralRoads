[![Procedural Roads](https://i.imgur.com/gvWIgpc.png)](https://www.youtube.com/watch?v=E4uMPrtST5k)

# Procedural Roads

Generates a network of roads throughout the Valheim world. Increases world generation time.

Version 1.4.4 supports Valheim 1.0. Install Jotunn 2.30.2 or newer on each client and server.

## Features

- Adds road networks throughout the world.
- Configurable road amount, width, and pathfinding iterations.
- Roads are generated per island. The IslandRoadPercentage setting controls what percentage of islands the mod will *attempt* to generate roads on, starting with the largest islands first.
- API for mod developers to register their locations for road network connection.
- Support for custom locations from Expand World Data or other mods.
- Anonymous analytics to help improve the mod (opt-out available in config).
- Required on both client and server.
- Checkout my youtube video showcasing the mod. Click the banner image above.

## Road generation process
- Road generation starts after all locations have been placed.
- First we detect all the islands in the world.
- Get the users island percentage config, i.e if 50% islands then we select 50% of total islands to give roads.
- For each selected island, we select generate a road network within the island locations.
- Within the island, we find locations and build a network of roads between them. Some locations are prioritized over others, such as bosses and dungeons. Some locations are excluded entirely.
- Once we have locations, pathfind between the locations using A* pathfinding, avoiding rivers, steep slopes, and water.
- Once a road is defined, paint it as a stone path and smooth its height to fit the surrounding terrain.

## Instructions
- Roads will automatically generate for new worlds.
- Please make a backup before adding roads to an existing world!
- To add roads to an existing world, use the road_generate command shown below. You can validate using the road_pins command to see where roads were placed. Then you can use road_clearpins to remove the road_pins on your map.
- Please note, I have not done extensive testing on how roads impact terrain within or around player builds. Roads could potentially generate directly through an existing build.

## Configuration

| Setting | Default | Range | Description |
|---------|---------|-------|-------------|
| `RoadWidth` | 4 | 2-10 | Width of generated roads in meters. |
| `IslandRoadPercentage` | 50 | 0-100 | Percentage of islands the mod will attempt to generate roads on (largest islands first). Setting this to 100 does not guarantee every island will have roads — see "Why some islands have no roads" below. |
| `PathfindingMaxIterations` | 10000 | 1000-100000 | Maximum number of iterations for each road segment's pathfinding algorithm. Higher values will generate more roads but increase generation time. Lower values will speed up generation time but cause less roads to generate. |
| `MaxLocationsPerIsland` | 12 | 2-30 | Maximum number of locations selected for road connections on each island. |
| `CustomLocations` | *(empty)* | | Comma-separated list of location names to include in road generation. Use this for locations added by Expand World Data or other mods. Custom locations are prioritized at the same level as dungeons. |

## Why some islands have no roads
Even with IslandRoadPercentage set to 100, some islands may not receive roads. This setting controls which islands the mod *attempts* to place roads on, but road generation can still fail for several reasons:

- **Not enough key locations** — Roads are built between recognized points of interest (bosses, dungeons, crypts, etc.). If an island has fewer than 2 recognized locations, no road network can be created.
- **Terrain is impassable** — The pathfinding algorithm avoids rivers, deep water, steep mountain slopes, and highly variable terrain. If there is no viable path between two locations, that road will not be generated.
- **Pathfinding iteration limit** — Complex or long-distance routes may require more iterations than the PathfindingMaxIterations config allows. Try increasing this value if you notice roads failing to generate.
- **Island is too small** — Very small landmasses are not detected as islands during the island detection phase.

To improve road coverage, try increasing `PathfindingMaxIterations`, raising `MaxLocationsPerIsland`, and adding location names to `CustomLocations`.

## Console Commands

All commands require cheats enabled (`devcommands`).

| Command | Description |
|---------|-------------|
| `road_generate` | Generate roads for an existing world. Use after adding mod to an existing save. |
| `road_pins` | Place map pins at road start points for visualization. |
| `road_islands` | Detect islands and place pins at their centers. Optional args: `[cellSize] [minCells]` |
| `road_clearpins` | Remove all map pins added by ProceduralRoads. |
| `road_debug` | Show road point info near player position (for troubleshooting terrain issues). |

## Donations/Tips
I make mods because I enjoy it and want to make Valheim more enjoyable for everyone. If you feel like saying thanks you can tip me here.

| My Ko-fi: | [![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/warpalicious) |
|-----------|---------------|

## Source Code
Source code is available on Github.

| Github Repository: | <img height="18" src="https://github.githubassets.com/favicons/favicon-dark.svg"></img><a href="https://github.com/jneb802/ProceduralRoads"> Procedural Roads</a> |
|-----------|---------------|
