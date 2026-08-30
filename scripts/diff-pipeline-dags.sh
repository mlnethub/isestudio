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
  # grep -c 在 0 匹配时返回 exit 1,用 `|| true` 避免 set -e 中断,也不要重复 echo "0"
  grep -c "Pipeline:" "$file" 2>/dev/null || true
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