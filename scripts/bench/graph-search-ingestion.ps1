[CmdletBinding()]
param(
    [Alias('Baseline')]
    [string]$BaselinePath = (Join-Path $PSScriptRoot '..\..\docs\migration\graph-search-ingestion-baseline.json'),
    [string]$Output = (Join-Path $PSScriptRoot '..\..\.artifacts\graph-search-ingestion.json'),
    [switch]$Record,
    [string]$TestProject = (Join-Path $PSScriptRoot '..\..\src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj')
)

$ErrorActionPreference = 'Stop'
$fixturePath = Join-Path $PSScriptRoot '..\..\src\ISEStudio.IntegrationTests\Benchmarks\Fixtures\graph-search-ingestion.v1.json'
$temporaryResults = Join-Path ([IO.Path]::GetTempPath()) ("isestudio-benchmarks-" + [Guid]::NewGuid().ToString('N'))
$oldOutputDirectory = $env:ISESTUDIO_BENCHMARK_OUTPUT_DIR
$oldRecordMode = $env:ISESTUDIO_BENCHMARK_RECORD

function Write-DeterministicJson {
    param([object]$Value, [string]$Path)

    $parent = Split-Path -Parent $Path
    if ($parent) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $Value | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 -Path $Path
}

try {
    New-Item -ItemType Directory -Force -Path $temporaryResults | Out-Null
    $env:ISESTUDIO_BENCHMARK_OUTPUT_DIR = $temporaryResults
    if ($Record) {
        $env:ISESTUDIO_BENCHMARK_RECORD = '1'
    }
    else {
        Remove-Item Env:ISESTUDIO_BENCHMARK_RECORD -ErrorAction SilentlyContinue
    }

    & dotnet test $TestProject --filter 'FullyQualifiedName~Benchmarks' --no-restore --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) {
        exit 1
    }

    $resultFiles = @(Get-ChildItem -Path $temporaryResults -Filter '*.json' -File | Sort-Object Name)
    if ($resultFiles.Count -ne 4) {
        Write-Error "Expected four benchmark result files, found $($resultFiles.Count)."
        exit 1
    }

    $fixture = Get-Content -Raw -Path $fixturePath | ConvertFrom-Json
    $metrics = @($resultFiles | ForEach-Object { Get-Content -Raw -Path $_.FullName | ConvertFrom-Json } | Sort-Object Name)
    $expectedNames = @('concurrent_graph_writes', 'graph_traversal', 'ingestion', 'search')
    $actualNames = @($metrics | ForEach-Object { [string]$_.Name })
    if ((Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames).Count -ne 0) {
        Write-Error 'Benchmark result names do not match the required contract.'
        exit 1
    }

    $tolerance = [ordered]@{
        p95_multiplier = 2.0
        throughput_floor = 0.5
    }
    $baselineMetrics = @()

    if ($Record) {
        foreach ($metric in $metrics) {
            $fixture.expected_result_hashes | Add-Member -MemberType NoteProperty -Name $metric.Name -Value $metric.result_hash -Force
            $baselineMetrics += [ordered]@{
                name = $metric.Name
                p50_ms = [double]$metric.p50_ms
                p95_ms = [double]$metric.p95_ms
                throughput_per_second = [double]$metric.throughput_per_second
                error_count = [int]$metric.error_count
                result_hash = $metric.result_hash
            }
        }

        Write-DeterministicJson $fixture $fixturePath
        Write-DeterministicJson ([ordered]@{
            version = 1
            fixture = 'graph-search-ingestion.v1.json'
            source = 'dotnet Testcontainers PostgreSQL 16'
            tolerance = $tolerance
            benchmarks = $baselineMetrics
        }) $BaselinePath
    }
    else {
        if (-not (Test-Path -LiteralPath $BaselinePath)) {
            Write-Error "Baseline is missing: $BaselinePath. Run with -Record to create it."
            exit 1
        }

        $baselineDocument = Get-Content -Raw -Path $BaselinePath | ConvertFrom-Json
        $baselineVersion = [int]$baselineDocument.version
        $baselineBenchmarks = @($baselineDocument.benchmarks)
        if ($baselineVersion -ne 1 -or $baselineBenchmarks.Count -eq 0) {
            Write-Error 'Baseline version or benchmark list is invalid.'
            exit 1
        }

        if ($null -ne $baselineDocument.tolerance) {
            $tolerance = [ordered]@{
            p95_multiplier = [double]$baselineDocument.tolerance.p95_multiplier
            throughput_floor = [double]$baselineDocument.tolerance.throughput_floor
            }
        }

        foreach ($metric in $metrics) {
            if ([int]$metric.error_count -ne 0) {
                Write-Error "Benchmark $($metric.Name) reported errors."
                exit 1
            }

            $expectedHash = $fixture.expected_result_hashes.($metric.Name)
            if ([string]::IsNullOrWhiteSpace($expectedHash) -or $metric.result_hash -cne $expectedHash) {
                Write-Error "Benchmark $($metric.Name) result hash does not match the checked-in fixture."
                exit 1
            }

            $recorded = @($baselineBenchmarks | Where-Object Name -ceq $metric.Name)
            if ($recorded.Count -ne 1) {
                Write-Error "Baseline entry for $($metric.Name) is missing or duplicated."
                exit 1
            }

            if ($metric.result_hash -cne $recorded[0].result_hash) {
                Write-Error "Benchmark $($metric.Name) result hash does not match the baseline."
                exit 1
            }

            if ([double]$metric.p95_ms -gt ([double]$recorded[0].p95_ms * $tolerance.p95_multiplier)) {
                Write-Error "Benchmark $($metric.Name) p95 exceeds the declared tolerance."
                exit 1
            }

            if ([double]$metric.throughput_per_second -lt ([double]$recorded[0].throughput_per_second * $tolerance.throughput_floor)) {
                Write-Error "Benchmark $($metric.Name) throughput is below the declared tolerance."
                exit 1
            }
        }

        $baselineMetrics = $metrics | ForEach-Object {
            [ordered]@{
                name = $_.Name
                p50_ms = [double]$_.p50_ms
                p95_ms = [double]$_.p95_ms
                throughput_per_second = [double]$_.throughput_per_second
                error_count = [int]$_.error_count
                result_hash = $_.result_hash
            }
        }
    }

    Write-DeterministicJson ([ordered]@{
        version = 1
        fixture = 'graph-search-ingestion.v1.json'
        source = 'dotnet Testcontainers PostgreSQL 16'
        tolerance = $tolerance
        benchmarks = @($baselineMetrics)
    }) $Output
}
finally {
    Remove-Item -Recurse -Force -Path $temporaryResults -ErrorAction SilentlyContinue
    if ($null -eq $oldOutputDirectory) { Remove-Item Env:ISESTUDIO_BENCHMARK_OUTPUT_DIR -ErrorAction SilentlyContinue } else { $env:ISESTUDIO_BENCHMARK_OUTPUT_DIR = $oldOutputDirectory }
    if ($null -eq $oldRecordMode) { Remove-Item Env:ISESTUDIO_BENCHMARK_RECORD -ErrorAction SilentlyContinue } else { $env:ISESTUDIO_BENCHMARK_RECORD = $oldRecordMode }
}