# Task 3 Fix 2 Report: Cross-Tenant Search Document Isolation

## Root cause

`PostgresSearchIndex` scoped `document_version` rows by `document_version.knowledge_system_id`, but joined the selected version's `document_id` to `document` without checking that the document belonged to the same knowledge system. A malformed cross-KS version/document relationship could therefore return another tenant's document text and SHA.

## Fix

- Added `document."KnowledgeSystemId" = ranked_versions.knowledge_system_id` to the existing document join in `PostgresSearchIndex`.
- Added a PostgreSQL Testcontainers regression case with a target-KS version pointing at a document owned by another KS; the target owner now receives no result.
- Retained and exercised normal owner, grant, unauthorized, other-tenant, latest-version, pagination, ranking, and `AsOf` behavior.
- Added an assertion that an `AsOf` timestamp earlier than the earliest document version returns an empty result.
- No migration or schema change was added. The existing schema does not enforce a cross-table composite ownership invariant, and the query-level equality predicate is the smallest compatible fix for historical migrations.

## Verification

- `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~PostgresSearchIndexTests --no-restore`
  - 4 passed, 0 failed; existing build/analyzer warnings only.
- `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~PostgresSearchIntegrationTests --no-restore`
  - 8 passed, 0 failed; PostgreSQL 16 Testcontainers.
- `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~Persistence --no-restore`
  - 5 passed, 0 failed.
- `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~Persistence --no-restore`
  - 9 passed, 0 failed; PostgreSQL Testcontainers.
- `git diff --check`
  - Passed.

## Residual risks

- The database still permits malformed cross-KS `document_version` rows; all search paths using this SQL are protected, but other future consumers of the raw version tables must apply the same ownership invariant.
- Search continues to use PostgreSQL `simple` full-text search and owner/`ksgrant` authorization semantics; these are existing Task 3 limitations, not changed by this fix.

## Scope boundary

Only Task 3 search code, its PostgreSQL/unit regression tests, and this report were changed. No frontend, Rust, or generated `bin`/`obj` files were modified.
