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

## Unicode Defect Review

The initial Stage 2 slice treated .NET UTF-16 string lengths and indexes as
character offsets. A non-BMP scalar at a hard chunk boundary could therefore
be split into two invalid surrogate fragments, while `char_start`/`char_end`
and `token_estimate` counted UTF-16 code units rather than Unicode scalars.

The fix keeps the existing paragraph/sentence algorithm and ASCII behavior,
but maps Unicode scalar indexes to safe UTF-16 slice boundaries. Public chunk
offsets and size budgets now use scalar/code-point counts. `TokenEstimator`
walks `Rune` values so each CJK character and emoji contributes one token under
the existing rule, while ASCII runs retain the four-characters-per-token
estimate. PostgreSQL coverage now verifies the corrected metadata after
persistence for mixed CJK and emoji text.

## Validation

- Stage 2 narrow tests: 3 passed, 0 failed (including the Unicode regression).
- All ingestion/document integration tests: 11 passed, 0 failed.
- Chunker parity and focused parsing tests: 10 passed, 0 failed.
- Graph integration tests: 25 passed, 0 failed.
- Host project build: `src\\ISEStudio\\ISEStudio.csproj` succeeded.
- `git diff --check`: passed.
- The broader `ISEStudio.Tests` document filter still has 31 pre-existing
  SQLite migration failures (`near "~": syntax error`) during test-host setup;
  no failure reaches the changed Chunker or TokenEstimator code.

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
