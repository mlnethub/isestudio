# CutoverScripts.Tests.ps1
#
# Pester tests for the production cutover / rollback orchestration
# scripts delivered by Stage 6 Task 4. These tests enforce that the
# hard preflight gates called by Invoke-ProductionCutover.ps1
# actually refuse to proceed when a gate fails, and that they
# continue to the next gate only when the previous one succeeds.
#
# Syntax notes (binding):
#  - Pester 3.4.0 is the version installed in this environment. The
#    tests below use `It`, `Mock`, `Should Throw`, and
#    `Assert-MockCalled` exclusively — no `BeforeAll`, no Pester 5
#    idioms.
#  - Pester 3 keeps mock call history inside a Describe block across
#    every It inside that block. Tests that need a clean call count
#    (notably the verbatim `refuses cutover while python backend is
#    running` test that checks `Invoke-SqlMigration -Times 0`) live in
#    their own Describe block so the mock state is reset.
#  - The cutover script and its gates library are dot-sourced into
#    the test scope (script-scope) so Pester's Mock can intercept the
#    gate-level functions (`Test-PythonBackendStopped`, etc.) before
#    the cutover script body runs.

[CmdletBinding()]
param(
    [string]$CutoverScriptPath = "$PSScriptRoot/../scripts/Invoke-ProductionCutover.ps1",
    [string]$GatesLibraryPath  = "$PSScriptRoot/../scripts/gates/CutoverGates.ps1"
)

# Resolve both paths relative to this file so the operator can run
# Pester from anywhere (CI, VS Code test runner, manual pwsh).
$CutoverScriptPath = (Resolve-Path -LiteralPath $CutoverScriptPath -ErrorAction SilentlyContinue)?.Path
$GatesLibraryPath  = (Resolve-Path -LiteralPath $GatesLibraryPath  -ErrorAction SilentlyContinue)?.Path

if (-not $CutoverScriptPath -or -not (Test-Path -LiteralPath $CutoverScriptPath)) {
    throw "CutoverScriptPath not found: '$CutoverScriptPath'"
}
if (-not $GatesLibraryPath -or -not (Test-Path -LiteralPath $GatesLibraryPath)) {
    throw "GatesLibraryPath not found: '$GatesLibraryPath'"
}

# Dot-source the gates library and the cutover script at the top of
# the test file (script scope). Re-dot-sourcing inside BeforeEach
# creates a fresh function binding that Pester 3's Mock cannot
# intercept.
. $GatesLibraryPath
. $CutoverScriptPath

# A valid cutover-record.md body used by every "happy path" test.
# The cutover script is strict about which fields must be present
# and non-empty; this template matches the
# production-cutover-record.template.md layout delivered alongside
# the runbooks.
$ValidRecordContent = @"
# Production Cutover Record

- Cutover start (UTC): 2026-08-18T10:00:00Z
- Backup path: /var/backups/ontopilot/2026-08-18
- Backup SHA-256: 0000000000000000000000000000000000000000000000000000000000000000
- Operator signature: Test Operator
- Expected post-cutover manifest checksums:
  - SQL verify summary: abc123
  - RDF verify summary: def456
  - blob verify summary: 7890ab
"@

# Helper that writes the valid record into a fresh $TestDrive path.
function New-CutoverRecordFixture {
    [CmdletBinding()]
    param(
        [string]$Body = $ValidRecordContent,
        [string]$Name  = 'cutover-record.md'
    )
    $path = Join-Path $TestDrive $Name
    Set-Content -LiteralPath $path -Value $Body -Encoding utf8
    return $path
}

Describe 'hard preflight gate: refuses cutover while python backend is running' {

    # This Describe block exists in isolation so the mock call history
    # for Invoke-SqlMigration starts fresh — the verbatim test below
    # asserts `Times 0`, which fails if a previous test in another
    # Describe block already invoked the mock.
    It 'refuses cutover while python backend is running' {
        $script:ValidRecord = New-CutoverRecordFixture

        Mock Test-PythonBackendStopped { $false }
        Mock Test-DatabaseWriteFrozen  { $true }
        Mock Test-VerifiedBackup       { param($Record) $true }
        Mock Invoke-RdfCopyVerification { }
        Mock Invoke-BlobMigration       { }
        Mock Invoke-SqlMigration        { }
        Mock Invoke-IriSqlSmokeCheck    { }
        Mock Assert-AllMigrationManifests { }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:ValidRecord } |
            Should Throw 'Python backend must be stopped'
        Assert-MockCalled Invoke-SqlMigration -Times 0
    }
}

