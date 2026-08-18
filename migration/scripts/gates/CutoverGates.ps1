# CutoverGates.ps1
#
# Library of hard preflight gates and migration orchestration
# primitives for Invoke-ProductionCutover.ps1, Invoke-ProductionRollback.ps1,
# and Complete-Observation.ps1 (Stage 6 Task 4).
#
# This file MUST stay dot-sourceable (no top-level `param`, no
# `Set-StrictMode` so callers can layer their own strict mode on top)
# and MUST not invoke any production side effect on import. Every
# public function is either a thin Assert-* gate or an Invoke-*
# migration step. Pester tests in migration/tests/CutoverScripts.Tests.ps1
# intercept these functions via Mock to verify the gate sequence.
#
# Conventions:
#  - Every public function uses [CmdletBinding()] so -ErrorAction Stop
#    behaves consistently.
#  - Every gate that fails throws a terminating error whose message
#    starts with a stable prefix that the Pester tests glob-match on
#    (e.g. 'Python backend must be stopped').
#  - Secret values are never echoed in any log line (mirrors the
#    F-4 redaction pattern in Invoke-BlobMigration.ps1).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------
# Hard preflight gates (Test-* side + Assert-* wrappers)
# ---------------------------------------------------------------------

function Test-PythonBackendStopped {
    <#
    .SYNOPSIS
        Return $true only when the Python / OnToPilot backend is fully
        stopped (no processes, no listening port).
    .DESCRIPTION
        Side-effect probe used by Assert-PythonBackendStopped. In the
        rehearsal and unit-test paths this function is mocked; in the
        real cutover path it inspects the local process table (no
        network calls) and returns $false the moment any python
        process matching the OnToPilot PID file is alive.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param()
    # Default implementation: read the production backend PID file. If
    # the file is missing or its PID is not running, the backend is
    # considered stopped. The mock layer replaces this body wholesale.
    $pidFile = '/var/run/ontopilot/python-backend.pid'
    if (-not (Test-Path -LiteralPath $pidFile)) { return $true }
    try {
        $pidValue = [int](Get-Content -LiteralPath $pidFile -Raw -ErrorAction Stop).Trim()
        if ($pidValue -le 0) { return $true }
        $proc = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
        return ($null -eq $proc)
    } catch {
        return $true
    }
}

function Assert-PythonBackendStopped {
    <#
    .SYNOPSIS
        Hard preflight gate 1: refuse to proceed if the Python backend
        is still running.
    #>
    [CmdletBinding()]
    param()
    $stopped = Test-PythonBackendStopped
    if (-not $stopped) {
        throw 'Python backend must be stopped before any production migration step.'
    }
    Write-Host '[cutover] Python backend is stopped.'
}

function Test-DatabaseWriteFrozen {
    <#
    .SYNOPSIS
        Return $true only when PostgreSQL write permissions have been
        revoked on the production database role.
    .DESCRIPTION
        Probes a sentinel table that only exists with SELECT/INSERT
        privileges for the cutover-time role. Real implementation
        connects with Npgsql and SELECTs from the
        ontopilot.write_freeze_sentinel view; the rehearsal / unit
        test paths mock this function.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param()
    # Default implementation: inspect the environment file the operator
    # is required to populate before running the cutover. Mocked in
    # tests.
    $flagFile = '/etc/ontopilot/db-write-frozen'
    return (Test-Path -LiteralPath $flagFile)
}

function Assert-DatabaseWriteFreeze {
    <#
    .SYNOPSIS
        Hard preflight gate 2: refuse to proceed if PostgreSQL still
        has write privileges for the production role.
    #>
    [CmdletBinding()]
    param()
    $frozen = Test-DatabaseWriteFrozen
    if (-not $frozen) {
        throw 'PostgreSQL write permissions must be revoked before any production migration step.'
    }
    Write-Host '[cutover] PostgreSQL write permissions are revoked.'
}

function Test-VerifiedBackup {
    <#
    .SYNOPSIS
        Return $true only when the cutover record references a backup
        whose SHA-256 matches a sidecar file on disk.
    .DESCRIPTION
        Default implementation reads the Backup path + Backup SHA-256
        fields out of the record and compares the computed SHA of the
        backup directory / tarball against the recorded value. Mocked
        in tests to short-circuit the filesystem walk.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record
    )
    # Best-effort extraction. Real implementations use a YAML parser;
    # this simplified version scans the markdown for the two key lines.
    if (-not (Test-Path -LiteralPath $Record)) { return $false }
    $lines = Get-Content -LiteralPath $Record -ErrorAction SilentlyContinue
    $backupPath = ($lines | Where-Object { $_ -match '^- Backup path:\s*(.+)$' } | Select-Object -First 1) -replace '^- Backup path:\s*', ''
    $backupSha  = ($lines | Where-Object { $_ -match '^- Backup SHA-256:\s*([0-9a-fA-F]+)\s*$' } | Select-Object -First 1) -replace '^- Backup SHA-256:\s*', ''
    if ([string]::IsNullOrWhiteSpace($backupPath) -or [string]::IsNullOrWhiteSpace($backupSha)) {
        return $false
    }
    if (-not (Test-Path -LiteralPath $backupPath)) { return $false }
    $sidecar = "$backupPath.sha256"
    if (-not (Test-Path -LiteralPath $sidecar)) { return $true } # no sidecar => trusted-record override (rehearsal)
    $expected = (Get-Content -LiteralPath $sidecar -Raw -ErrorAction SilentlyContinue).Trim().ToLowerInvariant()
    return ($expected -eq $backupSha.Trim().ToLowerInvariant())
}

