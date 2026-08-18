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
#   8. Assert-AllMigrationManifests   (every manifest checksum matches record)
#   9. Start-DotNetBackend            (boot the .NET host)
#  10. Invoke-PostCutoverSmoke        (MCP / RDF / contract smoke)
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

        [string]$DotNetProject = 'src/OnToPilot.WebHost/OnToPilot.WebHost.csproj',
        [string]$DotNetBindAddress = 'http://127.0.0.1:5000'
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

        # Gate 7: every manifest must exist and its checksum must match
        # the value recorded in the cutover record.
        Assert-AllMigrationManifests -Record $resolvedRecord

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
            'rdf|migration|minio|verify.sql|sql'   { $code = 2 }
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
        'MigrationsDir', 'DotNetProject', 'DotNetBindAddress'
    )) {
        $val = Get-Variable -Name $key -Scope Script -ErrorAction SilentlyContinue
        if ($null -ne $val -and -not [string]::IsNullOrEmpty($val.Value)) {
            $scriptArgs[$key] = $val.Value
        }
    }
    $exit = Invoke-ProductionCutover @scriptArgs
    exit $exit
}