Describe 'hard preflight gate: refuses cutover when database is still writable' {

    It 'refuses cutover when database is still writable' {
        $script:ValidRecord = New-CutoverRecordFixture

        Mock Test-PythonBackendStopped { $true }
        Mock Test-DatabaseWriteFrozen  { $false }
        Mock Test-VerifiedBackup       { param($Record) $true }
        Mock Invoke-RdfCopyVerification { }
        Mock Invoke-BlobMigration       { }
        Mock Invoke-SqlMigration        { }
        Mock Invoke-IriSqlSmokeCheck    { }
        Mock Assert-AllMigrationManifests { }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:ValidRecord } |
            Should Throw 'PostgreSQL write permissions must be revoked'
    }
}

Describe 'hard preflight gate: refuses cutover when backup is not verified' {

    It 'refuses cutover when backup is not verified' {
        $script:ValidRecord = New-CutoverRecordFixture

        Mock Test-PythonBackendStopped { $true }
        Mock Test-DatabaseWriteFrozen  { $true }
        Mock Test-VerifiedBackup       { param($Record) $false }
        Mock Invoke-RdfCopyVerification { }
        Mock Invoke-BlobMigration       { }
        Mock Invoke-SqlMigration        { }
        Mock Invoke-IriSqlSmokeCheck    { }
        Mock Assert-AllMigrationManifests { }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:ValidRecord } |
            Should Throw 'Backup'
    }
}

Describe 'happy path: proceeds past backup gate when backup is verified' {

    It 'proceeds past backup gate when verified backup is recorded' {
        $script:ValidRecord = New-CutoverRecordFixture

        Mock Test-PythonBackendStopped { $true }
        Mock Test-DatabaseWriteFrozen  { $true }
        Mock Test-VerifiedBackup       { param($Record) $true }
        Mock Invoke-RdfCopyVerification { }
        Mock Invoke-BlobMigration       { }
        Mock Invoke-SqlMigration        { }
        Mock Invoke-IriSqlSmokeCheck    { }
        Mock Assert-AllMigrationManifests { }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:ValidRecord } |
            Should Not Throw
        Assert-MockCalled Invoke-RdfCopyVerification -Times 1
    }
}

Describe 'migration gates: stop the sequence on the first failure' {

    It 'stops immediately when RDF copy verification fails' {
        $script:ValidRecord = New-CutoverRecordFixture

        Mock Test-PythonBackendStopped { $true }
        Mock Test-DatabaseWriteFrozen  { $true }
        Mock Test-VerifiedBackup       { param($Record) $true }
        Mock Invoke-RdfCopyVerification { throw 'rdf copy mismatch' }
        Mock Invoke-BlobMigration       { }
        Mock Invoke-SqlMigration        { }
        Mock Invoke-IriSqlSmokeCheck    { }
        Mock Assert-AllMigrationManifests { }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:ValidRecord } |
            Should Throw 'rdf copy mismatch'
    }

    It 'stops immediately when blob migration fails' {
        $script:ValidRecord = New-CutoverRecordFixture

        Mock Test-PythonBackendStopped { $true }
        Mock Test-DatabaseWriteFrozen  { $true }
        Mock Test-VerifiedBackup       { param($Record) $true }
        Mock Invoke-RdfCopyVerification { }
        Mock Invoke-BlobMigration       { throw 'minio unreachable' }
        Mock Invoke-SqlMigration        { }
        Mock Invoke-IriSqlSmokeCheck    { }
        Mock Assert-AllMigrationManifests { }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:ValidRecord } |
            Should Throw 'minio unreachable'
    }

    It 'stops immediately when SQL migration fails' {
        $script:ValidRecord = New-CutoverRecordFixture

        Mock Test-PythonBackendStopped { $true }
        Mock Test-DatabaseWriteFrozen  { $true }
        Mock Test-VerifiedBackup       { param($Record) $true }
        Mock Invoke-RdfCopyVerification { }
        Mock Invoke-BlobMigration       { }
        Mock Invoke-SqlMigration        { throw 'verify.sql rowcount drift' }
        Mock Invoke-IriSqlSmokeCheck    { }
        Mock Assert-AllMigrationManifests { }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:ValidRecord } |
            Should Throw 'verify.sql rowcount drift'
    }
}

Describe 'manifest validation gate: stops when checksums disagree' {

    It 'stops when manifest validation rejects the recorded checksums' {
        $script:ValidRecord = New-CutoverRecordFixture

        Mock Test-PythonBackendStopped { $true }
        Mock Test-DatabaseWriteFrozen  { $true }
        Mock Test-VerifiedBackup       { param($Record) $true }
        Mock Invoke-RdfCopyVerification { }
        Mock Invoke-BlobMigration       { }
        Mock Invoke-SqlMigration        { }
        Mock Invoke-IriSqlSmokeCheck    { }
        Mock Assert-AllMigrationManifests { throw 'blob checksum mismatch' }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:ValidRecord } |
            Should Throw 'blob checksum mismatch'
    }
}