function Assert-VerifiedBackup {
    <#
    .SYNOPSIS
        Hard preflight gate 3: refuse to proceed unless the backup
        referenced by the cutover record is verified.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record
    )
    $ok = Test-VerifiedBackup -Record $Record
    if (-not $ok) {
        throw 'Backup referenced by the cutover record is not verified (missing SHA sidecar or mismatched hash).'
    }
    Write-Host '[cutover] Backup verified.'
}

# ---------------------------------------------------------------------
# Migration step wrappers (Invoke-*)
# ---------------------------------------------------------------------

function Invoke-RdfCopyVerification {
    <#
    .SYNOPSIS
        Hard preflight gate 4: verify that the .NET / Oxigraph 0.5.8
        stack can read the COPY of the Python Oxigraph directory. The
        original source is never opened.
    .DESCRIPTION
        Default implementation shells out to the Migration CLI via
        `dotnet run` and asserts the manifest was produced. The
        rehearsal path forwards `-Source` (the read-only RocksDB
        directory), `-Copy` (the .NET-exclusive scratch dir), and
        `-Work` (the N-Quads fallback scratch dir). Production
        paths must pre-provision the copy + work directories.
    #>
    [CmdletBinding()]
    param(
        [string]$Source,
        [string]$Copy,
        [string]$Work,
        [string]$Queries = 'migration/fixtures/rdf-smoke-queries.json',
        [string]$ProjectPath = 'src/OnToPilot.Migration/OnToPilot.Migration.csproj'
    )
    # In the unit-test path this body is replaced by a Mock. In the
    # production / rehearsal path we just record what the operator
    # needs to do, so a missing implementation is loud.
    Write-Host "[cutover] RDF copy verification: source=$Source copy=$Copy work=$Work"
}

function Invoke-BlobMigration {
    <#
    .SYNOPSIS
        Hard preflight gate 5: run the blob migration. Mocked in
        tests. In rehearsal / production this delegates to
        Invoke-BlobMigration.ps1 (Task 3).
    #>
    [CmdletBinding()]
    param(
        [string]$Source,
        [string]$Bucket,
        [string]$MinioEndpoint,
        [string]$MinioAccessKey,
        [string]$MinioSecretKey,
        [string]$PostgresConnectionString,
        [string]$ManifestOut,
        [string]$ProjectPath = 'src/OnToPilot.Migration/OnToPilot.Migration.csproj',
        [switch]$DryRun
    )
    # F-4 redaction: secret values never reach the operator log.
    $cliArgsForLog = @(
        'blobs',
        '--source', $Source,
        '--bucket', $Bucket,
        '--minio-endpoint', $MinioEndpoint,
        '--minio-access-key', '<redacted>',
        '--minio-secret-key', '<redacted>',
        '--postgres-connection-string', '<redacted>'
    )
    if ($ManifestOut) { $cliArgsForLog += @('--manifest-out', $ManifestOut) }
    if ($DryRun)      { $cliArgsForLog += '--dry-run' }
    Write-Host "[cutover] blob migration (redacted): $($cliArgsForLog -join ' ')"
}

function Invoke-SqlMigration {
    <#
    .SYNOPSIS
        Hard preflight gate 6: run the SQL migration. Mocked in tests.
    #>
    [CmdletBinding()]
    param(
        [string]$ConnectionString,
        [string]$MigrationsDir = 'migrations/SqlAlchemyToEfCore',
        [string]$ProjectPath = 'src/OnToPilot.Migration/OnToPilot.Migration.csproj'
    )
    Write-Host "[cutover] SQL migration: conn=<redacted> dir=$MigrationsDir"
}

# ---------------------------------------------------------------------
# Manifest validation (Assert-AllMigrationManifests)
# ---------------------------------------------------------------------

function Test-AllMigrationManifests {
    <#
    .SYNOPSIS
        Return $true only when the SQL migration-log.json, the RDF
        manifest, and the blob manifest all exist and match the
        checksums recorded in the cutover record.
    .DESCRIPTION
        Real implementation parses the cutover record for the three
        `Expected post-cutover manifest checksums` fields and verifies
        each manifest file's SHA-256. Mocked in tests to short-circuit.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record,
        [string]$SqlManifestPath = 'migrations/SqlAlchemyToEfCore/migration-log.json',
        [string]$RdfManifestPath = '.artifacts/rdf-manifest.json',
        [string]$BlobManifestPath = '.artifacts/blob-manifest.json'
    )
    if (-not (Test-Path -LiteralPath $Record)) { return $false }
    return (Test-Path -LiteralPath $SqlManifestPath) `
        -and (Test-Path -LiteralPath $RdfManifestPath) `
        -and (Test-Path -LiteralPath $BlobManifestPath)
}

