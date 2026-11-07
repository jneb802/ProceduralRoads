# Decision owners

Every rule, check, threshold or limit in ProceduralRoads is decided in exactly one place: its owner. Code that needs the answer
calls the owner; it never restates the rule. This is the one list of those decisions.

- **Before adding or changing a check, threshold or rule**, find its decision here and call (or change) that owner.
- **A new decision adds a row here in the same commit.** A new number needs the maintainer's approval.
- An owner is written `Type.Member`, never `file:line`: lines move, names are checked. `OwnersDocTests` resolves every
  `Type.Member` in the Owner and Key constants columns against the code, so a rename or a delete fails the gate until this
  list is updated.
- `OwnerLiteralTests` fails when a key constant's number turns up as a literal outside its owner (see the Literals section).
- Overloads and partial classes count as one owner. `Type.Member` names the member that holds the rule.

## Decisions

| Decision | Owner | Key constants | Extend here |
|---|---|---|---|
| Where a road may go: the search, its costs, its refusals | `RoadPathfinder.FindPath`, `RoadPathfinder.FindPathBetween`, `RoadPathfinder.FindPathToNetwork`, `RoadNetworkGenerator.EarthworkRefusal` | `RoadConstants.DefaultSlopeMultiplier`, `RoadConstants.PathfindingCellSize`, `RoadConstants.MaxRiverCrossingCells`, `RoadConstants.MaxBridgeCrossingCells` | A new cost or refusal is a term in the search. Do not filter a finished path in a caller. |
| The road grade cap, pitches, and the landing at a road's end | `RoadGrade.Configured`, `RoadGrade.Limit`, `RoadPitches.PlacePitches`, `RoadPitches.LandEnd`, `RoadPitches.ClimbToFoot`, `RoadPitches.ApplyJunctionLanding` | `RoadConstants.DefaultMaxRoadGrade`, `RoadPitches.JunctionLanding`, `RoadPitches.JunctionLandingGrade`, `RoadPitches.PitchLength` | A grade limit reads `RoadGrade.Configured`. A landing before a staircase or a junction is `RoadPitches`' landing, not a new length. |
| Road width, and every rule that follows it | `RoadNetworkGenerator.RoadWidth`, `RoadRide.Reach`, `RoadBeside.Current`, `RoadRockCarve.WidestRoadHalfWidth` | `RoadConstants.DefaultRoadWidth` | Read the `RoadWidth` setting. The fixed 4 m is only the default. |
| Tight turns and bend radius | `RoadSwitchbacks.Shape`, `RoadSwitchbacks.BendRadius` | `RoadSwitchbacks.StairRadius` | A turn limit goes here, not in the search. |
| Sway and meander | `RoadWiggle.Apply`, `RoadWiggle.Sway`, `RoadPathfinder.MeanderBiome` | `RoadWiggle.Flat` | Search-time meander is the pathfinder's; the sway after the search is the wiggle's. |
| The earthwork cap: how far a road may cut or fill | `RoadNetworkGenerator.EarthworkCap`, `RoadNetworkGenerator.EarthworkLimit`, `RoadNetworkGenerator.ShapedLimit`, `RoadNetworkGenerator.PastTerrainLimit`, `RoadNetworkGenerator.WithinTerrainLimit` | `RoadNetworkGenerator.DefaultEarthworkCap`, `RoadConstants.TerrainDeltaMax`, `RoadConstants.TerrainDeltaMin` | Compare cut or fill against the cap with `PastTerrainLimit` / `WithinTerrainLimit` (they differ for NaN). Never write the 8 m. |
| What the terrain writer does: cuts, batter, the ground after a staircase's cuts | `RoadTerrainModifier.ModifyVertexHeights`, `RoadTerrainModifier.CutAt`, `RoadTerrainModifier.BatterExtra`, `RoadTerrainModifier.CutHeightAt`, `RoadTerrainModifier.GroundAfterCuts`, `RoadTerrainModifier.StairCutReach`, `RoadTerrainModifier.CutReach` | `RoadTerrainModifier.CutBatterReach`, `RoadTerrainModifier.CutBatterSlope` | Predict the ground a cut leaves with `GroundAfterCuts`; it is the writer's own formula. |
| The painted edge and footprint of a road | `RoadProfile.PaintEdge`, `RoadProfile.Footprint`, `RoadTerrainModifier.ApplyRoadPaint` | `RoadConstants.RoadPaintOuterRatio` | Anything that needs the painted half width calls `PaintEdge`. |
| Headroom: the box a walker needs over a road or a stair | `RockClearances.Bottom`, `RockClearances.Top`, `StairLayout.BoxTop`, `StairLayout.HighestWalk` | `RoadRockPolicy.ClearanceHeight`, `RoadRockPolicy.ClearanceLift` | One box for roads, stairs and the viaduct parapet band. |
| Which rocks a road clears or carves | `RockClearances.Near`, `RockClearances.Road`, `RoadRockPolicy.ClearingTakes`, `RoadRockPolicy.IsClearableRock`, `RoadRockPolicy.CarveReach`, `RoadRockPolicy.MayTouch`, `RoadRockPolicy.DeckTakes`, `RoadRockPolicy.HowTaken`, `RoadRockProbe.RunPass`, `RoadRockCarve.Carve` | `RoadRockPolicy.PlayerHeight`, `RoadRockPolicy.PlayerWidth` | A new rock name or biome goes in `RoadRockPolicy`. A rock a deck's clearance meets goes whole whatever the biome (`DeckTakes`); only a place's, a player's or another peer's rock stays. |
| Which rocks a place keeps | `SiteRock.KeptAt`, `SiteRock.Kept`, `PlacedSiteReader.RockRadius` | none | The kept-rock rule is one OR in `SiteRock.Kept`; callers keep only their own measures. |
| Vegetation clearing and lying logs | `VegetationClearing.ShouldRemove`, `VegetationClearing.Decide`, `VegetationClearing.InSquare`, `VegetationClearing.InsideClearArea` | `VegetationClearing.PlayerGroundRadius` | The generation side and the server side both ask `VegetationClearing`; a zone explored before the network is owed the lying pieces' judgement only (`ServerBakePlanner.LyingOwed`). |
| Site protection: how far a road keeps from a place | `RoadSiteProtection.Clearance`, `RoadSiteProtection.BlocksSegment`, `RoadSiteProtection.BlocksRoute`, `RoadSiteProtection.BlocksRoad`, `RoadSiteProtection.BlocksFinalSegment` | `RoadConstants.TerrainBlendMargin` | A road past a place asks `RoadSiteProtection`; it does not measure a ring itself. |
| A place's ring, doors and keep-out edge | `RoadNetworkGenerator.KeepOutEdge`, `RoadNetworkGenerator.DoorRing` | none | The door ring and the keep-out edge are read from here. |
| A place's landing, flat and ground as it will leave it | `PlacedSiteShape.LandingFor`, `PlacedSite.LandingHeight`, `PlacedSites.FlatFor`, `PlacedSites.GroundAt`, `PlacedSites.ExpectedGround` | `PlacedSiteShape.JoinApart` | A road's height at a place is `LandingHeight`; never re-sample the place. The ground a place the game placed (not placed first) leaves is `ExpectedGround` (vertex-exact). |
| Ways into a place, and twin ways | `PlaceWaysIn.Of`, `PlaceWaysIn.SameWay`, `PlaceWaysIn.Twins` | none | One record of the ways in; readers do not rebuild it. |
| Places that are entered from the front only | `PlacedSiteShape.FrontOnly` | none | Add the place to its list. |
| Staircase starts: the ring, the pads and the feet | `RoadEndRing.Starts`, `RoadEndRing.Allows`, `RoadStairRuns.StartPads`, `StairLayout.LowerFoot` | `RoadEndRing.PadUnderFloor` | A foot meets its road within a step (`StairLayout.Walkable`). |
| A height rounds to whole stairs: a foot, an end's stairs, a road arriving level with a place | `StairLayout.WholeStairsOver`, `StairLayout.StairFitsFrom` | `StairLayout.PieceRise` | The nearest whole stair: off by less than half a rise no stair fits (a foot's landing is filled to it; a road's end is level with its place). Read `StairFitsFrom`; never write half a rise. |
| Staircase ends: reading the drop, and finishing the top | `StairEnds.Drops`, `StairEnds.Choose`, `StairEnds.Corners`, `StairEnds.OverAhead`, `StairLayout.FinishTop` | `StairLayout.TopFloorBand`, `StairLayout.EndReach` | The end reads the ground at its two corners; do not add a second reading point. |
| Is this edge walked across (rails, stair ends) | `StairEnds.Walks`, `StairRailing.WalkedOn` | `StairLayout.TopFloorBand` | `StairEnds.Walks` is the test; the railing and the walk graph both call it. |
| Step and stair geometry: rise, run, a walker's step | `StairLayout.PieceRise`, `StairLayout.PieceRun`, `StairLayout.StepUp`, `StairRailing.LedgeDrop` | `StairLayout.PieceRise`, `StairLayout.PieceRun`, `StairLayout.StepUp` | Bridges and viaducts read the same constants as stairs. |
| A player walks between two heights: a road (or a shared landing) and a staircase, floor, structure end or walkable block | `StairLayout.Walkable`, `StructureEnds.SharesLanding` | `StairLayout.StepUp` | Two held ends share a level landing at their common height (`StructureEnds.LandingHeight`) only where each is within a step of it. Call `Walkable`; never compare a height difference with `StepUp` inline. `WalkOwnerTests` lists every other reader of `StepUp` and fails on a new one. |
| A staircase's foot meeting a road held at a height (a crossing's end), and a clipped top-to-top join meeting the top under it: within a step, else a stair sunk into the road or that top's floor | `StairLayout.FootToRoad`, `StairLayout.FootMeets`, `StairLayout.SinkStep`, `StairLayout.SunkStair`, `StairLayout.SunkStairSpans`, `StairLayout.ClipTops` | `StairLayout.StepUp`, `StairLayout.PieceRise` | The flex: a foot (or a clip's floor) more than a step over the road (or the top floor it meets), under a rise (`SunkStairSpans`), gets one more stair sunk into it, laid by `SunkStair`, the one place a sunk stair is laid (`SinkStep` under a foot; `ClipTops` into the lower top's 2 m row, the run ending on its far edge in line and centred; never over a top stair); `FootMeets` judges a foot. Structure ends' rows sit on the road and are never sunk. |
| End steps of bridges, viaducts and stairs meeting a road | `StructureEnds.StepCentre`, `StructureEnds.StepJoint`, `StructureEnds.StepBase`, `StructureEnds.StepFoot`, `StructureEnds.MeetEnds` | `StructureEnds.ApronLength` | Keep each helper's float expression whole. |
| Railings and parapets | `StairRailing.Lay`, `StairRailing.LayParapet`, `StairRailing.Railed` | none | A rail is left open only where the walk carries on. |
| Supports, foundations and burial | `StairSupport.Choose`, `StairSupport.Bury`, `BuriedPieces.Buried`, `SupportGraph.Settle` | `SupportGraph.DistanceMargin` | One stability model (`SupportGraph`) for stairs, bridges and viaducts. |
| Bridge and viaduct structure | `BridgeLayout.DeckHeight`, `BridgeLayout.IsFreeSpan`, `BridgeStructures.Solve` | `BridgeLayout.DeckHalfWidth` | |
| The ground under a deck: below its underside, every kit and the viaduct | `BridgeStructures.GroundUnderDeck`, `BridgeStructures.DeckDepth` | `BridgeLayout.DeckDepth`, `BridgeLayout.StoneDeckDepth`, `DvergrWoodBridge.DeckDepth`, `DvergrViaduct.FloorDrop`, `RoadCrossingDetector.DeckLineStep` | A kit's deck depth is a case in `DeckDepth`; the ground under a deck is cut there (stored as the road's crossing cuts), never levelled as road. |
| Structure kit by biome | `StairKit.LeansTo`, `StairKit.MistlandsKitBelongs`, `StairKit.StoneOverWoodBelongs` | `StairKit.BiomeShare` (0.4) | One share rule for staircases and bridges: on a biome edge a structure leans to the more advanced kit once 40 % of it belongs there. A new kit adds a "belongs" test here. |
| Is this ground water | `RoadWater.Of`, `RoadWater.Wet`, `RoadWater.AtOrOverSea`, `RoadWater.AtLeastDryFloor`, `RoadWater.BuiltDry`, `RoadWater.OnLand` | `RoadConstants.SeaLevel`, `RoadConstants.DryRoadFloor`, `RoadConstants.DeepWaterHeight`, `RoadConstants.ShallowWaterHeight` | Ask `RoadWater`; do not compare a height with the sea level. |
| Ford or bridge | `RoadCrossingDetector.IsFord`, `RoadCrossingDetector.IsFordJump` | `RoadConstants.FordWadeDepth`, `RoadConstants.SwampFordMaxFairway` | The search and the builder both ask `IsFord`. |
| The longest ford and bridge span | `RoadConstants.MaxFordSpan`, `RoadConstants.MaxBridgeSpan` | `RoadConstants.MaxFordSpan`, `RoadConstants.MaxBridgeSpan` | Never multiply the cell count by the cell size by hand. |
| Ground readers: natural, planning, finished | `BiomeBlendedHeight.GetBlendedHeight`, `RoadStairRuns.PlanningGround`, `RoadStairRuns.FinishedGround`, `RoadTerrainModifier.FinishedGround` | none | The game's blended ground is the only natural ground; `WorldGenerator.GetHeight` is not. |
| What counts as reached | `RoadNetworkGenerator.Reached`, `RoadNetworkGenerator.EndReached`, `RoadNetworkGenerator.LandingReached` | `RoadNetworkGenerator.LandingReach` | Arrival at a place is `Reached`. |
| Joining a network | `RoadNetworkGenerator.JoinTo`, `RoadNetworkGenerator.JoinNetwork`, `RoadNetworkGenerator.JoinTargets`, `JoinHold.Reason` | none | A manual road joins through `JoinTo`, as generation does. |
| Which places get a road and which connect | `RoadNetworkSelection.Quota`, `RoadConnectionPlanner.Connect`, `RoadCoastalLandings.Priority` | none | Priority and quota live in the selection, not at the call site. |
| The saved network: what is stored, read back and versioned | `RoadNetworkPersistence.SaveGlobalRoadData`, `RoadNetworkPersistence.TryLoadGlobalRoadData`, `StairLayout.LayoutVersion`, `BridgeLayout.LayoutVersion` | `StairLayout.LayoutVersion`, `BridgeLayout.LayoutVersion` | A change to stored geometry bumps the layout version of the type that lays it. |
| Placing pieces into zones, and whether a zone's structure is started | `StructurePlacement.OnZoneSpawned`, `StructurePlacement.SpawnInZone`, `StructurePlans.Started`, `StructureAppendQueue.Prepare` | none | Ask `StructurePlans.Started`; do not test the spawned or owed state yourself. |
| Terrain settled, and whether the world is held | `RoadTerrainModifier.TerrainSettled`, `RoadRockClearing.TerrainWritten`, `RoadLifecycleManager.HoldsTheWorld`, `GhostRepairLedger.DueAt` | none | Joins and passes wait on `HoldsTheWorld`. |
| Which zones the mod may write | `NetworkCutoff.Excludes`, `NetworkCutoff.Avoids` | none | A writer asks the cutoff. |
| Hidden place pins | `HiddenSiteIcons.Hide` | none | One owner. |
| The explored-world question at first load | `ExistingWorldFirstLoad.Decide` | none | One owner. |
| Island detection | `RoadIslandDetector.Detect` | `RoadConstants.WorldRadius`, `RoadConstants.WorldEdge` | The edge of the world is `WorldRadius` / `WorldEdge`. |
| A road riding or lying beside another | `RoadRide.Around`, `RoadBeside.At`, `RoadRide.Reach` | `RoadBeside.OnRoad` | Reach reads the road width. |
| Trench and ridge | `RoadNetworkGenerator.HeldRidges` | none | The ridge rise is the shaped limit. |
| Weathering and ruin of stairs and bridges | `StairWear.Weather`, `BridgeLayout.SpanSurvival`, `BridgeLayout.LocationHealth` | none | Rolls are seeded per site; see the hash row. |
| Seeded hashing of cells | `RoadPathfinder.HashCell` | none | One cell hash; do not write another mixer. |
| Zone, grid and cell arithmetic | `RoadConstants.ZoneIndex`, `SharedTerrain.FloorDiv`, `RoadPathfinder.CellCentre` | `RoadConstants.PathfindingCellSize` | Never write `floor(x / 64)`. |
| Server work: concurrency and the loading hold | `RoadParallel.IslandWorkers`, `ServerBakeLoadScreen.Fraction` | none | One worker count; one progress fraction. |

