# Production Cutover Runbook

> **Audience:** Authorized operator with shell access to the production
> OnToPilot host and read access to the backup volume. **This
> runbook is for the .NET cutover only**; the Python backend keeps
> running until step 5.

This runbook walks the operator through the production cutover from
the .NET / Oxigraph / PostgreSQL stack to the .NET / EF Core /
Oxigraph / MinIO stack. It enforces the hard preflight gates defined
by Stage 6 Task 4 of the data-cutover plan; every gate must pass in
order, and any failure aborts immediately.

## Prerequisites

1. **Rehearsal evidence.** A green run of
   `Invoke-MigrationRehearsal.ps1` against the most recent production
   backup, with the resulting `migration-report.json` attached to the
   change ticket.
2. **Authorized operator.** The operator must be named on the change
   ticket, must have a valid SSH key, and must have on-call pager
   credentials. The cutover cannot be triggered by a CI bot.
3. **Cutover record.** A filled-in copy of
   `production-cutover-record.template.md` saved as
   `migration/runbooks/production-cutover-record.md`. The script will
   refuse to start if any required field is missing.
4. **Communications channel.** A dedicated incident channel where
   failures and rollback decisions can be posted in real time.

## Preflight command list

These commands must all succeed (exit 0) before step 1 of the
cutover sequence:

```bash
# Verify the production backup is intact and matches its SHA-256
# sidecar.
sha256sum -c /var/backups/ontopilot/$(date -u +%Y-%m-%d).sha256

# Confirm the Python backend is still running (it should be — we
# stop it as gate 1 of the cutover).
systemctl status ontopilot-python-backend

# Confirm PostgreSQL is accepting writes (we revoke writes as gate 2).
psql -h $PGHOST -U $PGUSER -c "SELECT pg_is_in_recovery();"

# Confirm the .NET build artifacts are present.
test -f src/OnToPilot.WebHost/bin/Release/net8.0/OnToPilot.WebHost.dll

# Confirm the migration CLI is built.
test -f src/OnToPilot.Migration/bin/Release/net8.0/OnToPilot.Migration.dll
```

## Cutover sequence

The script `Invoke-ProductionCutover.ps1` walks the following gates
in order. Each gate is a hard preflight — a failure stops the
sequence with a non-zero exit code and a descriptive message.

| # | Gate                        | Exit on failure |
|---|-----------------------------|-----------------|
| 0 | Assert-CutoverRecord        | 1               |
| 1 | Assert-PythonBackendStopped | 1               |
| 2 | Assert-DatabaseWriteFreeze  | 1               |
| 3 | Assert-VerifiedBackup       | 1               |
| 4 | Invoke-RdfCopyVerification  | 2               |
| 5 | Invoke-BlobMigration        | 2               |
| 6 | Invoke-SqlMigration         | 2               |
| 7 | Assert-AllMigrationManifests| 3               |
| 8 | Start-DotNetBackend         | 1               |
| 9 | Invoke-PostCutoverSmoke     | 4               |

```bash
# Step 0: stop the Python backend. This is gate 1 — until this is
# done the cutover will refuse to start.
sudo systemctl stop ontopilot-python-backend

# Step 0: revoke PostgreSQL write permissions. This is gate 2 — until
# this is done the cutover will refuse to start.
sudo -u postgres psql -c "REVOKE INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public FROM ontopilot_app;"
touch /etc/ontopilot/db-write-frozen

# Step 1: run the cutover.
pwsh migration/scripts/Invoke-ProductionCutover.ps1 \
    -Record migration/runbooks/production-cutover-record.md \
    -RdfSource backend/data/oxigraph \
    -RdfCopy /var/lib/ontopilot/oxigraph-dotnet \
    -RdfWork /var/lib/ontopilot/oxigraph-work \
    -BlobSource backend/data/blobs \
    -BlobBucket ontopilot-blobs \
    -MinioEndpoint "$MINIO_ENDPOINT" \
    -MinioAccessKey "$MINIO_ACCESS_KEY" \
    -MinioSecretKey "$MINIO_SECRET_KEY" \
    -PostgresConnectionString "$PG_CONNECTION_STRING"
```

