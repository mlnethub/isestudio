# Invoke-IriRdfRelocation.ps1
#
# Thin wrapper around the OnToPilot.Migration CLI host that drives
# IriMigrationCommand.RunAsync(rdf). The PowerShell layer exists so the
# cutover orchestration can drive the Oxigraph RocksDB prefix rewrite
# the same way it drives Export-PythonRdf.ps1 — as a PowerShell child
# process with structured parameters, captured stdout, and explicit exit
# codes that the IRI cutover gate can inspect.
#
# The actual source-readonly → enumerate → rewrite → bulk-load logic
# lives in src/OnToPilot.Migration/Iri/IriRdfRelocator.cs (Phase 1).
# This script does no RDF work of its own beyond argument translation.
#
# Usage (rehearsal against a copy of the production RocksDB directory):
#   pwsh migration/scripts/Invoke-IriRdfRelocation.ps1 `
#       -Source /var/lib/ontopilot/oxigraph-readonly `
#       -Target /var/lib/ontopilot/oxigraph-iri-stage `
#       -FromPrefix "http://ontopilot.local/" `
#       -ToPrefix   "http://goodcrew.local/"
#
# The relocator never writes to -Source; the target directory must NOT
# exist before the call (the relocator refuses to overwrite a populated
# directory so an operator can't accidentally clobber a live store).
#
# Exit codes:
#   0 - relocation completed; per-graph quad counts + SHA-256 emitted
#       on stdout for the cutover record's expected-rdf-query-hashes.
#   1 - relocation failed (source missing, target pre-exists, bulk-load
#       error, bad args).
#   2 - environment failure (missing dotnet, project not built, etc.).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Target,

    [Parameter(Mandatory = $false)]
    [string]$FromPrefix = "http://ontopilot.local/",

    [Parameter(Mandatory = $false)]
    [string]$ToPrefix = "http://goodcrew.local/",

    [Parameter(Mandatory = $false)]
    [string]$ProjectPath = "src/OnToPilot.Migration/OnToPilot.Migration.csproj"
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "Invoke-IriRdfRelocation.ps1 requires PowerShell 7+ (found $($PSVersionTable.PSVersion))."
    exit 2
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if (-not $dotnet) {
    Write-Error "Invoke-IriRdfRelocation.ps1: 'dotnet' was not found on PATH. Install the .NET SDK."
    exit 2
}

$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath -ErrorAction SilentlyContinue)?.Path
if (-not $ProjectPath -or -not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
    Write-Error "Invoke-IriRdfRelocation.ps1: project file '$ProjectPath' not found."
    exit 2
}

$SourceFull = (Resolve-Path -LiteralPath $Source -ErrorAction SilentlyContinue)?.Path
if (-not $SourceFull -or -not (Test-Path -LiteralPath $SourceFull -PathType Container)) {
    Write-Error "Invoke-IriRdfRelocation.ps1: -Source '$Source' does not exist or is not a directory."
    exit 1
}

if (Test-Path -LiteralPath $Target) {
    Write-Error "Invoke-IriRdfRelocation.ps1: -Target '$Target' already exists; the relocator refuses to overwrite a populated directory. Remove it or pick a fresh path."
    exit 1
}

if ($FromPrefix -eq $ToPrefix) {
    Write-Error "Invoke-IriRdfRelocation.ps1: -FromPrefix and -ToPrefix are identical ('$FromPrefix'). No work to do."
    exit 1
}

# Argument list forwarded verbatim to the .NET host. Keep this in sync
# with IriMigrationCommand.RdfCliArgs in src/OnToPilot.Migration/Iri/.
$cliArgs = @(
    "iri",
    "rdf",
    "--source", $SourceFull,
    "--target", $Target,
    "--from-prefix", $FromPrefix,
    "--to-prefix", $ToPrefix
)

Write-Host "[Invoke-IriRdfRelocation] running: dotnet run --project $ProjectPath --no-build -- $($cliArgs -join ' ')"
& $dotnet run --project $ProjectPath --no-build -- @cliArgs
$exitCode = $LASTEXITCODE
Write-Host "[Invoke-IriRdfRelocation] dotnet exited with code $exitCode"
exit $exitCode