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