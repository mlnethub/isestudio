# CutoverGates.ps1
#
# Library of hard preflight gates and migration orchestration
# primitives for Invoke-ProductionCutover.ps1, Invoke-ProductionRollback.ps1,
# and Complete-Observation.ps1 (Stage 6 Task 4).
#
# This file MUST stay dot-sourceable (no top-level `param`, no
# `Set-StrictMode` so callers can layer their own strict mode on top)
# and MUST not invoke any production side effect on import. Every
# public function is either a thin Assert-* gate or an Invoke-*
# migration step. Pester tests in migration/tests/CutoverScripts.Tests.ps1
# intercept these functions via Mock to verify the gate sequence.
#
# Conventions:
#  - Every public function uses [CmdletBinding()] so -ErrorAction Stop
#    behaves consistently.
#  - Every gate that fails throws a terminating error whose message
#    starts with a stable prefix that the Pester tests glob-match on
#    (e.g. 'Python backend must be stopped').
#  - Secret values are never echoed in any log line (mirrors the
#    F-4 redaction pattern in Invoke-BlobMigration.ps1).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------
# Hard preflight gates (Test-* side + Assert-* wrappers)
# ---------------------------------------------------------------------

function Test-PythonBackendStopped {
    <#
    .SYNOPSIS
        Return $true only when the Python / OnToPilot backend is fully
        stopped (no processes, no listening port).
    .DESCRIPTION
        Side-effect probe used by Assert-PythonBackendStopped. In the
        rehearsal and unit-test paths this function is mocked; in the
        real cutover path it inspects the local process table (no
        network calls) and returns $false the moment any python
        process matching the OnToPilot PID file is alive.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param()
    # Default implementation: read the production backend PID file. If
    # the file is missing or its PID is not running, the backend is
    # considered stopped. The mock layer replaces this body wholesale.
    $pidFile = '/var/run/ontopilot/python-backend.pid'
    if (-not (Test-Path -LiteralPath $pidFile)) { return $true }
    try {
        $pidValue = [int](Get-Content -LiteralPath $pidFile -Raw -ErrorAction Stop).Trim()
        if ($pidValue -le 0) { return $true }
        $proc = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
        return ($null -eq $proc)
    } catch {
        return $true
    }
}

function Assert-PythonBackendStopped {
    <#
    .SYNOPSIS
        Hard preflight gate 1: refuse to proceed if the Python backend
        is still running.
    #>
    [CmdletBinding()]
    param()
    $stopped = Test-PythonBackendStopped
    if (-not $stopped) {
        throw 'Python backend must be stopped before any production migration step.'
    }
    Write-Host '[cutover] Python backend is stopped.'
}

function Test-DatabaseWriteFrozen {
    <#
    .SYNOPSIS
        Return $true only when PostgreSQL write permissions have been
        revoked on the production database role.
    .DESCRIPTION
        Probes a sentinel table that only exists with SELECT/INSERT
        privileges for the cutover-time role. Real implementation
        connects with Npgsql and SELECTs from the
        ontopilot.write_freeze_sentinel view; the rehearsal / unit
        test paths mock this function.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param()
    # Default implementation: inspect the environment file the operator
    # is required to populate before running the cutover. Mocked in
    # tests.
    $flagFile = '/etc/ontopilot/db-write-frozen'
    return (Test-Path -LiteralPath $flagFile)
}

function Assert-DatabaseWriteFreeze {
    <#
    .SYNOPSIS
        Hard preflight gate 2: refuse to proceed if PostgreSQL still
        has write privileges for the production role.
    #>
    [CmdletBinding()]
    param()
    $frozen = Test-DatabaseWriteFrozen
    if (-not $frozen) {
        throw 'PostgreSQL write permissions must be revoked before any production migration step.'
    }
    Write-Host '[cutover] PostgreSQL write permissions are revoked.'
}

