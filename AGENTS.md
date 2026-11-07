# Working on ProceduralRoads

## One owner per decision

- Before you add or change a check, threshold or rule, find its decision in [docs/OWNERS.md](docs/OWNERS.md) and call that owner. Do not write a second copy of the rule or its number.
- A new decision adds a row to docs/OWNERS.md in the same commit. Name the owner `Type.Member`, never `file:line`.
- A new number (a threshold, a limit, a margin) needs the maintainer's approval first. Say why no existing owner covers it.
- `OwnersDocTests` fails when a listed owner is renamed or deleted. `OwnerLiteralTests` fails when a key constant's number is written outside its owner.

## Floats

- Keep a stored float expression whole. The game's Mono rounds once, at the store, so splitting `a * b + c` into helpers or temporaries can change the result.
- `x >= k` is not `!(x < k)`: they differ when `x` is NaN. Keep each comparison as written.

## Checks

Build with 0 warnings and run the test classes your change touches. The full suite runs at integration.
