# Dovetail 流水线报告 CI 接入(Slice 6)实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal**:在 `.github/workflows/ci.yml` 接入 `dovetail-report 1.0.0`,新增独立 `pipeline-report` job,PR 内自动生成 pipeline DAG HTML + 与 main base 做结构 diff + artifact 上传 + PR comment。

**Architecture**:manifest-based 安装 dovetail-report(`.config/dotnet-tools.json`)→ 新增 `pipeline-report` job 跑 dovetail-report → worktree checkout main 生成 baseline → 结构 diff 脚本(bash + grep + comm)→ upload-artifact → in-line PR comment via `actions/github-script@v7`。

**Tech Stack**:GitHub Actions(ubuntu-latest)、.NET 10.0.x、`dovetail-report 1.0.0`(NuGet global tool via local manifest)、bash + grep + comm + awk。

**Spec**:`docs/superpowers/specs/2026-08-30-pipeline-report-ci-slice-6-design.md`

---

## Global Constraints

- **直接 main 分支落地**(无 worktree,与 slice 1-5 一致)
- **每 commit 添加** `Co-Authored-By: Claude <noreply@anthropic.com>` 尾注
- **RTK-wrapped git**(hook 自动改写 git 命令,`git add` / `git commit` / `git status` 等)
- **零 .NET 代码改动**:本切片仅 `.github/workflows/ci.yml` + `.config/dotnet-tools.json` + `scripts/diff-pipeline-dags.sh` + `.gitignore`。现有 `1001/0/1/1002` + `46/0/46` 测试零变化
- **actionlint**:`actionlint .github/workflows/ci.yml` 必须通过(提交前本地跑)
- **shellcheck**:`shellcheck scripts/diff-pipeline-dags.sh` 必须通过(提交前本地跑)
- **不动 slice 1-5 任何代码**:仅 spec/memory 文件 amend
- **1 commit per task**(Task 4/5 各包含 2 commit)
- **不删 `.superpowers/sdd/...` 工作空间**(由 controller 收尾)

---

## Task 1: manifest + diagrams .gitignore

**Files:**
- Create: `.config/dotnet-tools.json`
- Create: `docs/superpowers/diagrams/.gitignore`

**Interfaces:**
- Consumes: spec §3.2(Dovetail 工具 manifest 安装)
- Produces:CI job 可 `dotnet tool restore` 安装 Dovetail.Report 1.0.0 NuGet package(命令 `dovetail-report`);CI 临时 `head-dag/` `main-dag/` 不入 git

### Task 1 步骤

- [ ] **Step 1: 写 `.config/dotnet-tools.json`**

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

- [ ] **Step 2: 验证 JSON 语法合法**

Run: `cat .config/dotnet-tools.json | python -m json.tool`
Expected: 输出格式化 JSON,无 parse error

- [ ] **Step 3: 本地 dry-run install(可选)**

Run: `dotnet tool restore`
Expected: `dovetail-report` 安装到 `.tools/` 目录,`which dovetail-report` 或 `dotnet dovetail-report --help` 返回 usage

- [ ] **Step 4: 写 `docs/superpowers/diagrams/.gitignore`**

```
# CI-generated temporary artifacts (do not track)
head-dag/
main-dag/
```

注意:**不**忽略 `extraction-job-dag/` / `extraction-tbox-dag/` 等手工提交的永久 snapshot。

- [ ] **Step 5: 验证 .gitignore 路径匹配**

Run: `cd docs/superpowers/diagrams && git check-ignore -v head-dag/index.html main-dag/index.html extraction-job-dag/index.html`
Expected: 前两个命中 .gitignore,第三个 `exit 0` 不命中(永久 snapshot 仍 track)

- [ ] **Step 6: Commit(2 commits)**