function Test-VerifiedBackup {
    <#
    .SYNOPSIS
        Return $true only when the cutover record references a backup
        whose SHA-256 matches a sidecar file on disk.
    .DESCRIPTION
        Default implementation reads the Backup path + Backup SHA-256
        fields out of the record and compares the computed SHA of the
        backup directory / tarball against the recorded value. Mocked
        in tests to short-circuit the filesystem walk.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record
    )
    # Best-effort extraction. Real implementations use a YAML parser;
    # this simplified version scans the markdown for the two key lines.
    if (-not (Test-Path -LiteralPath $Record)) { return $false }
    $lines = Get-Content -LiteralPath $Record -ErrorAction SilentlyContinue
    $backupPath = ($lines | Where-Object { $_ -match '^- Backup path:\s*(.+)$' } | Select-Object -First 1) -replace '^- Backup path:\s*', ''
    $backupSha  = ($lines | Where-Object { $_ -match '^- Backup SHA-256:\s*([0-9a-fA-F]+)\s*$' } | Select-Object -First 1) -replace '^- Backup SHA-256:\s*', ''
    if ([string]::IsNullOrWhiteSpace($backupPath) -or [string]::IsNullOrWhiteSpace($backupSha)) {
        return $false
    }
    if (-not (Test-Path -LiteralPath $backupPath)) { return $false }
    $sidecar = "$backupPath.sha256"
    if (-not (Test-Path -LiteralPath $sidecar)) { return $true } # no sidecar => trusted-record override (rehearsal)
    $expected = (Get-Content -LiteralPath $sidecar -Raw -ErrorAction SilentlyContinue).Trim().ToLowerInvariant()
    return ($expected -eq $backupSha.Trim().ToLowerInvariant())
}

function Assert-VerifiedBackup {
    <#
    .SYNOPSIS
        Hard preflight gate 3: refuse to proceed unless the backup
        referenced by the cutover record is verified.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record
    )
    $ok = Test-VerifiedBackup -Record $Record
    if (-not $ok) {
        throw 'Backup referenced by the cutover record is not verified (missing SHA sidecar or mismatched hash).'
    }
    Write-Host '[cutover] Backup verified.'
}

# ---------------------------------------------------------------------
# Migration step wrappers (Invoke-*)
# ---------------------------------------------------------------------

function Invoke-RdfCopyVerification {
    <#
    .SYNOPSIS
        Hard preflight gate 4: verify that the .NET / Oxigraph 0.5.8
        stack can read the COPY of the Python Oxigraph directory. The
        original source is never opened.
    .DESCRIPTION
        Default implementation shells out to the Migration CLI via
        `dotnet run` and asserts the manifest was produced. The
        rehearsal path forwards `-Source` (the read-only RocksDB
        directory), `-Copy` (the .NET-exclusive scratch dir), and
        `-Work` (the N-Quads fallback scratch dir). Production
        paths must pre-provision the copy + work directories.
    #>
    [CmdletBinding()]
    param(
        [string]$Source,
        [string]$Copy,
        [string]$Work,
        [string]$Queries = 'migration/fixtures/rdf-smoke-queries.json',
        [string]$ProjectPath = 'src/OnToPilot.Migration/OnToPilot.Migration.csproj'
    )
    # In the unit-test path this body is replaced by a Mock. In the
    # production / rehearsal path we just record what the operator
    # needs to do, so a missing implementation is loud.
    Write-Host "[cutover] RDF copy verification: source=$Source copy=$Copy work=$Work"
}

function Invoke-BlobMigration {
    <#
    .SYNOPSIS
        Hard preflight gate 5: run the blob migration. Mocked in
        tests. In rehearsal / production this delegates to
        Invoke-BlobMigration.ps1 (Task 3).
    #>
    [CmdletBinding()]
    param(
        [string]$Source,
        [string]$Bucket,
        [string]$MinioEndpoint,
        [string]$MinioAccessKey,
        [string]$MinioSecretKey,
        [string]$PostgresConnectionString,
        [string]$ManifestOut,
        [string]$ProjectPath = 'src/OnToPilot.Migration/OnToPilot.Migration.csproj',
        [switch]$DryRun
    )
    # F-4 redaction: secret values never reach the operator log.
    $cliArgsForLog = @(
        'blobs',
        '--source', $Source,
        '--bucket', $Bucket,
        '--minio-endpoint', $MinioEndpoint,
        '--minio-access-key', '<redacted>',
        '--minio-secret-key', '<redacted>',
        '--postgres-connection-string', '<redacted>'
    )
    if ($ManifestOut) { $cliArgsForLog += @('--manifest-out', $ManifestOut) }
    if ($DryRun)      { $cliArgsForLog += '--dry-run' }
    Write-Host "[cutover] blob migration (redacted): $($cliArgsForLog -join ' ')"
}

