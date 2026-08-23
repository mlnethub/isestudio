# Invoke-ProductionCutover.ps1
#
# Production cutover orchestrator (Stage 6 Task 4).
#
# Runs the hard preflight gate sequence that Task 4 of the data-cutover
# plan requires:
#
#   1. Assert-CutoverRecord           (record completeness)
#   2. Assert-PythonBackendStopped    (no Python writes)
#   3. Assert-DatabaseWriteFreeze     (PostgreSQL write freeze)
#   4. Assert-VerifiedBackup          (backup SHA matches sidecar)
#   5. Invoke-RdfCopyVerification     (RDF copy read)
#   6. Invoke-BlobMigration           (blobs into MinIO)
#   7. Invoke-SqlMigration            (SQL GUID/LegacyId)
#   8. Invoke-IriSqlMigration         (IRI prefix SQL REPLACE)
#   8.5 Invoke-IriSqlSmokeCheck       (proves IRI SQL rewrite actually wrote data)
#   9. Invoke-IriRdfRelocation        (Oxigraph RocksDB IRI rewrite)
#  10. Invoke-IriShardRewrite         (N-Quads shard + manifest IRI rewrite)
#  11. Assert-AllMigrationManifests   (every manifest checksum matches record)
#  12. Start-DotNetBackend            (boot the .NET host)
#  13. Invoke-PostCutoverSmoke        (MCP / RDF / contract smoke)
#
# Any gate that throws a terminating error stops the sequence. No
# auto-proceed to the next data layer. Exit codes follow the global
# cutover convention:
#
#   0 - success; .NET is live; 24h observation starts now.
#   1 - preflight failure (one of gates 1-4 or 8).
#   2 - migration failure (gates 5, 6, 7).
#   3 - manifest validation failure (gate 8).
#   4 - post-cutover smoke failure (gate 10).
#   5 - rollback triggered (currently not used here; rollback is
#       handled by Invoke-ProductionRollback.ps1).
#
# ⚠️ Production steps MUST be triggered by an authorized operator. This
# script is delivered as part of Task 4 but the implementer never runs
# it against real infrastructure; the rehearsal script
# (Invoke-MigrationRehearsal.ps1) is the safe-to-test sibling.
#
# Testability note: the body lives inside `Invoke-ProductionCutover` so
# Pester can dot-source this script and call the function (and Mock the
# gate-level helpers from gates/CutoverGates.ps1) without invoking the
# mandatory `param()` block of the script itself.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Record,

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
    [string]$BlobManifestOut = '.artifacts/blob-manifest.json',

    [string]$MigrationsDir = 'migrations/SqlAlchemyToEfCore',

    [string]$IriFromPrefix = 'http://ontopilot.local/',
    [string]$IriToPrefix = 'http://goodcrew.local/',
    [string]$IriRdfSource,
    [string]$IriRdfTarget,
    [string]$IriReleasesRoot,
    [string]$IriExportsRoot,
    [switch]$IriDryRun,
    [string]$IriSqlVerifyReportOut = '.artifacts/iri-sql-verify-report.json',

    [string]$DotNetProject = 'src/OnToPilot.WebHost/OnToPilot.WebHost.csproj',
    [string]$DotNetBindAddress = 'http://127.0.0.1:5000'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve the gates library next to this script.
$gatesLibrary = Join-Path $PSScriptRoot 'gates/CutoverGates.ps1'
if (-not (Test-Path -LiteralPath $gatesLibrary)) {
    throw "Invoke-ProductionCutover.ps1: gates library '$gatesLibrary' is missing."
}
. $gatesLibrary

