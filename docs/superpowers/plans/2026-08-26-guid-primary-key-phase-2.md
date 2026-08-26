# Guid 主键 Phase 2 — LegacyIdAllocator 退役 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retire `LegacyIdAllocator` and write path. Keep `LegacyId` as a read-only compatibility field — new rows default to `0` (DB DEFAULT), drop UNIQUE indexes, leave the 24 `legacy_id` columns + 109 production read sites untouched. No data loss.

**Architecture:** 4 atomic commits sequenced as Phase A.1 (entity base + EntityConfigurations + LegacyIdAllocator file delete) → Phase A.2 (30 call site replacements + DI delete) → Phase A.3 (runbook) → Phase B (EF migration `LegacyIdDefaultZero`: DROP INDEX + ALTER COLUMN DEFAULT 0). Then Phase C (test cleanup + new tests) → Phase D (review) → Phase E (smoke). Phase A.0 (6 file renames → 7 actually) is **already done** as commit `aa5f89d` — no rebuild.

**Tech Stack:** .NET 10 + ASP.NET Core + EF Core 10 + Npgsql 10 + PostgreSQL 16 + MinIO + Docker Compose. EF Core `dotnet ef migrations add` for migration generation. GNU sed for bulk text rewrite.

**Spec:** `docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md` (current revision: 2026-08-26 halt → option B + D1c + D2 + D5')

---

## Global Constraints

(Verbatim from spec §2 / §5 / Decision Log §8 — every task implicitly inherits these.)

- **Scope:** Phase A.1 (entity base + EntityConfigurations + alloc file delete) + Phase A.2 (30 call sites + DI) + Phase A.3 (runbook §3.2/§3.3) + Phase B (EF migration `LegacyIdDefaultZero`) + Phase C (test cleanup) + Phase D (review) + Phase E (smoke — **NO** `docker compose down -v`,数据保留).
- **22 services × 30 call sites:** 25 × `AllocateAndPersistAsync` + 3 × `AllocateManyAndPersistAsync` across 22 service classes(per spec §4.2 table).`ResolutionService.cs:26` field declared but never called — also delete.
- **24 entity configurations:** `EntityConfigurations.cs` has 24 `HasColumnName("legacy_id")` blocks. Each gets `HasDefaultValue(0L)` added; `HasIndex(...).IsUnique() HasDatabaseName("ux_*_legacy_id")` removed(否则多 row 同为 0 撞 UNIQUE)。`SystemConfigEntity` 已 `HasDefaultValue(SingletonLegacyId)`,仅删 index。
- **LegacyId setter:** `LegacyAddressableEntity.LegacyId` remains `public long LegacyId { get; set; }` — see spec §4.1 Ruling 1; `SettingsService.cs:114` writes `LegacyId = SystemConfigEntity.SingletonLegacyId` (verified as the only production write). The private setter design was abandoned.
- **EF migration `LegacyIdDefaultZero`:** 仅 `migrationBuilder.DropIndex("ux_*_legacy_id", table)` + `migrationBuilder.AlterColumn(..., defaultValue: 0L, oldDefaultValue: null)`。若 EF 输出含 `CreateTable` / `InsertData`,**人工 patch**。
- **DI:** `Program.cs:336` `builder.Services.AddScoped<LegacyIdAllocator>();` 删除。
- **No IriSqlMigrator changes:** `legacy_id` 本来不在 `ColumnsToRewrite`(`long` 不是 `uniqueidentifier`),verifier baseline 不动。
- **Runbook 更新范围:** 仅限 `docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md` §0 / §3.2 / §3.3。§3.5 SQL INSERT 模板**保留原样**(`legacy_id` 列仍写,`COALESCE(MAX+1)` 是历史 admin 序号约定)。
- **历史 EF migration:** `20260816140916_InitialCompatibility.cs` 保留不动;Phase 2 的 migration 是 append-only。
- **Tag:** `pre-isestudio-rename` (at `fc06a73`) + `pre-python-retirement` 保留,Phase 2 不打新 tag。
- **Branch:** 全 work on `dotnet` branch(currently at `aa5f89d` = Phase A.0 7 file renames already done)。
- **数据:** **不丢**。现有 volume 数据完整保留,新 row `legacy_id = 0`。
- **Production 109 `.LegacyId` 访问点:** 全部 DO-NOT-TOUCH,行为不变。

---

## Task 1: Preflight verification

**Files:**
- Touch: none (verification only)

**Interfaces:**
- Consumes: nothing
- Produces: baseline verified; pre-isestudio-rename tag at `fc06a73`; count of 22 services × 30 call sites + 24 entity configurations + 6 test files verified

- [ ] **Step 1: Verify on `dotnet` branch and HEAD is `aa5f89d`**

```bash
git rev-parse --abbrev-ref HEAD
# Expected: dotnet
git log --oneline -1
# Expected: aa5f89d chore(phase2): finish Stage 3 territory — 7 OnToPilot* filenames to ISEStudio*
```

- [ ] **Step 2: Verify pre-isestudio-rename tag at fc06a73**

```bash
git rev-parse pre-isestudio-rename
# Expected: <fc06a73-hash> (NOT the current HEAD)
git log --oneline pre-isestudio-rename -1
# Expected: fc06a73 docs(rename): spec self-review fixes (typo + 3 internal contradictions)
```

- [ ] **Step 3: Verify baseline build green**

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo
# Expected: Build succeeded. 0 Error(s). 0 Warning(s)
cd ..
```

If build fails or has warnings, STOP. Investigate.

- [ ] **Step 4: Verify baseline tests green (count before Phase 2)**

```bash
cd src
dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo --no-build
# Expected: Passed! - Failed: 0, Passed: ~858 (capture exact count)
dotnet test ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj -c Release --nologo --no-build
# Expected: Passed! - Failed: 0, Passed: 167
dotnet test ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj -c Release --nologo --no-build
# Expected: Passed! - Failed: 0, Passed: ~63
cd ..
```

Record exact pre-Phase-2 counts as `<UNIT_BEFORE>`, `<CONTRACT_BEFORE>`, `<INTEGRATION_BEFORE>` for use in Task 7 (Phase D) gate verification.

- [ ] **Step 5: Verify scope matches spec (22 services × 30 call sites + 24 entity configurations)**

```bash
# Should be 30 (25 AllocateAndPersistAsync + 3 AllocateManyAndPersistAsync + some in test files):
grep -rn "AllocateAndPersistAsync\|AllocateManyAndPersistAsync" src/ISEStudio/ \
  --include="*.cs" | grep -v "LegacyIdAllocator.cs:" | wc -l
# Expected: 30 production call sites (production only; test files have more)

# Should be 22 service classes that inject LegacyIdAllocator:
grep -rln "LegacyIdAllocator" src/ISEStudio/ --include="*.cs" \
  | grep -v "LegacyIdAllocator.cs:" | grep -v "/bin/" | wc -l
# Expected: 22 production classes + 0 (Program.cs DI counts as 23rd, capture list)

# Should be 24 HasColumnName("legacy_id") blocks in EntityConfigurations.cs:
grep -c "HasColumnName.*legacy_id\|HasColumnName(\"legacy_id\")" \
  src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs
# Expected: 24

# Should be 6 test files referencing LegacyIdAllocator (per spec §2.1 test layer):
grep -rln "LegacyIdAllocator" src/ISEStudio.Tests/ src/ISEStudio.IntegrationTests/ \
  --include="*.cs" | sort
# Expected: 6-8 files (TokenServiceTests / ConflictAgentTests / ExportJobStoreTests /
# ExtractionAgentChainTests / TerminologyAgentOrchestrationTests / StructureAgentTests +
# LegacyIdAllocatorTests + possibly 1-2 PG integration tests)
```

- [ ] **Step 6: Verify spec/plan/discussion set is current**

```bash
# Spec should reference option B + D1c (default 0)
grep -E "option B|D1\(c\)|DEFAULT 0" \
  docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md | head -3
# Expected: at least 3 matches

# Plan header should NOT mention 6 file renames in active phase
grep -E "6 file renames via git mv" \
  docs/superpowers/plans/2026-08-26-guid-primary-key-phase-2.md
# Expected: 0 matches (A.0 already done as aa5f89d)
```

- [ ] **Step 7: Commit no-op if working tree is clean**

```bash
git status
# Expected: working tree clean (only docs/superpowers/plans/2026-08-25-isestudio-rename.md untracked is OK)
```

If working tree has unexpected modifications, STOP. Investigate.

---

## Task 2: Phase A.1 — Entity base class setter + EntityConfigurations + LegacyIdAllocator file delete

**Files:**
- Modify: `src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs` (1 setter change)
- Modify: `src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs` (24 HasColumnName + 24 HasIndex changes)
- Delete: `src/ISEStudio/Infrastructure/Persistence/LegacyIdAllocator.cs` (310 lines)
- Modify: `src/ISEStudio/Exports/ExportJobStore.cs:40-41` (doc comment update)

**Interfaces:**
- Consumes: baseline verified (Task 1); HEAD at `aa5f89d`
- Produces: `LegacyId { get; private set; }` + 24 entity configs `HasDefaultValue(0L)` + 24 `HasIndex` deleted + `LegacyIdAllocator.cs` removed + ExportJobStore doc fix

> **Sub-task 2.0 (interface contract pre-flight):**
>
> Before editing, confirm with grep that LegacyAddressableEntity is the actual base (per [[ontopilot-phase2-halt]] §"Lessons learned"):

```bash
# MUST show LegacyAddressableEntity.cs contains ONLY the base class
grep -n "abstract class" src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs
# Expected: 1 line, "public abstract class LegacyAddressableEntity"
```

- [ ] **Step 1: Verify LegacyId setter access pattern**

```bash
grep -n "LegacyId" src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs
# Expected: shows "public long LegacyId { get; set; }" (or similar)
```

- [ ] **Step 2: Verify LegacyId setter access pattern (D4 ABANDONED — public setter preserved)**

> **Correction (post-execution, 2026-08-26):** D4 was abandoned during execution (see spec §8.1 Ruling 1). The setter remains `public long LegacyId { get; set; }` because `SettingsService.cs:114` is the production code's only non-zero LegacyId write site (`LegacyId = SystemConfigEntity.SingletonLegacyId`) and depends on the public setter. The "defense in depth" motivation is now provided by the DB layer (no UNIQUE index, DEFAULT 0) rather than the property setter. The original `private set` plan below is retained here for historical reference only — DO NOT execute it.

```diff
- public long LegacyId { get; set; }
+ /// <summary>
+ /// Legacy compatibility field. New rows default to 0 (DB DEFAULT 0).
+ /// Production code MUST NOT write to this property — it's reserved for
+ /// EF materialization and historical cross-table correlation only.
+ /// </summary>
+ public long LegacyId { get; private set; }
```

> (Historical plan — not executed)

File: `src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs`

- [ ] **Step 3: Verify EntityConfigurations.cs has 24 HasColumnName sites**

```bash
grep -cE 'HasColumnName\("legacy_id"\)' \
  src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs
# Expected: 24
```

- [ ] **Step 4: Bulk-update EntityConfigurations.cs (24 sites × 2 changes each)**

Use sed to transform the 24 sites. Pattern:

```diff
- builder.Property(x => x.LegacyId).HasColumnName("legacy_id").IsRequired();
- builder.HasIndex(x => x.LegacyId).IsUnique().HasDatabaseName("ux_users_legacy_id");
+ builder.Property(x => x.LegacyId).HasColumnName("legacy_id").IsRequired().HasDefaultValue(0L);
```

```bash
cd src/ISEStudio/Infrastructure/Persistence/Configurations/

# 1) HasColumnName blocks: append .HasDefaultValue(0L) before .IsRequired()
# For non-SystemConfig blocks (SystemConfig already has its own default — handle separately)
sed -i 's/HasColumnName("legacy_id").IsRequired();$/HasColumnName("legacy_id").IsRequired().HasDefaultValue(0L);/' EntityConfigurations.cs

# 2) Drop the HasIndex blocks (24 sites) — these are lines immediately following HasColumnName legacy_id
sed -i '/builder\.HasIndex(x => x\.LegacyId)\.IsUnique()\.HasDatabaseName("ux_.*legacy_id");$/d' EntityConfigurations.cs

# Verify count of HasColumnName("legacy_id")+HasDefaultValue(0L):
grep -c 'HasColumnName("legacy_id").IsRequired().HasDefaultValue(0L)' EntityConfigurations.cs
# Expected: 23 (24 minus 1 for SystemConfigEntity which already has its own default)

# Verify count of HasIndex lines remaining:
grep -c 'HasIndex(x => x.LegacyId).IsUnique().HasDatabaseName("ux_.*legacy_id")' EntityConfigurations.cs
# Expected: 0

cd ../../../
```

- [ ] **Step 5: Handle SystemConfigEntity's special default**

```bash
grep -B 1 -A 1 "ux_systemconfig_legacy_id" \
  src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs
# Expected (post sed): should show HasColumnName("legacy_id").IsRequired().HasDefaultValue(0L).
# But SystemConfigEntity needs HasDefaultValue(SystemConfigEntity.SingletonLegacyId), NOT 0L.
# Manually edit:
#   HasColumnName("legacy_id").IsRequired().HasDefaultValue(0L);  (line 382 area)
# → HasColumnName("legacy_id").IsRequired().HasDefaultValue(SystemConfigEntity.SingletonLegacyId);
```

Use Read to find the line, then Edit to revert it.

- [ ] **Step 6: Delete LegacyIdAllocator.cs**

```bash
git rm src/ISEStudio/Infrastructure/Persistence/LegacyIdAllocator.cs
# Expected: rm 'src/ISEStudio/Infrastructure/Persistence/LegacyIdAllocator.cs'
```

- [ ] **Step 7: Update ExportJobStore.cs doc comment**

```bash
grep -n "LegacyIdAllocator" src/ISEStudio/Exports/ExportJobStore.cs
# Expected: lines 40-41 doc comment
```

Read lines 38-44 and replace the `<see cref="LegacyIdAllocator.AllocateAndPersistAsync{TEntity}"/>` reference with text matching new behavior (e.g., "(`legacy_id` is auto-assigned to 0 by DB DEFAULT in post-Phase-2 schemas; this row was inserted with explicit value)").

- [ ] **Step 8: Build and confirm ONLY expected compile errors**

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo
# Expected: ~22 errors all of form "CS0103: name 'LegacyIdAllocator' does not exist" or
# "CS1061: '_allocator' does not contain a definition for 'AllocateAndPersistAsync'"
# (These are EXPECTED — call sites haven't been migrated yet; this is Phase A.2's job.)
cd ..
```

If build has other errors (e.g., unrelated to allocator), STOP. Investigate.

- [ ] **Step 9: Commit Phase A.1**

```bash
git add src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs \
        src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs \
        src/ISEStudio/Infrastructure/Persistence/LegacyIdAllocator.cs \
        src/ISEStudio/Exports/ExportJobStore.cs

git commit -m "refactor(phase2): LegacyId { private set } + 24 entity configs HasDefaultValue(0L)

Phase A.1 of Guid PK Phase 2 (spec d9a3d1b revised, option B + D1c + D5').

- LegacyAddressableEntity.LegacyId setter made private — production code
  cannot accidentally write; EF still materializes via backing field.
- EntityConfigurations.cs (24 sites): .HasColumnName(\"legacy_id\").IsRequired()
  → .HasDefaultValue(0L); .HasIndex(...ux_*_legacy_id) deleted (otherwise
  multiple new rows with legacy_id=0 would collide on UNIQUE).
- LegacyIdAllocator.cs deleted (310 lines) — call sites not yet migrated,
  so build will be red until Phase A.2.
- ExportJobStore.cs doc comment updated to reference new behavior.

Spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md §4.1 + §4.4
Predecessor: aa5f89d (Phase A.0 7 file renames)
Tag: pre-isestudio-rename -> fc06a73 (unchanged)"
```

Capture commit SHA as `<A1_SHA>`.

---

## Task 3: Phase A.2 — 30 call site replacements + DI delete

**Files:**
- Modify: 22 production service classes (per spec §4.2 table)
- Modify: `src/ISEStudio/Program.cs:335-336` (delete DI + comment)

**Interfaces:**
- Consumes: `<A1_SHA>` (Phase A.1 applied)
- Produces: `_allocator.AllocateAndPersistAsync(x)` → `dbContext.Add(x); SaveChangesAsync(ct)` across 25 sites; `_allocator.AllocateManyAndPersistAsync(rows)` → `dbContext.AddRange(rows); SaveChangesAsync(ct)` across 3 sites; DI registration deleted

> **Sub-task 3.0 (decide on batch strategy):**
>
> 22 services × 30 call sites — 3 mechanical patterns:
> 1. Constructor + field + single call: 18 services (one-liner-ish refactor)
> 2. Constructor + field + multi-call: 4 services (ConflictService / DocumentService / KnowledgeService / ReleaseService)
> 3. Direct `new LegacyIdAllocator(db).AllocateAndPersistAsync()` (no DI field): 2 services (ExportJobStore / ExtractionJobStore)
>
> All 22 services can be migrated in one subagent dispatch as one big mechanical edit. The patterns are uniform and the diff per file is small.

- [ ] **Step 1: Verify 22 call site files match spec §4.2 table**

```bash
# Capture full list for the dispatch brief
grep -rln "LegacyIdAllocator" src/ISEStudio/ --include="*.cs" \
  | grep -v "LegacyIdAllocator.cs:" | sort
# Expected: 22 files matching spec §4.2 table
```

- [ ] **Step 2: Migrate AuthService.cs as a smoke test (verify pattern)**

```bash
grep -n "LegacyIdAllocator\|AllocateAndPersistAsync\|_allocator" \
  src/ISEStudio/Authentication/AuthService.cs
```

Edit AuthService.cs (per spec §4.2 pattern):
- Remove `private readonly LegacyIdAllocator _allocator;` field
- Remove `LegacyIdAllocator allocator` constructor param + assignment
- Replace `await _allocator.AllocateAndPersistAsync(entity, ct).ConfigureAwait(false);` with:
  ```csharp
  _dbContext.Users.Add(entity);
  await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
  ```

Build to verify AuthService.cs alone compiles cleanly:

```bash
cd src
dotnet build ISEStudio/ISEStudio.csproj -c Release --nologo
# Expected: 1 fewer error than after Phase A.1 (AuthService.cs migrated)
cd ..
```

- [ ] **Step 3: Migrate remaining 21 services via subagent dispatch**

Dispatch a single subagent with:
- All 22 service file paths
- The 30 call site table from spec §4.2
- The AuthService.cs migration as the canonical pattern
- Requirement: each service must compile individually after migration
- Verification: `dotnet build src/ISEStudio/ISEStudio.sln -c Release` should report 0 errors after all 22 migrated

Capture subagent result. Confirm 22 files migrated, 0 build errors.

- [ ] **Step 4: Migrate ExportJobStore.cs and ExtractionJobStore.cs (direct `new LegacyIdAllocator` pattern)**

These 2 services use `var allocator = new LegacyIdAllocator(db); await allocator.AllocateAndPersistAsync(...)` without constructor injection. Pattern:

```diff
- var allocator = new LegacyIdAllocator(db);
- await allocator.AllocateAndPersistAsync(row, cancellationToken)
-     .ConfigureAwait(false);
+ _dbContext.<Set>.Add(row);
+ await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
```

For ExtractionJobStore.cs:266 (`AllocateManyAndPersistAsync`):

```diff
- await _allocator.AllocateManyAndPersistAsync(rows, ct).ConfigureAwait(false);
+ _dbContext.<Set>.AddRange(rows);
+ await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
```

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo
# Expected: 0 errors
cd ..
```

- [ ] **Step 5: Delete DI registration in Program.cs**

```diff
- // to plain MAX+1 (single-writer DB). See LegacyIdAllocator.cs for rationale.
- builder.Services.AddScoped<LegacyIdAllocator>();
```

File: `src/ISEStudio/Program.cs:335-336`

- [ ] **Step 6: Verify clean build**

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo
# Expected: 0 Error(s)
cd ..
```

If any errors remain, STOP. Investigate before committing.

- [ ] **Step 7: Commit Phase A.2**

```bash
git add src/ISEStudio/Program.cs \
        $(git diff --name-only HEAD~1 | grep -E "src/ISEStudio/.*\.cs$")

git commit -m "refactor(phase2): retire LegacyIdAllocator — 30 call sites + DI

Phase A.2 of Guid PK Phase 2 (option B + D1c).

22 production service classes migrated (per spec §4.2 table):
- AuditLogService / AuthService / KnowledgeApiTokenService / McpTokenService
- ConflictAgent / ConflictService
- AuthController / DocumentService
- ExportJobStore / ExtractionJobStore
- TerminologyAgent / KnowledgeService
- ABoxProvenanceService / ABoxService / OntologyService / ReleaseService
- StructureAgent / ValidationDecisionService
- VocabularyProposalService / VocabularyService
- PromptService / ProviderService

Pattern: _allocator.AllocateAndPersistAsync(x) →
  dbContext.Add(x); await dbContext.SaveChangesAsync(ct);
Pattern: _allocator.AllocateManyAndPersistAsync(rows) →
  dbContext.AddRange(rows); await dbContext.SaveChangesAsync(ct);

ResolutionService.cs:26 field declaration removed (was injected but never called).

Program.cs:336 builder.Services.AddScoped<LegacyIdAllocator>() removed.
LegacyIdAllocator.cs (Phase A.1) is the orphaned class — only its deletion
in git remains.

Spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md §4.2
Predecessor: <A1_SHA>
Tag: pre-isestudio-rename -> fc06a73 (unchanged)"
```

Capture commit SHA as `<A2_SHA>`.

---

## Task 4: Phase A.3 — Bootstrap runbook update

**Files:**
- Modify: `docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md` (§0 + §3.2 + §3.3 only)

**Interfaces:**
- Consumes: `<A2_SHA>` (Phase A.2 applied)
- Produces: Runbook reflects post-Phase-2 `legacy_id = 0` semantics for new rows; §3.5 SQL INSERT 保留 `COALESCE(MAX+1)` 模板不变

> **Sub-task 4.0 (scope confirmation per spec §2.2):**
> Spec explicit DO-NOT-TOUCH: 历史 spec / Python baseline / 已 retired 文档 — **不动**。仅 `docs/superpowers/runbooks/` 范围。

- [ ] **Step 1: Read current runbook §3.2 / §3.3 / §0 to identify text**

```bash
grep -n "LegacyIdAllocator\|allocator\|allocated\|allocate" \
  docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md
# Expected: ~3-5 mentions in §3.2 / §3.3 / §4.6
```

- [ ] **Step 2: Edit §0 (add Phase 2 context banner)**

Read §0 (intro section), prepend a paragraph noting Phase 2 status:

```markdown
> **Phase 2 状态(2026-08-26+):** `LegacyIdAllocator` 已退役;新 row 的 `legacy_id` 由 DB `DEFAULT 0` 派发(非 `MAX+1`)。
> 本 runbook §3.5 的 `COALESCE(MAX+1)` SQL INSERT 模板**保留不变** —— 它仍是合法的 bootstrap 路径,只是 MAX+1 不再是硬约束(UNIQUE 索引已删);保留 `MAX+1` 是为了历史 admin 序号习惯(admin 序号 = 1)。
```

- [ ] **Step 3: Edit §3.2 (password hash generation)**

Find any `LegacyIdAllocator` mention. If present, remove.

- [ ] **Step 4: Edit §3.3 (schema columns)**

Update §3.3 schema table commentary (not the column list — column stays):

```diff
- If your table is all snake_case (说明 schema 是手写的,不是 EF 迁移来的)
+ (post-Phase-2): `legacy_id` 列保留,新 row 默认 `0` (DB DEFAULT 0);`ux_*_legacy_id` UNIQUE 索引已删。
+ 本 runbook 假设的 `users` 表结构兼容此变化。
```

- [ ] **Step 5: Leave §3.5 SQL INSERT template unchanged**

Per spec §2.1 runbook + D9: `legacy_id` 列仍写,`COALESCE(MAX+1)` 保留为 admin 序号约定。

- [ ] **Step 6: Verify no other spec / Python baseline docs were touched**

```bash
git status
# Expected: only runbook file modified
```

- [ ] **Step 7: Commit Phase A.3**

```bash
git add docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md

git commit -m "docs(phase2): runbook §0/§3.2/§3.3 reflects LegacyIdAllocator retirement

Phase A.3 of Guid PK Phase 2 (option B + D1c).

- §0 banner: post-Phase-2 status (DB DEFAULT 0 + UNIQUE index deleted)
- §3.2 / §3.3: removed stale 'LegacyIdAllocator.AllocateAndPersistAsync' references
- §3.5 SQL INSERT template: UNCHANGED — COALESCE(MAX+1) kept for admin
  ordinal habit (D9 decision)

Scope: docs/superpowers/runbooks/ only. Historical specs /
Python baseline / retired docs left untouched.

Spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md §2.1
Predecessor: <A2_SHA>
Tag: pre-isestudio-rename -> fc06a73 (unchanged)"
```

Capture commit SHA as `<A3_SHA>`.

---

## Task 5: Phase B — EF migration `LegacyIdDefaultZero`

**Files:**
- Create: `src/ISEStudio/Infrastructure/Persistence/Migrations/20260826HHMMSS_LegacyIdDefaultZero.cs` (EF auto-generated)
- Modify: `src/ISEStudio/Infrastructure/Persistence/Migrations/ISEStudioDbContextModelSnapshot.cs` (EF auto-updated)

**Interfaces:**
- Consumes: `<A3_SHA>` (Phase A complete)
- Produces: 24 tables × `DropIndex("ux_*_legacy_id")` + `AlterColumn(..., defaultValue: 0L)` migration; snapshot updated

> **Sub-task 5.0 (EF migration constraints per spec §4.3):**
> Migration MUST only contain `DropIndex` + `AlterColumn`. If EF outputs `CreateTable` / `InsertData`, **manually patch**. `SystemConfigEntity` default is `SingletonLegacyId` (not 0L) — already handled in Task 2 Step 5 via HasDefaultValue(SingletonLegacyId), so EF will emit `defaultValue: SingletonLegacyId` (a long constant).

- [ ] **Step 1: Confirm build green before migration**

```bash
cd src
dotnet build ISEStudio/ISEStudio.csproj -c Release --nologo
# Expected: 0 Error(s)
cd ..
```

- [ ] **Step 2: Generate migration via dotnet ef**

```bash
cd src
dotnet ef migrations add LegacyIdDefaultZero \
  --project ISEStudio/ISEStudio.csproj \
  --startup-project ISEStudio/ISEStudio.csproj \
  --context ISEStudioDbContext
cd ..
```

Expected: file `src/ISEStudio/Infrastructure/Persistence/Migrations/<timestamp>_LegacyIdDefaultZero.cs` created.

- [ ] **Step 3: Audit generated migration content**

```bash
# MUST contain DropIndex + AlterColumn only, no CreateTable / InsertData:
cat src/ISEStudio/Infrastructure/Persistence/Migrations/*_LegacyIdDefaultZero.cs \
  | grep -E "CreateTable|InsertData"
# Expected: 0 matches

# MUST contain 24 DropIndex + 24 AlterColumn:
grep -c "DropIndex" src/ISEStudio/Infrastructure/Persistence/Migrations/*_LegacyIdDefaultZero.cs
# Expected: 24
grep -c "AlterColumn.*legacy_id" src/ISEStudio/Infrastructure/Persistence/Migrations/*_LegacyIdDefaultZero.cs
# Expected: 24 (or 23 if EF inlines SystemConfigEntity differently)
```

If migration contains `CreateTable` or `InsertData`, manually patch (replace `CreateTable` block with the inlined `DropIndex + AlterColumn` calls).

- [ ] **Step 4: Apply migration locally (SQLite dev DB)**

```bash
cd src
dotnet ef database update \
  --project ISEStudio/ISEStudio.csproj \
  --startup-project ISEStudio/ISEStudio.csproj \
  --context ISEStudioDbContext
cd ..
```

Expected: migration applied successfully. SQLite dev DB now has `legacy_id` DEFAULT 0 + no `ux_*_legacy_id` indexes.

- [ ] **Step 5: Run unit tests to confirm migration didn't break anything**

```bash
cd src
dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo --no-build
# Expected: failures from deleted LegacyIdAllocatorTests (per Phase C) — capture count
cd ..
```

Phase B's expected state: tests fail because `LegacyIdAllocatorTests.cs` still references the deleted class. **This is OK** — Phase C fixes it.

- [ ] **Step 6: Commit Phase B**

```bash
git add src/ISEStudio/Infrastructure/Persistence/Migrations/*_LegacyIdDefaultZero.cs \
        src/ISEStudio/Infrastructure/Persistence/Migrations/ISEStudioDbContextModelSnapshot.cs

git commit -m "feat(phase2): EF migration LegacyIdDefaultZero

Phase B of Guid PK Phase 2.

24 tables:
- DROP INDEX ux_*_legacy_id (UNIQUE constraint removed per D5')
- ALTER COLUMN legacy_id SET DEFAULT 0L

SystemConfigEntity gets defaultValue: SystemConfigEntity.SingletonLegacyId (not 0L).

Migration is in-place alter (no CREATE TABLE / INSERT DATA). SQLite dev DB applied
locally to verify migration is clean.

Pre-existing LegacyIdAllocatorTests will fail in this commit — fixed in Phase C.

Spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md §4.3
Predecessor: <A3_SHA>
Tag: pre-isestudio-rename -> fc06a73 (unchanged)"
```

Capture commit SHA as `<B_SHA>`.

---

## Task 6: Phase C — Test cleanup + new LegacyIdDefaultTests

**Files:**
- Delete: `src/ISEStudio.Tests/Persistence/LegacyIdAllocatorTests.cs` (~280 lines, 12 Fact)
- Delete: `src/ISEStudio.IntegrationTests/Persistence/LegacyIdAllocatorAdvisoryLockTests.cs` (506 lines, per [[ontopilot-allocator-atomic]]; plan originally misnamed this as `PostgresLegacyIdAllocatorTests.cs`)
- Modify: 6 test files (sed to remove `LegacyIdAllocator` references): `TokenServiceTests.cs` / `ConflictAgentTests.cs` / `ExportJobStoreTests.cs` / `ExtractionAgentChainTests.cs` / `TerminologyAgentOrchestrationTests.cs` / `StructureAgentTests.cs`
- Create: `src/ISEStudio.Tests/Persistence/LegacyIdDefaultTests.cs` (4-5 Fact tests)

**Interfaces:**
- Consumes: `<B_SHA>` (Phase B applied)
- Produces: 0 allocator references in tests; new tests verify `legacy_id = 0` for new rows; full unit + contract + integration suite green

- [ ] **Step 1: Delete LegacyIdAllocatorTests.cs (SQLite unit test, 12 Fact)**

```bash
git rm src/ISEStudio.Tests/Persistence/LegacyIdAllocatorTests.cs
```

- [ ] **Step 2: Delete PG integration test for allocator**

> **Correction (post-execution, 2026-08-26):** The actual filename deleted in commit `617e21d` was `LegacyIdAllocatorAdvisoryLockTests.cs` (506 lines), not `PostgresLegacyIdAllocatorTests.cs` as the original plan stated. The file captures the PG concurrency tests for the advisory-lock allocator from [[ontopilot-allocator-atomic]].

```bash
ls src/ISEStudio.IntegrationTests/Persistence/ | grep -i allocator
# Expected: LegacyIdAllocatorAdvisoryLockTests.cs (actual filename, 506 lines)
git rm src/ISEStudio.IntegrationTests/Persistence/LegacyIdAllocatorAdvisoryLockTests.cs
```

- [ ] **Step 3: Verify all remaining test references to LegacyIdAllocator**

```bash
grep -rln "LegacyIdAllocator\|AllocateAndPersistAsync\|AllocateManyAndPersistAsync" \
  src/ISEStudio.Tests/ src/ISEStudio.IntegrationTests/ src/ISEStudio.ApiContract.Tests/ \
  --include="*.cs"
# Expected: 6 files (TokenServiceTests, ConflictAgentTests, ExportJobStoreTests,
# ExtractionAgentChainTests, TerminologyAgentOrchestrationTests, StructureAgentTests)
```

- [ ] **Step 4: Bulk-remove LegacyIdAllocator references in 6 test files**

Each test file has 1-2 lines referencing `LegacyIdAllocator` (typically in `NewAllocator` helper, service registration, or constructor arg). Pattern per file:

```diff
- private static LegacyIdAllocator NewAllocator(ISEStudioDbContext db) => new(db);
+ // allocator removed in Phase 2; new rows default to legacy_id = 0 via DB DEFAULT
```

```diff
- services.AddScoped<LegacyIdAllocator>();
+ // (LegacyIdAllocator retired; legacy_id assigned by DB DEFAULT 0)
```

```diff
- var allocator = new LegacyIdAllocator(agentDb);
- allocator.AllocateAndPersistAsync(...)
+ dbContext.<Set>.Add(...);
+ await dbContext.SaveChangesAsync();
```

For each of the 6 files, read the affected lines and Edit to remove allocator reference. Verify each file compiles individually after edit.

- [ ] **Step 5: Verify clean build (tests included)**

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo
# Expected: 0 Error(s)
cd ..
```

If errors, STOP. Most likely a missed reference.

- [ ] **Step 6: Create LegacyIdDefaultTests.cs**

File: `src/ISEStudio.Tests/Persistence/LegacyIdDefaultTests.cs`

```csharp
using ISEStudio.Infrastructure.Persistence;
using ISEStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ISEStudio.Tests.Persistence;

/// <summary>
/// Verifies Phase 2 behavior: new rows default legacy_id to 0 (DB DEFAULT 0).
/// LegacyIdAllocator retired; allocator-related tests moved here.
/// </summary>
public sealed class LegacyIdDefaultTests
{
    [Fact]
    public async Task NewRow_LegacyIdIsZero_WhenNotExplicitlySet()
    {
        using var db = TestDbContextFactory.Create();
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = $"u-{Guid.NewGuid():N}",
            PasswordHash = "x",
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        Assert.Equal(0L, user.LegacyId);
    }

    [Fact]
    public async Task MultipleNewRows_AllHaveLegacyIdZero()
    {
        using var db = TestDbContextFactory.Create();
        db.Users.AddRange(
            new UserEntity { Id = Guid.NewGuid(), Username = "u1", PasswordHash = "x", IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow },
            new UserEntity { Id = Guid.NewGuid(), Username = "u2", PasswordHash = "x", IsAdmin = true, Active = true, CreatedAt = DateTimeOffset.UtcNow }
        );
        await db.SaveChangesAsync();

        Assert.Equal(0L, db.Users.Single(u => u.Username == "u1").LegacyId);
        Assert.Equal(0L, db.Users.Single(u => u.Username == "u2").LegacyId);
    }

    [Fact]
    public async Task ExistingRow_LegacyIdUnchanged_OnUpdate()
    {
        using var db = TestDbContextFactory.Create();
        // Seed a row with explicit non-zero LegacyId (simulating historical data)
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = "u-hist",
            PasswordHash = "x",
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
            // Bypass private setter via reflection (or via backing field):
        };
        typeof(UserEntity).GetProperty(nameof(UserEntity.LegacyId))!
            .SetValue(user, 42L);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Detach + re-query to get a fresh materialization
        db.ChangeTracker.Clear();

        var reloaded = await db.Users.SingleAsync(u => u.Username == "u-hist");
        Assert.Equal(42L, reloaded.LegacyId);

        // Update unrelated field
        reloaded.Active = false;
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var reloaded2 = await db.Users.SingleAsync(u => u.Username == "u-hist");
        Assert.Equal(42L, reloaded2.LegacyId);
        Assert.False(reloaded2.Active);
    }

    [Fact]
    public async Task ExplicitLegacyId_HonoredWhenSetBeforeAdd()
    {
        using var db = TestDbContextFactory.Create();
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = "u-explicit",
            PasswordHash = "x",
            IsAdmin = true,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        // Backdoor setter via reflection (private set)
        typeof(UserEntity).GetProperty(nameof(UserEntity.LegacyId))!
            .SetValue(user, 999L);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        Assert.Equal(999L, user.LegacyId);
    }
}
```

(Adjust `TestDbContextFactory` if the actual factory class differs — match the existing pattern in `LegacyIdAllocatorTests.cs`.)

- [ ] **Step 7: Run new tests in isolation**

```bash
cd src
dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo \
  --filter "FullyQualifiedName~LegacyIdDefaultTests"