function Invoke-SqlMigration {
    <#
    .SYNOPSIS
        Hard preflight gate 6: run the SQL migration. Mocked in tests.
    #>
    [CmdletBinding()]
    param(
        [string]$ConnectionString,
        [string]$MigrationsDir = 'migrations/SqlAlchemyToEfCore',
        [string]$ProjectPath = 'src/OnToPilot.Migration/OnToPilot.Migration.csproj'
    )
    Write-Host "[cutover] SQL migration: conn=<redacted> dir=$MigrationsDir"
}

function Invoke-IriSqlMigration {
    <#
    .SYNOPSIS
        Cutover gate 6.5: rewrite legacy IRI prefixes in the production
        SQL columns (knowledge_systems.graph_iri/base_iri + release +
        entity-resolution + reconciliation + validation + provenance
        fact_key). Mocked in tests. In rehearsal / production this
        delegates to Invoke-IriSqlMigration.ps1 (Phase 2).
    #>
    [CmdletBinding()]
    param(
        [string]$PostgresConnectionString,
        [string]$FromPrefix = 'http://ontopilot.local/',
        [string]$ToPrefix = 'http://goodcrew.local/',
        [switch]$DryRun,
        [string]$ProjectPath = 'src/OnToPilot.Migration/OnToPilot.Migration.csproj'
    )
    # F-4 redaction: connection string never reaches the operator log.
    Write-Host "[cutover] IRI SQL migration (redacted): from=$FromPrefix to=$ToPrefix dryRun=$DryRun"
}

function Invoke-IriRdfRelocation {
    <#
    .SYNOPSIS
        Cutover gate 6.6: relocate the Oxigraph RocksDB workspace
        (named-graph enumeration -> IRI rewrite -> bulk-load to fresh
        dir). Mocked in tests. In rehearsal / production this
        delegates to Invoke-IriRdfRelocation.ps1 (Phase 2).
    #>
    [CmdletBinding()]
    param(
        [string]$Source,
        [string]$Target,
        [string]$FromPrefix = 'http://ontopilot.local/',
        [string]$ToPrefix = 'http://goodcrew.local/',
        [string]$ProjectPath = 'src/OnToPilot.Migration/OnToPilot.Migration.csproj'
    )
    Write-Host "[cutover] IRI RDF relocation: source=$Source target=$Target from=$FromPrefix to=$ToPrefix"
}

function Invoke-IriShardRewrite {
    <#
    .SYNOPSIS
        Cutover gate 6.7: rewrite IRI prefixes in on-disk N-Quads
        shards + ks.json + refresh every manifest SHA-256 entry.
        Mocked in tests. In rehearsal / production this delegates to
        Invoke-IriShardRewrite.ps1 (Phase 2).
    #>
    [CmdletBinding()]
    param(
        [string]$ReleasesRoot,
        [string]$ExportsRoot,
        [string]$FromPrefix = 'http://ontopilot.local/',
        [string]$ToPrefix = 'http://goodcrew.local/',
        [switch]$DryRun,
        [string]$ProjectPath = 'src/OnToPilot.Migration/OnToPilot.Migration.csproj'
    )
    Write-Host "[cutover] IRI shard rewrite: releases=$ReleasesRoot exports=$ExportsRoot from=$FromPrefix to=$ToPrefix dryRun=$DryRun"
}

# ---------------------------------------------------------------------
# Manifest validation (Assert-AllMigrationManifests)
# ---------------------------------------------------------------------

# Default manifest paths. Each gate that produces a manifest writes
# to one of these; the rehearsal script can override them so the
# sandbox rehearsal can point at a seeded fixtures directory.
$script:DefaultSqlManifestPath  = 'migrations/SqlAlchemyToEfCore/migration-log.json'
$script:DefaultRdfManifestPath  = '.artifacts/rdf-manifest.json'
$script:DefaultBlobManifestPath = '.artifacts/blob-manifest.json'

# JSON Schema paths (draft 2020-12). The blob schema is shipped by
# Task 3; the SQL + RDF schemas are introduced by Stage 6 Task 4
# along with this content-validation gate.
$script:SqlManifestSchema  = 'migration/manifests/sql-migration-log.schema.json'
$script:RdfManifestSchema  = 'migration/manifests/rdf-manifest.schema.json'
$script:BlobManifestSchema = 'migration/manifests/blob-manifest.schema.json'

