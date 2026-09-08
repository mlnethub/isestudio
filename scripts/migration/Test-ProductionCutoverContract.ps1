[CmdletBinding()]
param([string]$Root)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Join-Path $PSScriptRoot '..\..' }
$cutover = Get-Content (Join-Path $Root 'scripts\migration\Invoke-ProductionCutover.ps1') -Raw
$rollback = Get-Content (Join-Path $Root 'scripts\migration\Invoke-ProductionRollback.ps1') -Raw
foreach ($required in 'ConfirmStopWrites', 'VerifiedBackupManifest', 'RehearsalManifest', 'SmokeBaseUrl') { if ($cutover -notmatch [regex]::Escape($required)) { throw "cutover missing $required" } }
foreach ($required in 'VerifiedBackupManifest', 'PythonServiceName') { if ($rollback -notmatch [regex]::Escape($required)) { throw "rollback missing $required" } }
$cutoverOrder = 'stop-rust-writes','verify-db-write-freeze','verify-backup','rehearsal-gate','rdf-check','blob-check','sql-check','start-isestudio','smoke'
$rollbackOrder = 'stop-isestudio','restore-db-permission-and-backup','unlock-rust','start-rust'
for ($i = 0; $i -lt $cutoverOrder.Count - 1; $i++) { if ($cutover.IndexOf("'$($cutoverOrder[$i])'") -gt $cutover.IndexOf("'$($cutoverOrder[$i + 1])'")) { throw 'cutover order changed' } }
for ($i = 0; $i -lt $rollbackOrder.Count - 1; $i++) { if ($rollback.IndexOf("'$($rollbackOrder[$i])'") -gt $rollback.IndexOf("'$($rollbackOrder[$i + 1])'")) { throw 'rollback order changed' } }
if ($rollback -notmatch 'backup_retained = \$true') { throw 'rollback must retain backup' }
if ($cutover -notmatch 'Redact' -or $rollback -notmatch 'Redact') { throw 'redaction is required' }
Write-Output 'Production cutover/rollback contract passed.'