Describe 'happy path: full sequence reaches post-cutover smoke on success' {

    It 'runs the full sequence and reaches post-cutover smoke on success' {
        $script:ValidRecord = New-CutoverRecordFixture

        Mock Test-PythonBackendStopped { $true }
        Mock Test-DatabaseWriteFrozen  { $true }
        Mock Test-VerifiedBackup       { param($Record) $true }
        Mock Invoke-RdfCopyVerification { }
        Mock Invoke-BlobMigration       { }
        Mock Invoke-SqlMigration        { }
        Mock Invoke-IriSqlSmokeCheck    { }
        Mock Assert-AllMigrationManifests { }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:ValidRecord } |
            Should Not Throw
        Assert-MockCalled Test-PythonBackendStopped -Times 1
        Assert-MockCalled Test-DatabaseWriteFrozen  -Times 1
        Assert-MockCalled Test-VerifiedBackup       -Times 1
        Assert-MockCalled Invoke-RdfCopyVerification -Times 1
        Assert-MockCalled Invoke-BlobMigration       -Times 1
        Assert-MockCalled Invoke-SqlMigration        -Times 1
        Assert-MockCalled Invoke-IriSqlSmokeCheck    -Times 1
        Assert-MockCalled Assert-AllMigrationManifests -Times 1
        Assert-MockCalled Start-DotNetBackend        -Times 1
        Assert-MockCalled Invoke-PostCutoverSmoke    -Times 1
    }
}

Describe 'cutover record gate: refuses to start when the record is incomplete' {

    It 'refuses to start when the cutover record is missing required fields' {
        $script:BadRecord = New-CutoverRecordFixture -Body '# Empty' -Name 'cutover-record-bad.md'

        Mock Test-PythonBackendStopped { $true }
        Mock Test-DatabaseWriteFrozen  { $true }
        Mock Test-VerifiedBackup       { param($Record) $true }
        Mock Invoke-RdfCopyVerification { }
        Mock Invoke-BlobMigration       { }
        Mock Invoke-SqlMigration        { }
        Mock Invoke-IriSqlSmokeCheck    { }
        Mock Assert-AllMigrationManifests { }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:BadRecord } |
            Should Throw 'Cutover record'
    }
}

Describe 'IRI SQL smoke-check gate: stops the sequence on residual legacy prefix' {

    It 'stops immediately when smoke-check finds residual legacy-prefix rows' {
        $script:ValidRecord = New-CutoverRecordFixture

        Mock Test-PythonBackendStopped { $true }
        Mock Test-DatabaseWriteFrozen  { $true }
        Mock Test-VerifiedBackup       { param($Record) $true }
        Mock Invoke-RdfCopyVerification { }
        Mock Invoke-BlobMigration       { }
        Mock Invoke-SqlMigration        { }
        Mock Invoke-IriSqlSmokeCheck    { throw 'One or more IRI SQL columns still contain the legacy prefix: knowledgesystem.GraphIri: 1 row(s) still contain http://ontopilot.local/' }
        Mock Invoke-IriRdfRelocation    { }
        Mock Assert-AllMigrationManifests { }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:ValidRecord } |
            Should Throw 'IRI SQL columns still contain the legacy prefix'
        # Sequence must STOP at the smoke-check — RDF relocation must
        # never run on top of an unverified IRI SQL state.
        Assert-MockCalled Invoke-IriSqlSmokeCheck -Times 1
        Assert-MockCalled Invoke-IriRdfRelocation -Times 0
    }

    It 'skips smoke-check when -IriDryRun is set' {
        $script:ValidRecord = New-CutoverRecordFixture

        Mock Test-PythonBackendStopped { $true }
        Mock Test-DatabaseWriteFrozen  { $true }
        Mock Test-VerifiedBackup       { param($Record) $true }
        Mock Invoke-RdfCopyVerification { }
        Mock Invoke-BlobMigration       { }
        Mock Invoke-SqlMigration        { }
        # NOTE: Invoke-IriSqlSmokeCheck is intentionally NOT mocked
        # here. Pester 3 cannot detect "not called" for an unmocked
        # function, so we instead mock the gate that runs AFTER the
        # smoke-check (Invoke-IriRdfRelocation) and assert IT was
        # called once — proving the cutover script reached the next
        # step instead of throwing on a vacuous residual count.
        Mock Invoke-IriRdfRelocation    { }
        Mock Invoke-IriShardRewrite     { }
        Mock Assert-AllMigrationManifests { }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:ValidRecord -IriDryRun } |
            Should Not Throw
        Assert-MockCalled Invoke-IriRdfRelocation -Times 1
    }
}

