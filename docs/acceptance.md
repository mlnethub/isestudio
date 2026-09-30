# Phase 1 Release Acceptance

## Product Goal

A new user can complete **upload → extract → review → publish → export** in ten minutes with the sample corpus.

## Timed Manual Checklist

| Target | Action | Pass condition |
| --- | --- | --- |
| 0:00–2:00 | Start Docker Compose and sign in | Health checks pass; first-run guide is visible |
| 2:00–3:00 | Verify model endpoints and create a knowledge system | Knowledge-system overview opens |
| 3:00–4:00 | Upload and parse `examples/pump-operations.txt` | Document has chunks and is selectable |
| 4:00–7:00 | Run combined TBox + ABox extraction | Job completes; TBox, terminology, and instances are populated |
| 7:00–8:30 | Process all four review queues | No blocking release-quality findings remain |
| 8:30–9:15 | Create and approve a release draft, then publish | Version status is `published` |
| 9:15–10:00 | Export the complete release | Manifest, three RDF layers, and provenance files download successfully |

## Automated Gates

```bash
dotnet test src/ISEStudio.Tests
dotnet test src/ISEStudio.ApiContract.Tests

cd ../frontend
pnpm build

cd ..
docker compose config --quiet
```

## Current Isolated Smoke Result

The deterministic demo path was executed with an isolated SQLite database and Oxigraph directory:

- release quality gate: 0 blocking findings;
- TBox snapshot: 26 statements;
- terminology snapshot: 69 statements;
- ABox snapshot: 9 statements;
- complete bundle: 10 uncompressed artifacts;
- manifest and checksum presence: passed.

Container image execution still requires a running Docker engine on the verification host.

## Ingestion Source UI Acceptance

The Source UI browser contracts were run against a per-run SQLite database in the `Testing` environment. The API created its test schema with `EnsureCreated`; a random admin password and source-encryption key were process-only values and were not recorded. Playwright served the SPA from Vite and sent API traffic through Vite to a dedicated isolated .NET API port. The temporary database, process variables, API process, and failed-run trace artifacts were removed after verification; the Compose services and their data volumes were not used.

| Check | Result |
| --- | --- |
| `pnpm exec playwright test --project=dot-net e2e/dotnet/ingestion-sources.spec.ts e2e/dotnet/session.spec.ts e2e/dotnet/vocabulary.spec.ts` | Passed, 5/5 against the isolated SQLite API: three Source contracts, session login/logout, and vocabulary create/edit/delete. The Viewer contract verifies Sources are readable but create/edit/delete/sync/token controls and document upload/source-picker/drop affordances are absent; a user without KS membership receives 403. |
| Source UI browser contracts | Passed, 3/3: default folder Source capability/status and virtual-folder separation; source-aware upload and folder move preserve `source_id`; real Viewer and non-member sessions enforce the UI/access contract. |
| Vocabulary CRUD browser contract | Passed: the E2E now creates a scheme fixture on the selected temporary KS; the frontend maps preferred-label fields to the .NET DTO and sends concept IRIs in update/delete bodies. The preferred-term input is associated with its accessible label. |
| `pnpm test` | Passed, 16 tests across 3 files. |
| `pnpm build` | Passed. Vite reports a large-chunk warning (>500 kB); production chunk splitting was not part of this acceptance. |
| `pnpm lint` | Passed with existing Fast Refresh warnings. |
| Source API/security unit-test filter (`SourceApiTests`, `SourceSecretProtectorTests`, `SourceNetworkPolicyTests`, `SourceAdapterTests`, `SourcePushTests`, `DocumentApiTests`) | Passed, 58/58. |
| Focused Source token API contract after capability DTO change | Passed: Editor-only access, sealed persistence, no-store reveal/rotate, normal-projection exclusion, and `supports_push_token` list/detail metadata. |
| Source/schema/sync integration-test filter (`KnowledgeSourceSchemaTests`, document version/blob tests, `SourceSyncTests`, `SourceSyncWorkerTests`, `SourceConnectorTests`) | Passed, 47/47; Testcontainers started PostgreSQL successfully. |
| `dotnet build src/ISEStudio.sln --no-restore` | Passed. |

The current server registers only the passive `folder` kind. Sync and push-token UI actions are capability-gated; `folder` exposes neither. The Source token API contract verifies Editor-only access, encrypted persistence, no-store responses, and exclusion from normal projections. A focused API test also verifies the `supports_push_token` list/detail capability flag for `api` versus `folder`. Token reveal/rotation browser behavior, live push, and credentialed connector E2E were not run because no push-capable kind is registered in this isolated deployment. `azure_blob` is unavailable. The browser tests used the application's SQLite `EnsureCreated` path; a direct SQLite `--migrate` attempt failed on PostgreSQL-specific `CREATE FUNCTION` SQL, so that SQLite migration path is not verified. The upload → extract → review → publish Playwright workflow was not run because this isolated environment has no configured model provider; no extraction result is claimed. The isolated browser verification did not connect to or modify the Compose services or their data volumes.