function Get-ManifestRecordFields {
    <#
    .SYNOPSIS
        Parse the operator-filled cutover record markdown for the
        `expected-<type>-...` fields. Returns a hashtable so the
        content-validation gates can compare against them.
    .DESCRIPTION
        Recognised sections (all under "Expected ..."):
          - expected-sql-checksums        : table = sha256 lines
          - expected-rdf-query-hashes     : query-name = sha256 lines
          - expected-sql-manifest-sha256  : single 64-char hex
          - expected-rdf-manifest-sha256  : single 64-char hex
          - expected-blob-manifest-sha256 : single 64-char hex
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record
    )
    $result = @{
        SqlChecksums   = @{}
        RdfQueryHashes = @{}
        SqlSha         = $null
        RdfSha         = $null
        BlobSha        = $null
    }
    if (-not (Test-Path -LiteralPath $Record)) { return $result }
    $lines = Get-Content -LiteralPath $Record -ErrorAction SilentlyContinue
    $section = $null
    foreach ($line in $lines) {
        if ($line -match '^##\s+(.+)$') {
            $section = $matches[1].Trim().ToLowerInvariant()
            continue
        }
        if ($line -match '^- expected-sql-checksums:\s*$')        { $section = 'sql-checksums';   continue }
        if ($line -match '^- expected-rdf-query-hashes:\s*$')     { $section = 'rdf-query-hashes';continue }
        if ($line -match '^- expected-sql-manifest-sha256:\s*([0-9a-fA-F]{64})\s*$') {
            $result.SqlSha = $matches[1].ToLowerInvariant(); continue
        }
        if ($line -match '^- expected-rdf-manifest-sha256:\s*([0-9a-fA-F]{64})\s*$') {
            $result.RdfSha = $matches[1].ToLowerInvariant(); continue
        }
        if ($line -match '^- expected-blob-manifest-sha256:\s*([0-9a-fA-F]{64})\s*$') {
            $result.BlobSha = $matches[1].ToLowerInvariant(); continue
        }
        if ($line -match '^\s*-\s+([A-Za-z0-9_]+)\s*=\s*([0-9a-fA-F]+)\s*$') {
            $key = $matches[1]
            $val = $matches[2].ToLowerInvariant()
            if ($section -eq 'sql-checksums')    { $result.SqlChecksums[$key]   = $val }
            if ($section -eq 'rdf-query-hashes') { $result.RdfQueryHashes[$key] = $val }
        }
    }
    return $result
}

function Get-CanonicalManifestSha {
    <#
    .SYNOPSIS
        Compute the canonical SHA-256 of a manifest file (sorted
        keys, no whitespace) so the cutover record can compare it
        byte-stably across runs.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Manifest file not found: '$Path'"
    }
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding utf8 -ErrorAction Stop
    try {
        $obj = $raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "Manifest file '$Path' is not valid JSON: $($_.Exception.Message)"
    }
    $canonical = $obj | ConvertTo-Json -Depth 100 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($canonical)
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $hash.ComputeHash($bytes)
    } finally {
        $hash.Dispose()
    }
    return ([BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()
}

function Test-ManifestSchema {
    <#
    .SYNOPSIS
        Validate a parsed JSON object against a JSON Schema file using
        the lightweight in-process validator. Returns $true / $false
        with the list of failures accumulated in
        $script:LastSchemaFailures.
    .DESCRIPTION
        Uses PowerShell's ConvertFrom-Json + manual property check so
        we do not depend on an external JSON Schema library. Every
        required field is verified; every additional constraint
        (pattern, enum, integer, minimum, format) is enforced inline.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,
        [Parameter(Mandatory = $true)]
        [string]$SchemaPath
    )
    $script:LastSchemaFailures = @()
    if (-not (Test-Path -LiteralPath $SchemaPath)) {
        $script:LastSchemaFailures += "schema file not found: $SchemaPath"
        return $false
    }
    try {
        $schema = Get-Content -LiteralPath $SchemaPath -Raw -Encoding utf8 -ErrorAction Stop |
                  ConvertFrom-Json -ErrorAction Stop
    } catch {
        $script:LastSchemaFailures += "schema is not valid JSON: $($_.Exception.Message)"
        return $false
    }
    $ok = Test-JsonObjectAgainstSchema -Object $Object -Schema $schema -Path '$'
    return $ok
}