# Expected: 4/4 Passed
cd ..
```

- [ ] **Step 8: Run full unit + contract test suite**

```bash
cd src
dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo --no-build
# Expected: Passed! - Failed: 0, Passed: <UNIT_BEFORE> - 12 (alloc tests deleted) + 4 (new tests) = <UNIT_BEFORE - 8>
dotnet test ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj -c Release --nologo --no-build
# Expected: Passed! - Failed: 0, Passed: 167 (unchanged)
dotnet test ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj -c Release --nologo --no-build
# Expected: Passed! - Failed: 0, Passed: <INTEGRATION_BEFORE> - N (PG allocator tests deleted)
cd ..
```

If any failures, STOP. Investigate before committing.

- [ ] **Step 9: Commit Phase C**

```bash
git add src/ISEStudio.Tests/Persistence/LegacyIdAllocatorTests.cs \
        src/ISEStudio.Tests/Persistence/LegacyIdDefaultTests.cs \
        src/ISEStudio.IntegrationTests/Persistence/LegacyIdAllocatorAdvisoryLockTests.cs \
        $(git diff --name-only HEAD~1 | grep -E "src/ISEStudio.Tests/.*\.cs$|src/ISEStudio.IntegrationTests/.*\.cs$")

git commit -m "test(phase2): drop allocator tests, add LegacyIdDefaultTests

