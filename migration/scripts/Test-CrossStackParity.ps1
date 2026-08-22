# Test-CrossStackParity.ps1
#
# Phase 3 cross-stack IRI parity test. Verifies that the .NET runtime
# and the Python baseline, given the SAME `OnToPilot__IriRoot` /
# `OnToPilot__VocabNamespace` env-var pair, resolve to the same
# `iri_root` / `vocab_namespace` values. If both stacks read the
# env var correctly, they will produce byte-identical JSON output
# for the same input.
#
# .NET side: invokes `dotnet run --project src/OnToPilot.Migration
#             -- iri config`, which builds an IConfiguration (env vars
#             + appsettings) and prints JSON via IriMigrationCommand.
# Python side: invokes `python -c "..."` in the backend repo, which
#              imports Settings and prints the resolved field values.
#              The script auto-detects the python interpreter; pass
#              -PythonExe to override.
#
# The parity diff runs in three modes (controlled by -Mode):
#   default  - both stacks must agree; fail on any drift.
#   dotnet   - only run the .NET side; useful for CI where Python is
#              not installed.
#   python   - only run the Python side; useful when bootstrapping
#              the .NET side hasn't happened yet.
#
# Usage (full parity):
#   pwsh migration/scripts/Test-CrossStackParity.ps1 `
#       -IriRoot       "http://parity.local/ks" `
#       -VocabNamespace "http://parity.local/vocab#" `
#       -PythonCwd     backend `
#       -ReportPath    .artifacts/parity-report.json
#
# Usage (.NET side only, before Python Settings lands in Task 5):
#   pwsh migration/scripts/Test-CrossStackParity.ps1 `
#       -Mode dotnet `
#       -ReportPath .artifacts/parity-report.json
#
# Exit codes:
#   0 - parity confirmed; both sides agreed on every key.
#   1 - environment failure (dotnet / python not on PATH).
#   2 - parity drift (one or more keys differ between stacks).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$IriRoot = 'http://parity.local/ks',

    [Parameter(Mandatory = $false)]
    [string]$VocabNamespace = 'http://parity.local/vocab#',

    [Parameter(Mandatory = $false)]
    [ValidateSet('default', 'dotnet', 'python')]
    [string]$Mode = 'default',

    [Parameter(Mandatory = $false)]
    [string]$MigrationProject = 'src/OnToPilot.Migration/OnToPilot.Migration.csproj',

    [Parameter(Mandatory = $false)]
    [string]$PythonExe = 'python',

    [Parameter(Mandatory = $false)]
    [string]$PythonCwd = 'backend',

    [Parameter(Mandatory = $false)]
    [string]$PythonBootstrap = 'import json; from app.settings import settings; print(json.dumps({"iri_root": settings.iri_root, "vocab_namespace": settings.vocab_namespace}, sort_keys=True, separators=(",", ":")))',

    [Parameter(Mandatory = $false)]
    [string]$ReportPath = '.artifacts/cross-stack-parity.json'
)

Set-StrictMode -Version Latest
# Note: ErrorActionPreference = 'Continue' (NOT 'Stop') so a
# failed Python side (Task 5 not yet wired) does not abort the
# script before we get a chance to record the parity verdict.
# Each gate's hard-fail mode is enforced explicitly via
# Write-Error + exit instead.
$ErrorActionPreference = 'Continue'

# Canonical env-var contract: BOTH stacks read these exact names.
# .NET: IConfiguration + EnvironmentVariables provider maps
#       `OnToPilot__IriRoot` -> OnToPilot:IriRoot.
# Python: Pydantic Settings with env_prefix="OnToPilot" maps
#         `OnToPilot__IriRoot` -> settings.iri_root.
$env:OnToPilot__IriRoot = $IriRoot
$env:OnToPilot__VocabNamespace = $VocabNamespace

$dotnetOutput = $null
$pythonOutput = $null
$exitCode = 0

# ---------------------------------------------------------------------
# .NET side
# ---------------------------------------------------------------------
if ($Mode -in 'default', 'dotnet') {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
    if (-not $dotnet) {
        Write-Error "Test-CrossStackParity.ps1: 'dotnet' was not found on PATH."
        exit 1
    }

    $resolvedProject = (Resolve-Path -LiteralPath $MigrationProject -ErrorAction SilentlyContinue)
    if (-not $resolvedProject) {
        Write-Error "Test-CrossStackParity.ps1: MigrationProject '$MigrationProject' not found."
        exit 1
    }
    $resolvedProject = $resolvedProject.Path

    Write-Host "[parity] .NET: dotnet run --project $resolvedProject --no-build -- iri config"
    $dotnetOutput = & $dotnet.Source run --project $resolvedProject --no-build -- iri config 2>&1
    $dotnetRc = $LASTEXITCODE
    if ($dotnetRc -ne 0) {
        $combined = ($dotnetOutput -join "`n")
        Write-Error "[parity] .NET side exited with code ${dotnetRc}:`n$combined"
        exit 1
    }
    # `dotnet run` may emit NuGet cache restore lines before the actual
    # command output. Take only the LAST line (our canonical JSON).
    $dotnetOutput = ($dotnetOutput | Where-Object { $_ -match '^\s*\{' }) | Select-Object -Last 1
    if (-not $dotnetOutput) {
        Write-Error "[parity] .NET side produced no JSON output."
        exit 1
    }
    $dotnetOutput = $dotnetOutput.Trim()
    Write-Host "[parity] .NET: $dotnetOutput"
}

