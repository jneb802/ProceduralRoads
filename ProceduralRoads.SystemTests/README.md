# Roads system-test pilot

Assistant-written; reviewed by the maintainer.

Optional development tooling, outside the normal Roads release; the production mod has no dependency on it. Roads adopts the shared [ValheimTesting](https://github.com/tvongaza/ValheimTesting) toolkit: this file covers only what Roads tests, how to run it and what a pass shows. Generic behaviour (runner lifecycle, pins, isolation, launch, client rounds, adapter helpers) is documented once in the toolkit and linked below. The [FullLifecycle example](https://github.com/tvongaza/ValheimTesting/blob/main/examples/FullLifecycle/README.md#what-belongs-to-the-mod-and-what-to-the-toolkit) shows the same split between a mod and the toolkit.

## Projects and packages

| Project | Target | What it holds | Package |
|---|---|---|---|
| `ProceduralRoads.Tests` | net48 and net10.0 | The existing xUnit suite, Roads-only doubles (`Shims`: `ModShims`, `PluginShims`, `RoadsHeightmap`) and mod assertions | `Valheim.Testing` 0.1.0-preview.7, `Valheim.Testing.Doubles` 0.1.0-preview.6 (source package) |
| `ProceduralRoads.SystemTests` | net10.0 (`RollForward` Major) | The Roads plan, scenarios and oracles on the toolkit's pinned server runner | `Valheim.Testing.Game` 0.1.0-preview.14, which pins `Valheim.Testing.Cli` 0.1.0-preview.5 |
| `ProceduralRoads.SystemTests.Tests` | net10.0 | Plan rules, scenario flows against scripted transports, and the terrain and paint oracles with their negative cases | through the runner project |
| `ProceduralRoads.TestAdapter` | net48 | The game-side adapter plugin: the Roads commands below | `Valheim.Testing.Adapter` 0.1.0-preview.2 (source package) |

All packages restore from NuGet.org. To try an unpublished toolkit build, use a local feed as in [Package versions and feeds](https://github.com/tvongaza/ValheimTesting/blob/main/docs/getting-started.md#package-versions-and-feeds). `ProceduralRoads.Testing.slnx` groups the three test projects for an IDE ([IDE notes](https://github.com/tvongaza/ValheimTesting/blob/main/docs/getting-started.md#using-visual-studio-rider-or-vs-code)); the adapter is deliberately outside it.

## Fast checks (no game)

From the repository root:

```sh
dotnet test ProceduralRoads.Tests/ProceduralRoads.Tests.csproj -c Release -f net10.0
ProceduralRoads.Tests/run-tests.sh ProceduralRoads.Tests/ProceduralRoads.Tests.csproj
dotnet test ProceduralRoads.SystemTests.Tests/ProceduralRoads.SystemTests.Tests.csproj -c Release
```

`run-tests.sh` is the toolkit's [test runner](https://github.com/tvongaza/ValheimTesting/blob/main/tools/test-runners/README.md#run-it), copied unchanged: it runs both the net10.0 and net48 legs (net48 under Mono on macOS and Linux) and fails if either fails. On Windows, plain `dotnet test` also runs both legs.

Roads-specific unit coverage that uses the shared terrain model ([shared-world guide](https://github.com/tvongaza/ValheimTesting/blob/main/docs/shared-world.md)):

- `SyntheticWorld` delegates to `Valheim.Testing` terrain; the grade and search-margin tests build planes and composed causeways from the shared `PlaneTerrain` and `CompositeTerrain`.
- `SharedZoneWriterTests` runs the real Roads writer on shared multi-zone height and paint state: both zone orders meet at the edge, keep an earlier verge edit and paint, and are unchanged on repeat.
- `TerrainPaintMatrixTests` runs eight real-writer cases: positive and negative cross-slopes, fill and cut, the ±8 m clamp, 2/4/8 m widths, dirt and paved regions, paint-only roads, pre-existing RGBA and ordinary-load preservation. Every vertex across the road is declared: the core is levelled to the road height, the vertex one metre into the 2 m release moves half way, and the ground beyond is untouched. Earthwork noise, fill spread and cut batter are disabled in this matrix; their own unit tests still apply, and nothing here shows their native appearance.

These tests exercise Roads decisions and the writer on declared terrain, not Unity terrain, RPCs or save encoding.

## Build the runner and the adapter

```sh
dotnet publish ProceduralRoads.SystemTests/ProceduralRoads.SystemTests.csproj -c Release -p:UseAppHost=false -o <new-runner-output>
dotnet build ProceduralRoads.TestAdapter/ProceduralRoads.TestAdapter.csproj -c Release -p:CliDll=<runtime>/BepInEx/plugins/valheimCLI.dll -p:RoadsDll=<path>/ProceduralRoads.dll
```

`CliDll` has no default and the build stops without it: point it at the exact ValheimCLI core installed in the test runtime. `RoadsDll` defaults to this repository's Debug build. Game references come from Roads' own `environment.props`. The adapter has embedded symbols and no deployment target.

Install into the **test runtime** only: BepInEx, the matching ValheimCLI core with its Standard and World Tools packs, `ProceduralRoads.dll` and the adapter, all in `BepInEx/plugins`. Never install the adapter on a client.

## The adapter

The adapter registers the extension `roads.testing` through the toolkit's `TestExtension.Register`, which also serves the owned-session identity `roads.testing/session` (complete once the world is up and Roads reports its network available). The runner supplies `ROADS_TEST_SESSION_TOKEN`. It uses the toolkit's adapter helpers (`ZoneTerrain`, `Members`, `FixtureGate`); see [adapter helpers](https://github.com/tvongaza/ValheimTesting/blob/main/docs/testing-toolkit.md#adapter-fixture-command-and-harmony-helpers-valheimtestingadapter-preview-2).

| Command | Kind | What it does |
|---|---|---|
| `network` | read-only | The in-memory network: completion, cells, points, crossings, pending bridge-append zones and whether it was loaded from the save |
| `bridge-zone <zoneX> <zoneZ>` | read-only | Every saved object in one zone that carries the Roads bridge marker, with prefab, position and rotation; zones within ±255 only, and more than 512 marked pieces fails instead of truncating |
| `terrain-calibrate` | fixture | The declared two-zone calibration below; refused unless `ROADS_TEST_TERRAIN_CALIBRATION=1` and a session token are set |
| `terrain-persist` | fixture | Writes the persistent two-zone fixture below; refused unless `ROADS_TEST_PERSISTENT_TERRAIN=1` and a session token are set; once per process |

Both fixture commands also require a dedicated server with no peers and a completed, empty road network. They are never production road commands.

## Scenarios

A plan is the toolkit's pinned server plan plus the Roads fields. The runner is `PinnedServerRun`; for the plan rules every pinned plan follows (strict `pins`, `runtimePins`, `patchers`, `"pinning": "none"`, `-savedir {world}`, the token and Doorstop variables, `{world}`/`{runtime}`/`{port}` expansion) see [Pinned server runner](https://github.com/tvongaza/ValheimTesting/blob/main/docs/testing-toolkit.md#pinned-server-runner) and [Pins and the opt-out](https://github.com/tvongaza/ValheimTesting/blob/main/docs/testing-toolkit.md#pins-and-the-opt-out). Roads adds:

- `pins` must include `warpalicious.ProceduralRoads`, `valheimCLI.valheimCLI` and `testing.proceduralroads.adapter` (plus every other loaded plugin, as strict pins require).
- `runtimePins` is required; a runtime with a BepInEx preloader patcher (for example one that includes ServersideQoL) must name it in `patchers`.
- `scenario` is one of `empty-save`, `bridge-respawn`, `terrain-calibration`, `terrain-persistence`. Fields another scenario reads are refused.

### empty-save

Requires an existing nonempty network in the saved world, while the runtime's Roads config selects zero islands. The scenario runs `road_generate` once, waits for a complete empty result, requires a confirmed save, restarts only the owned server and requires an empty network **loaded from the save**, not regenerated.

### bridge-respawn

Plan fields: `append`, exactly one `road_path X,Z X,Z ...` command with finite pairs, and `expected`, independently frozen bridge pieces per zone, for example `[{"x":0,"z":0,"pieces":[{"prefabHash":123,"position":[1,2,3],"rotation":[0,0,0,1]}]}]`. Include every marked piece already in each listed zone.

The scenario requires no pending append, appends once, requires the append to still be pending (an append that already drained fails the fixture check; it is not a pass or skip), runs `road_bridges respawn` once, waits for the queue to drain, compares the marked pieces, confirms a save, restarts and compares again. Matching uses prefab, full 3D position, rotation angle and multiplicity (5 cm, 1 degree) and assumes no particular piece count.

The fixture must already have generated every zone the crossing occupies; the scenario does not drive a player or choose a crossing. For an independently chosen crossing, `prepare-bridge` generates the listed zones in a fresh copy (60 s per zone) and confirms a save. That is fixture preparation, not acceptance: freeze and hash the resulting save root as a separate world input before the real run.

### terrain-calibration

Set `ROADS_TEST_TERRAIN_CALIBRATION=1` in the plan's `environment` and start from an already-empty saved network; zones (200,200) and (201,200) must be ungenerated and unloaded.

The adapter creates a vanilla non-player square level modifier at height 64, two real zone heightmaps with ghost terrain compilers, and a 4 m road across their shared edge, applying targets 65, 65 again, 80 and 48. Earthwork noise, fill spread and cut batter are disabled only during this synchronous fixture and restored afterwards. The declared samples (`ProceduralRoads.TestFixtures/FlatRoadFixture.cs`) cover the centre, the 2 m flat edge, the 3 m half-strength release, the 4 m outer edge and an untouched 6 m control; targets beyond the ±8 m range must stop at 64 ± 8. Each sample is read from the heightmap **and its own collider**, on both copies of the zone boundary. The runner requires every declared sample exactly once and each layer within 2 cm. Nothing is saved: the objects and the ghost compiler ZDOs are destroyed, and the runner stops its disposable server.

This calibrates the Roads writer and the game's terrain compiler, not the world generator, natural routing, client replication or player support.

### terrain-persistence

Two steps, both with `ROADS_TEST_PERSISTENT_TERRAIN=1` in the plan's `environment`.

**`prepare-terrain`** on a copy of an empty saved network. The adapter picks two ungenerated Meadows zones with no saved terrain compiler, away from location exteriors, and writes a constant-height road across their boundary: width 4 by default, width 8 with `ROADS_TEST_PAINT_PROFILE=dirt-fade`. The core is lifted 1.5 m above the seam ground and never below 31.5 m, so a player on it stands dry (sea level is 30 m). Before writing it seeds saved paint entries with RGBA (0.2, 0.4, 0.6, 0.3) and captures the native pre-write heights and paint. Not finding a site is a failed preparation.

The runner's independent oracle expects full blend through the half-width, half one metre beyond, none from two metres beyond, and the ±8 m limit: 20 server samples at width 4, 24 at width 8, heightmap and collider, both boundary copies, with at least one meaningful change. Paint is sampled 2 m either side of the seam (paint has 64 cells per zone, heights 65 vertices): at width 4, eight core texels change colour, eight verge texels are unchanged and all 16 keep their alpha; dirt-fade expects 0.352 strength at 3 m. Both profiles stay clear of the 1000 ± 100 m dirt-to-stone handover, so each sample's colour is unambiguous. Preparation writes the inputs and residuals, `client-height-plan.json` (with a support point on the dry road core) and `client-paint-plan.json`, confirms a save and prints `PREPARED`, never `PASS`.

**`run`** on the prepared world, with these plan fields:

- `client`: the toolkit's client section ([Game clients in system tests](https://github.com/tvongaza/ValheimTesting/blob/main/docs/testing-toolkit.md#game-clients-in-system-tests-preview-12)), `owned` (with `installPins`) or `attach`. Its `pins` must name ValheimCLI exactly and pin `warpalicious.ProceduralRoads` and `warpalicious.More_World_Locations_AIO` as `absent`: the claim is what a client without Roads sees.
- `heightPlan` and `paintPlan`: the prepared client plans, each as `{"source": "<full path>", "sha256": "<hash>"}`, rechecked before use.

The client half is the toolkit's `ClientRounds` ([Plan rules and client rounds](https://github.com/tvongaza/ValheimTesting/blob/main/docs/testing-toolkit.md#plan-rules-and-client-rounds-preview-13)): wait until the server accepts joins, join (which protects the player), arrive on the support point, measure; then a confirmed save, the client leaves, only the owned server restarts, and the client rejoins and measures again. Roads supplies the measurement: every prepared vertex on the client's heightmap and its own collider (15 at width 4, 18 at width 8), the 16 paint samples, and the player standing supported on the dry road core. The client's logs are scanned with the server's at teardown.

## Run

```sh
# Hashes and copies the inputs and checks the plan; never launches a game.
dotnet <runner-output>/ProceduralRoads.SystemTests.dll validate plan.json <new-output-directory>
# Fixture preparation; never counts as acceptance.
dotnet <runner-output>/ProceduralRoads.SystemTests.dll prepare-bridge bridge-plan.json <new-output-directory>
dotnet <runner-output>/ProceduralRoads.SystemTests.dll prepare-terrain terrain-plan.json <new-output-directory>
# The scenario itself.
dotnet <runner-output>/ProceduralRoads.SystemTests.dll run plan.json <new-output-directory>
```

`prepare-bridge` runs only `bridge-respawn` plans and `prepare-terrain` only `terrain-persistence` plans; a `terrain-persistence` `run` without a `client` section is refused. Launch modes need a Windows or Linux dedicated server runtime on a host of the same platform; macOS has none. For the Linux server and its container image see [Linux dedicated server](https://github.com/tvongaza/ValheimTesting/blob/main/docs/testing-toolkit.md#linux-dedicated-server-preview-11) and the [Linux server image](https://github.com/tvongaza/ValheimTesting/blob/main/docker/linux-server/README.md); the Roads plugins go into the copied runtime's `BepInEx/plugins` as above.

For preparing the runtime, world and client, see [Prepare the native run](https://github.com/tvongaza/ValheimTesting/blob/main/examples/FullLifecycle/README.md#prepare-the-native-run); for what the runner stops and never touches, [Cleanup and its limits](https://github.com/tvongaza/ValheimTesting/blob/main/examples/FullLifecycle/README.md#cleanup-and-its-limits); for runtime, character and evidence rules, [runtime hygiene](https://github.com/tvongaza/ValheimTesting/blob/main/docs/runtime-hygiene.md). Use only disposable copies: never a production runtime or a world people play on. Pass `-logFile {runtime}/toolkit-unity.log` to include the Unity log in the [log scan](https://github.com/tvongaza/ValheimTesting/blob/main/docs/testing-toolkit.md#log-scan-at-teardown).

## What a pass shows, and what it does not

- `validate` checks the plan and inputs only; it is not a game result. `PREPARED` is fixture preparation, not acceptance.
- A `run` pass establishes only that scenario's assertions on that pinned runtime and world. The log scan counts warnings but does not certify that every loaded mod started cleanly; read the counts in `result.json`.
- `empty-save` and `bridge-respawn` show that the network, and the marked bridge pieces in the listed zones, survive a confirmed save and a restart of a separate owned process. `TotalRoadPoints` is not a persistence identity: generation adds planned points, bridge levelling does not update it and loading counts every stored grid entry, so never assert its equality across a save.
- `terrain-calibration` shows the Roads writer and the game's compiler agree with the declared profile on the server.
- `terrain-persistence` shows the road's heights, colliders and paint reach a stock client and survive a save, restart and rejoin, and that a player stands on the fixture's road core. It does not cover natural generation, earthwork noise, fill spread or cut batter, pathfinding, walking usability or visual quality.
- Walking and appearance are human judgements: walk a short switchback, a hillside approach to a point of interest and a bridge or ford in both directions. The toolkit's [WalkingReview](https://github.com/tvongaza/ValheimTesting/blob/main/examples/WalkingReview/README.md) records checkpoints beside a separate human verdict.
- The native runs of these scenarios predate the switch to the toolkit's `ClientRounds` and adapter helpers, and no Roads scenario has run on the Linux server yet; repeat a native run before relying on this revision.