# ---------------------------------------------------------------------
# Helper: build a synthetic SQL/RDF/blob manifest triple + the
# matching cutover record. The tests below construct manifests with
# different content shapes to exercise the content-validating
# Assert-AllMigrationManifests gate.
# ---------------------------------------------------------------------

function New-ManifestFixtures {
    [CmdletBinding()]
    param(
        [hashtable]$SqlOverrides   = @{},
        [hashtable]$RdfOverrides   = @{},
        [hashtable]$BlobOverrides  = @{},
        [hashtable]$RecordOverrides = @{},
        [switch]$IncludeMinIOBlock,
        [switch]$OmitRecordSha
    )

    # Canonical placeholder SHA-256 used for the SHA-chain checks.
    $zeroSha = '0' * 64
    $tableRow = @{ Table = 'document'; RowCount = 1; OrphanCount = 0; BusinessChecksum = $zeroSha.Substring(0,32) }

    $sqlBase = [ordered]@{
        StartedAt = '2026-08-18T10:00:00+00:00'
        FinishedAt = '2026-08-18T10:05:00+00:00'
        Steps = @(@{ FileName = '001.sql'; AppliedAt = '2026-08-18T10:01:00+00:00'; Checksum = $zeroSha })
        VerifySummary = @{ Rows = @($tableRow) }
    }
    foreach ($k in $SqlOverrides.Keys) { $sqlBase[$k] = $SqlOverrides[$k] }

    $rdfBase = [ordered]@{
        Strategy = 'direct'
        QuadCount = 10
        NamedGraphs = @('urn:ontopilot:test:tbox')
        QueryResultHashes = @{ 'all-quads' = $zeroSha; 'tbox-only' = $zeroSha }
        WriteRevertPassed = $true
    }
    foreach ($k in $RdfOverrides.Keys) { $rdfBase[$k] = $RdfOverrides[$k] }

    $blobBase = [ordered]@{
        version = '1.0.0'
        sourceDirectory = '/var/lib/ontopilot/blobs'
        bucket = 'ontopilot-blobs'
        generatedAtUtc = '2026-08-18T10:00:00+00:00'
        entries = @(@{
            sourcePath = 'ab/cd/abcdef0123456789abcdef0123456789abcdef0123456789abcdef01234567'
            objectKey  = 'ab/cd/abcdef0123456789abcdef0123456789abcdef0123456789abcdef01234567'
            size       = 42
            sha256     = 'abcdef0123456789abcdef0123456789abcdef0123456789abcdef01234567'
            referenceCount = 1
        })
    }
    foreach ($k in $BlobOverrides.Keys) { $blobBase[$k] = $BlobOverrides[$k] }

    $sqlPath  = Join-Path $TestDrive 'sql-migration-log.json'
    $rdfPath  = Join-Path $TestDrive 'rdf-manifest.json'
    $blobPath = Join-Path $TestDrive 'blob-manifest.json'
    $sqlBase  | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $sqlPath  -Encoding utf8
    $rdfBase  | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $rdfPath  -Encoding utf8
    $blobBase | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $blobPath -Encoding utf8

    # Cutover record that lists every expected reference value.
    $recordBody = @"
# Production Cutover Record

- Cutover start (UTC): 2026-08-18T10:00:00Z
- Backup path: /var/backups/ontopilot/2026-08-18
- Backup SHA-256: $zeroSha
- Operator signature: Test Operator
- MinIO endpoint: http://127.0.0.1:9000
- MinIO bucket: ontopilot-blobs
- Expected post-cutover manifest checksums:
  - SQL verify summary: $zeroSha
  - RDF verify summary: $zeroSha
  - blob verify summary: $zeroSha
- expected-sql-manifest-sha256: $zeroSha
- expected-rdf-manifest-sha256: $zeroSha
- expected-blob-manifest-sha256: $zeroSha
- expected-sql-checksums:
  - document = $($tableRow.BusinessChecksum)
- expected-rdf-query-hashes:
  - all-quads = $zeroSha
  - tbox-only = $zeroSha
"@
    foreach ($k in $RecordOverrides.Keys) {
        # Allow tests to overwrite individual lines by replacing the placeholder marker.
        $recordBody = $recordBody.Replace("__$k__", $RecordOverrides[$k])
    }
    $recordPath = Join-Path $TestDrive 'cutover-record.md'
    Set-Content -LiteralPath $recordPath -Value $recordBody -Encoding utf8

    return [pscustomobject]@{
        RecordPath = $recordPath
        SqlPath    = $sqlPath
        RdfPath    = $rdfPath
        BlobPath   = $blobPath
    }
}

