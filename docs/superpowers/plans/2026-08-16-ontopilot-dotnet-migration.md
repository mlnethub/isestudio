# OnToPilot .NET 10 迁移实现计划

> **供智能体执行者使用：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans`，逐项执行本计划。所有步骤使用 `- [ ]` 复选框跟踪。

**目标：** 用 ASP.NET Core 10 后端替换 FastAPI 后端，同时保持现有前端、REST/MCP 契约、受治理 RDF 行为以及可验证的回滚路径不变。

**架构：** 迁移拆成六个可独立评审和验收的阶段。Python 后端在切换前始终作为可执行契约基准；.NET 仅对 PostgreSQL、RocksDB 和 blob 数据副本做并行验证，正式切换必须停写，因为 pyoxigraph 与 Oxigraph.NET 不能同时打开同一个存储目录。

**技术栈：** .NET 10、ASP.NET Core 10、EF Core 10、Npgsql、Oxigraph 0.5.8、Oxigraph.Extensions.DotNetRDF 0.5.8、dotNetRDF 3.5.2、Microsoft.Extensions.AI 10.7.0、ModelContextProtocol.AspNetCore 2.x、xUnit、Testcontainers、AWSSDK.S3、Serilog、OpenTelemetry

## 全局约束

- 目标框架固定为 `net10.0`；EF Core 必须使用 10.x。
- `Oxigraph` 与 `Oxigraph.Extensions.DotNetRDF` 固定为 `0.5.8`，禁止从上游 `0.6.0-dev` 分支 HEAD 构建。
- `dotNetRDF` 固定为 `3.5.2`，`Microsoft.Extensions.AI` 固定为 `10.7.0`。
- React/TypeScript 前端不修改；路由、状态码、Cookie、JSON 字段名、空值语义和错误信封必须兼容。
- Python 与 .NET 进程不得并发打开同一个 Oxigraph RocksDB 目录。
- 生产切换前必须停止 Python 写入、备份 PostgreSQL、复制 RDF 目录并演练回滚。
- 源文档字节迁到 MinIO；发布制品仍保留在 Oxigraph/发布存储中。
- API 不返回密钥，日志不记录密钥。
- 每个行为任务先写失败测试，完成时运行窄范围测试和所属项目测试。
- .NET 后端观察满 24 小时前不得删除 Python 后端和原始 RDF/blob 数据；备份至少保留 30 天。

## 兼容性决策

1. EF 实体按规格使用 GUID 主外键；凡现有 REST 路由或响应暴露整数 ID 的资源，同时保存不可变且唯一的 `LegacyId`。兼容 DTO 将 `LegacyId` 序列化为 `id`，仓储通过 `LegacyId` 解析路由参数。
2. 现有上传端点继续由后端接收字节并写入 MinIO。预签名直传会要求修改前端，因此不属于本次迁移。
3. 不允许两套线上后端共享可变 RocksDB 做混合流量灰度。契约和影子验证使用 SQL/RDF/blob 副本；生产仅在停写后一次切换。
4. 当前 Python 实现与 `docs/external-api.zh-CN.md` 是契约基准。设计文档中的实体、端点和 MCP 示例均不是完整清单。
5. `Store.OpenReadOnly()`、`LoadFromFile()` 等 Oxigraph.NET API 必须先针对锁定的 `0.5.8` 包做编译探针，不直接照搬规格中的示意代码。
6. SHACL 只承接声明式约束；角色证据、grounding、datatype 转换、property collision 等 `tbox_guard.py` 行为仍由 `TBoxGuard` 过程逻辑实现。

## 计划集

| 阶段 | 子计划                                                  | 可独立验收的交付物                                                 |
| ---- | ------------------------------------------------------- | ------------------------------------------------------------------ |
| 1    | `2026-08-16-ontopilot-dotnet-foundation.md`             | 可构建解决方案、契约清单、24 个 EF 实体、认证、启动恢复、健康检查  |
| 2    | `2026-08-16-ontopilot-dotnet-rdf-core.md`               | Oxigraph 封装、TBox/SKOS/ABox、SHACL、冲突、发布、导入导出         |
| 3    | `2026-08-16-ontopilot-dotnet-documents-llm.md`          | MinIO、解析/分块、Provider 工厂、向量、抽取任务                    |
| 4    | `2026-08-16-ontopilot-dotnet-api-mcp.md`                | 153 个 REST 路由声明与实际 `tools/list` 基线中的全部 MCP Tool 兼容 |
| 5    | `2026-08-16-ontopilot-dotnet-contract-observability.md` | 差分契约、E2E、日志/追踪/指标、生产镜像                            |
| 6    | `2026-08-16-ontopilot-dotnet-data-cutover.md`           | SQL/RDF/blob 迁移演练、切换与回滚手册                              |

---

### 任务 1：冻结可执行兼容基线

**文件：**

- 创建：`docs/migration/dotnet-contract-baseline.md`
- 创建：`backend/scripts/export_contract_baseline.py`
- 创建：`migration/baseline/openapi-python.json`
- 创建：`migration/baseline/mcp-tools-python.json`
- 测试：`backend/tests/test_dotnet_contract_baseline.py`

**接口：**

- 输入：当前 FastAPI 的 `app.openapi()` 和 MCP `tools/list` 响应。
- 输出：供阶段 4 差分测试使用的确定性 OpenAPI 与 MCP 清单；Tool 数量由实际导出结果锁定，不预设为 20 或 21。

- [ ] **步骤 1：编写确定性导出失败测试**

```python
def test_contract_export_is_deterministic(tmp_path):
    from scripts.export_contract_baseline import export_baseline

    first = export_baseline(tmp_path / "first")
    second = export_baseline(tmp_path / "second")
    assert first.openapi_bytes == second.openapi_bytes
    assert first.mcp_bytes == second.mcp_bytes
    assert len(first.operations) >= 130
    assert {tool["name"] for tool in first.mcp_tools}
