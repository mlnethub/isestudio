# Guid 主键 Phase 2 — Legacy 字段退役 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete the `legacy_id` column from all 24 tables + retire `LegacyIdAllocator` + simplify the `LegacyAddressableEntity` base class + remove `legacy_id` from `IriSqlMigrator.ColumnsToRewrite` + drop the corresponding `ux_*_legacy_id` indexes. Hard cutover — postgres / minio volumes are wiped.

**Architecture:** 5 atomic commits sequenced as Phase A (code cleanup) → Phase B (EF migration) → Phase C (IriSqlMigrator cleanup) → Phase D (review) → Phase E (runtime smoke). Phase A itself is 3 atomic commits: A.0 (6 file renames) → A.1 (entity base) → A.2 (allocator + call sites + DI + EntityConfigurations). Each commit is independently revertable BEFORE `docker compose down -v` runs.

**Tech Stack:** .NET 10 + ASP.NET Core + EF Core 10 + Npgsql 10 + PostgreSQL 16 + MinIO + Docker Compose. EF Core `dotnet ef migrations add` for migration generation. GNU sed for bulk text rewrite (Windows git-bash). IriSqlMigrator 同步更新 baseline SHA-256。

**Spec:** `docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md` (commit `d9a3d1b`, 318 lines)

---

## Global Constraints

(Verbatim from spec §2 / §5 / Decision Log §8 — every task implicitly inherits these.)

- **Scope:** Phase A (代码清理) + Phase B (EF 配置) + Phase C (IriSqlMigrator) + Phase D (测试 + 评审) + Phase E (runtime smoke)。包含 `docs/superpowers/runbooks/` 下 bootstrap runbook 的 §0 / §3.3 / §3.5 / §4.6 更新。
- **24 entity 基类迁移:** 全部 entity 当前 `: LegacyAddressableEntity`,Phase 2 改 `: Entity`(verified by `grep -rhE "^public.+: LegacyAddressableEntity" src/ISEStudio/Infrastructure/Persistence/Entities/ | sort -u` 应返 24 个 entity)。`LegacyAddressableEntity` 类直接删除。
- **13+5 allocator call sites:** 13 个(per `LegacyIdAllocator.cs` doc-comment)+ 5 个历史补漏点(per [[ontopilot-allocator-missed-sites]],含 `ProviderService` + 3× `ConflictService` + `KnowledgeService grant`)全部替换为 `Id = Guid.NewGuid()` + 既有 `_db.SaveChangesAsync()`。
- **6 file renames(git mv,class body 不动):**
  - `src/ISEStudio/Infrastructure/Persistence/OnToPilotDbContext.cs` → `ISEStudioDbContext.cs`
  - `src/ISEStudio/Infrastructure/Persistence/OnToPilotDbContextFactory.cs` → `ISEStudioDbContextFactory.cs`
  - `src/ISEStudio/Mcp/OnToPilotMcpPrompts.cs` → `ISEStudioMcpPrompts.cs`
  - `src/ISEStudio/Mcp/OnToPilotMcpResources.cs` → `ISEStudioMcpResources.cs`
  - `src/ISEStudio/Mcp/OnToPilotMcpTools.cs` → `ISEStudioMcpTools.cs`
  - `src/ISEStudio/Serialization/OnToPilotJsonContext.cs` → `ISEStudioJsonContext.cs`
- **EF migration `DropLegacyId`:** 仅 `migrationBuilder.DropColumn("legacy_id", table)` + `migrationBuilder.DropIndex("ux_*_legacy_id", table)`。若 EF 输出含 `CreateTable` / `InsertData`,**人工 patch**。
- **IriSqlMigrator:** `ColumnsToRewrite` 移除 `legacy_id` 项 + `IriSqlVerifier` baseline 同步删 `legacy_id` row + SHA-256 重生成(`iri sql-smoke-check --update-baseline`)。
- **Runbook 更新范围:** **仅限 `docs/superpowers/runbooks/`**。`docs/migration/` / `migration/scripts/` / `docs/superpowers/specs/2026-08-25-isestudio-rename-design.md` 等历史 spec **不动**。
- **历史 EF migration:** `20260816140916_InitialCompatibility.cs`(含 legacy_id DDL)+ Designer **保留**作为历史快照。Phase 2 的 `DropLegacyId` 是 **append-only**,不删旧 migration。
- **Tag:** `pre-isestudio-rename` (at `fc06a73`) + `pre-python-retirement` 都保留,Phase 2 不打新 tag。
- **Branch:** 全 work on `dotnet` branch(currently `05516e0`)。
- **数据:** Postgres / MinIO volume 全清(运维已同意)。生产 / staging 必须先 `docker exec ... pg_dump`(详见 spec §3.1)。

---

## Task 1: Preflight verification

**Files:**
- Touch: none (verification only)

**Interfaces:**
- Consumes: nothing
- Produces: baseline verified; pre-isestudio-rename tag at `fc06a73`; count of 24 entity + 6 OnToPilot files + 13+5 call sites verified

- [ ] **Step 1: Verify on `dotnet` branch and HEAD is `05516e0`**

```bash
git rev-parse --abbrev-ref HEAD
# Expected: dotnet
git log --oneline -1
# Expected: 05516e0 docs(phase2): design spec for Guid PK Phase 2 (LegacyId retirement)
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
# Expected: Passed! - Failed: 0, Passed: 63
cd ..
```

Record exact pre-Phase-2 counts as `<UNIT_BEFORE>`, `<CONTRACT_BEFORE>`, `<INTEGRATION_BEFORE>` for use in Task 7 (Phase D) gate verification.

- [ ] **Step 5: Verify scope matches spec (24 entity + 6 OnToPilot files + 13+5 call sites)**

```bash
# Should be 24 (or close; verify list matches spec §2.1):
grep -rln ": LegacyAddressableEntity" src/ISEStudio/Infrastructure/Persistence/Entities/ | wc -l
# Expected: 24

# Should be 6:
git ls-files 'src/**/*.cs' | grep -E "OnToPilot[A-Z][a-zA-Z]*\.cs$"
# Expected: 6 files (the territory miss list)

# Should be 13+ (plus historical 5 leaks per memory):
grep -rln "AllocateAndPersistAsync\|AllocateManyAndPersistAsync" src/ \
  --include="*.cs" --exclude-dir=bin --exclude-dir=obj
# Expected: 18+ service files (capture list for Phase A.2 task brief)
```

If counts diverge from spec, STOP. Investigate.

- [ ] **Step 6: Verify docker compose config clean (pre-Phase 2 baseline)**

```bash
docker compose config --quiet
echo "Exit code: $?"
# Expected: 0
```

---

## Task 2: Phase A.0 — Finish Stage 3 territory (6 file renames via git mv)

**Files:**
- Rename (6): see Global Constraints above
- Touch: `.csproj` files that reference these (via file path references)

**Interfaces:**
- Consumes: baseline verified (Task 1)
- Produces: 6 `OnToPilot*` filenames renamed to `ISEStudio*`; class bodies untouched; build green

