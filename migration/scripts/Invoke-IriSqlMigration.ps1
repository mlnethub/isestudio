# Invoke-IriSqlMigration.ps1
#
# Thin wrapper around the OnToPilot.Migration CLI host that drives
# IriMigrationCommand.RunAsync(sql). The PowerShell layer exists so the
# cutover orchestration (Invoke-ProductionCutover.ps1) can drive the
# IRI prefix rewrite the same way it drives Invoke-BlobMigration.ps1
# and Invoke-SqlMigration.ps1 — as a PowerShell child process with
# structured parameters, captured stdout, and explicit exit codes that
# the IRI cutover gate can inspect.
#
# The actual REPLACE(col, @from, @to) + dry-run + apply logic lives in
# src/OnToPilot.Migration/Iri/IriSqlMigrator.cs (Phase 1). This script
# does no SQL work of its own beyond argument translation.
#
# Usage (rehearsal):
#   pwsh migration/scripts/Invoke-IriSqlMigration.ps1 `
#       -PostgresConnectionString "Host=...;Username=postgres;Password=...;Database=ontopilot_rehearsal" `
#       -FromPrefix "http://ontopilot.local/" `
#       -ToPrefix   "http://goodcrew.local/" `
#       -DryRun
#
# Usage (production cutover, apply mode):
#   pwsh migration/scripts/Invoke-IriSqlMigration.ps1 `
#       -PostgresConnectionString "Host=...;Username=postgres;Password=...;Database=ontopilot" `
#       -FromPrefix "http://ontopilot.local/" `
#       -ToPrefix   "http://goodcrew.local/"
#
# Exit codes:
#   0 - migration completed (or dry-run completed); reports AffectedRows
#       per column on stdout for the cutover record's expected-sql-checksums.
#   1 - migration failed (constraint violation, schema mismatch, bad args, etc.).
#   2 - environment failure (missing dotnet, project not built, etc.).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PostgresConnectionString,

    [Parameter(Mandatory = $false)]
    [string]$FromPrefix = "http://ontopilot.local/",

    [Parameter(Mandatory = $false)]
    [string]$ToPrefix = "http://goodcrew.local/",

    [Parameter(Mandatory = $false)]
    [switch]$DryRun,

    [Parameter(Mandatory = $false)]
    [string]$ProjectPath = "src/OnToPilot.Migration/OnToPilot.Migration.csproj"
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "Invoke-IriSqlMigration.ps1 requires PowerShell 7+ (found $($PSVersionTable.PSVersion))."
    exit 2
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if (-not $dotnet) {
    Write-Error "Invoke-IriSqlMigration.ps1: 'dotnet' was not found on PATH. Install the .NET SDK."
    exit 2
}

$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath -ErrorAction SilentlyContinue)?.Path
if (-not $ProjectPath -or -not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
    Write-Error "Invoke-IriSqlMigration.ps1: project file '$ProjectPath' not found."
    exit 2
}

if ($FromPrefix -eq $ToPrefix) {
    Write-Error "Invoke-IriSqlMigration.ps1: -FromPrefix and -ToPrefix are identical ('$FromPrefix'). No work to do."
    exit 1
}

# Argument list forwarded verbatim to the .NET host. Keep this in sync
# with IriMigrationCommand.SqlCliArgs in src/OnToPilot.Migration/Iri/.
$cliArgs = @(
    "iri",
    "sql",
    "--postgres-connection-string", $PostgresConnectionString,
    "--from-prefix", $FromPrefix,
    "--to-prefix", $ToPrefix
)
if ($DryRun) {
    $cliArgs += @("--dry-run")
}

# F-4 redaction: never echo the Postgres connection string to stdout.
# In a production cutover this would leak credentials into any log
# aggregator that captures the script's output.
$cliArgsForLog = @()
$redactNext = $false
foreach ($arg in $cliArgs) {
    if ($redactNext) {
        $cliArgsForLog += "<redacted>"
        $redactNext = $false
        continue
    }
    if ($arg -eq "--postgres-connection-string") {
        $cliArgsForLog += $arg
        $redactNext = $true
        continue
    }
    $cliArgsForLog += $arg
}
Write-Host "[Invoke-IriSqlMigration] running: dotnet run --project $ProjectPath --no-build -- $($cliArgsForLog -join ' ')"
& $dotnet run --project $ProjectPath --no-build -- @cliArgs
$exitCode = $LASTEXITCODE
Write-Host "[Invoke-IriSqlMigration] dotnet exited with code $exitCode"
exit $exitCode