# A network saved by a released build

`global-data.bin.gz` (gzip of the `ProceduralRoads_GlobalData` record) and `start-points.bin` (the
`ProceduralRoads_StartPoints` record) come from the two records a world's road metadata object held after a build of
ProceduralRoads at its 1.4.3 version generated and saved the world's roads, on Valheim 0.221.12. That build's mod source
is identical to the upstream 1.4.3 version commit; 1.4.4 reads and writes the same format. Nothing else of the world is
kept.

`start-points.bin` is the record unchanged. The network record is format 1, the only format a released build (1.4.x)
reads: `int32 format` (1), `int32 cell count`, then per cell `int32 x, int32 y, int32 point count` and per point four
`float32` (x, z, width, height). The saved record held 697 grid cells and 39858 points (646100 bytes, SHA-256
`4cb4af314dfda52333db58d8155c1eaa57f31f0241f0cc7f03276f837440f24f`). It is cut down to its first 4 cells as saved,
232 points (3768 bytes): the cells' bytes are the saved ones, in the saved order, and only the cell count is rewritten.
This build has no format-1 writer, so the record stays the released build's own encoding, never one this build wrote.
The saved record has no point a reader drops (non-finite or no width) and no cell listed twice, so the cut leaves out
no case the full one held. A 1.4.x network has no other records (no crossings, shore landings or structures).

What is not established: the save was made by a local build of that source, not by the downloaded release package, and
not by 1.4.4 itself. A load, save and reload of a world saved by the published 1.4.4 package is still owed in game.

`ReleasedSavePolicyTests` loads these records through the real reader and checks that no save path rewrites them and
no road is added. Never regenerate these files from this build's code.

SHA-256 of the uncompressed records are in `released-network.sha256`.
