# Ingestion Stage 3 Report

## Result

Stage 3 is implemented and verified against real PostgreSQL containers.

## Implementation

- Added `PlainTextIngestionJobProcessor` with the `plain_text` job kind.
- Reused `PlainTextIngestionService` for ownership checks, hashing, version
  idempotency, chunking, and transactional persistence.
- Recorded `pending`/`running`/`completed`/`failed` job observability through
  `ExtractionJobEntity`, including retryable failure behavior.
- Stage 3 review found that an existing job ID was accepted without checking
  its knowledge-system ownership or `plain_text` kind; the processor could
  therefore rewrite another job and mark it failed on a later error.
- Fixed `PlainTextIngestionJobProcessor` to validate both fields before any
  state or metadata mutation. Mismatches now throw clear exceptions and leave
  the existing job row unchanged.
- Registered the processor at the document service DI boundary.
- Added real PostgreSQL integration coverage for cross-system and non-
  `plain_text` existing jobs, asserting their `KnowledgeSystemId`, `Kind`,
  `Status`, `Error`, and `Model` remain unchanged.
- RED evidence: the new test failed before the fix because no exception was
  thrown for an existing cross-system job.
- GREEN evidence: the same test passed after the pre-mutation validation was
  added.

## Verification

- `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~PlainTextIngestionServiceTests --no-restore`
  - 8 passed, 0 failed.
- `dotnet test src/ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~Ingestion --no-restore`
    - 16 passed, 0 failed.
- `git diff --check`
  - Passed.

## Remaining Risk

The processor is intentionally limited to already-created jobs and complete
plain-text content. Queue execution, source acquisition, parsing, and HTTP
submission remain outside Stage 3.