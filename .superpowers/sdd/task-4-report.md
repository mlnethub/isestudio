# Task 4 report: migration rehearsal gates

## Implemented

- Added `MigrationRehearsalCommand` and serializable `MigrationRehearsalManifest`.
- Fixed step order: PostgreSQL connectivity, EF migrations, schema assertions,
  SQL snapshot, RDF copy, blob manifest, graph read-only counts, and report
  serialization.
- A failed step stops subsequent steps. The CLI returns `1` for a failed run.
- Reused `ISEStudioDbContext.Database.MigrateAsync`, `SqlSnapshot`, and
  `RdfMigrationCommand`; the blob step verifies an existing blob manifest
  without duplicating blob migration logic.
- Added Windows PowerShell wrappers and an exact step/status gate.

## Verification output

- `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~MigrationRehearsalTests --no-restore`: 2 passed.
- `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~Migration --no-restore`: 32 passed, including Docker PostgreSQL tests.
- `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~Migration --no-restore`: 10 passed.
- `dotnet build src\ISEStudio.Migration\ISEStudio.Migration.csproj --no-restore -warnaserror`: passed.
- CLI without a PostgreSQL connection produced a failed manifest; rehearsal exit code and `Test-MigrationGate.ps1` exit code were both `1`.

## Preconditions and unverified external steps

Live fresh, restored, and upgrade rehearsal runs require a PostgreSQL 16+
database reachable with the application schema/migrations available. Restored
mode additionally requires an operator-verified backup artifact already
restored into the supplied database. RDF verification requires source, copy,
and work directories. Blob verification requires a generated blob manifest.
No production database, cloud backup provider, MinIO cutover, or stop-write
operation was run in this workspace.

## Residual risks

- Backup restore orchestration remains deliberately external and manual; the
  command verifies the artifact and restored database rather than inventing a
  provider-specific restore API.
- Optional RDF/blob inputs are recorded as skipped when omitted, so production
  runbooks must supply them before treating a manifest as acceptance evidence.
- Existing unrelated test warnings remain in `ISEStudio.Tests`.