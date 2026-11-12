<#
.SYNOPSIS
    Rebuilds T2TOnline.Core and T2TOnline.Steam from the checked-out source and compares them, byte for byte, with the
    ones in a release's zip.

.DESCRIPTION
    Core and Steam need no game files to build, so anyone can rebuild them from the tag a release was made from and
    confirm the zip holds exactly that source's output: the DLLs and their PDBs must be identical. The plugin
    (T2TOnline.dll) compiles against the game's own interop assemblies, which only a machine with the game has, so it
    is not compared; it is listed as skipped. Release builds are deterministic (Directory.Build.props: Deterministic,
    source paths mapped to /_/, no Source Link), so the result does not depend on the folder or the PC the build ran
    in, only on the SDK global.json pins. Entries are found by file name, whatever folder of the zip they are in.

    Exit 0: every compared file is identical. Exit 1: a file differs or is missing from the zip. The tag-time workflow
    (.github\workflows\reproducible.yml) runs this when a release is published.

.PARAMETER Zip
    The release's public zip (T2TOnline-<version>.zip).

.PARAMETER Repo
    The source tree to build. Default: the repository this script is in.

.PARAMETER WorkDir
    Where the builds go (emptied first). Default: a new folder in the system temp folder, removed afterwards.

.EXAMPLE
    git checkout v0.12.2-beta; .\tools\package\Compare-ReleaseBuild.ps1 -Zip .\T2TOnline-0.12.2-beta.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Zip,
    [string]$Repo,
    [string]$WorkDir
)

$ErrorActionPreference = 'Stop'
function Get-FullPath([string]$path) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path).TrimEnd('\', '/') }
$root = if ($Repo) { Get-FullPath $Repo } else { (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\') }
$zipPath = Get-FullPath $Zip
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw "No zip at $zipPath" }
$ownWork = -not $WorkDir
$work = if ($WorkDir) { Get-FullPath $WorkDir } else { Join-Path ([IO.Path]::GetTempPath()) ('t2t-rebuild-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $work) { Remove-Item -Recurse -Force -LiteralPath $work }
New-Item -ItemType Directory -Force $work | Out-Null

try {
    # What a release build does (tools\package\New-ReleasePackage.ps1), without the plugin. ContinuousIntegrationBuild is
    # forced off so a build on a CI runner (which sets CI) matches one made on a PC.
    foreach ($project in 'T2TOnline.Core', 'T2TOnline.Steam') {
        $out = Join-Path $work $project
        & dotnet build (Join-Path $root "src\$project") -c Release --no-incremental -o $out --nologo -v quiet -p:ContinuousIntegrationBuild=false
        if ($LASTEXITCODE -ne 0) { throw "Building $project failed (exit $LASTEXITCODE)." }
    }

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    $failed = 0
    try {
        foreach ($project in 'T2TOnline.Core', 'T2TOnline.Steam') {
            foreach ($extension in 'dll', 'pdb') {
                $name = "$project.$extension"
                $entries = @($archive.Entries | Where-Object { ($_.FullName -split '/')[-1] -eq $name })
                if ($entries.Count -ne 1) {
                    Write-Host "FAIL $name : the zip has $($entries.Count) entries of that name, expected 1"
                    $failed++
                    continue
                }
                $sha = [Security.Cryptography.SHA256]::Create()
                $stream = $entries[0].Open()
                try { $zipHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() } finally { $stream.Dispose(); $sha.Dispose() }
                $builtHash = (Get-FileHash -LiteralPath (Join-Path (Join-Path $work $project) $name) -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($zipHash -eq $builtHash) { Write-Host "ok   $name : identical to the rebuild ($($builtHash.Substring(0, 16))...)" }
                else { Write-Host "FAIL $name : the zip's copy ($($zipHash.Substring(0, 16))...) differs from the rebuild ($($builtHash.Substring(0, 16))...)"; $failed++ }
            }
        }
    }
    finally { $archive.Dispose() }
    Write-Host 'skip T2TOnline.dll and .pdb : the plugin compiles against the game''s own assemblies, so only a machine with the game can rebuild it'
    if ($failed -gt 0) { Write-Host "Compare-ReleaseBuild: $failed difference(s)"; exit 1 }
    Write-Host 'Compare-ReleaseBuild: Core and Steam match the release byte for byte'
}
finally {
    if ($ownWork -and (Test-Path -LiteralPath $work)) { Remove-Item -Recurse -Force -LiteralPath $work }
}
