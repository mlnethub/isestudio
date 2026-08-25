# OnToPilot → ISEStudio brand rename(2026-08-25)

## 1. 背景

2026-08-25 Python 后端完整退役后(见 [[ontopilot-python-retirement]]),dotnet 后端 wire-side 已 100% .NET,产品主体无 Python 痕迹。本切片是 brand rename —— 把"OnToPilot"产品名替换为"ISEStudio",覆盖全栈(code namespaces、projects、env vars、cookie、DB、Docker、frontend、docs)。

本切片与同期 [[ontopilot-iri-phase1]](IRI Phase 1 工具链就位)、[[ontopilot-p3-4-139-expected-iri-verify-sha]](cutover SHA-chain)是姊妹切片。IRI base namespace(`http://goodcrew.local/`)代码侧已经完成切换(`OnToPilotOptions.IriRoot`/`VocabNamespace` 默认值),本 spec 仅做 sanity-check 与 docs 同步,不做 IRI 重命名工作。

### 1.1 动机

| 维度 | 现状 | 目标 |
|---|---|---|
| 产品名 | OnToPilot(ontology pilot / 8 月以来 doc/cookie/env var 都用) | ISEStudio(brand 升级,语义"intelligent semantic enterprise studio") |
| 代码命名空间 | `OnToPilot.{Configuration,Api,Application,...}` | `ISEStudio.{Configuration,Api,Application,...}` |
| 解决方案 | `src/OnToPilot.sln` + 7 个 `OnToPilot*.csproj` | `src/ISEStudio.sln` + 7 个 `ISEStudio*.csproj` |
| 配置层 | `OnToPilot` section + `OnToPilot__*` env vars | `ISEStudio` section + `ISEStudio__*` env vars |
| 用户会话 | `ontopilot_session` cookie + DB name `ontopilot` | `isestudio_session` cookie + DB name `isestudio` |
| IRI 数据身份 | `http://goodcrew.local/`(代码默认) | **保留** `http://goodcrew.local/`(本次不动) |

## 2. 范围

### 2.1 IN(本次 rename 触及)

- **.NET 解决方案与项目**:`src/OnToPilot.sln` → `src/ISEStudio.sln`;7 个 `.csproj` + folder + RootNamespace + AssemblyName
- **.NET 命名空间**:全部 `namespace OnToPilot.*` 声明 + 所有 `using OnToPilot.*` 引用
- **配置层**:`OnToPilotOptions.SectionName` + 全部 `OnToPilot__*` env var
- **运行时**:`OnToPilotOptions.SessionCookie` 默认值;docker-compose 服务名 + DB name;MinIO bucket;Docker image tag;frontend package name
- **用户文档**:顶层 README、README.zh-CN、`docs/architecture.md`、`docs/acceptance.md`、`docs/external-api*.md`、`docs/rdf-import*.md`、`docs/release-and-export.md`、`NOTICE`、`.env.example`、`frontend/README.md`、`.claude/`
- **CI/CD**:`.github/workflows/ci.yml` 中的 service 名 + image tag + 路径引用
- **Docker / Dockerfile label**:`Dockerfile`、`src/Dockerfile`、`frontend/Dockerfile`
- **图片资源**:`docs/images/ontopilot-hero-title.webp` + `docs/images/ontopilot-web-demo.png`(重命名文件名 + docs 引用同步)
- **IRI sanity grep gate**:验证 `OnToPilot__IriRoot` / `OnToPilot__VocabNamespace` 在 `src/` 内 0 命中(env var 名前缀须对齐 `ISEStudio__`,IRI 值本身保留 `goodcrew.local`)

### 2.2 OUT(本次 rename 不触及)