```

- [ ] **步骤 2：运行测试并确认导出器尚不存在**

运行：`cd backend; python -m pytest tests/test_dotnet_contract_baseline.py -q`
预期：失败，包含 `ModuleNotFoundError: No module named 'scripts.export_contract_baseline'`。

- [ ] **步骤 3：实现规范化 JSON 导出**

```python
@dataclass(frozen=True)
class Baseline:
    openapi: dict
    mcp_tools: list[dict]
    operations: list[tuple[str, str]]
    openapi_bytes: bytes
    mcp_bytes: bytes


def _canonical(value: object) -> bytes:
    text = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return (text + "\n").encode()


def export_baseline(output: Path) -> Baseline:
    output.mkdir(parents=True, exist_ok=True)
    openapi = app.openapi()
    operations = sorted(
        (method.upper(), path)
        for path, methods in openapi["paths"].items()
        for method in methods
        if method.lower() in {"get", "post", "put", "patch", "delete"}
    )
    tools = sorted(read_tools_list_response(), key=lambda item: item["name"])
    openapi_bytes, mcp_bytes = _canonical(openapi), _canonical(tools)
    (output / "openapi-python.json").write_bytes(openapi_bytes)
    (output / "mcp-tools-python.json").write_bytes(mcp_bytes)
    return Baseline(openapi, tools, operations, openapi_bytes, mcp_bytes)
