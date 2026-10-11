# NexaVerify

A multi-tenant **face recognition SaaS**: companies sign up, register people (with consent), verify or identify faces through a web portal or an API, and pay for credits. Platform staff manage clients, plans, licenses and payments from an admin console.

Built with **ASP.NET Core (.NET 10)**, **Clean Architecture**, **EF Core + SQL Server** and a **Blazor + MudBlazor** portal.

> **Status:** modules M1–M13 are built and tested (2,387 automated tests passing). Before real customers: the face engine is still a **mock** (matches by image brightness, not faces), the payment providers and SMTP have not been tried live, and the security/code reviews of M9a–M13 are outstanding. See [docs/STATUS.md](docs/STATUS.md) for the full board and open items.

## What it does

| For | Features |
|---|---|
| **Visitors** | Public website (home, features, pricing, developers, security, contact), self-service sign-up with email verification and a free trial |
| **Clients** | Register people, check a person (1:1), find a person (1:N), history, usage dashboard, license and credit ledger, buy credits online (invoices, GST), API keys, webhooks, API logs, team users and roles, MFA, notifications |
| **Platform staff** | Clients, licenses (issue, renew, adjust with two-person approval, suspend, revoke), plans and cost rules, credit packs and orders (refunds), reports and ledger integrity checks, audit logs, roles and permissions, emergency API-key revoke / kill switch |
| **Developers** | REST API with API keys and scopes, signed webhooks, idempotency keys, rate limits and daily quotas |

## Architecture

- **Clean Architecture** modular monolith: `Domain` → `Application` → `Infrastructure` → `Api`, with `Contracts` shared with the portal. Dependency rules are enforced by architecture tests (NetArchTest).
- **Multi-tenancy:** EF Core query filters plus SQL Server row-level security; face data is strictly tenant-owned and encrypted with per-client data keys wrapped by a master keyring.
- **Licensing:** atomic credit consumption and an append-only, hash-chained ledger with keyed checkpoints and a nightly integrity check.
- **Portal:** a backend-for-frontend; browsers never hold tokens (opaque `__Host-` session cookie, tokens stay server-side).
- **Security:** JWT + RBAC, API keys (hashed), TOTP MFA, rate limiting, SSRF-guarded webhooks, audit trail, CSP and security headers.
- **Operations:** outbox-based email and webhooks, Docker and IIS packages, CI/CD workflows, OpenTelemetry, Prometheus alert rules, runbooks.

Details: [Architecture](docs/01-architecture.md) · [Database](docs/02-database-design.md) · [API](docs/03-api-specification.md) · [Security](docs/04-security-strategy.md) · [UI](docs/06-ui-information-architecture.md)

## Repository layout

```
src/
  Domain/           entities, value objects, domain rules
  Application/      use cases, services, validators, interfaces
  Infrastructure/   EF Core, SQL, crypto, face engine, email, payments, background jobs
  Contracts/        DTOs shared by the API and the portal
  Api/              ASP.NET Core Web API
  Web/Blazor/       public website + client and admin portal
  Migrator/         database migrations, seed, key rotation and ledger tools
tests/              unit, architecture, integration (Testcontainers), component (bUnit), load
deploy/             Dockerfiles, compose files, IIS config, observability, CONFIG.md
docs/               design docs, status board, runbooks, reviews, release checklist
```

## Quick start (local development)

Prerequisites: **.NET 10 SDK**, **Docker** (for SQL Server and the integration tests).

```bash
# 1. SQL Server
docker run -d --name sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<strong password>' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest

# 2. Migrate and create the first Super Admin (password must satisfy the policy)
export ConnectionStrings__Default='Server=localhost,1433;Database=NexaVerify;User Id=sa;Password=<strong password>;TrustServerCertificate=true'
export Seed__SuperAdminEmail=admin@example.test Seed__SuperAdminPassword='<strong password>'
dotnet run --project src/Migrator -- migrate

# 3. API (https://localhost:7101) and portal (http://localhost:5102), in two terminals
dotnet run --project src/Api
dotnet run --project src/Web/Blazor
```

Open `http://localhost:5102` (public site) or `/login`. You must change the seeded password at first sign-in.

Development uses throw-away encryption and JWT keys. Set `Encryption:MasterKeyBase64` (or the `Encryption:Keys` ring) to keep data across restarts. Email is logged to the console in Development: sign-up and reset links appear in the API log.

**Try payments without a provider:** set `Billing__Enabled=true`, `Billing__Provider=Simulated` and `Portal__PublicBaseUrl=http://localhost:5102` on the API (Development/Testing only), create a credit pack under **Admin → Billing → Credit packs**, then buy it from a client account.

**Design review without an API:** `Ui__UseStubClients=true` on the portal (Development only) serves demo data and accepts any password; an email containing "admin" opens the admin console.

Full Docker route: `docker compose -f deploy/docker/docker-compose.dev.yml up`. A production-like stack is in `deploy/docker/docker-compose.prod.yml`.

## Configuration

Every setting, its default and its startup guards are documented in [deploy/CONFIG.md](deploy/CONFIG.md): connection strings, encryption keyring and rotation, JWT keys, MFA, API-key limits, webhooks, portal sessions, email (`Email:*`), billing (`Billing:*`), sign-up (`Signup:*`), captcha, telemetry and more. Secrets are read from environment variables or files in `NEXAVERIFY_SECRETS_DIR`.

## Testing

```bash
dotnet test NexaVerify.slnx
```

Integration tests use Testcontainers (SQL Server), so Docker must be running. The API integration suite takes about 30–40 minutes; the unit, architecture and component suites take under a minute. Last full run: **2,387 tests, 0 failures** (Domain 112, Application 280, Architecture 12, Infrastructure unit 360, Infrastructure integration 100, Web components 1,027, API integration 496).

Load-test scripts (k6 and a .NET driver) are in `tests/Load`.

## Operations

- [Runbooks](docs/runbooks/README.md): deploy, rollback, database migration, **key rotation**, backup and restore, emergency revoke, incident response, monitoring, staging soak test, IIS deployment
- [Release checklist](docs/release-checklist.md): go/no-go list with the known blockers
- Migrator commands: `migrate`, `script`, `app-principal`, `recover-superadmin`, `verify-ledger`, `rotate-master-key`, `rotation-status`, `verify-keyring`

## Known gaps before going live

1. Replace the mock face engine with a real model (and add liveness detection).
2. Try Stripe / Razorpay in their test modes and a real SMTP provider.
3. Run the security and code reviews for M9a–M13 (public sign-up, payments and key rotation first).
4. Run the CI/CD workflows, IIS package and staging soak/restore/rollback drills for real.
5. Legal review of the draft terms and privacy pages; biometric consent is the customer's duty.
6. Re-key (replace) client data keys: master-key rotation re-wraps them but does not replace them.

## Documentation

See [docs/README.md](docs/README.md) for the document index.

---

Built by Abhishek Vishwakarma ([@abhishekvk225](https://github.com/abhishekvk225)).
