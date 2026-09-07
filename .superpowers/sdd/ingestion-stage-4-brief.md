# Ingestion Stage 4 Brief

## Goal

Add the smallest application processor for parser invocation. A Stage 3
`plain_text` extraction job supplies the knowledge system, document, and model;
the processor retrieves the content-addressed blob, invokes `IDocumentParser`,
and delegates parsed text to `PlainTextIngestionService`.

## Scope

- Preserve Stage 3 extraction-job ownership and `kind=plain_text` checks.
- Load the document by id and require matching knowledge-system ownership before
  reading a blob or writing versions/chunks.
- Read `DocumentEntity.Sha256` through `IBlobStore.GetAsync` and dispose the
  returned stream after synchronous parser invocation.
- Persist parser backend, Unicode scalar/code-point text length, chunk count,
  and parse status on success.
- Record parse errors and failed, retryable jobs for missing blobs and parser
  failures; version/chunk persistence remains owned by `DocumentVersionStore`.
- Register the processor in the existing document service DI boundary.

## Non-goals

No Rust, graph-core, migrations, frontend, HTTP API, queue worker, source
connector, or new parser/blob abstraction.

## Acceptance Evidence

PostgreSQL/Testcontainers integration tests cover txt blob parsing and metadata,
missing blob, unsupported extension, duplicate delivery idempotency, and
cross-knowledge-system isolation. The test fixture uses the existing
`LocalCasBlobStore`, `DocumentParser`, and `DocumentVersionStore` contracts.
