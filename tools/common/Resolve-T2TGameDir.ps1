<#
.SYNOPSIS
    Defines Resolve-T2TGameDir, the one place the tools find the Tape to Tape game folder. Dot-source this file.

.DESCRIPTION
    Resolve-T2TGameDir returns an object with Path (the game folder, without a trailing backslash) and Source (where it
    came from), or throws a message that says how to provide the folder. It looks, in order, at:
      1. the -GameDir parameter the calling script was given;
      2. the T2T_GAME_DIR environment variable;
      3. T2TGameDir in the repository's Directory.Build.local.props (see Directory.Build.local.props.example);
      4. Steam's own records: SteamPath under HKCU:\Software\Valve\Steam, the libraries in its
         steamapps\libraryfolders.vdf, and the library that holds appmanifest_1566200.acf (Tape to Tape's Steam app),
         giving <library>\steamapps\common\<its installdir, normally "Tape to Tape">.
    The first one that is set decides: a folder named by 1 to 3 that doesn't exist is an error, not a reason to look
    further, so a typo is never silently replaced by another install. Only reads; changes nothing.
#>

function Resolve-T2TGameDir {
    [CmdletBinding()]
    param(
        # The calling script's -GameDir, if any.
        [string]$GameDir,
        # The repository root, for Directory.Build.local.props.
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $howTo = 'Pass -GameDir, set T2T_GAME_DIR, or set T2TGameDir in Directory.Build.local.props ' +
        '(copy Directory.Build.local.props.example).'

    function Get-CheckedFolder([string]$path, [string]$source) {
        # Relative to PowerShell's current folder, not the process's (which .NET's own GetFullPath would use).
        $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path).TrimEnd('\', '/')
        if (-not (Test-Path -LiteralPath $full -PathType Container)) {
            throw "The game folder from $source doesn't exist: $full. $howTo"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $full 'Tape to Tape.exe') -PathType Leaf)) {
            throw "The game folder from $source has no 'Tape to Tape.exe': $full. $howTo"
        }
        [pscustomobject]@{ Path = $full; Source = $source }
    }

    if ($GameDir) {
        return Get-CheckedFolder $GameDir 'the -GameDir parameter'
    }
    if ($env:T2T_GAME_DIR) {
        return Get-CheckedFolder $env:T2T_GAME_DIR 'the T2T_GAME_DIR environment variable'
    }

    $localProps = Join-Path $RepoRoot 'Directory.Build.local.props'
    if (Test-Path -LiteralPath $localProps -PathType Leaf) {
        $xml = [xml](Get-Content -LiteralPath $localProps -Raw)
        $value = @($xml.Project.PropertyGroup | ForEach-Object { $_.T2TGameDir } |
            Where-Object { $_ } | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.InnerText } }) |
            Select-Object -First 1
        if ($value) {
            if ($value -match '\$\(') {
                throw "T2TGameDir in $localProps uses an MSBuild property ($value); the tools need a plain folder. $howTo"
            }
            return Get-CheckedFolder $value 'Directory.Build.local.props'
        }
    }

    $steam = $null
    try { $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Stop).SteamPath }
    catch { $steam = $null }
    if (-not $steam) {
        throw "No game folder given, and Steam isn't installed for this Windows user (no SteamPath in the registry). $howTo"
    }
    $steam = [IO.Path]::GetFullPath($steam.Replace('/', '\'))
    $libraries = New-Object System.Collections.Generic.List[string]
    $libraries.Add($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf -PathType Leaf) {
        foreach ($match in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
            # The file is Valve's KeyValues text: a backslash in a path is written twice.
            $library = [IO.Path]::GetFullPath($match.Groups[1].Value.Replace('\\', '\'))
            if (-not ($libraries -contains $library)) { $libraries.Add($library) }
        }
    }
    foreach ($library in $libraries) {
        $manifest = Join-Path $library 'steamapps\appmanifest_1566200.acf'
        if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { continue }
        $installDir = 'Tape to Tape'
        $found = [regex]::Match((Get-Content -LiteralPath $manifest -Raw), '"installdir"\s+"([^"]+)"')
        if ($found.Success) { $installDir = $found.Groups[1].Value }
        return Get-CheckedFolder (Join-Path $library "steamapps\common\$installDir") "Steam's library records ($library)"
    }
    throw "No game folder given, and none of Steam's libraries ($($libraries -join '; ')) has Tape to Tape installed. $howTo"
}
