# Durable Ingestion Worker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an in-process hosted worker that durably claims extraction jobs from PostgreSQL and routes `plain_text` jobs to the existing document ingestion processor.

**Architecture:** Reuse `ExtractionJobEntity` as the durable queue. Add a small store method that atomically claims one due pending job with PostgreSQL row locking, and a dispatcher that maps the claimed job to `DocumentIngestionJobProcessor`; unknown kinds are terminal failures. The hosted service polls with cancellation, while the processor owns document/version idempotency and job result semantics.

**Tech Stack:** .NET 10, ASP.NET Core `BackgroundService`, EF Core 10, Npgsql, PostgreSQL 16, xUnit, Testcontainers PostgreSQL.

## Global Constraints

- PostgreSQL remains the only authoritative job and graph-fact store.
- No new migration, HTTP API, source connector, object storage, LLM, frontend, or Rust changes.
- Do not include existing untracked `artifacts/` directories.
- Tests that exercise PostgreSQL run serially to avoid DLL/container contention.

---

### Task 1: Atomic Job Claiming

**Files:**
- Modify: `src/ISEStudio/Extraction/ExtractionJobStore.cs`
- Test: `src/ISEStudio.IntegrationTests/Ingestion/ExtractionJobStoreTests.cs`

**Interfaces:**
- Produces `Task<ExtractionJobEntity?> ClaimNextAsync(CancellationToken)`.
- Claim only `pending` jobs, set `running`, `Phase = "dispatching"`, and return the claimed row.

- [ ] **Step 1: Write the failing PostgreSQL tests** for one-job claiming, no duplicate claim under two stores, and empty queue.
- [ ] **Step 2: Run the focused tests** with `dotnet test ... --filter FullyQualifiedName~ExtractionJobStoreTests --no-restore`; expect compile/test failure because `ClaimNextAsync` is absent.
- [ ] **Step 3: Implement one atomic EF/Npgsql SQL claim** using `UPDATE ... WHERE id = (SELECT ... FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING ...`; preserve other job fields.
- [ ] **Step 4: Rerun the focused tests** and require all claim tests to pass.
- [ ] **Step 5: Commit** with `feat(ingestion): claim durable extraction jobs`.

### Task 2: Dispatcher and Hosted Worker

**Files:**
- Create: `src/ISEStudio/Extraction/ExtractionJobDispatcher.cs`
- Create: `src/ISEStudio/Extraction/DurableExtractionWorker.cs`
- Modify: `src/ISEStudio/Extraction/ExtractionServiceCollectionExtensions.cs`
- Modify: `src/ISEStudio/Documents/DocumentServiceCollectionExtensions.cs`
- Test: `src/ISEStudio.IntegrationTests/Ingestion/DurableExtractionWorkerTests.cs`

**Interfaces:**
- Dispatcher method: `Task DispatchAsync(ExtractionJobEntity job, CancellationToken)`.
- Worker constructor takes `IServiceScopeFactory`, `ExtractionJobStore`, and `TimeProvider`; it polls until cancelled.
- `plain_text` payload must contain `knowledge_system_id`, `document_id`, and optional `model`; invalid/missing payload fails the job without invoking ingestion.

- [ ] **Step 1: Write failing tests** for successful plain-text dispatch, unknown kind terminal failure, malformed payload failure, and worker cancellation.
- [ ] **Step 2: Run the focused tests** and confirm failure before implementation.
- [ ] **Step 3: Implement dispatcher** using a fresh DI scope per job; route to `DocumentIngestionJobProcessor`, parse payload with `JsonDocument`, and call `ExtractionJobStore.MarkFailedAsync` for dispatch errors.
- [ ] **Step 4: Implement `BackgroundService.ExecuteAsync`** as a cancellation-aware poll loop: claim, dispatch, then delay briefly when empty; no unbounded task fan-out.
- [ ] **Step 5: Register services** without changing existing processor registrations; use a configurable bounded poll interval with a short default.
- [ ] **Step 6: Rerun focused integration tests** and require all tests to pass.
- [ ] **Step 7: Commit** with `feat(ingestion): dispatch durable extraction jobs`.

### Task 3: Recovery, Documentation, and Full Verification

**Files:**
- Modify: `src/ISEStudio/Infrastructure/Startup/StaleJobRecoveryService.cs` only if needed to cover worker-owned `running` jobs.
- Modify: `.superpowers/sdd/progress.md`
- Create: `.superpowers/sdd/ingestion-stage-5-brief.md`
- Create: `.superpowers/sdd/ingestion-stage-5-report.md`

- [ ] **Step 1: Add a recovery test** proving a previously running worker job is requeued or failed according to the existing startup contract.
- [ ] **Step 2: Make the smallest recovery adjustment** only if the test exposes a gap.
- [ ] **Step 3: Run serial verification:**
  `dotnet test src\ISEStudio.IntegrationTests\ISEStudio.IntegrationTests.csproj --filter FullyQualifiedName~Ingestion --no-restore`
  `dotnet test src\ISEStudio.Tests\ISEStudio.Tests.csproj --filter FullyQualifiedName~Extraction --no-restore`
  `git diff --check`
- [ ] **Step 4: Record Stage 5 scope, evidence, and remaining source/API orchestration risks.**
- [ ] **Step 5: Commit documentation and the final focused implementation changes.**

## Self-Review

- Coverage: Task 1 covers atomic durable claiming; Task 2 covers routing, malformed jobs, cancellation, and failure persistence; Task 3 covers startup recovery and evidence.
- No placeholder requirements remain; every task names files, interfaces, tests, and commands.
- Type consistency: `ExtractionJobStore.ClaimNextAsync` returns the entity consumed by `ExtractionJobDispatcher.DispatchAsync`; the dispatcher constructs the existing `DocumentIngestionJob` contract.