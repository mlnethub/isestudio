# P0 Blocker: Prompt Localization + Python Parity Wire-up

**状态**: 已完成（修复 + 单元测试 + e2e 回归）
**日期**: 2026-08-23
**分支**: `dotnet`
**范围**: `src/OnToPilot/Prompts/PromptLocales.cs` (新) + 3× extraction services + `ExtractionOrchestrator` + 5× 测试文件 + 1× 新测试文件

---

## 1. 背景

P0 captive-dep 修复（commit cee2ae5）跑通 session e2e 后，e2e 又把第二个 P0 暴露出来：3 个 extraction service 的 system prompt 是简化 placeholder，且没有任何 zh-CN 翻译切换路径。同时还有一个 stale PromptKey（`terminology.propose`）和 Python backend 的 `terminology.steward` 不一致 — 会让 prompt snapshot 审计在 .NET ↔ Python 跨栈对比时找不到对应行。

| ID | 阻塞 | 根因 |
|---|---|---|
| P0-3 | TBox/ABox/Terminology 3 个 agent 的 system prompt 仍是 13-17 行简化 placeholder，没有 Python 等价物 | 早期 .NET slice 出于进度直接 inline 了占位符；zh-CN 翻译缺失是连带问题 |
| P0-4 | `TerminologyAgent.PromptKey = "terminology.propose"` 与 Python `terminology.steward` 不一致 | PromptKey 是早期 .NET-only 命名；Python 后端是 `terminology.steward` |
| P0-5 | `OnToPilotOptions.SystemLanguage` 字段定义但全栈无人读取 | 配置已存在但没有任何消费方 |
| P0-6 | `TBoxExtractionService.PromptKey = "tbox.extract"` 与 Python `tbox.extract.rag` 不一致 | 同 P0-4 |

修复完成后附带保留 3 个新发现的 P1 缺口（见 §5），它们在前一个 P0 ADR（`2026-08-23-p0-captive-dep-and-a11y.md`）已经登记过，本 ADR 不重复。

---

## 2. 决策

### 2.1 单一 `PromptLocales` 静态目录（不是 DI service）

**方案**: 创建一个静态 `OnToPilot.Prompts.PromptLocales` 类，承载 `Dictionary<string, Dictionary<SystemLanguage, string>>` 字典。

**为什么是静态**:
- ✅ 编译期保证键名稳定（不是 stringly-typed）
- ✅ 零运行时初始化（LLM 调用热路径不需要等 DI 容器）
- ✅ 测试不需要 mock — 直接读
- ❌ DI service 会引入不必要的生命周期问题（Singleton/Scoped 与 `OnToPilotOptions` 的可选 monitor 都需要重排）
- ❌ IOptions<T> 已经把 `SystemLanguage` 接进来；PromptLocales 只是消费 `string`，不需要再注入

### 2.2 19 个键全在目录里，只 wire 3 个

**方案**: `PromptLocales._byKey` 注册全部 19 个 Python `prompt_config` 键；其中 3 个（TBox/ABox/Terminology）有真实翻译，16 个标记 `NotYetWiredStub`。

**为什么是全部 19**:
- ✅ 一个未来 P1 切片启用新 agent 时不需要再改 PromptLocales — 直接 `_byKey[key]` 拿到现成的 zh-CN/en
- ✅ `ResolveWithFallback` 的 fallback 语义有真实测试数据（16 个 stub 验证 zh→en fallback）
- ❌ 只 wire 3 个会更"最小"，但代价是下个切片要重新改 PromptLocales + 重跑 e2e

### 2.3 `ResolveSystemPrompt()` 实例方法 + `OnToPilotOptions` 注入

**方案**: 每个 extraction service 注入 `IOptions<OnToPilotOptions>`，加 `public string ResolveSystemPrompt()` 实例方法；删除 `public const string SystemPrompt`。

**为什么不是 readonly static**:
- ✅ `SystemLanguage` 是运行时配置，readonly static 只能在构造期固化为一个语言 — 不能按请求切换
- ✅ 实例方法让 `ExtractionOrchestrator` 也能在 snapshot 时调用，得到当时实际发送的 prompt 字节
- ✅ TBox/ABox 是 Singleton，TerminologyAgent 是 Scoped — 但都用 `IOptions<>`（Singleton），生命周期一致
- ❌ 改 readonly static + 缓存 — 需要手动 invalidate 缓存（线程安全复杂度）
- ❌ 每个 chunk 调用都读 `IOptions<>.Value` — 浪费（每秒几十次 chunk 都要走 Options 缓存）

