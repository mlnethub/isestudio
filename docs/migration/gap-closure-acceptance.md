# Migration rehearsal acceptance

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
`restored` requires a non-empty, recognizable PostgreSQL plain/custom dump
file and a supplied database that already contains restored application
tables. The command does not invent provider-specific restore orchestration or
mutate production; the operator must perform restore before the rehearsal.

`upgrade` requires non-empty existing EF migration history and at least one
pending migration before it runs `MigrateAsync`; a current database is
rejected rather than reported as an upgrade. The manifest records migration
before/after SQL evidence (row counts, business checksums, FK orphan counts)
and graph fact/evidence counts. Connection strings and secrets are never
written to the manifest.

Record the commit, PostgreSQL image/version, command duration, manifest SHA-256,
and any skipped optional RDF/blob inputs alongside the generated manifests.
External MinIO, provider backup-restore, and production stop-write gates were
not run in this workspace. The PostgreSQL fresh path is covered by a real
Testcontainers rehearsal; restored and upgrade require externally prepared
database state and artifacts before they can be accepted.