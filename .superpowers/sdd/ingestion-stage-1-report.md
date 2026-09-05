# Ingestion Stage 1 Report

## Delivered Slice
Added a transactional `DocumentVersionStore` that records an immutable document content snapshot and its chunks. Version identity is scoped by knowledge system, document, and content SHA-256. Chunk rows reference exactly one version and are protected by a unique version/index constraint.

## RED/GREEN Evidence
- RED: the new integration tests initially failed to compile because the version store, contracts, DbSets, and persistence model did not exist.
- GREEN: `dotnet test src\\ISEStudio.IntegrationTests\\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~DocumentVersionStoreTests --no-restore`
- Result: 4 passed, 0 failed.
- The tests use Testcontainers PostgreSQL 16 and cover idempotency, chunk ownership, cross-knowledge-system rejection, transaction rollback, and the physical schema contract.

## Migration Review
`AddDocumentVersions` was removed and regenerated with EF Core after explicit snake_case column mappings were added. The reviewed migration creates only `document_version` and `document_version_chunk`, adds the two required unique indexes, uses Restrict for knowledge-system/document references, and Cascade for version-to-chunk cleanup. The down migration removes only these new tables.

## Boundaries
No frontend, Rust, HTTP API, object-store, parser, extraction-job, mutable chunk, or graph-core behavior was changed. The store is intentionally a narrow application-facing class in the existing single-host architecture; no unused service registration or API surface was introduced.

## Remaining Risk
Concurrent writers that race past the transactional re-check can still surface the database unique violation rather than reload the winning version. The current slice guarantees database-level idempotency and sequential idempotency; concurrent retry policy belongs to the ingestion orchestration layer.

## Commit
Implementation commit is recorded in git history after final validation.
