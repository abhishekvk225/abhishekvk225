# Release checklist and go / no-go

Copy this file into the release issue for each production release (`vX.Y.Z`). Every line is **Yes / No / N/A + link**. A single unresolved **blocker** line is a no-go. Items marked (pipeline) are evidence collected by CI/CD; items marked (manual) need a person.

Release: `v____`  Commit: `____`  Date: `____`  Release manager: `____`

## A. Code and pipeline (pipeline)

| # | Check | Status |
|---|---|---|
| A1 | CI green on the tagged commit: build with warnings as errors, unit, integration (Testcontainers) and API tests | |
| A2 | `dotnet list package --vulnerable --include-transitive` clean; dependency review passed | |
| A3 | CodeQL (security-extended) has no open High/Critical alerts | |
| A4 | gitleaks clean (full history) | |
| A5 | Trivy: no fixable HIGH/CRITICAL in api, web, migrator images; SBOMs attached; provenance attestations verified at deploy | |
| A6 | EF model matches migrations; `migrate.sql` attached with checksum | |
| A7 | `dotnet format` drift status known (advisory job) | |
| A8 | Stack smoke (compose) and IIS package jobs green | |

## B. Security gate (manual)

| # | Check | Status |
|---|---|---|
| B1 | Security re-verification of M9a features done and signed (MFA, adjust approvals, ledger anchors, emergency controls); open Lows have owners | **blocker** |
| B2 | Code review of M9a and of any later change complete, Majors fixed | **blocker** |
| B3 | No Critical/High finding open in `docs/reviews/` | **blocker** |
| B4 | Production startup guards verified on the deployed config: no ephemeral keys, `AllowedHosts` set, `FaceEngine:AllowMockInProduction` false, `Webhooks:AllowUnsafeTargets` false, `Email:LogBodies` false, least-privilege DB login (readiness 200) | **blocker** |
| B5 | TLS: certificate valid > 30 days, TLS >= 1.2 only, HSTS served, plain HTTP redirected or closed | |
| B6 | Forwarded headers trust list is the real proxy (no `/0`); per-IP limits see client addresses | |
| B7 | Secrets only in the vault/secret store; none in repo, images, variables or logs; access list reviewed | |
| B8 | Penetration test / external review scheduled or done (state which) | |

## C. Product readiness (manual) - known gaps that must be consciously accepted or closed

| # | Check | Status |
|---|---|---|
| C1 | **Face engine**: a real provider is configured and its accuracy/latency validated. The shipped `mock` engine recognises nothing; going live with it requires an explicit, written exception (demo only) | **blocker** |
| C2 | **Email delivery**: the shipped sender only logs messages (reset/invitation links are not delivered; `Email:LogBodies` is refused in Production). Real delivery is configured, or a manual process for invitations/resets is agreed | **blocker** |
| C3 | MFA enforced for Super Admins; recovery path rehearsed (`recover-superadmin`, MFA reset) | |
| C4 | Approval-queue UI for license adjustments: absent. Approvers use the API; process documented | accept/close |
| C5 | Portal sessions and MFA challenge store are per node: single node or sticky sessions confirmed | |
| C6 | Rate limits / daily quotas are per node: acceptable for the planned node count | |
| C7 | Performance and multi-node behaviour (M9b) tested at expected volume; targets in the soak plan met | **blocker** |
| C8 | Data protection: retention windows set per client contracts (`face.retentionDays`, history purge), DPA/records of processing, biometric consent flow reviewed, erase procedure tested | |

## D. Operations (manual)

| # | Check | Status |
|---|---|---|
| D1 | [Staging soak test](runbooks/staging-soak-test.md) completed on this release candidate; results attached; all pass criteria met | **blocker** |
| D2 | Backup restore drill performed this quarter: RTO ____ / RPO ____ meet targets; backups are off-host and encrypted | **blocker** |
| D3 | Master key and JWT key backed up in the vault with two-person access; recovery tested | **blocker** |
| D4 | **Master-key rotation/re-anchor tooling** ([key-rotation](runbooks/key-rotation.md) section 3) exists and was rehearsed, **or** the risk is accepted in writing with the interim plan | **blocker** until accepted |
| D5 | Monitoring live: health probes, dashboards, alert routing to a staffed channel; every alert fired once in drill (S13); thresholds tuned | **blocker** |
| D6 | Log shipping live, retention set; **event 7002 shipped to write-once storage** and its silence alert active | **blocker** |
| D7 | Business gauges gap acknowledged ([monitoring](runbooks/monitoring.md) section 5) with interim procedure (admin dashboard checks) | |
| D8 | Rollback rehearsed on staging within the last release cycle; non-additive migrations flagged in release notes | |
| D9 | Emergency revoke / kill switch rehearsed ([emergency-revoke](runbooks/emergency-revoke.md)) | |
| D10 | On-call rota, escalation contacts, status-page process, DPO / incident communication path filled in | **blocker** |
| D11 | GitHub: environments `staging` and `production` configured (required reviewers on production, tag-only), branch protection, secrets/variables set, deploy SSH key scoped to the deploy user | |
| D12 | Capacity: sizing for expected load, SQL Server edition/licence, disk growth of `api.ApiRequestLogs` and ledger estimated | |
| D13 | Support: runbooks reviewed by the on-call team; a first-line playbook for client questions (402/401/429 explanations) exists | |

## E. Go / no-go

| Function | Name | Decision (go / no-go / go-with-conditions) | Conditions | Date |
|---|---|---|---|---|
| Engineering lead | | | | |
| Security | | | | |
| Operations / SRE | | | | |
| Product owner | | | | |
| Data protection officer | | | | |

Decision rule: all **blocker** lines are Yes (or have a written, dated, owner-signed exception), no open Critical/High findings, soak and restore drill passed. Anything else is a no-go.

## F. Deployment record

Staging deployment run: ____  Production deployment run: ____  Change ticket: ____  Deployed at (UTC): ____  Post-deploy checks (30 min dashboard watch, 7002 after the next nightly run): ____  Rollback needed: yes / no.
