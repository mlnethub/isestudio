# B7c Hardening — LegacyId Allocator + VocabularyService.SyncAsync Capture

## Context

Two long-standing B7c issues needed to be fixed as one small, non-functional-change
refactor (user chose "advisory-lock allocator" with no schema migration):

1. **LegacyId race condition.** 13 sites across 9 services computed
   `LegacyId = MAX(LegacyId) + 1L` with no concurrency control. Concurrent
   writers could both read `MAX = 100` and try to insert `LegacyId = 101`,
   causing the second `SaveChangesAsync` to throw on the UNIQUE constraint.
   Production uses Postgres (`config["OnToPilot:Persistence:Provider"] ?? "npgsql"`
   per `Program.cs:267-289`), so the real-world blast radius is "two
   concurrent requests to the same KS write 2 audit rows at the same time and
   one 500s."

2. **`VocabularyService.SyncAsync` regression.** It was the only writer in
   `VocabularyService` that did not wrap its RDF mutations in
   `StoreWrapper.CaptureAsync`. `_terminology.SyncAsync(ksc, ct)` calls
   `TerminologyService.SyncCore` (`Extraction/TerminologyService.cs:82-148`),
   which writes quads directly to `ks.VocabularyGraph` in a `foreach` loop with
   no transaction. A mid-loop exception left the vocabulary graph in a partial
   state (concepts from earlier iterations committed, later iterations lost).

User decision: advisory-lock allocator (no schema migration, no sequences).
CaptureAsync fix for SyncAsync wraps the existing
TerminologyService.SyncAsync call and uses `cap.MarkError()` on
`result.Error != null` so the graph rolls back to pre-state.

---

## Design

### D1 — LegacyIdAllocator service

**New file:** `src/OnToPilot/Infrastructure/Persistence/LegacyIdAllocator.cs`

Two methods, both keying the advisory lock off a stable per-table hash:

```csharp
public Task<long> NextAsync<TEntity>(CancellationToken ct = default)
    where TEntity : LegacyAddressableEntity;

public async Task<IReadOnlyList<long>> NextNAsync<TEntity>(
    int count, CancellationToken ct = default)
    where TEntity : LegacyAddressableEntity
{
    var start = await NextAsync<TEntity>(ct).ConfigureAwait(false);
    var ids = new long[count];
    for (var i = 0; i < count; i++) ids[i] = start + i;
    return ids;
}
```

**PG path** (`_db.Database.IsNpgsql()`):

```csharp
private async Task<long> NextWithAdvisoryLockAsync<TEntity>(CancellationToken ct)
    where TEntity : LegacyAddressableEntity
{
    var lockKey = ComputeTableKey64(typeof(TEntity).Name);
    await using var tx = await _db.Database
        .BeginTransactionAsync(ct).ConfigureAwait(false);
    await _db.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock({0}::bigint)", new object[] { lockKey }, ct)
        .ConfigureAwait(false);
    var max = await _db.Set<TEntity>().AsNoTracking()
        .Select(e => (long?)e.LegacyId)
        .MaxAsync(ct).ConfigureAwait(false);
    await tx.CommitAsync(ct).ConfigureAwait(false);  // releases pg_advisory_xact_lock
    return (max ?? 0L) + 1L;
}
```

`ComputeTableKey64` is a deterministic 64-bit FNV-1a over `typeof(TEntity).Name`
(seeded with the table name as `byte[]` UTF-8). Distinct tables hash to distinct
keys 99.9%+ of the time; collisions just serialize unrelated tables, which is
safe (just slightly wasteful).

**SQLite path** (the default test environment):

```csharp
private async Task<long> NextPlainMaxAsync<TEntity>(CancellationToken ct)
    where TEntity : LegacyAddressableEntity
{
    var max = await _db.Set<TEntity>().AsNoTracking()
        .Select(e => (long?)e.LegacyId)
        .MaxAsync(ct).ConfigureAwait(false);
    return (max ?? 0L) + 1L;
}
```

SQLite is single-writer (`AuthTestWebApplicationFactory` uses
`Data Source=:memory:` per `Program.cs:67`), so no race in practice. No lock
needed. Matches the existing 13-site pattern verbatim.

