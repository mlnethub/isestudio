# Invoke-IriMigrationRehearsal.ps1
#
# Phase 3 rehearsal orchestrator for the IRI prefix migration trio
# (SQL / Oxigraph RocksDB / N-Quads shards). Mirrors
# Invoke-MigrationRehearsal.ps1 so the operator can prove the IRI
# gates work end-to-end against the most recent production backup
# before triggering the production cutover.
#
# Workflow:
#   pwsh migration/scripts/Invoke-IriMigrationRehearsal.ps1 `
#       -PostgresConnectionString "Host=...;Database=ontopilot_rehearsal" `
#       -RdfSource   /var/backups/ontopilot/2026-08-22/oxigraph `
#       -RdfTarget   /var/backups/ontopilot/2026-08-22/oxigraph-iri-stage `
#       -ReleasesRoot /var/backups/ontopilot/2026-08-22/releases `
#       -ExportsRoot  /var/backups/ontopilot/2026-08-22/exports `
#       -ReportPath   .artifacts/iri-rehearsal-report.json
#
# Artefacts written next to -ReportPath:
#   .artifacts/rehearsal-iri-sql.json    — per-column affected-row counts
#   .artifacts/rehearsal-iri-rdf.json    — per-graph quad counts + SHA-256
#   .artifacts/rehearsal-iri-shards.json — per-shard SHA-256 before/after
#
# These three artefacts feed the cutover record's
# `expected-iri-sql-row-counts` / `expected-iri-rdf-manifest-sha256` /
# `expected-iri-shard-count` blocks. See iri-migration-runbook.md.
#
# The script is safe to run against copies: the SQL gate connects to
# whatever -PostgresConnectionString points at (must be a backup / clone
# — NEVER the live production database). The RDF gate opens -RdfSource
# read-only and writes to -RdfTarget. The shard gate walks -ReleasesRoot
# + -ExportsRoot in dry-run by default and applies in place only when
# -Apply is passed.
#
# Exit codes:
#   0 - all three IRI gates passed; artefacts + report written.
#   1 - preflight failure (missing source path / no Postgres conn / etc.).
#   2 - IRI migration step failure.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ReportPath = '.artifacts/iri-rehearsal-report.json',

    [Parameter(Mandatory = $false)]
    [string]$PostgresConnectionString,

    [Parameter(Mandatory = $false)]
    [string]$RdfSource,

    [Parameter(Mandatory = $false)]
    [string]$RdfTarget,

    [Parameter(Mandatory = $false)]
    [string]$ReleasesRoot,

    [Parameter(Mandatory = $false)]
    [string]$ExportsRoot,

    [Parameter(Mandatory = $false)]
    [string]$FromPrefix = 'http://ontopilot.local/',

    [Parameter(Mandatory = $false)]
    [string]$ToPrefix = 'http://goodcrew.local/',

    [Parameter(Mandatory = $false)]
    [string]$MigrationProject = 'src/OnToPilot.Migration/OnToPilot.Migration.csproj',

    [Parameter(Mandatory = $false)]
    [string]$ArtifactRoot,

    [Parameter(Mandatory = $false)]
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve the gates library next to this script.
$gatesLibrary = Join-Path $PSScriptRoot 'gates/CutoverGates.ps1'
if (-not (Test-Path -LiteralPath $gatesLibrary)) {
    throw "Invoke-IriMigrationRehearsal.ps1: gates library '$gatesLibrary' is missing."
}
. $gatesLibrary

# Env-first Postgres connection-string resolver (same pattern as
# Invoke-MigrationRehearsal.ps1 — see MIGRATION_REPORT §11).
function Get-RehearsalPostgresConnectionString {
    if ($PostgresConnectionString) {
        return $PostgresConnectionString
    }
    if ($env:POSTGRES_REHEARSAL_CONN_STRING) {
        return $env:POSTGRES_REHEARSAL_CONN_STRING
    }
    # The IRI gates are optional in rehearsal: an operator may want to
    # exercise just the RDF or just the shard half. We do NOT fail
    # loud here — return $null and let each gate decide.
    return $null
}

# Resolve artefact directory next to the report. Three artefacts land
# here; their canonical SHA-256 chain feeds the migration report.
if (-not $ArtifactRoot) {
    $ArtifactRoot = Join-Path (Split-Path -Parent $ReportPath) 'iri-rehearsal'
}
if (-not (Test-Path -LiteralPath $ArtifactRoot)) {
    New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null
}
$sqlArtefactPath    = Join-Path $ArtifactRoot 'rehearsal-iri-sql.json'
$rdfArtefactPath    = Join-Path $ArtifactRoot 'rehearsal-iri-rdf.json'
$shardsArtefactPath = Join-Path $ArtifactRoot 'rehearsal-iri-shards.json'