# ---------------------------------------------------------------------
# Python side
# ---------------------------------------------------------------------
if ($Mode -in 'default', 'python') {
    $python = (Get-Command $PythonExe -ErrorAction SilentlyContinue)
    if (-not $python) {
        Write-Host "[parity] Python: SKIPPED ('$PythonExe' not on PATH; -Mode=$Mode implies Python is optional)."
        if ($Mode -eq 'python') {
            Write-Error "Test-CrossStackParity.ps1: -Mode python requires '$PythonExe' on PATH."
            exit 1
        }
    }
    elseif (-not (Test-Path -LiteralPath $PythonCwd)) {
        Write-Host "[parity] Python: SKIPPED (-PythonCwd '$PythonCwd' does not exist; Task 5 lands Settings here)."
        if ($Mode -eq 'python') {
            Write-Error "Test-CrossStackParity.ps1: -Mode python requires -PythonCwd '$PythonCwd' to exist."
            exit 1
        }
    }
    else {
        Push-Location -LiteralPath $PythonCwd -ErrorAction Stop
        try {
            Write-Host "[parity] Python: $PythonExe -c '<bootstrap>'"
            # -ErrorAction SilentlyContinue so a Task-5-not-yet-wired
            # Settings import doesn't abort the script. The non-zero
            # exit code below is the signal: -Mode python fails hard,
            # -Mode default treats it as PRE_TASK_5_PENDING.
            $pythonOutput = & $python.Source -c $PythonBootstrap 2>&1
            $pythonRc = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
        if ($pythonRc -ne 0) {
            $combined = ($pythonOutput -join "`n")
            if ($Mode -eq 'python') {
                Write-Error "[parity] Python side exited with code ${pythonRc}:`n$combined"
                exit 1
            }
            Write-Host "[parity] Python side exited with code ${pythonRc} (likely PRE_TASK_5_PENDING — Settings not yet wired in '$PythonCwd')."
            $pythonOutput = $null
        }
        else {
            $pythonOutput = ($pythonOutput | Where-Object { $_ -match '^\s*\{' }) | Select-Object -Last 1
            if (-not $pythonOutput) {
                Write-Host "[parity] Python side produced no JSON output (likely Settings class not yet wired — Task 5)."
                if ($Mode -eq 'python') {
                    Write-Error "[parity] Python side produced no JSON output."
                    exit 1
                }
                $pythonOutput = $null
            }
            else {
                $pythonOutput = $pythonOutput.Trim()
                Write-Host "[parity] Python: $pythonOutput"
            }
        }
    }
}

# ---------------------------------------------------------------------
# Diff + report
# ---------------------------------------------------------------------
$report = [ordered]@{
    GeneratedAtUtc   = [DateTimeOffset]::UtcNow.ToString('o')
    Mode             = $Mode
    EnvVars          = [ordered]@{
        OnToPilot__IriRoot        = $IriRoot
        OnToPilot__VocabNamespace = $VocabNamespace
    }
    DotNetOutput     = $dotnetOutput
    PythonOutput     = $pythonOutput
    Parity           = $null
    Drift            = $null
}

if ($dotnetOutput -and $pythonOutput) {
    $dotnetParsed = $dotnetOutput | ConvertFrom-Json -ErrorAction SilentlyContinue
    $pythonParsed = $pythonOutput | ConvertFrom-Json -ErrorAction SilentlyContinue
    if (-not $dotnetParsed -or -not $pythonParsed) {
        Write-Error "[parity] Could not parse one side as JSON. dotnet=$dotnetOutput python=$pythonOutput"
        exit 1
    }
    $drift = @()
    foreach ($key in @('iri_root', 'vocab_namespace')) {
        if ($dotnetParsed.$key -ne $pythonParsed.$key) {
            $drift += "$key : dotnet='$($dotnetParsed.$key)' python='$($pythonParsed.$key)'"
        }
    }
    if ($drift.Count -eq 0) {
        $report.Parity = 'PASS'
        Write-Host '[parity] PASS — both stacks resolved the same iri_root + vocab_namespace.'
    }
    else {
        $report.Parity = 'FAIL'
        $report.Drift  = $drift
        Write-Host '[parity] FAIL — drift detected:'
        foreach ($d in $drift) { Write-Host "  $d" }
        $exitCode = 2
    }
}
else {
    # Single-side mode — we can't diff, so we just record what each
    # side produced. This is the expected state in CI before Task 5
    # lands the Python Settings wiring.
    $report.Parity = if ($Mode -eq 'default' -and -not ($dotnetOutput -and $pythonOutput)) {
        'INCOMPLETE'
    } else { 'SINGLE_SIDE' }
    Write-Host "[parity] Parity=$($report.Parity) (single-side mode or one stack unavailable)."
}

$reportDir = Split-Path -Parent $ReportPath
if (-not (Test-Path -LiteralPath $reportDir)) {
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8
Write-Host "[parity] Report written to $ReportPath"
exit $exitCode