| 范围 | 不动原因 |
|---|---|
| **IRI base namespace 代码层**(`http://goodcrew.local/...`) | 已是当前默认(`OnToPilotOptions.cs:161,173` 默认值 + `appsettings.json:13-14` + `OnToPilotOptionsTests.cs:19,27` 断言 + `Assert.DoesNotContain("ontopilot.local")` 防御) |
| `migration/scripts/gates/CutoverGates.ps1` `-FromPrefix` 默认值 `http://ontopilot.local/` | **故意保留**:用户生产数据可能有 pre-rename 时代的 `ontopilot.local/` IRI,需要 cutover 工具帮他们迁到 `goodcrew.local/` |
| `migration/runbooks/iri-migration-runbook.md` | 工具 runbook,描述 `ontopilot.local → goodcrew.local` 迁移路径,价值随时间增长 |
| `migration/runbooks/production-cutover-record.template.md` | cutover record 模板,记录 `-expected-iri-from-prefix` + `-expected-iri-to-prefix` 字段 |
| `migrations/SqlAlchemyToEfCore/*.sql` | 历史 SQL 迁移脚本(描述 schema 演进,与品牌无关) |
| `docs/superpowers/specs/2026-08-13-ontopilot-dotnet-migration-design.md` 等 16 份 spec/plan 文件名 | spec/plan 文件名 + 内容中"OntoPilot"是历史叙事;重命名会破坏 git log 链接与外部引用 |
| `docs/migration/MIGRATION_REPORT.md` 等 4 份 | 迁移档案(spec §2.2 同 Python 退役分类) |
| `migration/scripts/*.ps1` 内部变量名(`$OntoPilotDb` 等) | 工具内部命名,与代码 namespace 解耦;改写增加 risk 0 value |
| 测试 fixture 中的 `BaseIri = "http://ontopilot.test/..."` | 在范围内:测试隔离域虽是 fake,但 brand 一致性要求改为 `isestudio.test` |
| GitHub repo name(`e--GitHub-ontopilot`) | 仓库设置层面,需 follow-up 切片单独处理 |

## 3. 决策汇总

| 维度 | 决策 | 理由 |
|---|---|---|
| Brand scope | **C. Full stack**(全栈 rename) | 产品代码与 brand 必须 1:1,避免"代码是 OnToPilot 但 README 是 ISEStudio"的不一致状态 |
| Naming convention | **PascalCase 1:1** | 跟 OnToPilot 现有节奏一致(.NET namespace PascalCase,env var PascalCase,DB/image 小写,frontend package 小写) |
| Cutover strategy | **A. Hard cutover**(0 aliases) | 用户已部署的 .NET 实例必须更新 env vars;不保留别名表,代码库保持干净 |
| IRI namespace | **保留** `http://goodcrew.local/`(代码已对齐) | IRI 是数据身份契约,不是品牌标识;Python 退役都没动 IRI schema(ADR §41 spirit) |
| 历史 spec/plan 文件名 | **保留** | spec/plan 文件名 + 内容中"OntoPilot"是历史叙事;重命名会破坏 git log 链接 |

## 4. 架构:2-stage atomic rename

参考 [[ontopilot-python-retirement]] 的 2-commit 模式(docs commit + deletion commit,tag 指向 docs commit parent),本次 rename 用 2 stage,每个 stage **一个原子 commit**,中间状态 build 必须 green。

### 4.1 Stage 1: Brand/runtime surface(commit `chore(rename): brand surface to ISEStudio`)

| 子任务 | 文件数估算 |
|---|---|
| 顶层 README + 架构 / acceptance / external-api / rdf-import docs | ~10 files |
| `docs/release-and-export.md`, `NOTICE`, `.env.example`, `LICENSE`(若含 brand) | ~5 |
| `docker-compose.yml` + `src/Dockerfile` + `src/.dockerignore` + `frontend/Dockerfile` | ~4 |
| Frontend `package.json` + `vite.config.ts` + `index.html` + `playwright.config.ts` + `frontend/README.md` | ~5 |
| `.github/workflows/ci.yml` | ~1 |
| `.claude/settings.json` + `.claude/launch.json` | ~2 |
| `src/.env.example` | ~1 |
| `src/OnToPilot/appsettings.json`(仅改 `OnToPilot` section 名为 `ISEStudio`) | ~1 |
| `src/OnToPilot/Configuration/OnToPilotOptions.cs`(改 `SessionCookie` 默认值 + `SectionName` 常量) | ~1 |
| 图片 `docs/images/ontopilot-hero-title.webp` → `docs/images/isestudio-hero-title.webp`(+ 引用更新) | ~2 files + 多处引用 |
| 测试 fixtures 中 `BaseIri = "http://ontopilot.test/..."` → `"http://isestudio.test/..."`(测试隔离域,非生产) | ~5 files |
| **Stage 1 总文件数估算** | ~80-100 files |