Describe 'manifest content validation: validates content not just existence' {

    It 'validates manifest content not just existence' {
        $fixtures = New-ManifestFixtures `
            -SqlOverrides  @{ VerifySummary = @{ Rows = @(@{ Table = 'document'; RowCount = 1; OrphanCount = 1; BusinessChecksum = ('0' * 32) }) } } `
            -RdfOverrides  @{ WriteRevertPassed = $false } `
            -BlobOverrides @{ entries = @(@{
                sourcePath = 'ab/cd/abcdef0123456789abcdef0123456789abcdef0123456789abcdef01234567'
                objectKey  = 'ab/cd/abcdef0123456789abcdef0123456789abcdef0123456789abcdef01234567'
                size       = 42
                sha256     = 'abcdef0123456789abcdef0123456789abcdef0123456789abcdef012345'  # 63 chars
                referenceCount = 1
            }) }

        $threw = $null
        try {
            Assert-AllMigrationManifests `
                -Record $fixtures.RecordPath `
                -SqlManifestPath $fixtures.SqlPath `
                -RdfManifestPath $fixtures.RdfPath `
                -BlobManifestPath $fixtures.BlobPath `
                -MinioEndpoint 'http://127.0.0.1:9000' `
                -MinioBucket 'ontopilot-blobs'
        } catch {
            $threw = $_
        }
        $threw | Should Not BeNullOrEmpty
        $threw.Exception.Message | Should Match 'OrphanCount'
        $threw.Exception.Message | Should Match 'WriteRevertPassed'
        $threw.Exception.Message | Should Match 'malformed SHA-256'
    }
}

Describe 'manifest content validation: matches expected checksums from cutover record' {

    It 'matches expected checksums from cutover record' {
        # Override the record's expected SHA-256s to the actual
        # canonical SHAs of the freshly-serialised manifests so the
        # SHA-chain gate passes.
        $fixtures = New-ManifestFixtures
        $sqlSha  = (Get-CanonicalManifestSha -Path $fixtures.SqlPath)
        $rdfSha  = (Get-CanonicalManifestSha -Path $fixtures.RdfPath)
        $blobSha = (Get-CanonicalManifestSha -Path $fixtures.BlobPath)
        $content = Get-Content -LiteralPath $fixtures.RecordPath -Raw
        $content = $content.Replace(('0' * 64), $sqlSha, 1)
        $content = $content.Replace(('0' * 64), $rdfSha, 1)
        $content = $content.Replace(('0' * 64), $blobSha, 1)
        Set-Content -LiteralPath $fixtures.RecordPath -Value $content -Encoding utf8

        # The blob manifest's per-object MinIO HEAD check needs
        # Test-MinioObjectExists to be mockable for this unit test.
        # We don't run a real MinIO here; the gate throws on MinIO
        # failure, so we expect a MinIO-side error. To isolate the
        # checksum happy path, mock Test-MinioObjectExists to
        # return true.
        Mock Test-MinioObjectExists { $true }

        { Assert-AllMigrationManifests `
                -Record $fixtures.RecordPath `
                -SqlManifestPath $fixtures.SqlPath `
                -RdfManifestPath $fixtures.RdfPath `
                -BlobManifestPath $fixtures.BlobPath `
                -MinioEndpoint 'http://127.0.0.1:9000' `
                -MinioBucket 'ontopilot-blobs' } | Should Not Throw
    }
}

Describe 'manifest content validation: rejects manifest sha256 mismatch' {

    It 'rejects manifest sha256 mismatch' {
        $fixtures = New-ManifestFixtures
        # Keep the record's expected-* SHA values at the zero
        # placeholder — they will NOT match the actual canonical
        # SHAs of the freshly-serialised manifests.

        Mock Test-MinioObjectExists { $true }

        $threw = $null
        try {
            Assert-AllMigrationManifests `
                -Record $fixtures.RecordPath `
                -SqlManifestPath $fixtures.SqlPath `
                -RdfManifestPath $fixtures.RdfPath `
                -BlobManifestPath $fixtures.BlobPath `
                -MinioEndpoint 'http://127.0.0.1:9000' `
                -MinioBucket 'ontopilot-blobs'
        } catch {
            $threw = $_
        }
        $threw | Should Not BeNullOrEmpty
        $threw.Exception.Message | Should Match 'SQL manifest SHA-256 mismatch'
    }
}