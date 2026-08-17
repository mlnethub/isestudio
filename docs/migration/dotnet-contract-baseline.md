# .NET Migration — Python Contract Baseline

This document freezes the current Python backend's contract surface so the
.NET migration's api-mcp stage can run parameterized parity tests. The two
artifacts below are byte-deterministic outputs of
`backend/scripts/export_contract_baseline.py`.

## Frozen counts (from the artifacts in this folder)

| Surface | Source | Count |
| ------- | ------ | ----- |
| REST operations | `app.openapi()["paths"]` (methods in `get`, `post`, `put`, `patch`, `delete`) | **154** |
| REST paths | `app.openapi()["paths"]` | **128** |
| MCP tools | `mcp.list_tools()` | **20** |

The artifacts live at `migration/baseline/openapi-python.json` and
`migration/baseline/mcp-tools-python.json`. They were produced by running:

```bash
cd backend
python scripts/export_contract_baseline.py ../migration/baseline
```

SHA-256 fingerprints captured on the commit that froze them:

- `openapi-python.json`
- `mcp-tools-python.json`

(The exact hashes are intentionally not pinned here so this document stays
useful after the artifacts move; regenerate them with
`sha256sum migration/baseline/*.json`.)

## Operation inventory — REST surface

The `openapi-python.json` baseline captures every operation declared by the
Python routers (`auth`, `documents`, `knowledge`, `ontology`, `extraction`,
`conflicts`, `history`, `abox`, `resolution`, `settings_api`, `providers`,
`tokens`, `external`, `published`, `rdf_import`, `vocabulary`, `prompts`,
`releases`, `mcp_tokens`). The .NET api-mcp stage must satisfy every
`(METHOD, path)` pair in the file; extras must be explicitly approved.

The plan's stage 4 mentions "153 个 REST 路由声明" as an aspirational figure
derived from a manual count; the live OpenAPI generator produces **154
operations across 128 paths** — one more operation than the manual count.
Treat the baseline as the source of truth.

## Tool inventory — MCP surface

The `mcp-tools-python.json` baseline is the JSON payload the FastMCP server
returns to an authenticated `tools/list` JSON-RPC request. Tools are sorted by
`name` to keep the file byte-deterministic. The frozen set is:

```
apply_instance_change
apply_ontology_changes
apply_vocabulary_change
decide_review_item
get_history
get_individual
get_ontology
get_workspace_context
list_documents
list_individuals
list_releases
list_review_items
list_vocabulary_concepts
manage_release
preview_ontology_changes
query_knowledge
resolve_term
rollback_history_event
search_ontology
start_extraction
```

## Source-vs-protocol discrepancy

The plan flagged that the previous source decorator count (21) and the old
`tools/list` assertion (20) disagreed. Both are now **20**:

- `backend/app/mcp_server.py` declares 20 `@mcp.tool(...)` functions
  (verified with `grep -c '^@mcp\.tool' backend/app/mcp_server.py`).
- `mcp.list_tools()` returns 20 `Tool` objects.
- `backend/tests/test_mcp.py::test_streamable_http_lists_authenticated_tools`
  asserts `len(names) == 20` over the authenticated Streamable HTTP response.

The discrepancy has been resolved: source and protocol agree at 20 tools, and
the baseline locks that number. If a future change adds or removes a tool,
rerun the export script and update the count above in the same commit.

## How determinism is guaranteed

`backend/scripts/export_contract_baseline.py` writes both files through the
following invariants:

- **Compact, sorted JSON.** `json.dumps(..., sort_keys=True, separators=(",", ":"))`
  so every dict/list appears in a stable order, and there is no whitespace
  variation between runs.
- **Trailing newline.** Each artifact ends with a single `\n` byte; this is the
  POSIX-friendly canonical terminator and keeps `git diff` output clean.
