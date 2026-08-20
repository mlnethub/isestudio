# OnToPilot 主键统一到 Guid 设计规格

**状态**: 已批准（待用户最终签字）
**日期**: 2026-08-20
**范围**: Backend (`src\OnToPilot` + `src\OnToPilot.IntegrationTests` + `src\OnToPilot.Tests` + `src\OnToPilot.ApiContract.Tests`)、Frontend (`frontend/src`)。Python 后端不在本次范围。

---

## 1. 背景与目标

### 1.1 现状

所有 `LegacyAddressableEntity` 子类（22 个）当前采用 **双键** 设计：

- `Id: Guid` — EF Core 主键，类型为 `Guid`
- `LegacyId: long` — 唯一索引列，对应 Python 后端的 `legacy_id`（用于 SQLAlchemy 兼容）

Wire DTO、URL 路径、跨资源引用当前都使用 `LegacyId: long` 作为主键表达。这导致：

- 前端 `lib/api.ts` 中所有 ID 类型都是 `number`
- URL 形如 `/api/knowledge/{ks_id}`（其中 `ks_id` 是 long）
- 跨表 FK 在 wire 上以 long 形式出现
- 与 Python 后端的 parity 校验成为契约的一部分（即使 Python 即将废弃）

### 1.2 Python 后端状态

Python 后端即将整体废弃（独立决策），不再需要维持 wire 兼容性。`legacy_id` 列在 PostgreSQL 中保留，作为**数据迁移备份**，不再出现在 wire 上。

### 1.3 目标

将 wire 表面（包括 JSON 字段、URL 路径、跨资源引用）的主键统一为 `Guid`：

- 前端 `id: string`（Guid 字符串）
- URL 路径 `:guid` route constraint
- Service 层签名使用 `Guid` 类型
- `LegacyIdAllocator` **保留运行**（仍然为新行赋 long 唯一索引值），但其输出不再出现在 wire 上
- DB schema 不动 — `legacy_id` 列 + `ux_*_legacy_id` 唯一索引保留，防止 PG 在并发写入时撞车（防止 [[ontopilot-allocator-missed-sites]] 描述的 race window 重开）

### 1.4 不在本次范围

- DB schema 不变（不删 `legacy_id` 列）
- 不动 Python 后端
- `LegacyIdAllocator` 内部实现不动
- 不优化 FK lookup（Guid PK 查找天然比 LegacyId 索引快，但本次不专门 benchmark）

---

## 2. 设计概览

### 2.1 Layer 视图

```
┌─────────────────────────────────────────────────────┐
│ Frontend (TS, frontend/src)                          │
│   types: id: string                                 │
│   url:  /api/knowledge/{id:guid}                    │
└─────────────────┬───────────────────────────────────┘
                  │ JSON: { id: "abc-...", ... }
                  ▼
┌─────────────────────────────────────────────────────┐
│ ASP.NET Core Web API                                │
│   Controllers: route attrs use :guid constraint     │
│   DTOs (wire): Guid fields only, no legacy_id       │
└─────────────────┬───────────────────────────────────┘
                  │ Guid-typed DTOs
                  ▼
┌─────────────────────────────────────────────────────┐
│ InternalOperationDispatcher + Services              │
│   Service signatures: Guid id (not long)            │
│   Internal lookups: WHERE Id == @guid               │
│   Cross-FK: Guid references                          │
└─────────────────┬───────────────────────────────────┘
                  │ Entity models (Id: Guid, LegacyId: long)
                  ▼
┌─────────────────────────────────────────────────────┐
│ EF Core / Npgsql                                     │
│   id (uuid) PK                                      │
│   legacy_id (bigint) UNIQUE — STAYS, internal only  │
│   LegacyIdAllocator assigns legacy_id for new rows  │
└─────────────────┬───────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────┐
│ PostgreSQL                                          │
│   Same schema as today — no DDL migration needed    │
└─────────────────────────────────────────────────────┘
```

### 2.2 关键约束

