# Runbook: rollback

Decide in minutes, not hours. Rule: **roll back first, diagnose afterwards.** Rolling back changes containers/files only; the database is never rolled back automatically.

## Decision

| Symptom after deployment | Action |
|---|---|
| Health probe failing, 5xx spike, latency regression, broken sign-in, smoke test failed | Roll back the application (section 1). |
| Wrong data written / bad migration / data corruption | Stop traffic (section 3), restore from backup ([db-migration](db-migration.md) section 5). |
| Suspected compromise | [incident-response](incident-response.md) first; rollback does not remove an attacker. |

A failed deployment started by the pipeline already rolls the containers back automatically (`_deploy.yml`, last step). Confirm it happened and that the smoke test passes.

## 1. Application rollback (containers)

GitHub: Actions -> **Rollback** -> environment, expected tag (the version you want to return to; the host aborts if its recorded `previous-tag` differs), reason. Production uses the same approval gate as a deployment.

On the host (break-glass):

```bash
cd /opt/nexaverify
cat current-tag previous-tag pending-tag 2>/dev/null     # pending-tag present = the last deployment never completed
NEXAVERIFY_REGISTRY=ghcr.io/<owner>/<repo> bash ./remote-deploy.sh --rollback --expect-tag <previous>
scripts/smoke-test.sh https://api.example https://portal.example
```

The script returns to `previous-tag` (or to `current-tag` when the last deployment did not finish), pulls those images, restarts `api`/`web`, waits for health and records the state.

## 2. Why the application rollback is safe for the schema

Migrations are written expand/contract: a release only adds tables/columns/indexes that the previous version ignores; destructive changes ship one release later after the old code is gone. Before approving a release, the reviewer confirms this for every migration in `migrate.sql` (checklist item). If a release contains a non-additive migration it must say so in the release notes and has **no** application-only rollback: use section 3.

## 3. Rollback with the database (restore)

1. Announce the maintenance window; stop `api` and `web` (`docker compose ... stop api web`), or `app_offline.htm` on IIS.
2. Restore the pre-deploy backup taken by `remote-deploy.sh` (`/var/opt/mssql/backup/NexaVerify-pre-<tag>-<timestamp>.bak`): [db-migration](db-migration.md) section 5. Everything written after the backup is lost: export what you can first (API request logs, ledger entries since the backup) for reconciliation.
3. Roll the containers back (section 1), start, smoke test.
4. After a restore, run an on-demand ledger verification (`POST /api/v1/admin/licensing/verify-ledger`) and compare the latest event 7002 lines with the restored ledger ([incident-response](incident-response.md), "Ledger integrity"): a restore legitimately removes recent rows, which an external checkpoint copy will show as "newer than the database"; document it as a known, explained difference.

## 4. After any rollback

- Pin the bad tag: do not redeploy it; open an incident/ticket with logs and the run URL.
- Keep the failed environment state (logs, `docker compose logs`) before it rotates.
- Post-incident review within five working days; add a regression test or checklist item.

## 5. IIS

`app_offline.htm` -> restore the previous site folder from `releases\<version>` -> remove `app_offline.htm` -> smoke test. Same database rules as above.