- **Sorted operations.** REST operations are flattened into `(METHOD, path)`
  tuples sorted lexicographically before serialisation, and the OpenAPI
  schema itself is dumped with `sort_keys=True` so the path dictionary order
  inside the file is deterministic too.
- **Sorted MCP tools.** Tool entries are sorted by `name` so two runs of
  `mcp.list_tools()` (whose internal ordering is implementation-defined)
  produce identical files.
- **No transient state.** `_read_openapi()` calls `app.openapi()` (FastAPI
  caches the schema at the module level, returning the same dict on repeat
  calls) and `_read_tools_list_response()` calls `mcp.list_tools()` which
  only inspects the tool registry — neither hits the database or the Oxigraph
  store, so no timestamp, generated ID, or external resource leaks into the
  output. The script also imports `app.main` lazily inside the helpers to
  avoid triggering startup side effects at module import time.

The regression test `backend/tests/test_dotnet_contract_baseline.py` exports
to two temporary directories in the same process and asserts the resulting
bytes are identical; the test would fail loudly if any of the invariants
above regressed.

## Regenerating the baseline

After any change to a FastAPI router or to a `@mcp.tool(...)` decorator:

1. Update this file's counts and tool list in the same commit.
2. Run `cd backend && python scripts/export_contract_baseline.py ../migration/baseline`.
3. Run `python -m pytest tests/test_dotnet_contract_baseline.py -q` to confirm
   the deterministic export still holds.
4. Commit the artifacts, the script, the test, and this document together so
   the api-mcp stage always reads from a coherent snapshot.

## Cross-references

- `docs/superpowers/plans/2026-08-16-ontopilot-dotnet-migration.md` — task 1
  brief (this document fulfils task 1).
- `docs/superpowers/plans/2026-08-16-ontopilot-dotnet-api-mcp.md` — the api-mcp
  stage that consumes these artifacts.
- `backend/scripts/export_contract_baseline.py` — the exporter.
- `backend/tests/test_dotnet_contract_baseline.py` — the determinism test.

## Task 1 inventory findings (api-mcp stage, plan task 1)

The api-mcp plan's task 1 establishes the parameterized contract gates.
Running the test skeleton at this commit produced:

- **OpenAPI parity**: 154 expected operations from the Python baseline vs.
  0 actual operations in the current .NET app. The .NET side has not
  wired `builder.Services.AddOpenApi()` or `app.MapOpenApi()` yet, and no
  internal controllers exist, so the inventory is empty by construction.
  This is the expected task-1 state and the inventory failure is the
  gate the brief calls for: a clear "missing 154 operations" diff that
  tasks 2 (controllers) and 3 (external / published) close one by one.
- **MCP parity**: 20 expected tools from the Python baseline vs. 0 in
  the .NET app. Task 4 owns the MCP transport and the per-tool
  registration; the inventory failure is expected and not a regression.
- **Facade smoke test**: passes. `IIntegrationApiFacade` compiles,
  `IntegrationApiFacade` can be instantiated, and every method throws
  `NotImplementedException` with a TODO comment naming the task that
  fills it in.

### 21/20 MCP decorator vs. test discrepancy — resolved

The plan flagged that `backend/app/mcp_server.py` had 21 `@mcp.tool(...)`
decorators and `backend/tests/test_mcp.py` asserted `len(names) == 20`.
Both numbers are now **20**:

- 20 `@mcp.tool(...)` decorators in `backend/app/mcp_server.py`
  (verified with `grep -c '^@mcp\.tool' backend/app/mcp_server.py`).
- `mcp.list_tools()` returns 20 `Tool` objects.
- `backend/tests/test_mcp.py::test_streamable_http_lists_authenticated_tools`
  asserts 20 over the authenticated Streamable HTTP response.

The discrepancy was closed before this commit and the baseline JSON
reflects the resolved count. Any future change that adds or removes a
tool MUST rerun `backend/scripts/export_contract_baseline.py` and update
this document in the same commit.
