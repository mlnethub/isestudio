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

function Get-Sha256Hex([string]$Value) {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))).Replace('-', ''))
    }
    finally {
        $sha256.Dispose()
    }
}

try {
    if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
        throw "Manifest not found: $Manifest"
    }
    $raw = Get-Content -LiteralPath $Manifest -Raw
    $data = $raw | ConvertFrom-Json
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

    $manifestChecksum = [string]$data.ManifestChecksum
    if ([string]::IsNullOrWhiteSpace($manifestChecksum)) {
        throw 'ManifestChecksum is required.'
    }
    if ($raw -notmatch ',"ManifestChecksum":"[0-9A-F]+"}') {
        throw 'ManifestChecksum must be the final JSON property emitted by the rehearsal serializer.'
    }
    $withoutManifestChecksum = [regex]::Replace($raw, ',"ManifestChecksum":"[0-9A-F]+"}', '}', 1)
    if ((Get-Sha256Hex $withoutManifestChecksum) -cne $manifestChecksum.ToUpperInvariant()) {
        throw 'ManifestChecksum does not match the final JSON payload.'
    }

    $reportStep = $steps | Where-Object { [string]$_.Name -ceq 'report-serialization' }
    $withoutReportChecksum = [regex]::Replace(
        $raw,
        '(?s)("Name":"report-serialization".*?"Checksum":)"[0-9A-F]+"',
        '${1}""',
        1)
    if ($withoutReportChecksum -eq $raw) {
        throw 'report-serialization checksum field was not found in canonical JSON.'
    }
    $withoutReportChecksum = [regex]::Replace($withoutReportChecksum, ',"ManifestChecksum":"[0-9A-F]+"}', '}', 1)
    if ((Get-Sha256Hex $withoutReportChecksum) -cne ([string]$reportStep.Checksum).ToUpperInvariant()) {
        throw 'report-serialization checksum does not match the final JSON payload.'
    }
    Write-Output "Migration gate passed: $Manifest"
    exit 0
}
catch {
    [Console]::Error.WriteLine("Migration gate failed: $($_.Exception.Message)")
    exit 1
}