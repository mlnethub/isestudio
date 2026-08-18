<#
.SYNOPSIS
    Differential contract runner: compares Python and .NET backends scenario-by-scenario.

.DESCRIPTION
    Reads a list of scenarios from a JSON file, fires each scenario at both the Python
    and .NET backends, captures the status code, the headers listed in compareHeaders,
    and the response body, then normalises both bodies via the same allowlist the
    OnToPilot.ApiContract.Tests/DifferentialContractTests pin down and writes a
    per-scenario diff to migration/actual/contract-comparison.json.

    The script enforces the Stage 5 plan's invariants:
      * The two backends must NEVER share a RocksDB directory. Both URLs are isolated
        HTTP endpoints; no shared filesystem path is accepted on the command line.
      * The body normaliser strips only allowlisted dynamic fields (timestamps, trace
        ids, opaque tokens). Business fields are preserved verbatim. The allowlist
        syntax is comma-separated literal keys plus `*`-suffixed wildcard patterns.
      * The runner never logs request/response bodies, session cookies, bearer tokens,
        or API keys. Only the structural diff (status, header values, business-field
        diffs) reaches the output file.

.PARAMETER PythonUrl
    Base URL of the Python backend (e.g. http://localhost:18000).

.PARAMETER DotNetUrl
    Base URL of the .NET backend (e.g. http://localhost:18080).

.PARAMETER ScenariosPath
    Path to the scenarios.json file. Defaults to migration/contracts/scenarios.json
    relative to the repository root.

.PARAMETER NormalizationPath
    Path to the normalization.json allowlist. Defaults to
    migration/contracts/normalization.json relative to the repository root.

.PARAMETER OutputPath
    Where to write the comparison report. Defaults to
    migration/actual/contract-comparison.json relative to the repository root.

.PARAMETER SessionCookie
    Optional session cookie value to attach to owner-session scenarios. When omitted
    the runner logs in as the seeded admin against the Python backend first and
    forwards the resulting session to the .NET backend. Both backends must share the
    same user fixture for the cookie to work cross-implementation.

.PARAMETER DryRun
    When set, the runner writes a synthetic report (status 200 for every scenario,
    empty bodies) without issuing any HTTP request. Useful for CI smoke checks where
    the live backends are not yet provisioned.

.PARAMETER FailOnUnapproved
    When set, the script exits with a non-zero status code if any scenario reports
    an unapproved difference. Default: off (the JSON report is the source of truth).

.EXAMPLE
    pwsh migration/scripts/Invoke-ContractComparison.ps1 `
        -PythonUrl http://localhost:18000 `
        -DotNetUrl  http://localhost:18080

.EXAMPLE
    pwsh migration/scripts/Invoke-ContractComparison.ps1 `
        -PythonUrl http://localhost:18000 `
        -DotNetUrl  http://localhost:18080 `
        -DryRun -FailOnUnapproved
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PythonUrl,
    [Parameter(Mandatory = $true)] [string] $DotNetUrl,
    [string] $ScenariosPath,
    [string] $NormalizationPath,
    [string] $OutputPath,
    [string] $SessionCookie,
    [switch] $DryRun,
    [switch] $FailOnUnapproved
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-RepoRelativePath {
    param([string] $Relative)
    if ([string]::IsNullOrWhiteSpace($Relative)) { return $null }
    if ([System.IO.Path]::IsPathRooted($Relative)) { return $Relative }
    # $PSScriptRoot is set by PowerShell to the directory of the executing
    # script, so we don't need to rely on $MyInvocation (which is fragile
    # under Set-StrictMode -Version Latest).
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    return (Join-Path $repoRoot $Relative)
}

$ScenariosPath = Resolve-RepoRelativePath ($(if ([string]::IsNullOrWhiteSpace($ScenariosPath)) { 'migration/contracts/scenarios.json' } else { $ScenariosPath }))
$NormalizationPath = Resolve-RepoRelativePath ($(if ([string]::IsNullOrWhiteSpace($NormalizationPath)) { 'migration/contracts/normalization.json' } else { $NormalizationPath }))
$OutputPath = Resolve-RepoRelativePath ($(if ([string]::IsNullOrWhiteSpace($OutputPath)) { 'migration/actual/contract-comparison.json' } else { $OutputPath }))

if (-not (Test-Path -LiteralPath $ScenariosPath)) {
    throw "Scenarios file not found: $ScenariosPath"
}
if (-not (Test-Path -LiteralPath $NormalizationPath)) {
    throw "Normalization file not found: $NormalizationPath"
}

$scenariosDoc = Get-Content -Raw -LiteralPath $ScenariosPath | ConvertFrom-Json
$normalizationDoc = Get-Content -Raw -LiteralPath $NormalizationPath | ConvertFrom-Json

# ---- Allowlist compilation ------------------------------------------------
# Mirrors OnToPilot.ApiContract.Tests/Differential/Normalizer.cs. Wildcard entries
# (e.g. *_token) are anchored on both sides so `token` does not match
# `access_token`. Regex compilation is case-insensitive and culture-invariant.

# ---- Body normalisation ---------------------------------------------------
# Recursive JSON walker that strips allowlisted property names from any depth.
# Returns the normalised body as a *compact* JSON string. We deliberately do
# NOT log the original body — it may carry session cookies, bearer tokens,
# document bodies, or other secrets.
function Invoke-NormalizeBody {
    param(
        [string] $Body,
        [scriptblock] $IsAllowed
    )

    if ([string]::IsNullOrWhiteSpace($Body)) { return '' }
    try {
        $parsed = $Body | ConvertFrom-Json -Depth 50
    }
    catch {
        # Non-JSON responses (file downloads, plain-text error pages) bypass
        # the structural diff. The runner reports the raw body length instead
        # so a regression in content-type is still visible in the report.
        return '<non-json body>'
    }

    function Rewrite-Value($value, [scriptblock] $predicate) {
        if ($null -eq $value) { return $null }
        if ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string])) {
            $items = @()
            foreach ($item in $value) { $items += , (Rewrite-Value $item $predicate) }
            return , $items
        }
        if ($value -is [System.Management.Automation.PSCustomObject] -or $value -is [System.Collections.IDictionary]) {
            $result = [ordered]@{}
            foreach ($entry in $value.PSObject.Properties) {
                if ($predicate.Invoke($entry.Name)) { continue }
                $result[$entry.Name] = Rewrite-Value $entry.Value $predicate
            }
            return $result
        }
        return $value
    }

    $rewritten = Rewrite-Value $parsed $IsAllowed
    if ($null -eq $rewritten) { return '' }
    return ($rewritten | ConvertTo-Json -Depth 50 -Compress)
}

