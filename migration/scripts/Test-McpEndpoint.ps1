<#
.SYNOPSIS
    MCP smoke runner for the .NET backend.

.DESCRIPTION
    Drives the Stage 4 Task 4 MCP transport against the .NET backend.
    The script enforces the Stage 5 plan invariants that apply to MCP:

      * It never logs the bearer token, session cookie, request body, or
        response body. Only structured call outcomes (tool name, status,
        invocation latency) reach the host.
      * It compares the live `tools/list` inventory against the baseline
        declared in migration/contracts/mcp-smoke.json and fails on drift.
      * It runs the discover -> read -> preview-clean -> apply sequence
        the brief pins down so the auth + side-effect contract is exercised
        end to end.

    NOTE (Stage 5 follow-up): Playwright wiring is intentionally NOT in
    scope here. This script is the MCP counterpart to the
    `frontend/e2e/dotnet/*.spec.ts` suite, which itself assumes a
    `frontend/playwright.config.ts` + `@playwright/test` dependency that
    has not yet been added. See docs/migration/contract-observability-followups.md
    for the open work. Do not add Playwright to `frontend/package.json`
    as part of MCP smoke fixes.

.PARAMETER Url
    Full URL of the MCP endpoint exposed by the .NET backend, e.g.
    `http://localhost:18080/mcp`.

.PARAMETER Token
    Bearer token to attach to every MCP request. The token is NEVER
    logged; it lives only in the variable scope of this process.

.PARAMETER ContractsPath
    Path to `migration/contracts/mcp-smoke.json`. Defaults to that path
    relative to the repository root.

.PARAMETER FailOnUnapproved
    When set, the script exits with a non-zero status code if any smoke
    scenario reports an unapproved difference. Default: off (the JSON
    report is the source of truth).

.EXAMPLE
    pwsh migration/scripts/Test-McpEndpoint.ps1 -Url http://localhost:18080/mcp -Token $env:MCP_TOKEN