```

- [ ] **步骤 4：生成并验证入库基线**

运行：`cd backend; python -m pytest tests/test_dotnet_contract_baseline.py -q; python scripts/export_contract_baseline.py ../migration/baseline`
预期：测试通过；两个基线文件采用稳定排序；文档记录实际 operation 与 MCP Tool 数量，并说明源码 decorator 与协议结果的差异。

- [ ] **步骤 5：提交**

```bash
git add docs/migration backend/scripts/export_contract_baseline.py backend/tests/test_dotnet_contract_baseline.py migration/baseline
git commit -m "test: freeze backend compatibility baseline"
```

### 任务 2：执行阶段 1 基础计划

**文件：**

- 遵循：`docs/superpowers/plans/2026-08-16-ontopilot-dotnet-foundation.md`

**接口：**

- 输入：任务 1 的基线制品。
- 输出：`OnToPilot.sln`、可构建项目、EF schema、自定义 Session 认证、启动恢复和健康端点。

- [ ] **步骤 1：逐项执行阶段 1 子计划**

运行：`dotnet test src/OnToPilot.sln --configuration Release`
预期：所有基础单元测试和 PostgreSQL 集成测试通过。

- [ ] **步骤 2：执行阶段门禁**

运行：`dotnet format src/OnToPilot.sln --verify-no-changes; dotnet build src/OnToPilot.sln -warnaserror`
预期：两个命令均以 0 退出。

- [ ] **步骤 3：提交阶段结果**

```bash
git add src migrations docs/superpowers/plans/2026-08-16-ontopilot-dotnet-foundation.md
git commit -m "feat: establish dotnet backend foundation"
```

### 任务 3：执行阶段 2 RDF 核心计划

**文件：**

- 遵循：`docs/superpowers/plans/2026-08-16-ontopilot-dotnet-rdf-core.md`

**接口：**

- 输入：阶段 1 的 DI、配置和持久化抽象。
- 输出：与 Python gold fixture 规范化一致的本体服务。

- [ ] **步骤 1：逐项执行阶段 2 子计划**

运行：`dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "Category=RdfCore"`
预期：RDF CRUD、capture/revert、TBox、SKOS、ABox、SHACL、冲突、发布和导入导出测试通过。

- [ ] **步骤 2：仅对一次性副本运行跨绑定探针**

运行：`dotnet run --project src/OnToPilot.Migration -- rdf verify-copy --source backend/data/oxigraph --copy .artifacts/oxigraph-probe --queries migration/fixtures/rdf-smoke-queries.json`
预期：.NET 不打开源目录；报告副本的四元组数、具名图集合和查询结果哈希。

- [ ] **步骤 3：提交阶段结果**

```bash
git add src migration/fixtures docs/superpowers/plans/2026-08-16-ontopilot-dotnet-rdf-core.md
git commit -m "feat: port governed rdf core"
```

### 任务 4：执行阶段 3 文档与 LLM 计划

**文件：**

- 遵循：`docs/superpowers/plans/2026-08-16-ontopilot-dotnet-documents-llm.md`

**接口：**

- 输入：阶段 1 仓储和阶段 2 本体变更服务。
- 输出：文档存储、解析、分块与 Provider 无关的抽取编排。

- [ ] **步骤 1：逐项执行阶段 3 子计划**

运行：`dotnet test src/OnToPilot.Tests/OnToPilot.Tests.csproj --filter "Category=Documents|Category=Llm|Category=Extraction"`
预期：解析降级、分块 gold fixture、MinIO、Provider 路由和抽取状态测试通过。

- [ ] **步骤 2：用确定性 Fake Chat Client 验证完整抽取**

运行：`dotnet test src/OnToPilot.IntegrationTests/OnToPilot.IntegrationTests.csproj --filter "FullyQualifiedName~ExtractionWorkflow"`
预期：上传、解析、抽取、术语、provenance 和任务完成断言通过，不访问外部 LLM。

- [ ] **步骤 3：提交阶段结果**

```bash
git add src migration/fixtures docs/superpowers/plans/2026-08-16-ontopilot-dotnet-documents-llm.md
git commit -m "feat: port documents and extraction pipeline"
```

### 任务 5：执行阶段 4 REST 与 MCP 计划

**文件：**

- 遵循：`docs/superpowers/plans/2026-08-16-ontopilot-dotnet-api-mcp.md`

**接口：**

- 输入：阶段 1-3 的应用服务和任务 1 基线。
- 输出：兼容 Controller、外部 Token API、Published API 和认证 MCP 端点。

- [ ] **步骤 1：逐项执行阶段 4 子计划**

运行：`dotnet test src/OnToPilot.ApiContract.Tests/OnToPilot.ApiContract.Tests.csproj`
预期：路由、方法、状态码、schema、错误与 Cookie 契约通过。

- [ ] **步骤 2：比较生成清单**

运行：`dotnet run --project src/OnToPilot.ContractExporter -- --openapi migration/actual/openapi-dotnet.json --mcp migration/actual/mcp-tools-dotnet.json; dotnet test src/OnToPilot.ApiContract.Tests --filter "FullyQualifiedName~InventoryParity"`
预期：基线中的每个 operation 均有对应实现；MCP Tool 的名称、必需参数和权限元数据一致。

- [ ] **步骤 3：提交阶段结果**

```bash
git add src migration/actual docs/superpowers/plans/2026-08-16-ontopilot-dotnet-api-mcp.md
git commit -m "feat: expose compatible rest and mcp contracts"
```

### 任务 6：执行阶段 5 验证与运维计划

**文件：**

- 遵循：`docs/superpowers/plans/2026-08-16-ontopilot-dotnet-contract-observability.md`

**接口：**

- 输入：使用隔离 fixture 数据运行的 Python 与 .NET 后端。
- 输出：差分测试、E2E、遥测断言、生产镜像和 Compose 拓扑。

- [ ] **步骤 1：逐项执行阶段 5 子计划**

运行：`dotnet test src/OnToPilot.sln --configuration Release; cd frontend; pnpm test; pnpm build`
预期：全部 .NET 测试通过；未修改前端的测试和构建命令以 0 退出。

- [ ] **步骤 2：在数据副本上运行差分与浏览器测试**

运行：`pwsh migration/scripts/Invoke-ContractComparison.ps1 -PythonUrl http://localhost:18000 -DotNetUrl http://localhost:18080; pnpm --dir frontend exec playwright test`
预期：不存在未解释的 JSON、状态码或响应头差异；上传到发布、词汇和 MCP 场景通过。