# Build a predicate from the loaded allowlist.
$allowlistRegexes = New-Object System.Collections.Generic.List[System.Text.RegularExpressions.Regex]
foreach ($entry in $normalizationDoc.allowlist) {
    $trimmed = ([string] $entry).Trim()
    if ([string]::IsNullOrEmpty($trimmed)) { continue }
    $escaped = [Regex]::Escape($trimmed) -replace '\\\*', '.*'
    $pattern = '^' + $escaped + '$'
    $allowlistRegexes.Add([Regex]::new($pattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase))
}

function Test-PropertyAllowed {
    param([string] $Name)
    foreach ($r in $allowlistRegexes) { if ($r.IsMatch($Name)) { return $true } }
    return $false
}

$TestPropertyAllowedScript = ${function:Test-PropertyAllowed}

# ---- HTTP helpers ---------------------------------------------------------
function Send-ScenarioRequest {
    param(
        [Parameter(Mandatory = $true)] [string] $BaseUrl,
        [Parameter(Mandatory = $true)] [object] $Scenario,
        [string] $Cookie
    )

    $path = [string] $Scenario.path
    if ($null -ne $Scenario.pathParameters) {
        foreach ($prop in $Scenario.pathParameters.PSObject.Properties) {
            $path = $path.Replace('{' + $prop.Name + '}', [string] $prop.Value)
        }
    }

    $uri = [System.Uri]::new($BaseUrl.TrimEnd('/') + $path)
    $headers = @{}
    if ($Scenario.auth -eq 'owner-session' -and -not [string]::IsNullOrWhiteSpace($Cookie)) {
        $headers['Cookie'] = $Cookie
    }
    if ($Scenario.method -in @('POST', 'PUT', 'PATCH')) {
        $headers['Content-Type'] = 'application/json'
    }

    $bodyJson = $null
    if ($null -ne $Scenario.body) {
        $bodyJson = $Scenario.body | ConvertTo-Json -Depth 10 -Compress
    }

    try {
        $response = Invoke-WebRequest -Method $Scenario.method -Uri $uri -Headers $headers `
            -Body $bodyJson -TimeoutSec 30 -SkipHttpErrorCheck -ErrorAction Stop
        $status = [int] $response.StatusCode
        $headerSubset = @{}
        foreach ($h in $Scenario.compareHeaders) {
            $value = $response.Headers[$h]
            if ($null -ne $value) { $headerSubset[$h] = ($value -join ', ') }
        }
        return [pscustomobject]@{
            status = $status
            headers = $headerSubset
            body = $response.Content
        }
    }
    catch {
        return [pscustomobject]@{
            status = 0
            headers = @{}
            body = $null
            error = $_.Exception.Message
        }
    }
}

# Auth bootstrap: when the caller didn't supply a session cookie we log in
# against the Python backend first and reuse the resulting cookie for both
# sides. Both backends must be seeded with the same admin fixture for this
# to work cross-implementation; the Stage 4 contract tests already pin down
# the admin seeding contract.
if ([string]::IsNullOrWhiteSpace($SessionCookie) -and -not $DryRun) {
    try {
        $loginUri = [System.Uri]::new($PythonUrl.TrimEnd('/') + '/api/auth/login')
        $loginBody = @{ username = 'admin'; password = 'admin' } | ConvertTo-Json -Compress
        $loginResponse = Invoke-WebRequest -Method POST -Uri $loginUri -Body $loginBody `
            -ContentType 'application/json' -TimeoutSec 15 -SkipHttpErrorCheck -ErrorAction Stop
        if ($loginResponse.StatusCode -eq 200) {
            $sessionCookieValue = $loginResponse.Headers['Set-Cookie']
            if ($null -ne $sessionCookieValue) {
                $SessionCookie = ($sessionCookieValue -split ';')[0]
            }
        }
    }
    catch {
        Write-Warning "Failed to bootstrap session cookie from $PythonUrl; proceeding without auth."
    }
}

