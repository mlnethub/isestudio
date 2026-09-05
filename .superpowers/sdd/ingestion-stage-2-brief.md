# Ingestion Stage 2 Brief

## Goal

Provide one application boundary that accepts already-available plain text,
derives a canonical content identity, deterministically chunks the text, and
records the immutable document version and all version-owned chunks.

## Input and Output

- Input: knowledge-system ID, document ID, and the complete plain-text content.
- Output: the existing `DocumentVersionResult`, including the canonical
  lowercase SHA-256 and persisted chunk count.
- SHA-256 is computed from the UTF-8 bytes of the exact supplied string.
- Chunking reuses the existing paragraph-aware `Chunker` with the document
  defaults, preserving order and emitting absolute Unicode scalar/code-point
  character offsets and token estimates.

## Scope

- Add a small application service that composes hashing, `Chunker`, and the
  existing transactional `DocumentVersionStore`.
- Verify idempotency for repeated content, new versions for changed content,
  deterministic chunk text/order/metadata, knowledge-system isolation, and
  atomic rollback through real PostgreSQL tests.
- Keep Stage 1's immutable version/chunk persistence and SHA validation as the
  sole write path.

## Non-goals

- No source connector, object storage, upload flow, parser invocation, LLM
  extraction, background job, HTTP/API, frontend, Rust, or graph-core change.
- No current mutable document-chunk flow change.
- No migration is required because Stage 1 already stores the required chunk
  metadata and transaction constraints.

## Acceptance Criteria

- Same document and same text return the same version and do not duplicate
  versions or chunks.
- Changed text creates a new version while retaining the prior immutable one.
- Chunking is deterministic, ordered, rerunnable, and tests cover
  `char_start`, `char_end`, and `token_estimate` for ASCII and mixed CJK/non-BMP
  text without splitting surrogate pairs.
- Version and every chunk are committed atomically; a failed chunk write leaves
  neither a version nor its chunks.
- Existing knowledge-system isolation, lowercase SHA-256 normalization, and
  Stage 1 graph/ingestion behavior remain intact.
