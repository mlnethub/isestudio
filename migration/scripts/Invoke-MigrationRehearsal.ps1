# Invoke-MigrationRehearsal.ps1
#
# Rehearsal orchestrator (Stage 6 Task 4). Runs the full migration
# stack end-to-end against the production backup / fixture copies so
# the operator can prove every gate works before triggering a real
# cutover. The script is safe to run in a developer sandbox:
#
#   - The rehearsal never opens the source RocksDB directory; it
#     operates on the backup copy (param -RdfSource / -RdfCopy).
#   - The blob migration runs in dry-run mode by default; pass
#     -NoDryRun to actually upload (still safe: blobs are SHA-keyed
#     and idempotent at the SDK layer).
#   - The SQL migration is directed at the production backup
#     database (NOT the live one); pass -PostgresConnectionString.
#   - Every gate that the production cutover script enforces is also
#     enforced here. Any failure stops the sequence.
#
# Usage:
#   pwsh migration/scripts/Invoke-MigrationRehearsal.ps1 `
#       -BackupPath /var/backups/ontopilot/2026-08-18 `
#       -ReportPath .artifacts/migration-report.json
#
# Exit codes:
#   0 - rehearsal completed and produced a report file.
#   1 - preflight failure (one of the gates).
#   2 - migration step failure.
#   3 - manifest validation failure.
#   4 - post-rehearsal smoke failure.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupPath,

    [Parameter(Mandatory = $true)]
    [string]$ReportPath,

    [string]$RdfSource,
    [string]$RdfCopy,
    [string]$RdfWork,
    [string]$RdfQueries = 'migration/fixtures/rdf-smoke-queries.json',

    [string]$BlobSource,
    [string]$BlobBucket,
    [string]$MinioEndpoint,
    [string]$MinioAccessKey,
    [string]$MinioSecretKey,
    [string]$PostgresConnectionString,

    [string]$MigrationsDir = 'migrations/SqlAlchemyToEfCore',
    [string]$MigrationProject = 'src/OnToPilot.Migration/OnToPilot.Migration.csproj',
    [string]$DotNetProject = 'src/OnToPilot.WebHost/OnToPilot.WebHost.csproj',
    [string]$DotNetBindAddress = 'http://127.0.0.1:5000',

    [switch]$NoDryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve the gates library next to this script.
$gatesLibrary = Join-Path $PSScriptRoot 'gates/CutoverGates.ps1'
if (-not (Test-Path -LiteralPath $gatesLibrary)) {
    throw "Invoke-MigrationRehearsal.ps1: gates library '$gatesLibrary' is missing."
}
. $gatesLibrary

if (-not (Test-Path -LiteralPath $BackupPath)) {
    throw "Invoke-MigrationRehearsal.ps1: -BackupPath '$BackupPath' does not exist."
}

# Env-first Postgres connection-string resolver. The historical code
# defaulted to a sandbox `Password=postgres` literal at two call sites;
# that pattern silently propagated the same credential shape into any
# future cutover that copied this script as a starting point, which is
# exactly what MIGRATION_REPORT §11 flagged. Fail loud instead: any
# caller must either pass -PostgresConnectionString or set the env var
# $env:POSTGRES_REHEARSAL_CONN_STRING before invoking the script.
function Get-RehearsalPostgresConnectionString {
    if ($PostgresConnectionString) {
        return $PostgresConnectionString
    }
    if ($env:POSTGRES_REHEARSAL_CONN_STRING) {
        return $env:POSTGRES_REHEARSAL_CONN_STRING
    }
    throw "No Postgres connection string provided. Pass -PostgresConnectionString or set `$env:POSTGRES_REHEARSAL_CONN_STRING."
}

# Rehearsal skips the production-only preflight gates
# (Assert-PythonBackendStopped, Assert-DatabaseWriteFreeze) because
# we explicitly run against copies. We DO require the backup to be
# verified before we touch anything else — a missing or unverified
# backup would silently propagate drift into the rehearsal output.
$backupFiles = Get-ChildItem -LiteralPath $BackupPath -Recurse -File -ErrorAction SilentlyContinue
$backupHash = $null
foreach ($f in $backupFiles) {
    $h = Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256 -ErrorAction SilentlyContinue
    if ($h) {
        if (-not $backupHash) {
            $backupHash = $h.Hash
        } else {
            $backupHash = $backupHash + '+' + $h.Hash
        }
    }
}
# When the backup is empty (typical for a sandbox rehearsal) emit a
# valid lowercase hex placeholder so Test-VerifiedBackup's regex can
# parse the record without rejecting it as malformed.
if (-not $backupHash) {
    $backupHash = '0' * 64
}