- Wire 上**完全看不到** `legacy_id` 字段
- 所有跨资源 URL 路径使用 `:guid` route constraint（ASP.NET Core 自动 400 on malformed Guid）
- Service 层方法签名 `Guid id`，不再有 `long id` 参数
- `LegacyIdAllocator` + `pg_advisory_xact_lock` 保留 — 内部仍要给新行赋 long 唯一索引值
- DB schema 不动 — 不需要 migration

---

## 3. 改动清单

### 3.1 Backend OnToPilot 项目

| 类别 | 数量（约） | 改动要点 |
|------|------------|----------|
| Wire DTOs (`*Dtos.cs` × 22) | ~110 records | `LegacyId: long` → `Id: Guid`；FK 字段 `long` → `Guid` |
| Request DTOs | ~40 records | 同上 |
| Controller routes | ~80 endpoints | route attribute 加 `:guid` constraint；path 模板 `{ks_id:long}` → `{id:guid}` |
| Service method signatures | ~150 methods | `long id`、`long ksId`、`long legacyId` 参数 → `Guid` |
| Service body | ~600 lines | `WHERE LegacyId == x` → `WHERE Id == x`；`entity.LegacyId` 用于查找的地方改为 `entity.Id` |
| InternalOperationDispatcher | ~50 cases | 操作键 + path 模板用新格式 |
| `EntityConfigurations.cs` | 0 行 | 不动 |
| `Migrations/*` | 0 行 | 不动 schema |
| `LegacyIdAllocator.cs` | 0 行 | 保持原样 |

### 3.2 Frontend 项目

| 类别 | 数量（约） | 改动要点 |
|------|------------|----------|
| API types (`lib/api.ts`) | ~80 interfaces | `id: number` → `id: string`；FK `number` → `string` |
| URL builders | ~50 sites | 插值变量类型从 `number` 变 `string`（实际内容是 Guid） |
| Router (`App.tsx` + pages) | ~20 routes | 路由参数类型从 number 改为 string |
| Type guards | ~30 sites | `typeof x === 'number'` → `typeof x === 'string'` |

### 3.3 测试项目

| 测试类型 | 数量（约） | 改动要点 |
|---------|------------|----------|
| Unit (`OnToPilot.Tests`) | ~250 | `LegacyId: 1L` → `Id: Guid.NewGuid()`；long 字段名 → Guid；helper `BuildProvider(LegacyId: n)` 改 `BuildProvider(Id: g)` |
| Integration (`OnToPilot.IntegrationTests`) | ~10 | path 模板换 `:guid`；并发测试代码不变（allocator 仍跑） |
| Contract (`OnToPilot.ApiContract.Tests`) | ~200 cases | `OperationCase.Path` 重新生成；期望 JSON shape 删 `legacy_id`；`id` 改为 Guid 格式 |
| Python parity 校验 | 全部退役 | Python 即将废弃，不再验证 wire 一致性 |

---

## 4. 数据模型

### 4.1 Entity 改动

22 个 `LegacyAddressableEntity` 子类**实体层不动**：

```csharp
public abstract class LegacyAddressableEntity
{
    public Guid Id { get; set; }              // PK（不变）
    public long LegacyId { get; set; }        // 内部 long（不变，仍由 allocator 赋值）
}
```

DB schema 不动：

```sql
-- 所有 *legacy_id 表都有：
CREATE TABLE knowledgesystem (
    id          uuid PRIMARY KEY,
    legacy_id   bigint NOT NULL,
    -- 其他列
);
CREATE UNIQUE INDEX ux_knowledgesystem_legacy_id ON knowledgesystem(legacy_id);
```

### 4.2 Wire DTO 改动模式

**Rename 规则**（按各 Out 类型的当前实际字段应用）:

1. 如果 Out 类型当前**已有 `Id: Guid`** 字段（如 `ProviderOut`）— 仅删除 `LegacyId: long`，保留 `Id`。
2. 如果 Out 类型当前**只有 `LegacyId: long`**（如多数 Out 类型）— 将 `LegacyId` 字段重命名为 `Id`，类型从 `long` 改为 `Guid`。
3. 如果 Out 类型**同时有 `LegacyId: long` 和 `PublicId: Guid`**（如 `KnowledgeSystemOut`，`PublicId` 是外部可读 hash-id）— 保留 `PublicId`，删除 `LegacyId`，新增 `Id: Guid` 作为内部 PK 的 wire 表达。`PublicId` 与 `Id` 是两个独立字段，语义不重叠。
4. 如果 Out 类型**仅暴露 `LegacyId` 的别名字段**（例如 `ks_id`、`doc_id`）— 重命名为 `id` / 对应语义字段名（如 `knowledge_system_id`），类型改为 `Guid`。

**通用模板**（以 `KnowledgeSystemOut` 为例，仅作示意，实际字段需按各 entity 当前情况决定）:

```csharp
// Before
public sealed record KnowledgeSystemOut(
    long LegacyId,            // ← 删除 / 重命名（按上规则）
    Guid PublicId,            // ← 保留（如已存在）
    string Name,
    // ...);

// After
public sealed record KnowledgeSystemOut(
    Guid Id,                  // ← 新增（来自 entity.Id）
    Guid PublicId,            // ← 保留（外部可读 hash-id）
    string Name,
    // ...);
```

实施时按 entity 逐一 audit `*Dtos.cs`，按上述 4 条规则套用。

**Request 类型**:

```csharp
// Before
public sealed record AddMemberRequest(
    long KnowledgeSystemId,   // ← 改 Guid
    string Username,
    string Role);

// After
public sealed record AddMemberRequest(
    Guid KnowledgeSystemId,
    string Username,
    string Role);
```

### 4.3 URL 路径模式

```csharp
// Before
[HttpGet("/api/knowledge/{ks_id}")]
public async Task<...> GetAsync(long ks_id, ...);

// After
[HttpGet("/api/knowledge/{id:guid}")]
public async Task<...> GetAsync(Guid id, ...);
```

`InternalOperationDispatcher` 的 `OperationCase.Path` 同步更新：

```csharp
// Before
new OperationCase {
    Method = GET,
    Path = "/api/knowledge/{ks_id}",
    // ...
}

// After
new OperationCase {
    Method = GET,
    Path = "/api/knowledge/{id:guid}",
    // ...
}
```

---

## 5. 数据流

### 5.1 创建路径（POST /api/knowledge）

```
1. Frontend POST /api/knowledge
   body: { name: "ks1", owner_id: "guid-string" }

2. ASP.NET Core route constraint :guid
   ✓ "guid-string" 是合法 Guid → 进 controller
   ✗ 其他 → 400 Bad Request（自动，System.Text.Json）

3. Controller: 字段绑定到 KnowledgeSystemCreateRequest
   - OwnerId: Guid
   - 其他字段

4. Dispatcher → KnowledgeService.CreateAsync(req, actor, ct)
   - 创建 entity:
       Id = Guid.NewGuid()              ← 内部 Id（PK）
       Name, OwnerId, ...
       LegacyId 暂留 0
   - await _allocator.AllocateAndPersistAsync(ks, ct)
       ← 在 advisory lock 内：
           SELECT MAX(LegacyId) → 42
           ks.LegacyId = 43
           Add + SaveChanges + COMMIT

5. PG INSERT INTO knowledgesystem (id, legacy_id, ...)
   id = <new uuid>
   legacy_id = 43

6. Response 200:
   {
     "id": "guid-string",        ← Guid
     "name": "ks1",
     "owner_id": "guid-string",  ← FK 用 Guid
     "created_at": "..."
     // 注意：response 里没有 legacy_id 字段
   }
```

### 5.2 查询路径（GET /api/knowledge/{id:guid}）

```
1. Frontend GET /api/knowledge/abc-123-...

2. Route constraint :guid → 匹配

3. Controller: id = Guid.Parse("abc-123-...")

4. Dispatcher → KnowledgeService.GetAsync(ksId: Guid, actor, ct)
   - WHERE k.Id == @id            ← 直接 PK 查找（不经过 LegacyId）
   - EF Core 走 PK 索引

5. Response 200: { id: "guid-string", ... }
```