Phase C of Guid PK Phase 2.

Deleted:
- src/ISEStudio.Tests/Persistence/LegacyIdAllocatorTests.cs (12 Fact, ~280 lines)
- src/ISEStudio.IntegrationTests/Persistence/LegacyIdAllocatorAdvisoryLockTests.cs (506 lines)

Modified (6 test files): removed LegacyIdAllocator field / DI registration /
NewAllocator helper, replaced with direct dbContext.Add + SaveChangesAsync.

Added:
- src/ISEStudio.Tests/Persistence/LegacyIdDefaultTests.cs (4 Fact):
  - NewRow_LegacyIdIsZero_WhenNotExplicitlySet
  - MultipleNewRows_AllHaveLegacyIdZero
  - ExistingRow_LegacyIdUnchanged_OnUpdate
  - ExplicitLegacyId_HonoredWhenSetBeforeAdd

Test counts: unit 858 - 12 + 4 = 850; contract 167 (unchanged);
integration 63 - N (PG allocator).

Spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md §4.6
Predecessor: <B_SHA>
Tag: pre-isestudio-rename -> fc06a73 (unchanged)"
```

Capture commit SHA as `<C_SHA>`.

---

## Task 7: Phase D — Full test suite + branch review

**Files:**
- Touch: none (verification only)

**Interfaces:**
- Consumes: `<C_SHA>` (Phase C complete)
- Produces: branch review verdict; full test gate green

- [ ] **Step 1: Run full test suite from clean build**

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo
dotnet test ISEStudio.sln -c Release --nologo --no-build
cd ..
```