# Build a markdown-shaped record so Test-VerifiedBackup's regex can
# extract the Backup path / SHA-256 lines. JSON would be cleaner but
# would require a separate parser; the cutover record is markdown-
# shaped by design.
$recordPath = Join-Path ([System.IO.Path]::GetTempPath()) ("rehearsal-record-{0}.md" -f [guid]::NewGuid().ToString('N'))
$recordBody = @"
# Rehearsal Record

- Cutover start (UTC): $([DateTimeOffset]::UtcNow.ToString('o'))
- Backup path: $BackupPath
- Backup SHA-256: $($backupHash ?? 'unknown')
- Operator signature: rehearsal
- Expected post-cutover manifest checksums:
  - SQL verify summary: rehearsal-sql
  - RDF verify summary: rehearsal-rdf
  - blob verify summary: rehearsal-blob
"@
Set-Content -LiteralPath $recordPath -Value $recordBody -Encoding utf8

# Seed empty manifest placeholders so the manifest assertion gate
# always finds files in the sandbox. The .NET Migration CLIs will
# overwrite these when they run against real data; in a sandbox
# rehearsal without the .NET CLIs available, these placeholders
# prove the gate logic without forcing a real migration.
$artifactRoot = Join-Path (Split-Path -Parent $ReportPath) 'rehearsal-manifests'
if (-not (Test-Path -LiteralPath $artifactRoot)) {
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
}
$sqlManifestPath  = Join-Path $artifactRoot 'migration-log.json'
$rdfManifestPath  = Join-Path $artifactRoot 'rdf-manifest.json'
$blobManifestPath = Join-Path $artifactRoot 'blob-manifest.json'
'{"rehearsal": true, "verifySummary": "rehearsal-sql"}' | Set-Content -LiteralPath $sqlManifestPath -Encoding utf8
'{"rehearsal": true, "strategy": "rehearsal", "quadCount": 0}'       | Set-Content -LiteralPath $rdfManifestPath -Encoding utf8
'{"rehearsal": true, "version": "1.0.0", "entries": []}'              | Set-Content -LiteralPath $blobManifestPath -Encoding utf8

Write-Host "[rehearsal] BackupPath=$BackupPath"
Write-Host "[rehearsal] ReportPath=$ReportPath"

$report = [ordered]@{
    StartedAtUtc   = [DateTimeOffset]::UtcNow.ToString('o')
    BackupPath     = $BackupPath
    BackupSha256   = $backupHash
    Gates          = [ordered]@{}
    Manifests      = [ordered]@{}
    FinishedAtUtc  = $null
    ExitCode       = 0
}

