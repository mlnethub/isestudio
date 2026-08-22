# Invoke-IriShardRewrite.ps1
#
# Thin wrapper around the OnToPilot.Migration CLI host that drives
# IriMigrationCommand.RunAsync(shards). The PowerShell layer exists so
# the cutover orchestration can drive the on-disk shard rewrite the
# same way it drives the other migrators — as a PowerShell child
# process with structured parameters, captured stdout, and explicit
# exit codes that the IRI cutover gate can inspect.
#
# The actual line-anchored IRI REPLACE + manifest SHA-256 refresh
# logic lives in src/OnToPilot.Migration/Iri/IriShardRewriter.cs
# (Phase 1). This script does no shard work of its own beyond
# argument translation.
#
# The rewriter walks {releasesRoot}/{releaseId}/*.{nq,ks.json,manifest}
# and {exportsRoot}/{publicId}/{jobLegacyId}/*.nq and rewrites every
# legacy-prefix IRI to the new prefix. SHA-256 entries in
# manifest.json are refreshed in-place so the manifest stays
# byte-consistent with the files it describes.
#
# Usage (rehearsal, dry-run):
#   pwsh migration/scripts/Invoke-IriShardRewrite.ps1 `
#       -ReleasesRoot /var/lib/ontopilot/releases `
#       -ExportsRoot /var/lib/ontopilot/exports `
#       -FromPrefix "http://ontopilot.local/" `
#       -ToPrefix   "http://goodcrew.local/" `
#       -DryRun
#
# Usage (production cutover, apply mode):
#   pwsh migration/scripts/Invoke-IriShardRewrite.ps1 `
#       -ReleasesRoot /var/lib/ontopilot/releases `
#       -ExportsRoot /var/lib/ontopilot/exports `
#       -FromPrefix "http://ontopilot.local/" `
#       -ToPrefix   "http://goodcrew.local/"
#
# Exit codes:
#   0 - rewrite completed (or dry-run completed); per-shard counts
#       emitted on stdout for the cutover record's expected-shard-shas.
#   1 - rewrite failed (bad args, root not found, I/O error).
#   2 - environment failure (missing dotnet, project not built, etc.).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleasesRoot,

    [Parameter(Mandatory = $true)]
    [string]$ExportsRoot,

    [Parameter(Mandatory = $false)]
    [string]$FromPrefix = "http://ontopilot.local/",

    [Parameter(Mandatory = $false)]
    [string]$ToPrefix = "http://goodcrew.local/",

    [Parameter(Mandatory = $false)]
    [switch]$DryRun,

    [Parameter(Mandatory = $false)]
    [string]$ProjectPath = "src/OnToPilot.Migration/OnToPilot.Migration.csproj"
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "Invoke-IriShardRewrite.ps1 requires PowerShell 7+ (found $($PSVersionTable.PSVersion))."
    exit 2
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if (-not $dotnet) {
    Write-Error "Invoke-IriShardRewrite.ps1: 'dotnet' was not found on PATH. Install the .NET SDK."
    exit 2
}

$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath -ErrorAction SilentlyContinue)?.Path
if (-not $ProjectPath -or -not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
    Write-Error "Invoke-IriShardRewrite.ps1: project file '$ProjectPath' not found."
    exit 2
}

$releasesFull = (Resolve-Path -LiteralPath $ReleasesRoot -ErrorAction SilentlyContinue)?.Path
if (-not $releasesFull -or -not (Test-Path -LiteralPath $releasesFull -PathType Container)) {
    Write-Error "Invoke-IriShardRewrite.ps1: -ReleasesRoot '$ReleasesRoot' does not exist or is not a directory."
    exit 1
}
$exportsFull = (Resolve-Path -LiteralPath $ExportsRoot -ErrorAction SilentlyContinue)?.Path
if (-not $exportsFull -or -not (Test-Path -LiteralPath $exportsFull -PathType Container)) {
    Write-Error "Invoke-IriShardRewrite.ps1: -ExportsRoot '$ExportsRoot' does not exist or is not a directory."
    exit 1
}

if ($FromPrefix -eq $ToPrefix) {
    Write-Error "Invoke-IriShardRewrite.ps1: -FromPrefix and -ToPrefix are identical ('$FromPrefix'). No work to do."
    exit 1
}

# Argument list forwarded verbatim to the .NET host. Keep this in sync
# with IriMigrationCommand.ShardsCliArgs in src/OnToPilot.Migration/Iri/.
$cliArgs = @(
    "iri",
    "shards",
    "--releases-root", $releasesFull,
    "--exports-root", $exportsFull,
    "--from-prefix", $FromPrefix,
    "--to-prefix", $ToPrefix
)
if ($DryRun) {
    $cliArgs += @("--dry-run")
}

Write-Host "[Invoke-IriShardRewrite] running: dotnet run --project $ProjectPath --no-build -- $($cliArgs -join ' ')"
& $dotnet run --project $ProjectPath --no-build -- @cliArgs
$exitCode = $LASTEXITCODE
Write-Host "[Invoke-IriShardRewrite] dotnet exited with code $exitCode"
exit $exitCode