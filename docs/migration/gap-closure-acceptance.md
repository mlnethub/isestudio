# Migration rehearsal acceptance

Task 4 is accepted only when all three modes produce a JSON manifest and
`scripts/migration/Test-MigrationGate.ps1` exits zero.

## Commands

```powershell
powershell -File scripts\migration\Invoke-MigrationRehearsal.ps1 -Mode fresh -Manifest .artifacts\rehearsal-fresh.json -PostgresConnectionString $env:ISESTUDIO_POSTGRES
powershell -File scripts\migration\Invoke-MigrationRehearsal.ps1 -Mode restored -Manifest .artifacts\rehearsal-restored.json -PostgresConnectionString $env:ISESTUDIO_POSTGRES -Backup .artifacts\verified-backup.dump
powershell -File scripts\migration\Invoke-MigrationRehearsal.ps1 -Mode upgrade -Manifest .artifacts\rehearsal-upgrade.json -PostgresConnectionString $env:ISESTUDIO_POSTGRES
```

The database must be PostgreSQL 16 or later with the application role and
extensions required by the checked-in EF migrations. `restored` expects the
operator to restore the verified backup into the supplied PostgreSQL database;
the command verifies the backup artifact is present and rehearses the restored
database, but does not invent a cloud backup provider or mutate production.

Record the commit, PostgreSQL image/version, command duration, manifest SHA-256,
and any skipped optional RDF/blob inputs alongside the generated manifests.
External Docker, MinIO, backup-restore, and production stop-write gates were
not run unless their environment was explicitly supplied.