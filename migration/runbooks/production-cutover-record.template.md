# Production Cutover Record — TEMPLATE

> The operator copies this template to
> `migration/runbooks/production-cutover-record.md`, fills in every
> field, and signs it **before** running
> `Invoke-ProductionCutover.ps1`. The cutover script will refuse to
> start if any field is missing, blank, or malformed. The script
> also performs full content validation against every migration
> manifest; mismatches abort with exit code 3.

## Required fields (script-enforced)

```
- Cutover start (UTC): YYYY-MM-DDTHH:MM:SSZ
- Backup path: /var/backups/ontopilot/YYYY-MM-DD
- Backup SHA-256: <64-char lowercase hex>
- Operator signature: <full name + change ticket id>
- Original RDF dir: /var/lib/ontopilot/oxigraph-readonly
- MinIO endpoint: http://127.0.0.1:9000
- MinIO bucket: ontopilot-blobs
- Expected post-cutover manifest checksums:
  - SQL verify summary: <64-char lowercase hex>
  - RDF verify summary: <64-char lowercase hex>
  - blob verify summary: <64-char lowercase hex>
- expected-sql-manifest-sha256: <64-char lowercase hex>
- expected-rdf-manifest-sha256: <64-char lowercase hex>
- expected-blob-manifest-sha256: <64-char lowercase hex>
- expected-sql-checksums:
  - users = <32-char lowercase md5 hex>
  - document = <32-char lowercase md5 hex>
  - chunk = <32-char lowercase md5 hex>
  - knowledgesystem = <32-char lowercase md5 hex>
  - extractionjob = <32-char lowercase md5 hex>
  - audit_event = <32-char lowercase md5 hex>
  - ontologyrelease = <32-char lowercase md5 hex>
  - releasedeployment = <32-char lowercase md5 hex>
- expected-rdf-query-hashes:
  - all-quads = <64-char lowercase hex>
  - tbox-only = <64-char lowercase hex>
  - abox-only = <64-char lowercase hex>
  - count-by-graph = <64-char lowercase hex>
- expected-iri-from-prefix: http://ontopilot.local/
- expected-iri-to-prefix:   http://goodcrew.local/
- expected-iri-sql-row-counts:
  - knowledge_systems.graph_iri = <int>
  - knowledge_systems.base_iri = <int>
  - release_deployment.tbox_graph_iri = <int>
  - release_deployment.vocabulary_graph_iri = <int>
  - release_deployment.abox_graph_iri = <int>
  - entity_resolution.class_iri = <int>
  - entity_resolution.individual_iri = <int>
  - tbox_reconciliation.property_iri = <int>
  - validation_decision.property_iri = <int>
  - abox_provenance.fact_key = <int>
- expected-iri-rdf-quad-count: <int>
- expected-iri-rdf-manifest-sha256: <64-char lowercase hex>
- expected-iri-shard-count: <int>
```

> ⚠️ Every line above is parsed by the cutover script. Do not change
> the field names; do not add commentary between fields. The script
> uses simple regex matches and a missing `-` prefix will cause
> `Test-CutoverRecord` to return `$false` and abort the cutover.

## Validation gates (script-enforced)

The `Assert-AllMigrationManifests` gate performs six checks per
manifest before the cutover can proceed:

1. **File existence.** `<path>/sql-migration-log.json`,
   `<path>/rdf-manifest.json`, `<path>/blob-manifest.json`.
2. **JSON parse.** `ConvertFrom-Json` succeeds; malformed JSON
   throws with the file path.
3. **JSON Schema validation.** Each manifest is validated against
   `migration/manifests/sql-migration-log.schema.json` /
   `rdf-manifest.schema.json` / `blob-manifest.schema.json`.
4. **Business checks:**
   - SQL: every `VerifySummary.Rows[*].OrphanCount == 0`. Every
     `BusinessChecksum` matches the value in
     `expected-sql-checksums`.
   - RDF: `WriteRevertPassed == true`, `QuadCount > 0`, every
     `QueryResultHashes[*]` matches `expected-rdf-query-hashes`.
   - Blob: every `entries[*].sha256` is 64 lowercase hex chars.
     Every `entries[*].size` matches the actual MinIO object size
     via a HEAD request to `<MinIO endpoint>/<bucket>/<objectKey>`.
5. **Canonical SHA-256 chain.** Each manifest file is re-serialised
   with sorted keys / no whitespace and hashed; the SHA must match
   `expected-<type>-manifest-sha256`.
6. **No silent bypass.** If MinIO endpoint / bucket is missing, the
   gate **throws** rather than skipping the per-object HEAD check.

## Optional context (operator notes)

Use the space below for any context the next operator needs to know
(change ticket link, on-call rotation, etc.). The cutover script
ignores this section.

<!--
Example:
- Change ticket: CHG-12345
- On-call: ops@example.com / +1-555-555-5555
- Rehearsal evidence: .artifacts/rehearsal-2026-08-18.json
-->

---

## How to populate the template

1. **Run the rehearsal** against the most recent production backup:
   ```bash
   pwsh migration/scripts/Invoke-MigrationRehearsal.ps1 \
       -BackupPath /var/backups/ontopilot/$(date -u +%Y-%m-%d) \
       -ReportPath .artifacts/rehearsal-$(date -u +%Y-%m-%d).json
   ```
   The rehearsal writes the SQL/RDF/blob manifest paths and their
   canonical SHA-256s into the report file. Capture the
   `BusinessChecksum` per table and the `QueryResultHashes` per
   query from the SQL/RDF manifests and copy them into the
   `expected-sql-checksums` / `expected-rdf-query-hashes` blocks.

2. **Compute the backup SHA-256.** Use the same `sha256sum` invocation
   the rehearsal script uses:
   ```bash
   find /var/backups/ontopilot/$(date -u +%Y-%m-%d) -type f \
        -exec sha256sum {} + | sha256sum
   ```

3. **Copy the SQL/RDF/blob canonical SHA-256s** from the rehearsal
   report into the three `expected-<type>-manifest-sha256` lines.

4. **Sign.** Replace `<full name + change ticket id>` with the
   operator's full legal name and the change ticket id. The
   signature is part of the post-mortem evidence.

5. **Save** as `migration/runbooks/production-cutover-record.md` and
   commit it to the change-ticket branch (the file is intentionally
   excluded from `.gitignore` so the record is part of the audit
   trail).