# IRI Prefix Migration Runbook

> Phase 2 cutover runbook for the `http://ontopilot.local/` →
> `http://goodcrew.local/` rename. Companion to
> `production-cutover.md`; this document owns the IRI-specific
> sequence and the rehearsal / rollback boundary.
>
> **Read first**:
> - [production-cutover.md](production-cutover.md) — gates 1-13
>   (this runbook inserts IRI steps between gate 7 and gate 11).
> - [production-rollback.md](production-rollback.md) — code-layer
>   rollback only; **the IRI data rewrite is one-way** by design.
> - [Plan file](../../C:/Users/geffz/.claude/plans/majestic-sparking-crescent.md)
>   — the IRI migration design doc (constraints, rollback policy).

---

## Scope

The IRI prefix rename touches three layers of OnToPilot's runtime state:

1. **SQL columns** (10 IRI-bearing columns across 6 tables).
2. **Oxigraph RocksDB workspace** (every named graph's quads carry
   IRIs in subject / predicate / object / graph positions).
3. **On-disk N-Quads shards + manifests** (`{releasesRoot}/*.nq` and
   `{exportsRoot}/*.nq` carry IRI literals; `manifest.json` carries
   SHA-256 entries that must be refreshed after a rewrite).

A fourth layer — Python `Settings.iri_root` /
`Settings.vocab_namespace` — is changed **before** the cutover (see
Task 5). The cutover itself is a one-way data rewrite; rollback is a
code-layer rollback only.

## Constraints (verbatim from plan)

- **One-way rewrite.** The migration CLI does not ship a reverse
  REPLACE. To roll back, revert `OnToPilotOptions.IriRoot` /
  `VocabNamespace` to `http://ontopilot.local/` and redeploy the
  .NET backend — the data layer keeps the new IRIs.
- **Cutover-only.** The IRI rewrite runs only during the production
  cutover window. There is no background / online path because the
  workspace, the SQL columns, and the shards all share the same
  legacy prefix and must land at the new prefix together.
- **No concurrent writes.** Python backend must be stopped (Gate 2)
  and PostgreSQL write permissions must be revoked (Gate 3) before
  the IRI gates run.
- **Rehearsal mandatory.** `Invoke-MigrationRehearsal.ps1` must run
  against the most recent production backup before the IRI gates
  are called in production; the rehearsal evidence is captured in the
  cutover record's `expected-iri-checksums` block.

---

## Phase 2 sequence (gates 8 / 9 / 10 in production-cutover.md)

The cutover orchestrator runs these three IRI gates in order between
the legacy SQL GUID/LegacyId migration (gate 7) and the manifest
content-validation gate (gate 11). Each gate is independently
mockable and the order is strict — failing the SQL gate aborts
before any RDF or shard work begins.

| Gate | Script | Target | Failure mode |
|------|--------|--------|--------------|
| 8 | `Invoke-IriSqlMigration.ps1` | 10 SQL columns + `fact_key` | exit 2 |
| 9 | `Invoke-IriRdfRelocation.ps1` | Oxigraph RocksDB workspace | exit 2 |
| 10 | `Invoke-IriShardRewrite.ps1` | N-Quads shards + manifests | exit 2 |

All three gates share the same `FromPrefix` /
`ToPrefix` defaults; override via `-IriFromPrefix` /
`-IriToPrefix` on `Invoke-ProductionCutover.ps1` if a different
namespace pair is being cut over.

### Gate 8 — `Invoke-IriSqlMigration`

```powershell
pwsh migration/scripts/Invoke-IriSqlMigration.ps1 `
    -PostgresConnectionString "Host=...;Username=postgres;Password=...;Database=ontopilot" `
    -FromPrefix "http://ontopilot.local/" `
    -ToPrefix   "http://goodcrew.local/"
```

The migrator emits one line per column with the affected row count
on stdout:

```
[iri-migration] sql: knowledge_systems.graph_iri = 17 rows
[iri-migration] sql: knowledge_systems.base_iri   = 17 rows
[iri-migration] sql: release_deployment.tbox_graph_iri = 14 rows
[iri-migration] sql: ...
```

The cutover operator captures the per-table totals and copies them
into the `expected-iri-sql-row-counts` block of the cutover record.

### Gate 9 — `Invoke-IriRdfRelocation`

```powershell
pwsh migration/scripts/Invoke-IriRdfRelocation.ps1 `
    -Source "/var/lib/ontopilot/oxigraph-readonly" `
    -Target "/var/lib/ontopilot/oxigraph-iri-stage" `
    -FromPrefix "http://ontopilot.local/" `
    -ToPrefix   "http://goodcrew.local/"