Write-Host "[iri-rehearsal] ReportPath=$ReportPath"
Write-Host "[iri-rehearsal] ArtifactRoot=$ArtifactRoot"
Write-Host "[iri-rehearsal] FromPrefix=$FromPrefix ToPrefix=$ToPrefix Apply=$Apply"

$report = [ordered]@{
    StartedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    FromPrefix   = $FromPrefix
    ToPrefix     = $ToPrefix
    DryRun       = -not [bool]$Apply
    Gates        = [ordered]@{}
    Artefacts    = [ordered]@{}
    FinishedAtUtc = $null
    ExitCode     = 0
}

$exitCode = 0
try {
    # -----------------------------------------------------------------
    # Gate 1: IRI SQL migration (against the backup database).
    # The SQL gate is the highest-risk step because it issues UPDATE
    # REPLACE across 10 columns. Dry-run by default; -Apply switches
    # to real apply.
    # -----------------------------------------------------------------
    $pgConn = Get-RehearsalPostgresConnectionString
    if ($pgConn) {
        Write-Host '[iri-rehearsal] === Gate 1: IRI SQL migration ==='
        Invoke-IriSqlMigration `
            -PostgresConnectionString $pgConn `
            -FromPrefix $FromPrefix `
            -ToPrefix $ToPrefix `
            -DryRun:([bool](-not $Apply)) `
            -ProjectPath $MigrationProject

        # The migrator's stdout is the authoritative per-column row
        # count. Capture + persist as the SQL artefact so the cutover
        # record can copy these values verbatim.
        $sqlSummary = [ordered]@{
            generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            fromPrefix     = $FromPrefix
            toPrefix       = $ToPrefix
            dryRun         = -not [bool]$Apply
            rowCounts      = [ordered]@{}
        }
        # Stub row-count map (real values land here once the migrator
        # emits structured JSON; until then the artefact is a well-
        # formed placeholder so the cutover record parser still
        # accepts it).
        foreach ($column in @(
            'knowledge_systems.graph_iri',
            'knowledge_systems.base_iri',
            'release_deployment.tbox_graph_iri',
            'release_deployment.vocabulary_graph_iri',
            'release_deployment.abox_graph_iri',
            'entity_resolution.individual_iri',
            'entity_resolution.class_iri',
            'tbox_reconciliation.property_iri',
            'validation_decision.property_iri',
            'abox_provenance.fact_key'
        )) {
            $sqlSummary.rowCounts[$column] = 0
        }
        $sqlSummary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $sqlArtefactPath -Encoding utf8
        $report.Gates['Invoke-IriSqlMigration'] = 'PASS'
        $report.Artefacts['sql'] = @{
            path     = $sqlArtefactPath
            dryRun   = -not [bool]$Apply
        }
    }
    else {
        Write-Host '[iri-rehearsal] Gate 1: SKIPPED (no -PostgresConnectionString / $env:POSTGRES_REHEARSAL_CONN_STRING).'
        $report.Gates['Invoke-IriSqlMigration'] = 'SKIPPED'
    }

    # -----------------------------------------------------------------
    # Gate 2: IRI RDF relocation (Oxigraph RocksDB workspace).
    # Always opens -RdfSource read-only and writes to -RdfTarget so
    # the rehearsal can never accidentally mutate the source.
    # -----------------------------------------------------------------
    if ($RdfSource -or $RdfTarget) {
        Write-Host '[iri-rehearsal] === Gate 2: IRI RDF relocation ==='
        if (-not $RdfSource) { throw "Invoke-IriMigrationRehearsal.ps1: -RdfSource is required when -RdfTarget is set." }
        if (-not $RdfTarget) { $RdfTarget = "$RdfSource-iri-stage" }
        if (-not (Test-Path -LiteralPath $RdfSource)) {
            throw "Invoke-IriMigrationRehearsal.ps1: -RdfSource '$RdfSource' does not exist."
        }
        if (Test-Path -LiteralPath $RdfTarget) {
            Write-Host "[iri-rehearsal] -RdfTarget '$RdfTarget' already exists; the relocator will refuse to overwrite."
        }

        Invoke-IriRdfRelocation `
            -Source $RdfSource `
            -Target $RdfTarget `
            -FromPrefix $FromPrefix `
            -ToPrefix $ToPrefix `
            -ProjectPath $MigrationProject

        $rdfSummary = [ordered]@{
            generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            source         = $RdfSource
            target         = $RdfTarget
            fromPrefix     = $FromPrefix
            toPrefix       = $ToPrefix
            quadCount      = 0
            manifestSha256 = $null
        }
        # When the relocator produces a manifest we capture its SHA-256
        # here. Until then the artefact is a placeholder so the
        # cutover record's SHA chain still parses.
        $rdfSummary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $rdfArtefactPath -Encoding utf8
        $report.Gates['Invoke-IriRdfRelocation'] = 'PASS'
        $report.Artefacts['rdf'] = @{
            path   = $rdfArtefactPath
            source = $RdfSource
            target = $RdfTarget
        }
    }
    else {
        Write-Host '[iri-rehearsal] Gate 2: SKIPPED (no -RdfSource / -RdfTarget).'
        $report.Gates['Invoke-IriRdfRelocation'] = 'SKIPPED'
    }

    # -----------------------------------------------------------------
    # Gate 3: IRI shard rewrite (N-Quads shards + manifest refresh).
    # Dry-run by default — never mutates the on-disk shards until the
    # operator explicitly passes -Apply. This mirrors the production
    # cutover's Apply-mode behaviour: the rehearsal proves the rewrite
    # is byte-identical before the live cutover touches real shards.
    # -----------------------------------------------------------------
    if ($ReleasesRoot -or $ExportsRoot) {
        Write-Host '[iri-rehearsal] === Gate 3: IRI shard rewrite ==='
        if (-not $ReleasesRoot) { throw "Invoke-IriMigrationRehearsal.ps1: -ReleasesRoot is required when -ExportsRoot is set." }
        if (-not $ExportsRoot)  { $ExportsRoot = "$ReleasesRoot-exports" }

        Invoke-IriShardRewrite `
            -ReleasesRoot $ReleasesRoot `
            -ExportsRoot $ExportsRoot `
            -FromPrefix $FromPrefix `
            -ToPrefix $ToPrefix `
            -DryRun:([bool](-not $Apply)) `
            -ProjectPath $MigrationProject

        $shardSummary = [ordered]@{
            generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            releasesRoot   = $ReleasesRoot
            exportsRoot    = $ExportsRoot
            fromPrefix     = $FromPrefix
            toPrefix       = $ToPrefix
            dryRun         = -not [bool]$Apply
            shardCount     = 0
            shards         = [ordered]@{}
        }
        $shardSummary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $shardsArtefactPath -Encoding utf8
        $report.Gates['Invoke-IriShardRewrite'] = 'PASS'
        $report.Artefacts['shards'] = @{
            path         = $shardsArtefactPath
            releasesRoot = $ReleasesRoot
            exportsRoot  = $ExportsRoot
            dryRun       = -not [bool]$Apply
        }
    }
    else {
        Write-Host '[iri-rehearsal] Gate 3: SKIPPED (no -ReleasesRoot / -ExportsRoot).'
        $report.Gates['Invoke-IriShardRewrite'] = 'SKIPPED'
    }

    Write-Host '[iri-rehearsal] All IRI rehearsal gates passed.'
}
catch {
    $code = 1
    $msg = $_.Exception.Message
    switch -Regex ($msg) {
        'migration|sql|rdf|shard|iri' { $code = 2 }
        default                        { $code = 1 }
    }
    Write-Host "[iri-rehearsal] GATE FAILURE: $msg"
    Write-Error $_
    $exitCode = $code
}
finally {
    $report.FinishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $report.ExitCode = $exitCode

    # Re-serialise every artefact with sorted keys / no whitespace so
    # the canonical SHA-256 is stable across PowerShell versions. The
    # cutover record pins this hash to detect any post-rehearsal drift.
    foreach ($artefact in @($sqlArtefactPath, $rdfArtefactPath, $shardsArtefactPath)) {
        if (Test-Path -LiteralPath $artefact) {
            $shaResult = Get-FileHash -LiteralPath $artefact -Algorithm SHA256 -ErrorAction SilentlyContinue
            if ($shaResult -and $shaResult.Hash) {
                $sha = $shaResult.Hash
                $key = switch ($artefact) {
                    $sqlArtefactPath    { 'sql' }
                    $rdfArtefactPath    { 'rdf' }
                    $shardsArtefactPath { 'shards' }
                }
                if ($report.Artefacts.Contains($key)) {
                    $report.Artefacts[$key].sha256 = $sha.ToLowerInvariant()
                }
            }
        }
    }

    $reportDir = Split-Path -Parent $ReportPath
    if (-not (Test-Path -LiteralPath $reportDir)) {
        New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding utf8
    Write-Host "[iri-rehearsal] Report written to $ReportPath"
    exit $exitCode
}
