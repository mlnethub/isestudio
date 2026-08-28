# Vocabulary 应用服务抽取 + dispatcher → application-service 拆分(5/13)

**状态**: 已完成(5/13 slice 落地,850 unit + 167 contract 全绿)
**日期**: 2026-08-28
**分支**: `dotnet`
**范围**: 28 个 `vocabulary.*` / `external.vocabulary.*` / `published.vocabulary.*` / `published.release.vocabulary.*`
operation,从 `InternalOperationDispatcher` god-class 拆出一个
`IVocabularyApplicationService` (定义在 `ISEStudio.Application.Integration`,实现在
`ISEStudio.Integration`),并把 10 个 SKOS DTO + 1 个 `TermProposalOut` DTO 从
`ISEStudio.Ontology` 搬到 `ISEStudio.Application.Vocabulary`。

接续 [2026-08-28-abox-application-service-pilot.md](2026-08-28-abox-application-service-pilot.md)
定下的 12 dispatcher-slice 拆分模板(abox 已完,conflicts → documents → releases
→ **vocabulary 5/13**,IN PROGRESS),本切片验证模板在 28-arms 高扇出 slice
的可用性,并修复模板的一个 pre-existing 漏洞(`DeserializeLooseBody` 把
JSON 数组降级为 `string`,`ExtractChunkIds` 看不见)。

---

## 1. 背景

`InternalOperationDispatcher` 在 4/13 切片后 ~3841 行,其中 vocabulary section
占 537 行(原 lines 3088-3624),承载 28 个 helper:

- 16 个 internal `vocabulary.*` reads/writes:
  - reads (5):`get` / `list_schemes` / `list_concepts` / `resolve_term` / `export`
  - scheme writes (3):`create_scheme` / `update_scheme` / `delete_scheme`
  - concept writes (3):`create_concept` / `update_concept` / `delete_concept`
  - sync (1):`sync`
  - terminology (4):`list_proposals` / `accept_proposal` / `reject_proposal` /
    `suggest_terms`
- 4 个 cross-surface published reads:
  `list_concepts` / `export` / `resolve_term` / `list_schemes`
- 8 个 1 行 facade:
  `external.vocabulary.{list_concepts, export, resolve_term, list_schemes}` +
  `published.vocabulary.{list_concepts, export, resolve_term, list_schemes}` +
  `published.release.vocabulary.{list_concepts, export, resolve_term, list_schemes}`

每个 helper 都重复 4 段 boilerplate:`ResolveVocabularyService()` /
`VocabularyProposalService` / `TerminologyAgent` 解析 + KS envelope 拆解 +
`DeserializeBody<T>` / `ExtractBodyIri` / `QueryString` / `QueryInt` +
`WrapAsync` + null-coalesce 到 `EmptyXxx` 匿名 fallback。同时 SKOS DTO
(`SkosSchemeData` / `SkosLabel` / `SkosConceptData` / `SkosConceptView` /
`SkosSchemeView` / `SkosView` / `SkosStats` / `SkosMatch` / `SkosConceptPage`)
和 `TermProposalOut` 都住在 `ISEStudio.Ontology`,跟 abox slice 的 21 个
ABox DTO 一样阻塞应用服务接口。

## 3. 决策

### 3.1 DTO 搬入 `ISEStudio.Application`(沿用 2.1 模板)

**结论**:搬。命名空间 `ISEStudio.Application.Vocabulary`。

**实现细节**:
- `git mv` 9 个 SKOS DTO + 1 个 `TermProposalOut` → `ISEStudio.Application/Vocabulary/SkosDtos.cs` + `TermProposalOut.cs`
- `SkosConceptData.EffectiveAltLabels` / `EffectiveHiddenLabels` /
  `EffectiveBroader` / `EffectiveRelated` 4 个 internal 成员 **改为
  `public`** —— `ISEStudio.Ontology/SkosManager.cs` 不是
  `ISEStudio.Application` assembly 的友元,看不到 internal 成员,
  CS1061 编译错。