## Verification steps

After the cutover reports exit 0:

1. **Smoke tests.** The script ran `Invoke-PostCutoverSmoke`. Verify
   the smoke output is empty (no failures). Spot-check that
   `Test-McpEndpoint.ps1` returned 0 and `Test-RdfParity.ps1`
   reported a manifest hash equal to the rehearsal's.
2. **Knowledge system reads.** Issue a known-good KS read through
   the API. Confirm the response is byte-identical to the rehearsal
   capture.
3. **Document download.** Pick three documents at random and verify
   their bytes round-trip through the .NET stack and MinIO.
4. **Audit log.** Confirm new audit events are landing in
   PostgreSQL (gate 2 was lifted by the .NET host; this proves
   write perms were correctly restored by the .NET backend's
   migrations runner, not by anything we did manually).

## What to do if a gate fails

**DO NOT PROCEED.** Every gate failure is a hard stop:

1. Note the gate name and the exit code from the cutover script.
2. Open the cutover record and append the failure timestamp +
   message.
3. **Consult `production-rollback.md` if any migration gate (4–7)
   fails.** Recovery from a half-migrated state is the rollback
   path's job, not the cutover path's.
4. **Re-run the rehearsal** to confirm the underlying problem is
   fixed before the next cutover attempt.
5. Do not re-run the cutover script after a failed gate. The script
   has no idempotent retry path; re-running risks data corruption.

## 24-hour observation checklist

The cutover script ends with the message `All gates passed. 24h
observation window starts.` From that moment on:

- [ ] **Hour 0:** Post the cutover timestamp + operator signature in
  the incident channel. Confirm the audit log has new rows.
- [ ] **Hour 1:** Re-run `Test-McpEndpoint.ps1` and
  `Test-RdfParity.ps1` from a separate workstation. Confirm the
  manifest hash matches the cutover record.
- [ ] **Hour 4:** Spot-check 10 random documents end-to-end
  (download, checksum, render).
- [ ] **Hour 8:** Verify release-artifact objects (the orphans the
  blob migration skipped) are still on the local filesystem under
  `backend/data/blobs/`. The MinIO bucket should NOT contain them.
- [ ] **Hour 12:** Check Postgres write permissions were restored by
  the .NET host (audit table has new INSERTs).
- [ ] **Hour 24:** Run `Complete-Observation.ps1 -Record ...`. If
  the post-cutover smoke is still green and the manifest hashes
  match the cutover record, observation is complete. Mark the
  backup with a 30-day retention marker.

## Rollback triggers

Open the rollback runbook (`production-rollback.md`) and execute it
**immediately** if any of the following is true at any point during
the 24-hour window:

1. **KS read smoke fails** for any document that the rehearsal
   captured a green hash for.
2. **RDF query result hash** differs from the rehearsal hash for
   any of the four smoke queries (`all-quads`, `tbox-only`,
   `abox-only`, `count-by-graph`).
3. **Blob checksum mismatch** when downloading a document and
   comparing to the cutover manifest.
4. **PostgreSQL audit log has no new rows** 15 minutes after the
   cutover ended (the .NET host is not writing).
5. **Operator judgement** — if anything looks wrong, rollback. The
   backup is kept for 30 days; we can retry.

## Post-cutover cleanup (after 24h success)

```bash
# Mark the backup for 30-day retention so a future re-cutover has a
# known-good baseline.
pwsh migration/scripts/Complete-Observation.ps1 \
    -Record migration/runbooks/production-cutover-record.md

# Archive the cutover record + smoke output into the change ticket.
mv migration/runbooks/production-cutover-record.md \
   /var/lib/ontopilot/archives/cutover-$(date -u +%Y-%m-%d).md
```