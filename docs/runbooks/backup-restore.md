# Runbook: backup, restore and the restore drill

> Owner: DevOps / on-call DBA. Review: after every schema-affecting release and at least quarterly (drill). Related: `docs/04-security-strategy.md` §7 (key
> separation), §11 (restore-from-backup drill is a pre-release gate), `deploy/CONFIG.md`.

A backup that has never been restored is a hope, not a backup. NexaVerify has one thing a plain restore cannot prove by itself: the
**credit ledger** (hash-chained, with HMAC checkpoints keyed from the master key). So a restore is only "good" when
`Migrator verify-ledger` is clean on the restored copy.

## 1. What has to survive (and where it must NOT live)

| Asset | Contents | Backed up how | Rule |
|---|---|---|---|
| SQL Server database (`NexaVerify`) | all tenant data, biometric templates (encrypted), ledger, audit, checkpoints, settings | Full backup + log backups (see 2), `CHECKSUM`, encrypted (TDE or `WITH ENCRYPTION`) | The backup is useless without the keys below. |
| `Encryption:MasterKeyBase64` (KEK) | wraps every client's data key; keys the ledger checkpoints and MFA secrets | Vault / HSM, **separate** from the database backups, with its own access list and its own recovery test | **Lose it and all biometric data, MFA secrets and ledger anchors are unrecoverable.** Never store it next to the `.bak`. |
| `Jwt:SigningKeyPem` | signs access tokens | Secret store | Losing it only signs everybody out (new key, `Jwt:SigningKeyId` bump). |
| Portal `DataProtection:KeyPath` | key ring for session payloads, cookies, antiforgery | Secret volume backup | Losing it signs every portal user out; nothing else. |
| Log stream (event id **7002**) | off-database copy of every ledger checkpoint (head hash + MAC) | Log pipeline / WORM storage | The only defence against "attacker deleted the newest checkpoints together with the newest ledger rows". Keep it at least as long as the backups. |
| Configuration | `appsettings` overrides, environment, `ForwardedHeaders`, … | Infrastructure-as-code / repository | No secrets in it. |

Not backed up on purpose: `api.UsageCounters` (rate-limit counters, rebuilt within a minute/day), portal session cache
(`PortalCache:*`, users sign in again), in-process caches, the email queue (in memory).

## 2. Backup policy (set the numbers with the business)

| | Target | How |
|---|---|---|
| RPO (data you can lose) | _fill in, suggested ≤ 15 min_ | Full backup daily + transaction-log backup every 5–15 min (`RECOVERY FULL`) |
| RTO (time to serve again) | _fill in, suggested ≤ 2 h_ | **Measured by the drill** (section 3), not guessed |
| Retention | _fill in, suggested 35 days online, monthly for 12 months_ | Privacy: erased biometric profiles reappear in older backups. State the backup retention in your DPA and re-apply erasures (section 5, step 8) after any restore. |
| Location | a second storage account / region, immutable if possible | |
| Encryption | TDE on the instance **or** `BACKUP … WITH ENCRYPTION (SERVER CERTIFICATE …)` | Back up the certificate/key separately from the backups. |
| Integrity | `WITH CHECKSUM` always, `RESTORE VERIFYONLY … WITH CHECKSUM` after every backup | The drill script does both. |

## 3. The restore drill

**When:** quarterly, after any change to backup tooling, after a master-key rotation, and before each release candidate
(`docs/04` §11). **Who:** on-call DBA with a second person watching. **Where:** a scratch database on the *same server version* as
production (a separate server for a full-scale rehearsal).

### 3.1 Automated drill

```bash
export NV_DRILL_SERVER=sql-prod-replica.internal,1433       # prefer a restored-from-backup / secondary, not the primary
export NV_SOURCE_DB=NexaVerify
export SQLCMDUSER=drill_admin SQLCMDPASSWORD='…'            # BACKUP/RESTORE/CREATE DATABASE rights; from the secret store
export Encryption__MasterKeyBase64='…'                      # the PRODUCTION master key, fetched from the vault for this run only
deploy/scripts/backup-restore-drill.sh
```

What it does (all steps must pass; exit code 0):

1. `BACKUP DATABASE … WITH COPY_ONLY, CHECKSUM, COMPRESSION` (does not disturb the log chain or differential base).
2. `RESTORE VERIFYONLY … WITH CHECKSUM`.
3. `RESTORE DATABASE <db>_drill_<utc> … WITH MOVE` (never over the source).
4. `DBCC CHECKDB` on the copy and a table-by-table row-count comparison with the source (via `sys.partitions`, which row-level security does not hide).
   Differences are normal on a live source (writes after the backup started; the API request log is written asynchronously) and
   only warn; use `NV_STRICT_COUNTS=1` on a quiesced source.
5. `Migrator verify-ledger` against the copy: recomputes every license's hash chain and balance and checks the signed
   checkpoints. **Exit code 3 = the restored ledger is not trustworthy.**
6. Drops the copy (`NV_KEEP_RESTORED=1` keeps it) and prints the timings.

Other switches: `NV_BACKUP_FILE`, `NV_RESTORE_DB`, `NV_VERIFY_CMD` (use a published `NexaVerify.Migrator verify-ledger` instead of `dotnet run`),
`NV_RESTORE_CONNECTION`, `NV_VERIFY_SERVER`, `SQLCMD` (e.g. `docker exec -i sql /opt/mssql-tools18/bin/sqlcmd -C`),
`NV_SKIP_LEDGER_CHECK=1` (**not** a complete drill). Header of the script documents them all.