实际抽取到 `ResolveSystemPrompt()` 一次后传给 `WithLlmActivity`，热路径不再额外读 Options。

### 2.4 ExtractionOrchestrator 用实例方法代替静态 const

`ExtractionOrchestrator.TBoxOnlyRunnerAsync` / `ABoxOnlyRunnerAsync` / `CombinedRunnerAsync` 原本直接读 `TBoxExtractionService.SystemPrompt` / `ABoxExtractionService.SystemPrompt` 来构建 prompt snapshot（持久化到 `ExtractionJobEntity.PromptSnapshot`）。

**方案**: 改为 `_tbox.ResolveSystemPrompt()` / `_abox.ResolveSystemPrompt()`。

**为什么**:
- 这是 §2.3 的连带改动：snapshot 必须保存实际发送的字节，不能再读 const
- 一致性：service 自己解析 prompt，orchestrator 不需要知道 SystemLanguage 存在

---

## 3. 实施

### 3.1 新增 `src/OnToPilot/Prompts/PromptLocales.cs`（约 350 行）

```csharp
public static class PromptLocales
{
    public enum SystemLanguage { English, SimplifiedChinese }

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<SystemLanguage, string>> _byKey = ...;

    public static string? Resolve(string key, SystemLanguage language) { ... }
    public static string? ResolveWithFallback(string key, SystemLanguage language) { ... }
    public static SystemLanguage ParseSystemLanguage(string? raw) { ... }
}
```

3 个 active keys 直接从 Python source 复刻：
- `tbox.extract.rag` — 来源 `backend/app/ontology/extract.py:29-117` (en) + `backend/app/prompt_locales.py:6-56` (zh-CN)
- `abox.extract` — 来源 `backend/app/ontology/abox_extract.py:178-224` (en) + `backend/app/prompt_locales.py:133-165` (zh-CN)
- `terminology.steward` — 来源 `backend/app/ontology/terminology_agent.py:24-60` (en) + `backend/app/prompt_locales.py:370-398` (zh-CN)

16 个 stub keys 用 `NotYetWiredStub` 占位文本（指向 dotnet gap tracker）。

### 3.2 3 个 service 改造

| 文件 | 改动 |
|---|---|
| `TBoxExtractionService.cs` | `using Microsoft.Extensions.Options` + `OnToPilot.Configuration` + `OnToPilot.Prompts`; `PromptKey "tbox.extract"` → `"tbox.extract.rag"`; 删除 `const string SystemPrompt`; 新增 `ctor(IOptions<OnToPilotOptions>)` + `ResolveSystemPrompt()` 实例方法; `ExtractAsync` 用 `var systemPrompt = ResolveSystemPrompt();` |
| `ABoxExtractionService.cs` | 同上 pattern; `PromptKey` 保持 `"abox.extract"` (本来就对) |
| `TerminologyAgent.cs` | 同上 pattern; `PromptKey "terminology.propose"` → `"terminology.steward"`; `BuildMessages` 从 `static` 改为 instance 方法以访问 `ResolveSystemPrompt()` |

### 3.3 ExtractionOrchestrator 调整

| 文件 | 改动 |
|---|---|
| `ExtractionOrchestrator.cs:269` | `[TBoxExtractionService.PromptKey] = TBoxExtractionService.SystemPrompt` → `_tbox.ResolveSystemPrompt()` |
| `ExtractionOrchestrator.cs:293` | `[ABoxExtractionService.PromptKey] = ABoxExtractionService.SystemPrompt` → `_abox.ResolveSystemPrompt()` |
| `ExtractionOrchestrator.cs:324-325` | 同上 (combined runner) |

### 3.4 测试更新

| 文件 | 改动 |
|---|---|
| `Tests/Extraction/ExtractionLlmFailureTests.cs` | 加 `using Microsoft.Extensions.Options; using OnToPilot.Configuration;` + `new TBoxExtractionService(Options.Create(new OnToPilotOptions()))` |
| `Tests/Extraction/ExtractionStateTests.cs` | 同上 |
| `Tests/Extraction/ExtractionCapacityKeyTests.cs` | 同上 |
| `Tests/Observability/TelemetryTests.cs` | 同上 + 静态属性 `Service = new(...)` |
| `IntegrationTests/Extraction/ExtractionWorkflowTests.cs` | 同上 |

### 3.5 新增测试 `Tests/Prompts/PromptLocalesTests.cs`（29 个 test cases）

