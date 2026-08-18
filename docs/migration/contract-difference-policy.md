# Contract Difference Policy

> Stage 5 plan reference: `docs/superpowers/plans/2026-08-16-ontopilot-dotnet-contract-observability.md` (Task 1)
>
> Companion artifacts: `migration/contracts/scenarios.json`, `migration/contracts/normalization.json`, `migration/scripts/Invoke-ContractComparison.ps1`, `src/OnToPilot.ApiContract.Tests/DifferentialContractTests.cs`.

## Purpose

The differential contract runner fires the same scenario against the frozen Python backend and the .NET backend and reports any structural divergence. This document defines what counts as an **approved** vs **unapproved** difference and how the runner enforces the policy.

The policy is intentionally strict: anything not explicitly approved below is treated as a contract regression and fails the runner (`-FailOnUnapproved` exits 2). Adding a new approved category requires editing `migration/contracts/normalization.json` AND updating this document in the same commit.

## Allowed differences (the allowlist)

The runner's normaliser (`OnToPilot.ApiContract.Tests.Differential.Normalizer`, mirrored in `Invoke-ContractComparison.ps1`) recursively strips exactly the keys listed below before comparing bodies. Anything else produces an unapproved diff.

| Category                | Examples                                              | Rationale                                              |
| ----------------------- | ----------------------------------------------------- | ------------------------------------------------------ |
| Timestamps              | `created_at`, `updated_at`, `deleted_at`, `timestamp`, `ts`, `*_at` | Wall-clock values legitimately differ run-to-run.       |
| Trace / correlation IDs | `trace_id`, `request_id`, `correlation_id`, `session_id` | Generated per request; never meaningful for parity.      |
| HTTP cache validators   | `etag`, `last_modified`                              | Implementation-specific, not part of the contract.      |
| Opaque tokens (exact)   | `token`, `access_token`, `refresh_token`, `trace_token`, `session_token`, `bearer_token`, `api_key`, `password` | Run-to-run secrets must not leak into the diff.        |
| Opaque tokens (wildcard)| `*_token`, `*_secret`                                | Catch-all for future token suffixes without re-editing the runner. |

The allowlist is **allowlist-only**. The normaliser never strips a key not listed above. In particular:

- `id`, `name`, `username`, `email`, `status`, `kind`, `role`, `score`, and any other business field are **never** stripped.
- The normaliser never coerces a value (no timestamp-to-epoch rewrites, no string trimming, no case folding). If both backends render a value differently, that is an unapproved diff and must be reconciled in source.

The allowlist file (`migration/contracts/normalization.json`) is the single source of truth. The xUnit contract tests (`DifferentialContractTests`) pin down the runtime semantics; the PowerShell runner ports the same logic. **If you change one, change the other and add a sibling test.**

### Headers

The runner compares only the headers named in each scenario's `compareHeaders` array (e.g. `content-type`). The following headers are explicitly excluded from comparison because they legitimately differ run-to-run:

- `date`
- `x-request-id`, `x-trace-id`, `x-correlation-id`
- `set-cookie` (session cookies carry the bootstrap session id; compare only via the `auth` channel)
- `x-ratelimit-remaining`, `x-ratelimit-reset`

If a scenario needs to compare a header that legitimately varies, declare it in `headerAllowlist` inside `normalization.json` so the runner knows to skip it without flagging an unapproved diff.

## Denied differences (unapproved)

Any of the following flips a scenario to `unapproved: true` and fails `-FailOnUnapproved`:

1. **Status code mismatch.** `python.status` ≠ `dotnet.status`, or either side disagrees with the scenario's `expectedStatus`.
2. **Body divergence after normalisation.** Once the allowlist has stripped dynamic fields, the two bodies must serialise to the same compact JSON.
3. **Type drift.** A field that is `string` in one backend and `number`/`boolean`/`null`/`array`/`object` in the other — the normaliser does not coerce types, so this surfaces as a body divergence.
4. **Missing field.** A business field the Python baseline documents is absent from the .NET response (or vice versa).
5. **Excess field.** A field that exists on one side but not the other and is not on the allowlist. Add the field name to the scenario or the source spec; do not add it to the allowlist "just to make the diff go away".
6. **Header drift on `compareHeaders`.** Any header listed in the scenario that differs between backends. The runner reports the diff with the actual values from both sides.
7. **Transport error.** Either backend returns a connection error, timeout, or non-HTTP response. Investigate; do not mark it as a known flakiness without a code change.

## Sandbox guarantees

- **No shared state.** The runner never accepts a filesystem path on the command line. Both backends are isolated HTTP endpoints, so the global constraint that the two backends must not share a RocksDB directory is preserved by construction.
- **No secret leakage.** The runner does not write the captured request body, response body, session cookie, or `Authorization` header to its output. The diff report carries only the structural comparison (status, header subset, normalised body equality flag, and the failure reason). The PowerShell script also never `Write-Host`s the captured body.
- **Health endpoint is pinned.** The health probe is fixed to `/api/health`. The scenarios file documents this so a regression to `/health` is caught immediately.

## Adding a new scenario

1. Add an entry to `migration/contracts/scenarios.json` with the required fields (`name`, `method`, `path`, `auth`, `compareHeaders`, `expectedStatus`).
2. If the operation requires path parameters, declare them in `pathParameters` (e.g. `{"ks_id": "1"}`).
3. If the scenario legitimately needs a header that varies, add the header to the scenario's `compareHeaders` AND to `headerAllowlist` in `normalization.json` with a comment explaining why.
4. If the scenario legitimately needs a body field that varies, add the field name to `allowlist` in `normalization.json` and document the rationale here.
5. Run the runner in dry-run mode first (`-DryRun`) to confirm the report structure parses.
6. Then run the live runner (`pwsh migration/scripts/Invoke-ContractComparison.ps1 -PythonUrl ... -DotNetUrl ... -FailOnUnapproved`) and verify the report's `summary.unapproved` count is zero.

## Review checklist

Before approving a new normaliser entry:

- [ ] The field is dynamic (generated per-request) or otherwise known to vary run-to-run.
- [ ] The field is not a business field — ask "would a product manager care if this value differed?". If yes, do not add it.
- [ ] The wildcards do not over-match. `*_token` is fine; `*` is not.
- [ ] A sibling xUnit test in `DifferentialContractTests` exercises the new allowlist entry.
