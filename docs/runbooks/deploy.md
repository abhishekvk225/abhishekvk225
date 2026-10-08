# Runbook: deploy

Applies to container deployments (production-like compose stack or an orchestrator using the same images). IIS: [iis-deployment](iis-deployment.md). Always finish [release-checklist](../release-checklist.md) first.

## 0. One-time host preparation

1. Docker Engine + Compose v2, a deploy-only OS user with SSH key login, directory `/opt/nexaverify` owned by it (mode 0750).
2. Create `/opt/nexaverify/.env.prod` from `deploy/docker/.env.prod.example` (host names, `NEXAVERIFY_REGISTRY=ghcr.io/<owner>/<repo>`, `SEED_ADMIN_EMAIL`, `ALLOW_MOCK_FACE_ENGINE=false` unless it is a demo).
3. Create `/opt/nexaverify/secrets/` (mode 0700) with the seven files `make-secrets.sh` documents (`sa_password`, `app_db_password`, `seed_admin_password`, `migrator_connection`, `api_connection`, `jwt_signing_key.pem`, `master_key`). Generate with `bash deploy/docker/prod/make-secrets.sh` on the host (it never overwrites) or populate from your vault. **Back up `master_key` and `jwt_signing_key.pem` in the vault before first use**: losing `master_key` makes all biometric data and MFA secrets unrecoverable.
4. Real certificates: remove `tls internal` from `deploy/docker/prod/Caddyfile` (public DNS, ACME) or terminate TLS on your own proxy and trust only its address in `ForwardedHeaders__KnownNetworks__0` (never `/0`). The compose subnet `172.29.0.0/24` is the trust list for the bundled Caddy.
5. External managed SQL Server instead of the bundled one: use a compose override that removes `sqlserver`, keep the two connection-string secrets (use `Encrypt=True` with a trusted certificate, not `TrustServerCertificate=True`), and pass `--backup-confirmed` to `remote-deploy.sh` after taking a backup.
6. GitHub: create environments `staging` and `production` (production: required reviewers, tag-only deployment rule, optional wait timer). Environment secrets `DEPLOY_SSH_KEY`, `DEPLOY_SSH_KNOWN_HOSTS`; variables `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_DIR`, `API_URL`, `PORTAL_URL`.

## 1. Release to staging (automatic)

1. Merge to `main`; CI green (build, tests, vulnerable packages, gitleaks, Trivy, stack smoke, IIS package).
2. Tag: `git tag -s v1.2.3 -m "v1.2.3" && git push origin v1.2.3` (use `-rc.N` suffix for candidates).
3. `release.yml` verifies again, builds and Trivy-gates the three images, pushes them to GHCR with provenance attestations and SBOMs, builds the IIS package and creates the GitHub release (assets: `migrate.sql`, SBOMs, IIS zip, `SHA256SUMS`).
4. It then calls `_deploy.yml` for **staging**: verifies attestations, ships files over SSH (pinned host key), `remote-deploy.sh` backs up the database, runs the migrator, starts the stack, waits for container health, then `scripts/smoke-test.sh` runs from the runner. On failure the containers roll back automatically (database untouched).
5. Run the [staging soak test](staging-soak-test.md).

## 2. Promote to production (manual, approved)

1. Open the release checklist PR/issue for the tag; every go/no-go item answered.
2. Actions -> "Deploy production" -> tag (stable `vX.Y.Z`) + change ticket. `preconditions` checks the tag has a GitHub release and a **successful staging deployment of the same commit**.
3. A reviewer of the `production` environment approves the deployment. Watch the run: attestation verification, migrate (backup first), deploy, smoke test.
4. Post-deploy: dashboard check for 30 minutes (error rate, p95, 402/401 rates), confirm event 7002 resumes after the next nightly verification, record the deployment in the change ticket.
5. If anything is wrong: [rollback](rollback.md) immediately; do not debug in place.

## 3. Manual deployment (no GitHub, break-glass)

```bash
cd /opt/nexaverify
# files from the release: docker-compose.prod.yml, prod/Caddyfile, remote-deploy.sh (scp them first)
export NEXAVERIFY_REGISTRY=ghcr.io/<owner>/<repo>
bash ./remote-deploy.sh --tag v1.2.3            # backup -> migrate -> up -> wait healthy
SMOKE_RETRIES=5 ./smoke-test.sh https://api.example https://portal.example
```

`--no-migrate` skips the migrator (code-only release). The application never migrates itself in production.

## 4. Multi-node notes

- API: stateless apart from per-node caches (API-key cache `ApiAuth:CacheSeconds`, rate-limit and daily-quota counters, MFA challenge store). Rate limits and quotas are **per node**; MFA challenges and portal sessions are per node unless a shared cache is configured. Use sticky sessions at the load balancer until a shared cache exists.
- Portal: sessions in memory (`Session:AllowInMemoryStore=true` is a conscious single-node choice). With several nodes use sticky routing (Caddy `lb_policy cookie`, IIS ARR affinity) and a shared `DataProtection:KeyPath`.
- Background jobs run on every API node (ledger verification is serialised by a SQL application lock; alerts de-duplicate by unique rows).

## 5. Ports and network

Only 80/443 (proxy) are published. SQL Server sits on an `internal` network with no route out; API and portal share the edge network with the proxy (the API needs outbound access for webhook deliveries, constrained by the SSRF guard).