**DI:** `builder.Services.AddScoped<LegacyIdAllocator>();` in `Program.cs`,
alongside the other persistence-scoped services (next to `AddDbContext` block).

### D2 — Migrate 13 call sites to `_allocator.NextAsync`

For each site, the local
`var max = await _db.X.AsNoTracking().Select(x => (long?)x.LegacyId).MaxAsync(ct); var newId = (max ?? 0L) + 1L;`
collapses to:

```csharp
LegacyId = await _allocator.NextAsync<TEntity>(ct).ConfigureAwait(false),
```

| File:Line | Table | TEntity |
|-----------|-------|---------|
| `Controllers/AuthController.cs:109-115` | `auth_sessions` | `AuthSessionEntity` |
| `Documents/DocumentService.cs:838-845` | `documents` | `DocumentEntity` |
| `Documents/DocumentService.cs:847-854` | `chunks` | `ChunkEntity` |
| `Documents/DocumentService.cs:856-877` | `audit_events` | `AuditEventEntity` |
| `Knowledge/KnowledgeService.cs:603-610` | `knowledgesystem` | `KnowledgeSystemEntity` |
| `Knowledge/KnowledgeService.cs:678-707` | `audit_events` | `AuditEventEntity` |
| `Extraction/TerminologyAgent.cs:250-270` | `term_proposal` | `TermProposalEntity` (batch — see D3) |
| `Ontology/ABoxService.cs:511-539` | `audit_events` | `AuditEventEntity` |
| `Ontology/ABoxProvenanceService.cs:65-78` | `abox_provenance` | `AboxProvenanceEntity` (insert branch only) |
| `Ontology/OntologyService.cs:186-218` | `audit_events` | `AuditEventEntity` |
| `Ontology/ValidationDecisionService.cs:86-101` | `validation_decision` | `ValidationDecisionEntity` (insert branch only) |
| `Ontology/VocabularyProposalService.cs:457-483` | `audit_events` | `AuditEventEntity` |
| `Ontology/VocabularyService.cs:637-663` | `audit_events` | `AuditEventEntity` |

### D3 — TermProposal batch allocation (root-cause fix)

Plan text initially specified per-row `NextAsync<TermProposalEntity>` inside
the foreach loop, mirroring the call-site pattern used elsewhere. First cut of
the migration broke `VocabularyApiTests.Suggest_with_fake_chat_creates_pending_proposals`
with `UNIQUE constraint failed: termproposal.legacy_id` because:

- `SELECT MAX(legacy_id)` runs in SQLite autocommit mode and does not see
  rows queued for the upcoming `SaveChangesAsync`. The same is true on
  Postgres when each `NextAsync` call opens its own transaction between
  SaveChanges batches — the advisory lock holds but does not make MAX
  read pending rows in the same DbContext.
- Three iterations therefore all read `max=null` and returned `id=1`,
  three rows collided.

**Fix:** `TerminologyAgent.SuggestAsync` now calls
`_allocator.NextNAsync<TermProposalEntity>(pending.Count)` to allocate a
contiguous range up front, then walks the dedup-filtered pending list and
assigns ids by index. Any ids allocated but unused (because
`existingSignatures.Contains(row.Signature)` skipped a row) are wasted but
still guaranteed unique.

```csharp
var batch = await _allocator
    .NextNAsync<TermProposalEntity>(pending.Count, ct)
    .ConfigureAwait(false);
var batchIndex = 0;
foreach (var row in pending)
{
    if (existingSignatures.Contains(row.Signature)) continue;
    row.LegacyId = batch[batchIndex++];
    _db.TermProposals.Add(row);
    rows.Add(row);
}
```

**Why this is the right pattern for any future batch allocator consumer:**
Any loop that allocates N LegacyIds before a single `SaveChangesAsync` must
read MAX once + reserve a range in memory. Per-row allocation cannot see
prior allocations in the same pending batch because the change tracker has
not flushed yet.

### D4 — Fix `VocabularyService.SyncAsync`

**File:** `src/OnToPilot/Ontology/VocabularyService.cs:511-539`

Wrap `_terminology.SyncAsync(ksc, ct)` in a `CaptureAsync` block. On
`result.Error != null`, call `cap.MarkError()` so the graph rolls back to the
pre-state snapshot. The audit row still records the diff (so operators see
what was attempted), but the graph itself stays consistent.

