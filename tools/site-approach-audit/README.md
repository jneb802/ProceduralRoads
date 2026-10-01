# Offline site-approach terrain screen

Runs the production `RoadSiteApproach.Improve` and `RoadSpatialGrid.PlanRoadPath` from the test assembly against archived world terrain. Does not launch or modify Valheim.

```
dotnet run --project tools/site-approach-audit/ApproachAudit.csproj -- cells8.csv locations.csv result.json
```

The grid reader is reused from the Roads study's `CsvWorld` implementation. It validates a rectangular grid and interpolates between samples. No routing implementation is copied into the tool.

For each of six name-based families (frost cave, troll cave, tar pit, tower, dungeon, settlement), choose the four locations with the largest height mismatch among four cardinal approaches. Ashlands and Deep North are excluded. Only dry centres with an exterior radius fitting the production 64 m approach limit enter the screen. A straight 192 m approach to the footprint edge is a deliberate stress input, **not a road observed in the generated network**. Each location appears once.

Compare the original and improved final 64 m from the same anchor. Record endpoint height mismatch, grade, mean/worst width-sampled terrain change, length and protected-footprint intersections. Every location in the dump contributes its exterior footprint to the protection index, selected or not. A changed result with an invalid profile, grade over 35%, footprint intersection or arrival mismatch over 1.5 m fails the run. Unchanged results are reported, not called successful fixes. Existing output is never overwritten.

## Limits

These dumps have procedural height at 8 m spacing, not loaded terrain, authored modifier footprints, placed LocationProxy height/rotation, doors, rocks or colliders. The reference height is procedural ground at the centre, **not a measured entrance/platform height**. All listed families are hypothetical approach inputs; runtime only invokes this improvement when it has a usable common authored level. Larger actual modifier footprints can exclude an approach that this screen permits. Tar pits chiefly benefit from protection against through-roads, not necessarily from having their own approach generated.

This identifies broader potential and regression candidates. It cannot establish visual quality, walkability, runtime eligibility, actual connection counts or full-world terrain preservation. Existing-world terrain already modified by older roads is not undone by this tool.

## Comparing approach cost

Each row also records an exact binary control-point SHA-256, elapsed time inside `Improve` only, cumulative managed bytes allocated on that thread, and calls to the dump's height and biome accessors during that decision. Profile measurements happen after the timer and counters are captured. Allocated bytes are not peak or retained process memory, and dump lookup time is not game terrain-generation time.

For a performance comparison, build the baseline and candidate into separate output directories and use the same dumps. Compare route hashes and profile metrics as well as timing and calls; unchanged connection counts alone do not establish unchanged approaches. Reverse run order when repeating timing samples. No game launch is required.
