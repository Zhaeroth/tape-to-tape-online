<#
.SYNOPSIS
    The gate every merge and every release runs: builds, tests and every offline check of the mod against the game.

.DESCRIPTION
    Runs each step on its own, whatever the others found, so one run lists every problem; each step's own exit code
    decides it (never a pipe's). The steps:
      clean tree           git status: everything committed (with -Release a dirty tree fails; without, it's a note)
      plugin build         dotnet build of the plugin (and Core and Steam) in Release, from scratch, warnings as errors,
                           against the game's interop assemblies
      Core tests           dotnet test tests\T2TOnline.Core.Tests -c Release
      Steam tests          dotnet test tests\T2TOnline.Steam.Tests -c Release
      hookcheck            tools\checks\hookcheck.py on all four hook lists, against the decompiled listing
      rowcheck             tools\checks\rowcheck: every row of hooks.txt, menus_initdata.txt and clientmode.txt resolves
                           against the interop assemblies by the mod's own start-up rules (a row that doesn't turns
                           online off for every player)
      uipartscheck         tools\checks\uipartscheck.py: the native lobby's prefab parts in the game's bundles
      lobbywords_check     tools\checks\lobbywords_check.py: its own tests, then the lobby's words against the game's
                           fonts and terms
      tested game build    the installed game's GameAssembly.dll hash is in BuildFingerprint.TestedBuilds (a build
                           made after a game update without it would have online off for everyone, D12)
      package checks       tools\checks\zipcheck.py where they apply without making a zip: the READ ME template's
                           markers, the pinned MelonLoader against TestedMelonLoader, the checker's own tests, and the
                           plugin build's three assemblies and PDBs (versions equal VersionPrefix-VersionSuffix,
                           references resolve, PDB paths under /_/, MelonOptionalDependencies once declared). The release
                           script runs the rest on the written zips: allow-list, times, size, test install, update
                           simulation
      forbidden_strings    tools\checks\forbidden_strings.py: its own tests, then tree: no forbidden text in any
                           tracked or untracked file
      logcheck             tools\checks\logcheck.py: its own tests, then no log call writes a Steam name or ID, a lobby
                           ID, a full path, a raw exception message, player-written text (a skater's object name
                           included), the log-tag key (D127), a name read from a run file's or a save's JSON, or a number or date in the PC's culture (a
                           log line reads the same on every PC); the vetted lines are in its allow file, whose
                           counts can only go down
      rowuse               tools\checks\rowuse: its own tests, then the hook lists stay complete: every game member the
                           built plugin refers to has a row, and every row of hooks.txt and menus_initdata.txt is used
                           (the rules and the allow file, each entry with its reason, are in tools\checks\rowuse)
      patchinstance        tools\checks\patchinstance.py: its own tests, then no Harmony patch asks for __instance on a
                           static game method (Harmony passes null there), against the decompiled listing

    Some inputs are private to the maintainers and never enter the repository: the decompiled listing, the game data
    and the patterns file. Without -Release, a step whose private input wasn't given prints SKIPPED (with what to pass)
    and the gate can still pass. With -Release, every input is required, the tree must be committed and the
    forbidden_strings scan must be clean. The game folder is found by tools\common\Resolve-T2TGameDir.ps1.

    Ends with "GATE GREEN at <short commit>" (exit 0) or "GATE FAILED at <short commit>" and the failed steps (exit 1).
    Reads only, apart from the build outputs (bin\ and obj\, ignored by git).

.PARAMETER GameDir
    The Tape to Tape folder, with MelonLoader 0.7.3 installed and the game launched once (MelonLoader then generates
    the interop assemblies the build and rowcheck need). Default: T2T_GAME_DIR, then T2TGameDir in
    Directory.Build.local.props, then Steam's own records.

.PARAMETER ListingDir
    The decompiled listing of the installed game build (types.json, methods.tsv, dump.cs; see hookcheck.py) that
    hookcheck checks the hook lists against. Default: the T2T_LISTING_DIR environment variable.

.PARAMETER GameDataDir
    The folder with font_metrics.json and terms_full.tsv, made from the game's files, that lobbywords_check reads.
    Default: the T2T_GAME_DATA environment variable.

.PARAMETER PatternsFile
    The forbidden-strings patterns file (kept outside the repository; format in forbidden_strings.py).

.PARAMETER Release
    Every input is required, and the tree must be committed and clean of forbidden text.