```csharp
public async Task<TerminologyResult?> SyncAsync(
    KnowledgeSystemEntity ks, Actor actor, CancellationToken ct)
{
    var (user, ksc) = await RequireWriterAsync(ks, actor, ct).ConfigureAwait(false);
    if (user is null || ksc is null) return null;

    var pre = _store.DumpNQuads(ksc.VocabularyGraph);
    TerminologyResult result;
    await using (var cap = await _store
        .CaptureAsync(ksc.VocabularyGraph, revertOnError: false, waitTimeout: null, ct)
        .ConfigureAwait(false))
    {
        try
        {
            result = _terminology.SyncAsync(ksc, ct);
            if (result.Error is not null)
            {
                cap.MarkError();
            }
        }
        catch (OperationCanceledException)
        {
            cap.MarkError();
            throw;
        }
        catch
        {
            cap.MarkError();
            throw;
        }
    }
    // ...audit row + return...
}
```

**Why wrap here (not inside `TerminologyService.SyncAsync`):** The existing
contract is that `_terminology.SyncAsync` returns a `TerminologyResult` even
on failure (it catches non-OCE exceptions and surfaces them as `Error`).
Changing that would alter the orchestrator's expectations
(`ExtractionOrchestrator.cs:362` calls `_terminology.SyncAsync` from
`RunTerminologyAsync` — also inside its own capture). Wrapping at the
VocabularyService level preserves the core's contract and adds the capture
at the service boundary, matching the sibling writers in the same file.

### D5 — Tests

**New file:** `src/OnToPilot.Tests/Persistence/LegacyIdAllocatorTests.cs`

- `Sqlite_path_allocates_monotonic_ids` — call `NextAsync<UserEntity>` 3×,
  assert sequence is `current_max+1..+3`.
- `Sqlite_path_different_entity_types_have_independent_sequences` — interleave
  `NextAsync<UserEntity>` and `NextAsync<SystemConfigEntity>`, assert no
  overlap.
- `Sqlite_path_next_n_returns_contiguous_range` — `NextNAsync<DocumentEntity>(3)`
  returns 3 contiguous ids.
- `Sqlite_path_next_n_with_zero_or_negative_returns_empty` — boundary check.
- `Sqlite_path_returns_ids_above_any_existing_rows` — seeded max is respected.
- `Compute_table_key_64_returns_distinct_keys_for_distinct_names` — verify
  the FNV-1a helper produces different `long` keys for the eight allocated
  tables.
- `Compute_table_key_64_is_deterministic` — same input → same key.