- [ ] **Step 1: git mv the 6 files (keep class bodies unchanged)**

```bash
git mv src/ISEStudio/Infrastructure/Persistence/OnToPilotDbContext.cs            src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContext.cs
git mv src/ISEStudio/Infrastructure/Persistence/OnToPilotDbContextFactory.cs     src/ISEStudio/Infrastructure/Persistence/ISEStudioDbContextFactory.cs
git mv src/ISEStudio/Mcp/OnToPilotMcpPrompts.cs                                   src/ISEStudio/Mcp/ISEStudioMcpPrompts.cs
git mv src/ISEStudio/Mcp/OnToPilotMcpResources.cs                                 src/ISEStudio/Mcp/ISEStudioMcpResources.cs
git mv src/ISEStudio/Mcp/OnToPilotMcpTools.cs                                     src/ISEStudio/Mcp/ISEStudioMcpTools.cs
git mv src/ISEStudio/Serialization/OnToPilotJsonContext.cs                       src/ISEStudio/Serialization/ISEStudioJsonContext.cs

# Verify:
git status --short | grep "^R  "
# Expected: 6 lines starting with "R  " (rename in stage)
```

- [ ] **Step 2: Verify no `.csproj` / `.sln` references the old filenames**

```bash
git grep -l "OnToPilotDbContext\.cs\|OnToPilotMcp.*\.cs\|OnToPilotJsonContext\.cs" -- 'src/**/*.csproj' 'src/**/*.props' 'src/**/*.targets' src/ISEStudio.sln 2>/dev/null
# Expected: empty (sln/csproj reference by Project GUID, not by filename)
```

If any matches appear, edit manually to use new filenames.

- [ ] **Step 3: Verify build still green (class bodies unchanged)**

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo
# Expected: Build succeeded. 0 Error(s). 0 Warning(s)
cd ..
```

If build fails, STOP. Investigate.

- [ ] **Step 4: Commit Phase A.0**

```bash
git add -A
git status
# Verify: only 6 file renames are staged
git diff --cached --stat
# Expected: 6 files renamed (R), no content changes
git commit -m "chore(phase2): finish Stage 3 territory — 6 OnToPilot* filenames to ISEStudio*

Phase A.0 of Guid PK Phase 2 spec (commit d9a3d1b).

The brand rename slice (commit fc06a73 + e8c8d02) renamed all class
identifiers + namespaces but left 6 filenames with the old brand.
This commit completes that sweep via git mv (class bodies unchanged):

- OnToPilotDbContext.cs         -> ISEStudioDbContext.cs
- OnToPilotDbContextFactory.cs  -> ISEStudioDbContextFactory.cs
- OnToPilotMcpPrompts.cs        -> ISEStudioMcpPrompts.cs
- OnToPilotMcpResources.cs      -> ISEStudioMcpResources.cs
- OnToPilotMcpTools.cs          -> ISEStudioMcpTools.cs
- OnToPilotJsonContext.cs       -> ISEStudioJsonContext.cs

Verifies: dotnet build src/ISEStudio.sln -c Release: 0 error / 0 warning.

Spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md §2.1
Tag: pre-isestudio-rename -> fc06a73 (unchanged)"
```

Capture SHA: `<A0_SHA>`

---

## Task 3: Phase A.1 — Entity base class migration (24 files)

**Files:**
- Modify: 24 entity files in `src/ISEStudio/Infrastructure/Persistence/Entities/*.cs`
- Delete: `src/ISEStudio/Infrastructure/Persistence/Entities/LegacyAddressableEntity.cs` (if exists; per spec §4.1 the file lives here)
- Test: existing unit tests should pass without changes (no API surface change)

**Interfaces:**
- Consumes: Phase A.0 applied (Task 2)
- Produces: 24 entities now `: Entity` directly; `LegacyAddressableEntity` class removed; build green

- [ ] **Step 1: List all 24 entity inheritance sites**

```bash
grep -rln ": LegacyAddressableEntity" src/ISEStudio/Infrastructure/Persistence/Entities/ \
  --include="*.cs" --exclude-dir=bin --exclude-dir=obj
# Expected: 24 files
```

If count differs from spec, STOP and report.

- [ ] **Step 2: Locate the `LegacyAddressableEntity` definition file**

```bash
grep -rln "class LegacyAddressableEntity" src/ISEStudio/
# Expected: 1 file (likely Entities/LegacyAddressableEntity.cs)
```

- [ ] **Step 3: Bulk rewrite `: LegacyAddressableEntity` → `: Entity`**

```bash
# Find all 24 files and sed-replace:
grep -rln ": LegacyAddressableEntity" src/ISEStudio/Infrastructure/Persistence/Entities/ \
  --include="*.cs" | xargs sed -i 's/: LegacyAddressableEntity/: Entity/g'

# Verify zero hits:
grep -rln ": LegacyAddressableEntity" src/ISEStudio/
# Expected: empty (excluding the definition file being deleted next)
```

- [ ] **Step 4: Delete the `LegacyAddressableEntity` definition file**

```bash
LEGACY_ENTITY_FILE=$(grep -rln "class LegacyAddressableEntity" src/ISEStudio/)
if [ -n "$LEGACY_ENTITY_FILE" ]; then
  git rm "$LEGACY_ENTITY_FILE"
fi

# Verify:
grep -rln "class LegacyAddressableEntity" src/ISEStudio/
# Expected: empty
```

- [ ] **Step 5: Verify build green (after entity base change)**

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo
# Expected: Build succeeded. 0 Error(s). 0 Warning(s)
cd ..
```

If build fails (likely an entity that uses `LegacyAddressableEntity`-specific members), inspect and fix inline. `LegacyAddressableEntity` was just a thin wrapper around `Entity` + `LegacyId` property — most entities should inherit cleanly.

- [ ] **Step 6: Verify unit tests pass (no test changes needed)**

```bash
cd src
dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo --no-build
# Expected: <UNIT_BEFORE> passed, 0 failed
cd ..
```

If test count drops or new failures appear, STOP. Investigate — likely a test references the deleted class.

- [ ] **Step 7: Commit Phase A.1**

```bash
git add -A
git status
# Verify: 24 entity files modified + LegacyAddressableEntity.cs deleted
git diff --cached --stat | tail -30
git commit -m "refactor(phase2): migrate 24 entities from LegacyAddressableEntity to Entity

Phase A.1 of Guid PK Phase 2 spec (commit d9a3d1b).

Phase 1 already moved the wire-side primary key from long LegacyId to
Guid Id, leaving LegacyAddressableEntity as a thin compatibility shim:
\`\`\`csharp
public abstract class LegacyAddressableEntity : Entity
{
    public long LegacyId { get; set; }
}
\`\`\`

Phase 2 deletes the shim. All 24 entity classes that previously inherited
LegacyAddressableEntity now inherit Entity directly. The LegacyId property
goes away (column drop happens in Phase B's EF migration).

Migration:
- grep + sed ': LegacyAddressableEntity' -> ': Entity' across 24 entity files
- git rm LegacyAddressableEntity.cs
- All 24 entity classes still pass existing tests (no API surface change)

Verifies:
- dotnet build src/ISEStudio.sln: 0 error / 0 warning
- dotnet test ISEStudio.Tests: <UNIT_BEFORE> passed, 0 failed

Spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md §4.1
Predecessor: Phase A.0 commit <A0_SHA>
Tag: pre-isestudio-rename -> fc06a73 (unchanged)"
```

Capture SHA: `<A1_SHA>`

---

## Task 4: Phase A.2 — LegacyIdAllocator retirement (call sites + DI + EntityConfigurations)

**Files:**
- Modify: ~18 service files that call `AllocateAndPersistAsync` / `AllocateManyAndPersistAsync`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs`
- Modify: DI registration (likely in `src/ISEStudio/Program.cs` or DI extension class)
- Delete: `src/ISEStudio/Infrastructure/Persistence/LegacyIdAllocator.cs`
- Delete: `src/ISEStudio.Tests/Infrastructure/LegacyIdAllocatorTests.cs`

**Interfaces:**
- Consumes: Phase A.1 applied (Task 3); entity base no longer references `LegacyId`
- Produces: `LegacyIdAllocator` class deleted; DI registration removed; `EntityConfigurations.cs` no longer configures `legacy_id` column or `ux_*_legacy_id` index; all 13+5 call sites use `Id = Guid.NewGuid()` + `_db.SaveChangesAsync()`; build green + tests green

- [ ] **Step 1: List all call sites of allocator methods**

```bash
grep -rn "AllocateAndPersistAsync\|AllocateManyAndPersistAsync" src/ \
  --include="*.cs" --exclude-dir=bin --exclude-dir=obj
# Expected: 18+ matches (13 documented + 5 historical leaks per allocator memory)
```

Record the list. The 5 historical leaks per [[ontopilot-allocator-missed-sites]] include:
- `ProviderService`
- 3× `ConflictService` (different methods)
- `KnowledgeService grant`

For each call site, the migration pattern is:

**Before** (per spec §4.2):
```csharp
var entity = new UserEntity { ... };
await _allocator.AllocateAndPersistAsync(entity, ct);
```

**After**:
```csharp
var entity = new UserEntity { Id = Guid.NewGuid(), ... };
await _db.SaveChangesAsync(ct);
```

Or for batch:
```csharp
var entities = new[] { new UserEntity { ... }, new UserEntity { ... } };
foreach (var e in entities) e.Id = Guid.NewGuid();
await _db.SaveChangesAsync(ct);
```

- [ ] **Step 2: Replace all call sites**

For each file from Step 1's grep output:
- Remove the `_allocator` / `LegacyIdAllocator` field/ctor injection if no other usage in the file
- Replace `await _allocator.AllocateAndPersistAsync(entity, ct)` with `entity.Id = Guid.NewGuid(); await _db.SaveChangesAsync(ct);`
- Replace `await _allocator.AllocateManyAndPersistAsync(entities, ct)` with the foreach pattern

Manual edit (sed is risky here because of generics + variable names). Use editor or scripted refactor. Verify each replacement compiles.

- [ ] **Step 3: Drop `legacy_id` column / index config from `EntityConfigurations.cs`**

```bash
# Find the config:
grep -nE 'HasColumnName\("legacy_id"\)|HasIndex.*legacy_id|ux_.*_legacy_id' \
  src/ISEStudio/Infrastructure/Persistence/Configurations/EntityConfigurations.cs
# Expected: 24 HasColumnName + 24 HasIndex entries (one per entity)
```

Remove all 48 lines. Each entry looks roughly like:
```csharp
builder.Property(e => e.LegacyId).HasColumnName("legacy_id");
builder.HasIndex(e => e.LegacyId).IsUnique().HasDatabaseName("ux_users_legacy_id");
```

After deletion, verify build.

- [ ] **Step 4: Remove DI registration `AddScoped<LegacyIdAllocator>`**

```bash
grep -rn "AddScoped<LegacyIdAllocator\|AddSingleton<LegacyIdAllocator\|AddTransient<LegacyIdAllocator" src/ISEStudio/ \
  --include="*.cs"
# Expected: 1+ matches
```

Delete each match.

- [ ] **Step 5: Verify build green**

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo
# Expected: Build succeeded. 0 Error(s). 0 Warning(s)
cd ..
```

If build fails, inspect:
- Missed call site (build error like `'LegacyIdAllocator' could not be found` or `'AllocateAndPersistAsync' does not exist`)
- DI removal missed an edge case
- EntityConfigurations had a typo

Fix inline + re-build.

- [ ] **Step 6: Delete `LegacyIdAllocator.cs` + tests**

```bash
git rm src/ISEStudio/Infrastructure/Persistence/LegacyIdAllocator.cs
git rm src/ISEStudio.Tests/Infrastructure/LegacyIdAllocatorTests.cs
```

- [ ] **Step 7: Verify zero `LegacyId` / `LegacyIdAllocator` references remain (besides exempt historical spec files)**

```bash
grep -rln "LegacyId\b\|LegacyIdAllocator\|AllocateAndPersistAsync\|AllocateManyAndPersistAsync\|AllocateAndPersist" src/ \
  --include="*.cs" --exclude-dir=bin --exclude-dir=obj
# Expected: empty
```

If matches remain, inspect each:
- A service file with a partial replacement (re-apply Step 2)
- An XML doc comment (delete the sentence or rewrite to remove `LegacyId` reference)
- A test file using `entity.LegacyId` accessor (delete the test or rewrite)

- [ ] **Step 8: Verify unit tests pass**

```bash
cd src
dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo --no-build
# Expected: <UNIT_BEFORE> - LegacyIdAllocator tests passed, 0 failed
cd ..
```

Record exact count after deletion: `<UNIT_AFTER>`. Should equal `<UNIT_BEFORE> - <count of LegacyIdAllocatorTests>`.

- [ ] **Step 9: Verify contract tests pass (Phase 1 wire stability guarantee)**

```bash
cd src
dotnet test ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj -c Release --nologo --no-build
# Expected: <CONTRACT_BEFORE> passed, 0 failed
cd ..
```

If contract tests regress, STOP. Phase 2 is supposed to NOT touch wire shape — investigate which ApiContract scenario references LegacyId.

- [ ] **Step 10: Commit Phase A.2**

```bash
git add -A
git status
# Verify: ~18 service files + EntityConfigurations.cs + Program.cs + DI extension modified,
#         LegacyIdAllocator.cs + LegacyIdAllocatorTests.cs deleted
git diff --cached --stat | tail -40
git commit -m "refactor(phase2): retire LegacyIdAllocator + drop legacy_id column config

Phase A.2 of Guid PK Phase 2 spec (commit d9a3d1b).

Wire-side primary key switched to Guid.Id in Phase 1; this commit retires
the DB-side compatibility shim:

- 13 documented call sites + 5 historical leaks (ProviderService +
  3x ConflictService + KnowledgeService grant) replaced with
  'entity.Id = Guid.NewGuid(); await _db.SaveChangesAsync(ct);'
- EntityConfigurations.cs: 24 HasColumnName(\"legacy_id\") +
  24 HasIndex(...ux_*_legacy_id) entries removed
- DI: AddScoped<LegacyIdAllocator> removed from Program.cs / DI extensions
- git rm LegacyIdAllocator.cs + LegacyIdAllocatorTests.cs

The legacy_id column itself stays until Phase B's EF migration drops it;
this commit only removes the code that *generates* and *configures* it.

Verifies:
- Gate 1 grep 'LegacyId|LegacyIdAllocator|AllocateAndPersistAsync':
  0 hits in src/ISEStudio/ (excluding bin/obj/.dll)
- dotnet build src/ISEStudio.sln: 0 error / 0 warning
- dotnet test ISEStudio.Tests: <UNIT_AFTER> passed, 0 failed
  (delta from <UNIT_BEFORE>: <N> LegacyIdAllocator tests removed)
- dotnet test ISEStudio.ApiContract.Tests: <CONTRACT_BEFORE> passed, 0 failed
  (Phase 1 wire shape unchanged — Phase 2 doesn't touch wire)

Spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md §4.2 §4.1
Predecessor: Phase A.1 commit <A1_SHA>
Tag: pre-isestudio-rename -> fc06a73 (unchanged)"
```

Capture SHA: `<A2_SHA>`

---

## Task 5: Phase A.3 — Bootstrap runbook update

**Files:**
- Modify: `docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md`

**Interfaces:**
- Consumes: Phase A.2 applied (Task 4)
- Produces: runbook §0 / §3.3 / §3.5 / §4.6 reflect that `legacy_id` no longer exists

- [ ] **Step 1: Update runbook §0 (banner note)**

```bash
# File: docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md
# Line 3-5 area: add a banner note below the existing 触发条件 description
```

Add after line 5 (or after the existing 触发条件 list):

```markdown
> **Phase 2 已部署(2026-08-26+):** 本 runbook 的 §3.3 schema 表与 §3.5 INSERT 模板中 `legacy_id` 列在 ISEStudio Phase 2 之后已不再存在。运维跑本 runbook 时,**不需要** 给 INSERT 写 `legacy_id` 列(EF migration `DropLegacyId` 已经把它删了)。
```

- [ ] **Step 2: Update §3.3 (drop `legacy_id` row from schema listing)**

In section 3.3, the schema listing currently shows:
```
 id             | uuid                     | not null |
 Username       | character varying(255)   | not null |           <-- PascalCase!
 DisplayName    | character varying(255)   |          |
 PasswordHash   | character varying(255)   | not null |
 IsAdmin        | boolean                  | not null | false
 Active         | boolean                  | not null | true
 CreatedAt      | timestamp with time zone | not null |
 legacy_id      | bigint                   | not null |           <-- snake_case
```

Delete the `legacy_id | bigint | not null |` line + the `<-- snake_case` annotation.

- [ ] **Step 3: Update §3.5 (drop `legacy_id` column from INSERT template)**

In the INSERT statement block (current ~line 134-156), remove:
- From the column list: `legacy_id,`
- From the VALUES list: `COALESCE((SELECT MAX(legacy_id) FROM users), 0) + 1`
- From the RETURNING list: `legacy_id,`

Add a brief comment after the SQL block:

```markdown
> Phase 2 之前 INSERT 还需要 `legacy_id`(由 `LegacyIdAllocator` 派发 `MAX+1`);Phase 2 已退役该列,直接省略即可。
```

- [ ] **Step 4: Update §4.6 (legacy volume discussion)**

In §4.6, find the line:
> 如果 `isestudio-postgres` volume 是空的但 `ontopilot_ontopilot-postgres`(rename 前的 volume)还在...

Append a new line at the end of §4.6:

```markdown
> **Phase 2 已部署:** `legacy_id` 列已删除,§3.5 的 INSERT 不再需要它;如果运维是从 pre-Phase 2 的 volume 还原数据,需要先跑 `iri sql-migrate` 把 legacy_id 列脱掉再走 §3.5。
```

- [ ] **Step 5: Verify markdownlint clean**

```bash
# Verify no compact tables (|---|), no trailing whitespace, all sections have blank lines
# (same warnings as Phase 2 spec — use padded | --- | separators)
```

- [ ] **Step 6: Commit Phase A.3**

```bash
git add docs/superpowers/runbooks/2026-08-25-fresh-deployment-bootstrap.md
git diff --cached --stat
git commit -m "docs(phase2): bootstrap runbook reflects legacy_id retirement

Phase A.3 of Guid PK Phase 2 spec (commit d9a3d1b).

The fresh-deployment bootstrap runbook describes the manual-SQL-INSERT
fallback path (when the docker-compose seed-admin profile is unavailable).
After Phase 2, the legacy_id column no longer exists in the users table,
so the INSERT template + schema listing would mislead operators.

Updates:
- §0: add banner note 'Phase 2 已部署 — legacy_id 已不再存在'
- §3.3: drop legacy_id row from users table schema listing
- §3.5: drop legacy_id from INSERT column list / VALUES / RETURNING,
        add Phase 2 comment explaining the historical context
- §4.6: add note about restoring from pre-Phase 2 volume via iri sql-migrate

Other runbook sections (触发条件 / 密码约束 / 踩坑 / Lesson learned)
unchanged — they describe fail-closed behavior that doesn't depend on
legacy_id.

Scope limited to docs/superpowers/runbooks/ per user direction in spec
review; historical spec files (docs/superpowers/specs/2026-08-25-...)
and Python baseline (docs/migration/, migration/scripts/) preserved.

Spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md §2.1
Predecessor: Phase A.2 commit <A2_SHA>
Tag: pre-isestudio-rename -> fc06a73 (unchanged)"
```

Capture SHA: `<A3_SHA>`

---

## Task 6: Phase B — EF migration `DropLegacyId`

**Files:**
- Create: `src/ISEStudio/Infrastructure/Persistence/Migrations/20260826HHMMSS_DropLegacyId.cs`
- Create: `src/ISEStudio/Infrastructure/Persistence/Migrations/20260826HHMMSS_DropLegacyId.Designer.cs`
- Modify: `src/ISEStudio/Infrastructure/Persistence/Migrations/ISEStudioDbContextModelSnapshot.cs`

**Interfaces:**
- Consumes: Phase A complete (Tasks 2-5)
- Produces: EF migration that drops `legacy_id` column + `ux_*_legacy_id` index from all 24 tables; migration applies cleanly; contract tests still green

- [ ] **Step 1: Regenerate the model snapshot**

```bash
cd src
# (rebuild first to ensure EntityConfigurations.cs changes are baked)
dotnet build ISEStudio.sln -c Release --nologo

# Optional: confirm snapshot delta
diff <(git show HEAD:src/ISEStudio/Infrastructure/Persistence/Migrations/ISEStudioDbContextModelSnapshot.cs) \
     src/ISEStudio/Infrastructure/Persistence/Migrations/ISEStudioDbContextModelSnapshot.cs | head -50
# Expected: snapshot now lacks LegacyId property references across 24 entities
cd ..
```

If snapshot didn't auto-update, manually re-generate via:
```bash
cd src
dotnet ef migrations remove --force  # only if there's a stale pending migration
# (otherwise snapshot is auto-updated on `migrations add` next step)
cd ..
```

- [ ] **Step 2: Add the `DropLegacyId` migration**

```bash
cd src
dotnet ef migrations add DropLegacyId \
  --project src/ISEStudio \
  --startup-project src/ISEStudio \
  --context ISEStudioDbContext

# Verify file created:
ls -la src/ISEStudio/Infrastructure/Persistence/Migrations/ | grep -i "droplegacyid"
# Expected: 20260826HHMMSS_DropLegacyId.cs + 20260826HHMMSS_DropLegacyId.Designer.cs

# Read the generated Up() body:
cat src/ISEStudio/Infrastructure/Persistence/Migrations/20260826HHMMSS_DropLegacyId.cs
cd ..
```

- [ ] **Step 3: Audit the generated migration (Gate 6)**

Per spec §4.3 + Gate 6, the migration must contain **only** `DropColumn` + `DropIndex`, no `CreateTable` / `InsertData`:

```bash
grep -E "CreateTable|InsertData|AddColumn|RenameColumn" \
  src/ISEStudio/Infrastructure/Persistence/Migrations/20260826HHMMSS_DropLegacyId.cs
# Expected: empty (only DropColumn + DropIndex)
```

If `CreateTable` / `InsertData` appears (EF detected schema drift beyond just dropping legacy_id), apply the manual patch per spec §4.3:

```csharp
// Patch Up() to only contain:
foreach (var table in new[] { "users", "chunks", /* ... 22 more table names ... */ })
{
    migrationBuilder.DropIndex(name: $"ux_{table}_legacy_id", table: table);
    migrationBuilder.DropColumn(name: "legacy_id", table: table);
}
```

Use a script to enumerate the 24 tables:

```bash
# Tables that had legacy_id (verify by looking at the snapshot before Phase A):
grep -E 'Table\(.*"' src/ISEStudio/Infrastructure/Persistence/Migrations/ISEStudioDbContextModelSnapshot.cs \
  | grep -v "MigrationsHistory" \
  | sed -E 's/.*Table\("(.*)",.*/\1/' \
  | sort -u
