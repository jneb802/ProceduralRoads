# Run a mod's test project on modern .NET and on .NET Framework, the runtime
# family of the game's Mono, and report each.
#
#   run-tests.ps1 [options] <Tests.csproj> [dotnet test arguments]
#
#   -f, --framework TFM     run only this target framework; repeat for more
#                           (default: net10.0 and net48)
#   -c, --configuration C   build configuration (default Debug)
#   --filter EXPR           dotnet test filter for every framework; for .NET
#                           Framework translated to xunit console options
#   --netfx-parallel MODE   xunit console -parallel for .NET Framework:
#                           none, collections, assemblies, all or default (the
#                           assembly's own setting). Default: none under Mono,
#                           default on Windows
#
# PowerShell twin of run-tests.sh, for Windows PowerShell 5.1 and PowerShell 7.
# Keep the two in step: same options, output and exit codes. Run it with
#
#   powershell -ExecutionPolicy Bypass -File tools\test-runners\run-tests.ps1 MyMod.Tests\MyMod.Tests.csproj
#
# Modern frameworks (net10.0) run with `dotnet test`. .NET Framework ones
# (net48) are built with `dotnet build` and run by the xunit v2 console runner
# (the project's xunit.runner.console package reference): directly on Windows,
# under Mono elsewhere. Other arguments after the project (or after `--`) go
# to `dotnet test` only.
#
# Every selected framework runs even when an earlier one failed; a summary
# with one line per framework ends the output.
#
# Exit code: 0 every selected framework passed; 1 a build or a test run
# failed, or the console runner ran no tests; 2 usage, a filter the console
# runner cannot express, or a framework the project does not target (nothing
# is run); 3 a prerequisite is missing (Mono, or xunit.runner.console) and
# nothing failed.
#
# Environment:
#   MONO           the Mono executable off Windows (default: mono on PATH)
#   XUNIT_CONSOLE  xunit.console.exe to use instead of the package's own
#
# See tools/test-runners/README.md.

function Write-Problem([string]$Message) { [Console]::Error.WriteLine($Message) }

function Exit-Usage {
    Write-Problem 'usage: run-tests.ps1 [-f|--framework TFM]... [-c|--configuration C] [--filter EXPR] [--netfx-parallel MODE] <Tests.csproj> [dotnet test arguments]'
    exit 2
}

# Any other failure (a missing tool, an unreadable file) stops with exit 1.
trap {
    Write-Problem "ERROR: $_"
    exit 1
}

# PowerShell hands a script an argument such as -p:Name=value as two, '-p:' and 'Name=value'; rejoin them.
$argv = @()
for ($i = 0; $i -lt $args.Count; $i++) {
    $arg = [string]$args[$i]
    if ($arg -match '^-[^:]+:$' -and $i + 1 -lt $args.Count) { $arg += [string]$args[++$i] }
    $argv += $arg
}

$frameworks = @()
$config = 'Debug'
$filter = ''
$hasFilter = $false
$parallel = ''
$project = ''
$extra = @()
for ($i = 0; $i -lt $argv.Count; $i++) {
    $arg = [string]$argv[$i]
    switch -CaseSensitive ($arg) {
        { $_ -in @('-f', '--framework', '-c', '--configuration', '--filter', '--netfx-parallel') } {
            if ($i + 1 -ge $argv.Count) { Exit-Usage }
            $value = [string]$argv[++$i]
            switch ($arg) {
                { $_ -in @('-f', '--framework') } { $frameworks += $value }
                { $_ -in @('-c', '--configuration') } { $config = $value }
                '--filter' { $filter = $value; $hasFilter = $true }
                '--netfx-parallel' { $parallel = $value }
            }
            continue
        }
        '--' {
            if ($i + 1 -lt $argv.Count) { $extra += @($argv[($i + 1)..($argv.Count - 1)] | ForEach-Object { [string]$_ }) }
            $i = $argv.Count
            continue
        }
        default {
            if ($project -ne '') { $extra += $arg }
            elseif ($arg.StartsWith('-')) { Write-Problem "ERROR: unknown option $arg"; Exit-Usage }
            else { $project = $arg }
        }
    }
}
if ($project -eq '') { Exit-Usage }
if ($frameworks.Count -eq 0) { $frameworks = @('net10.0', 'net48') }
if ($parallel -notin @('', 'none', 'collections', 'assemblies', 'all', 'default')) {
    Write-Problem 'ERROR: --netfx-parallel takes none, collections, assemblies, all or default'
    exit 2
}