### 4.2 Stage 2: .NET solution + code(commit `refactor(rename): OnToPilot namespaces + projects to ISEStudio`)

| 子任务 | 文件数估算 |
|---|---|
| `src/OnToPilot.sln` → `src/ISEStudio.sln`(git mv) | 1 file + sln 内部 `.csproj` 路径同步 |
| 7 个 `OnToPilot*.csproj` → `ISEStudio*.csproj`(git mv) | 7 files + `Directory.Build.props` + 各 csproj 内 `<AssemblyName>` / `<RootNamespace>` / `<ProjectReference>` 路径 |
| 7 个 project folder rename:`src/OnToPilot/`, `src/OnToPilot.Application/`, `src/OnToPilot.Migration/`, `src/OnToPilot.Tests/`, `src/OnToPilot.IntegrationTests/`, `src/OnToPilot.ApiContract.Tests/`, `src/OnToPilot.OxigraphProbe/` | 7 folder rename |
| `.cs` 文件内 `namespace OnToPilot.*` 声明(`namespace OnToPilot.Configuration;` 等) | ~250 files |
| `.cs` 文件内 `using OnToPilot.*;` 引用 | ~250 files |
| `src/OnToPilot/Properties/launchSettings.json` 路径引用 | 1 file |
| `.sln` 文件内 `.csproj` 路径 + `Project(...)` GUID 引用 | 1 file |
| **Stage 2 总文件数估算** | ~250-300 files |

### 4.3 Tag

`pre-isestudio-rename` → Stage 1 之前的 commit(parent of rename),作为恢复快照入口。

### 4.4 中间状态

Stage 1 完成后,顶层 docs / Docker / CI 全部 ISEStudio,但代码 namespace 仍 OnToPilot(不一致)。Stage 2 完成后,全部统一 ISEStudio。两个 stage 之间 grep 会看到混合状态——**故意允许**,只要 `dotnet build` 与 `dotnet test` green。

## 5. Layer mapping(每个 layer 的具体替换)

| Layer | 旧 | 新 | 备注 |
|---|---|---|---|
| .NET solution | `OnToPilot.sln` | `ISEStudio.sln` | |
| .NET projects(7) | `OnToPilot{,Application,Migration,Tests,IntegrationTests,ApiContract.Tests,OxigraphProbe}` | `ISEStudio{,Application,Migration,Tests,IntegrationTests,ApiContract.Tests,OxigraphProbe}` | csproj + folder + RootNamespace + AssemblyName |
| .NET namespaces | `OnToPilot.{Configuration,Api,Application,Migration,Tests,IntegrationTests,ApiContract.Tests,OxigraphProbe}` | `ISEStudio.*`(1:1) | `namespace` 声明 + `using` 引用 |
| Config section | `"OnToPilot"`(`OnToPilotOptions.SectionName`) | `"ISEStudio"` | ASP.NET Core config binding |
| Env var prefix | `OnToPilot__*` | `ISEStudio__*` | 全 .env / docker-compose / docs |
| `OnToPilot__IriRoot` / `OnToPilot__VocabNamespace` | env var 名:`OnToPilot__IriRoot` → `ISEStudio__IriRoot`;env var 值:`http://goodcrew.local/ks` 保留(IRI 命名空间决策) | env var 名随 prefix 重命名;env var 值保留 goodcrew.local 不变 | IRI sanity grep gate 验证 0 个 `OnToPilot__IriRoot`/`OnToPilot__VocabNamespace` 引用残留 |
| Cookie default | `"ontopilot_session"` | `"isestudio_session"` | `OnToPilotOptions.SessionCookie` 默认值 |
| DB name | `ontopilot` | `isestudio` | docker-compose `POSTGRES_DB` |
| MinIO bucket | `ontopilot-blobs` | `isestudio-blobs` | `OnToPilot__Storage__Bucket` 默认 |
| Docker compose service | `backend`, `migrate`, `seed-admin` | `isestudio`, `isestudio-migrate`, `isestudio-seed-admin` | compose services + depends_on |
| Docker image | `ontopilot` | `isestudio` | Dockerfile label + compose image |
| Frontend package | `name: "ontopilot"` | `name: "isestudio"` | `package.json` |
| Frontend page title | `<title>OntoPilot</title>` | `<title>ISEStudio</title>` | `index.html` |
| 顶层 README/docs | "OntoPilot" | "ISEStudio" | ~80 files |
| `ONTOPILOT_BIND_ADDRESS` | `ONTOPILOT_*` shell env | `ISESTUDIO_*` | compose-interpolated vars |
| 图片资源 | `docs/images/ontopilot-{hero-title,web-demo}.{webp,png}` | `docs/images/isestudio-{hero-title,web-demo}.{webp,png}` | 二进制 rename + 引用更新 |

