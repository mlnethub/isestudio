# OnToPilot 主键统一到 Guid 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 wire surface (JSON / URL / FK) 的主键从 `long LegacyId` 切到 `Guid Id`,同时保留 DB schema 不变 + `LegacyIdAllocator` 内部继续运行(防 race window 重开)。

**Architecture:** 机械式重命名(Approach A1)。所有 wire DTO 中 `LegacyId: long` 按 4 条规则(详见 spec §4.2)转为 `Id: Guid`;URL path 加 `:guid` route constraint;Service 层 `long id` 全部改为 `Guid id`;`LegacyIdAllocator` + `ux_*_legacy_id` 索引保留。DB 不动。

**Tech Stack:** ASP.NET Core 10 / EF Core / Npgsql;React + TypeScript frontend;xUnit + Testcontainers(集成测试);System.Text.Json(GuidConverter 内置)。

**Spec:** [docs/superpowers/specs/2026-08-20-guid-primary-key-design.md](../specs/2026-08-20-guid-primary-key-design.md)

---

## Global Constraints

- **DB schema 不变**:`legacy_id` 列 + `ux_*_legacy_id` UNIQUE 索引保留
- **`LegacyIdAllocator` 实现不动**:仍然为新行赋 long 唯一索引值
- **不动 Python 后端**(已废弃)
- **大爆炸迁移**(无并行旧 wire)
- **wire shape 完全无 `legacy_id` 字段**
- **Guid 用 kebab-case 字符串格式**(System.Text.Json 默认,例:`"abc-123-..."`)
- **Phase 0 必须先完成**(contract test baseline 干净),Phase 1 才能开始
- **每完成一个 task 必须 `dotnet build OnToPilot.sln` 通过**
- **每完成一个 macro task 必须跑对应测试套件**

---

## Phase 0 — 清理 contract test baseline(独立 commit,在 Phase 1 之前)

### Task 1: 把 validation `InvalidOperationException` 包成 4xx envelope

**Files:**
- Modify: `src/OnToPilot/Api/FastApiErrorMiddleware.cs`
- Possibly: `src/OnToPilot/Conflicts/ConflictService.cs:577`
- Possibly: `src/OnToPilot/Knowledge/KnowledgeService.cs:356,121`
- Possibly: `src/OnToPilot/Providers/ProviderService.cs:80,329`

**目标:** contract test 现在发空 body,期望 4xx 但拿到 500。把 validation errors 包成 4xx。

**Step 1:** 找 `FastApiErrorMiddleware` 的 exception 映射分支
```bash
grep -n "InvalidOperationException" src/OnToPilot/Api/FastApiErrorMiddleware.cs
```

**Step 2:** 在 mapper 中加 4xx 分支(用 `ValidationException` 或新异常类型):
```csharp
// 找到现有 catch 链,加一条:
catch (ValidationException ex) {
    await WriteErrorAsync(context, StatusCodes.Status400BadRequest, ex.Message);
    return;
}
```

**Step 3:** 把 service 里的 `throw new InvalidOperationException("name is required.")` 改成 `throw new ValidationException("name is required.")`(在已知 validation 失败点)。

**Step 4:** 编译 + 跑 contract test 验证:
```bash
dotnet build OnToPilot.sln
dotnet test OnToPilot.ApiContract.Tests --filter "FullyQualifiedName~providers" --logger "console;verbosity=minimal"
```
期望:之前 500 的 provider 路径现在 4xx(或具体见基线)

**Step 5:** Commit:
```bash
git add -A
git commit -m "fix(api): map validation errors to 4xx envelope (closes pre-existing 500s)"
```

---

### Task 2: `Program.cs` 在 Testing 环境跳过 Oxigraph 初始化

**Files:**
- Modify: `src/OnToPilot/Program.cs`(line 408 附近 — `StoreWrapper` 注册处)

**Step 1:** 找现有注册:
```bash
grep -n "StoreWrapper\|Oxigraph" src/OnToPilot/Program.cs
```

**Step 2:** 改成环境感知:
```csharp
// Before:
builder.Services.AddSingleton<StoreWrapper>(_ => new StoreWrapper(workspacePath));

// After:
if (builder.Environment.IsDevelopment() || builder.Environment.IsProduction())
{
    builder.Services.AddSingleton<StoreWrapper>(_ => new StoreWrapper(workspacePath));
}
else
{
    // Testing: 不开 RocksDB-backed Store,改用 null-object 让 conflict detect
    // 走 "return what's already in DB" fallback (ConflictService:102-108)
    builder.Services.AddSingleton<StoreWrapper?>(_ => null);
}
```

