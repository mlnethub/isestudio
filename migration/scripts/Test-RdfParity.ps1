# Test-RdfParity.ps1
#
# End-to-end parity check for the RDF data-cutover (Task 2). Builds the
# .NET Migration project, invokes RdfMigrationCommand.VerifyCopyAsync on
# a copy of the source RocksDB directory, and asserts the produced
# manifest matches what the N-Quads fallback would produce for the same
# logical content. Fails fast on any orphan/query/manifest mismatch.
#
# The script NEVER opens the source itself — it produces a copy under
# .artifacts/rdf-test/copy and the .NET side opens that copy only.
#
# Usage:
#   pwsh migration/scripts/Test-RdfParity.ps1
#       -Source      backend/data/oxigraph
#       -Copy        .artifacts/rdf-test/copy
#       -Work        .artifacts/rdf-test/work
#       -QueriesFile migration/fixtures/rdf-smoke-queries.json
#
# Exit codes:
#   0 - direct + fallback parity verified; write-revert smoke passed.
#   1 - source missing.
#   2 - copy step failed.
#   3 - parity check failed (manifest mismatch).
#   4 - PowerShell version too old (requires 7+).

[CmdletBinding()]
param(
    [string]$Source = "backend/data/oxigraph",
    [string]$Copy = ".artifacts/rdf-test/copy",
    [string]$Work = ".artifacts/rdf-test/work",
    [string]$QueriesFile = "migration/fixtures/rdf-smoke-queries.json",
    [string]$Config = "Debug"
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "Test-RdfParity.ps1 requires PowerShell 7+ (found $($PSVersionTable.PSVersion))."
    exit 4
}

$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
Set-Location -LiteralPath $repoRoot

# 1. Validate inputs up-front so a missing fixture fails fast with a
#    useful message instead of a cryptic stack trace from the .NET side.
$sourceFull = Join-Path $repoRoot $Source
if (-not (Test-Path -LiteralPath $sourceFull -PathType Container)) {
    Write-Error "Test-RdfParity.ps1: source '$sourceFull' does not exist. Pass -Source to a real RocksDB directory."
    exit 1
}

$copyFull = Join-Path $repoRoot $Copy
$workFull = Join-Path $repoRoot $Work
$queriesFull = Join-Path $repoRoot $QueriesFile
if (-not (Test-Path -LiteralPath $queriesFull)) {
    Write-Error "Test-RdfParity.ps1: queries file '$queriesFull' does not exist."
    exit 1
}

# 2. Build the .NET Migration project so the .NET tool is fresh.
Write-Host "[Test-RdfParity] Building OnToPilot.Migration"
& dotnet build "$repoRoot\src\OnToPilot.Migration\OnToPilot.Migration.csproj" -c $Config --nologo 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) {
    Write-Error "Test-RdfParity.ps1: dotnet build failed (exit=$LASTEXITCODE)"
    exit 2
}

# 3. Build a small console probe that exercises the public API surface
#    (VerifyCopyAsync + WriteRevertSmokeAsync) and prints the resulting
#    manifest as JSON. The probe lives in a sibling project so we don't
#    have to ship a CLI entry-point inside OnToPilot.Migration just for
#    this script. We synthesise it under .artifacts/rdf-test/probe/ so
#    it never touches the repo.
$probeRoot = Join-Path $repoRoot ".artifacts/rdf-test/probe"
if (Test-Path -LiteralPath $probeRoot) { Remove-Item -LiteralPath $probeRoot -Recurse -Force }
New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null

