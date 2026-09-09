# Production Cutover Record

This template is completed by `Invoke-ProductionCutover.ps1`. It is an observation record, not a production authorization.

## Required evidence

- Date/time (UTC):
- Operator/change reference:
- Verified backup manifest path and SHA-256:
- Rehearsal manifest path and SHA-256:
- Smoke base URL:
- Machine-readable state file:
- Result: `passed` / `failed`

## Ordered gates

1. Stop Rust writes:
2. Verify database write freeze:
3. Verify backup:
4. Rehearsal gate:
5. RDF check:
6. Blob check:
7. SQL check:
8. Start ISEStudio:
9. Post-cutover smoke (health, ingestion, graph, search, MCP):

## Notes

Do not include passwords, bearer tokens, connection strings, object-store access keys, or secret values in this document.

## Runtime RDF dependency boundary (Oxigraph removal, 2026-09-09)

The cutover is PostgreSQL-authoritative for all runtime RDF state:

- Workspace layers (TBox / ABox / vocabulary) live in `WorkspaceStatementEntity`
  rows accessed through `IRdfStatementRepository`; the runtime never opens an
  on-disk embedded graph store, and startup does not create `data/rdf`
  (guarded by the host smoke test).
- Releases serve from `ReleaseStatementEntity`; capture still writes immutable
  N-Quads shards to the artifact store.
- `scripts/verify-postgresql-authoritative-storage.ps1` enforces that
  `src/ISEStudio` contains no Oxigraph / RocksDB / StoreWrapper reference —
  not even in comments.

Explicit migration exception: `ISEStudio.Migration` and
`ISEStudio.OxigraphProbe` may reference Oxigraph because neither is hosted by
the runtime. Migration retains direct RocksDB validation of legacy data and an
N-Quads fallback path; it is a one-time reader, never part of the serving
pipeline.
