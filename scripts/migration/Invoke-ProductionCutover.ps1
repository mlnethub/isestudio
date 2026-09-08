[CmdletBinding()]
param(
    [switch]$ConfirmStopWrites,
    [Parameter(Mandatory = $true)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$VerifiedBackupManifest,
    [Parameter(Mandatory = $true)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$RehearsalManifest,
    [Parameter(Mandatory = $true)][uri]$SmokeBaseUrl,
    [string]$StopRustWritesCommand = $env:UTOPIA_STOP_WRITES_COMMAND,
    [string]$VerifyDbWriteFreezeCommand = $env:UTOPIA_VERIFY_DB_FREEZE_COMMAND,
    [string]$VerifyBackupCommand = $env:UTOPIA_VERIFY_BACKUP_COMMAND,
    [string]$RdfCheckCommand = $env:UTOPIA_RDF_CHECK_COMMAND,
    [string]$BlobCheckCommand = $env:UTOPIA_BLOB_CHECK_COMMAND,
    [string]$SqlCheckCommand = $env:UTOPIA_SQL_CHECK_COMMAND,
    [string]$StartISEStudioCommand = $env:ISESTUDIO_START_COMMAND,
    [string]$SmokeCommand = $env:ISESTUDIO_SMOKE_COMMAND,
    [string]$RecordPath,
    [string]$StatePath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RecordPath)) { $RecordPath = Join-Path $PSScriptRoot '..\..\docs\migration\production-cutover-record.json' }
if ([string]::IsNullOrWhiteSpace($StatePath)) { $StatePath = Join-Path $PSScriptRoot '..\..\.artifacts\production-cutover-state.json' }
$steps = [System.Collections.Generic.List[object]]::new()
function Redact([string]$Value) { if ($null -eq $Value) { return '' }; $Value -replace '(?i)(password|passwd|secret|token|api[-_]?key|connection(string)?|access[-_]?key)\s*[=:]\s*([^\s;]+)', '$1=<redacted>' -replace '(?i)Bearer\s+[A-Za-z0-9._~+/=-]+', 'Bearer <redacted>' -replace '(?i)(postgres|mysql|sqlserver)://[^\s]+', '<redacted-connection-string>' }
function Write-State([string]$Status, [string]$Failure = $null) { $parent = Split-Path -Parent $StatePath; if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }; [pscustomobject]@{ operation = 'cutover'; status = $Status; updated_at = [DateTimeOffset]::UtcNow; steps = @($steps); failure = (Redact $Failure) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $StatePath -Encoding UTF8 }
function Invoke-Gate([string]$Name, [string]$Command) { if ([string]::IsNullOrWhiteSpace($Command)) { throw "Required production command is not configured for '$Name'." }; Write-Output "[cutover] $Name"; Invoke-Expression ([Environment]::ExpandEnvironmentVariables($Command)) 2>&1 | ForEach-Object { Write-Output (Redact ([string]$_)) }; if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Gate '$Name' failed with exit code $LASTEXITCODE." }; $steps.Add([pscustomobject]@{ name = $Name; status = 'passed'; completed_at = [DateTimeOffset]::UtcNow }); Write-State 'running' }

try {
    if (-not $ConfirmStopWrites) { throw 'Cutover requires -ConfirmStopWrites.' }
    if (-not (Test-Path -LiteralPath $VerifiedBackupManifest -PathType Leaf)) { throw 'Verified backup manifest not found.' }
    if (-not (Test-Path -LiteralPath $RehearsalManifest -PathType Leaf)) { throw 'Rehearsal manifest not found.' }
    Write-State 'started'
    Invoke-Gate 'stop-rust-writes' $StopRustWritesCommand
    Invoke-Gate 'verify-db-write-freeze' $VerifyDbWriteFreezeCommand
    Invoke-Gate 'verify-backup' $VerifyBackupCommand
    & (Join-Path $PSScriptRoot 'Test-MigrationGate.ps1') -Manifest $RehearsalManifest
    if ($LASTEXITCODE -ne 0) { throw 'Rehearsal manifest gate failed.' }
    $steps.Add([pscustomobject]@{ name = 'rehearsal-gate'; status = 'passed'; completed_at = [DateTimeOffset]::UtcNow })
    Write-State 'running'
    Invoke-Gate 'rdf-check' $RdfCheckCommand
    Invoke-Gate 'blob-check' $BlobCheckCommand
    Invoke-Gate 'sql-check' $SqlCheckCommand
    Invoke-Gate 'start-isestudio' $StartISEStudioCommand
    $env:ISESTUDIO_SMOKE_BASE_URL = $SmokeBaseUrl.AbsoluteUri
    Invoke-Gate 'smoke' $SmokeCommand
    $record = [pscustomobject]@{ operation = 'production-cutover'; completed_at = [DateTimeOffset]::UtcNow; smoke_base_url = $SmokeBaseUrl.AbsoluteUri; verified_backup_manifest = (Resolve-Path $VerifiedBackupManifest).Path; rehearsal_manifest = (Resolve-Path $RehearsalManifest).Path; steps = @($steps); status = 'passed' }
    $recordParent = Split-Path -Parent $RecordPath; if ($recordParent) { New-Item -ItemType Directory -Force -Path $recordParent | Out-Null }; $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $RecordPath -Encoding UTF8
    Write-State 'passed'
    Write-Output "Production cutover passed. Record: $(Redact $RecordPath)"
    exit 0
}
catch {
    Write-State 'failed' $_.Exception.Message
    [Console]::Error.WriteLine("Production cutover failed: $(Redact $_.Exception.Message)")
    exit 1
}