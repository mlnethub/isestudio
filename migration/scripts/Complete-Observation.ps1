# Complete-Observation.ps1
#
# Closes the 24-hour observation window that the production cutover
# opens (Stage 6 Task 4).
#
# Successful path:
#   1. Assert-CutoverRecord (record must exist)
#   2. Verify post-cutover smoke (last known-good run)
#   3. Mark-BackupRetention (keep the cutover's backup for 30 days)
#
# Rollback path (operator invokes Invoke-ProductionRollback.ps1
# separately; this script just records the outcome).
#
# Exit codes:
#   0 - observation closed successfully; .NET is the permanent service.
#   1 - preflight failure.
#   5 - observation closed with a triggered rollback (manual).
#
# ⚠️ Production steps MUST be triggered by an authorized operator.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Record,

    [string]$DotNetBindAddress = 'http://127.0.0.1:5000',
    [int]$BackupRetentionDays = 30,

    [switch]$RollbackTriggered
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$gatesLibrary = Join-Path $PSScriptRoot 'gates/CutoverGates.ps1'
if (-not (Test-Path -LiteralPath $gatesLibrary)) {
    throw "Complete-Observation.ps1: gates library '$gatesLibrary' is missing."
}
. $gatesLibrary

$Record = (Resolve-Path -LiteralPath $Record -ErrorAction SilentlyContinue)?.Path
if (-not $Record -or -not (Test-Path -LiteralPath $Record)) {
    Write-Error "Complete-Observation.ps1: -Record '$Record' is missing or unreadable."
    exit 1
}

Write-Host "[observation] Closing 24h observation window for '$Record'."

try {
    Assert-CutoverRecord -Record $Record

    if ($RollbackTriggered) {
        Write-Host "[observation] Rollback flag set; the cutover did NOT become permanent."
        Write-Host "[observation] Operator must run Invoke-ProductionRollback.ps1 against '$Record'."
        exit 5
    }

    # Final smoke against the .NET backend. The contract: the smoke
    # suite (Test-McpEndpoint.ps1 + Test-RdfParity.ps1) must pass
    # before we declare the cutover permanent.
    Invoke-PostCutoverSmoke -BackendUrl $DotNetBindAddress

    # Resolve backup path from the record so we can apply retention.
    $lines = Get-Content -LiteralPath $Record -ErrorAction SilentlyContinue
    $backupPath = ($lines | Where-Object { $_ -match '^- Backup path:\s*(.+)$' } |
                   Select-Object -First 1) -replace '^- Backup path:\s*', ''
    if (-not [string]::IsNullOrWhiteSpace($backupPath)) {
        Mark-BackupRetention -BackupPath $backupPath -RetentionDays $BackupRetentionDays
    }

    Write-Host "[observation] 24h observation complete; .NET is the permanent service."
    exit 0
}
catch {
    Write-Host "[observation] FAILED: $($_.Exception.Message)"
    Write-Error $_
    exit 1
}