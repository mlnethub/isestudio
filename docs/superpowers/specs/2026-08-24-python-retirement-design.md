# Python Backend Retirement — Design Spec

**Status**: 待用户确认
**Date**: 2026-08-24
**Branch**: `dotnet`
**User decision**: 完整退役(完整退役 > 仅删代码 > 删代码+解锁 Guid Phase 2);保留 `backend/data/` 本地数据

## 1. 背景

OnToPilot 的 wire-side 已 100% 迁移到 .NET(docker-compose 栈为 ".NET 10 backend + PostgreSQL + MinIO + Frontend",backend 镜像 `ontopilot-backend`,P1/P2/P3 缺口全部闭环,858 unit + 167 contract 全绿)。Python FastAPI 后端(`backend/`,102 个 .py)仅作为 parity baseline 保留,git 中最后 3 个 commit 均为移植工作("port document parsing and chunking")。

用户决定:完全退役 Python。退役同时解锁 Guid PK Phase 2(删 `legacy_id` 列 + 退役 `LegacyIdAllocator`)的前置条件(原 ADR 约束"等 Python 退役后")。

## 2. 退役边界

### 2.1 删除

| 对象 | 说明 |
|---|---|
| `backend/` 全部 tracked 文件 | 102 .py + tests + `requirements*.txt` + `pytest.ini` + `.dockerignore` + `.gitignore`(backup 另见 §3) |
| **除** `backend/data/` | 已 gitignore,磁盘保留(用户决策);`rm -rf` 前先 `mv` 出或用 `git rm` 只删 tracked 文件 |

### 2.2 保留(有裁决理由)

| 对象 | 理由 |
|---|---|
| `migration/baseline/regenerate_for_guid.py` | one-shot baseline 转换的 audit trail(Guid 迁移切片裁决"保留作为审计记录");非运行时 Python;Phase 2 类似工具会复用 |
| `docs/superpowers/specs/` 17 篇 + `docs/superpowers/plans/` | 历史 parity 文档,记录"当时对齐 Python"的事实;改动量大且破坏历史价值 |
| `docs/migration/` 3 篇(contract-difference-policy / dotnet-contract-baseline / MIGRATION_REPORT) | 迁移过程档案,性质同上 |
| .NET 代码注释里的 "Python baseline" / "matches the Python backend" | parity 文档价值;大 diff 无收益 |
| 顶层 `.gitignore` 的 `backend/` 条目 | 继续保护磁盘上的 `backend/data/` 不被误 add |

### 2.3 改写(活文档,共 6 文件)

**README.md**(15 处 Python 引用,逐处处理):

| 行/段 | 现状 | 改法 |
|---|---|---|
| :15 badge | `![Python](...3.12%2B...)` | 删;补 `.NET` badge(`![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)`) |
| :110 架构图 | `API["FastAPI Backend"]` | `API["ASP.NET Core Backend"]` |
| :129 组件表 | `FastAPI \| Authentication, project permissions, ...` | `ASP.NET Core 10 \| Authentication, project permissions, ...` |
| :151 Docker 配置步骤 | `cp backend/.env.example backend/.env`(文件已不存在) | 删该行 |
| :165-171 backend/.env 段 | `# backend/.env` + `OPENROUTER_API_KEY` 等 | 段删除;若 .NET 侧等效变量在顶层 `.env.example` 已有,改为引用顶层文件 |
| :215 seed_demo | `python backend/scripts/seed_demo.py` | 删 Python 分句,保留 `SEED_DEMO_DATA=true` 句 |
| :284 配置参考 | `backend/.env.example` | 只留 `.env.example` |
| :292 配置表 `DATABASE_URL` | `local SQLite`(SQLAlchemy URL 是 Python 术语) | 按 .NET 现实改:`ConnectionStrings__Default`(以 `docker-compose.yml` + `OnToPilotOptions` 实际名称为准,实施时核对) |
| :297 `MCP_PUBLIC_URL` 默认 | `http://localhost:8000/mcp` | `http://localhost:8080/mcp`(与 :160 一致) |
| :307-325 Source Development | Python 3.12+ 需求 + venv/uvicorn 段 | 重写:`dotnet run --project src/OnToPilot` + dev 数据说明按 .NET 实际存储位置 |
| :336 dev proxy | 8000 | `http://localhost:5072`(.NET launchSettings 已核实:`src/OnToPilot/Properties/launchSettings.json` `applicationUrl: http://localhost:5072`) |
| :348-352 Testing 段 | `pytest` + `python scripts/...` | 重写:`dotnet test src/OnToPilot.Tests` + `dotnet test src/OnToPilot.ApiContract.Tests`(+ integration 软跳说明) |
| :403 troubleshooting | `Source frontend calls port 8000 unexpectedly` | 改为 5072 措辞 |