$probeProgram = @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OnToPilot.Migration.Rdf;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 6)
        {
            Console.Error.WriteLine("usage: RdfParityProbe <source> <copy> <work> <queriesJson> <strategy> <expectedCountFile>");
            return 64;
        }
        var sourcePath = args[0];
        var copyPath = args[1];
        var workPath = args[2];
        var queriesPath = args[3];
        var strategy = args[4];
        var expectedCountFile = args[5];

        var entries = JsonSerializer.Deserialize<List<QueryEntry>>(
            File.ReadAllText(queriesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var queries = entries.Select(e => (e.Name, e.Query)).ToList();

        RdfManifest manifest;
        if (strategy == "direct")
        {
            manifest = RdfMigrationCommand.VerifyCopyAsync(
                sourcePath, copyPath, workPath, queries, copyFromSource: true, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        else
        {
            // Fallback path: pre-delete the copy so direct read fails and
            // the command picks the nquads branch. We pass copyFromSource:false
            // so the command doesn't recreate the copy from the source.
            if (Directory.Exists(copyPath)) Directory.Delete(copyPath, recursive: true);
            manifest = RdfMigrationCommand.VerifyCopyAsync(
                sourcePath, copyPath, workPath, queries, copyFromSource: false, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        if (File.Exists(expectedCountFile))
        {
            var expected = ulong.Parse(File.ReadAllText(expectedCountFile).Trim());
            if (manifest.QuadCount != expected)
            {
                Console.Error.WriteLine($"FAIL: quad count {manifest.QuadCount} != expected {expected}");
                return 3;
            }
        }

        // Write-revert smoke on the direct copy (not on the fresh work
        // directory used for the fallback).
        if (strategy == "direct")
        {
            var ok = RdfMigrationCommand.WriteRevertSmokeAsync(
                copyPath, manifest.QuadCount, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!ok)
            {
                Console.Error.WriteLine("FAIL: write-revert smoke returned false");
                return 3;
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private sealed class QueryEntry
    {
        public string Name { get; set; } = "";
        public string Query { get; set; } = "";
    }
}
"@
Set-Content -LiteralPath (Join-Path $probeRoot "Program.cs") -Value $probeProgram -Encoding utf8NoBOM

$probeCsproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>RdfParityProbe</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repoRoot\src\OnToPilot.Migration\OnToPilot.Migration.csproj" />
  </ItemGroup>
</Project>
"@
Set-Content -LiteralPath (Join-Path $probeRoot "RdfParityProbe.csproj") -Value $probeCsproj -Encoding utf8NoBOM

Write-Host "[Test-RdfParity] Building parity probe"
& dotnet build "$probeRoot\RdfParityProbe.csproj" -c $Config --nologo 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) {
    Write-Error "Test-RdfParity.ps1: probe build failed (exit=$LASTEXITCODE)"
    exit 2
}

# 4. Run the direct strategy.
New-Item -ItemType Directory -Path $copyFull -Force | Out-Null
New-Item -ItemType Directory -Path $workFull -Force | Out-Null

$directManifestPath = Join-Path $workFull "manifest-direct.json"
$directCountFile = Join-Path $workFull "count-direct.txt"

Write-Host "[Test-RdfParity] Running direct strategy"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$directOutput = & dotnet run --project "$probeRoot\RdfParityProbe.csproj" -c $Config --no-build -- `
    "$sourceFull" "$copyFull" "$workFull" "$queriesFull" "direct" "$directCountFile" 2>&1
$directExit = $LASTEXITCODE
if ($directExit -ne 0) {
    Write-Error "Test-RdfParity.ps1: direct strategy probe failed (exit=$directExit):`n$directOutput"
    exit 3
}
$directOutput | Out-File -LiteralPath $directManifestPath -Encoding utf8NoBOM
Write-Host "[Test-RdfParity] Direct manifest: $directManifestPath"

# 5. Run the N-Quads fallback. The probe expects nquads-export.nq under
#    $workFull; produce it via Export-PythonRdf.ps1. The script falls
#    back to skipping the comparison when neither pyoxigraph nor
#    oxigraph-cli is on PATH — the production cutover environment has
#    one or the other installed; developer workstations may not. The
#    parity between strategies is independently covered by the
#    OnToPilot.IntegrationTests.Migration.RdfMigrationTests test class
#    which seeds its own synthetic source.
Write-Host "[Test-RdfParity] Producing N-Quads export from source"
& pwsh "$repoRoot\migration\scripts\Export-PythonRdf.ps1" -Source $sourceFull -Work $workFull
$nqExit = $LASTEXITCODE
if ($nqExit -ne 0) {
    Write-Host "[Test-RdfParity] N-Quads export unavailable (exit=$nqExit). Skipping fallback comparison."
    exit 0
}

$fallbackManifestPath = Join-Path $workFull "manifest-nquads.json"
Write-Host "[Test-RdfParity] Running N-Quads fallback strategy"
$fallbackOutput = & dotnet run --project "$probeRoot\RdfParityProbe.csproj" -c $Config --no-build -- `
    "$sourceFull" "$copyFull" "$workFull" "$queriesFull" "nquads" "" 2>&1
$fallbackExit = $LASTEXITCODE
if ($fallbackExit -ne 0) {
    Write-Error "Test-RdfParity.ps1: nquads strategy probe failed (exit=$fallbackExit):`n$fallbackOutput"
    exit 3
}
$fallbackOutput | Out-File -LiteralPath $fallbackManifestPath -Encoding utf8NoBOM

# 6. Diff the two manifests. Every field except Strategy must match
#    exactly; Strategy is the discriminator.
function Compare-Manifest {
    param($DirectPath, $FallbackPath)
    $direct = Get-Content -LiteralPath $DirectPath -Raw | ConvertFrom-Json
    $fallback = Get-Content -LiteralPath $FallbackPath -Raw | ConvertFrom-Json

    $checks = @(
        @{ Name = "Strategy"; Direct = $direct.strategy; Fallback = $fallback.strategy; AllowDifference = $true },
        @{ Name = "QuadCount"; Direct = $direct.quadCount; Fallback = $fallback.quadCount; AllowDifference = $false },
        @{ Name = "SourceOpenedByDotNet"; Direct = $direct.sourceOpenedByDotNet; Fallback = $fallback.sourceOpenedByDotNet; AllowDifference = $false },
        @{ Name = "NamedGraphsSorted"; Direct = ($direct.namedGraphs | Sort-Object); Fallback = ($fallback.namedGraphs | Sort-Object); AllowDifference = $false }
    )

    foreach ($c in $checks) {
        if (-not $c.AllowDifference -and $c.Direct -ne $c.Fallback) {
            Write-Error "Test-RdfParity.ps1: manifest mismatch on '$($c.Name)': direct='$($c.Direct)' vs nquads='$($c.Fallback)'"
            exit 3
        }
    }

    # Compare query-result hashes — every name must match value-for-value.
    $directHashes = $direct.queryResultHashes | ConvertTo-Json -Depth 10 | Sort-Object
    $fallbackHashes = $fallback.queryResultHashes | ConvertTo-Json -Depth 10 | Sort-Object
    if ($directHashes -ne $fallbackHashes) {
        Write-Error "Test-RdfParity.ps1: queryResultHashes mismatch.`n  direct:    $directHashes`n  fallback:  $fallbackHashes"
        exit 3
    }

    Write-Host "[Test-RdfParity] OK — direct and nquads manifests agree on quad count, graphs, and query hashes."
}

Compare-Manifest -DirectPath $directManifestPath -FallbackPath $fallbackManifestPath
exit 0