.EXAMPLE
    .\tools\gate\Invoke-Gate.ps1

.EXAMPLE
    .\tools\gate\Invoke-Gate.ps1 -Release -ListingDir <listing> -GameDataDir <game data> -PatternsFile <patterns.txt>
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$ListingDir,
    [string]$GameDataDir,
    [string]$PatternsFile,
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\')
. (Join-Path $repo 'tools\common\Resolve-T2TGameDir.ps1')
$hooksDir = Join-Path $repo 'src\T2TOnline.Plugin\Hooks'
$checks = Join-Path $repo 'tools\checks'

function Get-FullPath([string]$path) {
    # Relative to PowerShell's current folder, not the process's.
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path).TrimEnd('\', '/')
}

# Runs a program and returns its exit code and output lines (stdout and stderr). A program that can't be found is
# exit code 127, never a stale $LASTEXITCODE from an earlier command.
function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    if (-not (Get-Command $Exe -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{ ExitCode = 127; Lines = @("'$Exe' was not found on PATH") }
    }
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $global:LASTEXITCODE = $null
        $lines = @(& $Exe @Arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $saved
    }
    if ($null -eq $code) { $code = 126 }
    [pscustomobject]@{ ExitCode = $code; Lines = $lines }
}

# Python 3: 'python' when it runs (Windows can have a Store stub that doesn't), else the 'py' launcher.
$python = $null
foreach ($candidate in @(@('python'), @('py', '-3'))) {
    $probe = Invoke-Native $candidate[0] (@($candidate | Select-Object -Skip 1) + '--version')
    if ($probe.ExitCode -eq 0 -and ($probe.Lines -join ' ') -match 'Python 3') { $python = $candidate; break }
}
function Invoke-Python([string]$Script, [string[]]$Arguments) {
    if (-not $python) {
        return [pscustomobject]@{ ExitCode = 127; Lines = @('Python 3 was not found (python or py -3 on PATH)') }
    }
    # -I: isolated mode, so nothing from the current folder or the user's site packages is imported.
    Invoke-Native $python[0] (@($python | Select-Object -Skip 1) + @('-I', (Join-Path $checks $Script)) + $Arguments)
}

$results = New-Object System.Collections.Generic.List[object]
$stepNumber = 0
$stepCount = 14
function Add-Result([string]$Name, [string]$Status, [string]$Summary, [string[]]$Details, [TimeSpan]$Took) {
    $script:stepNumber++
    $results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Summary = $Summary })
    $color = @{ PASSED = 'Green'; FAILED = 'Red'; SKIPPED = 'Yellow'; NOTE = 'Yellow' }[$Status]
    $time = if ($Took.TotalSeconds -ge 1) { " ({0:N0} s)" -f $Took.TotalSeconds } else { '' }
    Write-Host ("[{0}/{1}] {2,-18} {3}{4}" -f $stepNumber, $stepCount, $Name, $Status, $time) -ForegroundColor $color -NoNewline
    Write-Host $(if ($Summary) { "  $Summary" } else { '' })
    foreach ($line in @($Details | Where-Object { $_ } | Select-Object -First 40)) { Write-Host "      $line" }
    if (@($Details).Count -gt 40) { Write-Host "      ... and $(@($Details).Count - 40) more" }
}

# A private input not given: SKIPPED, or FAILED with -Release.
function Add-Missing([string]$Name, [string]$HowTo) {
    if ($Release) { Add-Result $Name 'FAILED' "needs $HowTo (required with -Release)" @() ([TimeSpan]::Zero) }
    else { Add-Result $Name 'SKIPPED' "($HowTo)" @() ([TimeSpan]::Zero) }
}

function Measure-Step([scriptblock]$Body) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $value = & $Body
    $watch.Stop()
    , @($value, $watch.Elapsed)
}

# Inputs.
$game = $null
$gameProblem = $null
try { $game = Resolve-T2TGameDir -GameDir $GameDir -RepoRoot $repo }
catch { $gameProblem = $_.Exception.Message }
if (-not $ListingDir -and $env:T2T_LISTING_DIR) { $ListingDir = $env:T2T_LISTING_DIR }
if (-not $GameDataDir -and $env:T2T_GAME_DATA) { $GameDataDir = $env:T2T_GAME_DATA }
if ($ListingDir) { $ListingDir = Get-FullPath $ListingDir }
if ($GameDataDir) { $GameDataDir = Get-FullPath $GameDataDir }
if ($PatternsFile) { $PatternsFile = Get-FullPath $PatternsFile }

