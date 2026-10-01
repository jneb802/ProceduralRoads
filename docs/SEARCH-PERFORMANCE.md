# Route-search performance — 18 September 2026

## What was expensive

A disposable instrumented copy of the combined branch separated route searches from switchback shaping. On island 5 in the RoadSeedE dump, it measured 144 point-to-point searches (72 pricing probes and 72 other searches), plus 42 searches for an existing road. These accounted for 62.0 of 62.4 seconds inside island road generation. Switchback shaping plus separation took 0.019 seconds.

This profile used the terrain-cache improvement before the footprint-index improvement. Its timings overlapped verification work and are for attribution, not the speedup comparison below. The dump lacks live location-levelling descriptors: the curved nearby-approach trials did not run. Therefore this is evidence that direct switchback calculation is cheap in this fixture, not a complete measurement of in-game POI approach cost. Rejected switchbacks can still cause expensive alternative route searches.

## Implementation

Port the existing network-performance terrain cache into this branch. Cache exact grid-cell height, river weight, biome and terrain variance, reset at the start of every search. A fixed 16,384-slot array holds approximately 448 KiB of value data per pathfinder; collisions replace entries and never approximate values. Configurable costs, protected-site decisions and road-network state are not cached.

Use a coordinate comparer in search dictionaries/sets and the POI-footprint index. Valheim's `Vector2i` uses `x ^ y`, concentrating diagonal cells into shared hash buckets. The new comparer preserves equality while distributing those keys. Update the test double to match the installed game's equality/hash first, so the baseline models the same issue.

Routing budgets, candidate order, costs, grade limits, switchback clearance and POI protection are unchanged. No rock-clearing changes are included.

## Sequential offline comparison

Both versions compile the production sources with the same game doubles and CSV world adapter. Settings: 100,000 search iterations, 30 locations per island, 4 m roads and maximum grade 0.35. The final candidate ran first and the baseline second, with no concurrent builds/tests. Valheim remained open, so these are practical observations rather than controlled benchmark guarantees.

| Measurement | Baseline | Candidate |
|---|---:|---:|
| Road-generation timer | 128.5 s | 43.5 s |
| Whole regeneration call, including detection | 131.05 s | 46.01 s |
| Height queries | 381,302,328 | 99,121,123 |
| River queries | 46,643,141 | 16,468,083 |
| Biome queries | 95,964,626 | 84,728,793 |
| Cumulative managed allocation | 1,563,865,920 bytes | 1,564,330,648 bytes |

Approximately **3x faster road generation**, with 74% fewer height queries. Total managed allocation is essentially unchanged; this is not a claim of lower peak memory. The bounded cache adds a small fixed allocation.

Both versions produced exactly the same 178,028 serialized road bytes (SHA-256 `CF7F3C79BD8C946EF60345864FCFC9FCD03A5AB5B269B9D0E64394D13192BE86`), 11 roads, 7,795.3257 m, 7,909 points and 164 cells. The dump run does not reproduce the live fixture's nine roads: it uses rounded POI positions, sampled/interpolated terrain and lacks live levelling data. It verifies implementation equivalence on this input, not equivalence of the dump and game worlds.

The preliminary baseline ran alongside build/test work and took 165.4 seconds; it is retained in the private evidence but excluded from the speedup claim. Its deterministic query counts and network bytes match the final baseline.

## Validation

PR suite: 188 tests on .NET 10 and .NET Framework 4.8/Mono, no failures or skips; Release build has no warnings or errors. Combined candidate: 390 tests on both runtimes, no failures or skips, clean Release build. A negative control bypassing the cache in the real pathfinder makes its regression fail (16 origin reads instead of the allowed 1–4). Other regressions cover cache collisions, reset after failed searches, thrown reads, unused facts, bounded storage and coordinate equality/distribution.

## In-game comparison

The combined candidate was then deployed and hash-verified on the Mac, running Valheim 1.0.15. A fresh copy of the same RoadSeedE POI fixture was used; every non-comment config setting matched the prior run, including the 100,000 iteration budget, 30-location cap and 0.35 grade limit. Automatic generation was suppressed and only island 5 regenerated.

**Road generation: 606.7 → 243.2 seconds — 2.49x faster, 59.9% less time, saving just over six minutes.** Both runs report 9 roads, 6,215 m, network version 1627085781, 6,297 points and 138 cells. This is matching in-game fingerprint/count evidence, not an in-game byte-for-byte network export. The offline comparison separately establishes serialized-byte identity on its input.

Island detection took 13.3 seconds in the candidate run and is outside the quoted road-generation timer. Loaded terrain queues differed (six zones versus five); that work follows the timed generation. This is one measured same-fixture comparison, not a guarantee for other worlds or systems. The earlier 608.1 → 606.7-second result covers the preceding approach-only optimization and remains valid.