注意:`ConflictService` 当前已经接受 `StoreWrapper?`,所以 null 安全。

**Step 3:** 编译:
```bash
dotnet build OnToPilot.sln
```

**Step 4:** 跑之前 500 的 vocabulary/conflict 路径:
```bash
dotnet test OnToPilot.ApiContract.Tests --filter "FullyQualifiedName~vocabulary" --logger "console;verbosity=minimal"
```
期望:Oxigraph 路径 500 消失

**Step 5:** Commit:
```bash
git add src/OnToPilot/Program.cs
git commit -m "chore(testing): skip Oxigraph init in Testing env (closes contract 500s)"
```

---

### Task 3: Phase 0 验证 — contract test 全部通过

**Step 1:** 跑完整 contract test:
```bash
dotnet test OnToPilot.ApiContract.Tests --logger "console;verbosity=minimal"
```

**Step 2:** 期望输出:`失败: 0,通过: 119+,已跳过: 0`(以前 69 个 500 全消失)

如果还有剩余 500,逐个看 stack trace 顶部异常,可能还有 `JsonSchemaAssert` 之类的问题需要单独修。

**Step 3:** Commit(如果改了任何 baseline):
```bash
git add -A
git commit -m "test(contract): regenerate baseline after 4xx + Oxigraph skip"
```

---

## Phase 1A — Wire DTO 重命名(Backend)

### Task 4: Provider DTO

**Files:**
- Modify: `src/OnToPilot/Providers/ProviderDtos.cs`

**Rename 规则**(按 spec §4.2 套用):
- ProviderOut 当前有 `Guid Id` + `long LegacyId` — 仅删除 `LegacyId`,`Id` 保持。

**Step 1:** 删除 `LegacyId` 字段(已经从 entity 暴露 `Id`):
```csharp
// Before:
public sealed record ProviderOut(
    Guid Id,
    long LegacyId,        // ← 删除
    string Name,
    ...);

// After:
public sealed record ProviderOut(
    Guid Id,
    string Name,
    ...);
```

**Step 2:** 编译 — 期望报 caller 错(ProviderService / InternalOperationDispatcher):
```bash
dotnet build OnToPilot.sln 2>&1 | grep "error CS"
```

**Step 3:** 不在此 task 修 caller,先 commit 这一步(用 `#pragma warning disable` 或让 build 失败但记录哪些 caller 受影响)。

实际上更好的做法:**每个 DTO 文件 + 它的 caller 在同一 task 里改完**,避免 dangling reference。

**修改方案 — 一次性改 Provider DTO + caller:**

Files to modify in this task:
- `src/OnToPilot/Providers/ProviderDtos.cs`(删 LegacyId)
- `src/OnToPilot/Providers/ProviderService.cs`(无 DTO 构造,直接映射 entity→DTO,可不动)
- `src/OnToPilot/Integration/InternalOperationDispatcher.cs`(grep `ProviderOut`,可能用 `provider.LegacyId` 之类)
- `src/OnToPilot.Tests/Providers/ProvidersApiTests.cs`(断言用 `LegacyId`)

**Step 1 (combined):** grep 受影响的所有点:
```bash
grep -rn "\.LegacyId" src/OnToPilot/Providers/ src/OnToPilot.Tests/Providers/ src/OnToPilot/Integration/
```

**Step 2:** 逐个改:
- ProviderDtos.cs: 删除 `long LegacyId`
- ProviderService.cs: 不引用 DTO.LegacyId,无需改
- InternalOperationDispatcher.cs: 改 `req.LegacyId` → `req.Id`(若有)
- ProvidersApiTests.cs: `Assert.Equal(1, dto.LegacyId)` → `Assert.Equal(Guid, dto.Id)`

**Step 3:** 编译:
```bash
dotnet build OnToPilot.sln
```
期望:无错

**Step 4:** 跑 provider 测试:
```bash
dotnet test OnToPilot.Tests/Providers --logger "console;verbosity=minimal"
```
期望:全绿

**Step 5:** Commit:
```bash
git add -A
git commit -m "refactor(provider): drop LegacyId from wire DTO, use Id (Guid)"
```

---

### Task 5: Knowledge DTO