- 3 个 active key × {en non-empty, zh-CN non-empty, en ≠ zh-CN} = 9 个 Theory cases
- 3 个 canonical PromptKey constant assertion（TBox/ABox/Terminology）
- 4 个 zh-CN 解析 case（zh-CN/ZH-CN/Zh-Cn/zh-cn → SimplifiedChinese）
- 7 个 fallback case（en/EN/""/null/fr/zh/zh_CN → English）
- 1 个 ResolveWithFallback stub fallback case
- 1 个 unknown key 返回 null case
- 4 个 service wire-up Theory cases（TBox/ABox × {en, zh-CN}）

---

## 4. 验证

### 4.1 编译

```bash
dotnet build src/OnToPilot/OnToPilot.csproj -c Debug
# 0 警告 0 错误
dotnet build src/OnToPilot.Tests/OnToPilot.Tests.csproj -c Debug
# 4 警告(预存,与本 ADR 无关) 0 错误
dotnet build src/OnToPilot.IntegrationTests/OnToPilot.IntegrationTests.csproj -c Debug
# 0 警告 0 错误
```

### 4.2 单元测试

```bash
dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~Extraction" --no-build
# 36/36 通过 (TBox/ABox snapshot + LlmFailure + State + Capacity)

dotnet test src/OnToPilot.Tests --filter "FullyQualifiedName~PromptLocales" --no-build
# 29/29 通过
```

### 4.3 E2E（回归）

```bash
docker compose -f docker-compose.yml restart backend
# health check: healthy in 9s

E2E_ADMIN_USERNAME=root E2E_ADMIN_PASSWORD='...' DOTNET_BASE_URL=http://localhost:8080 \
  pnpm exec playwright test e2e/dotnet --reporter=list
# session.spec ✅ (login/logout round-trip 3.0s)
# vocabulary.spec ❌ (P1-1 backend gap, scheme=0)
# upload-extract-publish.spec ❌ (P1-3 LLM provider 缺失)
```

**结论**: session 路径 0 回归。剩余 2 个失败是 §5 已登记的 P1 后端缺口，不是本 ADR 引入的回归。

---

## 5. 与前一个 P0 ADR 的 P1 缺口关系

前一个 ADR `2026-08-23-p0-captive-dep-and-a11y.md §5` 登记的 3 个 P1 缺口:

| ID | 状态 |
|---|---|
| P1-1 Vocabulary scheme=0 | 未触及 — vocabulary e2e 仍然因它失败 |
| P1-2 MinIO bucket 不自动创建 | 未触及 |
| P1-3 Extract LLM provider 缺失 | 未触及 — upload-extract-publish e2e 仍然因它失败 |

它们登记在 [[ontopilot-dotnet-gap-2026-08-22]] 里作为下一个 sprint 候选，本 ADR 不重复 plan。

---

## 6. 不在本次范围

- 不实现 `IPromptService` DI 抽象（当前静态 `PromptLocales` 满足需求）
- 不为 16 个 stub key 写实际翻译（跟着 P1 切片走）
- 不修改 `PromptCatalog.cs` placeholder — 它本来就未被任何 service 引用
- 不实现 IOptionsMonitor 触发热重载（`system_language` 切换在重启后生效已足够）

---

## 7. 参考

- [[2026-08-23-p0-captive-dep-and-a11y]] — 前一个 P0 ADR (captive-dep + a11y)
- [[ontopilot-dotnet-gap-2026-08-22]] — P1/P2 缺口跟踪
- [[ontopilot-p1-5-p1-6]] — Sprint N+1 prompt_locales 复刻 + 5 agent 对齐
- `backend/app/prompt_locales.py` — Python zh-CN 来源
- `backend/app/ontology/extract.py` — Python tbox.extract.rag en 默认
- `backend/app/ontology/abox_extract.py` — Python abox.extract en 默认
- `backend/app/ontology/terminology_agent.py` — Python terminology.steward en 默认
- `src/OnToPilot/Prompts/PromptLocales.cs:42-141` — 19 键目录
- `src/OnToPilot/Extraction/TBoxExtractionService.cs:23-58` — TBox 改造
- `src/OnToPilot/Extraction/ABoxExtractionService.cs:18-50` — ABox 改造
- `src/OnToPilot/Extraction/TerminologyAgent.cs:34-79` — Terminology 改造
- `src/OnToPilot/Extraction/ExtractionOrchestrator.cs:269,293,324-325` — snapshot 读取点
- `src/OnToPilot.Tests/Prompts/PromptLocalesTests.cs` — 29 个新单元测试
