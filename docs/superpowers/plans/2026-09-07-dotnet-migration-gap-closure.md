# .NET 10 Migration Gap Closure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the remaining migration gaps between the approved Utopia-to-ISEStudio design and the current .NET 10 implementation, then prove the system is ready for a rehearsed cutover.

**Architecture:** Preserve PostgreSQL as the only authoritative store and extend the existing `ExtractionJobStore`, `DurableExtractionWorker`, migration command, and ASP.NET Core DI boundaries. Add handler-based dispatch for every supported extraction kind, a provider-neutral search contract backed first by PostgreSQL FTS and pgvector, and deterministic rehearsal/benchmark gates. Do not recreate the completed RDF, API, authentication, observability, or blob migration work.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core 10, Npgsql, PostgreSQL 16 with `pg_trgm`/pgvector, `BackgroundService`, xUnit, Testcontainers PostgreSQL, PowerShell, React/TypeScript frontend.

## Global Constraints

- PostgreSQL remains the only authoritative job, graph-fact, provenance, audit, and embedding store.
- `ISEStudio` remains the only ASP.NET Core host; cross-module contracts belong in `ISEStudio.Application`.
- Do not reintroduce Rust or a second runtime graph database into production.
- Do not expose EF Core, Npgsql, parser, Lucene, or provider-specific LLM types through API contracts.
- Preserve the existing `ExtractionJobEntity` lifecycle and `PromptSnapshot`; durable payload is separate JSONB data.
- Every new behavior starts with a failing test and ends with its focused test plus the owning project test suite.
- Never add or commit `artifacts/`, `bin/`, or `obj/` output.
- A production cutover remains a manually authorized, stop-write, backup-verified, reversible operation.

---

## Current Baseline

The following work is already implemented and must be reused: .NET 10 host and EF Core migrations, RDF/Oxigraph import boundary, graph facts/provenance/temporal recursive traversal, REST/MCP/authentication, Serilog/OpenTelemetry, MinIO/blob migration, plain-text parser invocation, and the `plain_text` durable extraction worker. The current worker intentionally leaves TBox/ABox pending rows to their existing orchestration routes; the current migration project contains SQL, RDF, IRI, and blob primitives but no single end-to-end rehearsal gate; the current source tree has no `ISearchIndex` implementation.

## File Map

### Existing files to modify

- `src/ISEStudio/Extraction/ExtractionJobDispatcher.cs`: replace kind branching with handler resolution while preserving terminal failure behavior.
- `src/ISEStudio/Extraction/DurableExtractionWorker.cs`: keep sequential claiming and add configurable supported-kind dispatch.
- `src/ISEStudio/Extraction/ExtractionServiceCollectionExtensions.cs`: register all durable handlers and options.
- `src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContext.cs`: map search document/chunk metadata and outbox rows if the existing model requires it.
- `src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs`: add only the mappings required by the search/outbox schema.
- `src/ISEStudio/Infrastructure/Persistence/Migrations/*`: add one migration for search vectors/FTS indexes and one for outbox state only if the existing schema does not already contain them.
- `src/ISEStudio.IntegrationTests/Ingestion/DurableExtractionWorkerTests.cs`: add TBox/ABox handler routing and retry/idempotency cases.
- `src/ISEStudio.IntegrationTests/Deployment/ContainerSmokeTests.cs`: add post-cutover endpoint and ingestion smoke assertions.
- `src/ISEStudio/ISEStudio.csproj`: add only the PostgreSQL vector/search package required by the selected implementation.

### New files

