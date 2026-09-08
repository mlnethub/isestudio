# Production Rollback Record

This template is completed by `Invoke-ProductionRollback.ps1`. Backups and the Python/Rust service are retained unless an explicitly separate retention procedure says otherwise.

## Required evidence

- Date/time (UTC):
- Operator/change reference:
- Python service name:
- Verified backup manifest path and SHA-256:
- Machine-readable state file:
- Result: `passed` / `failed`
- Backup retained: `true`

## Ordered gates

1. Stop ISEStudio/.NET:
2. Restore database permission and backup:
3. Unlock Rust writes:
4. Start Rust/Python service:

## Notes

Rollback is intended to be idempotent. Do not include passwords, bearer tokens, connection strings, object-store access keys, or secret values in this document.