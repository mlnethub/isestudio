# Production Rollback Runbook

> **Audience:** Authorized operator with shell access to the production
> OnToPilot host. **This runbook restores the Python backend as the
> authoritative service.** Use it whenever a rollback trigger fires
> during the cutover itself or during the 24-hour observation window.

## When to roll back

Roll back **immediately** if any of the following is true:

1. **During the cutover sequence:** any gate (4–9 in the cutover
   runbook) fails. Recovery from a half-migrated state is the
   rollback path's job, not the cutover path's.
2. **During the 24-hour observation window:** any rollback trigger
   from `production-cutover.md` fires (KS smoke fails, RDF hash
   drift, blob checksum mismatch, audit log silent, or operator
   judgement).

## Pre-rollback checklist

1. **Authorize.** Rollback is destructive (it stops the .NET host
   and re-grants PostgreSQL write permissions). The operator must
   be named on the change ticket and must post in the incident
   channel before starting.
2. **Locate the cutover record.** The script reads the original
   backup path and the original RDF directory from
   `migration/runbooks/production-cutover-record.md`. If the
   record was already archived to
   `/var/lib/ontopilot/archives/cutover-<date>.md`, restore it
   to its working path before running the script.
3. **Verify Python is reachable.** `systemctl status
   ontopilot-python-backend` should currently report `inactive
   (dead)` — that's correct; the rollback script will signal it
   that it can re-open the original RDF dir.

## Rollback sequence

The script `Invoke-ProductionRollback.ps1` walks the following
steps in order. Each step is a hard preflight — a failure stops the
sequence with a non-zero exit code and a descriptive message.

| # | Step                       | Exit on failure |
|---|----------------------------|-----------------|
| 0 | Assert-CutoverRecord       | 1               |
| 1 | Stop-DotNetBackend         | 1               |
| 2 | Restore-DatabaseWrite      | 1               |
| 3 | Restore-PythonBackendAccess| 1               |
| 4 | Mark-BackupRetention       | 1               |

```bash
# Step 0: stop the .NET backend. This is step 1 of the rollback —
# until this is done the Python backend cannot safely take over.
sudo systemctl stop ontopilot-dotnet-backend

# Step 1: run the rollback. The script will:
#   - read the cutover record for the original backup path + RDF dir
#   - stop the .NET host (idempotent — re-runs are safe)
#   - re-grant PostgreSQL write permissions
#   - signal the Python backend that the original RocksDB dir is
#     writable again (the Python supervisor / systemd unit decides
#     when to re-mount)
#   - mark the backup for 30-day retention
pwsh migration/scripts/Invoke-ProductionRollback.ps1 \
    -Record migration/runbooks/production-cutover-record.md
```

## Expected post-rollback state

After the script reports exit 5 (success) or exit 1 (failure):

1. **.NET stopped.** `systemctl status ontopilot-dotnet-backend`
   should report `inactive (dead)`.
2. **Python login OK.** Authenticate a known-good user against the
   Python backend and verify the session cookie is issued.
3. **KS reads OK.** Spot-check 5 documents through the Python API.
   Compare their stored SHA-256 with the cutover manifest.
4. **RDF queries OK.** Run a small SPARQL query that the cutover
   rehearsal recorded a hash for. Confirm the Python backend's
   response set hashes to the same value.
5. **PostgreSQL writes OK.** Insert a row into the audit log via
   the Python API and verify it lands.
6. **Backup retained.** The backup path now has a sidecar file
   `<backup-path>.keep-until-day-30` so a re-cutover attempt has a
   known-good baseline.

## After a successful rollback

```bash
# Post the rollback completion + new operator signature in the
# incident channel. Update the change ticket with the rollback
# timestamp.

# Optional: revert the smoke / parity scripts so future rehearsals
# run against Python, not .NET. The scripts auto-detect, but the
# change ticket should mention which backend is currently
# authoritative.

# DO NOT delete the .NET migration logs yet — they are evidence for
# the post-mortem. They live under
# /var/lib/ontopilot/dotnet-migration-archives/.
```

## Re-cutover attempt

After fixing the root cause:

1. Re-run the rehearsal (`Invoke-MigrationRehearsal.ps1`) against
   the most recent production backup.
2. Update the cutover record (`migration/runbooks/production-cutover-record.md`)
   with the new rehearsal evidence.
3. Re-authorize (the original operator ticket is closed; a new
   ticket is required).
4. Run `Invoke-ProductionCutover.ps1 -Record ...` following
   `production-cutover.md` from scratch.