- `src/ISEStudio.Application/Search/ISearchIndex.cs`: provider-neutral search request/result contracts.
- `src/ISEStudio/Infrastructure/Search/PostgresSearchIndex.cs`: PostgreSQL FTS plus vector candidate search.
- `src/ISEStudio/Infrastructure/Search/SearchServiceCollectionExtensions.cs`: search DI registration.
- `src/ISEStudio.Tests/Search/PostgresSearchIndexTests.cs`: SQLite-independent SQL and result-shaping unit tests using a fake connection boundary.
- `src/ISEStudio.IntegrationTests/Search/PostgresSearchIntegrationTests.cs`: Testcontainers FTS/vector ranking and tenant/permission/time filters.
- `src/ISEStudio.IntegrationTests/Migration/MigrationRehearsalTests.cs`: fresh, restored, and upgrade-path migration gates.
- `src/ISEStudio.Migration/Rehearsal/MigrationRehearsalCommand.cs`: deterministic orchestration and manifest output.
- `src/ISEStudio.Migration/Rehearsal/MigrationRehearsalManifest.cs`: serializable step/result schema.
- `scripts/migration/Invoke-MigrationRehearsal.ps1`: local/sandbox rehearsal entry point.
- `scripts/migration/Invoke-ProductionCutover.ps1`: manual stop-write cutover gates.
- `scripts/migration/Invoke-ProductionRollback.ps1`: tested rollback sequence.
- `scripts/migration/Test-MigrationGate.ps1`: shared JSON manifest and exit-code validation.
- `scripts/bench/graph-search-ingestion.ps1`: repeatable graph/search/ingestion benchmark runner.
- `docs/migration/gap-closure-acceptance.md`: evidence checklist and actual measured thresholds.

---

### Task 1: Unify All Extraction Kinds Behind Durable Handlers

**Files:**
- Create: `src/ISEStudio/Extraction/IExtractionJobHandler.cs`
- Create: `src/ISEStudio/Extraction/PlainTextExtractionJobHandler.cs`
- Create: `src/ISEStudio/Extraction/TBoxExtractionJobHandler.cs`
- Create: `src/ISEStudio/Extraction/ABoxExtractionJobHandler.cs`
- Modify: `src/ISEStudio/Extraction/ExtractionJobDispatcher.cs`
- Modify: `src/ISEStudio/Extraction/DurableExtractionWorker.cs`
- Modify: `src/ISEStudio/Extraction/ExtractionServiceCollectionExtensions.cs`
- Test: `src/ISEStudio.IntegrationTests/Ingestion/DurableExtractionWorkerTests.cs`

**Interfaces:**
- `IExtractionJobHandler.Kind` is a non-null `string`.
- `Task HandleAsync(ExtractionJobEntity job, CancellationToken cancellationToken)` is the only handler operation.
- `ExtractionJobDispatcher.DispatchAsync` selects exactly one handler by `job.Kind`; missing or duplicate handlers mark the job terminally failed without invoking a processor.
- Payload keys are explicit: `plain_text` uses `knowledge_system_id`, `document_id`, and optional `model`; `tbox` and `abox` use `knowledge_system_id`, `job_id`, and `source_version`.

- [ ] **Step 1: Write failing routing tests** for one successful TBox job, one successful ABox job, duplicate delivery idempotency, unknown kind terminal failure, and malformed payload terminal failure.

```csharp
[Fact]
public async Task Tbox_job_is_claimed_and_sent_to_the_tbox_handler_once()
{
    var job = await SeedPendingJobAsync(kind: "tbox", payload: ValidTboxPayload());
    await StartWorkerUntilIdleAsync();

    Assert.Equal("completed", await ReadJobStatusAsync(job.Id));
    Assert.Equal(1, TboxFake.InvocationCount);
}
```

- [ ] **Step 2: Run the focused test**

Run: `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~DurableExtractionWorkerTests --no-restore`

Expected: FAIL because no handler contract exists for `tbox` or `abox`.

- [ ] **Step 3: Add the handler interface and adapters**

```csharp
public interface IExtractionJobHandler
{
    string Kind { get; }
    Task HandleAsync(ExtractionJobEntity job, CancellationToken cancellationToken);
}
```

Each adapter must validate its JSON payload before calling the existing TBox/ABox orchestration service, pass the persisted `job.Id` as the idempotency key, and let the dispatcher mark failures through the existing job store. Do not duplicate graph writes in the adapters.

- [ ] **Step 4: Replace dispatcher kind branching with a keyed handler map**

```csharp
var handlers = handlerSet.ToDictionary(item => item.Kind, StringComparer.Ordinal);
if (!handlers.TryGetValue(job.Kind, out var handler))
{
    await jobStore.MarkFailedAsync(job.Id, $"Unsupported extraction kind '{job.Kind}'.", cancellationToken);
    return;
}
await handler.HandleAsync(job, cancellationToken);
```

- [ ] **Step 5: Register handlers and preserve sequential polling**

Register one scoped handler per kind, keep `ClaimNextAsync(cancellationToken, kind: null)` for the worker, and configure the poll delay through the existing options pattern. The worker must not create parallel task fan-out.

- [ ] **Step 6: Run focused and full extraction tests**