## 6. 不动(豁免)(同 §2.2)

| 范围 | 原因 |
|---|---|
| IRI namespace 代码层 | 已是当前默认 |
| `CutoverGates.ps1` `-FromPrefix ontopilot.local` | 工具迁移入口 |
| `iri-migration-runbook.md` | 工具 runbook |
| `production-cutover-record.template.md` | cutover 记录模板 |
| `migrations/SqlAlchemyToEfCore/*.sql` | 历史 SQL 迁移脚本 |
| 16 份 spec/plan 文件名 + 内容 brand 引用(内容里"OntoPilot"也保留) | 历史叙事,Python 退役切片(spec §2.2)同 pattern,保持历史档案不动 |
| `docs/migration/*.md` | 迁移档案 |
| `migration/scripts/*.ps1` 内部变量名 | 工具内部命名 |
| 测试 fixture `BaseIri = "http://ontopilot.test/..."` | 测试隔离域(改为 isestudio.test 算 rename 一部分) |
| GitHub repo name | 仓库设置,follow-up 切片 |

## 7. 验证(7 项 gate)

1. **`git grep -inE "OnToPilot|OntoPilot|ontopilot|ONTO_PILOT" -- src/ frontend/ docker-compose.yml .github/ src/.env.example .env.example docs/release-and-export.md NOTICE LICENSE frontend/README.md`** → **0 hits**(豁免: §2.2 列出的范围)
2. **IRI sanity**:`git grep "OnToPilot__IriRoot|OnToPilot__VocabNamespace" -- src/` → 0 hits(IRI 已用 `goodcrew.local`,env var 前缀需对齐 ISEStudio)
3. **`dotnet build src/ISEStudio.sln -c Release`** → exit 0(0 warning)
4. **测试**:858 unit + 167 contract + 63 integration 全绿;`OnToPilotOptionsTests` 防御性 `Assert.DoesNotContain("ontopilot.local")` 仍通过
5. **`docker compose config --quiet`** → exit 0
6. **`migration/scripts/gates/CutoverGates.ps1` `-ToPrefix` 默认值仍 `http://goodcrew.local/`**(cutover 目标对齐,源码不变)
7. **`git status` clean**(除 `backend/data/` untracked,gitignore 覆盖)

## 8. 实施方法:SDD

参考 [[ontopilot-python-retirement]] 的 SDD 流程,具体配置:

| 角色 | 模型 | 任务 |
|---|---|---|
| Stage 1 implementer | sonnet | brand surface rename(~80-100 files)+ IRI sanity gate |
| Stage 2 implementer | sonnet | .NET namespaces + csproj + sln(~250-300 .cs files)+ build 验证 |
| Task reviewer ×2 | sonnet | spec compliance + code quality |
| Final reviewer | **opus** | 整分支 review(high-risk,rename 切片历史经验) |
| Fix subagent | sonnet | 收尾 findings |
| Scoped re-review | sonnet | 修后核对 |

### 8.1 SDD 控制器裁决预登记

- **Ruling**:Stage 1 + Stage 2 必须是 2 个原子 commit,不可拆分为 3+ 个 commit(sln+csproj+namespace 必须在同一 commit 内完成,否则中间状态 build break)。
- **Ruling**:Tag `pre-isestudio-rename` 指向 Stage 1 之前的 commit(parent of rename),不指向任何 stage commit 本身。
- **Ruling**:build / test / grep gate 全部必须在 final review 前跑过,final reviewer 看到的是已 green 的分支。
- **Ruling**:本次 review 的"spec compliance"含义是"layer mapping §5 表中的每一行都得到执行",final reviewer 抽样审计。