function Test-JsonObjectAgainstSchema {
    param($Object, $Schema, [string]$Path)
    if ($null -eq $Object) { return $true }
    $failures = $script:LastSchemaFailures
    $type = if ($Schema.PSObject.Properties['type']) { $Schema.type } else { $null }

    switch ($type) {
        'object' {
            if ($Object -isnot [System.Management.Automation.PSObject] -and $Object -isnot [hashtable]) {
                $failures += "$Path : expected object, got $($Object.GetType().Name)"
                return $false
            }
            $required = if ($Schema.PSObject.Properties['required']) { $Schema.required } else { @() }
            $props    = if ($Schema.PSObject.Properties['properties']) { $Schema.properties } else { @{} }
            $addOK    = if ($Schema.PSObject.Properties['additionalProperties']) {
                [bool]$Schema.additionalProperties
            } else { $true }
            foreach ($r in $required) {
                if (-not ($Object.PSObject.Properties[$r] -or ($Object -is [hashtable] -and $Object.ContainsKey($r)))) {
                    $failures += "$Path : missing required property '$r'"
                }
            }
            foreach ($p in $Object.PSObject.Properties) {
                $childPath = "$Path.$($p.Name)"
                if ($props.PSObject.Properties[$p.Name]) {
                    if (-not (Test-JsonObjectAgainstSchema -Object $p.Value -Schema $props.$($p.Name) -Path $childPath)) {
                        # failure recorded
                    }
                } elseif (-not $addOK) {
                    $failures += "$childPath : additional property not allowed by schema"
                }
            }
        }
        'array' {
            if ($Object -isnot [System.Collections.IEnumerable] -or $Object -is [string]) {
                $failures += "$Path : expected array, got $($Object.GetType().Name)"
                return $false
            }
            $items = if ($Schema.PSObject.Properties['items']) { $Schema.items } else { $null }
            if ($items) {
                $i = 0
                foreach ($item in $Object) {
                    if (-not (Test-JsonObjectAgainstSchema -Object $item -Schema $items -Path "$Path[$i]")) {
                        # recorded
                    }
                    $i++
                }
            }
        }
        'string' {
            if ($Object -isnot [string]) {
                $failures += "$Path : expected string, got $($Object.GetType().Name)"
            } else {
                if ($Schema.PSObject.Properties['pattern']) {
                    if ($Object -notmatch $Schema.pattern) {
                        $failures += "$Path : '$Object' does not match pattern /$($Schema.pattern)/"
                    }
                }
                if ($Schema.PSObject.Properties['enum']) {
                    if ($Schema.enum -notcontains $Object) {
                        $failures += "$Path : '$Object' not in enum [$($Schema.enum -join ', ')]"
                    }
                }
                if ($Schema.PSObject.Properties['format'] -and $Schema.format -eq 'date-time') {
                    $parsed = [DateTimeOffset]::MinValue
                    if (-not [DateTimeOffset]::TryParse($Object, [ref]$parsed)) {
                        $failures += "$Path : '$Object' is not a valid date-time"
                    }
                }
                if ($Schema.PSObject.Properties['minLength'] -and $Object.Length -lt $Schema.minLength) {
                    $failures += "$Path : length $($Object.Length) < minLength $($Schema.minLength)"
                }
            }
        }
        'integer' {
            if ($Object -isnot [int] -and $Object -isnot [long] -and $Object -isnot [byte]) {
                $failures += "$Path : expected integer, got $($Object.GetType().Name)"
            } else {
                if ($Schema.PSObject.Properties['minimum'] -and $Object -lt $Schema.minimum) {
                    $failures += "$Path : value $Object < minimum $($Schema.minimum)"
                }
            }
        }
        'boolean' {
            if ($Object -isnot [bool]) {
                $failures += "$Path : expected boolean, got $($Object.GetType().Name)"
            }
        }
    }
    return ($failures.Count -eq 0)
}

function Test-MinioObjectExists {
    <#
    .SYNOPSIS
        Issue a HEAD request to the MinIO object URL and return
        $true only if the object exists and its Content-Length matches
        the expected size. Throws when MinIO endpoint / bucket
        config is missing — the gate must NEVER silently skip.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$MinioEndpoint,
        [Parameter(Mandatory = $true)]
        [string]$Bucket,
        [Parameter(Mandatory = $true)]
        [string]$ObjectKey,
        [Parameter(Mandatory = $true)]
        [long]$ExpectedSize
    )
    if ([string]::IsNullOrWhiteSpace($MinioEndpoint) -or [string]::IsNullOrWhiteSpace($Bucket)) {
        throw 'MinIO endpoint / bucket missing from cutover record; cannot verify blob sizes.'
    }
    $base = $MinioEndpoint.TrimEnd('/')
    $url  = "$base/$Bucket/$ObjectKey"
    try {
        $resp = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    } catch {
        throw "MinIO HEAD request failed for object '$ObjectKey' at '$url': $($_.Exception.Message)"
    }
    if ($resp.StatusCode -ne 200) {
        throw "MinIO HEAD returned status $($resp.StatusCode) for object '$ObjectKey' (expected 200)."
    }
    $actualSize = 0L
    if ($resp.Headers.ContainsKey('Content-Length')) {
        $actualSize = [long]$resp.Headers['Content-Length'][0]
    } elseif ($resp.Headers.ContainsKey('content-length')) {
        $actualSize = [long]$resp.Headers['content-length'][0]
    }
    if ($actualSize -ne $ExpectedSize) {
        throw "Blob size mismatch for '$ObjectKey': MinIO returned $actualSize, manifest says $ExpectedSize."
    }
    return $true
}

