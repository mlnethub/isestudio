# Task 3 Fix Report: Search Isolation and Version Semantics

## Findings fixed

- Missing `ActorId` now rejects `SearchRequest` construction with `UnauthorizedAccessException`; SQL no longer contains a nullable-actor bypass.
- PostgreSQL search ranks versions per `DocumentId` and returns only the newest visible version, using `AsOf` as the knowledge-system snapshot time when supplied.
- `SearchHit` now includes `DocumentVersionId`.
- Application capability naming is provider-neutral through `SearchCapabilities.NoVectorSearch`.
- Removed the unrelated legacy Graph Task 3 content from `task-3-report.md`.

## Tests added or updated

- Unit test for missing actor rejection and provider-neutral capability naming.
- Integration tests for missing actor, same-document historical/current versions with and without `AsOf`, version identity, and empty results.
- Existing tenant, grant, time-snapshot, vector capability, ranking, and pagination coverage retained.

## Verification

- `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~PostgresSearchIndexTests --no-restore`
  - 4 passed, 0 failed.
- `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~PostgresSearchIntegrationTests --no-restore`
  - 7 passed, 0 failed.
- `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~Persistence --no-restore`
  - 9 passed, 0 failed.
- `git diff --check`: passed.