- 5 个 web 端消费者(`SkosManager.cs` / `VocabularyProposalService.cs` /
  `VocabularyService.cs` / `TerminologyService.cs` /
  `VocabularyProposalApiTests.cs` / `SkosManagerTests.cs` /
  `TerminologyServiceTests.cs` / `ShaclValidatorTests.cs` /
  `ABoxManagerTests.cs`)加 `using ISEStudio.Application.Vocabulary;`。

### 3.2 应用服务接口 = 16 internal + 4 cross-surface = 20 个强类型方法

**结论**:签名采用 `Task<TOut?>(InternalRequest, CancellationToken)`,20 个方法。

**理由**:
- 沿用 abox slice 的 envelope 入参(2.2),让 app service 接受
  `InternalRequest` 而非 5 个散参数。
- cross-surface 4 个 reads 通过 `request.PublicId` 解析 KS(外部 /
  已发布路由只用 public id),internal 16 个用 `request.KnowledgeSystemGuid`
  —— `VocabularyApplicationService` 把这两条路径分别委托给
  `InternalRequestHelpers.ResolveKsAsync` / `ResolveKsByPublicIdAsync`,
  复用 dispatcher 跨 slice helpers。
- cross-surface 是 future extension 的接缝:3 个 surface(external /
  published / published.release)目前共享 `VocabularyService` 4 个 read
  方法,Reader gate 在 service 内部 enforcement access;release-pinned
  graph 区分是 follow-up,本切片不打开。

### 3.3 dispatcher arm 不动,只缩 helper(沿用 2.3)

**结论**:28 个 `InvokeVocabulary*Async` / `InvokeExternal*Async` /
`InvokePublished*Async` helper 都缩成 1 行委托。

**实现细节**:
- 28 个 helper 中 20 个(`16 internal + 4 cross-surface`)需要 ResolveKsAsync
  + WrapAsync + null-degrade → 提取成 `InvokeVocabularyAsync` 共享 wrapper,
  签名 `Func<IVocabularyApplicationService, Task<object?>> call`,
  `Func<object> onMissing`, `Func<object>? onNull = null`。
- 8 个 1 行 facade(`external.{list_concepts, export, resolve, list_schemes}` +
  `published.{list_concepts, export, resolve, list_schemes}` +
  `published.release.{list_concepts, export, resolve, list_schemes}`)都是
  直接转发到 4 个 cross-surface helper,不需要 wrapper。

### 3.4 守卫包装 (`WrapAsync`) 留在 dispatcher arm 上(沿用 2.4)

`InvokeVocabularyAsync` 内部 `WrapAsync(async () => { ... })`,
`InvalidOperationException` 由 `FastApiErrorMiddleware` 翻译成
`{ "detail": "..." }` envelope(同 abox 2.4)。

### 3.5 dispatcher 私有 `QueryString` / `QueryInt` / `ResolveKsAsync` 等保留为 shim

**结论**:不删,改为 `InternalRequestHelpers.X` 的 1 行委托。

**理由**:
- abox / sparql / releases slice 仍然调用 `QueryString` / `QueryInt` /
  `ResolveKsAsync` 等 dispatcher 私有 helper。
- vocabulary 块已经移走了 `ResolveKsAsync` / `QueryString` 等 helper 的
  定义(它们本来在 vocabulary section 顶部)。
- 最小变更路径:在 dispatcher 文件顶部加 1 段 shim section,让现有
  调用方继续编译。abox / sparql slice 自己的 dispatcher 拆分切片再把
  调用方改成 `InternalRequestHelpers.X`。

### 3.6 `SuggestTermsAsync` 必须用 `DeserializeBody<Dictionary<string,object?>>`,**不是** `DeserializeLooseBody`(★新增★)

**根因**:`InternalRequestHelpers.DeserializeLooseBody` 通过
`JsonElementToObject(prop.Value)` 转换每个 prop。`JsonElementToObject`
的 default 分支返回 `el.GetRawText()`(string)。JSON 数组会被降级为
raw text,`ExtractChunkIds` 看不到数组,返 `Array.Empty<Guid>()`,
TerminologyAgent 收到空 chunk list,提早 return,proposals total = 0。

**触发测试**:`VocabularyApiTests.Suggest_with_fake_chat_creates_pending_proposals`
期望 total=3,实际 0。