### 5.3 跨表引用（以 AddMember 为例）

```
1. POST /api/knowledge/{id:guid}/members
   body: { username: "alice", role: "editor" }

2. Controller: id (Guid) + AddMemberRequest

3. KnowledgeService.AddMemberAsync(ksId: Guid, req, actor, ct)
   - ks = _db.KnowledgeSystems.FirstOrDefault(k => k.Id == ksId)   ← PK lookup
   - 检查 owner
   - target user lookup
   - _db.KSGrants.Add(new KSGrantEntity {
         KnowledgeSystemId = ks.Id,    ← Guid（FK 在 entity 层）
         UserId = target.Id,
         ...
       })
   - await _allocator.AllocateAndPersistAsync(grant, ct)

4. Response 200: List<MemberOut>
   - MemberOut.Id = Guid（grant 的 PK）
   - MemberOut.KnowledgeSystemId = Guid
   - MemberOut.UserId = Guid
```

---

## 6. Error handling

| 场景 | 当前（long） | 改后（Guid） |
|------|------------|------------|
| Path 上是 `abc`（非数字） | 404（路由不匹配） | **400**（constraint 失败，语义更准） |
| Path 上是 `123` 合法但不存在 | 404 | 404（不变） |
| Body 中 `id: "not-a-guid"` | 400（反序列化失败） | 400（不变） |
| Body 中 `ks_id: "not-a-guid"` | 400 | 400（不变） |

保留的错误类型：

- `400 Bad Request` — Guid 反序列化失败（System.Text.Json 默认行为）
- `404 Not Found` — resource 不存在
- `409 Conflict` — FK 引用被占用 / 重复
- `500 Internal Server Error` — 未处理异常

Middleware 不变：

- `FastApiErrorMiddleware` 仍按 `{"detail": "..."}` envelope 输出
- `InvalidOperationException` → 4xx 行为不变
- `ResourceInUseException` → 409 行为不变

---

## 7. LegacyIdAllocator 角色变更

### 7.1 内部行为保持

`LegacyIdAllocator` 实现完全不动：

- `AllocateAndPersistAsync<T>(entity, ct)`：在 `pg_advisory_xact_lock` 内读 `MAX(LegacyId)`、赋 `entity.LegacyId = max + 1`、`Add` + `SaveChanges` + `COMMIT`
- `AllocateManyAndPersistAsync<T>(entities, ct)`：同上，分配连续区间
- 调用方在 `wire DTO` 中**不再引用 `LegacyId`**（Guid 主键替代）。allocator 的输出 `entity.LegacyId` 仍是必须的（用于 `INSERT legacy_id=...`），但 wire 路径上完全不暴露

### 7.2 调用方模式

```csharp
// 现在（Guid 化后）
var ks = new KnowledgeSystemEntity {
    Id = Guid.NewGuid(),         // wire 用
    Name = ...,
    // LegacyId 暂留 0
};
await _allocator.AllocateAndPersistAsync(ks, ct);
// ks.Id   = Guid（wire 用，response 里）
// ks.LegacyId = 43（DB 唯一索引用，不出现在 wire）
```

### 7.3 不动的地方

- `LegacyIdAllocator` 类文件不动
- `EntityConfigurations.cs` 中 `HasIndex(x => x.LegacyId).IsUnique()` 保留
- 所有 `ux_*_legacy_id` 索引保留（防 race window 重开 — 参考 [[ontopilot-allocator-missed-sites]]）

---

## 8. Frontend 改动

### 8.1 类型声明

```typescript
// Before
export interface KnowledgeSystemOut {
  legacy_id: number;
  name: string;
  // ...
}

export interface ProviderOut {
  legacy_id: number;
  id: string;       // (已有 Guid 字段)
  name: string;
}

// After
export interface KnowledgeSystemOut {
  id: string;       // ← 新增（Guid）
  name: string;
  // ...
}

export interface ProviderOut {
  id: string;       // (Guid，已是)
  name: string;
}
```