Run: `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~Ingestion --no-restore`

Expected: all existing plain-text tests plus the new TBox/ABox routing tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src/ISEStudio/Extraction src/ISEStudio.IntegrationTests/Ingestion/DurableExtractionWorkerTests.cs
git commit -m "feat: route all extraction kinds through durable worker"
```

---

### Task 2: Complete Multi-Format Durable Ingestion

**Files:**
- Create: `src/ISEStudio/Documents/ParserExtractionJobHandler.cs`
- Modify: `src/ISEStudio/Documents/DocumentIngestionJobProcessor.cs`
- Modify: `src/ISEStudio/Documents/PlainTextIngestionService.cs`
- Modify: `src/ISEStudio/Parsing/*` only where an existing parser lacks a deterministic metadata contract
- Modify: `src/ISEStudio/Extraction/ExtractionJobDispatcher.cs`
- Test: `src/ISEStudio.IntegrationTests/Extraction/ExtractionWorkflowTests.cs`
- Test: `src/ISEStudio.IntegrationTests/Ingestion/DurableExtractionWorkerTests.cs`

**Interfaces:**
- `ParserExtractionJobHandler` consumes `document_id`, `knowledge_system_id`, and `document_sha256` from durable JSONB payload.
- The existing `IDocumentParser` remains the parser boundary and returns normalized text, media type, parser name, and parser version.
- Every supported extension (`.txt`, `.pdf`, `.docx`, `.xlsx`, `.html`, `.rss`, `.rdf`, `.owl`) produces the same document-version/chunk persistence contract; unsupported extensions fail terminally without changing prior versions.

- [ ] **Step 1: Add failing format matrix tests**

```csharp
[Theory]
[InlineData("sample.txt", "text/plain")]
[InlineData("sample.pdf", "application/pdf")]
[InlineData("sample.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
[InlineData("sample.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
[InlineData("sample.html", "text/html")]
public async Task_supported_document_format_is_idempotently_persisted(string name, string mediaType)
{
    var first = await RunParserJobAsync(name, mediaType);
    var second = await RunParserJobAsync(name, mediaType);

    Assert.Equal(first.VersionId, second.VersionId);
    Assert.Equal(first.ChunkCount, second.ChunkCount);
}
```

- [ ] **Step 2: Run the matrix and record the first missing path**

Run: `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~ExtractionWorkflowTests --no-restore`

Expected: FAIL only for formats that are not yet wired to a durable handler; existing text/parser tests remain green.

- [ ] **Step 3: Implement parser job routing**

Use the existing blob SHA to read bytes, select `IDocumentParser`, persist parser metadata, and delegate normalized text to `PlainTextIngestionService`. The handler must not write a new version before parsing succeeds and must preserve the current cross-knowledge-system guard.

- [ ] **Step 4: Add RDF/OWL boundary behavior**

RDF/OWL jobs must invoke the existing Oxigraph/dotNetRDF import boundary and persist the same job audit/failure semantics. Runtime graph facts remain PostgreSQL-authoritative; no parser may open the mutable production RDF directory.

- [ ] **Step 5: Run integration and unit suites**

Run:

```powershell
dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~ExtractionWorkflow --no-restore
dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~Documents --no-restore
```

Expected: all format rows pass, duplicate delivery creates no duplicate version/chunks, and failed parsing leaves the prior version untouched.

- [ ] **Step 6: Commit**

```powershell
git add src/ISEStudio/Documents src/ISEStudio/Parsing src/ISEStudio/Extraction src/ISEStudio.IntegrationTests/Extraction src/ISEStudio.IntegrationTests/Ingestion
git commit -m "feat: complete durable multi-format ingestion"
```

---

### Task 3: Deliver PostgreSQL FTS and Vector Search Contract

**Files:**
- Create: `src/ISEStudio.Application/Search/ISearchIndex.cs`
- Create: `src/ISEStudio/Infrastructure/Search/PostgresSearchIndex.cs`
- Create: `src/ISEStudio/Infrastructure/Search/SearchServiceCollectionExtensions.cs`
- Create: `src/ISEStudio.Tests/Search/PostgresSearchIndexTests.cs`
- Create: `src/ISEStudio.IntegrationTests/Search/PostgresSearchIntegrationTests.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContext.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs`
- Create: `src/ISEStudio/Infrastructure/Persistence/Migrations/<timestamp>_AddSearchVectorsAndIndexes.cs`
- Modify: `src/ISEStudio/ISEStudio.csproj` only if a vector parameter package is required

**Interfaces:**

```csharp
public sealed record SearchRequest(
    Guid KnowledgeSystemId,
    string Query,
    int Limit = 20,
    DateTimeOffset? AsOf = null,
    Guid? ActorId = null);

public sealed record SearchHit(
    Guid ChunkId,
    Guid DocumentId,
    string Text,
    double LexicalScore,
    double? VectorScore,
    string SourceSha256);

public interface ISearchIndex
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write failing contract tests** for tenant isolation, limit enforcement, temporal filtering, FTS matching, vector fallback when no embedding exists, and deterministic tie ordering.

- [ ] **Step 2: Run the focused tests**

Run: `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~PostgresSearchIndexTests --no-restore`

Expected: FAIL because `ISearchIndex` and the implementation do not exist.

- [ ] **Step 3: Add the migration schema**

Create a nullable vector column tied to the immutable document-version/chunk identity, add a `tsvector` generated/indexed expression or an equivalent GIN index, enable pgvector only through the migration, and ensure the migration can run on an empty database and an existing database without rewriting historical text.

- [ ] **Step 4: Implement SQL candidate retrieval**

Use parameterized SQL only. Apply `knowledge_system_id`, permission, `AsOf`, and approval filters before ranking. Combine FTS rank with cosine distance only when a vector is present; order by combined score, then `ChunkId` for determinism.

- [ ] **Step 5: Register the abstraction and connect the existing search workflow**

Controllers and application services consume `ISearchIndex`; none may reference `NpgsqlConnection`, vector types, or SQL fragments.

- [ ] **Step 6: Run PostgreSQL integration tests**

Run: `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~PostgresSearchIntegrationTests --no-restore`

Expected: FTS, vector ranking, scope, time, and deterministic ordering tests pass against PostgreSQL/Testcontainers.

- [ ] **Step 7: Commit**

```powershell
git add src/ISEStudio.Application/Search src/ISEStudio/Infrastructure/Search src/ISEStudio/Infrastructure/Persistence src/ISEStudio.Tests/Search src/ISEStudio.IntegrationTests/Search src/ISEStudio/ISEStudio.csproj
git commit -m "feat: add postgres full text and vector search"
```

---

### Task 4: Make Migration Rehearsal a Tested Command

**Files:**
- Create: `src/ISEStudio.Migration/Rehearsal/MigrationRehearsalManifest.cs`
- Create: `src/ISEStudio.Migration/Rehearsal/MigrationRehearsalCommand.cs`
- Modify: `src/ISEStudio.Migration/Program.cs`
- Create: `src/ISEStudio.IntegrationTests/Migration/MigrationRehearsalTests.cs`
- Create: `scripts/migration/Test-MigrationGate.ps1`
- Create: `scripts/migration/Invoke-MigrationRehearsal.ps1`
- Create: `docs/migration/gap-closure-acceptance.md`

**Interfaces:**

```csharp
public sealed record MigrationRehearsalManifest(
    string RunId,
    string DatabaseMode,
    IReadOnlyList<MigrationStepResult> Steps,
    bool Passed,
    DateTimeOffset CompletedAt);

public sealed record MigrationStepResult(
    string Name,
    string Status,
    long Rows,
    string Checksum,
    string? Detail);

public Task<MigrationRehearsalManifest> RunAsync(
    MigrationRehearsalOptions options,
    CancellationToken cancellationToken);
```

- [ ] **Step 1: Write failing tests** for fresh database, Utopia backup restore, and previous ISEStudio schema upgrade. Each test must assert EF migration history, row counts, FK orphan count, graph fact count, evidence count, and manifest checksum.

- [ ] **Step 2: Run the tests**

Run: `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~MigrationRehearsalTests --no-restore`

Expected: FAIL because no unified rehearsal command or manifest exists.

- [ ] **Step 3: Implement deterministic rehearsal steps**

Execute, in order: database connectivity, EF migration, schema assertions, SQL snapshot verification, RDF copy verification, blob manifest verification, graph read-only checks, and report serialization. A failed step must stop later steps and return a nonzero process exit code.

- [ ] **Step 4: Add PowerShell gate validation**

`Test-MigrationGate.ps1` must parse the manifest, require `Passed == true`, require every named step to have `Status == "passed"`, and reject missing or extra step names. It must not log connection strings or tokens.

- [ ] **Step 5: Run all three rehearsal modes and document evidence**

Run:

```powershell
dotnet run --project src\ISEStudio.Migration -- rehearsal --mode fresh --manifest .artifacts/rehearsal-fresh.json
dotnet run --project src\ISEStudio.Migration -- rehearsal --mode restored --manifest .artifacts/rehearsal-restored.json
dotnet run --project src\ISEStudio.Migration -- rehearsal --mode upgrade --manifest .artifacts/rehearsal-upgrade.json
powershell -File scripts\migration\Test-MigrationGate.ps1 -Manifest .artifacts/rehearsal-fresh.json
```

Expected: all manifests pass; the acceptance document records command, commit, database image, duration, and checksum.

- [ ] **Step 6: Commit**

```powershell
git add src/ISEStudio.Migration src/ISEStudio.IntegrationTests/Migration scripts/migration docs/migration/gap-closure-acceptance.md
git commit -m "test: rehearse all database migration paths"
```

---

### Task 5: Establish Production-Scale Graph, Search, Ingestion, and Concurrency Baselines

**Files:**
- Create: `scripts/bench/graph-search-ingestion.ps1`
- Create: `src/ISEStudio.IntegrationTests/Benchmarks/BenchmarkFixture.cs`
- Create: `src/ISEStudio.IntegrationTests/Benchmarks/GraphTraversalBenchmarkTests.cs`
- Create: `src/ISEStudio.IntegrationTests/Benchmarks/SearchBenchmarkTests.cs`
- Create: `src/ISEStudio.IntegrationTests/Benchmarks/IngestionBenchmarkTests.cs`
- Modify: `docs/migration/gap-closure-acceptance.md`

**Interfaces:**
- Benchmark input is a versioned JSON fixture containing `fact_count`, `chunk_count`, `concurrent_writers`, `query_depth`, and expected result hashes.
- Each benchmark emits JSON with `p50_ms`, `p95_ms`, `throughput_per_second`, `error_count`, and `result_hash`.
- A benchmark fails when result hashes differ, errors are nonzero, or a declared Rust baseline threshold is exceeded.

- [ ] **Step 1: Add deterministic fixture and failing assertions**

```json
{
  "fact_count": 10000,
  "chunk_count": 5000,
  "concurrent_writers": 8,
  "query_depth": 4,
  "expected_result_hash": ""
}
```

The first run must generate the result hash only through an explicit `--record` switch; normal CI runs must compare against the checked-in baseline.

- [ ] **Step 2: Run benchmark tests against PostgreSQL**

Run: `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~Benchmarks --no-restore`

Expected: FAIL until fixture seeding and metric output are implemented.

- [ ] **Step 3: Implement four measured paths**

Measure bounded recursive graph traversal, FTS/vector search, batch durable ingestion, and concurrent graph writes. Use a single shared Testcontainers PostgreSQL instance per test class, clean data between cases, and keep all queries parameterized.

- [ ] **Step 4: Add CI-safe threshold comparison**

The PowerShell runner must accept `-Baseline`, `-Output`, and `-Record`; without `-Record`, it exits 1 when p95 or throughput exceeds the recorded tolerance.

- [ ] **Step 5: Record Rust comparison and commit**

Run: `powershell -File scripts\bench\graph-search-ingestion.ps1 -Baseline docs\migration\rust-baseline.json -Output .artifacts\dotnet-benchmark.json`

Expected: all four paths have stable hashes and measured values within the approved threshold; record exceptions explicitly in `docs/migration/gap-closure-acceptance.md`.

```powershell
git add scripts/bench src/ISEStudio.IntegrationTests/Benchmarks docs/migration/gap-closure-acceptance.md
git commit -m "test: establish migration performance baselines"
```

---

### Task 6: Execute Cutover, Smoke, and Rollback Gates

**Files:**
- Create: `scripts/migration/Invoke-ProductionCutover.ps1`
- Create: `scripts/migration/Invoke-ProductionRollback.ps1`
- Create: `scripts/migration/Test-MigrationGate.ps1`
- Modify: `src/ISEStudio.IntegrationTests/Deployment/ContainerSmokeTests.cs`
- Create: `docs/migration/production-cutover-record.md`
- Create: `docs/migration/production-rollback-record.md`

**Interfaces:**
- Cutover requires `-ConfirmStopWrites`, `-VerifiedBackupManifest`, `-RehearsalManifest`, and `-SmokeBaseUrl`.
- Rollback requires `-VerifiedBackupManifest` and `-PythonServiceName`; it must stop .NET before restoring database permissions.
- Both scripts return nonzero on any failed gate and redact passwords, bearer tokens, connection strings, and object-store secrets from logs.

- [ ] **Step 1: Write failing smoke and rollback-order tests**

```csharp
[Fact]
public async Task post_cutover_smoke_requires_health_ingestion_graph_search_and_mcp()
{
    await Smoke.RunAsync(baseUrl);
    Assert.Equal(HttpStatusCode.OK, Smoke.HealthStatus);
    Assert.True(Smoke.IngestionSucceeded);
    Assert.True(Smoke.GraphReadSucceeded);
    Assert.True(Smoke.SearchSucceeded);
    Assert.True(Smoke.McpToolListSucceeded);
}
```

The PowerShell test fixture must also assert rollback order: stop .NET, restore PostgreSQL write permission/backup, then unlock and start Rust.

- [ ] **Step 2: Run focused deployment tests**

Run: `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~ContainerSmokeTests --no-restore`

Expected: FAIL until the smoke path invokes the migrated graph, search, ingestion, and MCP workflows.

- [ ] **Step 3: Implement manual cutover gates**

The cutover script must enforce this exact order: stop Rust writes, verify database write freeze, verify backup, run rehearsal manifest gate, run RDF/blob/SQL checks, start ISEStudio, run smoke, and write a cutover record. It must refuse to run without the explicit confirmation switch.

- [ ] **Step 4: Implement rollback and observation records**

Rollback must be idempotent, record every completed step, and leave a machine-readable status file. Do not delete Python/RDF/blob backups during rollback.

- [ ] **Step 5: Run final project gates**

Run:

```powershell
dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --no-restore
dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --no-restore
dotnet build src\ISEStudio\ISEStudio.csproj --no-restore -warnaserror
dotnet format src\ISEStudio\ISEStudio.csproj --verify-no-changes --no-restore
pnpm --dir frontend build
docker compose config --quiet
git diff --check
```

Expected: every command exits 0; known package advisories remain the only non-error diagnostics; acceptance records contain fresh test output and manifest hashes.

- [ ] **Step 6: Commit the cutover package**

```powershell
git add scripts/migration src/ISEStudio.IntegrationTests/Deployment docs/migration
git commit -m "ops: verify dotnet migration cutover and rollback"
```

---

## Self-Review

### Spec coverage

- PostgreSQL graph authority, recursive CTE behavior, provenance, temporal filtering, and permissions are exercised by existing graph tests and Task 3/5 regression gates.
- Durable parsing, extraction, retries, idempotency, and failure isolation are covered by Tasks 1 and 2.
- PostgreSQL FTS, pgvector, and replaceable search abstraction are covered by Task 3.
- Fresh, restored, and upgraded database paths are covered by Task 4.
- Recursive traversal, vector retrieval, batch ingestion, and concurrent writes are covered by Task 5.
- Backup verification, stop-write cutover, post-cutover smoke, and Rust rollback are covered by Task 6.
- Existing API/MCP, authentication, observability, frontend, and blob migration work is reused and re-run in Task 6 rather than rebuilt.

### Placeholder scan

The plan contains no `TBD`, `TODO`, or unspecified implementation step. `<timestamp>` in the migration filename is an EF-generated timestamp and must be replaced by `dotnet ef migrations add` during Task 3; it is not a runtime placeholder.

### Type consistency

`IExtractionJobHandler.HandleAsync` consumes the existing `ExtractionJobEntity`; `ExtractionJobDispatcher` resolves handlers by `Kind`; `ISearchIndex.SearchAsync` consumes `SearchRequest` and returns `SearchHit`; `MigrationRehearsalCommand.RunAsync` returns `MigrationRehearsalManifest`; the benchmark runner consumes the rehearsal/fixture JSON and emits the documented metric schema.

### Explicit remaining risk

The pgvector extension and production-scale baseline require a PostgreSQL environment with the extension installed. If the deployment image lacks pgvector, Task 3 must stop at the migration preflight with a clear nonzero result; it must not silently downgrade the production contract to lexical-only search.
