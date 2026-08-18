# Contract & Observability — Open Follow-ups

This document tracks the **known-but-out-of-scope** work items surfaced by
the Stage 5 Task 2 review (`task-2-review.md`). Each item lives here so
that downstream agents (or a future Stage 5 task) can pick them up
without re-deriving the context from the review report.

> **Do NOT action these items as part of any Task 2 fix-up round.** They
> are explicitly deferred per the Stage 5 brief.

---

## F1 — Playwright project wiring

**Status:** Deferred (Stage 5 follow-up).
**Discovered by:** `task-2-review.md` item I3 / Implementer concern #1.
**Owner:** TBD (next Stage 5 task that touches `frontend/e2e/`).

### Problem

The .NET E2E suite delivered under `frontend/e2e/dotnet/` (Stage 5
Task 2) ships three Playwright spec files plus shared helpers:

- `frontend/e2e/dotnet/upload-extract-publish.spec.ts`
- `frontend/e2e/dotnet/vocabulary.spec.ts`
- `frontend/e2e/dotnet/session.spec.ts`
- `frontend/e2e/dotnet/helpers/{auth,publish,config}.ts`

Neither `frontend/playwright.config.ts` nor `@playwright/test` in
`frontend/package.json` exists today. The specs are therefore
non-executable as shipped — the brief explicitly scoped `frontend/src/`
and `frontend/package.json` out of Task 2, so this gap is by design.

### Required work (next task)

1. Add `frontend/playwright.config.ts` with at minimum:
   - `testDir: 'e2e'`
   - A `dot-net` project that scopes to `e2e/dotnet/**`
   - A `webServer` block that boots the .NET backend on port 18080
     (so the existing `beforeAll` `/api/health` probe in `helpers/config.ts`
     resolves)
   - Browser matrix (Chromium is sufficient for the smoke suite)
2. Add `@playwright/test` to `frontend/package.json` devDependencies and
   pin a version that matches the lockfile conventions used elsewhere
   in the repo.
3. Add `pnpm exec playwright install --with-deps chromium` to the
   operator README so CI/developers can run the suite.
4. Verify the spec files resolve by running
   `pnpm --dir frontend exec playwright test e2e/dotnet` end to end
   against a live .NET backend.

### Why deferred here

- Touches `frontend/package.json`, which is outside the Stage 5 Task 2
  brief's allowed change set (`frontend/e2e/` and `migration/` only).
- Belongs naturally with whichever Stage 5 task formalises the
  end-to-end CI pipeline.

---

## F2 — `mcp-smoke.json` baseline drift handling

**Status:** Resolved in this fix-up round (round 1).
**Discovered by:** `task-2-review.md` items C1 + I1.

No follow-up action needed. Documented here so future readers know why
the baseline lists all 20 tools and the scenarios only exercise 5.

### What changed

- `baselineToolNames` now mirrors the canonical Stage 4 inventory
  (`src/OnToPilot/Mcp/OnToPilotMcpTools.cs:88-142` and
  `migration/baseline/mcp-tools-python.json`) exactly.
- Scenarios exercise 5 real tools: `tools/list` (discovery), `get_ontology`,
  `search_ontology`, `list_releases`, `preview_ontology_changes`.
- `defaultArguments` for `preview_ontology_changes` uses `operations: []`
  and asserts on the scalar `added_triples` field — matching the real
  schema at `OnToPilotMcpTools.cs:587-592`.

---

## F3 — Tail-snippet throw gate

**Status:** Resolved in this fix-up round (round 1).
**Discovered by:** `task-2-review.md` item I2.

No follow-up action needed.

### What changed

The verbatim tail snippet in `Test-McpEndpoint.ps1` previously threw
on any baseline drift regardless of `-FailOnUnapproved`. The throw is
now gated: hard fail when `-FailOnUnapproved` is set, `Write-Warning`
when it is not. The JSON report remains the source of truth for
soft-discovery runs.

---

## F4 — Optional follow-ups (from Minor + Suggestion sections)

These were raised by the reviewer but are **not blockers** for Task 2.
Track here so they don't get lost:

- **M1** — `logoutAsAdmin` clears all cookies instead of reading the
  Set-Cookie header to capture the session name. Acceptable proxy for
  the session spec; revisit if a stricter logout assertion is needed.
- **M2** — `setInputFiles("e2e/fixtures/pump.pdf")` is cwd-relative.
  Switch to `path.join(__dirname, "..", "fixtures", "pump.pdf")` once
  Playwright is wired (see F1).
- **M3** — `vocabulary.spec.ts` regex uses English-only selectors
  (`/open/i`, `/tab/`, etc.). Add an i18n-constants export from
  `frontend/src/lib/i18n.tsx` and reference it from the spec, or
  pin the suite to the English locale explicitly.
- **M4** — `pump.pdf` is a 597-byte minimum-viable PDF; it produces
  a "parsed" status without exercising real text extraction. Replace
  with a 1-page deterministic PDF once Stage 5 has a fixture helper.
- **S1** — Factor the runner's inventory + scenario + tail block into
  three small functions for readability. Defer until the next round of
  MCP runner edits.
- **S2** — Add a `# >>> DO NOT EDIT — pins the brief snippet verbatim`
  marker above the tail snippet so future refactors cannot move it into
  a helper. (Done in this round.)
- **S3** — Add a one-line README header to `frontend/e2e/fixtures/pump.pdf`
  documenting that it is the canonical upload target.