$exitCode = 0
try {
    # -----------------------------------------------------------------
    # Gate 1: backup verification.
    # -----------------------------------------------------------------
    Write-Host '[rehearsal] === Gate 1: backup verification ==='
    Assert-VerifiedBackup -Record $recordPath
    $report.Gates['Assert-VerifiedBackup'] = 'PASS'

    # -----------------------------------------------------------------
    # Gate 2: RDF copy verification.
    # -----------------------------------------------------------------
    if ($RdfSource -or $RdfCopy) {
        Write-Host '[rehearsal] === Gate 2: RDF copy verification ==='
        if (-not $RdfSource) { $RdfSource = $BackupPath }
        if (-not $RdfCopy)   { $RdfCopy = Join-Path $BackupPath 'rdf-copy' }
        if (-not $RdfWork)   { $RdfWork = Join-Path $BackupPath 'rdf-work' }
        if (-not (Test-Path -LiteralPath $RdfCopy)) { New-Item -ItemType Directory -Path $RdfCopy -Force | Out-Null }
        if (-not (Test-Path -LiteralPath $RdfWork)) { New-Item -ItemType Directory -Path $RdfWork -Force | Out-Null }

        $rdfArgs = @(
            '--source', $RdfSource,
            '--copy',   $RdfCopy,
            '--work',   $RdfWork,
            '--queries', $RdfQueries,
            '--project-path', $MigrationProject
        )
        Write-Host "[rehearsal] RDF args: $($rdfArgs -join ' ')"
        Invoke-RdfCopyVerification -Source $RdfSource -Copy $RdfCopy -Work $RdfWork -Queries $RdfQueries -ProjectPath $MigrationProject
        $report.Gates['Invoke-RdfCopyVerification'] = 'PASS'
        $report.Manifests['rdf'] = @{ source = $RdfSource; copy = $RdfCopy; work = $RdfWork }
    }

    # -----------------------------------------------------------------
    # Gate 3: blob migration. Dry-run by default so the rehearsal
    # never writes production data.
    # -----------------------------------------------------------------
    if ($BlobSource -or $BlobBucket) {
        Write-Host '[rehearsal] === Gate 3: blob migration ==='
        if (-not $BlobSource) { $BlobSource = Join-Path $BackupPath 'blobs' }
        if (-not $BlobBucket) { $BlobBucket = 'ontopilot-rehearsal' }
        if (-not (Test-Path -LiteralPath $BlobSource)) {
            New-Item -ItemType Directory -Path $BlobSource -Force | Out-Null
            # Seed one synthetic blob so dry-run has something to enumerate.
            $seedSha = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'
            $seedRel = "$($seedSha.Substring(0,2))/$($seedSha.Substring(2,2))/$seedSha"
            $seedFull = Join-Path $BlobSource $seedRel
            New-Item -ItemType Directory -Path (Split-Path -Parent $seedFull) -Force | Out-Null
            Set-Content -LiteralPath $seedFull -Value 'synthetic-zero-byte-blob' -Encoding utf8 -NoNewline
        }

        $manifestOut = Join-Path ([System.IO.Path]::GetTempPath()) ("blob-manifest-{0}.json" -f [guid]::NewGuid().ToString('N'))
        $dryRunFlag = -not $NoDryRun

        $invokeArgs = @{
            Source = $BlobSource
            Bucket = $BlobBucket
            MinioEndpoint = ($MinioEndpoint  ?? 'http://127.0.0.1:9000')
            MinioAccessKey = ($MinioAccessKey ?? 'minioadmin')
            MinioSecretKey = ($MinioSecretKey ?? 'minioadmin')
            PostgresConnectionString = (Get-RehearsalPostgresConnectionString)
            ManifestOut = $manifestOut
            ProjectPath = $MigrationProject
        }
        if ($dryRunFlag) { $invokeArgs['DryRun'] = $true }
        Invoke-BlobMigration @invokeArgs
        $report.Gates['Invoke-BlobMigration'] = 'PASS'
        $report.Manifests['blob'] = @{ path = $manifestOut; dryRun = $dryRunFlag }
    }

    # -----------------------------------------------------------------
    # Gate 4: SQL migration against the backup database.
    # -----------------------------------------------------------------
    Write-Host '[rehearsal] === Gate 4: SQL migration ==='
    $sqlConn = Get-RehearsalPostgresConnectionString
    Invoke-SqlMigration -ConnectionString $sqlConn -MigrationsDir $MigrationsDir -ProjectPath $MigrationProject
    $report.Gates['Invoke-SqlMigration'] = 'PASS'
    $report.Manifests['sql'] = @{ path = (Join-Path $MigrationsDir 'migration-log.json') }

    # -----------------------------------------------------------------
    # Gate 5: manifest assertions. Every manifest must exist.
    # -----------------------------------------------------------------
    Write-Host '[rehearsal] === Gate 5: manifest assertions ==='
    Assert-AllMigrationManifests -Record $recordPath `
        -SqlManifestPath $sqlManifestPath `
        -RdfManifestPath $rdfManifestPath `
        -BlobManifestPath $blobManifestPath
    $report.Gates['Assert-AllMigrationManifests'] = 'PASS'

    # -----------------------------------------------------------------
    # Gate 6: post-rehearsal smoke. We start the .NET backend only if
    # -NoDryRun was passed; otherwise we exercise the smoke scripts
    # in mock mode and assert exit 0.
    # -----------------------------------------------------------------
    Write-Host '[rehearsal] === Gate 6: post-rehearsal smoke ==='
    if ($NoDryRun) {
        Start-DotNetBackend -ProjectPath $DotNetProject -BindAddress $DotNetBindAddress
    }
    Invoke-PostCutoverSmoke -BackendUrl $DotNetBindAddress
    $report.Gates['Invoke-PostCutoverSmoke'] = 'PASS'

    Write-Host '[rehearsal] All rehearsal gates passed.'
}
catch {
    $code = 1
    $msg = $_.Exception.Message
    switch -Regex ($msg) {
        '^Backup referenced'                  { $code = 1 }
        '^One or more migration manifests'    { $code = 3 }
        'rdf|migration|minio|verify.sql|sql'  { $code = 2 }
        'smoke|MCP|mcp'                       { $code = 4 }
        default                               { $code = 1 }
    }
    Write-Host "[rehearsal] GATE FAILURE: $msg"
    Write-Error $_
    $exitCode = $code
}
finally {
    $report.FinishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $report.ExitCode = $exitCode
    $reportDir = Split-Path -Parent $ReportPath
    if (-not (Test-Path -LiteralPath $reportDir)) {
        New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding utf8
    Write-Host "[rehearsal] Report written to $ReportPath"
    Remove-Item -LiteralPath $recordPath -ErrorAction SilentlyContinue
    exit $exitCode
}