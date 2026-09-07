# Ingestion Stage 4 Report

## Result

Completed the parser invocation slice with `DocumentIngestionJobProcessor` and
`DocumentIngestionJob` / result records.

The processor preserves the Stage 3 `plain_text` job ownership contract,
rejects cross-knowledge-system documents before blob access, reads blobs by the
document SHA, invokes `IDocumentParser.Parse`, and delegates parsed text to
`PlainTextIngestionService`. Successful parsing updates `ParseStatus`, parser
backend, Unicode scalar text count, and chunk count before completing the job.
Blob and parser failures mark the owned document and job failed; the job remains
retryable. Existing version/chunk transaction and idempotency behavior is
unchanged.

## Tests

`DocumentIngestionJobProcessorTests` uses real PostgreSQL/Testcontainers and
covers success, parser metadata, missing blob, unsupported extension, duplicate
version/chunk delivery, and cross-knowledge-system isolation. The fixture uses
a temporary `LocalCasBlobStore`; no artifact files are part of the intended
change.

## Remaining Risk

The processor is application-level only. Queue dispatch, HTTP submission, and
production orchestration are intentionally outside this slice. A failure after
version commit but before final document/job metadata save would require a
broader transaction boundary if that failure mode becomes operationally
relevant; parser failures themselves occur before version persistence.
