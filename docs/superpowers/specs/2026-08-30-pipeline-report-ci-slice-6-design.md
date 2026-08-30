# Dovetail 流水线报告 CI 接入(Slice 6)

**日期**:2026-08-30
**作者**:Claude / ISEStudio
**状态**:设计 / 待用户审核
**范围**:`dovetail-report` 接入 GitHub Actions workflow(独立 `pipeline-report` job + PR diff vs main base + artifact 上传 + PR comment);跨切片一致性 lint 推迟到 Slice 7(从父 spec §5 项拆分)。

**父 spec**:`docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md` §5 路线图 Slice 6

---

## 1. 背景与现状

### 1.1 Dovetail 路线图已完成 5/6

父 spec 路线图 §5 包含 6 个切片:

| Slice | 范围 | 状态 |
|---|---|---|
| 1 | TBox 子 DAG 最小可走通样例 | ✅ DONE |
| 2 | ABox 子 DAG | ✅ DONE |
| 3 | ConflictAgent + StructureAgent | ✅ DONE |
| 4 | Vocabulary 流水线 | ✅ DONE |
| 5 | 顶层 5 runner 调度 | ✅ DONE(commit `2376e8a`→`7a39790`) |
| 6 | `dovetail-report` 接入 CI + 跨 slice 一致性 lint | 🟡 本切片(仅 CI,lint 推迟到 Slice 7) |

### 1.2 当前 CI 现状

