# Test-ContainerHealth.ps1
#
# Poll the production .NET container's /api/health endpoint until it
# returns 200 OK, or fail after a configurable deadline. Designed to
# run AFTER `docker compose up -d --build postgres minio backend frontend`
# so the sidecars (PostgreSQL, MinIO) are already warm by the time the
# backend boot reaches its EF Core migrate + MinIO handshake phase.
#
# Usage:
#   pwsh migration/scripts/Test-ContainerHealth.ps1
#   pwsh migration/scripts/Test-ContainerHealth.ps1 -BaseUrl http://localhost:8080 -TimeoutSeconds 120 -IntervalSeconds 1
#
# Exit codes:
#   0  - /api/health returned 200 within the deadline.
#   1  - timeout reached without a healthy response.
#   2  - PowerShell version too old (requires 7+).

[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:8080",
    [int]$TimeoutSeconds = 120,
    [int]$IntervalSeconds = 1,
    [string]$HealthPath = "/api/health"
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "Test-ContainerHealth.ps1 requires PowerShell 7+ (found $($PSVersionTable.PSVersion))."
    exit 2
}

# Normalise the trailing slash so a caller passing "...8080" and a caller
# passing "...8080/" both produce the same URL.
$BaseUrl = $BaseUrl.TrimEnd("/")
$Target = "$BaseUrl$HealthPath"

Write-Host "[Test-ContainerHealth] Polling $Target every ${IntervalSeconds}s (timeout=${TimeoutSeconds}s)"

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$attempt = 0
$lastStatus = $null
$lastError = $null

while ((Get-Date) -lt $deadline) {
    $attempt++
    try {
        # Use HttpClient via .NET so we don't pay the WebCmdlet startup
        # tax on every retry. The default request timeout is generous
        # because the first request to a cold .NET host can take 10+
        # seconds during EF Core schema validation.
        $response = Invoke-WebRequest -Uri $Target -Method Get -TimeoutSec 10 -SkipHttpErrorCheck -ErrorAction Stop
        $lastStatus = $response.StatusCode
        if ($response.StatusCode -eq 200) {
            $body = $response.Content
            Write-Host "[Test-ContainerHealth] Healthy after $attempt attempt(s) ($([math]::Round(((Get-Date) - $deadline.AddSeconds(-$TimeoutSeconds)).TotalSeconds, 1))s)."
            Write-Host "[Test-ContainerHealth] Response body: $body"
            exit 0
        }
        Write-Host "[Test-ContainerHealth] attempt=$attempt status=$($response.StatusCode) (continuing)"
    }
    catch {
        $lastError = $_.Exception.Message
        Write-Host "[Test-ContainerHealth] attempt=$attempt error=$lastError (continuing)"
    }
    Start-Sleep -Seconds $IntervalSeconds
}

Write-Error "[Test-ContainerHealth] FAILED: $Target did not return 200 within ${TimeoutSeconds}s. last_status=$lastStatus last_error=$lastError"
exit 1