## 9. 风险与缓解

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| 大 diff 难 review(单 stage 200+ files) | 高 | review surface 巨大,漏看 | 2 stage 拆分 + final review opus + 关键 review 集中在 namespace 引用完整性 + build/test gate |
| 漏 rename 路径(CSP/CORS/audit/MCP/JSON 模板/secret 名/cookie path/cookie domain) | 中 | 运行时静默失败 | grep gate 1 + final review Critical 区 |
| 大小写敏感替换陷阱(`OnToPilot`/`OntoPilot`/`ontopilot`/`ONTO_PILOT` 四种形态 + 图片文件名 `ontopilot-*.webp`) | 高 | 漏 rename 残留 | sed/脚本四形式分别替换 + 图片同步重命名 + 引用更新 |
| Docker image cache 污染 | 低 | 部署用旧 image | 改 image tag 后 `docker compose build --pull --no-cache` |
| 已存在部署 env vars 未更新 → 启动失败 | 高 | 用户部署 break | README + `.env.example` + release notes 同步更新 + 醒目标注 BREAKING |
| 图片二进制 rename → docs 引用 break | 低 | 文档图片失效 | 替换引用 + 同名重命名 + 引用方 docs 同步 |
| `OnToPilot.sln` → `ISEStudio.sln` 但 sln 内容里的 `.csproj` 路径也要同步 | 高 | build break | git mv 后一次性 sed 处理 sln 内部所有 `OnToPilot*.csproj` 引用 |
| `OnToPilotOptions.SectionName` 改名 → 部署 config 文件失效 | 高 | 配置加载失败 | README + `appsettings.json` + `appsettings.Production.json` 同步更新 |
| 测试 fixture `BaseIri` 路径 rename → 已发布 release data IRIs 错位 | 中 | 测试回归 | fixture 改用 `isestudio.test` 是测试隔离域,生产 IRI base 仍 `goodcrew.local`,逻辑分离 |
| 已有用户的 session cookie(`ontopilot_session`)失效 → 重新登录 | 高 | 用户体验影响 | Hard cutover 接受(决策 §3 已定),release notes 说明 |

## 10. 不在范围(spec §6)

- GitHub repo name rename(仓库设置层面,需 follow-up 切片)
- 已发布 release data 重写(immutable,IRI 命名空间不变)
- 第三方镜像内嵌代码(`docs/benchmarks/ontolearner-*.md` 引用外部 OntoLearner 工具)
- 历史 spec/plan 文件名重命名
- IRI base namespace 改动(代码侧已是 `goodcrew.local`)
- `migration/scripts/*.ps1` 内部变量名重命名
- 跨阶段 cutover 演练(运维范畴,独立切片)

## 11. 解锁

本切片完成后:
- 产品 brand = ISEStudio(对外一致)
- .NET 命名空间 = `ISEStudio.*`(与 brand 对齐)
- IRI 数据身份 = `goodcrew.local/`(已对齐,与 brand 解耦)
- Python parity baseline = 已删除(见 [[ontopilot-python-retirement]])
- **Guid PK Phase 2** = 已解锁(见 [[ontopilot-adr-gap-2026-08-23]] D 组)

## 12. 关联 spec / ADR

- 上游:
  - [[ontopilot-python-retirement]] — Python 退役切片(brand rename 前置:无 Python 残留)
  - [[ontopilot-iri-phase1]] — IRI Phase 1 工具链就位
  - [[ontopilot-p3-4-139-expected-iri-verify-sha]] — cutover SHA-chain
  - [[ontopilot-guid-migration-complete]] — Guid PK 主迁移
  - [[ontopilot-rbac-coverage-matrix]] — RBAC 覆盖矩阵
  - [[ontopilot-dotnet-gap-2026-08-23]] — 缺口回归核查表
- ADR §41 — IRI schema 不动约束
- ADR §42 — Python baseline 不动约束(已废止,见 [[ontopilot-python-retirement]])
- ADR §589 — OpenAPI 自动生成评估(brand rename 完成后可推进)
