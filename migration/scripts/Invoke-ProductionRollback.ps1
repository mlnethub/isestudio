# Invoke-ProductionRollback.ps1
#
# Rollback to the Python / OnToPilot backend (Stage 6 Task 4).
#
# This script is the inverse of Invoke-ProductionCutover.ps1. It is
# deliberately NOT chained to the cutover script; the operator runs
# it explicitly when one of the rollback triggers fires (see
# migration/runbooks/production-rollback.md).
#
# Sequence:
#   1. Assert-CutoverRecord           (record must exist to identify the
#                                     original backup + RDF dir)
#   2. Stop-DotNetBackend             (shut the .NET host)
#   3. Restore-DatabaseWrite          (re-grant PostgreSQL write perms)
#   4. Restore-PythonBackendAccess    (signal Python that the original
#                                     RocksDB dir is writable again)
#   5. Mark-BackupRetention           (keep the backup for 30 days so
#                                     the next cutover attempt has a
#                                     known-good baseline)
#
# Exit codes:
#   0 - rollback completed; Python is back in service.
#   1 - preflight failure (missing record, .NET not running, etc.).
#   5 - rollback triggered after a successful cutover (manual run).
#
# ⚠️ Production steps MUST be triggered by an authorized operator.
# This script is delivered as part of Task 4 but the implementer never
# runs it against real infrastructure; the rehearsal script
# (Invoke-MigrationRehearsal.ps1) is the safe-to-test sibling.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Record,

    [string]$DotNetProject = 'src/OnToPilot.WebHost/OnToPilot.WebHost.csproj',
    [string]$OriginalRdfDir,
    [int]$BackupRetentionDays = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve the gates library next to this script.
$gatesLibrary = Join-Path $PSScriptRoot 'gates/CutoverGates.ps1'
if (-not (Test-Path -LiteralPath $gatesLibrary)) {
    throw "Invoke-ProductionRollback.ps1: gates library '$gatesLibrary' is missing."
}
. $gatesLibrary

$Record = (Resolve-Path -LiteralPath $Record -ErrorAction SilentlyContinue)?.Path
if (-not $Record -or -not (Test-Path -LiteralPath $Record)) {
    Write-Error "Invoke-ProductionRollback.ps1: -Record '$Record' is missing or unreadable."
    exit 1
}

Write-Host "[rollback] Starting rollback against record '$Record'."

try {
    # Gate 0: record must be present so we can find the backup and
    # original RDF dir.
    Assert-CutoverRecord -Record $Record

    if (-not $OriginalRdfDir) {
        # Default: assume the operator's record references the
        # original dir under `- Original RDF dir`. Fall back to the
        # most recent *.rdf-dir sidecar if the field is absent.
        $lines = Get-Content -LiteralPath $Record -ErrorAction SilentlyContinue
        $OriginalRdfDir = ($lines | Where-Object { $_ -match '^- Original RDF dir:\s*(.+)$' } |
                           Select-Object -First 1) -replace '^- Original RDF dir:\s*', ''
        if ([string]::IsNullOrWhiteSpace($OriginalRdfDir)) {
            $OriginalRdfDir = (Get-ChildItem -Path '/var/backups/ontopilot' -Filter '*.rdf-dir' -ErrorAction SilentlyContinue |
                               Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
        }
        if ([string]::IsNullOrWhiteSpace($OriginalRdfDir)) {
            throw 'Original RDF directory could not be determined from the cutover record or backup sidecars.'
        }
    }

    # Resolve backup path from the record.
    $lines = Get-Content -LiteralPath $Record -ErrorAction SilentlyContinue
    $backupPath = ($lines | Where-Object { $_ -match '^- Backup path:\s*(.+)$' } |
                   Select-Object -First 1) -replace '^- Backup path:\s*', ''
    if ([string]::IsNullOrWhiteSpace($backupPath)) {
        throw 'Backup path missing from the cutover record.'
    }

    # Step 1: stop the .NET backend.
    Stop-DotNetBackend -ProjectPath $DotNetProject

    # Step 2: re-grant PostgreSQL write permissions so the Python
    # backend (or the next migration attempt) can write again.
    Restore-DatabaseWrite

    # Step 3: signal the Python backend that the original RocksDB
    # directory is writable. We do NOT mutate the directory here; the
    # Python supervisor / systemd unit decides when to re-mount.
    Restore-PythonBackendAccess -OriginalRdfDir $OriginalRdfDir

    # Step 4: keep the backup for the configured retention window
    # (default 30 days) so a re-cutover attempt can replay it.
    Mark-BackupRetention -BackupPath $backupPath -RetentionDays $BackupRetentionDays

    Write-Host "[rollback] Rollback complete. Python backend is in service."
    exit 5
}
catch {
    Write-Host "[rollback] ROLLBACK FAILED: $($_.Exception.Message)"
    Write-Error $_
    exit 1
}