```

The relocator opens the source RocksDB read-only, enumerates every
named graph, rewrites the prefix in the dumped N-Quads, and bulk-
loads the rewritten quads into a fresh target directory. The
operator then `mv`s (or symlink-flips) the stage directory into the
live workspace path once gates 8 and 10 succeed.

**Important**: Oxigraph 0.5.8's RocksDB writer defers some WAL
compaction work past `Dispose`, so the relocator's read-only handle
must open the source path **after** the Python backend has been
stopped for at least a few seconds (gate 2's stop-script waits 10
seconds before flipping back). The rehearsal evidence will show
whether your specific source directory is stable enough; if not,
introduce a longer post-stop pause.

### Gate 10 — `Invoke-IriShardRewrite`

```powershell
pwsh migration/scripts/Invoke-IriShardRewrite.ps1 `
    -ReleasesRoot "/var/lib/ontopilot/releases" `
    -ExportsRoot  "/var/lib/ontopilot/exports" `
    -FromPrefix   "http://ontopilot.local/" `
    -ToPrefix     "http://goodcrew.local/"
```

The rewriter walks `{releasesRoot}/{releaseId}/{tbox,vocabulary,abox}.nq`
+ `ks.json` + `manifest.json` and `{exportsRoot}/{publicId}/{jobLegacyId}/*.nq`.
The IRI replace is anchored to the `<...>` N-Quads IRI delimiters so
substring matches inside string literals can't accidentally rewrite.
Each touched file's SHA-256 is recomputed and the value pushed back
into the corresponding `manifest.json` entry — this keeps the
manifest byte-consistent with the files it describes.

Dry-run mode (`-DryRun`) walks the tree and reports what would change
without writing. Use it for the rehearsal and the first cutover
preview; the production cutover runs apply mode.

---

## Cutover record additions

The cutover record (`production-cutover-record.template.md`) gains
three new blocks alongside the existing `expected-sql-checksums` /
`expected-rdf-query-hashes`:

```yaml
- expected-iri-sql-row-counts:
  - knowledge_systems.graph_iri = 17
  - knowledge_systems.base_iri = 17
  - release_deployment.tbox_graph_iri = 14
  - ...
- expected-iri-rdf-quad-count: <int>
- expected-iri-rdf-manifest-sha256: <64-char lowercase hex>
- expected-iri-shard-count: <int>
```

The row counts and quad counts come from the rehearsal run (they
must equal the production run; the rehearsal uses the most recent
production backup). The shard count is the total `*.nq` files
under `releasesRoot` + `exportsRoot`.

---

## Rehearsal — first run

`Invoke-MigrationRehearsal.ps1` already loads the cutover record
template and runs every gate in dry-run mode. Phase 2's IRI work
adds three new rehearsal-only outputs to the rehearsal artefact:

- `artifacts/rehearsal-iri-sql.json` — per-column affected-row counts
  for the IRI SQL REPLACE.
- `artifacts/rehearsal-iri-rdf.json` — per-graph quad counts + SHA-256
  of the rewritten N-Quads.
- `artifacts/rehearsal-iri-shards.json` — per-shard SHA-256 before
  and after the rewrite.

The rehearsal's outputs feed the cutover record's
`expected-iri-...` blocks. The operator copies the values verbatim
into the cutover record before signing.

---

## 24-hour observation

The post-cutover observation checklist
([Complete-Observation.ps1](../scripts/Complete-Observation.ps1))
gains three IRI-specific checks at the 1-hour and 4-hour marks:

- **Hour 1**: `Test-RdfParity.ps1` re-run. The RDF manifest
  `QuadSetHash` should match `expected-iri-rdf-manifest-sha256`
  in the cutover record.
- **Hour 4**: 10 random KS end-to-end smoke. Each KS's `graph_iri`
  / `base_iri` in PostgreSQL, the Oxigraph named graphs, and the
  on-disk shards must all carry the `goodcrew.local/` prefix with
  no legacy `ontopilot.local/` references.
- **Hour 12**: cross-layer consistency gate — `SELECT DISTINCT
  graph_iri FROM knowledge_systems` (Postgres) intersected with the
  Oxigraph named-graph set via `Match(...)` enumeration must contain
  zero `http://ontopilot.local/` entries.

---

## Rollback

**There is no automatic data-layer rollback.** The IRI rewrite is
one-way by design (Phase 0 user decision).

To roll back a botched IRI cutover:

1. Stop the .NET backend (gate 12 already starts it; `Stop-DotNetBackend`
   from `CutoverGates.ps1` flips back).
3. Restore the production backup taken before the IRI rewrites
   (Gate 4 verifies this backup's SHA-256 sidecar).
4. Re-deploy the .NET backend with `IriRoot = "http://ontopilot.local/"`
   / `VocabNamespace = "http://ontopilot.local/vocab#"` so the
   code layer expects the legacy prefix.
5. Re-start the Python backend.
6. File a post-mortem and re-schedule the cutover after the
   root-cause fix.

The IRI migration manifests remain on disk and may be inspected by
the post-mortem — they record what was rewritten, when, and from
which prefix to which prefix.