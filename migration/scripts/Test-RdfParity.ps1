# Test-RdfParity.ps1
#
# End-to-end parity check for the RDF data-cutover (Task 2). Builds the
# .NET Migration project, invokes RdfMigrationCommand.VerifyCopyAsync on
# a copy of the source RocksDB directory, and asserts the produced
# report matches what the N-Quads fallback would produce for the same
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
#       [-NoStrict]  pass to allow the dev-workstation skip-with-warning
#                    path when pyoxigraph / oxigraph-cli is unavailable.
#                    Default is strict (exit 5 on skip) — the production
#                    cutover pipeline relies on this; only developer
#                    workstations without Python tooling should pass
#                    -NoStrict.
#
# Exit codes:
#   0 - direct + fallback parity verified; write-revert smoke passed.
#     - or: direct strategy succeeded but the N-Quads fallback was
#       skipped because pyoxigraph / oxigraph-cli is unavailable AND
#       `-NoStrict` was passed (a stderr WARN is still emitted).
#   1 - source missing.
#   2 - copy step failed.
#   3 - parity check failed (manifest mismatch).
#   4 - PowerShell version too old (requires 7+).
#   5 - N-Quads fallback unavailable in default strict mode (the
#       parity gate cannot be considered verified; production cutover
#       treats this as a hard failure).

