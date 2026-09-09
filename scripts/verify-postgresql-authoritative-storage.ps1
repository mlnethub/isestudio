# Verifies the runtime RDF dependency boundary: PostgreSQL is the
# runtime-authoritative store, so the ISEStudio runtime project must not
# reference Oxigraph, Oxigraph.Extensions, RocksDB, or the legacy
# StoreWrapper — not even in comments. The migration tool
# (ISEStudio.Migration) and the standalone OxigraphProbe are the
# documented exceptions and are NOT scanned by this gate.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeDir = Join-Path $repoRoot 'src\ISEStudio'

$matches = Get-ChildItem $runtimeDir -Recurse -File -Include *.cs,*.csproj |
    Select-String -Pattern 'Oxigraph|Oxigraph\.Extensions|RocksDB|StoreWrapper'
if ($matches) {
    $matches | ForEach-Object { Write-Error "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
    exit 1
}
Write-Host 'Runtime RDF dependency boundary verified.'
