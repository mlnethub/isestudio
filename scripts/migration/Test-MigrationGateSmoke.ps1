[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256Hex([string]$Value) {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))).Replace('-', ''))
    }
    finally {
        $sha256.Dispose()
    }
}

function New-ManifestJson {
    $steps = @(
        [ordered]@{ Name = 'database-connectivity'; Status = 'passed'; Rows = 0; Checksum = 'db'; Detail = 'fixture'; StartedAt = '2026-09-07T00:00:00+00:00'; CompletedAt = '2026-09-07T00:00:00+00:00' },
        [ordered]@{ Name = 'ef-migrations'; Status = 'passed'; Rows = 0; Checksum = 'ef'; Detail = 'fixture'; StartedAt = '2026-09-07T00:00:00+00:00'; CompletedAt = '2026-09-07T00:00:00+00:00' },
        [ordered]@{ Name = 'schema-assertions'; Status = 'passed'; Rows = 0; Checksum = 'schema'; Detail = 'fixture'; StartedAt = '2026-09-07T00:00:00+00:00'; CompletedAt = '2026-09-07T00:00:00+00:00' },
        [ordered]@{ Name = 'sql-snapshot'; Status = 'passed'; Rows = 0; Checksum = 'sql'; Detail = 'fixture'; StartedAt = '2026-09-07T00:00:00+00:00'; CompletedAt = '2026-09-07T00:00:00+00:00' },
        [ordered]@{ Name = 'rdf-copy'; Status = 'passed'; Rows = 0; Checksum = 'rdf'; Detail = 'fixture'; StartedAt = '2026-09-07T00:00:00+00:00'; CompletedAt = '2026-09-07T00:00:00+00:00' },
        [ordered]@{ Name = 'blob-manifest'; Status = 'passed'; Rows = 0; Checksum = 'blob'; Detail = 'fixture'; StartedAt = '2026-09-07T00:00:00+00:00'; CompletedAt = '2026-09-07T00:00:00+00:00' },
        [ordered]@{ Name = 'graph-read-only'; Status = 'passed'; Rows = 0; Checksum = 'graph'; Detail = 'fixture'; StartedAt = '2026-09-07T00:00:00+00:00'; CompletedAt = '2026-09-07T00:00:00+00:00' },
        [ordered]@{ Name = 'report-serialization'; Status = 'passed'; Rows = 0; Checksum = ''; Detail = 'fixture'; StartedAt = '2026-09-07T00:00:00+00:00'; CompletedAt = '2026-09-07T00:00:00+00:00' }
    )
    $payload = [ordered]@{
        RunId = 'smoke-run'
        DatabaseMode = 'fresh'
        Steps = $steps
        Passed = $true
        CompletedAt = '2026-09-07T00:00:00+00:00'
    }

    $withoutReportChecksum = $payload | ConvertTo-Json -Compress -Depth 10
    $reportChecksum = Get-Sha256Hex $withoutReportChecksum
    $steps[7].Checksum = $reportChecksum
    $withoutManifestChecksum = $payload | ConvertTo-Json -Compress -Depth 10
    $manifestChecksum = Get-Sha256Hex $withoutManifestChecksum
    return $withoutManifestChecksum.TrimEnd('}') + ',"ManifestChecksum":"' + $manifestChecksum + '"}'
}

function Invoke-Gate([string]$ManifestPath) {
    & (Join-Path $PSScriptRoot 'Test-MigrationGate.ps1') -Manifest $ManifestPath *> $null
    return $LASTEXITCODE
}

$tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('migration-gate-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDirectory | Out-Null
$manifestPath = Join-Path $tempDirectory 'manifest.json'
try {
    $validJson = New-ManifestJson
    Set-Content -LiteralPath $manifestPath -Value $validJson -NoNewline -Encoding utf8
    if ((Invoke-Gate $manifestPath) -ne 0) {
        throw 'Valid manifest was rejected by Test-MigrationGate.ps1.'
    }

    $tamperedPassed = $validJson.Replace('"Passed":true', '"Passed":false', [System.StringComparison]::Ordinal)
    Set-Content -LiteralPath $manifestPath -Value $tamperedPassed -NoNewline -Encoding utf8
    if ((Invoke-Gate $manifestPath) -ne 1) {
        throw 'Manifest with tampered Passed flag was not rejected.'
    }

    $tamperedReport = $validJson -replace '("Name":"report-serialization".*?"Checksum":)"[0-9A-F]+"', '${1}"BAD"'
    Set-Content -LiteralPath $manifestPath -Value $tamperedReport -NoNewline -Encoding utf8
    if ((Invoke-Gate $manifestPath) -ne 1) {
        throw 'Manifest with tampered report checksum was not rejected.'
    }

    $tamperedMetadata = $validJson.Replace('"Detail":"fixture"', '"Detail":"tampered"', [System.StringComparison]::Ordinal)
    Set-Content -LiteralPath $manifestPath -Value $tamperedMetadata -NoNewline -Encoding utf8
    if ((Invoke-Gate $manifestPath) -ne 1) {
        throw 'Manifest with tampered step metadata was not rejected.'
    }

    Write-Output 'Migration gate smoke passed: valid manifest accepted and 3 tampered manifests rejected.'
    exit 0
}
finally {
    Remove-Item -LiteralPath $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