$head = Invoke-Native 'git' @('-C', $repo, 'rev-parse', '--short', 'HEAD')
$commit = if ($head.ExitCode -eq 0 -and $head.Lines) { $head.Lines[0].Trim() } else { 'unknown' }
Write-Host "T2T Online gate$(if ($Release) { ' (release)' }) at $commit"
Write-Host "  game folder: $(if ($game) { "$($game.Path)  (from $($game.Source))" } else { "not found: $gameProblem" })"
Write-Host "  listing: $(if ($ListingDir) { $ListingDir } else { 'not given' }); game data: $(if ($GameDataDir) { $GameDataDir } else { 'not given' }); patterns: $(if ($PatternsFile) { 'given' } else { 'not given' })"
Write-Host ''

# 1. Clean tree.
$dirty = $false
$step = Measure-Step {
    $status = Invoke-Native 'git' @('-C', $repo, 'status', '--porcelain', '--untracked-files=normal')
    if ($status.ExitCode -ne 0) { return @('FAILED', 'git status failed, so the tree could not be checked', $status.Lines) }
    if ($status.Lines.Count -gt 0) {
        $script:dirty = $true
        $kind = if ($Release) { 'FAILED' } else { 'NOTE' }
        return @($kind, "$($status.Lines.Count) uncommitted change(s): a release needs everything committed", @($status.Lines | Select-Object -First 10))
    }
    @('PASSED', 'everything committed', @())
}
Add-Result 'clean tree' $step[0][0] $step[0][1] $step[0][2] $step[1]

# 2. Plugin build. Without the game's interop assemblies the build quietly leaves out every part that touches the
#    game (HAS_INTEROP), so their absence fails the step.
$step = Measure-Step {
    if (-not $game) { return @('FAILED', $gameProblem, @()) }
    $interop = Join-Path $game.Path 'MelonLoader\Il2CppAssemblies'
    if (-not (Test-Path (Join-Path $interop '*.dll'))) {
        return @('FAILED', "no interop assemblies in $interop`: launch the game once with MelonLoader 0.7.3 installed", @())
    }
    $build = Invoke-Native 'dotnet' @('build', (Join-Path $repo 'src\T2TOnline.Plugin'), '-c', 'Release',
        "-p:T2TGameDir=$($game.Path)", '--no-incremental', '-warnaserror', '--nologo')
    $warnings = ($build.Lines | Where-Object { $_ -match '^\s*(\d+) Warning\(s\)' } | Select-Object -Last 1) -replace '^\s*(\d+).*', '$1'
    $errors = ($build.Lines | Where-Object { $_ -match '^\s*(\d+) Error\(s\)' } | Select-Object -Last 1) -replace '^\s*(\d+).*', '$1'
    $summary = "exit $($build.ExitCode), $warnings warning(s), $errors error(s)"
    if ($build.ExitCode -ne 0 -or $warnings -ne '0') {
        return @('FAILED', $summary, @($build.Lines | Where-Object { $_ -match ': (warning|error) ' } | Select-Object -Unique))
    }
    @('PASSED', $summary, @())
}
Add-Result 'plugin build' $step[0][0] $step[0][1] $step[0][2] $step[1]

# 3 and 4. Tests.
foreach ($project in 'Core', 'Steam') {
    $step = Measure-Step {
        $test = Invoke-Native 'dotnet' @('test', (Join-Path $repo "tests\T2TOnline.$project.Tests"), '-c', 'Release', '--nologo')
        $totals = @($test.Lines | Where-Object { $_ -match '^\s*(Passed|Failed)!' } | ForEach-Object { $_.Trim() })
        if ($test.ExitCode -ne 0 -or $totals.Count -eq 0) {
            $failed = @($test.Lines | Where-Object { $_ -match '^\s*Failed |error ' })
            return @('FAILED', "exit $($test.ExitCode)", ($failed + $totals))
        }
        @('PASSED', ($totals -join '; ' -replace '\s+', ' '), @())
    }
    Add-Result "$project tests" $step[0][0] $step[0][1] $step[0][2] $step[1]
}

