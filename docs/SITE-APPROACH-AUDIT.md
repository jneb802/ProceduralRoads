# Offline hillside approach audit — 18 September 2026

The user accepted the latest live frost-cave approach, including its remaining small natural bump. Preserve that build and the protected site terrain. This audit did not touch Valheim, the running world, plugins or the character.

## What was tested

Production approach/profile code at `85b10db`, using five archived 8 m world dumps (RoadSeedA–E). The tool links the real source through the test assembly. For each of six POI name families, it selects the four sites with the worst height mismatch among four cardinal approach directions. Each receives a hypothetical straight approach; this is a terrain stress screen, not replay of actual generated roads. All dumped location exterior radii participate in protection. Ashlands and Deep North are excluded.

The target is procedural ground at the POI centre because these dumps lack placed roots and authored modifiers. Accordingly, these cases demonstrate the algorithm's broader potential, **not that these particular locations would be eligible or served this way in game**. The actual cave's successful exterior reference came from its saved root; an 8 m dump cannot reproduce that fully.

## Results

| Family | Difficult approaches tested | Changed to a closer-height arrival |
|---|---:|---:|
| dungeon | 20 | 7 |
| frost cave | 20 | 2 |
| settlement | 20 | 6 |
| tar pit | 20 | 5 |
| tower | 20 | 3 |
| troll cave | 20 | 1 |

**24 of 120 changed; 96 retained the original input.** Across the changed cases, mean endpoint-to-reference height difference fell from **18.02 m to 0.56 m**. All 24 had a valid final profile, grade at or below 35% within floating-point tolerance, arrival within 1.5 m of the reference, and no segment entering the indexed footprints plus 4 m clearance.

Mean width-sampled terrain change fell from **3.95 m to 2.13 m** across changed cases. However, **10 of 24 increased that individual measure**: the selection score balances earthwork against reaching the site at the right height. Some absolute cuts/fills remain large. Do not describe all changed cases as prettier, low-earthwork or manually walkable.

Of the 96 unchanged inputs, **50 could not form the initial straight tail within the grade cap**. The production helper cannot score that starting profile and leaves it alone. The other 46 found no qualifying replacement. This is not 96 lost in-game connections: the main route search would not necessarily offer these hypothetical straight approaches. It does establish that the local helper cannot rescue every difficult approach.

## What applies beyond caves

- The approach algorithm is not cave-specific. It can find better-height arrivals for other terrain-bearing locations when production has a usable common level and the bounded search finds a valid alternative.
- Below-root excavations no longer drag the exterior arrival down into their cut; higher common platforms can still request a raised arrival.
- Footprint protection covers every known site, selected for roads or not. It prevents terrain/paint writes inside protected locations and routes through-roads around them. Tar pits principally benefit from this protection; a successful hypothetical tar-pit arrival here does not mean the network selects tar pits as destinations.
- None of this erases damage already baked by an older network, guarantees a clear doorway, or checks cave rocks. Those require appropriate runtime evidence.

## Validation and evidence

All five audit processes exited 0. The reporter labels unchanged cases as having no replacement check, rather than awarding them a pass. A second audit with that reporting clarification preserves every route-change decision and metric. The focused approach/protection suite passed **21 tests, zero failures/skips** on .NET 10. No production routing, terrain or deployment code changed during this audit.

`tools/site-approach-audit/` contains the reproducible harness and method. Adjacent `site-approach-audit-20260918/` evidence contains the per-site CSV, source/input hashes and process exit records. Original full logs remain in the local run directory; no game dump is committed.

## Follow-ups, not blockers to the accepted cave

Leave the small bump intact. If future visual examples justify more work, first compare approach candidates' natural connection to the entrance with reliable entrance/collision data, rather than grading the protected interior. A larger main-route reroute for cases the local helper cannot rescue is a separate feature, not a reason to keep changing this accepted cave.

Large natural mountain-rock clearance is recorded in `ROAD-FOLLOWUPS.md`; POI-authored rocks must stay protected.
