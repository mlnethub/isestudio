# Ingestion Stage 1 Brief

## Goal
Persist an immutable source-document version and its version-owned chunks with idempotency scoped by knowledge system and document.

## Scope
- Add application input/result contracts for recording a document version.
- Add EF Core persistence entities and PostgreSQL migration.
- Enforce `(knowledge_system_id, document_id, content_sha256)` idempotency.
- Enforce unique `(document_version_id, idx)` chunk ownership.
- Save one version and all chunks in one transaction.
- Reject a document ID that is not owned by the requested knowledge system.

## Non-goals
- No HTTP/API or frontend changes.
- No Rust changes.
- No object-store, parser, extraction-job, or current mutable chunk-flow changes.
- No graph-core behavior changes.
- No completion claim for later ingestion stages.

## Acceptance Criteria
- Repeating the same version input returns the same version and does not duplicate chunks.
- A document cannot be versioned through another knowledge system.
- A chunk uniqueness failure leaves neither the version nor its chunks persisted.
- PostgreSQL migration uses the repository's snake_case naming and expected FK delete behavior.
- Existing graph-core tests remain green.
