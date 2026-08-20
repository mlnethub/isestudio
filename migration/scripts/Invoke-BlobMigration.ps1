# Invoke-BlobMigration.ps1
#
# Thin wrapper around the OnToPilot.Migration CLI host that drives
# BlobMigrationCommand. The PowerShell layer exists so the rehearsal /
# cutover orchestration (Task 4) can drive the migration the same way
# it drives Export-PythonRdf.ps1 / Invoke-ContractComparison.ps1 — as a
# PowerShell child process with structured parameters, captured stdout,
# and explicit exit codes that Task 4's gate can inspect.
#
# The actual SHA-256 streaming + MinIO upload + state-store resume logic
# lives in src/OnToPilot.Migration/Blobs/BlobMigrationCommand.cs. This
# script does no blob work of its own beyond argument translation.
#
# Usage:
#   pwsh migration/scripts/Invoke-BlobMigration.ps1 `
#       -Source backend/data/blobs `
#       -Bucket ontopilot-blobs `
#       -MinioEndpoint http://127.0.0.1:9000 `
#       -MinioAccessKey minioadmin `
#       -MinioSecretKey minioadmin `
#       -PostgresConnectionString "Host=...;Username=postgres;Password=...;Database=ontopilot" `
#       -ManifestOut .artifacts/blob-manifest.json `
#       -StatePath .artifacts/blob-state.json `
#       -DryRun
#
# Exit codes:
#   0 - migration completed (or dry-run completed); manifest written
#       when -ManifestOut was supplied.
#   1 - migration failed (corruption gate, MinIO error, bad args, etc.).
#   2 - environment failure (missing dotnet, project not built, etc.).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Bucket,

    [Parameter(Mandatory = $true)]
    [string]$MinioEndpoint,

    [Parameter(Mandatory = $true)]
    [string]$MinioAccessKey,

    [Parameter(Mandatory = $true)]
    [string]$MinioSecretKey,

    [Parameter(Mandatory = $false)]
    [string]$MinioRegion = "us-east-1",

    [Parameter(Mandatory = $true)]
    [string]$PostgresConnectionString,

    [Parameter(Mandatory = $false)]
    [string]$ManifestOut,

    [Parameter(Mandatory = $false)]
    [string]$StatePath,

    [Parameter(Mandatory = $false)]
    [switch]$DryRun,

    [Parameter(Mandatory = $false)]
    [switch]$Force,

    [Parameter(Mandatory = $false)]
    [string]$ProjectPath = "src/OnToPilot.Migration/OnToPilot.Migration.csproj"
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "Invoke-BlobMigration.ps1 requires PowerShell 7+ (found $($PSVersionTable.PSVersion))."
    exit 2
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if (-not $dotnet) {
    Write-Error "Invoke-BlobMigration.ps1: 'dotnet' was not found on PATH. Install the .NET SDK."
    exit 2
}

$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath -ErrorAction SilentlyContinue)?.Path
if (-not $ProjectPath -or -not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
    Write-Error "Invoke-BlobMigration.ps1: project file '$ProjectPath' not found."
    exit 2
}

$SourceFull = (Resolve-Path -LiteralPath $Source -ErrorAction SilentlyContinue)?.Path
if (-not $SourceFull -or -not (Test-Path -LiteralPath $SourceFull -PathType Container)) {
    Write-Error "Invoke-BlobMigration.ps1: -Source '$Source' does not exist or is not a directory."
    exit 1
}

# Argument list forwarded verbatim to the .NET host. Keep this in sync
# with BlobMigrationCliArgs.Parse in src/OnToPilot.Migration/Blobs/.
$cliArgs = @(
    "blobs",
    "--source", $SourceFull,
    "--bucket", $Bucket,
    "--minio-endpoint", $MinioEndpoint,
    "--minio-access-key", $MinioAccessKey,
    "--minio-secret-key", $MinioSecretKey,
    "--minio-region", $MinioRegion,
    "--postgres-connection-string", $PostgresConnectionString
)

if ($ManifestOut) {
    $manifestFull = [System.IO.Path]::GetFullPath(([System.IO.Path]::Combine((Get-Location).Path, $ManifestOut))
        , (Get-Location).Path)
    $cliArgs += @("--manifest-out", $manifestFull)
}

if ($StatePath) {
    $stateFull = [System.IO.Path]::GetFullPath(([System.IO.Path]::Combine((Get-Location).Path, $StatePath))
        , (Get-Location).Path)
    $cliArgs += @("--state-path", $stateFull)
}

if ($DryRun) {
    $cliArgs += @("--dry-run")
}
if ($Force) {
    $cliArgs += @("--force")
}

# F-4 fix: never echo secret values to stdout. In a production cutover
# this would leak --minio-secret-key and --postgres-connection-string
# into any log aggregator that captures the script's output. Print
# argument NAMES only — values stay in $cliArgs for the dotnet call
# but never reach the operator log line.
$cliArgsForLog = @()
$redactNext = $false
foreach ($arg in $cliArgs) {
    if ($redactNext) {
        $cliArgsForLog += "<redacted>"
        $redactNext = $false
        continue
    }
    if ($arg -in @("--minio-secret-key", "--postgres-connection-string", "--minio-access-key")) {
        $cliArgsForLog += $arg
        $redactNext = $true
        continue
    }
    $cliArgsForLog += $arg
}
Write-Host "[Invoke-BlobMigration] running: dotnet run --project $ProjectPath -- $($cliArgsForLog -join ' ')"
& $dotnet run --project $ProjectPath --no-build -- @cliArgs
$exitCode = $LASTEXITCODE
Write-Host "[Invoke-BlobMigration] dotnet exited with code $exitCode"
exit $exitCode
