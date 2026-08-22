# P0 Blocker: Captive Dependency + Frontend Accessibility Hardening

**状态**: 已完成（修复 + e2e 验证）
**日期**: 2026-08-23
**分支**: `dotnet`
**范围**: `src/OnToPilot/Program.cs` + `frontend/src/` (a11y) + `frontend/e2e/dotnet/`

---

## 1. 背景

Sprint N+1 的 e2e 回归在 docker-compose + Playwright 上做端到端冒烟,发现两个 P0 阻塞:

| ID | 阻塞 | 根因 |
|---|---|---|
| P0-1 | `POST /api/auth/login` 偶发 `Cannot consume scoped service 'DbContextOptions<OnToPilotDbContext>' from singleton 'IDbContextFactory<OnToPilotDbContext>'` | EF Core captive-dependency:同时注册了 `AddDbContext<>`(Scoped DbContextOptions)与 `AddDbContextFactory<>`(Singleton),factory 内部消费 Scoped options 触发 |
| P0-2 | Playwright 在 `/login` 找不到 `getByRole('heading')` 而 `toBeVisible()` 失败 | shadcn CardTitle 当前渲染为 `<div>`,不暴露 heading role — 同时违反 WCAG H25(认证页面应有一级 heading) |

修复后附带发现 3 个 P1 缺口(见 §5)。

---

## 2. 决策

### 2.1 Captive Dependency — single-point fix in Program.cs

**方案**: 删除 `AddDbContext<OnToPilotDbContext>` 注册,只保留 `AddDbContextFactory<>`,再显式注册一个 `Scoped<OnToPilotDbContext>` 代理到 factory。

**为什么不是其它**:
- ❌ 把 `AddDbContextFactory` 注册为 Scoped — 会破坏长生命周期的服务(如 hosted background workers)
- ❌ 全部迁到 `IDbContextFactory<>.CreateDbContextAsync()` 调用点 — 改动面太大,且与现有 `using var db = ...` 风格不符
- ✅ 单点修 Program.cs,其它代码零改动;保留 factory 的 Singleton 注册语义

### 2.2 LoginPage + KnowledgePage Accessibility

| 页面 | 改动 | 收益 |
|---|---|---|
| LoginPage | `<CardTitle>` → `<h1>`(用 `CardTitle` 同样的样式) | a11y:登录页有 page heading + Playwright `getByRole('heading')` 可定位 |
| KnowledgePage | 整张 Card 包裹在 `NavLink` 中,Pencil/Trash 按钮 `relative z-10` + `e.preventDefault()/stopPropagation()` | a11y:Card-as-link pattern(WCAG H30),整张可点 + 真实 link role;Playwright `getByRole('link', { name: /open/i })` 可定位 |

i18n 新增 `knowledge.open: "Open {name}"` / `"打开{name}"`,作为 NavLink 的 `aria-label`(辅助技术朗读,屏幕阅读器友好)。

### 2.3 e2e helper 重写

`auth.ts` 之前用"Documents link 出现在 side nav"判定 session 建立。SideNav 是 KS-scoped,登录后默认跳 `/` 不渲染。改为:

```typescript
// 登录 form 消失即视为 session 建立 (useAuth() 已切换为 authenticated user)
await expect(page.getByRole("heading", { name: /sign in|log in/i })).toBeHidden({ timeout: 15_000 })
```

`session.spec.ts` post-login 断言改为 Log out 按钮可见(全局 header,KS-scoped 不影响)。

---

## 3. 实施

### 3.1 Captive Dependency

`src/OnToPilot/Program.cs` EF Core 注册块(约 line 280-330):

```csharp
// 之前 (同时注册两个,触发 captive dependency)
builder.Services.AddDbContext<OnToPilotDbContext>(/* sqlite or npgsql */);
builder.Services.AddDbContextFactory<OnToPilotDbContext>(/* 同上 */);

// 之后
builder.Services.AddDbContextFactory<OnToPilotDbContext>((sp, options) => {
    var config = sp.GetRequiredService<IConfiguration>();
    var provider = config["OnToPilot:Persistence:Provider"] ?? "npgsql";
    if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase)) {
        var sqlite = config["OnToPilot:Persistence:SqliteConnection"] ?? "Data Source=:memory:";
        options.UseSqlite(sqlite);
    } else {
        var npgsql = config["OnToPilot:Persistence:ConnectionString"]
            ?? "Host=localhost;Port=5432;Database=ontopilot;Username=postgres;Password=postgres";
        options.UseNpgsql(npgsql);
    }
});
builder.Services.AddScoped<OnToPilotDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<OnToPilotDbContext>>().CreateDbContext());
```

### 3.2 Frontend A11y