**Files:**
- Modify: `src/OnToPilot/Knowledge/KnowledgeDtos.cs`
- Modify: `src/OnToPilot/Knowledge/KnowledgeService.cs`(FK 字段:KnowledgeSystemId long→Guid)
- Modify: `src/OnToPilot/Integration/InternalOperationDispatcher.cs`
- Modify: `src/OnToPilot.Tests/Knowledge/KnowledgeApiTests.cs`

**Rename 规则:** KnowledgeSystemOut 当前 `LegacyId: long` + `PublicId: Guid` — 按规则 3:**保留 PublicId**,**删除 LegacyId**,**新增 `Id: Guid`** 来自 entity.Id。

**Step 1:** grep 影响点:
```bash
grep -rn "\.LegacyId" src/OnToPilot/Knowledge/ src/OnToPilot.Tests/Knowledge/
```

**Step 2:** 改 KnowledgeDtos.cs:
- KnowledgeSystemOut: 添加 `Guid Id` 字段(在第一个位置),删除 `long LegacyId`
- 所有 Request DTO:`KnowledgeSystemId: long` → `Guid`
- 所有 Out DTO 含 KnowledgeSystemId 引用:`long` → `Guid`

**Step 3:** 改 KnowledgeService.cs:
- 方法签名 `long ksId` → `Guid ksId`
- body 内 `Where(x.LegacyId == ...)` → `Where(x.Id == ...)`

**Step 4:** 编译 + 跑测试:
```bash
dotnet build OnToPilot.sln
dotnet test OnToPilot.Tests/Knowledge --logger "console;verbosity=minimal"
```

**Step 5:** Commit:
```bash
git add -A
git commit -m "refactor(knowledge): drop LegacyId from wire DTO, use Id (Guid)"
```

---

### Task 6: Conflict DTO

**Files:**
- Modify: `src/OnToPilot/Conflicts/ConflictDtos.cs`
- Modify: `src/OnToPilot/Conflicts/ConflictService.cs`(签名 + body)
- Modify: `src/OnToPilot/Integration/InternalOperationDispatcher.cs`
- Modify: `src/OnToPilot.Tests/Conflicts/ConflictApiTests.cs`

**Rename 规则:** ConflictOut 当前 `LegacyId: long` — 按规则 2 重命名为 `Id: Guid`。

**Step 1:** grep 影响点 + 改 DTO(同上模式)
**Step 2:** 改 Service 签名 + body(`Where(c.KnowledgeSystemId == ks.LegacyId)` → `Where(c.KnowledgeSystemId == ks.Id)`)
**Step 3:** 编译 + 跑测试 + Commit

---

### Task 7: Document DTO

**Files:**
- Modify: `src/OnToPilot/Documents/DocumentDtos.cs`
- Modify: `src/OnToPilot/Documents/DocumentService.cs`
- Modify: `src/OnToPilot/Integration/InternalOperationDispatcher.cs`
- Modify: `src/OnToPilot.Tests/Documents/...`(若有)

**Rename:** DocumentOut 重命名为 `Id: Guid`,所有 FK `long` → `Guid`。

---

### Task 8: 其余 18 个 entity 的 DTO

按相同模式依次处理(每 entity 一个 task group):
- Chunk
- AuthSession
- User
- KSGrant
- KnowledgePromptOverride
- KnowledgeApiToken
- McpUserToken
- OntologyRelease
- ReleaseDeployment
- ReleaseStatementProvenance
- ExportJob
- EntityResolution
- TermProposal
- TboxReconciliation
- ValidationDecision
- ExtractionJob
- AxiomProvenance
- AboxProvenance
- AuditEvent

每 entity 模板:
1. `grep -rn "\.LegacyId" src/OnToPilot/<EntityDir>/`
2. 改 DTO + Service + Dispatcher + Tests
3. `dotnet build OnToPilot.sln`
4. `dotnet test OnToPilot.Tests/<EntityDir>`
5. Commit

**合并建议**:把改动小的 entity(auth / audit 之类的)合并成几个 commit,如 `refactor(auth): drop LegacyId from wire DTOs`。

---

### Task 9: Phase 1A 完成验证

**Step 1:** 全套构建:
```bash
dotnet build OnToPilot.sln
```
期望:无错