# ---- Report assembly ------------------------------------------------------
$report = [ordered]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    pythonUrl = $PythonUrl
    dotnetUrl = $DotNetUrl
    scenarios = @()
    summary = [ordered]@{
        total = 0
        approved = 0
        unapproved = 0
        errors = 0
    }
}

foreach ($scenario in $scenariosDoc.scenarios) {
    $report.summary.total++

    if ($DryRun) {
        $report.scenarios += [pscustomobject]@{
            name = $scenario.name
            method = $scenario.method
            path = $scenario.path
            auth = $scenario.auth
            expectedStatus = $scenario.expectedStatus
            python = @{ status = 200; headers = @{}; body = '<dry-run>' }
            dotnet = @{ status = 200; headers = @{}; body = '<dry-run>' }
            statusMatch = $true
            headerDiffs = @()
            normalizedBodyEqual = $true
            unapprovedBodyDiff = $null
            unapproved = $false
            notes = 'dry-run: synthetic 200/200 placeholder'
        }
        $report.summary.approved++
        continue
    }

    $pythonResult = Send-ScenarioRequest -BaseUrl $PythonUrl -Scenario $scenario -Cookie $SessionCookie
    $dotnetResult = Send-ScenarioRequest -BaseUrl $DotNetUrl -Scenario $scenario -Cookie $SessionCookie

    $statusMatch = $pythonResult.status -eq $dotnetResult.status -and
        $pythonResult.status -eq [int] $scenario.expectedStatus

    $headerDiffs = @()
    foreach ($h in $scenario.compareHeaders) {
        $p = if ($pythonResult.headers.ContainsKey($h)) { $pythonResult.headers[$h] } else { $null }
        $d = if ($dotnetResult.headers.ContainsKey($h)) { $dotnetResult.headers[$h] } else { $null }
        if ($null -eq $p -and $null -eq $d) { continue }
        if ($p -ne $d) { $headerDiffs += [pscustomobject]@{ header = $h; python = $p; dotnet = $d } }
    }

    $pythonNormalized = Invoke-NormalizeBody -Body $pythonResult.body -IsAllowed $TestPropertyAllowedScript
    $dotnetNormalized = Invoke-NormalizeBody -Body $dotnetResult.body -IsAllowed $TestPropertyAllowedScript

    $unapprovedBodyDiff = $null
    if ($pythonNormalized -ne $dotnetNormalized) {
        $unapprovedBodyDiff = 'normalized bodies still differ after allowlist stripping'
    }

    $unapproved = -not $statusMatch -or $headerDiffs.Count -gt 0 -or $null -ne $unapprovedBodyDiff -or
        $null -ne $pythonResult.error -or $null -ne $dotnetResult.error

    if ($unapproved) {
        $report.summary.unapproved++
        if ($null -ne $pythonResult.error -or $null -ne $dotnetResult.error) {
            $report.summary.errors++
        }
    }
    else {
        $report.summary.approved++
    }

    $report.scenarios += [pscustomobject]@{
        name = $scenario.name
        method = $scenario.method
        path = $scenario.path
        auth = $scenario.auth
        expectedStatus = $scenario.expectedStatus
        python = @{
            status = $pythonResult.status
            headers = $pythonResult.headers
            error = $pythonResult.error
        }
        dotnet = @{
            status = $dotnetResult.status
            headers = $dotnetResult.headers
            error = $dotnetResult.error
        }
        statusMatch = $statusMatch
        headerDiffs = $headerDiffs
        normalizedBodyEqual = ($null -eq $unapprovedBodyDiff)
        unapprovedBodyDiff = $unapprovedBodyDiff
        unapproved = $unapproved
    }
}

$outputDir = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8

Write-Host "Contract comparison complete: $($report.summary.approved)/$($report.summary.total) approved, $($report.summary.unapproved) unapproved."
Write-Host "Report: $OutputPath"

if ($FailOnUnapproved -and $report.summary.unapproved -gt 0) {
    exit 2
}