| 文件 | 改动 |
|---|---|
| `frontend/src/pages/LoginPage.tsx` | 删除 `CardTitle` import;`<CardTitle className="text-lg">{t("login.title")}</CardTitle>` → `<h1 className="text-lg font-heading font-medium leading-snug">{t("login.title")}</h1>` |
| `frontend/src/pages/KnowledgePage.tsx` | Card 改为 `NavLink` 包裹;Pencil/Trash 按钮 `e.preventDefault()/stopPropagation()`;新增 `knowledge.open` i18n key |
| `frontend/src/lib/i18n.tsx` | 新增 `"knowledge.open": "Open {name}"` + `"knowledge.open": "打开{name}"` |

### 3.3 E2E Helper

| 文件 | 改动 |
|---|---|
| `frontend/e2e/dotnet/helpers/auth.ts` | login 后断言从"Documents link" → login heading hidden |
| `frontend/e2e/dotnet/session.spec.ts` | post-login 断言:Log out button(/log\s*out/i) |
| `frontend/e2e/dotnet/vocabulary.spec.ts` | "tab" → "link";SideNav 用 `<a>` 不是 `<button role="tab">` |
| `frontend/e2e/dotnet/upload-extract-publish.spec.ts` | 进 KS → 进 Documents → 上传 → Parse → 等 parsed → Extract → 等 completed |

---

## 4. 验证

### 4.1 Captive Dependency

```bash
curl -X POST -H "Content-Type: application/json" -d '{"username":"root","password":"..."}' \
  http://localhost:8080/api/auth/login -w "\nstatus=%{http_code}\n"
# 修复前: 500 (captive dependency exception)
# 修复后: 200 (返回 user JSON)
```

### 4.2 Frontend A11y

```bash
pnpm exec tsc -p tsconfig.app.json --noEmit
# 0 errors

pnpm exec vite build
# ✓ built
```

### 4.3 E2E

```bash
E2E_ADMIN_USERNAME=root E2E_ADMIN_PASSWORD='...' \
  DOTNET_BASE_URL=http://localhost:8080 \
  pnpm exec playwright test e2e/dotnet/session.spec.ts --reporter=list

# 修复前: 3 failed (heading missing → Documents missing → loop)
# 修复后: 1 passed (4.0s)
```

| Spec | 修复前 | 修复后 |
|---|---|---|
| `session.spec.ts` | ❌ Documents link not found | ✅ pass (2.3s) |
| `vocabulary.spec.ts` | ❌ getByRole('heading') no match | ⚠️ scheme=0 backend gap (P1-1) |
| `upload-extract-publish.spec.ts` | ❌ Upload input hidden + no heading | ⚠️ LLM provider missing (P1-2) |

---

## 5. P1 缺口(新发现,e2e 验证时暴露)

### 5.1 Vocabulary scheme=0 backend gap

**现象**: `GET /api/knowledge/{ksId}/vocabulary/schemes` 返回 `{"items":[],"total":0,"scheme_count":0,"concept_count":44}` — concepts 存在但 schemes 为空。

**业务影响**: VocabularyPanel 的 `selectedSchemeIri` 永远为空,导致 "New term" button 永远 disabled。

**根因(假设)**: VocabularyScheme 的 seed/初始化逻辑漏写 — concepts 由 extraction 自动创建,但 schemes 没有 default initialization。

**修复预估**: 0.5 人天(VocabularyService 启动时检测 schemes=0 自动创建一个 default scheme)。

### 5.2 MinIO bucket 不自动创建

**现象**: `POST /api/knowledge/{ksId}/documents/upload` → `Amazon.S3.AmazonS3Exception: The specified bucket does not exist`

**临时绕过**: `docker exec ontopilot-minio-1 mc mb local/ontopilot-blobs`

**真修复**: `MinioBlobStore` 构造时(或 Program.cs DI 注册时)检测 bucket 不存在则自动 `PutBucketAsync`。

**修复预估**: 0.25 人天。

### 5.3 Extract Job 等待路径 + LLM provider

**现象**: extract 步骤依赖 LLM provider 配置;e2e 当前没有 provider 时,extract job 永远不会完成(`completed` indicator 一直不出现)。

**修复方向**: e2e 在 extract 阶段加 `test.skip` 当 `settings.models` 为空,或注入 mock provider。

**修复预估**: 0.5 人天(e2e 适配)。

---

## 6. 不在本次范围

- 不引入 branded type(保持 `string` 简单性 — 见 [[2026-08-23-frontend-id-type-decision]])
- 不动 EF Core schema(只调整 DI 注册)
- 不实现 LLM provider mock(留待 §5.3)

---

## 7. 参考

- [[2026-08-23-frontend-id-type-decision]] — frontend id 决策 ADR
- [[ontopilot-dotnet-gap-2026-08-22]] — 缺口核查
- [[ontopilot-rdf-import-complete]] — RDF import workflow
- `docs/superpowers/specs/2026-08-13-ontopilot-dotnet-migration-design.md` — 总设计
- `src/OnToPilot/Program.cs:280-330` — EF Core DI 注册
- `frontend/src/pages/LoginPage.tsx:40` — `<h1>` 替换 `<CardTitle>`
- `frontend/src/pages/KnowledgePage.tsx:283-336` — Card-as-link 改造
