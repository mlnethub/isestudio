# Migration Gap Closure Acceptance

## Scope

This record covers the .NET migration rehearsal command and its manifest gate.
PostgreSQL remains the authoritative store. A production cutover still requires
explicit stop-write authorization, an operator-verified backup, and a reversible
rollback plan.

## Evidence

| Check | Result | Evidence |
| --- | --- | --- |
| Focused rehearsal tests | 17 passed, 0 failed | `MigrationRehearsalTests` and `PostgreSqlMigrationRehearsalTests` |
| Broader migration integration suite | 47 passed, 0 failed | `ISEStudio.IntegrationTests`, filter `FullyQualifiedName~Migration` |
| Persistence migration suite | 10 passed, 0 failed | `ISEStudio.Tests`, filter `FullyQualifiedName~Migration` |
| Migration project strict build | Passed | `dotnet build ... -warnaserror` |
| Canonical checksum regression | Passed | delimiter and NULL/empty-value collision fixture |
| Backup digest resource bound | Passed | streamed SHA-256 and byte-count validation |
| Restore validator failure modes | Passed | missing executable and timeout/process-tree cancellation tests |
| Upgrade marker contract | Passed | schema, source migration, target schema, and actual history checks |
| Gate smoke | Passed | valid fixture accepted; `Passed`, report checksum, and step metadata tampering rejected |
| PowerShell syntax | Passed | gate, gate smoke, and rehearsal wrapper AST parsing |

## Reproducible commands

```powershell
dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter 'FullyQualifiedName~MigrationRehearsalTests|FullyQualifiedName~PostgreSqlMigrationRehearsalTests' --no-restore --logger 'console;verbosity=minimal'
pwsh -NoProfile -File scripts\migration\Test-MigrationGateSmoke.ps1
```

The focused suite uses PostgreSQL 16 Testcontainers and completed successfully in
this workspace. The smoke script invokes `Test-MigrationGate.ps1` as a subprocess
for both acceptance and tamper-rejection cases.

## Unverified external evidence

- `pg_restore` is not installed on this workstation. The actual dump-list happy path
  therefore remains unverified locally; missing executable and timeout behavior are
  covered by tests using injected executable paths.
- No production database, provider-specific backup restore, MinIO cutover, or
  stop-write operation was run here.
- Restored and upgrade production rehearsals require an operator-provided database,
  backup artifact, migration history, and credentials. Those inputs must be recorded
  with the resulting manifest checksum before cutover acceptance.

## Residual risk

The rehearsal command verifies a backup artifact and the state of a restored target;
it does not implement provider-specific restore orchestration. RDF and blob inputs
are optional in the command and are recorded as skipped when omitted, so a production
runbook must provide them before treating a manifest as complete acceptance evidence.# Migration rehearsal acceptance

Task 4 is accepted only when a mode's JSON manifest satisfies its explicit
preconditions and `scripts/migration/Test-MigrationGate.ps1` exits zero.
Omitted RDF or blob inputs are recorded as `skipped`, which makes
`Manifest.Passed` false; they are never silently accepted as passed.

## Commands

```powershell
powershell -File scripts\migration\Invoke-MigrationRehearsal.ps1 -Mode fresh -Manifest .artifacts\rehearsal-fresh.json -PostgresConnectionString $env:ISESTUDIO_POSTGRES
powershell -File scripts\migration\Invoke-MigrationRehearsal.ps1 -Mode restored -Manifest .artifacts\rehearsal-restored.json -PostgresConnectionString $env:ISESTUDIO_POSTGRES -Backup .artifacts\verified-backup.dump
powershell -File scripts\migration\Invoke-MigrationRehearsal.ps1 -Mode upgrade -Manifest .artifacts\rehearsal-upgrade.json -PostgresConnectionString $env:ISESTUDIO_POSTGRES
```

The database must be PostgreSQL 16 or later with the application role and
extensions required by the checked-in EF migrations. `fresh` requires an
empty public schema with no application tables or EF migration history.
`restored` requires a non-empty backup file accepted by `pg_restore --list` (or
an injected validator in tests), a non-empty supplied database containing
restored application tables, and records backup size/SHA-256 evidence. The
manifest explicitly labels restore verification `external/manual`. The command
does not invent provider-specific restore orchestration or mutate production;
the operator must perform restore before the rehearsal. Missing `pg_restore`, a
non-listable artifact, or an empty target fails closed.

`upgrade` requires non-empty existing EF migration history, at least one
pending migration, and an explicit `__migration_rehearsal_upgrade_marker`
table containing a `pre-upgrade` row before it runs `MigrateAsync`; a current
database or an unmarked database is rejected rather than reported as an
upgrade. The manifest records and compares migration before/after SQL evidence
(row counts, business checksums, FK orphan counts) and graph fact/evidence
content checksums. Connection strings and secrets are never written to the
manifest.

Record the commit, PostgreSQL image/version, command duration, manifest SHA-256,
and any skipped optional RDF/blob inputs alongside the generated manifests.
External MinIO, provider backup-restore, and production stop-write gates were
not run in this workspace. The PostgreSQL fresh path is covered by a real
Testcontainers rehearsal; restored and upgrade require externally prepared
database state and artifacts before they can be accepted.

## Benchmark Baseline

The versioned benchmark fixture is `graph-search-ingestion.v1.json` with 10,000
facts, 5,000 document chunks, eight concurrent writers, and traversal depth 4.
The benchmark runner measures graph traversal, PostgreSQL FTS search, batch
ingestion, and concurrent graph writes. The production-scale baseline was
recorded on 2026-09-08 with Docker Desktop 29.2.0 and PostgreSQL 16. All four
paths reported zero errors, and a subsequent non-record run passed stable-hash,
p95, throughput, and error-count gates. Current recorded p95 values are about
65.78 ms for concurrent writes, 42.77 ms for traversal, 49.88 ms for ingestion,
and 292.51 ms for FTS search. Exact values and hashes are in
`graph-search-ingestion-baseline.json`.

```powershell
powershell -File scripts\bench\graph-search-ingestion.ps1 -Baseline docs\migration\graph-search-ingestion-baseline.json -Output .artifacts\graph-search-ingestion.json
```