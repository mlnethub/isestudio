[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Manifest
)

$ErrorActionPreference = 'Stop'
$required = @(
    'database-connectivity', 'ef-migrations', 'schema-assertions', 'sql-snapshot',
    'rdf-copy', 'blob-manifest', 'graph-read-only', 'report-serialization'
)

try {
    if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
        throw "Manifest not found: $Manifest"
    }
    $data = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
    if ($data.Passed -ne $true) { throw 'Manifest Passed must be true.' }
    $steps = @($data.Steps)
    if ($steps.Count -ne $required.Count) {
        throw "Manifest must contain exactly $($required.Count) steps."
    }
    if ($steps | Where-Object { $null -eq $_ -or [string]::IsNullOrWhiteSpace([string]$_.Name) }) {
        throw 'Manifest contains an empty step.'
    }
    $actual = @($steps | ForEach-Object { [string]$_.Name })
    if (($actual | Select-Object -Unique).Count -ne $actual.Count) {
        throw 'Manifest contains duplicate steps.'
    }
    $unknown = @($actual | Where-Object { $_ -notin $required })
    if ($unknown.Count -gt 0) {
        throw "Manifest contains unknown step(s): $($unknown -join ', ')."
    }
    $actualKey = ($actual | Sort-Object) -join '|'
    $requiredKey = ($required | Sort-Object) -join '|'
    if ($actualKey -ne $requiredKey) {
        throw 'Manifest step names do not exactly match the migration gate contract.'
    }
    foreach ($step in $steps) {
        if ([string]$step.Status -cne 'passed') { throw "Step '$($step.Name)' is not passed." }
    }
    Write-Output "Migration gate passed: $Manifest"
    exit 0
}
catch {
    [Console]::Error.WriteLine("Migration gate failed: $($_.Exception.Message)")
    exit 1
}