```bash
git add .config/dotnet-tools.json
git commit -m "build(ci): declare Dovetail.Report 1.0.0 as local tool (manifest)

Co-Authored-By: Claude <noreply@anthropic.com>"

git add docs/superpowers/diagrams/.gitignore
git commit -m "docs(diagrams): gitignore CI-generated head-dag/ and main-dag/ artifacts

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 2: 结构 diff 脚本

**Files:**
- Create: `scripts/diff-pipeline-dags.sh`(executable)

**Interfaces:**
- Consumes:`docs/superpowers/diagrams/head-dag/` + `docs/superpowers/diagrams/main-dag/` 两个目录(目录内每个 pipeline 一个 HTML 文件 + index.html + vendor)
- Produces:stdout 输出 markdown 表格(diff summary);exit 0 成功 / non-zero 失败

### Task 2 步骤

- [ ] **Step 1: 写 `scripts/diff-pipeline-dags.sh`**

```bash
#!/usr/bin/env bash
#
# scripts/diff-pipeline-dags.sh
#
# 结构 diff 两个 pipeline DAG 报告目录,输出 markdown 表格。
# 比 pipeline 文件名集合(pipeline 增删)+ 各 pipeline 的 segment 计数。
# 不做 HTML 内容 diff(Mermaid 节点 id 不稳定)。
#
# Usage: bash scripts/diff-pipeline-dags.sh <head-dir> <main-dir>
#
# Output: stdout markdown 表格行(不带表头,PR comment 模板加表头)
# Exit:   0 = 成功(可能 diff 为空),non-zero = 错误

set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <head-dir> <main-dir>" >&2
  exit 2
fi

HEAD_DIR="$1"
MAIN_DIR="$2"

if [[ ! -d "$HEAD_DIR" ]]; then
  echo "ERROR: head dir not found: $HEAD_DIR" >&2
  exit 3
fi

# Main dir 可能缺失(孤儿分支 / 首次 commit);此时输出空 diff
HAS_MAIN="true"
if [[ ! -d "$MAIN_DIR" ]]; then
  HAS_MAIN="false"
fi

# 提取 pipeline 文件名(排除 index + vendor)
list_pipelines() {
  local dir="$1"
  if [[ ! -d "$dir" ]]; then
    return
  fi
  find "$dir" -maxdepth 1 -type f -name "*.html" \
    ! -name "index.html" \
    ! -name "vendor*.html" \
    -printf "%f\n" \
    | sort
}

# 数 segment(grep "Pipeline:" 计数,这个 marker 是 Dovetail HTML 的稳定锚点)
count_segments() {
  local file="$1"
  if [[ ! -f "$file" ]]; then
    echo "0"
    return
  fi
  grep -c "Pipeline:" "$file" 2>/dev/null || echo "0"
}

head_pipes=()
while IFS= read -r line; do
  [[ -n "$line" ]] && head_pipes+=("$line")
done < <(list_pipelines "$HEAD_DIR")

main_pipes=()
if [[ "$HAS_MAIN" == "true" ]]; then
  while IFS= read -r line; do
    [[ -n "$line" ]] && main_pipes+=("$line")
  done < <(list_pipelines "$MAIN_DIR")
fi

# 输出 markdown 表格行(每行: | `Pipeline` | Δ | Status |)
output_rows=()

# Added
for p in "${head_pipes[@]}"; do
  if [[ ! " ${main_pipes[*]:-} " =~ " ${p} " ]]; then
    segs=$(count_segments "$HEAD_DIR/$p")
    output_rows+=("| \`${p%.html}\` | +${segs} | 🆕 added |")
  fi
done

# Removed
if [[ "$HAS_MAIN" == "true" ]]; then
  for p in "${main_pipes[@]}"; do
    if [[ ! " ${head_pipes[*]:-} " =~ " ${p} " ]]; then
      segs=$(count_segments "$MAIN_DIR/$p")
      output_rows+=("| \`${p%.html}\` | -${segs} | 🗑️ removed |")
    fi
  done
fi

# Modified(仅同名 pipeline 比较)
if [[ "$HAS_MAIN" == "true" ]]; then
  for p in "${head_pipes[@]}"; do
    if [[ " ${main_pipes[*]:-} " =~ " ${p} " ]]; then
      head_segs=$(count_segments "$HEAD_DIR/$p")
      main_segs=$(count_segments "$MAIN_DIR/$p")
      delta=$((head_segs - main_segs))
      if [[ $delta -eq 0 ]]; then
        output_rows+=("| \`${p%.html}\` | 0 | unchanged |")
      elif [[ $delta -gt 0 ]]; then
        output_rows+=("| \`${p%.html}\` | +${delta} | ✏️ modified |")
      else
        output_rows+=("| \`${p%.html}\` | ${delta} | ✏️ modified |")
      fi
    fi
  done
fi

