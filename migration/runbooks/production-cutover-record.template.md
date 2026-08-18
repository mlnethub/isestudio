# Production Cutover Record — TEMPLATE

> The operator copies this template to
> `migration/runbooks/production-cutover-record.md`, fills in every
> field, and signs it **before** running
> `Invoke-ProductionCutover.ps1`. The cutover script will refuse to
> start if any field is missing, blank, or malformed. The script
> also cross-checks the recorded manifest checksums against the live
> migration manifests; mismatches abort with exit code 3.

## Required fields (script-enforced)

```
- Cutover start (UTC): YYYY-MM-DDTHH:MM:SSZ
- Backup path: /var/backups/ontopilot/YYYY-MM-DD
- Backup SHA-256: <64-char lowercase hex>
- Operator signature: <full name + change ticket id>
- Original RDF dir: /var/lib/ontopilot/oxigraph-readonly
- Expected post-cutover manifest checksums:
  - SQL verify summary: <64-char lowercase hex>
  - RDF verify summary: <64-char lowercase hex>
  - blob verify summary: <64-char lowercase hex>
```

> ⚠️ Every line above is parsed by the cutover script. Do not change
> the field names; do not add commentary between fields. The script
> uses simple regex matches and a missing `-` prefix will cause
> `Test-CutoverRecord` to return `$false` and abort the cutover.

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
   SHA-256s into the report file.

2. **Compute the backup SHA-256.** Use the same `sha256sum` invocation
   the rehearsal script uses:
   ```bash
   find /var/backups/ontopilot/$(date -u +%Y-%m-%d) -type f \
        -exec sha256sum {} + | sha256sum
   ```

3. **Copy the SQL/RDF/blob SHA-256s** from the rehearsal report into
   the "Expected post-cutover manifest checksums" block above.

4. **Sign.** Replace `<full name + change ticket id>` with the
   operator's full legal name and the change ticket id. The
   signature is part of the post-mortem evidence.

5. **Save** as `migration/runbooks/production-cutover-record.md` and
   commit it to the change-ticket branch (the file is intentionally
   excluded from `.gitignore` so the record is part of the audit
   trail).