<#
.SYNOPSIS
    Builds a release's two zips: the public download, and the same with the official MelonLoader 0.7.3 release.

.DESCRIPTION
    Runs the release gate first (tools\gate\Invoke-Gate.ps1 -Release: a committed tree clean of forbidden text, the
    plugin build, both test projects, hookcheck, rowcheck, uipartscheck, lobbywords_check, the package checks and the
    tested game build), then builds the plugin from scratch against the game into a fresh folder (so nothing stale can
    ship), checks the build holds the mod's game code, carries no machine paths, has the version in Directory.Build.props
    and passes tools\checks\zipcheck.py built (versions, references, PDB structure), and writes to -OutDir:
      T2TOnline-<version>.zip                     the public download, in the one-folder layout (D124):
                                                  Mods\T2TOnline\manifest.json (generated), LICENSE.txt,
                                                  THIRD-PARTY-NOTICES.txt, Plugins\T2TOnline.dll and .pdb,
                                                  UserLibs\T2TOnline.Core.dll, T2TOnline.Steam.dll and their .pdbs, and
                                                  "T2T Online - READ ME FIRST.txt" at the root
      T2TOnline-<version>-with-MelonLoader.zip    the same plus the official MelonLoader.x64.zip, unmodified (the
                                                  release pinned in melonloader.pin, downloaded once to <OutDir>\cache and
                                                  checked against its SHA-256); for the maintainers' own testers, never
                                                  published
      T2TOnline-<version>-release-notes.txt       each zip's SHA-256 and size, the commit and the SDK
    Each zip is laid out like the game folder (unzipping it into a Tape to Tape folder installs it) and holds no game
    files: MelonLoader generates its game-specific files on the player's first launch. Every entry carries the release
    commit's UTC time, in sorted order with fixed attributes, so building twice from one commit gives the same zip
    (checked: unless -SkipRebuildCheck the plugin is built a second time and the zips must match byte for byte).
    Each written zip is read back by tools\checks\zipcheck.py: an allow-list of exact entries, times, order, a size
    budget, the READ ME, manifest and licence texts, the assemblies (versions, references, PDB structure,
    MelonOptionalDependencies once declared), a test install by MelonLoader's folder rules, and an update simulation
    over -PreviousZip. Both zips are then scanned with forbidden_strings.py zip before they get their real names, and
    each gets a .sha256 next to it, to send separately so a player can check the zip. One line per zip (version, file,
    commit, gate result, SDK, the game's fingerprint, SHA-256) is appended to <OutDir>\packages.log, and so is a run
    that made no zip.
    Every release has its own version: bump VersionPrefix or VersionSuffix in Directory.Build.props, so players'
    builds can always be told apart. A zip of the same version is never replaced without -Force.

    The READ ME comes from tools\package\READ ME FIRST.txt: {{VERSION}} is the version as a player reads it
    ("0.11.0 Beta"); a line {{#FRIENDS}} ... {{/FRIENDS}} is kept only in the with-MelonLoader zip, {{#PUBLIC}} ...
    {{/PUBLIC}} only in the public one (markers alone on their line, never nested).

.PARAMETER GameDir
    The Tape to Tape folder, with MelonLoader 0.7.3 installed and the game launched once (it generates the interop
    assemblies the build needs). Default: as the gate finds it (T2T_GAME_DIR, Directory.Build.local.props, Steam).

.PARAMETER ListingDir
    For the gate's hookcheck. Default: the T2T_LISTING_DIR environment variable.

.PARAMETER GameDataDir
    For the gate's lobbywords_check. Default: the T2T_GAME_DATA environment variable.

.PARAMETER PatternsFile
    The forbidden-strings patterns file, for the gate's tree scan and the scan of both zips. Required.

.PARAMETER PreviousZip
    The previous release's public zip, kept outside OutDir (which holds only the current release). The update
    simulation extracts it, then the new zip over it, and expects exactly its leftovers. Required unless
    -SkipUpdateCheck.

.PARAMETER SkipUpdateCheck
    No previous release to update from: skips the update simulation.

.PARAMETER SkipRebuildCheck
    Skips the second build that proves the zips are reproducible (for a quick run while working on the script).

.PARAMETER OutDir
    Where the zips, their .sha256 files, the release notes, packages.log and the MelonLoader download cache go. Default:
    dist\ in the repository (ignored by git).

.PARAMETER LicenseFile
    The LICENSE to ship. Default: LICENSE at the repository root.

.PARAMETER NoticesFile
    The THIRD_PARTY_NOTICES.md to ship. Default: THIRD_PARTY_NOTICES.md at the repository root.

.PARAMETER DryRun
    Runs the gate and the package's own checks, then stops: nothing is built, downloaded or written but packages.log.

.PARAMETER Force
    Replaces zips of this version that already exist. The gate still has to pass.

.EXAMPLE
    .\tools\package\New-ReleasePackage.ps1 -ListingDir <listing> -GameDataDir <game data> -PatternsFile <patterns.txt> -PreviousZip <previous release's public zip>

.EXAMPLE
    .\tools\package\New-ReleasePackage.ps1 -PatternsFile <patterns.txt> -SkipUpdateCheck -DryRun
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$ListingDir,
    [string]$GameDataDir,
    [string]$PatternsFile,
    [string]$PreviousZip,
    [string]$OutDir,
    [string]$LicenseFile,
    [string]$NoticesFile,
    [switch]$SkipUpdateCheck,
    [switch]$SkipRebuildCheck,
    [switch]$DryRun,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\')
. (Join-Path $repo 'tools\common\Resolve-T2TGameDir.ps1')

function Get-FullPath([string]$path) {
    # Absolute, relative to PowerShell's current folder: the .NET calls below (ReadAllBytes, WriteAllText) resolve
    # relative paths against the process folder instead.
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path).TrimEnd('\', '/')
}

# Runs a program with its output shown and returns its exit code. Its stderr must not stop this script (under
# Windows PowerShell 5.1, redirected stderr lines become errors), so only the exit code decides.
function Invoke-Program([string]$Exe, [string[]]$Arguments) {
    if (-not (Get-Command $Exe -ErrorAction SilentlyContinue)) { Write-Host "'$Exe' was not found on PATH"; return 127 }
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $global:LASTEXITCODE = $null
        & $Exe @Arguments 2>&1 | ForEach-Object { Write-Host "$_" }
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $saved }
    if ($null -eq $code) { 126 } else { $code }
}

# The same, returning the first line of output (for dotnet --version and git).
function Get-ProgramLine([string]$Exe, [string[]]$Arguments) {
    # All of the output first: stopping the pipeline at its first line can cut the program off before it exits.
    try { $lines = @(& $Exe @Arguments 2>$null); if ($LASTEXITCODE -eq 0 -and $lines.Count -gt 0) { "$($lines[0])".Trim() } else { $null } }
    catch { $null }
}

$dist = if ($OutDir) { Get-FullPath $OutDir } else { Join-Path $repo 'dist' }
$license = if ($LicenseFile) { Get-FullPath $LicenseFile } else { Join-Path $repo 'LICENSE' }
$notices = if ($NoticesFile) { Get-FullPath $NoticesFile } else { Join-Path $repo 'THIRD_PARTY_NOTICES.md' }
if ($PatternsFile) { $PatternsFile = Get-FullPath $PatternsFile }
if ($PreviousZip) { $PreviousZip = Get-FullPath $PreviousZip }
$readMeSource = Join-Path $PSScriptRoot 'READ ME FIRST.txt'
$readMeName = 'T2T Online - READ ME FIRST.txt'
$modFolder = 'Mods\T2TOnline'
$checks = Join-Path $repo 'tools\checks'
$writeZip = Join-Path $PSScriptRoot 'write_zip.py'
$manifestDescription = "Online co-op for Tape to Tape: the game's four-player couch co-op, played with friends on their own PCs."

# The MelonLoader the with-MelonLoader zip carries (melonloader.pin: one source for this script and zipcheck).
$pin = @{}
$pinFile = Join-Path $PSScriptRoot 'melonloader.pin'
if (Test-Path -LiteralPath $pinFile) {
    foreach ($line in Get-Content -LiteralPath $pinFile) {
        if ($line -match '^\s*([a-z0-9]+)\s*=\s*(.+?)\s*$') { $pin[$Matches[1]] = $Matches[2] }
    }
}

$props = [xml](Get-Content -Raw (Join-Path $repo 'Directory.Build.props'))
$prefix = ($props.Project.PropertyGroup | Where-Object { $_.VersionPrefix } | Select-Object -First 1).VersionPrefix
$suffix = ($props.Project.PropertyGroup | Where-Object { $_.VersionSuffix } | Select-Object -First 1).VersionSuffix
$version = if ($suffix) { "$prefix-$suffix" } else { $prefix }
# As a player reads it (ReleaseVersion.ForScreen): the file's name keeps the release, 0.9.3-beta; the READ ME says
# 0.9.3 Beta.
$versionShown = $version -replace '-beta$', ' Beta'
$publicZip = Join-Path $dist "T2TOnline-$version.zip"
$melonBundleZip = Join-Path $dist "T2TOnline-$version-with-MelonLoader.zip"
$notesFile = Join-Path $dist "T2TOnline-$version-release-notes.txt"
$zips = @($publicZip, $melonBundleZip)
$commit = try { & git -C $repo rev-parse HEAD } catch { $null }
if ($LASTEXITCODE -ne 0 -or -not $commit) { $commit = 'unknown' }
$commit = "$commit".Trim()
# Every zip entry carries the release commit's UTC time: the zip says nothing about the builder's time zone.
$commitTime = $null
$commitDate = Get-ProgramLine 'git' @('-C', $repo, 'log', '-1', '--format=%cI')
if ($commitDate) {
    $commitTime = [DateTimeOffset]::Parse($commitDate, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
}
$sdk = Get-ProgramLine 'dotnet' @('--version')
if (-not $sdk) { $sdk = 'unknown' }

function Write-PackageLog([string]$file, [string]$gate, [string]$gameFingerprint = '', [string]$sha256 = '') {
    New-Item -ItemType Directory -Force $dist | Out-Null
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z'
    $line = "$stamp version $version, $file, commit $commit, gate $gate, sdk $sdk"
    if ($gameFingerprint) { $line += ", game GameAssembly.dll sha256 $gameFingerprint" }
    if ($sha256) { $line += ", zip sha256 $sha256" }
    Add-Content -Path (Join-Path $dist 'packages.log') -Encoding ascii -Value $line
}

# 0. The package's own checks, then the release gate. Everything runs whatever the others found, so one run lists
#    every problem.
$problems = New-Object System.Collections.Generic.List[string]
foreach ($required in $license, $notices) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { $problems.Add("no $(Split-Path $required -Leaf) at $required") }
}
if (-not (Test-Path -LiteralPath $readMeSource -PathType Leaf)) {
    $problems.Add("no READ ME template at $readMeSource")
}
foreach ($key in 'version', 'url', 'sha256') {
    if (-not $pin[$key]) { $problems.Add("no $key in $pinFile") }
}
if (-not $PatternsFile) {
    $problems.Add('no -PatternsFile: a release scans its tree and both zips for forbidden text')
}
if (-not $commitTime) { $problems.Add('git gave no commit time: every zip entry carries the release commit''s UTC time') }
foreach ($zip in $zips) {
    if ((Test-Path -LiteralPath $zip) -and -not $Force) {
        $problems.Add("$(Split-Path $zip -Leaf) exists already: players may have it, so a new release needs a new version " +
            '(VersionPrefix or VersionSuffix in Directory.Build.props); -Force replaces it')
    }
}
if ($PreviousZip) {
    if (-not (Test-Path -LiteralPath $PreviousZip -PathType Leaf)) { $problems.Add("no previous release's zip at $PreviousZip") }
}
elseif (-not $SkipUpdateCheck) {
    $problems.Add('no -PreviousZip: the update simulation needs the previous release''s public zip (-SkipUpdateCheck if there is none)')
}
# Python 3 as the gate picks it: 'python' when it runs (Windows can have a Store stub of that name that doesn't), else
# the 'py' launcher.
$python = @('py', '-3')
if ((Get-ProgramLine 'python' @('--version')) -match 'Python 3') { $python = @('python') }
function Invoke-Python([string]$Script, [string[]]$Arguments) {
    Invoke-Program $python[0] (@($python | Select-Object -Skip 1) + @('-I', $Script) + $Arguments)
}
$zipcheck = Join-Path $checks 'zipcheck.py'
if (Test-Path -LiteralPath $readMeSource) {
    if ((Invoke-Python $zipcheck @('template', $readMeSource)) -ne 0) { $problems.Add('the READ ME template fails zipcheck.py template (above)') }
}
if ((Invoke-Python $zipcheck @('melonpin', '--repo', $repo)) -ne 0) { $problems.Add('the pinned MelonLoader fails zipcheck.py melonpin (above)') }
$game = $null
try { $game = Resolve-T2TGameDir -GameDir $GameDir -RepoRoot $repo }
catch { $problems.Add($_.Exception.Message) }

Write-Host "Release gate for $version ..."
$gateArguments = @{ Release = $true; ListingDir = $ListingDir; GameDataDir = $GameDataDir; PatternsFile = $PatternsFile }
if ($game) { $gateArguments.GameDir = $game.Path }
$global:LASTEXITCODE = $null
& (Join-Path $repo 'tools\gate\Invoke-Gate.ps1') @gateArguments
$gatePassed = ($LASTEXITCODE -eq 0)
if (-not $gatePassed) { $problems.Add("the release gate failed (exit $LASTEXITCODE; its steps are listed above)") }

$gate = if ($gatePassed) { 'passed' } else { 'FAILED' }
$gameFingerprint = ''
if ($game -and (Test-Path -LiteralPath (Join-Path $game.Path 'GameAssembly.dll'))) {
    $gameFingerprint = (Get-FileHash -LiteralPath (Join-Path $game.Path 'GameAssembly.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ($problems.Count -gt 0 -or $DryRun) {
    $what = if ($DryRun) { 'no zip (dry run)' } else { 'no zip' }
    $why = if ($problems.Count -gt 0) { ': ' + (($problems | ForEach-Object { ($_ -split "`n")[0] }) -join '; ') } else { '' }
    Write-PackageLog $what "$gate$why" $gameFingerprint
}
foreach ($problem in $problems) { Write-Warning "Release: $problem" }
if ($problems.Count -gt 0) {
    throw "Not packaging $version ($($problems.Count) problem(s), above)."
}
if ($DryRun) {
    Write-Host "Dry run: the gate passed; would build and write $publicZip and $melonBundleZip (and their .sha256 and release notes)."
    Write-Host 'Nothing was built or written but packages.log.'
    return
}

# The READ ME with its version and its variant's blocks, in Windows line endings (every text editor shows its lines),
# plain ASCII without a byte order mark (zipcheck.py checks both).
function Expand-ReadMe([string]$Text, [string]$Variant) {
    $skip = $false
    $kept = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($Text -split '\r?\n')) {
        if ($line -match '^\s*\{\{([#/])(FRIENDS|PUBLIC)\}\}\s*$') {
            $skip = ($Matches[1] -eq '#') -and ($Matches[2] -ne $Variant)
            continue
        }
        if (-not $skip) { $kept.Add($line.Replace('{{VERSION}}', $versionShown)) }
    }
    $kept -join "`r`n"
}

function ConvertTo-CrLf([string]$Text) { [regex]::Replace($Text, '\r?\n', "`r`n") }

# Writes every file of one variant's zip under a fresh stage folder.
function New-Stage([string]$Folder, [string]$Variant, [string]$BuildFolder) {
    if (Test-Path -LiteralPath $Folder) { Remove-Item -Recurse -Force -LiteralPath $Folder }
    $mod = Join-Path $Folder $modFolder
    New-Item -ItemType Directory -Force (Join-Path $mod 'Plugins'), (Join-Path $mod 'UserLibs') | Out-Null
    foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $BuildFolder $file[0]) -Destination (Join-Path $mod $file[1]) }
    $ascii = New-Object System.Text.ASCIIEncoding
    [IO.File]::WriteAllBytes((Join-Path $mod 'LICENSE.txt'), $ascii.GetBytes((ConvertTo-CrLf ([IO.File]::ReadAllText($license)))))
    [IO.File]::WriteAllBytes((Join-Path $mod 'THIRD-PARTY-NOTICES.txt'), $ascii.GetBytes((ConvertTo-CrLf ([IO.File]::ReadAllText($notices)))))
    # The manifest MelonLoader looks for to treat the folder as one mod (it never reads the content): name, version and
    # a one-line description, no address.
    $manifest = "{`n  `"name`": `"T2T Online`",`n  `"version_number`": `"$version`",`n  `"description`": `"$manifestDescription`"`n}`n"
    [IO.File]::WriteAllBytes((Join-Path $mod 'manifest.json'), $ascii.GetBytes($manifest))
    $readMe = Expand-ReadMe ([IO.File]::ReadAllText($readMeSource)) $Variant
    [IO.File]::WriteAllBytes((Join-Path $Folder $readMeName), $ascii.GetBytes($readMe))
}

# Builds the plugin (and Core and Steam) from scratch into a fresh folder. Without the game's interop assemblies the
# build quietly leaves out every part that touches the game (HAS_INTEROP) and the zip would hold a mod that does
# nothing, so their absence is an error, and the built mod is checked for that code.
function Build-Plugin([string]$OutFolder) {
    $interop = Join-Path $game.Path 'MelonLoader\Il2CppAssemblies'
    if (-not (Test-Path (Join-Path $interop '*.dll'))) {
        throw "No interop assemblies in $interop. Launch the game once with MelonLoader 0.7.3 installed (it generates them), then package again."
    }
    if (Test-Path -LiteralPath $OutFolder) { Remove-Item -Recurse -Force -LiteralPath $OutFolder }
    $code = Invoke-Program 'dotnet' @('build', (Join-Path $repo 'src\T2TOnline.Plugin'), '-c', 'Release',
        "-p:T2TGameDir=$($game.Path)", '--no-incremental', '-o', $OutFolder, '--nologo', '-v', 'quiet')
    if ($code -ne 0) { throw "Plugin build failed (exit $code)." }
    $plugin = Join-Path $OutFolder 'T2TOnline.dll'
    # The game's code is referenced (Il2CppMainScriptsAssembly) and the runtime class compiled (ModRuntime) only with
    # the interop assemblies; neither name occurs in the mod's embedded text files.
    $metadata = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($plugin))
    foreach ($name in 'Il2CppMainScriptsAssembly', 'ModRuntime') {
        if (-not $metadata.Contains($name)) {
            throw "The built T2TOnline.dll has no ${name}: it was built without the game's interop assemblies and would do nothing. Not packaging it."
        }
    }
    $built = (Get-Item -LiteralPath $plugin).VersionInfo.ProductVersion -replace '\+.*$', ''
    if ($built -ne $version) {
        throw "The built T2TOnline.dll says version $built, but Directory.Build.props says $version. Not packaging it."
    }
    # No machine paths: Release builds map the repository root to /_/ (Directory.Build.props), so no DLL or PDB may
    # name this build's folders, as text or as UTF-16. forbidden_strings.py scans the zips again below.
    $machinePaths = @($repo, $repo.Replace('\', '/'), $game.Path, $game.Path.Replace('\', '/'), $dist, $env:USERPROFILE,
        [IO.Path]::GetTempPath().TrimEnd('\')) | Where-Object { $_ } | Select-Object -Unique
    foreach ($file in $files) {
        $source = Join-Path $OutFolder $file[0]
        if (-not (Test-Path -LiteralPath $source)) { throw "The build has no $($file[0])." }
        $bytes = [IO.File]::ReadAllBytes($source)
        $texts = @([Text.Encoding]::GetEncoding(28591).GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))
        if ($bytes.Length -gt 1) { $texts += [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1) }
        foreach ($path in $machinePaths) {
            foreach ($text in $texts) {
                if ($text.IndexOf($path, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    throw "The built $($file[0]) contains a path of this PC ($path). Not packaging it."
                }
            }
        }
    }
    # Versions, references, PDB structure (zipcheck.py built); the zips get the same checks read back below.
    $code = Invoke-Python $zipcheck @('built', '--dir', $OutFolder, '--repo', $repo, '--game-dir', $game.Path, '--commit', $commit)
    if ($code -ne 0) { throw "zipcheck.py built failed on the build (exit $code; listed above). Not packaging $version." }
}

# Writes both zips (as .partial files next to the real names) from one build.
function New-Zips([string]$BuildFolder, [string]$StageRoot, [string[]]$Names) {
    $public = Join-Path $StageRoot 'public'
    $friends = Join-Path $StageRoot 'friends'
    New-Stage $public 'PUBLIC' $BuildFolder
    New-Stage $friends 'FRIENDS' $BuildFolder
    $code = Invoke-Python $writeZip @('--out', $Names[0], '--time', $commitTime, '--stage', $public)
    if ($code -ne 0) { throw "write_zip.py failed for the public zip (exit $code)." }
    $code = Invoke-Python $writeZip @('--out', $Names[1], '--time', $commitTime, '--stage', $friends, '--copy-from', $melonZip)
    if ($code -ne 0) { throw "write_zip.py failed for the with-MelonLoader zip (exit $code)." }
}

$out = Join-Path $dist 'build'
$out2 = Join-Path $dist 'build-again'
$stage = Join-Path $dist 'stage'
$stage2 = Join-Path $dist 'stage-again'
$partials = @("$publicZip.partial", "$melonBundleZip.partial")
$partials2 = @("$publicZip.again.partial", "$melonBundleZip.again.partial")
# Exactly these files, each required.
$files = @(
    @('T2TOnline.dll', 'Plugins'), @('T2TOnline.pdb', 'Plugins'),
    @('T2TOnline.Core.dll', 'UserLibs'), @('T2TOnline.Core.pdb', 'UserLibs'),
    @('T2TOnline.Steam.dll', 'UserLibs'), @('T2TOnline.Steam.pdb', 'UserLibs')
)
$written = $false
try {
    # 1. Build.
    Build-Plugin $out

    # 2. MelonLoader, verified.
    $cache = Join-Path $dist 'cache'
    New-Item -ItemType Directory -Force $cache | Out-Null
    $melonZip = Join-Path $cache "MelonLoader.x64-$($pin['version']).zip"
    if (-not (Test-Path -LiteralPath $melonZip)) {
        Invoke-WebRequest -Uri $pin['url'] -OutFile $melonZip -UseBasicParsing
    }
    $actual = (Get-FileHash -LiteralPath $melonZip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $pin['sha256']) {
        Remove-Item -LiteralPath $melonZip
        throw "MelonLoader download hash mismatch ($actual); deleted it. Expected $($pin['sha256'])."
    }

    # 3. Stage and zip. Each zip is written under a temporary name and renamed only once complete, checked and
    #    scanned, so a failed run never leaves a zip under a real name. The MelonLoader zip's entries are copied as they
    #    are, byte for byte; a file of ours with the same path as one of MelonLoader's is an error.
    New-Zips $out $stage $partials

    # 4. Each zip read back and checked (entries, times, order, size, documents, assemblies, test install, update
    #    simulation, MelonLoader's version).
    $common = @('--version', $version, '--commit', $commit, '--commit-time', $commitTime, '--repo', $repo,
        '--game-dir', $game.Path, '--ml-zip', $melonZip)
    $publicArgs = @('zip', '--zip', $partials[0], '--variant', 'public') + $common
    if ($PreviousZip) { $publicArgs += @('--previous-zip', $PreviousZip) }
    Write-Host "Reading the public zip back ..."
    $code = Invoke-Python $zipcheck $publicArgs
    if ($code -ne 0) { throw "zipcheck.py found a problem in the public zip (exit $code; listed above). Not packaging $version." }
    Write-Host "Reading the with-MelonLoader zip back ..."
    $code = Invoke-Python $zipcheck (@('zip', '--zip', $partials[1], '--variant', 'friends', '--public-zip', $partials[0]) + $common)
    if ($code -ne 0) { throw "zipcheck.py found a problem in the with-MelonLoader zip (exit $code; listed above). Not packaging $version." }

    # 5. Both zips against the forbidden patterns, every entry and binaries included, except MelonLoader's own files:
    #    those are copied unchanged from the release checked against its SHA-256 above (the scan confirms each is
    #    identical to it), and third-party text there can match a pattern by chance.
    $scanArguments = @('--patterns', $PatternsFile, '--skip-identical-to', $melonZip, 'zip') + $partials
    $code = Invoke-Python (Join-Path $checks 'forbidden_strings.py') $scanArguments
    if ($code -ne 0) {
        throw "forbidden_strings.py zip found forbidden text in the zips (exit $code; listed above). Not packaging $version."
    }

    # 6. Reproducible: a second build of the same commit gives the same bytes in both zips.
    if ($SkipRebuildCheck) {
        Write-Host 'Rebuild check skipped (-SkipRebuildCheck): this run did not prove the zips are reproducible.'
    }
    else {
        Write-Host 'Building again to prove the zips are reproducible ...'
        Build-Plugin $out2
        New-Zips $out2 $stage2 $partials2
        for ($i = 0; $i -lt 2; $i++) {
            $first = (Get-FileHash -LiteralPath $partials[$i] -Algorithm SHA256).Hash.ToLowerInvariant()
            $second = (Get-FileHash -LiteralPath $partials2[$i] -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($first -ne $second) {
                throw "A second build of the same commit gave a different $(Split-Path $zips[$i] -Leaf) ($first, then $second): the release is not reproducible. Not packaging $version."
            }
        }
        Write-Host 'ok   reproducible: two builds of this commit give identical zips'
    }

    # 7. The real names, and each zip's SHA-256 next to it in sha256sum's format: send it separately from the zip (a
    #    message, not the same upload), so a player can check the zip they got is this one (Get-FileHash <zip> in
    #    PowerShell). The release notes carry both digests and the commit.
    $notes = New-Object System.Collections.Generic.List[string]
    $notes.Add("T2T Online $versionShown ($version)")
    $notes.Add('')
    $notes.Add("Commit: $commit ($commitTime)")
    $notes.Add("Built with .NET SDK $sdk")
    $notes.Add("MelonLoader in the with-MelonLoader zip: $($pin['version']), the official release unchanged (SHA-256 of its zip: $($pin['sha256']))")
    $notes.Add('')
    for ($i = 0; $i -lt $zips.Count; $i++) {
        Move-Item -Force -LiteralPath $partials[$i] -Destination $zips[$i]
        $zipSha256 = (Get-FileHash -LiteralPath $zips[$i] -Algorithm SHA256).Hash.ToLowerInvariant()
        $leaf = Split-Path $zips[$i] -Leaf
        [IO.File]::WriteAllText("$($zips[$i]).sha256", "$zipSha256  $leaf`n", (New-Object System.Text.ASCIIEncoding))
        Write-PackageLog $leaf 'passed' $gameFingerprint $zipSha256
        Write-Host "Wrote $($zips[$i])"
        Write-Host "  SHA-256 $zipSha256 (also in $leaf.sha256)"
        $notes.Add($leaf + $(if ($i -eq 1) { '  (for the maintainers'' own testers, never published)' } else { '' }))
        $notes.Add("  SHA-256: $zipSha256")
        $notes.Add("  Size: $((Get-Item -LiteralPath $zips[$i]).Length) bytes")
    }
    $notes.Add('')
    $notes.Add("Check a download: in PowerShell, Get-FileHash .\T2TOnline-$version.zip -Algorithm SHA256 must print the SHA-256 above.")
    [IO.File]::WriteAllText($notesFile, (($notes -join "`n") + "`n"), (New-Object System.Text.ASCIIEncoding))
    Write-Host "Wrote $notesFile"
    $partials = @()
    $written = $true
}
catch {
    Write-PackageLog 'no zip' "passed, packaging failed: $($_.Exception.Message)" $gameFingerprint
    throw
}
finally {
    foreach ($partial in $partials + $partials2) { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial } }
    foreach ($folder in $stage, $stage2, $out, $out2) { if (Test-Path -LiteralPath $folder) { Remove-Item -Recurse -Force -LiteralPath $folder } }
}
Write-Host "Released $version from commit $commit. Publish only $(Split-Path $publicZip -Leaf); the with-MelonLoader zip is for the maintainers' own testers."
