# P1-1: Vocabulary scheme=0 backend gap（默认 ConceptScheme 自动创建）

**状态**: 已完成（修复 + 单元测试 + 全量回归）
**日期**: 2026-08-23
**分支**: `dotnet`
**范围**: `src/OnToPilot/Extraction/TerminologyService.cs` + `src/OnToPilot/Ontology/KsContext.cs` + `src/OnToPilot/Extraction/ExtractionOrchestrator.cs` + 1× 新测试文件

---

## 1. 背景

对一个**抽取过**的知识系统，`GET /api/knowledge/{id}/vocabulary/schemes` 返回 `scheme_count: 0`，但 `concept_count: 44`。前端 `VocabularyPanel` 的 "New term" 按钮因此永久禁用 —— `selectedSchemeIri` 为空（`pickPrimaryScheme` 找不到任何 scheme）。

| ID | 现象 | 根因 |
|---|---|---|
| P1-1 | schemes=[] 但 concepts 有 44 条 | `TerminologyService.SyncCore` 缺 `ensure_scheme` + concept 不写 `skos:inScheme` |

这个缺口在 P0 ADR（`2026-08-23-p0-captive-dep-and-a11y.md §5.1`）登记，本 ADR 是它的收尾。

---

## 2. 根因（与 Python parity 对比）

Python 后端 `backend/app/ontology/terminology_sync.py::sync_from_ontology`：

1. 开头调用 `ensure_scheme(ks)` —— 当 vocabulary graph 无任何 scheme 时，用固定 IRI `{vocabulary_graph}#scheme-extracted` 创建 default scheme（title = `{ks.name}术语表` / `{ks.name} terminology`，`origin="extraction"`）。
2. `create_concept` 传 `scheme_iri: scheme["iri"]`，`skos._concept_triples` 为每个 concept 写 `skos:inScheme` triple。

.NET 移植 `TerminologyService.SyncCore` 两处都漏了：

- **无 `ensure_scheme`** —— 从不创建 scheme，于是 `scheme_count` 恒为 0。
- **concept 不写 `inScheme`** —— 即使未来有 scheme，`SkosConceptView.SchemeIri` 也是空串，concept 无法关联到任何 scheme（`BuildView` 里 `schemes_for_concept` 为空）。

---

## 3. 决策

### 3.1 在 `SyncCore` 内 `EnsureScheme` + 写 `inScheme`（不是懒创建 / 启动钩子）

镜像 Python 的落点：scheme 的保证发生在 **terminology sync**（TBox 抽取后）里，而不是 VocabularyService 的某个读路径。sync 是唯一"有 entities 才发生"的自然时机，且自带幂等（RDF 四元组天然去重）。

`EnsureScheme` 决策顺序（逐字对照 Python `ensure_scheme`）：

1. TBox 无 entities（classes + object_properties + data_properties 全空）→ 返回 `null`（不建空 scheme）。
2. 已存在 `#scheme-extracted` → 复用。
3. 恰好 1 个 scheme → 复用。
4. 有 `origin=extraction` 的 scheme → 取 `concept_count` 最大者。
5. 有其他 scheme → 取 mapped concept 数最多者（tie-break 按 concept count）。
6. 无任何 scheme → 用 `{vocabulary_graph}#scheme-extracted` 创建 default scheme。

### 3.2 `KsContext` 增加 `Name`（向后兼容的默认参数）

Python 的 `_scheme_title(ks)` 用 `ks.name` 生成 scheme title（中文名→中文 title）。.NET 的 `KsContext` 原本只有 `GraphIri` + `BaseIri`，加一个 `string Name = ""` 默认参数即可，`FromEntity` 与 orchestrator 构造点显式传入 `entity.Name`，测试构造点因默认值不受影响。

### 3.3 语言判定对齐 `_language`

scheme title/description 按 KS name 是否含 CJK 统一表意文字（`[㐀-鿿]`）选 `zh-CN` 或 `en`，文案逐字复制 Python。

---

## 4. 实施

### 4.1 `TerminologyService.SyncCore`（核心改动）

- 在 `classes.Count == 0` 短路之后、`mappedIndex` 之前调用 `EnsureScheme(ks, view)`；返回 `null`（无 entities）则直接 `Zero`。
- concept 四元组新增 `SkosVocab.InScheme → schemeIri`，并把重复构造的 `new OntoNamedNode(...)` 提到 `concept` 局部变量。

### 4.2 新增私有方法

- `EnsureScheme(KsContext, OntologyView)` —— 上述决策顺序。
- `SchemeTitle(string ksName)` —— 返回 `(title, description, language)`。
- `ContainsCjk(string?)` —— 判定中文字符。

### 4.3 `KsContext` + orchestrator 传 name

- `KsContext.cs`：record 增加 `string Name = ""`；`FromEntity` 传 `entity.Name`。
- `ExtractionOrchestrator.cs:185`：`new KsContext(..., Name: ksEntity.Name)`。

---

## 5. 验证

### 5.1 新增单元测试（`TerminologyServiceTests`,4/4 通过）

- `Sync_creates_default_scheme_when_vocabulary_is_empty` —— 2 个 class → 1 scheme + 2 concept，scheme IRI = `#scheme-extracted`、origin=extraction、title=英文，所有 concept 的 `scheme_iri` 指向它。
- `Sync_is_idempotent_and_reuses_existing_scheme` —— 二次 sync terms_added=0，scheme 仍 1 个。
- `Sync_with_chinese_name_uses_chinese_scheme_title` —— name 含中文 → title `{name}术语表` + `zh-CN`。
- `Sync_without_tbox_classes_creates_nothing` —— 无 TBox → 0 scheme 0 concept。

### 5.2 全量回归

- 主项目 + Tests + 全解决方案 0 错误 0 警告。
- `OnToPilot.Tests` 全量 600/600 通过（含新增 4 个）。
- 前端链路确认：`VocabularyPanel.pickPrimaryScheme` 优先匹配 `#scheme-extracted`（`VocabularyPanel.tsx:38`），修复后 `selectedSchemeIri` 非空，`newTerm` 按钮（`disabled={!selectedSchemeIri}`）解锁。

---

## 6. 遗留 / 不在本次范围

`TerminologyService.SyncCore` 仍只是 Python `sync_from_ontology` 的一个**子集**，以下 parity 缺口未在本次 P1-1 处理（仍待后续）：

- **properties**：Python 对 `object_properties` + `data_properties` 也建 mapped concept；.NET 只处理 `classes`。
- **stale_mappings_removed**：移除指向已删除 ontology entity 的过期映射。
- **aliases_added**：为已 mapped concept 追加缺的 label。
- **broader_added**：OWL subclass 层级 → `skos:broader`。
- **mapping_conflicts**：label 冲突计数。

以上属 `terms_added`/`terms_mapped` 之外的更细粒度 parity，独立于 scheme=0 现象，另行跟踪。

---

## 7. 参考

- [[2026-08-23-p0-captive-dep-and-a11y]] — §5.1 登记本缺口
- `backend/app/ontology/terminology_sync.py::ensure_scheme` / `_scheme_title` / `sync_from_ontology`
- `backend/app/ontology/skos.py::_concept_triples` — `skos:inScheme` 写入点
- `src/OnToPilot/Extraction/TerminologyService.cs` — EnsureScheme / SyncCore
- `src/OnToPilot/Ontology/KsContext.cs` — Name 字段
