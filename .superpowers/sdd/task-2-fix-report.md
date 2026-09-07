# Task 2 Fix Report

## Scope

修复 `5f63cf2` 独立审查指出的 durable multi-format ingestion 问题。

## Changes

- RDF/OWL fallback 复用 `RdfImportParser` 的 triple 结构，按稳定字典序输出 subject、predicate、object，并保留 named node、blank node、literal datatype 和 language。
- `DocumentIngestionJobProcessor` 在 parser 前对 CAS 实际读取字节重新计算 SHA-256；不匹配时标记 job/document failed，并在创建版本前抛错。
- `ParserExtractionJobHandler` 的 `document_sha256` 改为 optional，旧 payload 继续工作；新 parser durable 测试 producer 默认写入该字段。
- HTML fallback 测试改为成功断言；PPTX unsupported 断言保留。
- 增加 RDF 内容碰撞、CAS 替换、历史 payload 兼容和 durable worker 回归覆盖。

## Verification

- `ParserFallbackTests`: 6 passed.
- `DocumentIngestionJobProcessorTests`: 10 passed.
- `DurableExtractionWorkerTests`: 11 passed.
- `ExtractionWorkflowTests`: 1 passed.
- `FullyQualifiedName~Ingestion`: passed.
- `git diff --check`: passed.

现有测试工程仍报告 4 个预先存在的 warning，位于 `ReleaseServiceTests`、`HistoryServiceTests` 和 `ExportArtifactStoreTests`，本次未改动。

## Residual Risks

- 当前仓库没有独立的 production parser-job enqueue service；因此 `document_sha256` 的 producer 契约在现有 durable producer test helper 中固定，并由 handler/processor 负责兼容与最终内容校验。
- PDF/DOCX/XLSX 的 parser 单元测试已存在；本次新增的 durable processor matrix 直接覆盖 TXT、HTML、RSS、RDF、OWL，未重复引入大型二进制 fixture。