### 8.2 URL builder

```typescript
// Before
const url = `/api/knowledge/${ks.legacy_id}`;

// After
const url = `/api/knowledge/${ks.id}`;
// 注意：变量类型从 number 改为 string，但 URL 字符串形态不变
```

### 8.3 Router

```typescript
// Before
<Route path="/knowledge/:ks_id" element={<KnowledgePage />} />

// After
<Route path="/knowledge/:id" element={<KnowledgePage />} />
```

### 8.4 类型守卫

```typescript
// Before
if (typeof x.legacy_id === 'number') { ... }

// After
if (typeof x.id === 'string') { ... }
```

---

## 9. 测试策略

### 9.1 Unit tests

所有 fixture 改为：

```csharp
// Before
var provider = new ProviderEntity {
    LegacyId = 1L,
    Name = "p1",
    // ...
};

// After
var provider = new ProviderEntity {
    Id = Guid.NewGuid(),       // 不显式赋也行（默认值 Guid.Empty 也可以接受为 PK）
    Name = "p1",
    // LegacyId 暂留 0
};
// 然后走 _allocator.AllocateAndPersistAsync(...) 赋 LegacyId
```

### 9.2 Integration tests

刚加的并发测试**保留原样**：

```csharp
// ProviderServicePgIntegrationTests.Pg_concurrent_provider_create_...
// 路径不变，仍走 ProviderService.CreateAsync → AllocateAndPersistAsync
```

只是 path 模板（如 `{id:guid}`）和 Guid 解析可能要小调整。

### 9.3 Contract tests

`OperationCase.Path` 全部更新：

```csharp
// Before
new OperationCase {
    Path = "/api/knowledge/{ks_id}",
    ExpectedStatus = 200,
    ResponseSchema = ...,  // 包含 legacy_id
}

// After
new OperationCase {
    Path = "/api/knowledge/{id:guid}",
    ExpectedStatus = 200,
    ResponseSchema = ...,  // 删 legacy_id，加 id (Guid format)
}
```

**Phase 0**: 独立 commit 修 69 个预先存在 500（validation 4xx + Oxigraph skip in Testing），让 baseline 干净。

**Phase 1**: 本次 commit，更新 ~200 个 `OperationCase.Path` 模板，重新生成 baseline。

### 9.4 Python parity 测试

**全部退役**（Python 后端即将废弃）。

### 9.5 Frontend 测试

如果存在 TS 类型断言 / URL builder 单测，更新对应类型。

---

## 10. 迁移步骤（Phasing）

### Phase 0 — 独立 commit（**Phase 1 之前**）

**目标**：让 `OnToPilot.ApiContract.Tests` baseline 干净。

| 任务 | 文件 |
|------|------|
| `InvalidOperationException` validation → 4xx envelope | `ProviderService.ValidateCommon`, `KnowledgeService.CreateAsync`, etc. |
| `Program.cs` 在 `Testing` 环境跳过 Oxigraph 初始化 | `Program.cs` line 408 附近 |
| `StoreWrapper` 接受 `null` path，提供 no-op 实现 | `Ontology/StoreWrapper.cs` |
| 重跑 `OnToPilot.ApiContract.Tests`，期望全部通过（baseline 干净） | — |

### Phase 1 — 主 commit（**Guid 迁移 PR**）

按以下顺序执行（每步可独立编译 + 跑测试）：

