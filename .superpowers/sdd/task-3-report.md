# Task 3 Report: Provider-Neutral PostgreSQL Search

## Implemented

- Added provider-neutral `ISearchIndex`, `SearchRequest`, `SearchHit`, and `SearchCapabilities` contracts under `ISEStudio.Application.Search`.
- Added `PostgresSearchIndex` using parameterized PostgreSQL full-text SQL over immutable `document_version_chunk` rows.
- Added owner/grant permission filtering, knowledge-system scoping, `AsOf` version filtering, deterministic rank plus chunk-id ordering, and limit/offset pagination.
- Registered `ISearchIndex` in the single ASP.NET host through `AddPostgresSearch`.
- Added a migration with a PostgreSQL GIN `to_tsvector('simple', text)` index and a version search-scope index.
- Added unit/contract tests and Testcontainers PostgreSQL integration tests.

## Verification

- `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~PostgresSearchIndexTests --no-restore`
  - 4 passed, 0 failed.
- `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~PostgresSearchIntegrationTests --no-restore`
  - 7 passed, 0 failed, PostgreSQL 16 Testcontainers.
- `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~Persistence --no-restore`
  - 9 passed, 0 failed.
- `git diff --check`
  - Passed.

## Schema and package assumptions

- Existing document version and version-chunk tables are the authoritative searchable text store.
- Existing schema has no embedding/vector column and the projects have no pgvector package or configured vector provider.
- Existing migrations use mixed naming: version tables use explicit snake_case while several legacy tables retain EF default PascalCase columns. The SQL follows the applied schema.
- No pgvector extension, vector column, or synthetic vector scores were added. A vector request reports `SupportsVectorSearch == false` and throws `NotSupportedException`.
- No Lucene, Tantivy, Rust runtime, or secondary graph/search store was introduced.

## Residual risks

- Search currently requires PostgreSQL FTS-compatible query text and uses the `simple` configuration; language-specific stemming is not configured.
- Vector candidate retrieval remains an explicit optional capability until an embedding schema, storage contract, and package are approved.
- Permission semantics currently use knowledge-system owner or `ksgrant` membership; future row-level permission concepts would need an application contract extension.

## Scope boundary

- No Graph Task 3 content is included in this report.

End of report.