- [ ] **步骤 3：构建并健康检查生产镜像**

运行：`docker compose build backend; docker compose up -d postgres minio backend; docker compose ps`
预期：后端、PostgreSQL 和 MinIO 均为 healthy；`/api/health` 返回 200。

- [ ] **步骤 4：提交阶段结果**

```bash
git add src frontend docker-compose.yml backend/Dockerfile migration docs/superpowers/plans/2026-08-16-ontopilot-dotnet-contract-observability.md
git commit -m "test: verify dotnet backend parity and operations"
```

### 任务 7：执行阶段 6 迁移演练与切换计划

**文件：**

- 遵循：`docs/superpowers/plans/2026-08-16-ontopilot-dotnet-data-cutover.md`

**接口：**

- 输入：候选发布镜像和生产形态的备份副本。
- 输出：签字确认的演练报告、迁移 manifest、切换和回滚命令。

- [ ] **步骤 1：执行到演练门禁为止的全部复选框**

运行：`pwsh migration/scripts/Invoke-MigrationRehearsal.ps1 -BackupPath .artifacts/production-backup -ReportPath .artifacts/migration-report.json`
预期：SQL 表行数/校验和、RDF 图计数/查询哈希、blob SHA-256 manifest 一致；回滚演练可让 Python 服务使用原数据副本恢复。

- [ ] **步骤 2：取得明确的生产 go/no-go 批准**

在 `migration/runbooks/production-cutover-record.md` 记录已批准的镜像 digest、备份位置、维护窗口、操作人、回滚负责人和报告校验和。

- [ ] **步骤 3：在批准窗口执行生产切换**

运行：`pwsh migration/scripts/Invoke-ProductionCutover.ps1 -Record migration/runbooks/production-cutover-record.md`
预期：脚本强制检查 Python 已停止、数据库停写、备份已验证、RDF 独占访问和冒烟测试通过，并启动 24 小时观察计时。

- [ ] **步骤 4：结束观察期**

运行：`pwsh migration/scripts/Complete-Observation.ps1 -Record migration/runbooks/production-cutover-record.md`
预期：错误率、延迟、抽取、RDF、MinIO 和 MCP 指标均在记录阈值内；备份删除日期不得早于第 30 天。

- [ ] **步骤 5：提交迁移证据**

```bash
git add migration/runbooks migration/manifests docs/superpowers/plans/2026-08-16-ontopilot-dotnet-data-cutover.md
git commit -m "ops: record dotnet migration cutover"
```

## 最终验收门禁

- [ ] `dotnet test src/OnToPilot.sln --configuration Release` 通过。
- [ ] `dotnet format src/OnToPilot.sln --verify-no-changes` 通过。
- [ ] `dotnet build src/OnToPilot.sln -warnaserror` 通过。
- [ ] 未修改的前端构建成功，且针对 .NET 的 E2E 通过。
- [ ] OpenAPI 差分测试覆盖基线中的每个 operation。
- [ ] 基线中的全部 MCP Tool 通过发现、认证、授权和行为测试。
- [ ] Python gold fixture 与 .NET 本体输出规范化一致。
- [ ] SQL、RDF 和 blob 迁移演练生成机器可读的对等报告。
- [ ] 生产切换前已实际演练回滚。
- [ ] 测试和运维过程中均未出现 Python/.NET 并发打开同一 RocksDB 目录。