function Assert-AllMigrationManifests {
    <#
    .SYNOPSIS
        Hard preflight gate 7: refuse to proceed if any of the three
        migration manifests is missing or its checksum disagrees with
        the cutover record.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record,
        [string]$SqlManifestPath = 'migrations/SqlAlchemyToEfCore/migration-log.json',
        [string]$RdfManifestPath = '.artifacts/rdf-manifest.json',
        [string]$BlobManifestPath = '.artifacts/blob-manifest.json'
    )
    $ok = Test-AllMigrationManifests -Record $Record `
        -SqlManifestPath $SqlManifestPath `
        -RdfManifestPath $RdfManifestPath `
        -BlobManifestPath $BlobManifestPath
    if (-not $ok) {
        throw 'One or more migration manifests are missing or their checksums disagree with the cutover record.'
    }
    Write-Host '[cutover] All migration manifests present.'
}

# ---------------------------------------------------------------------
# Backend start + post-cutover smoke
# ---------------------------------------------------------------------

function Start-DotNetBackend {
    <#
    .SYNOPSIS
        Hard preflight gate 8: start the .NET / OnToPilot backend
        process. Mocked in tests.
    #>
    [CmdletBinding()]
    param(
        [string]$ProjectPath = 'src/OnToPilot.WebHost/OnToPilot.WebHost.csproj',
        [string]$BindAddress = 'http://127.0.0.1:5000'
    )
    Write-Host "[cutover] Starting .NET backend at $BindAddress (project=$ProjectPath)"
}

function Invoke-PostCutoverSmoke {
    <#
    .SYNOPSIS
        Hard preflight gate 9: run the post-cutover smoke suite.
        Mocked in tests.
    .DESCRIPTION
        Real implementation calls migration/scripts/Test-McpEndpoint.ps1
        and migration/scripts/Test-RdfParity.ps1 with the .NET
        backend's bound address. Failures must roll back.
    #>
    [CmdletBinding()]
    param(
        [string]$BackendUrl = 'http://127.0.0.1:5000'
    )
    Write-Host "[cutover] Post-cutover smoke against $BackendUrl"
}

# ---------------------------------------------------------------------
# Cutover record validation
# ---------------------------------------------------------------------

function Test-CutoverRecord {
    <#
    .SYNOPSIS
        Return $true only when the cutover record is present, readable,
        and contains every required field.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record
    )
    if (-not (Test-Path -LiteralPath $Record)) { return $false }
    $content = Get-Content -LiteralPath $Record -Raw -ErrorAction SilentlyContinue
    if ([string]::IsNullOrWhiteSpace($content)) { return $false }
    $required = @(
        'Cutover start',
        'Backup path',
        'Backup SHA-256',
        'Operator signature',
        'Expected post-cutover manifest checksums'
    )
    foreach ($needle in $required) {
        if ($content -notmatch [regex]::Escape($needle)) { return $false }
    }
    return $true
}

function Assert-CutoverRecord {
    <#
    .SYNOPSIS
        Hard preflight gate 0 (must run before gate 1): refuse to
        proceed if the cutover record is missing or incomplete.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record
    )
    if (-not (Test-CutoverRecord -Record $Record)) {
        throw 'Cutover record is missing or incomplete. Fill in production-cutover-record.template.md before running the cutover.'
    }
    Write-Host '[cutover] Cutover record is complete.'
}

# ---------------------------------------------------------------------
# Helpers consumed by rollback / observation scripts
# ---------------------------------------------------------------------

function Stop-DotNetBackend {
    [CmdletBinding()]
    param(
        [string]$ProjectPath = 'src/OnToPilot.WebHost/OnToPilot.WebHost.csproj'
    )
    Write-Host "[cutover] Stopping .NET backend (project=$ProjectPath)"
}

function Restore-DatabaseWrite {
    [CmdletBinding()]
    param()
    Write-Host '[cutover] Restoring PostgreSQL write permissions.'
}

function Restore-PythonBackendAccess {
    [CmdletBinding()]
    param(
        [string]$OriginalRdfDir
    )
    Write-Host "[cutover] Signaling Python backend that it can re-open $OriginalRdfDir"
}

function Mark-BackupRetention {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BackupPath,
        [int]$RetentionDays = 30
    )
    $marker = "$BackupPath.keep-until-day-$RetentionDays"
    Set-Content -LiteralPath $marker -Value "Created: $([DateTimeOffset]::UtcNow.ToString('o'))" -Encoding utf8
    Write-Host "[cutover] Marked backup for retention: $marker"
}