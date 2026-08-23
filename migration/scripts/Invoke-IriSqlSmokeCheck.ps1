# Invoke-IriSqlSmokeCheck.ps1
#
# Thin wrapper around the OnToPilot.Migration CLI host that drives
# IriMigrationCommand.RunAsync(sql-smoke-check). The PowerShell layer
# exists so the cutover orchestration (Invoke-ProductionCutover.ps1)
# can drive the post-migration verification the same way it drives
# Invoke-IriSqlMigration.ps1 — as a PowerShell child process with
# structured parameters, captured stdout, and explicit exit codes
# that the cutover gate can inspect.
#
# The actual COUNT(*) per (table, column) + residual aggregation lives
# in src/OnToPilot.Migration/Iri/IriSqlVerifier.cs (Phase 3 P3-4).
# This script does no SQL work of its own beyond argument translation.
#
# Usage:
#   pwsh migration/scripts/Invoke-IriSqlSmokeCheck.ps1 `
#       -PostgresConnectionString "Host=...;Username=postgres;Password=...;Database=ontopilot" `
#       -FromPrefix "http://ontopilot.local/" `
#       -ToPrefix   "http://goodcrew.local/" `
#       -ReportOut  ".artifacts/iri-sql-verify-report.json"
#
# Exit codes:
#   0 - every IRI-bearing column has zero residual legacy-prefix rows.
#   1 - at least one column still contains the legacy prefix.
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
    [string]$ReportOut,

    [Parameter(Mandatory = $false)]
    [string]$ProjectPath = "src/OnToPilot.Migration/OnToPilot.Migration.csproj"
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "Invoke-IriSqlSmokeCheck.ps1 requires PowerShell 7+ (found $($PSVersionTable.PSVersion))."
    exit 2
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if (-not $dotnet) {
    Write-Error "Invoke-IriSqlSmokeCheck.ps1: 'dotnet' was not found on PATH. Install the .NET SDK."
    exit 2
}

$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath -ErrorAction SilentlyContinue)?.Path
if (-not $ProjectPath -or -not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
    Write-Error "Invoke-IriSqlSmokeCheck.ps1: project file '$ProjectPath' not found."
    exit 2
}

if ($FromPrefix -eq $ToPrefix) {
    Write-Error "Invoke-IriSqlSmokeCheck.ps1: -FromPrefix and -ToPrefix are identical ('$FromPrefix'). Nothing to verify."
    exit 1
}

# Argument list forwarded verbatim to the .NET host. Keep this in sync
# with IriMigrationCommand.ParseSqlArgs in src/OnToPilot.Migration/Iri/.
$cliArgs = @(
    "iri",
    "sql-smoke-check",
    "--postgres-connection-string", $PostgresConnectionString,
    "--from-prefix", $FromPrefix,
    "--to-prefix",   $ToPrefix
)
if ($ReportOut) {
    $cliArgs += @("--report-out", $ReportOut)
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
Write-Host "[Invoke-IriSqlSmokeCheck] running: dotnet run --project $ProjectPath --no-build -- $($cliArgsForLog -join ' ')"
& $dotnet run --project $ProjectPath --no-build -- @cliArgs
$exitCode = $LASTEXITCODE
Write-Host "[Invoke-IriSqlSmokeCheck] dotnet exited with code $exitCode"
exit $exitCode