# Expected: 24 table names
```

- [ ] **Step 4: Verify migration applies cleanly (against a fresh DB)**

```bash
cd src
# Drop the existing dev DB if any
dotnet ef database drop --project src/ISEStudio --startup-project src/ISEStudio --force --no-build

# Apply all migrations (including the new DropLegacyId)
dotnet ef database update --project src/ISEStudio --startup-project src/ISEStudio --no-build
# Expected: "Done." with no errors

# Verify legacy_id is GONE from all 24 tables:
dotnet ef migrations script --project src/ISEStudio --startup-project src/ISEStudio --no-build \
  | grep -c "DROP COLUMN.*legacy_id"
# Expected: 24 (one per table)

dotnet ef migrations script --project src/ISEStudio --startup-project src/ISEStudio --no-build \
  | grep -c "DROP INDEX.*legacy_id"
# Expected: 24 (one per table)
cd ..
```

- [ ] **Step 5: Verify contract tests still green (Phase 2 is invisible at wire layer)**

```bash
cd src
dotnet test ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj -c Release --nologo --no-build
# Expected: <CONTRACT_BEFORE> passed, 0 failed
cd ..
```

If contract tests regress, investigate — likely a fixture referencing `legacy_id` literal:

```bash
grep -rn "legacy_id" src/ISEStudio.ApiContract.Tests/ --include="*.cs"
# Should be empty (Phase 1 already removed wire references)
```

- [ ] **Step 6: Commit Phase B**

```bash
git add -A
git status
git diff --cached --stat | tail -10
git commit -m "feat(phase2): DropLegacyId EF migration + model snapshot regen

