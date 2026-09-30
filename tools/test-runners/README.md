# Test runners

`run-tests.sh` and `run-tests.ps1` are copied unchanged from [ValheimTesting's tools/test-runners](https://github.com/tvongaza/ValheimTesting/tree/main/tools/test-runners). They run a test project for `net10.0` and for `net48`, report each and fail if either fails. The `net48` leg needs Mono on macOS and Linux.

```sh
tools/test-runners/run-tests.sh ProceduralRoads.Tests/ProceduralRoads.Tests.csproj
```
