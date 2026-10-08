# Runbooks

| Runbook | Use it when |
|---|---|
| [deploy](deploy.md) | releasing to staging / production, manual break-glass deployment, host preparation |
| [iis-deployment](iis-deployment.md) | deploying to Windows Server / IIS from `scripts/publish-iis.ps1` |
| [rollback](rollback.md) | a deployment misbehaves (application rollback, restore) |
| [db-migration](db-migration.md) | schema changes, DBA-controlled flow, backup and restore drill |
| [key-rotation](key-rotation.md) | rotating JWT/DB/master keys, webhook secrets, API keys (read the master-key gap) |
| [emergency-revoke](emergency-revoke.md) | cutting off a key, a client or an account right now |
| [incident-response](incident-response.md) | alerts, outages, ledger tampering (7001), suspected breach |
| [monitoring](monitoring.md) | health endpoints, OpenTelemetry, alert rules, dashboards, log shipping incl. event 7002 |
| [staging-soak-test](staging-soak-test.md) | validating a release candidate before production |
| [../release-checklist.md](../release-checklist.md) | go / no-go for a production release |

Related: [deploy/CONFIG.md](../../deploy/CONFIG.md) (every setting), [04-security-strategy](../04-security-strategy.md), [STATUS](../STATUS.md).
