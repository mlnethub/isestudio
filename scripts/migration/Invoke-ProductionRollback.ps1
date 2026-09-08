[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$VerifiedBackupManifest,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$PythonServiceName,
    [string]$StopISEStudioCommand = $env:ISESTUDIO_STOP_COMMAND,
    [string]$RestoreDbCommand = $env:ISESTUDIO_RESTORE_DB_COMMAND,
    [string]$UnlockRustCommand = $env:UTOPIA_UNLOCK_COMMAND,
    [string]$StartRustCommand = $env:UTOPIA_START_COMMAND,
    [string]$RecordPath,
    [string]$StatePath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RecordPath)) { $RecordPath = Join-Path $PSScriptRoot '..\..\docs\migration\production-rollback-record.json' }
if ([string]::IsNullOrWhiteSpace($StatePath)) { $StatePath = Join-Path $PSScriptRoot '..\..\.artifacts\production-rollback-state.json' }
$steps = [System.Collections.Generic.List[object]]::new()
if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
    try {
        $previous = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        foreach ($step in @($previous.steps)) { $steps.Add($step) }
    }
    catch { throw 'Existing rollback state is not valid JSON.' }
}
function Redact([string]$Value) { if ($null -eq $Value) { return '' }; $Value -replace '(?i)(password|passwd|secret|token|api[-_]?key|connection(string)?|access[-_]?key)\s*[=:]\s*([^\s;]+)', '$1=<redacted>' -replace '(?i)Bearer\s+[A-Za-z0-9._~+/=-]+', 'Bearer <redacted>' -replace '(?i)(postgres|mysql|sqlserver)://[^\s]+', '<redacted-connection-string>' }
function Write-State([string]$Status, [string]$Failure = $null) { $parent = Split-Path -Parent $StatePath; if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }; [pscustomobject]@{ operation = 'rollback'; status = $Status; python_service = $PythonServiceName; updated_at = [DateTimeOffset]::UtcNow; steps = @($steps); failure = (Redact $Failure); backup_retained = $true } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $StatePath -Encoding UTF8 }
function Invoke-Gate([string]$Name, [string]$Command) { if (@($steps | Where-Object name -eq $Name | Where-Object status -eq 'passed').Count -gt 0) { Write-Output "[rollback] $Name already passed; skipping."; return }; if ([string]::IsNullOrWhiteSpace($Command)) { throw "Required production command is not configured for '$Name'." }; Write-Output "[rollback] $Name"; Invoke-Expression ([Environment]::ExpandEnvironmentVariables($Command)) 2>&1 | ForEach-Object { Write-Output (Redact ([string]$_)) }; if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Gate '$Name' failed with exit code $LASTEXITCODE." }; $steps.Add([pscustomobject]@{ name = $Name; status = 'passed'; completed_at = [DateTimeOffset]::UtcNow }); Write-State 'running' }

try {
    if (-not (Test-Path -LiteralPath $VerifiedBackupManifest -PathType Leaf)) { throw 'Verified backup manifest not found.' }
    Write-State 'started'
    Invoke-Gate 'stop-isestudio' $StopISEStudioCommand
    Invoke-Gate 'restore-db-permission-and-backup' $RestoreDbCommand
    Invoke-Gate 'unlock-rust' $UnlockRustCommand
    Invoke-Gate 'start-rust' $StartRustCommand
    $record = [pscustomobject]@{ operation = 'production-rollback'; completed_at = [DateTimeOffset]::UtcNow; python_service = $PythonServiceName; verified_backup_manifest = (Resolve-Path $VerifiedBackupManifest).Path; steps = @($steps); status = 'passed'; backup_retained = $true }
    $parent = Split-Path -Parent $RecordPath; if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }; $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $RecordPath -Encoding UTF8
    Write-State 'passed'
    Write-Output "Production rollback passed. Backup retained. Record: $(Redact $RecordPath)"
    exit 0
}
catch {
    Write-State 'failed' $_.Exception.Message
    [Console]::Error.WriteLine("Production rollback failed: $(Redact $_.Exception.Message)")
    exit 1
}