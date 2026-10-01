| `Version` | `Update Notes`    |
|-----------|-------------------|
| 1.4.4     | - Added Valheim 1.0 compatibility for zone identifiers, debug markers, terrain rebuilds, and saved-world location readiness. |
|           | - Fixed road metadata object initialization. Empty road networks now replace previously saved road data. |
|           | - Added the MaxLocationsPerIsland setting. Updated the Jotunn requirement to 2.30.2. |
| 1.4.3     | - Fixed an issue where customLocations rarely got roads. I increased their default selection weight from 20 --> 80 |
| 1.4.2     | - Increased default pathfinding iterations to 10,000 to improve road generation success on complex terrain. Added configurable PathfindingMaxIterations setting. |
|           | - Added small analytics code to ping whenever game starts with my mod so I can get usage data. Totally anonymous and no player data is sent. You can disable if you'd like. |
| 1.4.1     | - Increase generation time by decreasing the max pathfinding iterations during A* pathfinding |
| 1.4.0     | - Fixed an issue that caused roads to regenerate every time openning a world. |
| 1.3.1     | - Fixed and issue causing custom locations to not be registered to the road network. |
| 1.3.0     | - Fixed the infinite world generation bug. |
| 1.2.0     | - Fixed road point peristence between world loads, road_pins should work after world reload now. |
| 1.1.0     | - Added ability for road points to persist to world zdo so that road_pins works after world reload. |
| 1.0.0     | - Initial Release |