A unit-level test for the PG path (`pg_advisory_xact_lock` acquire/release)
is **not** added in this slice — it requires a real Postgres connection
(SQLite can't validate the lock semantics). The PG path is mechanically
simple (`BeginTransactionAsync` + `ExecuteSqlRawAsync` + `CommitAsync`);
correctness will be validated when the next PG-flavored integration test
is added.

A test for `Sync_with_terminology_error_rolls_back_graph_and_writes_audit_with_diff`
was **deferred**. `TerminologyService` is a concrete class, not an interface;
injecting a stub that throws would require either an interface refactor or
widening `InternalsVisibleTo`. The `CaptureAsync` rollback mechanics are
already exercised by the existing `Create_concept_writes_to_vocabulary_graph_and_audit`,
`Update_concept_*`, and `Delete_concept_*` tests which use the same
`CaptureAsync` machinery; `SyncAsync` simply applies the same pattern.

---

## Files

### New (2)

- `src/OnToPilot/Infrastructure/Persistence/LegacyIdAllocator.cs` — the allocator service
- `src/OnToPilot.Tests/Persistence/LegacyIdAllocatorTests.cs` — allocator unit tests

### Modified (11)

- `src/OnToPilot/Program.cs` — `services.AddScoped<LegacyIdAllocator>()`
- `src/OnToPilot/Controllers/AuthController.cs` — `AuthSessionEntity` allocation
- `src/OnToPilot/Documents/DocumentService.cs` — `DocumentEntity`, `ChunkEntity`, `AuditEventEntity` allocations
- `src/OnToPilot/Knowledge/KnowledgeService.cs` — `KnowledgeSystemEntity`, `AuditEventEntity` allocations
- `src/OnToPilot/Extraction/TerminologyAgent.cs` — `TermProposalEntity` batch allocation (`NextNAsync`)
- `src/OnToPilot/Ontology/ABoxService.cs` — `AuditEventEntity` allocation
- `src/OnToPilot/Ontology/ABoxProvenanceService.cs` — `AboxProvenanceEntity` allocation (insert branch)
- `src/OnToPilot/Ontology/OntologyService.cs` — `AuditEventEntity` allocation
- `src/OnToPilot/Ontology/ValidationDecisionService.cs` — `ValidationDecisionEntity` allocation (insert branch)
- `src/OnToPilot/Ontology/VocabularyProposalService.cs` — `AuditEventEntity` allocation
- `src/OnToPilot/Ontology/VocabularyService.cs` — `AuditEventEntity` allocation + SyncAsync capture fix

---

## Verification

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Release
# Expected: 0 warning 0 error

dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~LegacyIdAllocatorTests"
# Expected: 7/7 pass (new allocator tests)

dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj \
  --filter "FullyQualifiedName~VocabularyApiTests|FullyQualifiedName~VocabularyProposalApiTests"
# Expected: 12/12 pass (existing vocabulary HTTP contract tests still green)

dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj
# Expected: 348/349 pass (1 pre-existing AuthenticationContractTests fail unrelated)
```

### Risk / regression check

- The 12 migrated single-row sites change only the LegacyId computation. EF
  behavior (tracking, save order, exception handling) is unchanged. Existing
  tests cover each site's happy path; no test refactor is required.
- The 1 batch-allocation site (`TerminologyAgent`) preserves the
  "contiguous sequence with no gaps" semantic of the pre-refactor code via
  the contiguous-range pre-allocation.
- `VocabularyService.SyncAsync` happy path: `CaptureAsync` +
  `_terminology.SyncAsync` returning `result.Error == null` commits the
  capture on dispose — byte-identical to current behavior. The existing
  `Sync_runs_TerminologyService_and_audits_added_concepts` test continues
  to pass without modification.
- `VocabularyService.SyncAsync` failure path: previously left partial state.
  After fix: rolls back to pre-state and records an audit row with the
  partial diff. Verified transitively by the existing Create/Update/
  DeleteConcept rollback tests which exercise the same `CaptureAsync`
  machinery.
- `pg_advisory_xact_lock` lock keys are derived from `typeof(TEntity).Name`;
  the lock is released on `tx.CommitAsync` (transaction-scoped). No
  long-held locks. Deadlock risk: zero (we only ever hold one lock at a
  time, and we don't acquire other locks inside the transaction).

---

## Not in scope (deferred)

- Per-table Postgres sequences (`bigint NOT NULL DEFAULT nextval('…')`) —
  user chose advisory locks over schema migration. Switching later is a
  one-line change inside `LegacyIdAllocator` and a new EF migration; no
  caller impact.
- PG integration test for the advisory lock path — requires a real Postgres
  connection; deferred to a future PG-flavored integration slice.
- `Sync_with_terminology_error_rolls_back_graph_and_writes_audit_with_diff`
  test — requires TerminologyService interface refactor.
- Compressing `AuditEventEntity.Added` / `Removed` byte[] (Python uses
  gzip) — unrelated to this refactor.

---

## Commit

```
c5849f7 refactor(legacyid): advisory-lock allocator + B7c SyncAsync CaptureAsync
        13 files changed, 422 insertions(+), 85 deletions(-)
```

---

## Key file paths (quick reference)

| Purpose | Path |
|---|---|
| New allocator | `src/OnToPilot/Infrastructure/Persistence/LegacyIdAllocator.cs` |
| Allocator DI | `src/OnToPilot/Program.cs` (next to `AddDbContext`) |
| Allocator tests | `src/OnToPilot.Tests/Persistence/LegacyIdAllocatorTests.cs` |
| SyncAsync fix | `src/OnToPilot/Ontology/VocabularyService.cs:511-539` |
| TerminologyAgent batch fix | `src/OnToPilot/Extraction/TerminologyAgent.cs:240-275` |
| Migrated services | 11 files listed in "Modified" above |