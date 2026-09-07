# Task 2: Complete Multi-Format Durable Ingestion

## Scope

Implemented durable parser-backed ingestion for HTML, RSS, RDF, and OWL while preserving the existing `IDocumentParser` boundary and the existing `plain_text` durable job kind.

## Changes

- Extended `DocumentParser` with deterministic HTML and RSS fallback extraction.
- Routed RDF and OWL through the existing `RdfImportParser` boundary before producing durable text.
- Added media type and parser version metadata to `ParseResult` and `DocumentEntity`.
- Added EF Core migration `AddDocumentParserVersion`.
- Added `ParserExtractionJobHandler` for durable payload validation and processing.
- Added optional `document_sha256` / `documentSha256` validation, including KS ownership and claimed-job KS checks.
- Preserved existing immutable version/chunk persistence and content-SHA idempotency behavior.
- Added format matrix, parser metadata, stale SHA, and durable worker tests.

## Validation

All commands were run from `E:\GitHub\ontopilot`.

- `dotnet test src\\ISEStudio.IntegrationTests\\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~DocumentIngestionJobProcessorTests --no-restore`
  - Passed: 8, failed: 0.
- `dotnet test src\\ISEStudio.IntegrationTests\\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~DurableExtractionWorkerTests --no-restore`
  - Passed: 10, failed: 0.
- `dotnet test src\\ISEStudio.IntegrationTests\\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~ExtractionWorkflow --no-restore`
  - Passed in the focused validation batch, failed: 0.
- `dotnet test src\\ISEStudio.Tests\\ISEStudio.Tests.csproj --filter FullyQualifiedName~Documents --no-restore`
  - Passed: 28, failed: 0.
- `dotnet test src\\ISEStudio.IntegrationTests\\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~Ingestion --no-restore`
  - Passed: 39, failed: 0.
- `git diff --check`
  - Passed with no output.

## Residual Risks

- HTML/RSS use deterministic fallback text extraction rather than a layout-preserving document backend.
- RDF/OWL durable ingestion validates and summarizes triples through the existing RDF parser boundary; it does not mutate the production RDF store as part of document ingestion.
- The EF CLI emitted an existing warning that installed EF tools `10.0.0` are older than runtime `10.0.11`; migration generation and tests completed successfully.