# 5. hookcheck on all four hook lists.
if (-not $ListingDir) {
    Add-Missing 'hookcheck' 'the decompiled listing: pass -ListingDir or set T2T_LISTING_DIR'
}
else {
    $step = Measure-Step {
        if (-not (Test-Path -LiteralPath (Join-Path $ListingDir 'types.json'))) {
            return @('FAILED', "no decompiled listing in $ListingDir (no types.json)", @())
        }
        $failed = $false
        $counts = @()
        $details = @()
        foreach ($list in 'hooks.txt', 'menus_initdata.txt', 'clientmode.txt', 'presentation.txt') {
            $run = Invoke-Python 'hookcheck.py' @($ListingDir, (Join-Path $hooksDir $list))
            $counts += "$list $(@($run.Lines | Where-Object { $_ -match '^ok' }).Count) ok"
            if ($run.ExitCode -ne 0) {
                $failed = $true
                $details += "$list (exit $($run.ExitCode)):"
                $details += @($run.Lines | Where-Object { $_ -notmatch '^ok' } | ForEach-Object { "  $_" })
            }
        }
        @($(if ($failed) { 'FAILED' } else { 'PASSED' }), ($counts -join ', '), $details)
    }
    Add-Result 'hookcheck' $step[0][0] $step[0][1] $step[0][2] $step[1]
}

# 6. rowcheck: every hook row against the interop assemblies, by the start-up check's own rules.
$step = Measure-Step {
    if (-not $game) { return @('FAILED', $gameProblem, @()) }
    $lists = 'hooks.txt', 'menus_initdata.txt', 'clientmode.txt' | ForEach-Object { Join-Path $hooksDir $_ }
    $run = Invoke-Native 'dotnet' (@('run', '--project', (Join-Path $checks 'rowcheck'), '-c', 'Release', '--', $game.Path) + $lists)
    $last = ($run.Lines | Where-Object { $_ -match 'rows checked' } | Select-Object -Last 1)
    if ($run.ExitCode -ne 0) {
        return @('FAILED', "exit $($run.ExitCode)$(if ($last) { ", $last" })", @($run.Lines | Where-Object { $_ -match '^NO |error|interop' }))
    }
    @('PASSED', $last, @())
}
Add-Result 'rowcheck' $step[0][0] $step[0][1] $step[0][2] $step[1]

# 7. uipartscheck: the native lobby's prefab parts (a missing one turns off only the lobby in the game; a release
#    should still know).
$step = Measure-Step {
    if (-not $game) { return @('FAILED', $gameProblem, @()) }
    $run = Invoke-Python 'uipartscheck.py' @($game.Path, (Join-Path $hooksDir 'ui_parts.txt'))
    $last = $run.Lines | Select-Object -Last 1
    if ($run.ExitCode -ne 0) {
        return @('FAILED', "exit $($run.ExitCode), $last", @($run.Lines | Where-Object { $_ -notmatch '^ok' }))
    }
    @('PASSED', $last, @())
}
Add-Result 'uipartscheck' $step[0][0] $step[0][1] $step[0][2] $step[1]