**Step 2:** 全套 unit test:
```bash
dotnet test OnToPilot.Tests --logger "console;verbosity=minimal"
```
期望:之前所有因为缺字段而失败的测试现在通过(可能仍有 2 个 pre-existing flake 不动)

**Step 3:** 验证 wire DTO 上无 legacy_id 字段:
```bash
grep -rn "LegacyId" src/OnToPilot/ | grep -i "Dtos.cs\|Out\|Request"
```
期望:无输出(grep 应该返回空,说明 wire DTO 已干净)

**Step 4:** Commit(如果有改动):
```bash
git add -A
git commit -m "chore(dto): verify all wire DTOs use Id (Guid), no legacy_id leak"
```

---

## Phase 1B — Service 签名 + body

### Task 10: KnowledgeService 全面 Guid 化

**Files:**
- Modify: `src/OnToPilot/Knowledge/KnowledgeService.cs`

**Step 1:** grep 所有 `long` 参数:
```bash
grep -n "long " src/OnToPilot/Knowledge/KnowledgeService.cs
```

**Step 2:** 替换 `long ksId`、`long docId` → `Guid`:
- 涉及方法:ListAsync / GetAsync / CreateAsync / UpdateAsync / DeleteAsync / AddMemberAsync / RemoveMemberAsync / ResolveKnowledgeSystemAsync 等
- body 内 `Where(x => x.LegacyId == id)` → `Where(x => x.Id == id)`

**Step 3:** 编译:
```bash
dotnet build OnToPilot.sln
```

**Step 4:** Commit:
```bash
git add src/OnToPilot/Knowledge/KnowledgeService.cs
git commit -m "refactor(knowledge): all service signatures use Guid"
```

---

### Task 11-19: 其他 service 同样处理

按相同模式处理每个 service:
- ProviderService
- ConflictService
- DocumentService
- VocabularyService
- OntologyService
- ABoxService
- ABoxProvenanceService
- VocabularyProposalService
- ValidationDecisionService
- ExtractionService
- AuthController(及 AuthService 如有)
- TerminologyAgent

每 service:
1. grep `long` 参数和 `LegacyId`
2. 改签名 + body
3. build
4. commit

---

## Phase 1C — Controller + Dispatcher 路由

### Task 20: Controller 路由 `:long` → `:guid`

**Files:** 所有 controllers
- `src/OnToPilot/Controllers/*.cs`
- `src/OnToPilot/Workspace/*.cs` (如果有)

**Step 1:** grep 所有路由模板:
```bash
grep -rn 'HttpGet\|HttpPost\|HttpPatch\|HttpDelete' src/OnToPilot/Controllers/ | grep "{.*}"
```

**Step 2:** 替换 `:` constraint:
```csharp
// Before:
[HttpGet("/api/knowledge/{ks_id}")]
[HttpGet("/api/providers/{provider_id}")]

// After:
[HttpGet("/api/knowledge/{id:guid}")]
[HttpGet("/api/providers/{id:guid}")]
```

注意:**所有 path 段含 long id 的都要加 `:guid`**,包括嵌套路径如 `/api/knowledge/{ks_id:long}/documents/{doc_id:long}`。

**Step 3:** 改 controller 方法签名 `long ks_id` → `Guid id`:
```csharp
// Before:
public async Task<...> GetAsync(long ks_id, ...)

// After:
public async Task<...> GetAsync(Guid id, ...)
```

**Step 4:** 编译 + 跑测试。

**Step 5:** Commit:
```bash
git commit -m "refactor(controllers): all routes use {id:guid} constraint"
```

---

### Task 21: InternalOperationDispatcher 全面更新

**Files:**
- Modify: `src/OnToPilot/Integration/InternalOperationDispatcher.cs`

**Step 1:** grep 所有 long 路径和 long id 字段:
```bash
grep -n "{ks_id\|{doc_id\|long " src/OnToPilot/Integration/InternalOperationDispatcher.cs
```

**Step 2:** 替换 path 模板 + 参数类型:
- `"/api/knowledge/{ks_id}"` → `"/api/knowledge/{id:guid}"`
- 所有 path 参数 `long` → `Guid`

**Step 3:** 编译 + 跑 dispatcher 相关测试。

**Step 4:** Commit:
```bash
git commit -m "refactor(dispatcher): all operation paths use {id:guid}"
```

---

## Phase 1D — 测试更新

### Task 22: Unit test fixtures 全面 Guid 化

**Files:**
- Modify: 所有 `src/OnToPilot.Tests/**/*.cs`

