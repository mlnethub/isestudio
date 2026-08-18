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
        Mock Test-AllMigrationManifests  { $true }
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
        Mock Test-AllMigrationManifests  { $true }
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
        Mock Test-AllMigrationManifests  { $true }
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
        Mock Test-AllMigrationManifests  { $true }
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
        Mock Test-AllMigrationManifests  { $true }
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
        Mock Test-AllMigrationManifests  { $true }
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
        Mock Test-AllMigrationManifests  { $true }
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
        Mock Test-AllMigrationManifests  { throw 'blob checksum mismatch' }
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
        Mock Test-AllMigrationManifests  { $true }
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
        Assert-MockCalled Test-AllMigrationManifests  -Times 1
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
        Mock Test-AllMigrationManifests  { $true }
        Mock Start-DotNetBackend        { }
        Mock Invoke-PostCutoverSmoke    { }

        { & Invoke-ProductionCutover -Record $script:BadRecord } |
            Should Throw 'Cutover record'
    }
}