# 8. lobbywords_check: every glyph of every lobby word in every language, and every label's width.
if (-not $GameDataDir) {
    Add-Missing 'lobbywords_check' 'the game data: pass -GameDataDir or set T2T_GAME_DATA'
}
else {
    $step = Measure-Step {
        $metrics = Join-Path $GameDataDir 'font_metrics.json'
        $terms = Join-Path $GameDataDir 'terms_full.tsv'
        $absent = @($metrics, $terms | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
        if ($absent.Count -gt 0) { return @('FAILED', "missing in the game data: $($absent -join ', ')", @()) }
        $self = Invoke-Python 'lobbywords_check.py' @('--selftest')
        if ($self.ExitCode -ne 0) { return @('FAILED', "selftest exit $($self.ExitCode)", @($self.Lines)) }
        $run = Invoke-Python 'lobbywords_check.py' @('--metrics', $metrics, '--terms', $terms)
        $last = $run.Lines | Select-Object -Last 1
        if ($run.ExitCode -ne 0) { return @('FAILED', "exit $($run.ExitCode), $last", @($run.Lines | Select-Object -SkipLast 1)) }
        @('PASSED', $last, @())
    }
    Add-Result 'lobbywords_check' $step[0][0] $step[0][1] $step[0][2] $step[1]
}

# 9. The installed game is a tested build: a build made after a game update without the new hashes would have online
#    off for everyone (D12).
$step = Measure-Step {
    if (-not $game) { return @('FAILED', $gameProblem, @()) }
    $gameAssembly = Join-Path $game.Path 'GameAssembly.dll'
    if (-not (Test-Path -LiteralPath $gameAssembly)) {
        return @('FAILED', "no GameAssembly.dll in $($game.Path), so the tested build couldn't be checked", @())
    }
    $hash = (Get-FileHash -LiteralPath $gameAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
    $fingerprint = Get-Content -Raw (Join-Path $repo 'src\T2TOnline.Plugin\Startup\BuildFingerprint.cs')
    # The hash as a key of the TestedBuilds table (a line that starts ["<hash>"] =), not anywhere in the file: one named
    # in a comment, or an entry commented out, is not a tested build.
    $entry = '(?im)^\s*\[\s*"' + [regex]::Escape($hash) + '"\s*\]\s*='
    if ($fingerprint -notmatch $entry) {
        return @('FAILED', "the installed game ($($hash.Substring(0, 8))...) isn't in BuildFingerprint.TestedBuilds: a build from this tree would have online off for everyone. Go through DEVELOPMENT.md's after-patch list", @())
    }
    @('PASSED', "GameAssembly.dll $($hash.Substring(0, 8))... is a tested build", @())
}
Add-Result 'tested game build' $step[0][0] $step[0][1] $step[0][2] $step[1]

# 10. The package checks that apply without a zip (the release script runs the zip-level ones on the written zips).
$step = Measure-Step {
    if (-not $game) { return @('FAILED', $gameProblem, @()) }
    $built = Join-Path $repo 'src\T2TOnline.Plugin\bin\Release\net6.0'
    if (-not (Test-Path -LiteralPath (Join-Path $built 'T2TOnline.dll'))) {
        return @('FAILED', "no plugin build in $built (the plugin build step above builds it)", @())
    }
    $runs = @(
        @('template', @('template', (Join-Path $repo 'tools\package\READ ME FIRST.txt'))),
        @('melonpin', @('melonpin', '--repo', $repo)),
        @('selftest', @('selftest')),
        @('built', @('built', '--dir', $built, '--repo', $repo, '--game-dir', $game.Path))
    )
    $failed = $false
    $names = @()
    $details = @()
    foreach ($run in $runs) {
        $result = Invoke-Python 'zipcheck.py' $run[1]
        $names += $run[0]
        if ($result.ExitCode -ne 0) {
            $failed = $true
            $details += "zipcheck $($run[0]) (exit $($result.ExitCode)):"
            $details += @($result.Lines | Where-Object { $_ -notmatch '^ok ' } | ForEach-Object { "  $_" })
        }
    }
    @($(if ($failed) { 'FAILED' } else { 'PASSED' }), ($names -join ', '), $details)
}
Add-Result 'package checks' $step[0][0] $step[0][1] $step[0][2] $step[1]

# 11. forbidden_strings over every tracked and untracked file.
if (-not $PatternsFile) {
    Add-Missing 'forbidden_strings' 'the patterns file: pass -PatternsFile'
}
else {
    $step = Measure-Step {
        if (-not (Test-Path -LiteralPath $PatternsFile -PathType Leaf)) { return @('FAILED', "no patterns file at $PatternsFile", @()) }
        $self = Invoke-Python 'forbidden_strings.py' @('selftest')
        if ($self.ExitCode -ne 0) {
            return @('FAILED', "selftest exit $($self.ExitCode)", @($self.Lines))
        }
        $run = Invoke-Python 'forbidden_strings.py' @('--patterns', $PatternsFile, 'tree', $repo)
        $count = $run.Lines | Where-Object { $_ -match '^forbidden_strings: ' } | Select-Object -Last 1
        if ($run.ExitCode -ne 0) {
            return @('FAILED', "exit $($run.ExitCode), $count", @($run.Lines | Where-Object { $_ -notmatch '^forbidden_strings: \d+ match' }))
        }
        @('PASSED', $count, @())
    }
    Add-Result 'forbidden_strings' $step[0][0] $step[0][1] $step[0][2] $step[1]
}
# 12. logcheck: its own tests, then no log call writes personal data (D127).
$step = Measure-Step {
    $self = Invoke-Python 'logcheck.py' @('--selftest')
    if ($self.ExitCode -ne 0) {
        return @('FAILED', "selftest exit $($self.ExitCode)", @($self.Lines))
    }
    $run = Invoke-Python 'logcheck.py' @($repo)
    $count = $run.Lines | Where-Object { $_ -match '^logcheck: ' } | Select-Object -Last 1
    if ($run.ExitCode -ne 0) {
        return @('FAILED', "exit $($run.ExitCode), $count", @($run.Lines | Where-Object { $_ -notmatch '^logcheck: ' }))
    }
    @('PASSED', $count, @())
}
Add-Result 'logcheck' $step[0][0] $step[0][1] $step[0][2] $step[1]

# 13. rowuse: the hook lists stay complete. Its own tests (each plants a case and asserts the analysis fails on it) run
#     first in the same process, then the built plugin's member references against the lists: every game member the plugin
#     uses has a row, every row of hooks.txt and menus_initdata.txt is used (the allow file holds what the rules can't decide).
$step = Measure-Step {
    if (-not $game) { return @('FAILED', $gameProblem, @()) }
    $built = Join-Path $repo 'src\T2TOnline.Plugin\bin\Release\net6.0\T2TOnline.dll'
    if (-not (Test-Path -LiteralPath $built)) {
        return @('FAILED', "no plugin build at $built (the plugin build step above builds it)", @())
    }
    $lists = 'hooks.txt', 'menus_initdata.txt', 'clientmode.txt', 'presentation.txt' | ForEach-Object { Join-Path $hooksDir $_ }
    $run = Invoke-Native 'dotnet' (@('run', '--project', (Join-Path $checks 'rowuse'), '-c', 'Release', '--', '--selftest', $game.Path,
        $built, (Join-Path $checks 'rowuse_allow.txt')) + $lists)
    $self = ($run.Lines | Where-Object { $_ -match '^rowuse selftest: ' } | Select-Object -Last 1)
    $last = ($run.Lines | Where-Object { $_ -match '^rowuse: ' } | Select-Object -Last 1)
    if ($run.ExitCode -ne 0 -or -not $self -or -not $last) {
        return @('FAILED', "exit $($run.ExitCode)$(if ($self) { ", $self" })", @($run.Lines | Where-Object { $_ -match '^(UNLISTED|UNUSED|STALE|selftest FAILED|rowuse)' -or $_ -match 'error' }))
    }
    @('PASSED', "$($self -replace '^rowuse ', ''); $($last -replace '^rowuse: ', '')", @())
}
Add-Result 'rowuse' $step[0][0] $step[0][1] $step[0][2] $step[1]

# 14. patchinstance: no Harmony patch asks for __instance on a static game method (it would read null on every call). Its
#     own tests first, then the plugin's patch registrations against the decompiled listing.
if (-not $ListingDir) {
    Add-Missing 'patchinstance' 'the decompiled listing: pass -ListingDir or set T2T_LISTING_DIR'
}
else {
    $step = Measure-Step {
        $dump = Join-Path $ListingDir 'dump.cs'
        if (-not (Test-Path -LiteralPath $dump -PathType Leaf)) { return @('FAILED', "no dump.cs in $ListingDir", @()) }
        $self = Invoke-Python 'patchinstance.py' @('--selftest')
        if ($self.ExitCode -ne 0) { return @('FAILED', "selftest exit $($self.ExitCode)", @($self.Lines)) }
        $run = Invoke-Python 'patchinstance.py' @((Join-Path $repo 'src\T2TOnline.Plugin'), $dump, (Join-Path $checks 'patchinstance_allow.txt'))
        $count = $run.Lines | Where-Object { $_ -match '^patchinstance: ' } | Select-Object -Last 1
        if ($run.ExitCode -ne 0) {
            return @('FAILED', "exit $($run.ExitCode)$(if ($count) { ", $count" })", @($run.Lines | Where-Object { $_ -notmatch '^patchinstance: \d+ file' }))
        }
        @('PASSED', ($count -replace '^patchinstance: ', ''), @())
    }
    Add-Result 'patchinstance' $step[0][0] $step[0][1] $step[0][2] $step[1]
}

Write-Host ''
$failedSteps = @($results | Where-Object Status -eq 'FAILED' | ForEach-Object Name)
$skippedSteps = @($results | Where-Object Status -eq 'SKIPPED' | ForEach-Object Name)
if ($skippedSteps.Count -gt 0) { Write-Host "Skipped (private inputs not given): $($skippedSteps -join ', ')" -ForegroundColor Yellow }
if ($failedSteps.Count -gt 0) {
    Write-Host "GATE FAILED at $commit$(if ($dirty) { ' (uncommitted changes)' }): $($failedSteps -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "GATE GREEN at $commit$(if ($dirty) { ' (with uncommitted changes)' })" -ForegroundColor Green
exit 0