**Step 1:** grep 受影响范围:
```bash
grep -rln "LegacyId\s*[:=]\|long " src/OnToPilot.Tests/
```

**Step 2:** 逐文件改:
- `var x = new KnowledgeSystemEntity { LegacyId = 1, ... }` → `{ Id = Guid.NewGuid(), ... }`
- `Assert.Equal(1L, dto.LegacyId)` → `Assert.Equal(Guid, dto.Id)`
- helper `BuildProvider(LegacyId: n)` → `BuildProvider(Id: g)`

**Step 3:** 全套跑:
```bash
dotnet test OnToPilot.Tests --logger "console;verbosity=minimal"
```
期望:除 2 个 pre-existing flake 外全绿。

**Step 4:** Commit:
```bash
git commit -m "test(unit): all fixtures use Id (Guid) instead of LegacyId"
```

---

### Task 23: Integration test fixtures 全面 Guid 化

**Files:**
- Modify: `src/OnToPilot.IntegrationTests/**/*.cs`(包括新加的 `ProviderServicePgIntegrationTests.cs`)

**Step 1:** 跑现有测试看哪些挂:
```bash
dotnet test OnToPilot.IntegrationTests --logger "console;verbosity=minimal"
```

**Step 2:** 改每个 fixture(path template + Guid 解析)。

**Step 3:** 重点验证并发测试仍通过:
```bash
dotnet test OnToPilot.IntegrationTests --filter "FullyQualifiedName~ProviderServicePgIntegrationTests"
```
期望:`Pg_concurrent_provider_create_does_not_violate_legacy_id_unique` 通过(allocator 路径未动)。

**Step 4:** Commit:
```bash
git commit -m "test(integration): all fixtures use Id (Guid), allocator paths untouched"
```

---

### Task 24: Contract test 全面更新 + 重生成 baseline

**Files:**
- Modify: `src/OnToPilot.ApiContract.Tests/Baseline/*.cs`
- Modify: `src/OnToPilot.ApiContract.Tests/Baseline/OpenApiInventoryTests.cs`(如涉及)

**Step 1:** 跑看哪些挂:
```bash
dotnet test OnToPilot.ApiContract.Tests --logger "console;verbosity=minimal"
```

**Step 2:** 改 `OperationCase.Path`(所有 `{ks_id}` → `{id:guid}`):
```csharp
// Before:
new OperationCase {
    Path = "/api/knowledge/{ks_id}",
    ExpectedStatus = 200,
    ResponseSchema = ...,  // 包含 legacy_id
}

// After:
new OperationCase {
    Path = "/api/knowledge/{id:guid}",
    ExpectedStatus = 200,
    ResponseSchema = ...,  // 删 legacy_id,加 id (Guid format)
}
```

**Step 3:** 改 ResponseSchema(去掉 legacy_id,加 id string):
```bash
# 用任何 sed/awk 批量替换,或 IDE 重构
```

**Step 4:** 跑全套 + 修复直到全绿:
```bash
dotnet test OnToPilot.ApiContract.Tests --logger "console;verbosity=minimal"
```

**Step 5:** Commit:
```bash
git commit -m "test(contract): regenerate baseline for Guid wire shape"
```

---

## Phase 1E — Frontend

### Task 25: 更新 `frontend/src/lib/api.ts` 类型

**Files:**
- Modify: `frontend/src/lib/api.ts`

**Step 1:** grep 所有 `id: number` / `legacy_id: number`:
```bash
grep -n "id:\s*number\|legacy_id:\s*number" frontend/src/lib/api.ts
```

**Step 2:** 替换 `id: number` → `id: string`,删除所有 `legacy_id: number`:
```typescript
// Before:
export interface KnowledgeSystemOut {
  legacy_id: number;
  name: string;
}

// After:
export interface KnowledgeSystemOut {
  id: string;
  name: string;
}
```

**Step 3:** 编译(类型检查):
```bash
cd frontend && npx tsc --noEmit
```
期望:可能报 caller 错,逐个修。

**Step 4:** Commit:
```bash
git add frontend/src/lib/api.ts
git commit -m "refactor(frontend): api.ts types use id: string (Guid)"
```

---

### Task 26: 更新 URL builders

**Files:** 所有引用 `*.legacy_id` 的 `.tsx`/`.ts`

**Step 1:** grep:
```bash
grep -rn "\.legacy_id" frontend/src/
```