**修复**:VocabularyApplicationService.SuggestTermsAsync 用
`DeserializeBody<Dictionary<string, object?>>`(保持 `chunk_ids` 为
`JsonElement` array),而非 `DeserializeLooseBody`(降级为 string)。

**Follow-up 候选**:把 `DeserializeLooseBody` 改成把数组保留为 `JsonElement`,
所有 caller 都受益;但这是 P3-12 单独的 task,本切片只 fix caller。

---

## 4. 文件清单

### 新增

| 文件 | 行 | 说明 |
|------|----|----|
| `src/ISEStudio.Application/Vocabulary/SkosDtos.cs` | 110 | 9 个 SKOS DTO |
| `src/ISEStudio.Application/Vocabulary/TermProposalOut.cs` | 35 | 18-field proposal DTO |
| `src/ISEStudio.Application/Integration/IVocabularyApplicationService.cs` | 150 | 20-method 接口 |

### 修改

| 文件 | 改动 |
|------|----|
| `src/ISEStudio/Integration/InternalOperationDispatcher.cs` | -336 +25 行(vocabulary section 537 → 201 行,shim +25 行) |
| `src/ISEStudio/Integration/VocabularyApplicationService.cs` | +270 行(实现) |
| `src/ISEStudio/Ontology/SkosManager.cs` | -98 行(DTO 搬走) |
| `src/ISEStudio/Ontology/VocabularyProposalService.cs` | -29 行(`TermProposalOut` 搬走) |
| `src/ISEStudio/Ontology/VocabularyService.cs` | +5 行(using + DI) |
| `src/ISEStudio/Extraction/TerminologyService.cs` | +1 行(using) |
| 5 test files | +5 行(`using ISEStudio.Application.Vocabulary;`) |

### dispatcher 行数

- 前:3841 行
- 后:3530 行(vocabulary section 537 → 201 行,shim +25 行)
- 净减少 **311 行**

## 5. 验证

```
$ dotnet build src/ISEStudio/ISEStudio.csproj
  0 错误 / 0 警告

$ dotnet test src/ISEStudio.Tests/ISEStudio.Tests.csproj
  通过:   850, 已跳过: 1, 失败: 0 / 总: 851 (1 m 43 s)

$ dotnet test src/ISEStudio.ApiContract.Tests/...
  通过:   167, 已跳过: 0, 失败: 0 / 总: 167 (59 s)
```

P3-11 测试矩阵中 `VocabularyApiTests.Suggest_with_fake_chat_creates_pending_proposals`
作为本切片发现并修复的 ★DeserializeLooseBody 数组降级 bug★ 的回归测试,
原 dispatcher 因为用 `DeserializeBody<Dictionary<string,object?>>` 而幸免。

---

## 6. 后续切片(剩 8)

按用户锁定的 [ontopilot-dispatcher-split-workflow](ontopilot-dispatcher-split-workflow.md) push order:

- [ ] 6/13 ontology (mutations + impact walk + audit diff)
- [ ] 7/13 extraction (lifecycle + job_id envelope)
- [ ] 8/13 resolution
- [ ] 9/13 history
- [ ] 10/13 prompts
- [ ] 11/13 external + published (4 读端点)
- [ ] 12/13 providers + settings + auth + knowledge + tokens + mcp_tokens
- [ ] 13/13 rdf.import

每个切片都会复用本切片定下的 4 段模式:
1. DTO 搬入 `ISEStudio.Application`
2. `IXxxApplicationService` 接口 + envelope 入参
3. dispatcher arm 不动,helper 缩成 1 行委托
4. 守卫包装留在 arm 上,不沉到 app service

`DeserializeLooseBody` 的数组降级 bug 是 P3-12 候选,影响 accept_proposal /
reject_proposal / suggest_terms 三个 endpoint,本切片只修了 suggest_terms,
其余两个路径走 `payload` override 而不是 array,没有立即 regression。

---

## 7. Decision Log

- 2026-08-28: 5/13 vocabulary slice 完成(commits 待 push)。
  本切片锁定 28-arms slice 的拆分模式,确认 `DeserializeBody<Dictionary<string,object?>>`
  对带数组字段的请求是必需选择;`InternalRequestHelpers.DeserializeLooseBody`
  的数组降级 bug 列入 P3-12 follow-up。