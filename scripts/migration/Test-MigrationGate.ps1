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
    $actual = @($data.Steps | ForEach-Object { $_.Name })
    $actualKey = ($actual | Sort-Object -Unique) -join '|'
    $requiredKey = ($required | Sort-Object -Unique) -join '|'
    if ($actualKey -ne $requiredKey) {
        throw 'Manifest step names do not exactly match the migration gate contract.'
    }
    foreach ($step in $data.Steps) {
        if ($step.Status -ne 'passed') { throw "Step '$($step.Name)' is not passed." }
    }
    Write-Output "Migration gate passed: $Manifest"
    exit 0
}
catch {
    [Console]::Error.WriteLine("Migration gate failed: $($_.Exception.Message)")
    exit 1
}