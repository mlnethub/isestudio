# Ingestion Stage 2 Report

## Delivered Slice

Added `PlainTextIngestionService`, a narrow application boundary for complete
plain text. It computes the lowercase SHA-256 over the exact UTF-8 content,
reuses the existing deterministic paragraph-aware `Chunker`, maps chunk spans
including character offsets and token estimates, and delegates persistence to
the transactional Stage 1 `DocumentVersionStore`.

## RED/GREEN Evidence

- RED: new real PostgreSQL/Testcontainers tests initially failed because the
  application service did not exist.
- GREEN: the Stage 2 narrow test passed with 2 tests and 0 failures.
- The tests cover SHA-256 identity, ordered chunk text, `char_start`,
  `char_end`, `token_estimate`, repeated-content idempotency, and changed
  content producing a new version without changing prior chunks.

## Validation

- Stage 2 narrow tests: 2 passed, 0 failed.
- All ingestion/document tests: 10 passed, 0 failed.
- Graph integration tests: 25 passed, 0 failed.
- Host project build: `src\\ISEStudio\\ISEStudio.csproj` succeeded.
- `git diff --check`: passed.

## Migration and Transaction Review

No migration was required. Stage 1 already persists all required chunk
metadata and enforces immutable version-owned chunks. Stage 2 does not write
around that store, so version and all chunks remain one atomic transaction;
Stage 1's PostgreSQL rollback test remains green.

## Boundaries and Non-goals

No frontend, Rust, graph-core behavior, source connector, object storage,
parser invocation, LLM extraction, background job, HTTP/API, or mutable
current-document chunk flow was changed. The service accepts only already
available plain text.

## Remaining Risk

The slice intentionally does not connect uploaded bytes or parser output to
this boundary. A later stage must choose and test that orchestration without
bypassing `DocumentVersionStore`.