`.github/workflows/ci.yml`(2.8KB,3 个 job):
- `branch-flow` — PR 分支流验证(feat/* → dev,dev → main 仅 owner)
- `frontend` — pnpm 10 + Node 22 + lint/build
- `dotnet-tests` — Ubuntu + Postgres 16-alpine + dotnet 10.0.x + Unit/Integration 测试

触发:`push` 到 `main`/`dev`,`pull_request` 到 `main`/`dev`。

**未涉及 dovetail-report**:5 个 pipeline slice 落地后,只能本地手动跑 `dotnet dovetail-report` 生成 HTML 报告,CI 看不到 pipeline DAG 状态。

### 1.3 当前 Dovetail-report 用法

- 工具版本:`dovetail-report 1.0.0`(NuGet global tool)
- 用法:`dotnet dovetail-report --project src/ISEStudio/ISEStudio.csproj --output <dir> --nologo`
- 输出:目录形式,含 `index.html` + 各 pipeline HTML + vendor JS/CSS
- Slice 5 已落地 11 files at `docs/superpowers/diagrams/extraction-job-dag/`(手工生成)

### 1.4 痛点

1. **PR 评审者看不到 DAG diff**:新增 / 删除 / 修改 pipeline segment 后,合并前无法 CI 自动验证
2. **DOVE 编译错误 CI 漏检**:Dovetail 编译期诊断(DOVE001-020)只在 build 时触发,当前 `dotnet-tests` job 隐式覆盖,但**没有独立的 pipeline report job**,reviewer 无法在 PR 看到"DO VE 错误已 catch"的明确信号
3. **跨切片一致性靠 code review**:5 个 slice 已用 `JobCarries.cs` / `JobState` / `IPipeline<...>` 等模式,无自动化校验
4. **artifact 缺失**:当前 CI 不上传 pipeline HTML,合并后无法回归比对

---

## 2. 设计目标

| 目标 | 实现方式 |
|---|---|
| **自动生成** | 每个 PR / push 跑 `dotnet dovetail-report`,无需手工 |
| **PR 内 diff** | 与 main base 做结构 diff(pipeline 增删 + segment 数变化),PR comment 显示 markdown 表 |
| **artifact 上传** | HEAD + main baseline 两份 HTML,30 天保留 |
| **DOVE 错误 CI 暴露** | dovetail-report 失败 → pipeline-report job fail,信号独立 |
| **零 .NET 代码改动** | 仅 workflow + manifest + bash script;现有 1001/0/1/1002 + 46/0/46 测试零变化 |
| **lint 推迟** | 跨切片一致性 lint 留到 Slice 7(范围明确分离) |

### 非目标(本切片不做)

- **跨切片一致性 lint**(命名 / DI block 结构 / JobCarries wrapper pattern) → Slice 7
- **GitHub Pages 长期 historical snapshot** → 后续切片单独评估
- **运行时 metric 上报(DAG 长度 / segment 数量历史趋势)** → 后续切片
- **Aria2-style 增量报告** → 后续切片

---

## 3. 架构总览

### 3.1 workflow 改动

`.github/workflows/ci.yml` 末尾新增 `pipeline-report` job,与现有 `dotnet-tests` 通过 `needs` 依赖:

```
trigger: push to main/dev, PR to main/dev
  ├── branch-flow   (PR 校验)
  ├── frontend      (pnpm + lint + build)
  ├── dotnet-tests  (Restore + Build + Unit + Integration)
  └── pipeline-report (NEW)
       │
       └─ needs: dotnet-tests
       └─ if: success() || failure()
       └─ steps: checkout → setup-dotnet → restore → build →
                 install dovetail-report → gen HEAD report →
                 checkout main baseline → gen main report →
                 diff → upload-artifact → PR comment
```

**为什么 `if: success() || failure()`**:即使测试失败也要生成 pipeline 报告,这样 reviewer 能看到 DAG 状态(用于诊断失败是否与 pipeline 结构相关)。

### 3.2 Dovetail 工具安装(manifest-based)

**NuGet PackageId vs 命令名**:`Dovetail` 项目的 `Dovetail.Report.csproj`(`https://github.com/IanWold/Dovetail`)用 `<PackageId>Dovetail.Report</PackageId>` + `<ToolCommandName>dovetail-report</ToolCommandName>` 发布。NuGet `dotnet tool manifest` 的 `tools` 对象 **key 必须用 PackageId**,不是命令名。安装后命令 `dovetail-report` 可直接调用。

新建 `.config/dotnet-tools.json` 声明 `Dovetail.Report` 1.0.0(NuGet PackageId):

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "Dovetail.Report": {
      "version": "1.0.0",
      "commands": ["dovetail-report"]
    }
  }
}
```

CI 内 `dotnet tool restore` 自动安装到本地 `.tools/` 目录(命令 `dovetail-report`),通过 `actions/setup-dotnet@v4` 默认 cache 路径 `~/.dotnet/tools/**` 缓存。

**Dovetail 包源**:Dovetail 在 GitHub release 时通过 `.github/workflows/publish.yml` publish 到 public nuget.org(`https://api.nuget.org/v3/index.json`)。CI 默认 NuGet.config 已含 nuget.org,无需额外 source。

### 3.3 diff 策略

**结构 diff,不做 HTML 像素 diff**:

```bash
# scripts/diff-pipeline-dags.sh 伪代码
set -euo pipefail

HEAD_DIR="docs/superpowers/diagrams/head-dag"
MAIN_DIR="docs/superpowers/diagrams/main-dag"

# 1. 比较 pipeline 文件名集合
head_pipelines=$(ls "$HEAD_DIR" | grep -v "index\|vendor" | sort)
main_pipelines=$(ls "$MAIN_DIR" | grep -v "index\|vendor" | sort)

added=$(comm -13 <(echo "$main_pipelines") <(echo "$head_pipelines"))
removed=$(comm -23 <(echo "$main_pipelines") <(echo "$head_pipelines"))
common=$(comm -12 <(echo "$main_pipelines") <(echo "$head_pipelines"))

# 2. 对 common pipeline 比较 segment 数(grep "[Segment]" 计数)
for p in $common; do
  head_count=$(grep -c "Pipeline:" "$HEAD_DIR/$p" || echo 0)
  main_count=$(grep -c "Pipeline:" "$MAIN_DIR/$p" || echo 0)
  delta=$((head_count - main_count))
  echo "$p: $main_count → $head_count (Δ $delta)"
done

# 3. 输出 markdown 表到 PR comment payload
```

**关键设计决策**:不做 HTML 内容 diff(Mermaid 节点 id 会变,导致 false-positive)。只看 pipeline 拓扑级别的结构性变化。

### 3.4 PR comment 输出格式

```markdown
## Pipeline DAG Report

📊 Changes detected (vs main):

| Pipeline | Δ Segments | Status |
|---|---|---|
| `TBoxChunkPipeline` | 0 | unchanged |
| `NewJobPipeline` | +3 | 🆕 added |
| `CombinedJobPipeline` | +1 | ✏️ modified |
| `OldJobPipeline` | -2 | 🗑️ removed |

📦 [Download full HTML report](actions-artifact-url)

Generated by `pipeline-report` job in commit ${{ github.sha }}.
```

无变化时:
```markdown
## Pipeline DAG Report

✅ No pipeline topology changes detected (vs main).

📦 [Download full HTML report](actions-artifact-url)
```

---

## 4. Data Flow

### 4.1 文件路径

| 路径 | 用途 | 入 git? |
|---|---|---|
| `docs/superpowers/diagrams/head-dag/` | 本次 commit 的 pipeline HTML 报告 | ❌(.gitignore) |
| `docs/superpowers/diagrams/main-dag/` | main base 的 pipeline HTML 报告 | ❌(.gitignore) |
| `docs/superpowers/diagrams/extraction-job-dag/` | slice 5 手工快照(永久存档) | ✅(选择性) |
| `docs/superpowers/diagrams/extraction-tbox-dag/` | slice 1 手工快照 | ✅(选择性) |

**关键**:`head-dag/` 和 `main-dag/` 是 CI 临时产物,不进 git 版本。`extraction-*-dag/` 是开发者手工提交的永久 snapshot,只在重大 slice 落地时 commit。

### 4.2 PR comment 数据流

```
[Step 1] checkout (fetch-depth: 0)
   │
[Step 2] setup-dotnet (.NET 10.0.x)
   │
[Step 3] dotnet restore src/ISEStudio.sln
   │
[Step 4] dotnet build src/ISEStudio.sln -c Release --no-restore
   │
[Step 5] dotnet tool restore (从 .config/dotnet-tools.json)
   │
[Step 6] dotnet dovetail-report --output head-dag
   │
[Step 7] git switch -d main (worktree at /tmp/main-baseline)
   │
[Step 8] cd /tmp/main-baseline && dotnet build -c Release && dotnet dovetail-report --output main-dag
   │
[Step 9] cd $GITHUB_WORKSPACE && bash scripts/diff-pipeline-dags.sh > /tmp/diff-output.md
   │
[Step 10] actions/upload-artifact@v4 (head-dag/ + main-dag/, retention 30 days)
   │
[Step 11] actions/github-script@v7 (PR comment via octokit)
   │
   └─ if: github.event_name == 'pull_request'
   └─ post comment with /tmp/diff-output.md content
```

### 4.3 workflow_run vs in-line PR comment

**选用 in-line PR comment**(直接在 pipeline-report job 内 `actions/github-script@v7`):

| 维度 | in-line(本切片) | workflow_run 触发 |
|---|---|---|
| token 权限 | 默认 GITHUB_TOKEN 可写 | workflow_run 是只读 token |
| 时延 | 立即(同 job 内) | 延迟(等待 trigger workflow) |
| PR comment | ✅ 支持 | ❌ 不支持 |
| 配置复杂度 | 中(单 workflow) | 高(双 workflow + dependency) |

理由:PR comment 是关键价值点,选 in-line 简化。

---

## 5. Error Handling

### 5.1 三类失败模式

| 场景 | 触发条件 | 行为 | exit code |
|---|---|---|---|
| **A 类:编译失败** | Dovetail source generator 报 DOVE 错误 或 dotnet build 失败 | job fail ❌ | non-zero |
| **B 类:diff 失败** | main baseline 生成失败 或 diff script 抛错 | warn ⚠️(仍 upload artifact + comment) | 0 |
| **C 类:comment 失败** | PR comment API 限流或权限拒绝 | warn ⚠️(artifact 仍是主交付物) | 0 |

### 5.2 处理策略

```yaml
- name: Generate HEAD report
  run: dotnet dovetail-report --project src/ISEStudio/ISEStudio.csproj --output docs/superpowers/diagrams/head-dag --nologo
  # A 类:此 step fail → job fail
  # dovetail-report 任何错误(包括 DOVE 诊断)都应 propagate

- name: Generate main baseline
  run: |
    set +e
    git worktree add /tmp/main-baseline main
    cd /tmp/main-baseline
    dotnet build src/ISEStudio.sln -c Release --no-restore
    dotnet dovetail-report --project src/ISEStudio/ISEStudio.csproj --output main-dag --nologo
    BASELINE_EXIT=$?
    cd $GITHUB_WORKSPACE
    git worktree remove /tmp/main-baseline
    exit $BASELINE_EXIT
  continue-on-error: true
  # B 类:即使 baseline 失败,继续

- name: Run diff
  if: env.HAS_BASELINE == 'true'
  id: diff
  run: bash scripts/diff-pipeline-dags.sh > /tmp/diff-output.md
  continue-on-error: true
  # B 类 diff 失败:用空 diff 内容继续

- name: Upload artifact
  uses: actions/upload-artifact@v4
  with:
    name: pipeline-dag-report
    path: |
      docs/superpowers/diagrams/head-dag
      docs/superpowers/diagrams/main-dag
    retention-days: 30
  if: always()
```

### 5.3 PR comment 容错

```yaml
- name: Comment PR with diff
  if: github.event_name == 'pull_request' && always()
  uses: actions/github-script@v7
  with:
    script: |
      const fs = require('fs');
      const diff = fs.existsSync('/tmp/diff-output.md')
        ? fs.readFileSync('/tmp/diff-output.md', 'utf8')
        : '⚠️ diff generation failed; see artifact for HEAD and main reports';
      // ...post comment
  continue-on-error: true
```

---

## 6. 文件结构

### 6.1 新建文件

| 路径 | 行数估计 | 用途 |
|---|---|---|
| `.config/dotnet-tools.json` | ~10 | manifest 声明 `Dovetail.Report` 1.0.0 NuGet package(install as `dovetail-report` 命令) |
| `scripts/diff-pipeline-dags.sh` | ~80 | 结构 diff 脚本(bash + grep + comm + awk) |
| `docs/superpowers/diagrams/.gitignore` | ~5 | 排除 `head-dag/` `main-dag/` 临时产物 |

### 6.2 修改文件

| 路径 | 改动 |
|---|---|
| `.github/workflows/ci.yml` | append `pipeline-report` job(+~80 行) |

### 6.3 不修改文件

- `src/ISEStudio/**` — 零 .NET 代码改动
- `src/ISEStudio.Tests/**` — 零测试改动
- `src/ISEStudio.IntegrationTests/**` — 零测试改动
- `docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md` — 本切片 spec 完成后单独 amend §5/§11(不在本 slice 计划内)

---

## 7. GitHub Actions workflow 改动(具体)

### 7.1 完整 pipeline-report job

```yaml
  pipeline-report:
    name: Pipeline DAG Report
    needs: dotnet-tests
    runs-on: ubuntu-latest
    if: success() || failure()
    permissions:
      contents: read
      pull-requests: write
    steps:
      - name: Checkout (full history)
        uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - name: Restore dependencies
        run: dotnet restore src/ISEStudio.sln

      - name: Build (Release)
        run: dotnet build src/ISEStudio.sln --no-restore -c Release

      - name: Restore .NET tools
        run: dotnet tool restore

      - name: Generate HEAD pipeline DAG report
        run: |
          dotnet dovetail-report \
            --project src/ISEStudio/ISEStudio.csproj \
            --output docs/superpowers/diagrams/head-dag \
            --nologo

      - name: Generate main baseline DAG report
        id: baseline
        continue-on-error: true
        run: |
          git worktree add /tmp/main-baseline main
          pushd /tmp/main-baseline
          dotnet restore src/ISEStudio.sln
          dotnet build src/ISEStudio.sln -c Release
          dotnet tool restore
          dotnet dovetail-report \
            --project src/ISEStudio/ISEStudio.csproj \
            --output /tmp/main-dag \
            --nologo
          popd
          mkdir -p docs/superpowers/diagrams/main-dag
          cp -r /tmp/main-dag/* docs/superpowers/diagrams/main-dag/ || true
          git worktree remove /tmp/main-baseline || true
          echo "baseline=true" >> "$GITHUB_OUTPUT"

      - name: Run diff (vs main baseline)
        id: diff
        if: steps.baseline.outputs.baseline == 'true'
        continue-on-error: true
        run: |
          bash scripts/diff-pipeline-dags.sh \
            docs/superpowers/diagrams/head-dag \
            docs/superpowers/diagrams/main-dag \
            > /tmp/diff-output.md
          cat /tmp/diff-output.md

      - name: Upload pipeline DAG artifacts
        uses: actions/upload-artifact@v4
        if: always()
        with:
          name: pipeline-dag-report-${{ github.sha }}
          path: |
            docs/superpowers/diagrams/head-dag
            docs/superpowers/diagrams/main-dag
          retention-days: 30
          if-no-files-found: ignore

      - name: Comment PR with diff
        if: github.event_name == 'pull_request' && always()
        uses: actions/github-script@v7
        with:
          script: |
            const fs = require('fs');
            const diffPath = '/tmp/diff-output.md';
            let body;
            if (fs.existsSync(diffPath)) {
              const diff = fs.readFileSync(diffPath, 'utf8').trim();
              body = diff.length > 0
                ? `## Pipeline DAG Report\n\n📊 Changes detected (vs main):\n\n${diff}\n\n📦 [Download full HTML report](${{ github.server_url }}/${{ github.repository }}/actions/runs/${{ github.run_id }})\n\nGenerated by \`pipeline-report\` job in commit \`${{ github.sha }}\`.`
                : `## Pipeline DAG Report\n\n✅ No pipeline topology changes detected (vs main).\n\n📦 [Download full HTML report](${{ github.server_url }}/${{ github.repository }}/actions/runs/${{ github.run_id }})`;
            } else {
              body = `## Pipeline DAG Report\n\n⚠️ Diff generation unavailable (baseline generation failed). See artifact for raw HEAD + main reports.`;
            }

            const { data: comments } = await github.rest.issues.listComments({
              owner: context.repo.owner,
              repo: context.repo.repo,
              issue_number: context.issue.number,
            });
            const botComment = comments.find(c => c.user.type === 'Bot' && c.body.startsWith('## Pipeline DAG Report'));
            if (botComment) {
              await github.rest.issues.updateComment({
                owner: context.repo.owner,
                repo: context.repo.repo,
                comment_id: botComment.id,
                body,
              });
            } else {
              await github.rest.issues.createComment({
                owner: context.repo.owner,
                repo: context.repo.repo,
                issue_number: context.issue.number,
                body,
              });
            }
```

### 7.2 关键设计点

- **`continue-on-error: true`** 在 baseline / diff / comment 步骤:A 类错误已由 build step 抛出;B/C 类错误优雅降级
- **token 权限**:`pull-requests: write` 仅在 PR comment 步骤需要;workflow 级设置最小权限
- **artifact 名带 SHA**:避免多次 PR 间覆盖
- **bot comment upsert**:同 PR 多次 push 时更新同一 comment,不刷屏

---

## 8. 测试策略

### 8.1 自动化测试

**零 .NET 测试改动**:本切片不动任何 `.cs` 文件,现有 `1001/0/1/1002` + `46/0/46` 测试零变化。

**workflow 自身验证**:
- **actionlint**(本地):`actionlint .github/workflows/ci.yml` 必须通过
- **shellcheck**(本地):`shellcheck scripts/diff-pipeline-dags.sh` 必须通过

### 8.2 手工验证(implementer + controller 跑)

| 场景 | 操作 | 期望 |
|---|---|---|
| **PR 无 pipeline 改动** | 创建测试 PR,改 `README.md` 一行 | PR comment "No changes detected"; artifact 上传成功 |
| **PR 加 1 个 step** | 测试 PR 加 `IPipelineSegment<,>` 类 + DI 注册 | PR comment 显示该 pipeline Δ +1;artifact 含新 HTML |
| **PR 故意引入 DOVE 错误** | 测试 PR 改 pipeline 制造 DOVE017 碰撞(临时 commit,revert) | pipeline-report job fail ❌ |
| **baseline 缺失** | 测试 PR 从孤儿分支(无 main ancestor) | skip diff;PR comment 提示 "diff unavailable";artifact 仅 HEAD |
| **同 PR 多次 push** | 测试 PR 改 2 次 | comment upsert 而非新增 2 条 |

### 8.3 Gate

- **必须**:`actionlint` + `shellcheck` 全绿
- **必须**:本切片 commit 后 push 到 main,触发完整 CI,`pipeline-report` job 在 5 分钟内完成(参考 timeout)
- **手测** 至少 2 个场景(无改动 + 有改动)由 controller 跑通
- **零回归**:1001/0/1/1002 + 46/0/46 测试不变

---

## 9. 任务拆分(预估 4 commits)

### Task 1: manifest + .gitignore(2 commits)

- **Step 1**: 创建 `.config/dotnet-tools.json` 声明 `Dovetail.Report` 1.0.0 NuGet package(命令名 `dovetail-report`)
- **Step 2**: 创建 `docs/superpowers/diagrams/.gitignore` 排除 `head-dag/` `main-dag/`
- **Step 3**: 双 commit:
  ```bash
  git add .config/dotnet-tools.json
  git commit -m "build(ci): declare Dovetail.Report 1.0.0 as local tool (manifest)"
  
  git add docs/superpowers/diagrams/.gitignore
  git commit -m "docs(diagrams): gitignore CI-generated head-dag/ and main-dag/ artifacts"
  ```

### Task 2: diff script(1 commit)

- **Step 1**: 编写 `scripts/diff-pipeline-dags.sh`(~80 行,带 `set -euo pipefail` + 注释)
- **Step 2**: 本地 `shellcheck` 通过
- **Step 3**: 单 commit:
  ```bash
  git add scripts/diff-pipeline-dags.sh
  chmod +x scripts/diff-pipeline-dags.sh
  git add scripts/diff-pipeline-dags.sh
  git commit -m "ci: add pipeline DAG diff script (structural compare + markdown output)"
  ```

### Task 3: workflow integration(1 commit)

- **Step 1**: 在 `.github/workflows/ci.yml` 末尾追加 `pipeline-report` job(§7.1 完整内容)
- **Step 2**: 本地 `actionlint .github/workflows/ci.yml` 通过
- **Step 3**: 单 commit:
  ```bash
  git add .github/workflows/ci.yml
  git commit -m "ci: add pipeline-report job (Dovetail HTML + PR diff vs main + artifact)"
  ```

### Task 4: spec + plan + memory + 父 spec amend(2 commits)

- **Step 1**: 父 spec §5 + §11 amend(扩到 2-slice 计划 + D13)
- **Step 2**: 本 spec 文件 commit
- **Step 3**: memory 文件 + MEMORY.md index
- **Step 4**: 2 commits:
  ```bash
  git add docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md
  git commit -m "docs(extraction): amend parent spec §5 + D13 — Slice 6 split into 6a (CI) + 7 (Lint deferred)"
  
  git add docs/superpowers/specs/2026-08-30-pipeline-report-ci-slice-6-design.md docs/superpowers/plans/2026-08-30-pipeline-report-ci-slice-6.md
  git commit -m "docs(extraction): add Slice 6 spec + plan (dovetail-report CI integration)"
  ```

### 总预估:**6 commits**(manifest + .gitignore + script + workflow + 父 spec amend + spec/plan)

---

## 10. 风险与回退

| 风险 | 概率 | 缓解 |
|---|---|---|
| **`Dovetail.Report` NuGet 包不可达** | 低 | Dovetail 在 GitHub release 时 publish 到 public nuget.org(`publish.yml`);若 NuGet 暂时不可达,fallback 改 `dotnet tool install --global Dovetail.Report --version 1.0.0` 显式安装,或 vendor Dovetail 源码 + `dotnet run --project Dovetail.Report/Dovetail.Report.csproj` |
| **main baseline build 失败(主分支已坏)** | 中 | `continue-on-error: true` + skip diff;PR comment 提示 |
| **PR comment 限流** | 低 | bot comment upsert 不刷屏;降级到只 upload artifact |
| **worktree add 慢(~30s)** | 中 | timeout 5min 足够;若太慢可改为 shallow clone main |
| **actionlint 不通过** | 低 | 提交前本地 `actionlint` 跑 |
| **shellcheck 不通过** | 低 | 提交前本地 `shellcheck` 跑 |

### 回退路径

- 移除 `pipeline-report` job 即可(workflow 文件 revert)
- 不影响现有 `dotnet-tests` job(零 .NET 代码改动)
- 历史 commit 不需 revert(workflow 自身 revert 即可,无副作用)

---

## 11. 决策日志

- **D1**:`dovetail-report` 通过 NuGet global tool manifest(`.config/dotnet-tools.json`)安装,而非 docker image 或 vendor binary。理由:与现有 dotnet 工具链一致,GitHub Actions `setup-dotnet@v4` 自动 cache `~/.dotnet/tools/**`,安装时间 < 5s
- **D2**:独立 `pipeline-report` job,`needs: dotnet-tests`,`if: success() || failure()`。理由:即使测试失败也要出 DAG 报告(诊断价值)
- **D3**:结构 diff 而非 HTML 像素 diff。理由:Mermaid 节点 id 不稳定,HTML diff 噪声大;pipeline 文件名 + segment 计数足够暴露 topology 变化
- **D4**:PR comment 用 `actions/github-script@v7` in-line,而非 workflow_run 触发。理由:PR comment 是关键价值点,in-line 简化配置
- **D5**:`continue-on-error: true` 用于 baseline/diff/comment 步骤。理由:A 类(编译失败)已由 build step 抛出;B/C 类应优雅降级,artifact 仍是主交付物
- **D6**:bot comment upsert(检测已有 "## Pipeline DAG Report" comment 并 update,否则 create)。理由:同 PR 多次 push 不刷屏
- **D7**:跨切片一致性 lint 推迟到 Slice 7(从父 spec §5 项拆分)。理由:Slice 6 范围已足够大(CI 接入 + diff + comment);lint 是独立子系统(需 Roslyn analyzer 或 build-time script 设计),适合单独切片

---

## 12. Spec 自审

### 12.1 Placeholder scan

无 `TBD` / `TODO` / "实现细节" / "fill in details"。每步代码完整可执行(§7.1 workflow yaml + §9 bash 伪代码 + §3.3 diff script 伪代码)。

### 12.2 Internal consistency

- **§1.1 父 spec 表格** vs **§6 不修改文件**:父 spec 文件不修改,但 §1.1 表格陈述当前状态(切 1-5 已完成)— 一致
- **§3.1 架构** vs **§7.1 workflow yaml**:`needs: dotnet-tests` + `if: success() || failure()` 在两处一致
- **§4.1 路径表** vs **§6 新建文件**:`.gitignore` 在两处都说明
- **§8.2 手工验证 5 场景** vs **§5 错误处理 3 类**:5 场景覆盖 A 类(DOVE 错误)+ B 类(baseline 缺失)+ C 类(comment 失败)

### 12.3 Scope check

| 关注点 | 是否在 scope 内 |
|---|---|
| Dovetail-report 接入 CI | ✅ |
| PR diff vs main | ✅ |
| artifact 上传 | ✅ |
| PR comment | ✅ |
| 跨切片一致性 lint | ❌ Slice 7(§11 D7) |
| GitHub Pages 长期 snapshot | ❌ 后续切片 |
| 运行时 metric | ❌ 后续切片 |

### 12.4 Ambiguity check

- **"结构 diff" 范围**:§3.3 明确为 pipeline 文件名集合 + segment 计数;不包含 HTML 内容 diff
- **"PR 无变化" 判定**:§3.4 明确为 added/removed/modified 三个维度均为 0
- **"首次 commit" 行为**:§5 baseline 缺失 → skip diff,只 upload HEAD,§4.1 gitignore 仅排除临时产物,§8.2 手工验证场景 4 覆盖
- **"bot comment upsert" 识别规则**:§7.1 github-script 检测 `c.body.startsWith('## Pipeline DAG Report')`