**README.zh-CN.md**(13 处):与英文版同源改写,中文对应。

**docs/architecture.md**(3 处):
- :9 `API["FastAPI governance API"]` → `API["ASP.NET Core governance API"]`
- :57 `participant API as FastAPI` → `participant API as ASP.NET Core`
- :92 "ABox export never materializes the complete graph in Python memory" → "…in memory"(去 Python 字样)

**frontend/.env.example** + **frontend/vite.config.ts**:默认 `http://127.0.0.1:8000` → `http://localhost:5072`(注释同步)。

**frontend/README.md**::26 backend URL 8000 → 5072。

**docs/acceptance.md**:Python 引用段按 .NET 实际命令改写(实施时读全文定位)。

> 注:`docker-compose.yml`、`Dockerfile`、顶层 `.env.example` 已无 Python 引用(扫描确认),不动。

## 3. 可恢复性

- 删除 commit 前打 tag:`git tag pre-python-retirement`(落在删除前最后一 commit 上)
- git history 本身保留全部 backend/ 内容;tag 提供一键 checkout 点
- `backend/data/` 磁盘保留(用户决策),`git rm` 只删 tracked 文件,不触碰 data/

## 4. 验证标准(退役完成的定义)

1. `git ls-files backend/` 为空(除 0 文件)
2. 全量测试:858/858 unit + 167/167 contract 全绿(零 .NET 生产代码改动,应天然绿)
3. `git grep -i "uvicorn\|fastapi\|python -m venv\|pytest" -- README.md README.zh-CN.md docs/architecture.md docs/acceptance.md frontend/` 零命中(排除 §2.2 保留文件)
4. `git grep "8000" -- frontend/` 零命中(5072 到位)
5. `docker compose config --quiet` 通过(compose 未改动,验证无意外)
6. tag `pre-python-retirement` 存在且指向正确 commit
7. `git status` 干净(除 `backend/data/` 的 untracked 状态,由 .gitignore 覆盖)

## 5. 退役后解锁(登记,非本切片)

- **Guid PK Phase 2**(adr-gap D 组 7 项):删 `legacy_id` 列、退役 `LegacyIdAllocator`、简化 `LegacyAddressableEntity` — 前置条件"Python 退役"已满足,登记为下一候选切片
- memory:`ontopilot-python-retirement.md` 新文件 + `ontopilot-dotnet-gap-2026-08-23.md` / `ontopilot-adr-gap-2026-08-23.md` 同步(Guid Phase 2 前置条件解除)+ MEMORY.md 索引

## 6. 不在范围

- Guid PK Phase 2 本身(见 §5,独立切片)
- `migration/baseline/regenerate_for_guid.py` 的删除(Phase 2 完成时再评估)
- specs/plans/migration 历史文档的 Python 字样清理
- .NET 代码注释清理
- `backend/data/` 的删除或归档(用户保留)

## 7. 风险

| 风险 | 缓解 |
|---|---|
| README 配置表改写与 .NET 实际 env 名不符 | 实施时以 `docker-compose.yml` + `OnToPilotOptions`/`Program.cs` 为准核对,不以本文 §2.3 为准(§2.3 标注了"实施时核对"的行) |
| dev proxy 改 5072 后用户 dev 流程变动 | launchSettings 端口是 .NET 默认 dev 端口;`.env` 覆盖机制不变 |
| 删错文件 | tag + git history;`git rm` 只作用于 tracked 文件,data/ 不受影响 |
| README.zh-CN 与英文版不同步 | 同一切片内完成,逐处对照 |

## 8. 实施切片

单 commit 原子退役:

```text
chore(retire): remove Python backend, complete .NET migration

- git rm -r backend/ (tracked files only; backend/data/ stays on disk)
- README.md + README.zh-CN.md: Python-era sections rewritten for .NET
- docs/architecture.md: FastAPI -> ASP.NET Core
- frontend dev proxy default 8000 -> 5072 (.env.example + vite.config.ts + README)
- docs/acceptance.md: Python commands -> dotnet
- tag pre-python-retirement on the parent commit

Co-Authored-By: Claude <noreply@anthropic.com>
```

执行方式(与 RBAC 切片一致):SDD 单 task(删除型机械清理,单 implementer + task review + final review)。