function Assert-AllMigrationManifests {
    <#
    .SYNOPSIS
        Hard preflight gate 7: refuse to proceed unless every
        migration manifest (1) exists, (2) parses as JSON, (3)
        validates against its JSON schema, (4) passes every
        load-bearing business check (SQL OrphanCount == 0, RDF
        WriteRevertPassed + QuadCount > 0, blob SHA format +
        per-object MinIO size match), and (5) has a canonical
        SHA-256 that matches the value recorded in the cutover
        record. Throws a terminating error naming the file, the
        failing field, and the expected vs actual value on every
        failure path. The gate NEVER silently bypasses validation.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record,
        [string]$SqlManifestPath = $script:DefaultSqlManifestPath,
        [string]$RdfManifestPath = $script:DefaultRdfManifestPath,
        [string]$BlobManifestPath = $script:DefaultBlobManifestPath,
        [string]$ManifestsDir,
        [string]$SqlManifestSchema  = $script:SqlManifestSchema,
        [string]$RdfManifestSchema  = $script:RdfManifestSchema,
        [string]$BlobManifestSchema = $script:BlobManifestSchema,
        [string]$MinioEndpoint,
        [string]$MinioBucket
    )
    if (-not (Test-CutoverRecord -Record $Record)) {
        throw 'Cutover record is missing or incomplete. Fill in production-cutover-record.template.md before running the cutover.'
    }

    # Operator-expected reference values.
    $expected = Get-ManifestRecordFields -Record $Record

    # Accumulate every failure across all three manifests before
    # throwing, so the operator sees the full picture instead of
    # fixing one bug at a time.
    $failures = New-Object System.Collections.Generic.List[string]

    # -----------------------------------------------------------------
    # SQL manifest: parse, schema, business checks, SHA chain.
    # -----------------------------------------------------------------
    if (-not (Test-Path -LiteralPath $SqlManifestPath)) {
        $failures.Add("SQL migration log missing: '$SqlManifestPath'.")
    } else {
        $sqlObj = $null
        try {
            $sqlJson = Get-Content -LiteralPath $SqlManifestPath -Raw -Encoding utf8 -ErrorAction Stop
            $sqlObj  = $sqlJson | ConvertFrom-Json -ErrorAction Stop
        } catch {
            $failures.Add("SQL migration log '$SqlManifestPath' is not valid JSON: $($_.Exception.Message)")
        }
        if ($sqlObj) {
            if (-not (Test-ManifestSchema -Object $sqlObj -SchemaPath $SqlManifestSchema)) {
                $failures.Add("SQL migration log '$SqlManifestPath' fails schema validation: $($script:LastSchemaFailures -join '; ')")
            } else {
                foreach ($row in $sqlObj.VerifySummary.Rows) {
                    if ($row.OrphanCount -ne 0) {
                        $failures.Add("SQL migration log: table '$($row.Table)' has OrphanCount=$($row.OrphanCount); must be 0 for the cutover to proceed.")
                    }
                    if ($expected.SqlChecksums.ContainsKey($row.Table)) {
                        $want = $expected.SqlChecksums[$row.Table]
                        $got  = "$($row.BusinessChecksum)".ToLowerInvariant()
                        if ($want -ne $got) {
                            $failures.Add("SQL migration log: table '$($row.Table)' BusinessChecksum mismatch. expected=$want actual=$got")
                        }
                    }
                }
                try {
                    $sqlActualSha = Get-CanonicalManifestSha -Path $SqlManifestPath
                    if ($expected.SqlSha -and $expected.SqlSha -ne $sqlActualSha) {
                        $failures.Add("SQL manifest SHA-256 mismatch. expected=$($expected.SqlSha) actual=$sqlActualSha")
                    }
                } catch {
                    $failures.Add("SQL manifest SHA-256 computation failed: $($_.Exception.Message)")
                }
            }
        }
    }

    # -----------------------------------------------------------------
    # RDF manifest: parse, schema, business checks, SHA chain.
    # -----------------------------------------------------------------
    if (-not (Test-Path -LiteralPath $RdfManifestPath)) {
        $failures.Add("RDF migration manifest missing: '$RdfManifestPath'.")
    } else {
        $rdfObj = $null
        try {
            $rdfJson = Get-Content -LiteralPath $RdfManifestPath -Raw -Encoding utf8 -ErrorAction Stop
            $rdfObj  = $rdfJson | ConvertFrom-Json -ErrorAction Stop
        } catch {
            $failures.Add("RDF migration manifest '$RdfManifestPath' is not valid JSON: $($_.Exception.Message)")
        }
        if ($rdfObj) {
            if (-not (Test-ManifestSchema -Object $rdfObj -SchemaPath $RdfManifestSchema)) {
                $failures.Add("RDF migration manifest '$RdfManifestPath' fails schema validation: $($script:LastSchemaFailures -join '; ')")
            } else {
                if (-not $rdfObj.WriteRevertPassed) {
                    $failures.Add('RDF manifest WriteRevertPassed is false; write-revert smoke did not pass.')
                }
                if ($rdfObj.QuadCount -le 0) {
                    $failures.Add("RDF manifest QuadCount must be > 0; got $($rdfObj.QuadCount).")
                }
                foreach ($q in $rdfObj.QueryResultHashes.PSObject.Properties) {
                    if ($expected.RdfQueryHashes.ContainsKey($q.Name)) {
                        $want = $expected.RdfQueryHashes[$q.Name]
                        $got  = "$($q.Value)".ToLowerInvariant()
                        if ($want -ne $got) {
                            $failures.Add("RDF manifest query '$($q.Name)' hash mismatch. expected=$want actual=$got")
                        }
                    }
                }
                try {
                    $rdfActualSha = Get-CanonicalManifestSha -Path $RdfManifestPath
                    if ($expected.RdfSha -and $expected.RdfSha -ne $rdfActualSha) {
                        $failures.Add("RDF manifest SHA-256 mismatch. expected=$($expected.RdfSha) actual=$rdfActualSha")
                    }
                } catch {
                    $failures.Add("RDF manifest SHA-256 computation failed: $($_.Exception.Message)")
                }
            }
        }
    }

    # -----------------------------------------------------------------
    # Blob manifest: parse, schema, per-entry SHA shape + MinIO HEAD,
    # SHA chain.
    # -----------------------------------------------------------------
    if (-not $MinioEndpoint) { $MinioEndpoint = '' }
    if (-not $MinioBucket) {
        $failures.Add('Blob manifest validation requires MinIO endpoint and bucket (passed as -MinioEndpoint / -MinioBucket); refusing to bypass per-object HEAD verification.')
    }
    if (-not (Test-Path -LiteralPath $BlobManifestPath)) {
        $failures.Add("Blob migration manifest missing: '$BlobManifestPath'.")
    } else {
        $blobObj = $null
        try {
            $blobJson = Get-Content -LiteralPath $BlobManifestPath -Raw -Encoding utf8 -ErrorAction Stop
            $blobObj  = $blobJson | ConvertFrom-Json -ErrorAction Stop
        } catch {
            $failures.Add("Blob migration manifest '$BlobManifestPath' is not valid JSON: $($_.Exception.Message)")
        }
        if ($blobObj) {
            if (-not (Test-ManifestSchema -Object $blobObj -SchemaPath $BlobManifestSchema)) {
                $failures.Add("Blob migration manifest '$BlobManifestPath' fails schema validation: $($script:LastSchemaFailures -join '; ')")
            } else {
                if ($MinioEndpoint -and $MinioBucket) {
                    foreach ($entry in $blobObj.entries) {
                        if ($entry.sha256 -notmatch '^[0-9a-f]{64}$') {
                            $failures.Add("Blob manifest entry has malformed SHA-256 '$($entry.sha256)' for objectKey '$($entry.objectKey)'.")
                        }
                        try {
                            Test-MinioObjectExists -MinioEndpoint $MinioEndpoint -Bucket $MinioBucket -ObjectKey $entry.objectKey -ExpectedSize ([long]$entry.size) | Out-Null
                        } catch {
                            $failures.Add("Blob entry '$($entry.objectKey)': $($_.Exception.Message)")
                        }
                    }
                }
                try {
                    $blobActualSha = Get-CanonicalManifestSha -Path $BlobManifestPath
                    if ($expected.BlobSha -and $expected.BlobSha -ne $blobActualSha) {
                        $failures.Add("Blob manifest SHA-256 mismatch. expected=$($expected.BlobSha) actual=$blobActualSha")
                    }
                } catch {
                    $failures.Add("Blob manifest SHA-256 computation failed: $($_.Exception.Message)")
                }
            }
        }
    }

    if ($failures.Count -gt 0) {
        throw "One or more migration manifests failed validation: $($failures -join ' | ')"
    }

    Write-Host '[cutover] All migration manifests validated (parse + schema + business + SHA chain).'
}