**Step 2:** 替换 `\`/api/knowledge/${x.legacy_id}\`` → `\`/api/knowledge/${x.id}\``(变量类型从 number 改 string,但 URL 形态相似)。

**Step 3:** 编译 + 跑前端 build:
```bash
cd frontend && npx tsc --noEmit && npm run build
```

**Step 4:** Commit:
```bash
git commit -m "refactor(frontend): URL builders use id (string) instead of legacy_id"
```

---

### Task 27: 更新 Router + 类型守卫

**Files:**
- Modify: `frontend/src/App.tsx`
- Modify: 所有 pages 中 `useParams<{ ks_id: string }>` 之类
- Modify: 所有 `typeof x === 'number'` 类型守卫

**Step 1:** grep:
```bash
grep -rn "useParams\|typeof.*legacy_id\|typeof.*=== 'number'" frontend/src/
```

**Step 2:** 替换。

**Step 3:** 编译 + build。

**Step 4:** Commit:
```bash
git commit -m "refactor(frontend): router + type guards use id (Guid string)"
```

---

## Final Verification

### Task 28: 全套回归

**Step 1:** 完整构建:
```bash
dotnet build OnToPilot.sln
cd frontend && npm run build && cd ..
```
期望:全绿

**Step 2:** 完整测试套件:
```bash
dotnet test OnToPilot.sln --logger "console;verbosity=minimal"
```
期望:
- `OnToPilot.Tests`: 353+/355(2 pre-existing flake)
- `OnToPilot.IntegrationTests`: 36/36
- `OnToPilot.ApiContract.Tests`: 全绿(Phase 0 干净 baseline + Phase 1D 重生成)

**Step 3:** 手工 spot check(对照 spec §12):
```bash
# 起 backend
dotnet run --project src/OnToPilot

# 另开 terminal:
curl -X GET http://localhost:5000/api/knowledge/abc-123-...
# 期望 400

curl -X GET http://localhost:5000/api/knowledge/123
# 期望 400(原 404 → 现 400,语义更准)

# POST 创建,然后 GET 验证
curl -X POST http://localhost:5000/api/knowledge -H "Content-Type: application/json" -d '{"name":"test","owner_id":"<some-guid>"}'
# 期望 200,response.id 是 Guid string
```

**Step 4:** 验证 wire response 无 `legacy_id`:
```bash
# 在响应 JSON 中 grep:
curl -s http://localhost:5000/api/knowledge | grep -i "legacy_id"
# 期望:无输出
```

**Step 5:** 验证 DB schema 不变:
```bash
psql -d ontopilot -c "\d+ provider"
# 期望:legacy_id 列 + ux_provider_legacy_id 索引仍在
```

---

### Task 29: 最终 commit

**Step 1:** 确认所有改动已 commit:
```bash
git status
```
期望:working tree clean

**Step 2:** 打 tag 或合并 commit:
```bash
git log --oneline -20
# 确认看到完整的迁移 commit 链
```

---

## Self-Review

### Spec coverage 检查

| Spec 节 | 对应 Task |
|---------|----------|
| §1 背景与目标 | (本文本身) |
| §2 设计概览 | Task 4-9(DTO 改)+ Task 20(路由)|
| §3 改动清单 | Task 4-27 全覆盖 |
| §4 数据模型 | Task 4-9(DTO rename)+ Task 10-19(service 签名)|
| §5 数据流 | (行为级,无单独 task,通过 DTO + service 改动实现)|
| §6 Error handling | Task 20(`:guid` route constraint 自动 400)|
| §7 LegacyIdAllocator 角色变更 | **不变动 task**(实现不动,验证见 Task 23 集成测试)|
| §8 Frontend 改动 | Task 25-27 |
| §9 测试策略 | Task 22-24 |
| §10 迁移步骤 | Phase 0 → Phase 1A-1E → Final |
| §11 风险与权衡 | (本文本身)|
| §12 验收标准 | Task 28 |

### Placeholder scan

无 TODO / TBD / "implement later"。

### Type consistency

所有 task 中使用的类型:
- `Guid` (wire / entity PK)
- `long` (仅在 entity.LegacyId 内部)
- `string` (URL path 段,JSON 字段)
- Wire DTO 字段名:统一用 `Id`(来自 entity.Id)
- Service 签名:`Guid id`(替代 `long id`)

无 inconsistent 命名。
