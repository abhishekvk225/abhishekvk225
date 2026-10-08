# Runbook: incident response

Roles: **Incident commander (IC)** decides and communicates; **Operator** executes; **Scribe** keeps the timeline (ticket + `#incident` channel). One person may hold two roles in a small team; the IC never debugs.

## 1. Triage (first 15 minutes)

1. Acknowledge the alert; open an incident ticket; note start time (UTC).
2. Classify:

| Severity | Examples | Target |
|---|---|---|
| SEV1 | data exposure or suspected tampering (event 7001, leaked keys/master key, cross-tenant data), total outage | IC within 15 min, customer notice per policy |
| SEV2 | partial outage, error rate > 2 %, readiness failing on one node, licensing refusing valid clients | respond 30 min |
| SEV3 | degraded latency, background job failures, single client affected | next business day |

3. Look at (in this order): `/health/ready` response and body, the Grafana "NexaVerify overview" dashboard, last deployment time (a deployment in the last hour -> **roll back first**, [rollback](rollback.md)), container/service state, error lines (`"@l":"Error"`) grouped by `SourceContext`.
4. Preserve evidence before restarting anything: `docker compose logs --since 2h > incident-<id>.log`, copy the audit rows of the window (`GET /admin/clients/{id}/activity`), do not delete containers/volumes.

## 2. Playbooks

### Health probe / readiness failing
`/health/live` failing = process down (container restarts? OOM? crash loop: read logs). `/health/ready` 503 body names the failing check: `database` (connectivity, login, SQL Server health, disk, a failover) or `tenant-protection` (RLS policy missing or API login over-privileged: **SEV1**, do not bypass; run the Migrator `migrate`, find who changed the policy via the audit/SQL logs).

### Error-rate or latency alert
Check deployment history, dependency (SQL latency, deadlocks), CPU saturation (face decoding is CPU-bound; 503 load shedding on face routes means the image-gate queue is full: scale out or throttle the noisy client with the kill switch). Roll back if a release correlates.

### Credential stuffing / auth failure spike
Per-account lockout (`Auth:MaxFailedAttempts`) and per-IP limits are already active. Confirm the real client IP is visible (forwarded headers): if every request shows the proxy address, all users share one rate-limit bucket (fix `ForwardedHeaders` trust list). Block at the edge (WAF/proxy) for abusive ranges; consider forcing MFA (`Mfa:RequiredPlatformRoles`, client `security.requireMfa`); reset affected accounts.

### License exhaustion burst (402)
Usually a large client running out. Check the admin dashboard (expiring / low balance), contact the client, renew or adjust (adjustments above `Licensing:MaxAdjustPerAction` need a second approver). A 402 burst that does not match balances points at a metering bug: capture request ids, verify the ledger (below).

### Leaked API key / abusive client
[emergency-revoke](emergency-revoke.md).

### Ledger integrity (event 7001 / checkpoint problems) - SEV1 until explained
Event 7001 means a license ledger fails its hash chain, balance or signed-checkpoint check; the text names license, entry and reason. Persisting breaks repeat nightly (Critical) but are audited once per license and thing (`licensing.LedgerBreakRecords`, `ledger.verification_failed`).
1. Do not adjust or "fix" the affected license. Suspend it if it is still being charged (`POST /admin/licenses/{id}/suspend`, reason).
2. Re-run an on-demand verification for that license (`POST /admin/licensing/verify-ledger` body `{"licenseId":"..."}` -> 202, poll `GET /admin/licensing/verify-ledger/runs/{id}`) to separate a one-off race from a persistent break (balance-only mismatches are re-checked before they are reported).
3. Compare with the **external copy of event 7002**: for the license, the last logged `rows` and `head` must match the database at that point (`licensing.LedgerCheckpoints` and the ledger). Rows missing from the database that the external log proves existed = deletion/tampering; extra/changed rows with an unchanged head = rewrite. A restore from backup also explains missing rows (check the restore history).
4. Check who could write: database audit, DBA activity, the app login's rights (`Database:RequireLeastPrivilege`), recent migrations/guards, deployment times.
5. If tampering is likely: preserve a database snapshot and logs, treat as a breach (section 3). If a code bug: fix forward, record the explanation in the ticket, and re-anchor only through the supported procedure ([key-rotation](key-rotation.md) section 3; never delete checkpoints by hand).
Silence of event 7002 for 36 h (`LedgerCheckpointsNotWritten`) is the same class of alert: the verification job is off, failing or the log shipping is broken; fix and run an on-demand verification.

### Webhook failures
`WebhookDeliveryBacklog` or an endpoint auto-disabled after 20 consecutive failures: client endpoint down; they re-enable it in the portal. If all clients fail: egress/DNS problem or dispatcher failing (`Webhook dispatch cycle failed`). The SSRF guard blocks private targets by design.

### Database problems
Connection pool exhaustion, deadlocks, full disk, failover: see SQL Server logs; the API fails readiness and recovers when the database is back; no data is processed while it is down (requests fail, not queue). If data is damaged: [rollback](rollback.md) section 3 and [db-migration](db-migration.md) section 5.

## 3. Suspected data breach (SEV1)

1. IC declares; restrict who has production access (use the kill switch / take the API out of rotation if exfiltration is ongoing).
2. Contain: revoke keys of affected clients, rotate JWT key (ends sessions), rotate DB passwords, rotate other exposed secrets ([key-rotation](key-rotation.md)). Master key exposure has no supported in-place rotation yet: escalate to engineering immediately.
3. Preserve evidence (logs, snapshots, audit rows); do not wipe hosts.
4. Assess scope with audit and request logs (what, which tenants, which period). Biometric templates are encrypted per client; their exposure depends on key exposure.
5. Notify: legal/DPO decide on regulator and customer notification (GDPR 72 h clock starts at awareness). Draft the customer message from facts only.
6. Recover, then post-incident review (blameless) within five working days: timeline, root cause, detection gap, actions with owners.

## 4. Communication templates

- Internal: "SEV<n> <title>. Impact: <who/what>. Started: <UTC>. IC: <name>. Next update: <time>."
- Customer (status page): "We are investigating <symptom> affecting <scope> since <time UTC>. Next update by <time>." Update at the promised time even if nothing changed.

## 5. Contacts and access (fill in before go-live)

On-call rota, escalation chain, DPO, hosting provider support, DBA, certificate owner, GitHub org owners: keep in the release checklist, not here (this repo is not a contact directory).