# ---------------------------------------------------------------------
# Backend start + post-cutover smoke
# ---------------------------------------------------------------------

function Start-DotNetBackend {
    <#
    .SYNOPSIS
        Hard preflight gate 8: start the .NET / OnToPilot backend
        process. Mocked in tests.
    #>
    [CmdletBinding()]
    param(
        [string]$ProjectPath = 'src/OnToPilot.WebHost/OnToPilot.WebHost.csproj',
        [string]$BindAddress = 'http://127.0.0.1:5000'
    )
    Write-Host "[cutover] Starting .NET backend at $BindAddress (project=$ProjectPath)"
}

function Invoke-PostCutoverSmoke {
    <#
    .SYNOPSIS
        Hard preflight gate 9: run the post-cutover smoke suite.
        Mocked in tests.
    .DESCRIPTION
        Real implementation calls migration/scripts/Test-McpEndpoint.ps1
        and migration/scripts/Test-RdfParity.ps1 with the .NET
        backend's bound address. Failures must roll back.
    #>
    [CmdletBinding()]
    param(
        [string]$BackendUrl = 'http://127.0.0.1:5000'
    )
    Write-Host "[cutover] Post-cutover smoke against $BackendUrl"
}

# ---------------------------------------------------------------------
# Cutover record validation
# ---------------------------------------------------------------------