[CmdletBinding()]
param(
    [string]$Source = "backend/data/oxigraph",
    [string]$Copy = ".artifacts/rdf-test/copy",
    [string]$Work = ".artifacts/rdf-test/work",
    [string]$QueriesFile = "migration/fixtures/rdf-smoke-queries.json",
    [string]$Config = "Debug",
    # Default $true for production-cutover invocations; pass `-NoStrict`
    # to allow the dev-workstation skip-with-warning path when
    # pyoxigraph / oxigraph-cli is unavailable. Using a negative-flag
    # switch (default $false) rather than `-Strict:$false` because
    # PowerShell's `-Switch:$false` colon-syntax does not reliably
    # override a `$true` default for `[bool]` parameters.
    [switch]$NoStrict
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
#    RdfMigrationResult (Report + Audit) as JSON. The probe lives in a
#    sibling project so we don't have to ship a CLI entry-point inside
#    OnToPilot.Migration just for this script. We synthesise it under
#    .artifacts/rdf-test/probe/ so it never touches the repo.
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

        RdfMigrationResult result;
        if (strategy == "direct")
        {
            result = RdfMigrationCommand.VerifyCopyAsync(
                sourcePath, copyPath, workPath, queries, copyFromSource: true, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        else
        {
            // Fallback path: pre-delete the copy so direct read fails and
            // the command picks the nquads branch. We pass copyFromSource:false
            // so the command doesn't recreate the copy from the source.
            if (Directory.Exists(copyPath)) Directory.Delete(copyPath, recursive: true);
            result = RdfMigrationCommand.VerifyCopyAsync(
                sourcePath, copyPath, workPath, queries, copyFromSource: false, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        if (File.Exists(expectedCountFile))
        {
            var expected = ulong.Parse(File.ReadAllText(expectedCountFile).Trim());
            if (result.Report.QuadCount != expected)
            {
                Console.Error.WriteLine($"FAIL: quad count {result.Report.QuadCount} != expected {expected}");
                return 3;
            }
        }

        // Write-revert smoke on the direct copy (not on the fresh work
        // directory used for the fallback). The smoke round-trip is
        // expected to pass with cleanupSucceeded=true; if either trips,
        // the parity gate fails.
        if (strategy == "direct")
        {
            var post = RdfMigrationCommand.WriteRevertSmokeAsync(
                result, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!post.Report.WriteRevertPassed)
            {
                Console.Error.WriteLine("FAIL: write-revert smoke returned WriteRevertPassed=false");
                return 3;
            }
            if (!post.Audit.CleanupSucceeded)
            {
                Console.Error.WriteLine("FAIL: write-revert smoke cleanup failed (probe graph residue)");
                return 3;
            }
            result = post;
        }

        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
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
#    $workFull; produce it via Export-PythonRdf.ps1. When neither
#    pyoxigraph nor oxigraph-cli is on PATH the parity gate cannot be
#    considered verified — Strict mode (default) fails with exit 5 so CI
#    catches the skip; non-Strict mode prints a loud stderr warning and
#    exits 0 for developer-workstation convenience.
Write-Host "[Test-RdfParity] Producing N-Quads export from source"
& pwsh "$repoRoot\migration\scripts\Export-PythonRdf.ps1" -Source $sourceFull -Work $workFull
$nqExit = $LASTEXITCODE
if ($nqExit -ne 0) {
    $skipMsg = "[Test-RdfParity] N-Quads export unavailable (exit=$nqExit); Export-PythonRdf.ps1 could not invoke pyoxigraph or oxigraph-cli. Parity comparison skipped."
    if (-not $NoStrict) {
        # Default (strict): fail loudly so CI catches the skip.
        # Write-Host goes to the host output stream; [Console]::Error.WriteLine
        # goes to the error stream so CI log scrapers can detect the skip.
        [Console]::Error.WriteLine("FAIL: $skipMsg")
        [Console]::Error.WriteLine("FAIL: Test-RdfParity.ps1 is in strict mode (default for production-cutover invocations); pass -NoStrict to allow the skip-with-warning path.")
        exit 5
    }
    # Non-strict: emit to stderr so log scrapers see it, but exit 0.
    [Console]::Error.WriteLine("WARN: $skipMsg")
    Write-Host "[Test-RdfParity] N-Quads export unavailable (exit=$nqExit). Skipping fallback comparison (-NoStrict). Skipped parity is NOT verified."
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

# 6. Diff the two results. Every relevant field must match exactly;
#    Strategy is the discriminator (always "direct" vs "nquads").
#    Each result has a Report (5 brief fields) + Audit (sibling) shape.
function Compare-Manifest {
    param($DirectPath, $FallbackPath)
    $direct = Get-Content -LiteralPath $DirectPath -Raw | ConvertFrom-Json
    $fallback = Get-Content -LiteralPath $FallbackPath -Raw | ConvertFrom-Json

    # Report-level checks (the brief's 5-field record).
    $reportChecks = @(
        @{ Name = "Strategy"; Direct = $direct.report.strategy; Fallback = $fallback.report.strategy; AllowDifference = $true },
        @{ Name = "QuadCount"; Direct = $direct.report.quadCount; Fallback = $fallback.report.quadCount; AllowDifference = $false },
        @{ Name = "WriteRevertPassed"; Direct = $direct.report.writeRevertPassed; Fallback = $fallback.report.writeRevertPassed; AllowDifference = $false },
        @{ Name = "NamedGraphsSorted"; Direct = ($direct.report.namedGraphs | Sort-Object); Fallback = ($fallback.report.namedGraphs | Sort-Object); AllowDifference = $false }
    )

    foreach ($c in $reportChecks) {
        if (-not $c.AllowDifference -and $c.Direct -ne $c.Fallback) {
            Write-Error "Test-RdfParity.ps1: report mismatch on '$($c.Name)': direct='$($c.Direct)' vs nquads='$($c.Fallback)'"
            exit 3
        }
    }

    # Audit-level checks: the structural invariants must hold on both
    # strategies, AND they must agree with each other.
    $auditChecks = @(
        @{ Name = "SourceOpenedByDotNet"; Direct = $direct.audit.sourceOpenedByDotNet; Fallback = $fallback.audit.sourceOpenedByDotNet; Expected = $false },
        @{ Name = "CleanupSucceeded"; Direct = $direct.audit.cleanupSucceeded; Fallback = $fallback.audit.cleanupSucceeded; Expected = $true }
    )

    foreach ($c in $auditChecks) {
        if ($c.Direct -ne $c.Expected -or $c.Fallback -ne $c.Expected) {
            Write-Error "Test-RdfParity.ps1: audit '$($c.Name)' must be '$($c.Expected)' on both strategies; got direct='$($c.Direct)' nquads='$($c.Fallback)'"
            exit 3
        }
    }

    # Compare query-result hashes — every name must match value-for-value.
    $directHashes = $direct.report.queryResultHashes | ConvertTo-Json -Depth 10 | Sort-Object
    $fallbackHashes = $fallback.report.queryResultHashes | ConvertTo-Json -Depth 10 | Sort-Object
    if ($directHashes -ne $fallbackHashes) {
        Write-Error "Test-RdfParity.ps1: queryResultHashes mismatch.`n  direct:    $directHashes`n  fallback:  $fallbackHashes"
        exit 3
    }

    Write-Host "[Test-RdfParity] OK — direct and nquads reports agree on quad count, graphs, query hashes; both audits confirm SourceOpenedByDotNet=false and CleanupSucceeded=true."
}

Compare-Manifest -DirectPath $directManifestPath -FallbackPath $fallbackManifestPath
exit 0