Expected counts (per spec §5 gate 5):
- Unit: <UNIT_BEFORE - 8> ≈ 850
- Contract: <CONTRACT_BEFORE> = 167 (unchanged)
- Integration: <INTEGRATION_BEFORE> - N (PG allocator deleted)

If any failures, STOP.

- [ ] **Step 2: Verify spec §5 gates 1, 2, 3**

```bash
# Gate 1: no allocator references
grep -rn "LegacyIdAllocator\|AllocateAndPersistAsync\|AllocateManyAndPersistAsync" \
  src/ISEStudio/ src/ISEStudio.Tests/ src/ISEStudio.IntegrationTests/ src/ISEStudio.ApiContract.Tests/ \
  --include="*.cs" | grep -v "/bin/" | grep -v "/obj/"
# Expected: 0 matches

# Gate 2: LegacyId setter private
grep "LegacyId {" src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs
# Expected: line with "private set"

# Gate 3: migration only AlterColumn + DropIndex
grep -E "CreateTable|InsertData" \
  src/ISEStudio/Infrastructure/Persistence/Migrations/*_LegacyIdDefaultZero.cs
# Expected: 0 matches
```

If any gate fails, STOP.

- [ ] **Step 3: Branch review (subagent)**

Dispatch a fresh subagent (opus tier) with:
- The diff: `git diff pre-isestudio-rename..HEAD --stat`
- The spec: `docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md`
- The plan: this file
- The review rubric: spec compliance + code quality + blast radius assessment