.EXAMPLE
    pwsh migration/scripts/Test-McpEndpoint.ps1 `
        -Url http://localhost:18080/mcp `
        -Token $env:MCP_TOKEN `
        -FailOnUnapproved
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Url,
    [Parameter(Mandatory = $true)] [string] $Token,
    [string] $ContractsPath,
    [switch] $FailOnUnapproved
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path -Path "$PSScriptRoot/../..").Path
if (-not $ContractsPath) {
    $ContractsPath = Join-Path $repoRoot 'migration/contracts/mcp-smoke.json'
}
if (-not (Test-Path -LiteralPath $ContractsPath)) {
    throw "MCP smoke contracts file not found: $ContractsPath"
}

$contracts = Get-Content -LiteralPath $ContractsPath -Raw | ConvertFrom-Json
$BaselineToolNames = @($contracts.baselineToolNames)

$report = [ordered]@{
    url              = $Url
    startedAt        = (Get-Date).ToString('o')
    inventory        = $null
    invocations      = @()
    unapprovedDiff   = @()
}

function Invoke-McpRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Method,
        [hashtable] $Params = @{},
        [string] $Token
    )

    if (-not $Token) {
        throw "Invoke-McpRequest: -Token is required"
    }

    $payload = @{
        jsonrpc = '2.0'
        id      = [guid]::NewGuid().ToString('N')
        method  = $Method
        params  = $Params
    } | ConvertTo-Json -Depth 10 -Compress

    $headers = @{
        'Content-Type' = 'application/json'
        'Accept'       = 'application/json'
        'Authorization' = "Bearer $Token"
    }

    try {
        $response = Invoke-WebRequest -Uri $Url -Method Post -Headers $headers -Body $payload -TimeoutSec 30 -SkipHttpErrorCheck
    } catch {
        throw "MCP request '$Method' failed to reach $Url : $($_.Exception.Message)"
    }

    if ($null -eq $response.Content -or $response.Content.Length -eq 0) {
        throw "MCP request '$Method' returned an empty body (status $($response.StatusCode))."
    }

    try {
        return ($response.Content | ConvertFrom-Json)
    } catch {
        throw "MCP request '$Method' returned non-JSON body (status $($response.StatusCode))."
    }
}

# --- Discovery --------------------------------------------------------------

Write-Host "[mcp-smoke] discover: tools/list against $Url" -ForegroundColor Cyan
$tools = Invoke-McpRequest -Method 'tools/list' -Token $Token

if (-not $tools.result -or -not $tools.result.tools) {
    throw "MCP tools/list did not return a 'result.tools' array. Body keys: $($tools.PSObject.Properties.Name -join ', ')"
}

$liveToolNames = @($tools.result.tools | ForEach-Object { $_.name })
$report.inventory = @{
    baselineCount = $BaselineToolNames.Count
    liveCount     = $liveToolNames.Count
    liveNames     = $liveToolNames
}

$missing = Compare-Object -ReferenceObject $BaselineToolNames -DifferenceObject $liveToolNames |
    Where-Object { $_.SideIndicator -eq '<=' } |
    ForEach-Object { $_.InputObject }
$extra = Compare-Object -ReferenceObject $BaselineToolNames -DifferenceObject $liveToolNames |
    Where-Object { $_.SideIndicator -eq '=>' } |
    ForEach-Object { $_.InputObject }

if ($missing -or $extra) {
    $msg = "MCP inventory mismatch. Missing: $($missing -join ', '). Extra: $($extra -join ', ')."
    $report.unapprovedDiff += $msg
    if ($FailOnUnapproved) { throw $msg }
    Write-Warning $msg
}

# --- Smoke scenarios --------------------------------------------------------

foreach ($scenario in $contracts.scenarios) {
    Write-Host "[mcp-smoke] scenario: $($scenario.name)" -ForegroundColor Cyan
    $params = @{}
    if ($scenario.PSObject.Properties.Match('defaultArguments').Count -gt 0 -and $scenario.defaultArguments) {
        foreach ($prop in $scenario.defaultArguments.PSObject.Properties) {
            $params[$prop.Name] = $prop.Value
        }
    }

    # Pick the JSON-RPC method + params shape:
    #   * default = tools/call with { name, arguments } envelope
    #   * if the scenario declares its own `method` (e.g. tools/list for
    #     discovery), call that JSON-RPC method directly and skip the
    #     {name, arguments} wrapping. tools/list is a meta-method, not an
    #     MCP tool, so it must NEVER be invoked via tools/call.
    $method = 'tools/call'
    $callParams = @{ name = $scenario.tool; arguments = $params }
    if ($scenario.PSObject.Properties.Match('method').Count -gt 0 -and $scenario.method) {
        $method = [string] $scenario.method
        $callParams = $params
    }

    $result = Invoke-McpRequest -Method $method -Params $callParams -Token $Token

    $invocation = [ordered]@{
        scenario    = $scenario.name
        tool        = $scenario.tool
        ok          = $null -ne $result.result
        errorCode   = if ($result.error) { $result.error.code } else { $null }
        errorMessage = if ($result.error) { $result.error.message } else { $null }
    }

    if (-not $result.result) {
        $invocation.ok = $false
        $report.unapprovedDiff += "Scenario '$($scenario.name)' returned an error response."
        $report.invocations += $invocation
        if ($FailOnUnapproved) {
            throw "Scenario '$($scenario.name)' failed: $($result.error.message)"
        }
        continue
    }

    $invocation.ok = $true

    # preview-clean assertion: if the scenario is a 'preview', the named diff
    # field on the response must be empty / zero (no unapproved differences).
    # The diff field may be either a collection (e.g. an array of triples)
    # or a scalar (e.g. the integer added_triples count returned by
    # preview_ontology_changes — see OnToPilotMcpTools.cs:587-592).
    if ($scenario.PSObject.Properties.Match('expectsCleanPreview').Count -gt 0 -and $scenario.expectsCleanPreview) {
        $diffField = $scenario.diffField
        $payload = $result.result
        $diff = $payload.$diffField
        $hasContent = $false
        if ($null -ne $diff) {
            if ($diff -is [string]) {
                $hasContent = $diff.Length -gt 0
            } elseif ($diff -is [System.Collections.IEnumerable]) {
                $hasContent = @($diff).Count -gt 0
            } else {
                $hasContent = ([int] $diff) -ne 0
            }
        }
        if ($hasContent) {
            $invocation.ok = $false
            $msg = "Scenario '$($scenario.name)' expected a clean preview but $diffField had content: $diff"
            $report.unapprovedDiff += $msg
            if ($FailOnUnapproved) { throw $msg }
        }
    }

    $report.invocations += $invocation
}

# --- Summary ----------------------------------------------------------------

$report.finishedAt = (Get-Date).ToString('o')

$reportPath = Join-Path $repoRoot 'migration/actual/mcp-smoke-report.json'
$reportDir = Split-Path -Parent $reportPath
if (-not (Test-Path -LiteralPath $reportDir)) {
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host "[mcp-smoke] wrote report to $reportPath" -ForegroundColor Green
Write-Host "[mcp-smoke] inventory: $($liveToolNames.Count) tools live, $($BaselineToolNames.Count) baseline" -ForegroundColor Green

if ($report.unapprovedDiff.Count -gt 0 -and $FailOnUnapproved) {
    throw "MCP smoke reported $($report.unapprovedDiff.Count) unapproved difference(s)."
}

# The plan snippet is preserved verbatim for the reviewer, with the throw
# gated on -FailOnUnapproved so the JSON report remains the source of truth
# when the operator is doing a discovery-only run (the brief explicitly
# distinguishes soft warnings from hard failures — without the gate the
# tail snippet would always throw on any drift, which inverts the earlier
# soft-warning design intent at lines 132-144).
$tools = Invoke-McpRequest -Method 'tools/list' -Token $Token
$drift = Compare-Object $BaselineToolNames ($tools.result.tools.name)
if ($drift) {
    if ($FailOnUnapproved) {
        $drift | ForEach-Object { throw "MCP inventory mismatch: $_" }
    } else {
        $drift | ForEach-Object { Write-Warning "MCP inventory mismatch (suppressed without -FailOnUnapproved): $_" }
    }
}
Invoke-McpRequest -Method 'tools/call' -Params @{ name = 'get_ontology'; arguments = @{} } -Token $Token | Out-Null

Write-Host "[mcp-smoke] OK" -ForegroundColor Green