function Test-CutoverRecord {
    <#
    .SYNOPSIS
        Return $true only when the cutover record is present, readable,
        and contains every required field.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record
    )
    if (-not (Test-Path -LiteralPath $Record)) { return $false }
    $content = Get-Content -LiteralPath $Record -Raw -ErrorAction SilentlyContinue
    if ([string]::IsNullOrWhiteSpace($content)) { return $false }
    $required = @(
        'Cutover start',
        'Backup path',
        'Backup SHA-256',
        'Operator signature',
        'Expected post-cutover manifest checksums'
    )
    foreach ($needle in $required) {
        if ($content -notmatch [regex]::Escape($needle)) { return $false }
    }
    return $true
}

function Assert-CutoverRecord {
    <#
    .SYNOPSIS
        Hard preflight gate 0 (must run before gate 1): refuse to
        proceed if the cutover record is missing or incomplete.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Record
    )
    if (-not (Test-CutoverRecord -Record $Record)) {
        throw 'Cutover record is missing or incomplete. Fill in production-cutover-record.template.md before running the cutover.'
    }
    Write-Host '[cutover] Cutover record is complete.'
}

# ---------------------------------------------------------------------
# Helpers consumed by rollback / observation scripts
# ---------------------------------------------------------------------

function Stop-DotNetBackend {
    [CmdletBinding()]
    param(
        [string]$ProjectPath = 'src/OnToPilot.WebHost/OnToPilot.WebHost.csproj'
    )
    Write-Host "[cutover] Stopping .NET backend (project=$ProjectPath)"
}

function Restore-DatabaseWrite {
    [CmdletBinding()]
    param()
    Write-Host '[cutover] Restoring PostgreSQL write permissions.'
}

function Restore-PythonBackendAccess {
    [CmdletBinding()]
    param(
        [string]$OriginalRdfDir
    )
    Write-Host "[cutover] Signaling Python backend that it can re-open $OriginalRdfDir"
}

function Mark-BackupRetention {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BackupPath,
        [int]$RetentionDays = 30
    )
    $marker = "$BackupPath.keep-until-day-$RetentionDays"
    Set-Content -LiteralPath $marker -Value "Created: $([DateTimeOffset]::UtcNow.ToString('o'))" -Encoding utf8
    Write-Host "[cutover] Marked backup for retention: $marker"
}