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
| EnableRoads | true | Enable road generation |
| RoadWidth | 4 | Road width in meters (2-10) |
| MaxRoadsFromSpawn | 5 | Number of roads from spawn (1-10) |
| MaxRoadLength | 3000 | Maximum road length in meters (500-8000) |

## License

MIT License - see LICENSE.md