Phase B of Guid PK Phase 2 spec (commit d9a3d1b).

Adds EF Core migration 20260826HHMMSS_DropLegacyId that drops the
legacy_id bigint column + ux_*_legacy_id unique index from all 24
tables. Migration body audited to contain only DropColumn + DropIndex
per spec Gate 6 — manual patch applied if EF generated CreateTable /
InsertData due to schema drift detection.

Model snapshot regenerated: ISEStudioDbContextModelSnapshot.cs no longer
references LegacyId property across 24 entities.

Verifies:
- dotnet ef database update applies cleanly: 'Done.' with 0 errors
- Migration script: 24 DROP COLUMN legacy_id + 24 DROP INDEX ux_*_legacy_id
- Gate 6 grep CreateTable|InsertData: 0 hits in DropLegacyId.cs
- dotnet test ApiContract.Tests: <CONTRACT_BEFORE> passed (wire shape unchanged)

Spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md §4.3
Predecessor: Phase A.3 commit <A3_SHA>
Tag: pre-isestudio-rename -> fc06a73 (unchanged)"
```

Capture SHA: `<B_SHA>`

---

## Task 7: Phase C — IriSqlMigrator cleanup + verifier baseline regen

**Files:**
- Modify: `src/ISEStudio.Migration/Iri/IriSqlMigrator.cs`
- Modify: `src/ISEStudio.Migration/Iri/IriSqlVerifier.cs`
- Modify: baseline file (likely `src/ISEStudio.Migration/Iri/baseline.json` or similar — verify by reading IriSqlVerifier.cs)
- Delete: corresponding tests in `src/ISEStudio.Tests/Migration/` + `src/ISEStudio.IntegrationTests/Migration/`

**Interfaces:**
- Consumes: Phase B applied (Task 6)
- Produces: `ColumnsToRewrite` no longer contains `legacy_id`; `IriSqlVerifier` baseline SHA-256 regenerated without `legacy_id` row; ~5-10 `It("legacy_id_*")` test blocks deleted; IRI migrator integration tests pass

- [ ] **Step 1: Inspect current `IriSqlMigrator.cs` ColumnsToRewrite list**

```bash
grep -n "ColumnsToRewrite\|legacy_id" src/ISEStudio.Migration/Iri/IriSqlMigrator.cs | head -30
# Should show ColumnsToRewrite list containing "legacy_id" entry
```

- [ ] **Step 2: Remove `legacy_id` from ColumnsToRewrite**

In `src/ISEStudio.Migration/Iri/IriSqlMigrator.cs`:

```csharp
// Before:
private static readonly IReadOnlyList<string> ColumnsToRewrite = new[]
{
    "legacy_id",  // ← Phase 2: delete this line
    "username",
    "displayname",
    // ...
};

