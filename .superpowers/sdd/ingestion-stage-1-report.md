# Ingestion Stage 1 Report

## Delivered Slice
Added a transactional `DocumentVersionStore` that records an immutable document content snapshot and its chunks. Version identity is scoped by knowledge system, document, and content SHA-256. Chunk rows reference exactly one version and are protected by a unique version/index constraint.

## RED/GREEN Evidence
- RED: the independent review found that concurrent writers could surface a unique-key exception, document/version ownership was enforced only by separate foreign keys, version rows and chunks were mutable, and SHA input was not normalized or format-checked. New PostgreSQL tests reproduced the missing database protections and the uppercase/lowercase idempotency gap.
- GREEN: `dotnet test src\\ISEStudio.IntegrationTests\\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~ISEStudio.IntegrationTests.Ingestion.DocumentVersionStoreTests --no-restore`
- Result: 8 passed, 0 failed.
- The tests use Testcontainers PostgreSQL 16 and cover real concurrent writes, winner reload after `ON CONFLICT DO NOTHING`, cross-knowledge-system direct SQL rejection, immutable version/chunk UPDATE and DELETE rejection, lowercase SHA normalization, invalid SHA rejection, chunk ownership, sequential idempotency, and transaction rollback.

## Migration Review
`HardenDocumentVersions` adds the `(document.id, document.knowledge_system_id)` alternate key and composite version foreign key, a 64-character lowercase SHA column plus CHECK constraint, and PostgreSQL triggers/functions that reject UPDATE/DELETE on versions and version chunks. Its Down migration drops triggers, function, foreign key, alternate key, CHECK, and restores the prior column shape. A generated follow-up migration records the required composite-FK index.

## Boundaries
No frontend, Rust, HTTP API, object-store, parser, extraction-job, mutable chunk, or graph-core behavior was changed. The store is intentionally a narrow application-facing class in the existing single-host architecture; no unused service registration or API surface was introduced.

## Review Findings Fixed
- Concurrent same-input calls now use one transaction with `INSERT ... ON CONFLICT DO NOTHING`, then query and return the committed winner without treating the unique violation as normal control flow.
- Document/version knowledge-system mismatch is rejected by a composite database foreign key, independently of the C# store.
- Database triggers make version and version-owned chunk rows immutable for direct SQL clients as well as EF callers.
- Store input is normalized to lowercase and rejected unless it is exactly 64 hexadecimal characters; the database column and CHECK provide a second line of defense.

## Remaining Risk
Later ingestion stages are not implemented. Existing historical data with non-canonical version SHA values must be normalized or rejected when this migration is applied; the migration intentionally does not silently invent hashes for invalid data. The current scope does not change mutable current-document chunk flow or graph-core behavior.

## Commit
Implementation commit is recorded in git history after final validation.
