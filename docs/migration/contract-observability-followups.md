# Contract & Observability — Open Follow-ups

This document tracks the **known-but-out-of-scope** work items surfaced by
the Stage 5 Task 2 review (`task-2-review.md`). Each item lives here so
that downstream agents (or a future Stage 5 task) can pick them up
without re-deriving the context from the review report.

> **Do NOT action these items as part of any Task 2 fix-up round.** They
> are explicitly deferred per the Stage 5 brief.

---

## F1 — Playwright project wiring

**Status:** Resolved.
**Discovered by:** `task-2-review.md` item I3 / Implementer concern #1.
**Resolved by:** Stage 5 F1 wiring slice (single commit, see
`git log -- frontend/playwright.config.ts`).

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

### What changed

1. **New** `frontend/playwright.config.ts` — defines `testDir: 'e2e'`
   with `testMatch: /e2e\/dotnet\/.*\.spec\.ts$/` so the runner scopes
   itself to the three .NET specs and leaves room for future
   `e2e/python/**` or `e2e/shadow/**` projects without config edits.
   Single Chromium project named `dot-net`. The `webServer` block is
   conditional on `DOTNET_BASE_URL` being unset: when a developer or
   CI pre-starts the backend, the config leaves it alone; otherwise
   Playwright boots `dotnet run --project ../src/OnToPilot --urls=http://+:18080`
   and waits for `/api/health` to respond before letting specs run.
2. **Modified** `frontend/package.json` — added `@playwright/test`
   `^1.50.0` to `devDependencies` (lockfile resolved to 1.62.1) plus
   two scripts: `test:e2e:dotnet` (`playwright test --project=dot-net`)
   and `test:e2e:dotnet:install` (`playwright install --with-deps chromium`).
3. **Modified** `frontend/README.md` — added an "End-to-end tests"
   section documenting the install + run sequence and explaining the
   `DOTNET_BASE_URL` override + `beforeAll` skip behavior so operators
   don't see a confusing run on a clean machine.

### Verification

- `pnpm exec playwright test e2e/dotnet --list` enumerates 3 specs
  (upload-extract-publish, vocabulary, session).
- With `DOTNET_BASE_URL` set to a non-running port,
  `pnpm exec playwright test e2e/dotnet` reports `3 skipped` — the
  `beforeAll` health-probe in `helpers/config.ts` short-circuits the
  suite, matching the plan's expected graceful-no-backend behavior.
- Without `DOTNET_BASE_URL`, the `webServer` block attempts
  `dotnet run`; if the local environment cannot satisfy the backend's
  required env vars (PG connection string, Oxigraph path, etc.) the
  webServer fails fast with the same DI errors a developer would see
  when starting the backend by hand — not a Playwright wiring failure.

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