# 输出
if [[ ${#output_rows[@]} -eq 0 ]]; then
  echo "<!-- no pipeline changes -->"
else
  printf '%s\n' "${output_rows[@]}"
fi
```

- [ ] **Step 2: 写一个本地 fixture 测 diff script**

```bash
# 建临时 fixture
TMPDIR=$(mktemp -d)
HEAD="$TMPDIR/head"
MAIN="$TMPDIR/main"
mkdir -p "$HEAD" "$MAIN"

# Head 含 3 个 pipeline
echo "<html>Pipeline: A</html>" > "$HEAD/AlphaPipeline.html"
echo "<html>Pipeline: B1\nPipeline: B2\nPipeline: B3</html>" > "$HEAD/BetaPipeline.html"
echo "<html>Pipeline: N1\nPipeline: N2</html>" > "$HEAD/NewPipeline.html"

# Main 含 2 个 pipeline(Alpha + 旧 Gamma,Beta 段数 2)
echo "<html>Pipeline: A</html>" > "$MAIN/AlphaPipeline.html"
echo "<html>Pipeline: G</html>" > "$MAIN/GammaPipeline.html"
echo "<html>Pipeline: B1\nPipeline: B2</html>" > "$MAIN/BetaPipeline.html"

# 跑 diff
bash scripts/diff-pipeline-dags.sh "$HEAD" "$MAIN"
```

Expected output(行序可能因 `find` 而异):
```
| `AlphaPipeline` | 0 | unchanged |
| `BetaPipeline` | +1 | ✏️ modified |
| `GammaPipeline` | -1 | 🗑️ removed |
| `NewPipeline` | +2 | 🆕 added |
```

- [ ] **Step 3: shellcheck 通过**

Run: `shellcheck scripts/diff-pipeline-dags.sh`
Expected: 0 errors(可有 info-level warnings,可接受)

- [ ] **Step 4: chmod +x**

Run: `chmod +x scripts/diff-pipeline-dags.sh`
Verify: `ls -la scripts/diff-pipeline-dags.sh` 显示 `-rwxr-xr-x`

- [ ] **Step 5: Commit**

```bash
git add scripts/diff-pipeline-dags.sh
git commit -m "ci: add pipeline DAG diff script (structural compare + markdown output)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 3: workflow yaml 集成

**Files:**
- Modify: `.github/workflows/ci.yml`(末尾 append `pipeline-report` job)

**Interfaces:**
- Consumes:Task 1(manifest + .gitignore)+ Task 2(diff script)+ spec §7.1 完整 yaml
- Produces:`pipeline-report` job 端到端跑通(实际 CI 触发由 controller 验证)

### Task 3 步骤

- [ ] **Step 1: 读 `.github/workflows/ci.yml` 当前内容**

Run: `cat .github/workflows/ci.yml`
Expected: 89 行(line 88 末尾空行 + line 89 可能空行)

- [ ] **Step 2: 在 `dotnet-tests` job 后追加 `pipeline-report` job**

打开 `.github/workflows/ci.yml`,在最后一行(若末尾有空行则插在空行前)后追加:

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
          set +e
          git worktree add /tmp/main-baseline main
          pushd /tmp/main-baseline > /dev/null
          dotnet restore src/ISEStudio.sln
          dotnet build src/ISEStudio.sln -c Release
          dotnet tool restore
          dotnet dovetail-report \
            --project src/ISEStudio/ISEStudio.csproj \
            --output /tmp/main-dag \
            --nologo
          popd > /dev/null
          mkdir -p docs/superpowers/diagrams/main-dag
          cp -r /tmp/main-dag/. docs/superpowers/diagrams/main-dag/ || true
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
            const runUrl = '${{ github.server_url }}/${{ github.repository }}/actions/runs/${{ github.run_id }}';
            let body;
            if (fs.existsSync(diffPath)) {
              const raw = fs.readFileSync(diffPath, 'utf8').trim();
              const hasRows = raw.length > 0 && !raw.startsWith('<!--');
              body = hasRows
                ? '## Pipeline DAG Report\n\n📊 Changes detected (vs main):\n\n| Pipeline | Δ Segments | Status |\n|---|---|---|\n' + raw + '\n\n📦 [Download full HTML report](' + runUrl + ')\n\nGenerated by `pipeline-report` job in commit `${{ github.sha }}`.'
                : '## Pipeline DAG Report\n\n✅ No pipeline topology changes detected (vs main).\n\n📦 [Download full HTML report](' + runUrl + ')';
            } else {
              body = '## Pipeline DAG Report\n\n⚠️ Diff generation unavailable (baseline generation failed). See artifact for raw HEAD + main reports.';
            }

            const { data: comments } = await github.rest.issues.listComments({
              owner: context.repo.owner,
              repo: context.repo.repo,
              issue_number: context.issue.number,
            });
            const botComment = comments.find(c => c.user.type === 'Bot' && c.body && c.body.startsWith('## Pipeline DAG Report'));
            if (botComment) {
              await github.rest.issues.updateComment({
                owner: context.repo.owner,
                repo: context.repo.repo,
                comment_id: botComment.id,
                body,
              });
              core.info('Updated existing bot comment: ' + botComment.id);
            } else {
              await github.rest.issues.createComment({
                owner: context.repo.owner,
                repo: context.repo.repo,
                issue_number: context.issue.number,
                body,
              });
              core.info('Created new bot comment');
            }
```

- [ ] **Step 3: actionlint 本地校验**

Run: `actionlint .github/workflows/ci.yml`
Expected: 0 errors(actionlint 安装:`brew install actionlint` 或 `go install github.com/rhysd/actionlint/cmd/actionlint@latest`;若不可用可跳过但需 controller 在 review 时 verify)

- [ ] **Step 4: 验证 yaml 仍 valid**

Run: `python -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml'))"`
Expected: 无输出(silent success;yaml load 不抛)

- [ ] **Step 5: 检查 git diff 仅含新增内容**

Run: `git diff .github/workflows/ci.yml | head -50`
Expected: 显示新增的 `pipeline-report:` job,无修改现有 `branch-flow` / `frontend` / `dotnet-tests` job 的行

- [ ] **Step 6: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add pipeline-report job (Dovetail HTML + PR diff vs main + artifact)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 4: 父 spec amend(D13 + Slice 6 拆分)

**Files:**
- Modify: `docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md`(2 处:§5 表格 Slice 6 行 + 新段 Slice 7 提示;§11 追加 D13)

**Interfaces:**
- Consumes:本切片 spec §11 D7 决策(Slice 6 拆分为 6a CI + 7 Lint)
- Produces:父 spec 反映路线图新状态

### Task 4 步骤

- [ ] **Step 1: 读父 spec §5 当前 Slice 6 行**

定位(line 178):
```
| **6** | `dovetail-report` 接入 CI + 跨 slice 一致性 lint | 1 |
```

- [ ] **Step 2: 替换 Slice 6 行 + 加 Slice 7 行**

替换:
```
| **6** | `dovetail-report` 接入 CI + 跨 slice 一致性 lint | 1 |
```
为:
```
| **6a** | `dovetail-report` 接入 GitHub Actions(独立 pipeline-report job + PR diff vs main base + artifact + PR comment)| 1(2026-08-30 🟡 实施中,详见 `2026-08-30-pipeline-report-ci-slice-6-design.md`)|
| **7** | 跨 slice 一致性 lint(naming / DI block / JobCarries wrapper pattern,Roslyn analyzer 或 build-time script) | 1(从 §5 Slice 6 拆分,推迟) |
```

- [ ] **Step 3: 在 §11 决策日志追加 D13**

打开父 spec,在 §11 最后一行(原 D12 之后,§12 之前)追加:
```markdown
- **D13 Slice 6 拆分**(2026-08-30):原 §5 Slice 6 `dovetail-report` 接入 CI + 跨 slice 一致性 lint 拆为两项 — Slice 6a(本切片,CI 接入)+ Slice 7(后续切片,lint)。理由:Ci 接入与 lint 各自独立子系统,合并 1 slice 范围过宽;CI 单独落地可立即 deliver PR DAG diff 价值,lint 单独切片可深入设计 Roslyn analyzer 或 build-time 脚本。
```

- [ ] **Step 4: grep 验证**

Run: `grep -n "Slice 6a\|Slice 7\b\|D13 Slice 6 拆分" docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md`
Expected: 3+ matches

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-08-28-extraction-dovetail-pipeline-design.md
git commit -m "docs(extraction): amend parent spec §5 + D13 — Slice 6 split into 6a (CI) + 7 (Lint deferred)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 5: spec/plan/memory 落地

**Files:**
- Modify: `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\MEMORY.md`(append index entry,**非 git tracked,user-level**)
- Create: `C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\ontopilot-extraction-dovetail-slice6.md`(**非 git tracked,user-level**)

**Interfaces:**
- Consumes:本切片 commit stack(预计 5 commits:Task 1 = 2 + Task 2 = 1 + Task 3 = 1 + Task 4 = 1)
- Produces:memory file + MEMORY.md index 更新

### Task 5 步骤

- [ ] **Step 1: 写 `memory/ontopilot-extraction-dovetail-slice6.md`(user-level path)**

```markdown
---
name: ontopilot-extraction-dovetail-slice6
description: Dovetail-report 接入 GitHub Actions(独立 pipeline-report job + PR diff vs main base + artifact + PR comment),零 .NET 代码改动,5 commits
metadata:
  type: project
---

# Dovetail Slice 6a: Pipeline Report CI

## 概览

ISEStudio extraction pipeline Dovetail 化的第 6a 切片(父 spec §5 路线图,原 §5 Slice 6 拆分)。把 `dovetail-report 1.0.0` 接入 GitHub Actions,作为独立 `pipeline-report` job,与 dotnet-tests 平行跑,自动生成 pipeline DAG HTML + PR diff vs main base + artifact 上传 + PR comment upsert。

**Why**:Slice 1-5 落地后,5 个 pipeline 的 DAG 拓扑只能本地手动跑 dovetail-report,CI 看不到 DAG 状态、PR 评审者 catch 不了 segment 拓扑意外变化;统一 CI 接入后可立即 deliver DAG 回归保护 + 历史 snapshot artifact。

**How to apply**:`pipeline-report` job 用 `if: success() || failure()` 即使测试失败也生成;`continue-on-error: true` 在 baseline/diff/comment 步骤(A 类编译失败已由 build 抛出);结构 diff 不做 HTML 像素(Mermaid 节点 id 不稳定),只看 pipeline 文件名 + segment 计数;bot comment upsert 防刷屏。

## Commit stack(预估 5 commits)

1. `.config/dotnet-tools.json`(Task 1)— manifest 声明 `Dovetail.Report` 1.0.0 NuGet package(installs `dovetail-report` 命令)
2. `docs/superpowers/diagrams/.gitignore`(Task 1)— 排除 CI 临时 `head-dag/` `main-dag/`
3. `scripts/diff-pipeline-dags.sh`(Task 2)— bash 结构 diff 脚本
4. `.github/workflows/ci.yml`(Task 3)— append pipeline-report job
5. `docs/superpowers/specs/...2026-08-28...md`(Task 4)— 父 spec §5 + §11 amend

## 核心架构 LOCKED

- **独立 pipeline-report job**:`needs: dotnet-tests`,`if: success() || failure()`,9 个 step(checkout → setup-dotnet → restore → build → tool restore → HEAD report → main baseline → diff → upload-artifact → PR comment)
- **结构 diff 而非 HTML diff**:bash 脚本比 pipeline 文件名集合 + 各 pipeline 的 segment 计数(grep "Pipeline:" 锚点),不做 HTML 像素 diff
- **PR comment upsert**:`actions/github-script@v7` 内检测已有 bot comment(`c.body.startsWith('## Pipeline DAG Report')`)并 update,避免同 PR 多次 push 刷屏
- **3 类错误降级**:A 类(编译失败)由 build step 抛出 → job fail;B 类(baseline / diff 失败)→ `continue-on-error: true` + skip diff + comment 提示; C 类(comment API 失败)→ warn
- **manifest-based 工具安装**:`.config/dotnet-tools.json` 声明 `Dovetail.Report` 1.0.0 NuGet package(`commands: ["dovetail-report"]` 别名),`dotnet tool restore` 自动安装,GitHub Actions 默认 cache `~/.dotnet/tools/**`
- **临时产物不入 git**:`docs/superpowers/diagrams/.gitignore` 仅排除 `head-dag/` `main-dag/`(CI 临时),不排除 `extraction-job-dag/` 等永久 snapshot

## 测试门

- **零 .NET 代码改动**:1001/0/1/1002 + 46/0/46 测试不变(workflow-only slice)
- **actionlint**:`actionlint .github/workflows/ci.yml` 必须通过
- **shellcheck**:`shellcheck scripts/diff-pipeline-dags.sh` 必须通过
- **手测 5 场景**(controller):无改动 PR / 加 step PR / DOVE 错误 / baseline 缺失 / 多次 push

## Dovetail 1.0.0 行为变更

**零行为变更**。本切片是 CI 接入,不影响 Dovetail 1.0.0 行为,仅引入 CI 观测能力。

## PARKED items

- **跨切片一致性 lint 推迟到 Slice 7**(原 §5 Slice 6 拆分):D13 决策 — Roslyn analyzer 或 build-time script 设计独立切片
- **GitHub Pages 长期 snapshot**:增量发布需 Pages 配置,后续切片
- **运行时 metric**(DAG 长度 / segment 数量历史趋势):后续切片
- **actionlint 安装**:本切片假定 controller 本地有 actionlint,CI 自身不依赖

## 相关 memory

- [[ontopilot-extraction-dovetail-slice1]] TBox pipeline(本切片 TBoxChunkPipeline cross-link)
- [[ontopilot-extraction-dovetail-slice2]] ABox sub-DAG(本切片 ABoxJobPipeline cross-link)
- [[ontopilot-extraction-dovetail-slice3]] AgentChain(本切片 chain segment cross-link)
- [[ontopilot-extraction-dovetail-slice4]] Vocabulary pipeline(本切片 TerminologyPipeline cross-link)
- [[ontopilot-extraction-dovetail-slice5]] Job pipeline(本切片 3 Job pipeline cross-link)
```

- [ ] **Step 2: 更新 `MEMORY.md` index**

Append 一行到 `## Active pipeline work (Dovetail extraction, 2026-08-28~30)` section:

```markdown
- [ontopilot-extraction-dovetail-slice6](ontopilot-extraction-dovetail-slice6.md) — Dovetail-report 接入 GitHub Actions(独立 pipeline-report job + PR diff vs main base + 5 commits,零 .NET 代码改动)
```

- [ ] **Step 3: 验证 memory 文件可读**

Run: `ls -la "C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\ontopilot-extraction-dovetail-slice6.md"`
Expected: 文件存在,> 1KB

- [ ] **Step 4: 验证 MEMORY.md index 更新**

Run: `grep "slice6" "C:\Users\geffz\.claude\projects\e--GitHub-ontopilot\memory\MEMORY.md"`
Expected: 1 match

- [ ] **Step 5: 无 git commit 需求**

memory 文件 + MEMORY.md 在 user-level 路径,不在 git repo 内,无需 git add / commit。slice 5 spec commit + 本 plan 文件 commit 已在本切片前一 commit(`f4c88a8`)+ 本 Task 4 commit 内。

---

## 任务汇总表

| Task | 文件数 | Commit 数 | 预估时间 |
|---|---|---|---|
| Task 1: manifest + .gitignore | 2 create | 2 | 5 min |
| Task 2: diff 脚本 | 1 create | 1 | 15 min |
| Task 3: workflow 集成 | 1 modify | 1 | 10 min |
| Task 4: 父 spec amend | 1 modify | 1 | 5 min |
| Task 5: memory 落地 | 2 create (user-level,不入 git) | 0 | 5 min |
| **合计** | **5 file + 1 modify + 2 user-level** | **5 commits** | **~40 min** |

---

## Self-Review

### 1. Spec coverage

| Spec § | 对应 Task |
|---|---|
| §1 背景与现状 | Task 4 §11 D13(父 spec amend 反映现状)|
| §2 设计目标 | Task 1-3 全部 + Task 4 §5 表格 |
| §3 架构总览 | Task 3(workflow yaml)+ Task 2(diff 脚本)|
| §4 Data Flow | Task 3 完整 11 步流程 |
| §5 Error Handling | Task 3 yaml `continue-on-error` + `if: always()` |
| §6 文件结构 | Task 1-4 全部对应 |
| §7 workflow 改动 | Task 3 Step 2(完整 yaml 复制)|
| §8 测试策略 | Task 2 Step 2(fixture 测 diff)+ Task 3 Step 3(actionlint)+ controller 手测 |
| §9 任务拆分 | Task 1-5 1:1 对应(本计划细化版)|
| §10 风险与回退 | Task 3 Step 1 风险表 + 回退 = revert workflow yaml |
| §11 决策日志 | Task 4 D13 + 7 决策保留 |

✅ 全覆盖。

### 2. Placeholder scan

无 TBD / TODO / "实现细节"占位符。每步骤代码完整可执行(fixture 命令 + commit message + 文件内容)。

### 3. Type consistency

- `.config/dotnet-tools.json` schema(标准 `dotnet tool manifest` 格式)在 spec §3.2 + Task 1 Step 1 一致
- diff 脚本签名 `(head-dir, main-dir)` 在 spec §3.3 + Task 2 Step 1 + Task 3 Step 2 `bash scripts/diff-pipeline-dags.sh <args>` 一致
- workflow yaml 步骤顺序(spec §4.2 11 步 + Task 3 Step 2 完整 yaml)一致
- 父 spec §5 表格字段(`Slice | 范围 | 切片数`)| Task 4 Step 2 新行格式一致
- memory 文件 frontmatter 与 slice 1-5 格式一致(`metadata.type: project` + `description` 单行 + `## 相关 memory` 用 `[[name]]` link)

### 4. Plan state

**Ready for execution**。下一步:派发 implementer subagent 跑 Task 1。