Capture subagent verdict. If load-bearing findings, address them and re-review.

---

## Task 8: Phase E — Runtime smoke test

**Files:**
- Touch: none (runtime verification only)

**Interfaces:**
- Consumes: `<C_SHA>` (or final post-review HEAD)
- Produces: smoke test verdict; **NO** `docker compose down -v` (data preserved)

> **Sub-task 8.0 (critical data preservation per spec §3):**
> Phase 2 does NOT drop or wipe data. Existing volume is intact. Smoke test runs against existing volume with the migration applied on top.

- [ ] **Step 1: Apply migration to running database**

```bash
cd src
dotnet ef database update \
  --project ISEStudio/ISEStudio.csproj \
  --startup-project ISEStudio/ISEStudio.csproj \
  --context ISEStudioDbContext
cd ..
```

Expected: migration applied; existing rows have unchanged `legacy_id` values; new rows would default to 0 (but we don't insert in smoke).

- [ ] **Step 2: Verify DB schema post-migration**

```bash
docker exec ontopilot-postgres-1 psql -U isestudio -d isestudio -c '\d users'
```

Expected output:
```
     Column     |           Type           | Nullable | Default
----------------+--------------------------+----------+---------
 legacy_id      | bigint                   | not null | 0
```

No `ux_users_legacy_id` index listed in indexes section.

- [ ] **Step 3: Start backend and verify health**

```bash
docker compose up -d isestudio
sleep 10
curl -s http://127.0.0.1:8080/api/health
# Expected: 200 OK
docker compose logs isestudio --tail=20 | grep -iE "error|fatal|exception"
# Expected: no errors
```

- [ ] **Step 4: Smoke test legacy behavior (optional)**

```bash
# Insert a new row directly via SQL and verify legacy_id = 0 default
docker exec ontopilot-postgres-1 psql -U isestudio -d isestudio -c \
  "INSERT INTO users (id, \"Username\", \"PasswordHash\", \"IsAdmin\", \"Active\", \"CreatedAt\") VALUES (gen_random_uuid(), 'smoke-test', 'x', false, true, NOW()) RETURNING id, \"Username\", legacy_id;"
# Expected: legacy_id = 0

# Cleanup the smoke-test row
docker exec ontopilot-postgres-1 psql -U isestudio -d isestudio -c \
  "DELETE FROM users WHERE \"Username\" = 'smoke-test';"
```

- [ ] **Step 5: Document smoke result**

```bash
echo "Phase 2 smoke result: PASS at $(date -Iseconds)" >> /tmp/phase2-smoke.log
```

---

## Task 9: Final verification + memory + handoff

**Files:**
- Touch: memory file `~/.claude/projects/e--GitHub-ontopilot/memory/ontopilot-phase2-complete.md` (NEW)

- [ ] **Step 1: Verify spec §5 gates 4, 5, 6, 7**

```bash
# Gate 4: dotnet build clean
dotnet build src/ISEStudio.sln -c Release --nologo
# Expected: 0 Error(s) / 0 Warning(s)

# Gate 5: tests green (re-run with --no-build)
cd src
dotnet test ISEStudio.sln -c Release --nologo --no-build
cd ..
# Expected: per Task 7 Step 1 counts

# Gate 6: migration applied via isestudio-migrate container
docker compose up -d isestudio-migrate
docker compose ps isestudio-migrate
# Expected: Exited (0)

# Gate 7: runtime health
curl -s http://127.0.0.1:8080/api/health
# Expected: 200 OK
```

- [ ] **Step 2: Tag verification**

```bash
git rev-parse pre-isestudio-rename
# Expected: <fc06a73-hash> (NOT the current HEAD — tag still at rename point)
git rev-parse pre-python-retirement
# Expected: <pre-python-retirement-hash>
```

- [ ] **Step 3: Write memory file**

File: `~/.claude/projects/e--GitHub-ontopilot/memory/ontopilot-phase2-complete.md`

```markdown
---
name: ontopilot-phase2-complete
description: Guid PK Phase 2 (LegacyIdAllocator 退役) 完成 — option B + D1c + D5' 设计
metadata:
  type: project
---

# Guid PK Phase 2 完成 (2026-08-26)

**Why:** Phase 2 退役 `LegacyIdAllocator` 服务 + DB UNIQUE 索引。生产 109 个 `.LegacyId` 读访问点全部保留(行为不变);写入路径不再依赖 allocator,新 row `legacy_id = 0` (DB DEFAULT)。

**How to apply:** 任何后续 slice 想"再用 LegacyId 做 correlation / sort / FK"时,记住 LegacyId 是 **只读 + 大部分为 0**(只有历史 data 是非零)。新代码应该用 `Guid Id`。EF 改 HasDefaultValue 是 D5' 决定(删 UNIQUE),不要重新加 HasIndex(...).IsUnique()。

## Commits

- Phase A.0 (7 file renames): `aa5f89d`
- Phase A.1 (entity base + EntityConfigs + alloc file delete): `<A1_SHA>`
- Phase A.2 (30 call sites + DI): `<A2_SHA>`
- Phase A.3 (runbook): `<A3_SHA>`
- Phase B (EF migration `LegacyIdDefaultZero`): `<B_SHA>`
- Phase C (test cleanup + LegacyIdDefaultTests): `<C_SHA>`

## 关联

- 上游:[[ontopilot-isestudio-rename]] + [Phase1 spec](2026-08-20-guid-primary-key-design.md)
- 修订:[[ontopilot-phase2-halt]](option B + D1c + D5' 设计依据)
- 平行:[[ontopilot-allocator-atomic]] + [[ontopilot-allocator-missed-sites]]

## 后续

- GitHub repo rename follow-up: 解锁(allocator 引用层 clean)
- Guid PK Phase 3: 可推进(若还需要)
- 删 `legacy_id` 列:需另开 slice(类似 D1-D5' 决策流程,但更激进)
```

- [ ] **Step 4: Add pointer to MEMORY.md index**

Edit `~/.claude/projects/e--GitHub-ontopilot/memory/MEMORY.md`:

```markdown
- [ontopilot-phase2-complete](ontopilot-phase2-complete.md) — Guid PK Phase 2 完成(2026-08-26):option B + D1c + D5',LegacyIdAllocator 退役,新 row legacy_id=0
```

- [ ] **Step 5: Final commit (if memory file added to repo)**

Memory file is in user-global location, not in repo. No commit needed for memory.

```bash
git status
# Expected: working tree clean (all Phase 2 commits in place)
git log --oneline pre-isestudio-rename..HEAD
# Expected: ~6 commits (A.1, A.2, A.3, B, C, optionally D if review made changes)
```

- [ ] **Step 6: Report completion**

Print summary:

```
Phase 2 complete:
- <A1_SHA>: LegacyId private set + EntityConfigs HasDefaultValue(0L) + alloc file delete
- <A2_SHA>: 30 call site replacements + DI delete
- <A3_SHA>: runbook §0/§3.2/§3.3
- <B_SHA>: EF migration LegacyIdDefaultZero
- <C_SHA>: test cleanup + LegacyIdDefaultTests

Tests: unit=850 contract=167 integration=<N>
Smoke: PASS (data preserved, no down -v)
Spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md (revised option B)
Plan: docs/superpowers/plans/2026-08-26-guid-primary-key-phase-2.md (this file)
```

End of plan.
