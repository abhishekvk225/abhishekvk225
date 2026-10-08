# Runbook: database migration

The application **never** migrates itself in production. The Migrator is the only component that changes the schema, installs the row-level-security policy and append-only guards, seeds identity data and creates the least-privilege app login.

## 1. Principals

| Principal | Used by | Rights |
|---|---|---|
| Admin / deployment login | Migrator only (`ConnectionStrings__Default` of the `migrator` container or a DBA session) | DDL, security policy, create login/user. Never given to the API. |
| `nexaverify_app` (created by `Migrator app-principal`) | API | DML on application tables; cannot alter the RLS policy or the guards. Readiness fails if it is sysadmin/db_owner (`Database:RequireLeastPrivilege`). |

## 2. Commands

| Command | What it does |
|---|---|
| `migrate` (default) | Drops the RLS policy, applies pending EF migrations, installs guards, seeds identity data (first Super Admin when `Seed__*` is set) and licensing defaults. |
| `script` | Prints the idempotent SQL equivalent (also a release artefact `migrate.sql`). Needs no database connection. |
| `app-principal` | Creates/updates the app login (`Migrator__AppLogin`, `Migrator__AppPassword`). Use it for DB password rotation. |
| `recover-superadmin` | Break-glass: (re)creates `Seed__SuperAdminEmail` as an active Super Admin. |

## 3. Standard flow (pipeline)

1. CI produces `migrate.sql` and fails the build if the EF model has pending changes without a migration.
2. Release attaches `migrate.sql` + checksum; `_deploy.yml` stores it as the run's audit artefact.
3. `remote-deploy.sh`: **backup** (`BACKUP DATABASE ... WITH COMPRESSION, CHECKSUM`) -> `migrator` container runs `migrate` + `app-principal` -> services start. A failed backup aborts before any change.
4. Smoke test (`/health/ready` proves the database and RLS policy are in place).

## 4. DBA-controlled flow (no migrator in production)

1. Review `migrate.sql` against [02-database-design](../02-database-design.md); check every statement is additive (new objects) or an explicitly announced change.
2. Take a verified backup; record its location.
3. Apply with the Migrator run by the DBA from a controlled workstation or job with the admin connection string (`dotnet NexaVerify.Migrator.dll migrate`, then `app-principal`). `migrate.sql` is the reviewable EF part only: it does **not** drop and reinstall the row-level-security policy or the append-only guards that the Migrator wraps around the migrations, so applying the raw script by hand on a schema change that touches secured tables must be paired with the Migrator guard installation (ask Engineering before doing this).
4. Deploy the new application version; smoke test.

## 5. Backup and restore

Backup (bundled SQL Server): `remote-deploy.sh` writes `/var/opt/mssql/backup/NexaVerify-pre-<tag>-<utc>.bak` into the `sqlbackup` volume. **Copy it off-host** and keep scheduled backups too: full nightly, differential every 4 h, log backups every 15 min on a FULL recovery model; encrypt backups at rest. Backups contain biometric templates (encrypted with per-client data keys wrapped by the master key): without `Encryption:MasterKeyBase64` of that moment a restore cannot decrypt them, so back the key up separately and keep it with the same retention.

Restore drill (do it in staging every quarter and before go-live):

```sql
RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/backup/<file>.bak' WITH CHECKSUM;
RESTORE DATABASE [NexaVerify] FROM DISK = N'/var/opt/mssql/backup/<file>.bak' WITH REPLACE, RECOVERY;
```

Then run the migrator `app-principal` (re-binds the login to the restored database users), start the API, `/health/ready` must be 200, run an on-demand ledger verification and sign in. Record the measured RTO/RPO in the release checklist.

## 6. Failure modes

- Migrator exits non-zero: nothing starts (compose `service_completed_successfully`); read the migrator logs; fix forward or restore.
- Migration timeout on a big table: raise `Database:CommandTimeoutSeconds` for the migrator process only; prefer the DBA flow for large changes.
- Login `nexaverify_app` exists with an old password: re-run `app-principal` with the new password and update the API secret, in that order (see [key-rotation](key-rotation.md)).