# net48, net472, net462: .NET Framework (no dot). net10.0 and other modern
# frameworks run under dotnet test.
function Test-NetFramework([string]$Tfm) { return $Tfm -match '^net[0-9]+$' }

$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

# Translate a dotnet test filter into xunit console options, or $null. The
# console ORs options of one kind and ANDs different kinds, so only terms of
# one kind joined by | translate exactly: FullyQualifiedName~text (or a bare
# text, dotnet test's shorthand for it), FullyQualifiedName=name, and
# Trait=value for any trait name. Test properties the console cannot select by
# (DisplayName, Name, ClassName, ...) are refused: as a trait they would match
# nothing. Property names compare case-insensitively, as dotnet test's do.
function Convert-Filter([string]$Expression) {
    if ($Expression -match '[&()!]') { return $null }
    $options = @()
    $kind = ''
    foreach ($raw in $Expression.Split('|')) {
        $term = $raw.Trim()
        if ($term -eq '') { return $null }
        $tilde = $term.IndexOf('~')
        $equals = $term.IndexOf('=')
        if ($tilde -ge 0) {
            if ($term.Substring(0, $tilde) -ne 'FullyQualifiedName') { return $null }
            $termKind = 'method'; $options += @('-method', ('*' + $term.Substring($tilde + 1) + '*'))
        } elseif ($equals -ge 0) {
            $key = $term.Substring(0, $equals)
            $value = $term.Substring($equals + 1)
            if ($key -eq '' -or $value -eq '') { return $null }
            if ($key -eq 'FullyQualifiedName') { $termKind = 'method'; $options += @('-method', $value) }
            elseif ($key -in @('DisplayName', 'Name', 'ClassName', 'TestCategory', 'Priority', 'Id')) { return $null }
            else { $termKind = 'trait'; $options += @('-trait', ($key + '=' + $value)) }
        } else {
            $termKind = 'method'; $options += @('-method', ('*' + $term + '*'))
        }
        if ($kind -ne '' -and $kind -ne $termKind) { return $null }
        $kind = $termKind
    }
    return , $options
}

$anyNetFramework = @($frameworks | Where-Object { Test-NetFramework $_ }).Count -gt 0

$xunitFilter = @()
if ($anyNetFramework -and $hasFilter) {
    $xunitFilter = Convert-Filter $filter
    if ($null -eq $xunitFilter) {
        Write-Problem "ERROR: the xunit console runner cannot express the filter '$filter'."
        Write-Problem '  It takes terms of one kind joined by |: FullyQualifiedName~text, FullyQualifiedName=name or Trait=value.'
        Write-Problem '  Simplify it, or run only the modern framework with --framework net10.0.'
        exit 2
    }
}

$mono = [Environment]::GetEnvironmentVariable('MONO')
if ([string]::IsNullOrEmpty($mono)) { $mono = 'mono' }
if ($anyNetFramework -and -not $onWindows -and $null -eq (Get-Command $mono -ErrorAction SilentlyContinue)) {
    Write-Problem "ERROR: .NET Framework tests need Mono on macOS and Linux, and '$mono' is not on PATH."
    Write-Problem '  Install it (macOS: brew install mono; Debian/Ubuntu: sudo apt-get install mono-complete),'
    Write-Problem '  or run only the modern framework with --framework net10.0.'
    exit 3
}

# One evaluated MSBuild property of the project, or $null (after showing why) when evaluation fails.
function Get-Property([string]$Name, [string[]]$Properties = @()) {
    $output = (@(& dotnet msbuild $project -nologo "-getProperty:$Name" @Properties) | ForEach-Object { [string]$_ }) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        Write-Problem "dotnet msbuild -getProperty:$Name exited $LASTEXITCODE`: $output"
        return $null
    }
    return $output.Trim()
}

