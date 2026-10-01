# Approach evaluation performance — 18 September 2026

## Change

Reject curved arrival candidates outside the existing 1.5 m height tolerance before constructing and sampling their profiles. Reuse exact-coordinate procedural heights across trial profiles within one approach decision. Keep authored location ground in a separate cache: it is not interchangeable with procedural terrain.

Each cache retains at most 16,384 entries; new points beyond capacity are computed without retention. Neither cache is static or survives the decision. Candidate order, search budgets, scoring, grade limits and protection rules are unchanged.

## Comparison

Baseline production source is `2c2b9cc`. Baseline and candidate use the same instrumented audit tool and the existing RoadSeedA–E dumps, selecting the same 24 difficult hypothetical approaches per world. The tool measures `Improve` alone, excluding dump loading and post-run profile measurements. It compares exact control-point hashes, changed/unchanged decisions and all before/after profile metrics.

The first pass, without concurrent builds/tests, measured:

| World | Baseline seconds | Candidate seconds | Height queries before → after | Managed bytes allocated before → after |
|---|---:|---:|---:|---:|
| A | 1.470 | 1.635 | 202,309 → 79,951 | 123,563,016 → 95,546,920 |
| B | 0.523 | 0.121 | 71,431 → 21,035 | 22,907,168 → 8,339,984 |
| C | 2.117 | 1.255 | 146,947 → 41,838 | 96,605,336 → 62,435,448 |
| D | 2.281 | 1.039 | 269,388 → 114,769 | 164,641,864 → 124,667,864 |
| E | 0.968 | 1.060 | 150,628 → 38,639 | 112,536,368 → 81,448,864 |

Across 120 cases: 7.360 → 5.109 seconds (31% less elapsed approach time), 840,703 → 296,232 height queries (65% fewer), 859,036 → 201,932 biome queries (76% fewer), and 520,253,752 → 372,439,080 managed bytes allocated (28% fewer). All routes and checked profiles are unchanged. Two individual worlds were slower; timings are short, include JIT/GC effects and were taken with the game still open. They are observations, not performance guarantees.

A second pass reverses baseline/candidate order. Its later timings overlap verification work and must not be used as isolated speed measurements; deterministic route and query-count comparisons remain useful. Both passes completed successfully: all 120 route hashes, decisions and profile metrics matched in each pass, and both sets of query counts reproduced exactly.

## Limits

This measures local approach evaluation on sampled terrain, not a whole generated network. It does not establish the effect on pricing/main route searches, terrain baking or arrival in the game. The separate whole-island measurement below did not show a meaningful speedup. Allocated bytes are cumulative managed allocations, not retained memory or peak process memory. A dump height query is much cheaper than Valheim's procedural terrain query, so neither offline seconds nor their ratio predicts the in-game gain.

## In-game whole-island check

A Mac Valheim 1.0.15 run regenerated island 5 from the same RoadSeedE POI fixture at the baseline settings (100,000 search iterations, 30 locations per island, maximum grade 0.35). The candidate was the integration baseline plus only these approach changes; rock-clearing changes were excluded from the comparison.

**608.1 → 606.7 seconds: 1.4 seconds, or 0.23% faster. This is effectively unchanged**, based on one earlier baseline and one candidate run. Both report 9 roads, 6,215 m, network version 1627085781, 6,297 points and 138 cells. Matching summary values and the network version are not a byte-for-byte saved-network comparison.

The approach-only reduction in sampling does not establish an end-to-end performance gain. Further work should measure the remaining route-search cost before promising shorter generation times.

## Validation

The full PR suite passes 180 tests on .NET 10 and .NET Framework 4.8 under Mono, zero failures/skips and real exit 0. Release build is clean. Four new tests cover early height rejection, exact profile reuse, separation and reuse of authored/procedural samples, and bounded cache retention. Restoring the late height check fails its regression; removing the shared profile sampler fails the reuse regression. Each control was run once and the final source restored.

[Per-world aggregate measurements](approach-performance-20260918.json) retain both run orders, including the noisier second-pass timings. Full per-site comparisons were checked by identity and exact route hash, not by totals alone.

## Search follow-up

The subsequent [route-search optimization](SEARCH-PERFORMANCE.md) removes repeated terrain queries and improves grid lookup hashing. It measured roughly 3x faster generation offline with identical serialized roads, and 606.7 → 243.2 seconds (2.49x faster) on the same in-game island fixture. This is separate from the approach-only optimization measured above.