## Literals

`OwnerLiteralTests` scans the code under `ProceduralRoads/Src` (comments and strings removed) for the numbers of these key
decisions. A match outside the allow-lists in that test fails the gate. Each allow-list entry carries the reason it is a
different fact. A known duplicate is listed there too, so the test passes today, and goes the moment it is fixed.

| Constant | Owner | Checked how |
|---|---|---|
| Earthwork cap 8 m | `RoadConstants.TerrainDeltaMax` | an 8 on a line about earthwork, cap, cut, fill or batter |
| Step 0.2 m | `StairLayout.StepUp` | 0.2 on a line about steps, rises, stairs, walking or ledges |
| Half a rise | `StairLayout.StairFitsFrom` (the nearest whole stair, with `WholeStairsOver`; a step's middle is `StairLayout.StepMiddle`) | `PieceRise * 0.5` or `PieceRise / 2` |
| Rise 1 m | `StairLayout.PieceRise` | 1 on a line naming a rise |
| Run 2 m | `StairLayout.PieceRun` | 2 on a line about a run, stair, step or piece that does not name `PieceRun` |
| Headroom 2 m, lift 0.1 m | `RoadRockPolicy.ClearanceHeight`, `RoadRockPolicy.ClearanceLift` | 2 on a clearance or box line; 0.1 on a clearance, lift, bottom or rock line |
| Sea level 30, dry floor 30.5, deep water 28 | `RoadConstants.SeaLevel`, `RoadConstants.DryRoadFloor`, `RoadConstants.DeepWaterHeight` | the bare number anywhere |
| Paint edge ratio 0.85 | `RoadConstants.RoadPaintOuterRatio` | the bare number anywhere |
