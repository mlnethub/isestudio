[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('fresh', 'restored', 'upgrade')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$Manifest,
    [Parameter(Mandatory = $true)][string]$PostgresConnectionString,
    [string]$Backup,
    [string]$RdfSource,
    [string]$RdfCopy,
    [string]$RdfWork,
    [string]$BlobManifest,
    [string]$ExpectedBackupSha256,
    [Nullable[long]]$ExpectedBackupSize
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\..\src\ISEStudio.Migration\ISEStudio.Migration.csproj'
$arguments = @('run', '--project', $project, '--', 'rehearsal', '--mode', $Mode,
    '--manifest', $Manifest, '--postgres-connection-string', $PostgresConnectionString)
if ($Backup) { $arguments += @('--backup', $Backup) }
if ($RdfSource) { $arguments += @('--rdf-source', $RdfSource) }
if ($RdfCopy) { $arguments += @('--rdf-copy', $RdfCopy) }
if ($RdfWork) { $arguments += @('--rdf-work', $RdfWork) }
if ($BlobManifest) { $arguments += @('--blob-manifest', $BlobManifest) }
if ($ExpectedBackupSha256) { $arguments += @('--expected-backup-sha256', $ExpectedBackupSha256) }
if ($null -ne $ExpectedBackupSize) { $arguments += @('--expected-backup-size', $ExpectedBackupSize.ToString()) }

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'Test-MigrationGate.ps1') -Manifest $Manifest
exit $LASTEXITCODE