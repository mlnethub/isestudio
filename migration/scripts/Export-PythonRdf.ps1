# Export-PythonRdf.ps1
#
# Exports the Python / pyoxigraph-managed Oxigraph RocksDB directory
# (the original source the .NET migration MUST never open) to an
# N-Quads file the .NET fallback path can consume.
#
# This script is the ONLY producer of N-Quads in the data-cutover
# pipeline. It runs against the source dir; the .NET command never sees
# the source. The N-Quads file lands in $Work/nquads-export.nq so
# RdfMigrationCommand's fallback path can pick it up.
#
# Usage:
#   pwsh migration/scripts/Export-PythonRdf.ps1
#       -Source backend/data/oxigraph
#       -Work   .artifacts/rdf-test/work
#
# Exit codes:
#   0 - N-Quads export written and non-empty.
#   1 - source directory does not exist or is empty.
#   2 - neither pyoxigraph nor oxigraph-cli could run the export.
#   3 - PowerShell version too old (requires 7+).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Work
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "Export-PythonRdf.ps1 requires PowerShell 7+ (found $($PSVersionTable.PSVersion))."
    exit 3
}

$Source = (Resolve-Path -LiteralPath $Source -ErrorAction SilentlyContinue)?.Path
if (-not $Source -or -not (Test-Path -LiteralPath $Source -PathType Container)) {
    Write-Error "Export-PythonRdf.ps1: source directory '$Source' does not exist or is not a directory."
    exit 1
}

if (-not (Test-Path -LiteralPath $Work -PathType Container)) {
    New-Item -ItemType Directory -Path $Work -Force | Out-Null
}

$out = Join-Path $Work 'nquads-export.nq'

# Preferred path: pyoxigraph (the Python library OnToPilot uses) — it
# opens the RocksDB directory and writes N-Quads via the same code path
# the production Python backend uses.
$python = (Get-Command python -ErrorAction SilentlyContinue)?.Source
if (-not $python) {
    $python = (Get-Command python3 -ErrorAction SilentlyContinue)?.Source
}

if ($python) {
    Write-Host "[Export-PythonRdf] Trying pyoxigraph via $python"
    $script = @"
import sys
try:
    import pyoxigraph
except ImportError:
    sys.stderr.write('pyoxigraph not installed\n')
    sys.exit(11)
store = pyoxigraph.Store('$($Source -replace "'", "''")')
nq = store.dump(format=pyoxigraph.RdfFormat.N_QUADS)
sys.stdout.write(nq)
"@
    & $python -c $script 2>$null | ForEach-Object { $_ } | Set-Content -LiteralPath $out -Encoding utf8NoBOM
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $out) -and ((Get-Item $out).Length -gt 0)) {
        $bytes = (Get-Item $out).Length
        Write-Host "[Export-PythonRdf] pyoxigraph wrote $bytes bytes to $out"
        exit 0
    }
    Write-Host "[Export-PythonRdf] pyoxigraph path failed (exit=$LASTEXITCODE); trying oxigraph-cli"
}

# Fallback: oxigraph-cli (the standalone binary) — supports
# `oxigraph convert --from oxigraph --to nquads`.
$oxic = (Get-Command oxigraph -ErrorAction SilentlyContinue)?.Source
if ($oxic) {
    Write-Host "[Export-PythonRdf] Trying oxigraph-cli at $oxic"
    & $oxic convert --from-store "$Source" --to nquads --output "$out" 2>$null
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $out) -and ((Get-Item $out).Length -gt 0)) {
        $bytes = (Get-Item $out).Length
        Write-Host "[Export-PythonRdf] oxigraph-cli wrote $bytes bytes to $out"
        exit 0
    }
    Write-Host "[Export-PythonRdf] oxigraph-cli failed (exit=$LASTEXITCODE)"
}

Write-Error "Export-PythonRdf.ps1 FAILED: neither pyoxigraph nor oxigraph-cli could produce an N-Quads export of '$Source'. Install pyoxigraph (pip install pyoxigraph) or place the oxigraph-cli binary on PATH."
exit 2