// After:
private static readonly IReadOnlyList<string> ColumnsToRewrite = new[]
{
    "username",
    "displayname",
    // ...
};
```

- [ ] **Step 3: Inspect `IriSqlVerifier.cs` for baseline structure**

```bash
grep -n "legacy_id\|baseline\|sha256\|SHA256" src/ISEStudio.Migration/Iri/IriSqlVerifier.cs | head -20
```

The baseline is likely either:
- An embedded `expected` dictionary with per-column SHA-256 rows
- A JSON file path read at runtime (e.g. `IriBaseline.json`)

Identify which and proceed accordingly.

- [ ] **Step 4: Remove `legacy_id` row from baseline**

If embedded in code: delete the `"legacy_id" -> <sha256>` entry.

If in a JSON file: edit the JSON to remove the `legacy_id` key.

- [ ] **Step 5: Regenerate baseline SHA-256**

Per spec §4.4 + Gate 5 mitigation in §7.1, run:

```bash
cd src
dotnet run --project ISEStudio.Migration -- sql-smoke-check --update-baseline
# (or equivalent — read IriSqlVerifier.cs main to find the CLI entry point)
cd ..
```

The `--update-baseline` flag writes a new SHA-256 based on the current state (post-Phase-2 schema). Verify the file/embedded JSON changed.

- [ ] **Step 6: Delete legacy_id-specific tests**

```bash
# Find all tests that reference legacy_id:
grep -rln "legacy_id" src/ISEStudio.Tests/Migration/ src/ISEStudio.IntegrationTests/Migration/ \
  --include="*.cs"
# Expected: 5-10 test files (each likely has It("legacy_id_rewrites", ...))
```

For each file:
- Open it
- Remove the `It("legacy_id_*", ...)` blocks (whole `It(...)` invocation including the body)
- Keep any setup / helpers (they may be used by other tests)
- If the file ONLY has legacy_id tests + setup that's not used elsewhere, delete the whole file with `git rm`

```bash
# Example for a file that ONLY has legacy_id tests:
git rm src/ISEStudio.Tests/Migration/IriLegacyIdRewriteTests.cs
```

- [ ] **Step 7: Verify IriSqlMigrator builds + tests pass**

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo
# Expected: Build succeeded. 0 Error(s). 0 Warning(s)

dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo --no-build
# Expected: <UNIT_AFTER> - <MIGRATION_TESTS_DELETED> passed, 0 failed

dotnet test ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj -c Release --nologo --no-build
# Expected: <INTEGRATION_BEFORE> - <MIGRATION_INTEGRATION_TESTS_DELETED> passed, 0 failed
cd ..
```

- [ ] **Step 8: Verify Gate 3 (`"legacy_id"` quoted string zero hits)**

```bash
grep -rn '"legacy_id"' src/ISEStudio/ src/ISEStudio.Migration/ \
  --include="*.cs" --exclude-dir=bin --exclude-dir=obj
# Expected: empty
# (Only allowed: src/ISEStudio/Infrastructure/Persistence/Migrations/20260816140916_InitialCompatibility.cs historical migration)
```

If a non-historical hit remains, fix.

- [ ] **Step 9: Commit Phase C**

```bash
git add -A
git status
git diff --cached --stat
git commit -m "refactor(phase2): retire legacy_id from IriSqlMigrator + verifier baseline

Phase C of Guid PK Phase 2 spec (commit d9a3d1b).

IriSqlMigrator.ColumnsToRewrite used to include legacy_id for IRI-driven
column rewrites during the brand rename slice. With the legacy_id column
itself gone (Phase B), the rewrite entry is dead code.

Changes:
- IriSqlMigrator.cs: drop 'legacy_id' line from ColumnsToRewrite
- IriSqlVerifier.cs + baseline file: drop legacy_id row + regenerate
  SHA-256 baseline via 'iri sql-smoke-check --update-baseline'
- 5-10 'It(\"legacy_id_*\", ...)' test blocks deleted from
  src/ISEStudio.Tests/Migration/ + IntegrationTests/Migration/

The remaining ColumnsToRewrite entries (username / displayname / ...)
are unchanged — they correspond to live DB columns.

Verifies:
- Gate 3 grep '\"legacy_id\"': 0 hits in non-historical cs files
  (InitialCompatibility migration is exempt)
- dotnet build src/ISEStudio.sln: 0 error / 0 warning
- dotnet test Tests: <UNIT_AFTER - MIGRATION_TESTS_DELETED> passed
- dotnet test IntegrationTests: <INTEGRATION_BEFORE - MIGRATION_INTEGRATION_TESTS_DELETED> passed

Spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md §4.4
Predecessor: Phase B commit <B_SHA>
Tag: pre-isestudio-rename -> fc06a73 (unchanged)"
```

Capture SHA: `<C_SHA>`

---

## Task 8: Phase D — Full test suite + branch review

**Files:**
- Touch: none (review + test only)

**Interfaces:**
- Consumes: Phase A + B + C applied (Tasks 2-7)
- Produces: full test suite green; branch review approved

- [ ] **Step 1: Run the full test suite**

```bash
cd src
dotnet build ISEStudio.sln -c Release --nologo
# Expected: 0 error / 0 warning (Gate 4)

dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo --no-build
# Expected: <FINAL_UNIT_COUNT> passed (Gate 5 partial)
dotnet test ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj -c Release --nologo --no-build
# Expected: <FINAL_CONTRACT_COUNT> passed (Gate 5 partial)
dotnet test ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj -c Release --nologo --no-build
# Expected: <FINAL_INTEGRATION_COUNT> passed (Gate 5 partial)
cd ..
```

Where:
- `<FINAL_UNIT_COUNT>` = `<UNIT_BEFORE> - <LegacyIdAllocatorTests deleted in A.2>`
- `<FINAL_CONTRACT_COUNT>` = `<CONTRACT_BEFORE>` (no contract tests should change)
- `<FINAL_INTEGRATION_COUNT>` = `<INTEGRATION_BEFORE> - <legacy_id It() blocks deleted in C>`

If any fail, STOP. Investigate + fix + amend the relevant Phase commit (no new commits for fixups).

- [ ] **Step 2: Dispatch a branch reviewer (opus)**

Use `superpowers:requesting-code-review` skill. Hand the reviewer:
- Full diff: `git diff pre-isestudio-rename..HEAD`
- Spec: `docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md`
- Plan: this file
- Memory pointers: [[ontopilot-allocator-missed-sites]] for the 5 leak sites

Wait for review verdict. Address load-bearing findings inline (amend Phase commits, no new commits). Park non-load-bearing observations in the SDD ledger.

- [ ] **Step 3: Capture reviewer verdict in spec §8 Decision Log**

If the reviewer added a Decision, append to spec §8 (separate commit `docs(phase2): log reviewer decisions`).

---

## Task 9: Phase E — Runtime smoke test

**Files:**
- Touch: none (smoke only)

**Interfaces:**
- Consumes: Phase A + B + C applied + Phase D review passed
- Produces: docker compose stack starts fresh against empty volumes; admin seed succeeds; `/api/health` returns 200; login works; `isestudio_session` cookie set