function Invoke-ProductionCutover {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record,

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
        [string]$BlobManifestOut = '.artifacts/blob-manifest.json',

        [string]$MigrationsDir = 'migrations/SqlAlchemyToEfCore',

        [string]$IriFromPrefix = 'http://ontopilot.local/',
        [string]$IriToPrefix = 'http://goodcrew.local/',
        [string]$IriRdfSource,
        [string]$IriRdfTarget,
        [string]$IriReleasesRoot,
        [string]$IriExportsRoot,
        [switch]$IriDryRun,
        [string]$IriSqlVerifyReportOut = '.artifacts/iri-sql-verify-report.json',

        [string]$DotNetProject = 'src/OnToPilot.WebHost/OnToPilot.WebHost.csproj',
        [string]$DotNetBindAddress = 'http://127.0.0.1:5000',

        [string]$SqlManifestPath,
        [string]$RdfManifestPath
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    $resolvedRecord = (Resolve-Path -LiteralPath $Record -ErrorAction SilentlyContinue)?.Path
    if (-not $resolvedRecord -or -not (Test-Path -LiteralPath $resolvedRecord)) {
        throw "Invoke-ProductionCutover: -Record '$Record' is missing or unreadable."
    }

    Write-Host "[cutover] Starting production cutover against record '$resolvedRecord'."

    try {
        # Gate 0: record must be complete before we touch infrastructure.
        Assert-CutoverRecord -Record $resolvedRecord

        # Gates 1-3: stop Python, freeze DB, verify backup. ANY failure
        # here aborts before we touch RDF / blobs / SQL.
        Assert-PythonBackendStopped
        Assert-DatabaseWriteFreeze
        Assert-VerifiedBackup -Record $resolvedRecord

        # Gate 4: RDF copy verification. The .NET stack reads the COPY
        # only — the source RocksDB directory remains read-only.
        Invoke-RdfCopyVerification `
            -Source $RdfSource `
            -Copy   $RdfCopy `
            -Work   $RdfWork `
            -Queries $RdfQueries

        # Gate 5: blob migration (dry-run during cutover's first pass;
        # real upload during the cutover's second pass after smoke).
        Invoke-BlobMigration `
            -Source  $BlobSource `
            -Bucket  $BlobBucket `
            -MinioEndpoint $MinioEndpoint `
            -MinioAccessKey $MinioAccessKey `
            -MinioSecretKey $MinioSecretKey `
            -PostgresConnectionString $PostgresConnectionString `
            -ManifestOut $BlobManifestOut

        # Gate 6: SQL GUID/LegacyId migration (idempotent re-runnable).
        Invoke-SqlMigration `
            -ConnectionString $PostgresConnectionString `
            -MigrationsDir $MigrationsDir

        # Gates 8-10: IRI prefix migration across SQL / RDF / shards.
        # The IRI gates are inserted after SQL so the cutover record's
        # expected-* fields can refer to the IRI-derived hashes (the
        # underlying SQL rows are already rewired; the IRI rewrite is
        # a column-level REPLACE on top of that). Each gate is
        # independently Mockable; the rehearsal path forwards
        # -IriDryRun for first-pass rehearsal, then drops it for the
        # apply-mode cutover.
        Invoke-IriSqlMigration `
            -PostgresConnectionString $PostgresConnectionString `
            -FromPrefix $IriFromPrefix `
            -ToPrefix   $IriToPrefix `
            -DryRun:([bool]$IriDryRun)

        # Gate 6.55: smoke-check that Invoke-IriSqlMigration actually
        # rewrote the data. Skipped under -IriDryRun because the
        # migrator did not write, so residual counts would always fail
        # (which would break the first-pass rehearsal). The verifier
        # writes its JSON report to -IriSqlVerifyReportOut so the
        # cutover record can pin the audit trail by SHA-256.
        if (-not $IriDryRun) {
            Invoke-IriSqlSmokeCheck `
                -PostgresConnectionString $PostgresConnectionString `
                -FromPrefix $IriFromPrefix `
                -ToPrefix   $IriToPrefix `
                -ReportOut  $IriSqlVerifyReportOut
        }

        Invoke-IriRdfRelocation `
            -Source $IriRdfSource `
            -Target $IriRdfTarget `
            -FromPrefix $IriFromPrefix `
            -ToPrefix   $IriToPrefix

        Invoke-IriShardRewrite `
            -ReleasesRoot $IriReleasesRoot `
            -ExportsRoot  $IriExportsRoot `
            -FromPrefix   $IriFromPrefix `
            -ToPrefix     $IriToPrefix `
            -DryRun:([bool]$IriDryRun)

        # Gate 7: every manifest must exist and its checksum must match
        # the value recorded in the cutover record. The content-
        # validating gate runs schema + business + MinIO HEAD + SHA
        # checks; it must NEVER silently bypass.
        $manifestGateArgs = @{
            Record           = $resolvedRecord
            BlobManifestPath = $BlobManifestOut
            MinioEndpoint    = $MinioEndpoint
            MinioBucket      = $BlobBucket
        }
        if ($SqlManifestPath) { $manifestGateArgs['SqlManifestPath'] = $SqlManifestPath }
        if ($RdfManifestPath) { $manifestGateArgs['RdfManifestPath'] = $RdfManifestPath }
        Assert-AllMigrationManifests @manifestGateArgs

        # Gate 8: boot the .NET backend.
        Start-DotNetBackend `
            -ProjectPath $DotNetProject `
            -BindAddress $DotNetBindAddress

        # Gate 9: post-cutover smoke. Failure here triggers rollback
        # (handled by the operator following production-rollback.md).
        Invoke-PostCutoverSmoke `
            -BackendUrl $DotNetBindAddress

        Write-Host "[cutover] All gates passed. 24h observation window starts."
        return 0
    }
    catch {
        $code = 1
        $msg = $_.Exception.Message
        switch -Regex ($msg) {
            '^Python backend must be stopped'      { $code = 1 }
            '^PostgreSQL write permissions'        { $code = 1 }
            '^Backup referenced'                   { $code = 1 }
            '^Cutover record'                      { $code = 1 }
            '^One or more migration manifests'     { $code = 3 }
            'rdf|migration|minio|verify.sql|sql|iri' { $code = 2 }
            'smoke|MCP|mcp'                        { $code = 4 }
            default                                { $code = 1 }
        }
        Write-Host "[cutover] GATE FAILURE: $msg"
        Write-Error $_
        return $code
    }
}

# If the script is being run directly (not dot-sourced by Pester),
# forward the script-level parameters to the function. The condition
# uses `$MyInvocation.InvocationName` — when dot-sourced the value is
# '.', and when run as `pwsh Invoke-ProductionCutover.ps1 ...` it is
# the script's own path.
if ($MyInvocation.InvocationName -ne '.') {
    $scriptArgs = @{}
    foreach ($key in @(
        'Record', 'RdfSource', 'RdfCopy', 'RdfWork', 'RdfQueries',
        'BlobSource', 'BlobBucket', 'MinioEndpoint', 'MinioAccessKey',
        'MinioSecretKey', 'PostgresConnectionString', 'BlobManifestOut',
        'MigrationsDir',
        'IriFromPrefix', 'IriToPrefix', 'IriRdfSource', 'IriRdfTarget',
        'IriReleasesRoot', 'IriExportsRoot', 'IriDryRun',
        'IriSqlVerifyReportOut',
        'DotNetProject', 'DotNetBindAddress'
    )) {
        $val = Get-Variable -Name $key -Scope Script -ErrorAction SilentlyContinue
        if ($null -ne $val -and -not [string]::IsNullOrEmpty($val.Value)) {
            $scriptArgs[$key] = $val.Value
        }
    }
    $exit = Invoke-ProductionCutover @scriptArgs
    exit $exit
}