$targets = Get-Property 'TargetFrameworks'
if ($null -ne $targets -and $targets -eq '') { $targets = Get-Property 'TargetFramework' }
if ($null -eq $targets) {
    Write-Problem "ERROR: cannot evaluate $project"
    exit 2
}
$targetList = @($targets.Split(';') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
foreach ($tfm in $frameworks) {
    if ($targetList -notcontains $tfm) {
        $shown = $targets; if ($shown -eq '') { $shown = 'nothing' }
        Write-Problem "ERROR: $project does not target $tfm (it targets: $shown). Add it to <TargetFrameworks>, or choose with --framework."
        exit 2
    }
}

# The xunit.console.exe of the xunit.runner.console version the project restored, or ''.
function Find-Console([string]$Tfm) {
    $override = [Environment]::GetEnvironmentVariable('XUNIT_CONSOLE')
    if (-not [string]::IsNullOrEmpty($override)) { return $override }
    $props = @("-p:TargetFramework=$Tfm", "-p:Configuration=$config")
    $assets = Get-Property 'ProjectAssetsFile' $props
    $root = Get-Property 'NuGetPackageRoot' $props
    if ([string]::IsNullOrEmpty($assets) -or [string]::IsNullOrEmpty($root) -or -not (Test-Path -LiteralPath $assets -PathType Leaf)) { return '' }
    $m = [regex]::Match([IO.File]::ReadAllText($assets), '"xunit\.runner\.console/([^"]+)"', 'IgnoreCase')
    if (-not $m.Success) { return '' }
    return [IO.Path]::Combine($root, 'xunit.runner.console', $m.Groups[1].Value.ToLowerInvariant(), 'tools', 'net48', 'xunit.console.exe')
}

$results = @()
$failed = $false
$missing = $false
foreach ($tfm in $frameworks) {
    if (-not (Test-NetFramework $tfm)) {
        Write-Output "== $tfm (dotnet test)"
        $testArgs = @('test', $project, '-f', $tfm, '-c', $config)
        if ($hasFilter) { $testArgs += @('--filter', $filter) }
        & dotnet @testArgs @extra
        $status = $LASTEXITCODE
        if ($status -eq 0) { $results += "${tfm}: passed" }
        else { $results += "${tfm}: FAILED (tests, exit $status)"; $failed = $true }
        continue
    }

    if ($onWindows) { Write-Output "== $tfm (xunit console, .NET Framework)" }
    else { Write-Output "== $tfm (xunit console, Mono)" }
    & dotnet build $project -f $tfm -c $config -nologo -v quiet
    $status = $LASTEXITCODE
    if ($status -ne 0) { $results += "${tfm}: FAILED (build, exit $status)"; $failed = $true; continue }
    $dll = Get-Property 'TargetPath' @("-p:TargetFramework=$tfm", "-p:Configuration=$config")
    if ([string]::IsNullOrEmpty($dll) -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        $results += "${tfm}: FAILED (the build reported $dll, which does not exist)"; $failed = $true; continue
    }
    $console = Find-Console $tfm
    if ($console -eq '' -or -not (Test-Path -LiteralPath $console -PathType Leaf)) {
        $at = ''; if ($console -ne '') { $at = " at $console" }
        Write-Problem "ERROR: no xunit console runner$at. Add <PackageReference Include=`"xunit.runner.console`" Version=`"<your xunit version>`" PrivateAssets=`"all`" /> to the test project, or set XUNIT_CONSOLE."
        $results += "${tfm}: not run (no xunit.runner.console)"; $missing = $true; continue
    }
    $mode = $parallel
    if ($mode -eq '') { if ($onWindows) { $mode = 'default' } else { $mode = 'none' } }
    # The XML report counts the tests that ran: a filter or discovery problem
    # that selects none must not pass.
    $report = [IO.Path]::GetTempFileName()
    $run = @($dll, '-xml', $report, '-nologo')
    if ($mode -ne 'default') { $run += @('-parallel', $mode) }
    $run += $xunitFilter
    if ($onWindows) { & $console @run }
    else { & $mono $console @run }
    $status = $LASTEXITCODE
    $total = 0
    if (Test-Path -LiteralPath $report -PathType Leaf) {
        foreach ($m in [regex]::Matches([IO.File]::ReadAllText($report), '<assembly\s[^>]*?\stotal="(\d+)"')) { $total += [int]$m.Groups[1].Value }
        Remove-Item -LiteralPath $report -Force -ErrorAction SilentlyContinue
    }
    if ($status -ne 0) { $results += "${tfm}: FAILED (tests, exit $status)"; $failed = $true }
    elseif ($total -eq 0) {
        Write-Problem "ERROR: the xunit console runner ran no tests on $tfm. Check the filter, and that the tests are public xunit tests."
        $results += "${tfm}: FAILED (no tests ran)"; $failed = $true
    }
    else { $results += "${tfm}: passed ($total tests)" }
}

Write-Output '== summary'
foreach ($line in $results) { Write-Output $line }
if ($failed) { exit 1 }
if ($missing) { exit 3 }
exit 0