- [ ] **Step 1: (Optional but recommended for staging/prod) snapshot the current DB**

```bash
docker compose stop isestudio isestudio-migrate
docker exec ontopilot-postgres-1 pg_dump -U isestudio -d isestudio \
  --no-owner --no-acl -Fc > ontopilot-pre-phase2-$(date +%Y%m%d).dump
# Confirm file size > 0
ls -lh ontopilot-pre-phase2-*.dump
```

Skip this step on local dev if data loss is acceptable (per spec §3.1).

- [ ] **Step 2: Tear down + wipe volumes (Gate 7 prerequisite)**

```bash
docker compose down
docker volume rm ontopilot_isestudio-postgres ontopilot_isestudio-data ontopilot_isestudio-minio
docker volume ls | grep -E "isestudio-(postgres|data|minio)"
# Expected: empty (volumes removed)
```

- [ ] **Step 3: Build images + start the stack**

```bash
docker compose build
docker compose up -d

# Wait for migrate container to finish + backend to become healthy
docker compose ps
# Expected within ~60s:
#   postgres           Up (healthy)
#   minio              Up (healthy)
#   isestudio-migrate  Exited (0)
#   isestudio          Up (healthy)
#   frontend           Up
```

If `isestudio` is `Restarting`, check `docker compose logs isestudio --tail=30`:
- If `Bootstrap required: the users table is empty`: proceed to Step 4 (expected pre-seed)
- If anything else: STOP and investigate

- [ ] **Step 4: Seed the first admin (per bootstrap runbook)**

```bash
# Use the seed-admin profile (PRIMARY path per runbook §0):
docker compose --profile bootstrap run --rm seed-admin
# Expected: logs 'admin user created' (or 'already exists' if idempotent retry)
# Exit code: 0

# Verify user exists:
docker exec ontopilot-postgres-1 psql -U isestudio -d isestudio -c \
  'SELECT "Username", "IsAdmin", "Active" FROM users;'
# Expected: 1 row (admin | t | t)
```

- [ ] **Step 5: Health check**

```bash
curl -sS -i http://127.0.0.1:8080/api/health
# Expected: HTTP/1.1 200 OK with JSON body {"status":"healthy",...}
```

- [ ] **Step 6: Login + session cookie check**

```bash
curl -sS -i -X POST http://127.0.0.1:8080/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"<seed-admin-password-from-.env>"}'
# Expected:
#   HTTP/1.1 200 OK
#   Set-Cookie: isestudio_session=<...>; path=/; httponly
#   {"id":"<uuid>","username":"admin",...}
```

- [ ] **Step 7: Verify Gate 7 (full smoke sequence)**

```bash
docker compose down -v && \
  docker compose --profile bootstrap run --rm seed-admin && \
  docker compose up -d --build && \
  sleep 10 && \
  curl -s http://127.0.0.1:8080/api/health
# Expected: HTTP 200 + JSON body
```

- [ ] **Step 8: Tear down smoke environment**

```bash
docker compose down -v
# Leave volumes wiped; clean environment for next phase / handoff
```

---

## Task 10: Final verification + memory + handoff

**Files:**
- Create: `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\ontopilot-phase2-guid-pk-retirement.md`
- Modify: `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\MEMORY.md`

**Interfaces:**
- Consumes: Phase A-E complete (Tasks 2-9)
- Produces: all 7 gates re-verified; memory file documenting the slice; MEMORY.md updated; ready for `superpowers:finishing-a-development-branch`

- [ ] **Step 1: Re-run all 7 gates one final time**

```bash
echo "=== Gate 1: 代码无 LegacyId 残留 ==="
grep -rln "LegacyId\b\|LegacyIdAllocator\|AllocateAndPersistAsync\|AllocateManyAndPersistAsync" \
  src/ --include="*.cs" --exclude-dir=bin --exclude-dir=obj
# Expected: empty

echo "=== Gate 2: 实体无 legacy_id 列名引用 ==="
grep -rln '"legacy_id"' src/ISEStudio/ src/ISEStudio.Migration/ \
  --include="*.cs" --exclude-dir=bin --exclude-dir=obj
# Expected: empty (only allowed: InitialCompatibility historical migration)

echo "=== Gate 3: dotnet build 干净 ==="
cd src && dotnet build ISEStudio.sln -c Release --nologo && cd ..
# Expected: 0 error / 0 warning

echo "=== Gate 4: 测试全绿 ==="
cd src
dotnet test ISEStudio.Tests/ISEStudio.Tests.csproj -c Release --nologo --no-build
dotnet test ISEStudio.ApiContract.Tests/ISEStudio.ApiContract.Tests.csproj -c Release --nologo --no-build
dotnet test ISEStudio.IntegrationTests/ISEStudio.IntegrationTests.csproj -c Release --nologo --no-build
cd ..

echo "=== Gate 5: EF migration 只 DROP 不 CREATE ==="
grep -E "CreateTable|InsertData|AddColumn|RenameColumn" \
  src/ISEStudio/Infrastructure/Persistence/Migrations/20260826HHMMSS_DropLegacyId.cs
# Expected: empty

echo "=== Gate 6: docker compose config 干净 ==="
docker compose config --quiet

echo "=== Gate 7: pre-isestudio-rename tag 保留 ==="
git rev-parse pre-isestudio-rename
# Expected: <fc06a73-hash> (NOT current HEAD)
```

- [ ] **Step 2: Verify all Phase A/B/C commit SHAs in git log**

```bash
git log --oneline pre-isestudio-rename..HEAD
# Expected: <A0_SHA> <A1_SHA> <A2_SHA> <A3_SHA> <B_SHA> <C_SHA> (+ optional reviewer-decisions commit)
```

- [ ] **Step 3: Write memory file documenting the slice**

Create `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\ontopilot-phase2-guid-pk-retirement.md` with this structure:

```markdown
---
name: ontopilot-phase2-guid-pk-retirement
description: "Guid PK Phase 2 — Legacy 字段退役 (2026-08-26):删 legacy_id 列 + 退役 LegacyIdAllocator + 简化 Entity 基类 + IriSqlMigrator 同步 + 6 file renames + bootstrap runbook 更新,~5 atomic commits on dotnet branch"
metadata:
  type: project
  modified: 2026-08-26T...
---

# Guid PK Phase 2 — Legacy 字段退役 (2026-08-26)

## Commits (5 atomic)
- Phase A.0 (chore): 6 OnToPilot* filenames → ISEStudio* (git mv)
- Phase A.1 (refactor): 24 entities LegacyAddressableEntity → Entity
- Phase A.2 (refactor): LegacyIdAllocator + 13+5 call sites + DI + EntityConfigurations
- Phase A.3 (docs): bootstrap runbook §0/§3.3/§3.5/§4.6 legacy_id removal
- Phase B (feat): DropLegacyId EF migration + model snapshot regen
- Phase C (refactor): IriSqlMigrator ColumnsToRewrite + IriSqlVerifier baseline SHA-256 regen

## Decisions
- D1: 清 volume,接受数据丢失(staging/prod 需 pg_dump 先行)
- D2: IriSqlMigrator legacy_id 重写分支同步退役
- D3: EF DropLegacyId 只 DROP,不重建(若 EF 输出 CREATE TABLE 人工 patch)
- D4: 历史 EF migration InitialCompatibility 保留(append-only)
- D5: pre-isestudio-rename + pre-python-retirement tag 都保留,不打新 tag

## Boundaries (per spec §2.2)
- Runbook scope: 仅 docs/superpowers/runbooks/
- 历史 spec / Python baseline: docs/migration/, migration/scripts/, docs/superpowers/specs/2026-08-25-* 全部不动

## Verification gates (all 7 passed)
1. 代码无 LegacyId 残留
2. 实体无 legacy_id 列名引用(InitialCompatibility 例外)
3. dotnet build 干净
4. 测试全绿(<FINAL_UNIT> + <FINAL_CONTRACT> + <FINAL_INTEGRATION>)
5. EF migration 只 DROP 不 CREATE
6. docker compose config 干净
7. pre-isestudio-rename tag 保留在 fc06a73

## File rename (Phase A.0)
- 6 OnToPilot* filenames → ISEStudio*:
  - DbContext / DbContextFactory
  - Mcp{Prompts,Resources,Tools}
  - Serialization/JsonContext

## Unlock / next steps
- Brand rename 在所有层 clean(包括数据层)
- GitHub repo rename follow-up(只剩仓库设置层面)

## Link
- [[ontopilot-isestudio-rename]] (上游:brand rename 解锁 Phase 2)
- [[ontopilot-allocator-missed-sites]] (5 个补漏点是 Phase A.2 清单)
- [[ontopilot-apicontract-prebaseline-fix]] (Phase 0.5 baseline 锁定延续)
- spec: docs/superpowers/specs/2026-08-26-guid-primary-key-phase-2-design.md
- plan: docs/superpowers/plans/2026-08-26-guid-primary-key-phase-2.md
```

- [ ] **Step 4: Add entry to MEMORY.md index**

Append a new bullet at the bottom of `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\MEMORY.md`:

```markdown
- [ontopilot-phase2-guid-pk-retirement](ontopilot-phase2-guid-pk-retirement.md) — Guid PK Phase 2:删 legacy_id 列 + 退役 LegacyIdAllocator + 简化 Entity 基类 + IriSqlMigrator 同步 + 6 file renames + bootstrap runbook 更新 (2026-08-26,~5 atomic commits,24 entity 退役 LegacyAddressableEntity,13+5 call sites 替换为 Guid.NewGuid,Gate 7 全过)
```

- [ ] **Step 5: Report slice complete + handoff**

Report to user:
- 5 commit SHAs (`<A0_SHA>` / `<A1_SHA>` / `<A2_SHA>` / `<A3_SHA>` / `<B_SHA>` / `<C_SHA>`) + reviewer-decisions commit (if any)
- Pre/post test counts: `<UNIT_BEFORE>` → `<FINAL_UNIT_COUNT>` etc.
- All 7 gates passed
- Memory file written + MEMORY.md updated
- Ready for `superpowers:finishing-a-development-branch`

DO NOT invoke `finishing-a-development-branch` automatically — wait for user instruction.

---

## Self-Review (per writing-plans skill §"Self-Review")

### Spec coverage check

| Spec § | Requirement | Plan coverage |
|---|---|---|
| §2.1 代码层 | LegacyIdAllocator.cs 删除 | Task 4 Steps 6-7 |
| §2.1 代码层 | LegacyAddressableEntity.cs 删除 | Task 3 Step 4 |
| §2.1 代码层 | 24 entity 基类简化 | Task 3 Step 3 |
| §2.1 代码层 | 13+5 allocator call sites | Task 4 Steps 1-2 |
| §2.1 代码层 | EntityConfigurations legacy_id 列 / 索引删除 | Task 4 Step 3 |
| §2.1 代码层 | DI 移除 | Task 4 Step 4 |
| §2.1 Stage 3 territory | 6 file renames | Task 2 Steps 1-2 |
| §2.1 EF 迁移 | DropLegacyId migration | Task 6 Steps 1-3 |
| §2.1 IriSqlMigrator | ColumnsToRewrite + verifier baseline | Task 7 Steps 2-5 |
| §2.1 测试 | LegacyId allocator tests 删除 | Task 4 Step 6 + Step 8 |
| §2.1 Runbook | bootstrap runbook §0/§3.3/§3.5/§4.6 | Task 5 Steps 1-4 |
| §2.1 Compose | docker compose down -v | Task 9 Step 2 |
| §2.2 OUT | EF migration InitialCompatibility 保留 | implicit (excluded from `migrations add`) |
| §2.2 OUT | Python baseline / historical spec | implicit (sed exclusions + manual) |
| §2.2 OUT | pre-isestudio-rename tag | implicit (not moved) |
| §3 数据迁移 | 清 volume + cutover + smoke | Task 9 Steps 1-7 |
| §4.1 Entity 简化 | sed + delete | Task 3 |
| §4.2 Allocator 调用点移除 | 13+5 sites | Task 4 |
| §4.3 EF migration 生成 + audit | only DROP | Task 6 Step 3 |
| §4.4 IriSqlMigrator | ColumnsToRewrite + verifier | Task 7 |
| §5 验证 gates | 7 gates | Task 10 Step 1 |
| §6 任务分解 | 5 phases | Tasks 2-9 |
| §7 风险 | EF CREATE TABLE patch | Task 6 Step 3 |
| §7 风险 | IRI baseline SHA 漂移 | Task 7 Step 5 |
| §7 风险 | ApiContract 回归 | Task 6 Step 5 + Task 8 Step 1 |
| §7 风险 | Docker volume 误删 | Task 9 Step 1 (snapshot) + Step 2 (verify ls) |
| §8 Decision Log | D1-D5 | captured in Global Constraints |

**Coverage gap:** none. Every spec requirement maps to a Step.

### Placeholder scan

- No "TBD", "TODO", "implement later", "fill in details", "add appropriate error handling", "similar to Task N" markers.
- All code blocks contain real commands, real sed patterns, real expected outputs.
- Where a step requires judgment (e.g. "find all 24 HasColumnName lines and delete them"), the step provides the grep + the expected count.

### Type consistency

- `LegacyAddressableEntity` consistent across: Global Constraints (target of retirement), Task 3 (deletion site), Task 4 (no further references).
- `LegacyIdAllocator` consistent across: Global Constraints, Task 4 (deletion site), Task 7 (no further references).
- `ISEStudioDbContext` consistent across: Task 1 (build target), Task 2 (file rename target), Task 6 (EF migration context name).
- `OnToPilotDbContext.cs` consistent across: Global Constraints (rename source), Task 2 (rename source), Task 4 (no further references after A.0 commit).
- File paths in `src/ISEStudio/...` consistent across all tasks.

Plan complete and self-reviewed.