The same checks run on every build as tests: `tests/Api.IntegrationTests/BackupRestoreDrillTests.cs` (backup → restore → ledger
verification clean; wrong master key is reported; a ledger altered before the backup is caught on the restored copy; row counts match).

### 3.2 Manual steps the script does not do (once per drill)

- [ ] Start a **staging API** against the restored copy (least-privilege login: `Migrator app-principal` if the login does not exist on that server), call `/health/ready`, sign in as a test client, run one verify and check the balance.
- [ ] Confirm the **portal** works against that API (sign-in, dashboard).
- [ ] Confirm the master key retrieved from the vault is the one in use (the ledger check proves it: a wrong key shows up as *failed signature* on every license).
- [ ] Time the whole thing end to end (vault access, copy of the `.bak`, restore, migrate, verify, smoke test): that sum is the real RTO.

### 3.3 Drill log (append a row per drill; keep the output as evidence)

| Date (UTC) | Who | Source (server/db, size) | Backup | Restore | Ledger check | Total RTO | Result / findings / follow-ups |
|---|---|---|---|---|---|---|---|
| _yyyy-mm-dd_ | | | _s_ | _s_ | _clean / failed_ | _min_ | |

## 4. Reading the failures

| Symptom | Meaning | Action |
|---|---|---|
| `RESTORE VERIFYONLY` / `RESTORE … CHECKSUM` fails | The backup file is damaged (storage, transfer) | Use the previous backup; investigate the storage path; the drill failed — page the owner. |
| `DBCC CHECKDB` reports errors | Corruption in the source or in the backup chain | Treat as an incident: take a new full backup from a healthy replica, open a Sev-2. |
| `verify-ledger` exit 3, **every** license "fails its signature check" | The master key used is not the one that anchored the ledger (rotated/typo/ephemeral key) | Fetch the right key version from the vault. If the key was rotated on purpose, re-anchor deliberately (`deploy/CONFIG.md`, ledger anchors). |
| `verify-ledger` exit 3, specific licenses "content does not match its hash" / "chain broken" / "has no matching ledger row" | The ledger in the backup was altered or rows were removed | **Security incident.** Compare with the 7002 checkpoint log lines and older backups to find the last good point; do not restore this copy to production without Security sign-off. |
| `verify-ledger` exit 3, "balance differs from the license's remaining credits" only | Usually a backup taken mid-transaction on an unquiesced copy, or a manual DB edit | Re-run on a fresh backup; if persistent, treat as tampering until explained. |
| Row counts differ and `NV_STRICT_COUNTS=1` | Writes during the backup | Re-run against a quiesced source or a secondary. |
| `Encryption__MasterKeyBase64` rejected | Not 32 bytes of base64 | Fix the vault value. |

## 5. Real restore (disaster) — procedure

Decide who is incident commander, announce the incident, and stop the bleeding first.

1. **Freeze writes.** Scale the API and portal to zero (or enable the client kill switch `api.accessDisabled` for all clients) so nothing writes to the damaged database. Keep a copy of the damaged database if you can (forensics).
2. **Pick the restore point** (latest full + log chain up to just before the incident; `STOPAT` for point in time). Verify the files with `RESTORE VERIFYONLY … WITH CHECKSUM`.
3. **Restore** to the target server (a new database name first, rename after validation). For a different server: server-level logins are not in the backup — create the application login with `Migrator app-principal` (`Migrator:AppLogin`, `Migrator:AppPassword`).
4. **Bring the schema and guards in line:** `Migrator migrate` (idempotent; re-applies pending migrations, row-level-security policy, append-only triggers and seed data).
5. **Prove the ledger:** `Encryption__MasterKeyBase64=<prod key> Migrator verify-ledger` → exit 0. Do not go further on exit 3 (section 4).
6. **Start one API node** (readiness `/health/ready` must be 200: it checks RLS coverage, triggers and the login's privileges), run a smoke test (sign in, verify with a test key, balance). Then start the rest and the portal.
7. **Reconcile the gap** between the restore point and the incident (RPO): credits consumed in the gap are missing from the ledger — compare with the API request log shipped to your log pipeline and with clients' own records; use `licenses.adjust` (two-person approval above `Licensing:MaxAdjustPerAction`) with a reason that cites the incident. Webhook deliveries queued in the gap were lost or will be re-sent; receivers de-duplicate on `X-Event-Id`.
8. **Re-apply privacy erasures** performed after the restore point (`DELETE /faces/profiles/{id}` from the audit trail of the damaged copy or the log), and client offboardings (crypto-shredding), so erased people do not come back.
9. **Re-anchor**: the next nightly ledger run (or `POST /admin/licensing/verify-ledger`) writes fresh checkpoints; compare the head hashes with the 7002 log lines for the overlapping period.
10. **Rotate** secrets that were exposed if the incident was a breach (JWT key, API keys via the emergency revoke, DB passwords); **lift the freeze**; write the post-mortem and add the timings to the drill log.

## 6. Things that look alarming but are expected after a restore

- Everyone has to sign in again if the portal cache or key ring was not restored (sessions are not part of the database).
- API rate-limit counters start at zero (`api.UsageCounters` is rebuilt on use).
- Open ledger-break records (`GET /api/v1/admin/licensing/ledger-breaks`) come back as they were at the restore point; they clear when the license verifies clean again.
- Checkpoints written after the restore point are gone with the rest of that period. The 7002 log lines still hold them: after the first ledger run compare the head hashes for the overlapping licenses; a mismatch means the ledger you restored is not the one that was anchored (stop and escalate to Security).
