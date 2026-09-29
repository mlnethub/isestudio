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
| `pnpm test:e2e:dotnet --grep "ingestion sources"` | Passed, 2/2 in the latest isolated browser run: default folder Source has `supports_push_token=false` and no sync/token controls; virtual Folder view remains available; `azure_blob` is absent from the create menu and rejected by the API (400); a second folder Source can be selected for upload, and moving the document preserves its `source_id`. |
| `pnpm test` | Passed, 16 tests across 3 files. |
| `pnpm build` | Passed. Vite reports a large-chunk warning (>500 kB); production chunk splitting was not part of this acceptance. |
| `pnpm lint` | Passed with existing Fast Refresh warnings. |
| Source API/security unit-test filter (`SourceApiTests`, `SourceSecretProtectorTests`, `SourceNetworkPolicyTests`, `SourceAdapterTests`, `SourcePushTests`, `DocumentApiTests`) | Passed, 58/58. |
| Focused Source token API contract after capability DTO change | Passed: Editor-only access, sealed persistence, no-store reveal/rotate, normal-projection exclusion, and `supports_push_token` list/detail metadata. |
| Source/schema/sync integration-test filter (`KnowledgeSourceSchemaTests`, document version/blob tests, `SourceSyncTests`, `SourceSyncWorkerTests`, `SourceConnectorTests`) | Passed, 47/47; Testcontainers started PostgreSQL successfully. |
| `dotnet build src/ISEStudio.sln --no-restore` | Passed. |

The current server registers only the passive `folder` kind. Sync and push-token UI actions are capability-gated; `folder` exposes neither. The Source token API contract verifies Editor-only access, encrypted persistence, no-store responses, and exclusion from normal projections. A focused API test also verifies the `supports_push_token` list/detail capability flag for `api` versus `folder`. Token reveal/rotation browser behavior, live push, and credentialed connector E2E were not run because no push-capable kind is registered in this isolated deployment. Real Viewer-session UI restrictions remain unverified. `azure_blob` is unavailable. The browser test used the application's SQLite `EnsureCreated` path; a direct SQLite `--migrate` attempt failed on PostgreSQL-specific `CREATE FUNCTION` SQL, so that SQLite migration path is not verified. The broader `pnpm test:e2e:dotnet` workflow (upload through extract/review/publish) was not run in this isolated pass; only the Source UI contracts above were executed.