| 步骤 | 改动 | 测试 |
|------|------|------|
| 1. **Wire DTO 改动**（先改 response，再改 request） | 所有 `*Dtos.cs` 中的 `LegacyId` 字段移除/重命名为 `Id: Guid`；所有 FK 字段 `long` → `Guid` | 编译错 → 同步改 caller |
| 2. **Service 签名 + body** | 所有 service 方法 `long id` → `Guid id`；body 内 `WHERE LegacyId == x` → `WHERE Id == x` | 编译错 → 同步改 dispatcher / controller |
| 3. **Controller + Dispatcher route** | route attribute `:guid`；path 模板更新；`OperationCase.Path` 更新 | 编译错 → 同步改测试 |
| 4. **Test fixtures 更新** | Unit + Integration + Contract 测试 | 重跑 |
| 5. **Frontend 类型 + URL + Router** | `frontend/src/lib/api.ts` + 所有调用点 | tsc + build |
| 6. **最终回归** | 跑完整测试套件 + 跑前端 build | 期望全绿 |

### Phase 2 — 后续清理（**本次 PR 之后**）

不阻塞本次 PR，留作后续 ticket：

- 评估删除 `legacy_id` 列的可行性（一旦 Python 数据完全迁移走）
- 评估删除 `LegacyIdAllocator`（一旦列删除）

---

## 11. 风险与权衡

### 11.1 风险

| 风险 | 缓解 |
|------|------|
| **Diff 大**（~600-800 行） | 同质改动，可一遍过；commit message 注明影响面；review 分块做 |
| **Frontend 与 Backend 必须同步** | 同一 PR；CI 跑 `tsc` + `dotnet test` 双验证 |
| **Contract test baseline 重生成** | Phase 0 先把 baseline 干净，避免新旧 wire 冲突 |
| **外部客户端（如果有）破坏** | 当前只有内部 frontend，没有外部消费者；如有需要，OpenAPI 注明 breaking change |
| **OpenAPI spec 破坏** | 重新生成 baseline；contract inventory 测试重跑 |

### 11.2 权衡

| 选项 | 选 | 理由 |
|------|----|------|
| 机械重命名 vs 边界 mapping | 机械 | 长期干净；big-bang + Python 废弃，无兼容需求 |
| 立即删 `legacy_id` vs 保留 | 保留 | 不动 schema；防 race；后续清理 |
| Frontend 同步 vs 独立 PR | 同步 | 避免后端先上线导致前端 broken state |

---

## 12. 验收标准

- [ ] `OnToPilot.Tests` 全部通过（除 2 个预先存在 flake）
- [ ] `OnToPilot.IntegrationTests` 全部通过（含 `ProviderServicePgIntegrationTests` 并发测试）
- [ ] `OnToPilot.ApiContract.Tests` 全部通过（Phase 0 后 baseline 干净，Phase 1 后新 baseline 全绿）
- [ ] `OnToPilot.sln` 编译无错
- [ ] `frontend/` 通过 `tsc` + `vite build`
- [ ] 手工 spot check：
  - [ ] `GET /api/knowledge/<valid-guid>` 返回 200，response 含 `id` (string)
  - [ ] `GET /api/knowledge/not-a-guid` 返回 400
  - [ ] `GET /api/knowledge/123` 返回 400（malformed Guid，constraint 拦）
  - [ ] `POST /api/knowledge` 创建后 `GET /api/knowledge/<id>` 能查到
  - [ ] Response body 中**无** `legacy_id` 字段
- [ ] DB schema 不变（`psql \d+ provider` 显示 `legacy_id` 列还在）

---

## 13. 后续清理（Phase 2）

不阻塞本次 PR：

- 评估删除 `legacy_id` 列 + `LegacyIdAllocator`（一旦 Python 数据完全迁移走）
- 评估 OpenAPI spec 自动生成 vs 手维护
- 评估把 `LegacyAddressableEntity` 简化为 `AddressableEntity`（去 "Legacy" 前缀）

---

## 14. 参考

- [[ontopilot-allocator-missed-sites]] — `ux_*_legacy_id` race window 历史；本次保留 `LegacyIdAllocator` 防止 race 重开
- [[ontopilot-allocator-atomic]] — d5be7cb 的 atomic alloc+save 设计
- [[ontopilot-b7c-hardening]] — advisory lock + B7c hardening 历史
- `docs/superpowers/specs/2026-08-13-ontopilot-dotnet-migration-design.md` — .NET 迁移规格
