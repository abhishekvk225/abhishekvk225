# 01 — Solution Architecture & Module Breakdown

> Owner: Solution Architect Agent · Status: **Draft v1 — awaiting sign-off** · Target runtime: **.NET 10 (LTS)**

## 1. Goals and non-goals

**Goals**
- A multi-tenant SaaS where a Super Admin provisions *Clients* (tenants), assigns *Licenses* (credit packs with expiry) and each client integrates face recognition through a secure REST API or uses the portal.
- Every billable operation is metered, deducted atomically and recorded in an immutable ledger.
- The recognition engine is a swappable *provider* behind an interface.
- Extensible: more roles, plans, providers, payment integration, reports and a mobile app must not require structural change.

**Non-goals for v1** (designed for, not built): payment gateway, SSO/SAML, liveness detection, mobile apps, multi-region data residency, vector database.

## 2. Architecture style

Clean Architecture, modular monolith, feature-sliced inside each layer. One deployable API, one deployable Blazor web app, one background worker (can be hosted in-process in the API at first, split later without code change).

```
                         ┌────────────────────────────┐
  Browser ──cookie──────▶│  Web (Blazor Web App,      │
                         │  Interactive Server, BFF)  │
                         │  MudBlazor UI, no business │
                         │  logic, typed API clients  │
                         └─────────────┬──────────────┘
                                       │ HTTPS + JWT (server-side only)
  Integrator ──X-Api-Key──────────────▶│
  (client's own systems)               ▼
                         ┌────────────────────────────┐
                         │  API (ASP.NET Core)        │
                         │  thin controllers, authN,  │
                         │  authZ, rate limit, ProblemDetails│
                         └─────────────┬──────────────┘
                                       ▼
                         ┌────────────────────────────┐
                         │  Application               │
                         │  services, use-cases,      │
                         │  validators, abstractions  │
                         └──────┬───────────────┬─────┘
                                ▼               ▼
                         ┌────────────┐  ┌──────────────────────────────┐
                         │  Domain    │  │  Infrastructure              │
                         │  entities, │  │  EF Core/SQL Server, Identity│
                         │  rules,    │  │  face providers, blob store, │
                         │  events    │  │  crypto, email, background   │
                         └────────────┘  └──────────────────────────────┘
```

### Dependency rule (enforced by an architecture test using NetArchTest)

| Project | May reference | Must not reference |
|---|---|---|
| `Domain` | nothing | everything else |
| `Contracts` (wire DTOs, enums, error codes) | nothing | everything else |
| `Application` | Domain, Contracts | Infrastructure, API, Web, EF Core |
| `Infrastructure` | Application, Domain | API, Web |
| `Api` (composition root) | Application, Infrastructure, Contracts | Web |
| `Web` | Contracts only | Domain, Application, Infrastructure |

The Web project talks to the API over HTTP only. That keeps business logic out of Blazor pages by construction and keeps the API reusable for the future mobile app.

## 3. Key architectural decisions (ADRs)

| # | Decision | Rationale / trade-off |
|---|---|---|
| ADR-01 | **Modular monolith**, not microservices | One team, one DB, strong consistency needed for license deduction. Modules are folders with internal boundaries; a module can be extracted later. |
| ADR-02 | **Shared database, shared schema, `ClientId` discriminator**, enforced by EF global query filters **and** SQL Server Row-Level Security | Cheapest to operate for many small/medium tenants. Two independent isolation layers so one bug cannot leak data. Database-per-tenant remains possible for an enterprise tier because all access goes through `ITenantContext`. |
| ADR-03 | **No MediatR**; plain application services + FluentValidation | Matches the requested Service pattern, fewer moving parts, no licensing risk. |
| ADR-04 | **EF Core directly in Infrastructure; repositories only for aggregates with real query logic** (licenses, face profiles). Generic repository over `DbSet` is not used. | A generic repository over EF is a leaky duplicate of `DbContext`. Services depend on small purpose-built interfaces (`ILicenseRepository`, `IFaceProfileRepository`) and on `IUnitOfWork`. |
| ADR-05 | **License deduction = single conditional `UPDATE` + ledger `INSERT` in one DB transaction** (no stored procedure) | Atomic under concurrency, testable from C#, one place for the rule (no duplicate logic in T-SQL). See §6. |
| ADR-06 | **Blazor Web App, Interactive Server, acting as BFF** with HttpOnly cookie; JWT held server-side | Tokens never reach the browser (XSS-safe), antiforgery covers CSRF, no CORS needed for the portal. Trade-off: needs sticky sessions / SignalR backplane when scaled out. WASM was rejected because tokens would live in browser storage. |
| ADR-07 | **Two authentication schemes on the API**: JWT Bearer (portal users) and API key (integrators). Face endpoints accept both. | Same business code path regardless of caller; the principal always carries `ClientId`, actor type and permissions. |
| ADR-08 | **Permission-based RBAC** (roles → permissions) with dynamic policy provider | New roles/permissions are data, not code. `[HasPermission("licenses.manage")]` on endpoints. |
| ADR-09 | **ASP.NET Core Identity** for user store, lockout, password hashing | Don't hand-roll credential storage. Custom `Role`/`Permission` tables sit alongside. |
| ADR-10 | **Envelope encryption** for biometric templates and secrets: per-client data key wrapped by a master key from a key provider (Key Vault / env / DPAPI in dev) | Crypto-shredding on client offboarding; key rotation without re-encrypting everything at once. |
| ADR-11 | **Face provider abstraction** (`IFaceEngine`) + templates tagged with `Provider` and `ModelVersion` | Provider swap = new templates; old ones keep working until re-enrolled by a migration job. |
| ADR-12 | **Outbox pattern** for webhooks, emails and notifications | At-least-once delivery that survives crashes; requests are never blocked by third-party endpoints. |
| ADR-13 | **Serilog** structured logging with a redaction policy; OpenTelemetry for traces/metrics | Searchable logs, correlation id across Web → API → DB. |
| ADR-14 | **UTC everywhere**, rendered in the client's configured timezone in the UI | Avoids expiry-date bugs across timezones. |

## 4. Solution folder structure

```
/
├── FaceSaaS.slnx
├── Directory.Build.props         # nullable, analyzers, warnings-as-errors, deterministic
├── Directory.Packages.props      # central package management (all versions pinned here)
├── global.json                   # SDK pin
├── .editorconfig
├── docs/                         # this documentation
├── src/
│   ├── Domain/                   # FaceSaaS.Domain
│   │   ├── Common/               # Entity, AuditableEntity, ITenantOwned, ValueObjects, DomainException
│   │   ├── Identity/             # User, Role, Permission, RefreshToken, LoginAttempt
│   │   ├── Tenancy/              # Client, ClientUser, ClientSetting, ClientKey
│   │   ├── Licensing/            # License, LicenseTransaction, Plan, CostRule (+ state machine)
│   │   ├── Faces/                # FaceProfile, FaceTemplate, RecognitionRequest, MatchResult
│   │   ├── ApiAccess/            # ApiKey, WebhookEndpoint, WebhookDelivery, ApiRequestLog
│   │   ├── Auditing/             # AuditLog
│   │   └── Notifications/
│   ├── Contracts/                # FaceSaaS.Contracts — request/response DTOs, enums, ErrorCodes, Permissions
│   ├── Application/              # FaceSaaS.Application
│   │   ├── Common/               # Result<T>, Error, PagedResult, ICurrentUser, ITenantContext, IClock, behaviors
│   │   ├── Abstractions/         # IUnitOfWork, IFaceEngine, IBlobStore, IKeyProvider, IEmailSender, ...
│   │   ├── Identity/             # AuthService, UserService, TokenService
│   │   ├── Tenancy/              # ClientService, ClientSettingsService
│   │   ├── Licensing/            # LicenseService, LicenseMeteringService, CostRuleResolver
│   │   ├── Faces/                # EnrollmentService, VerificationService, IdentificationService, HistoryService
│   │   ├── ApiAccess/            # ApiKeyService, WebhookService, RequestLogService
│   │   ├── Dashboards/           # AdminDashboardService, ClientDashboardService
│   │   ├── Auditing/             # AuditService
│   │   └── Notifications/
│   ├── Infrastructure/           # FaceSaaS.Infrastructure
│   │   ├── Persistence/          # AppDbContext, Configurations/, Interceptors/, Migrations/, Rls/, Seed/
│   │   ├── Identity/             # Identity setup, JwtTokenIssuer, PermissionStore
│   │   ├── Security/             # AesGcmEnvelopeEncryptor, ApiKeyHasher, KeyProviders/
│   │   ├── Faces/                # Providers/{Mock,OnnxLocal,...}, TemplateMatcher
│   │   ├── Storage/              # FileSystemBlobStore (+ Azure Blob later)
│   │   ├── Background/           # LicenseExpirySweeper, UsageRollupJob, OutboxDispatcher, RetentionPurge
│   │   ├── Messaging/            # Email, Webhook HTTP sender
│   │   └── DependencyInjection.cs
│   ├── Api/                      # FaceSaaS.Api (composition root)
│   │   ├── Controllers/{Auth,Admin,Client,Faces}/
│   │   ├── Middleware/           # CorrelationId, ExceptionHandler (ProblemDetails), SecurityHeaders, RequestLogging
│   │   ├── Auth/                 # ApiKeyAuthHandler, PermissionPolicyProvider, ClientStatusGuard
│   │   ├── RateLimiting/
│   │   └── Program.cs
│   └── Web/
│       └── Blazor/               # FaceSaaS.Web
│           ├── Layouts/          # AdminLayout, ClientLayout, AuthLayout
│           ├── Pages/{Admin,Client,Auth}/
│           ├── Components/       # StatCard, DataTable<T>, ConfirmDialog, EmptyState, ErrorState, SkeletonCard, ChartCard, StatusChip, SecretRevealDialog, FaceCapture
│           ├── Services/         # Typed API clients, NotificationService, ThemeService, AuthStateProvider
│           ├── Theme/            # MudTheme (light + dark), design tokens
│           └── wwwroot/          # css, js interop (camera capture only)
├── tests/
│   ├── Domain.UnitTests/
│   ├── Application.UnitTests/    # license deduction, state machines, cost rules, auth, tenancy
│   ├── Infrastructure.IntegrationTests/   # Testcontainers SQL Server: concurrency, RLS, query filters
│   ├── Api.IntegrationTests/     # WebApplicationFactory: authN/Z, tenant isolation, rate limit, error contract
│   ├── Web.ComponentTests/       # bUnit
│   ├── Web.E2E/                  # Playwright (critical journeys)
│   └── Architecture.Tests/       # dependency rule, naming, "no controller touches DbContext"
├── deploy/
│   ├── docker/                   # Dockerfiles, docker-compose (api, web, sqlserver, seq)
│   ├── iis/                      # web.config templates, publish profiles, runbook
│   └── ci/                       # pipeline definitions
└── .github/workflows/            # build, test, CodeQL, dependency review, container scan, release
```

## 5. Module breakdown

| # | Module | Responsibility | Key services (Application) | Key tables |
|---|---|---|---|---|
| M1 | **Platform Foundation** | Building blocks, EF context, tenancy plumbing, error handling, logging, health checks, seeding | `ITenantContext`, `IClock`, `Result<T>` | — |
| M2 | **Identity & Access** | Login, refresh rotation, password reset, lockout, roles/permissions, user management, login history | `AuthService`, `TokenService`, `UserService`, `RoleService` | Users, Roles, Permissions, RolePermissions, UserRoles, RefreshTokens, LoginHistory |
| M3 | **Client (Tenant) Management** | Create/edit/activate/deactivate/suspend clients, reset passwords, per-client settings, activity view | `ClientService`, `ClientSettingsService` | Clients, ClientUsers, ClientSettings, ClientKeys |
| M4 | **Licensing & Metering** | Plans, license lifecycle, cost rules, atomic deduction, ledger, expiry sweeps, alerts | `LicenseService`, `LicenseMeteringService`, `CostRuleResolver` | Plans, Licenses, LicenseCostRules, LicenseTransactions |
| M5 | **Face Recognition** | Enroll, verify (1:1), identify (1:N), delete/erase, history, per-client recognition config, provider abstraction | `EnrollmentService`, `VerificationService`, `IdentificationService`, `IFaceEngine` | FaceProfiles, FaceTemplates, FaceRecognitionRequests, FaceMatchResults |
| M6 | **API Access Management** | API keys (create/rotate/revoke), rate limiting, request logs, webhooks, API docs | `ApiKeyService`, `WebhookService`, `RequestLogService` | ApiKeys, ApiRequestLogs, WebhookEndpoints, WebhookDeliveries |
| M7 | **Usage & Dashboards** | Rollups, admin dashboard, client dashboard, reports/export | `AdminDashboardService`, `ClientDashboardService`, `UsageRollupJob` | UsageLogs (+ reads from all) |
| M8 | **Audit & Notifications** | Immutable audit trail, in-app notifications, alert rules, email | `AuditService`, `NotificationService` | AuditLogs, Notifications, OutboxMessages |
| M9 | **Web Portal (Blazor)** | Admin and Client experiences, reusable component library, theme | UI-only; typed API clients | — |
| M10 | **Ops & Delivery** | Docker/IIS, CI/CD, config, monitoring | — | — |

### Cross-cutting conventions
- **Result pattern**: services return `Result<T>` with a typed `Error` (code from `Contracts.ErrorCodes`); controllers map it to ProblemDetails in one place. Exceptions are for bugs and infrastructure faults.
- **Validation**: FluentValidation per request DTO, run by an API filter before the service; services re-assert domain invariants.
- **Authorization**: policy per permission; *resource-level* tenant check is automatic (query filter), never a hand-written `if (x.ClientId == ...)`.
- **Auditing**: `SaveChanges` interceptor fills `CreatedBy/UpdatedBy/dates`; explicit `IAuditService.RecordAsync` for business events (license suspended, key revoked, password reset, …).
- **Cancellation**: every async API/service method takes a `CancellationToken` (bound to `HttpContext.RequestAborted`). Background jobs honour the host's stopping token. Billing commits are *not* cancelled mid-transaction.
- **Configuration**: strongly typed options validated at startup (`ValidateOnStart`); no literals for limits/thresholds/URLs (see `docs/04` §10 for the config catalogue).

## 6. Multi-tenancy strategy

**Model:** shared DB / shared schema / `ClientId` on every tenant-owned row.

**Resolving the tenant (`ITenantContext`)** — never from request body/query/route:
1. JWT user → `cid` claim (platform users have none).
2. API key → `ClientId` on the key row after hash lookup.
3. Background jobs → explicitly set per unit of work via `ITenantScope.Begin(clientId)`.
4. Super Admin acting on a client → *explicit* `IPlatformAccess` scope, audited; the client is taken from the route (`/admin/clients/{id}/...`) and authorised by permission, not by the query filter.

**Five isolation layers (defense in depth)**
1. **Authentication** puts `ClientId` in the principal.
2. **Authorization policies** (platform vs client scope on each permission).
3. **EF Core global query filter** `e => e.ClientId == tenant.ClientId` on every `ITenantOwned` entity; `IgnoreQueryFilters()` is banned outside `Infrastructure/Platform` by an analyzer/architecture test.
4. **Write guard**: `SaveChanges` interceptor stamps `ClientId` on new `ITenantOwned` rows and *rejects* any Added/Modified row whose `ClientId` ≠ current tenant (unless platform scope).
5. **SQL Server Row-Level Security**: connection interceptor sets `SESSION_CONTEXT('ClientId')` / `('IsPlatform')` on every connection open; filter + block predicates on tenant tables. Even raw SQL or a forgotten filter cannot cross tenants.

**Tenant lifecycle effects** (checked on every request via a short-TTL cached `ClientStatusGuard`, so suspension bites within ~30 s):
| Client status | Portal login | API key calls | Data retained |
|---|---|---|---|
| Active | yes | yes | yes |
| Inactive | no | no | yes |
| Suspended | no (message shown) | no (`403 CLIENT_SUSPENDED`) | yes |
| Deleted (soft) | no | no | purged after retention window; client key destroyed (crypto-shred) |

## 7. License & metering design (summary — detail in `docs/02` and `docs/03`)

- A **License** is a credit pack for one client: `TotalCredits`, `ConsumedCredits`, `StartsAt`, `ExpiresAt`, `Status`. A client may hold several; consumption picks the **earliest-expiring usable license first (FEFO)**.
- **Cost rules** (`LicenseCostRules`) map *operation → credits* with a *charge policy*; resolution: client override → plan → platform default. Defaults (all editable by Super Admin): Enroll = 1, Verify = 1, Identify = 1; policy `OnCompleted` (charged when the engine produced a definitive answer, including "no match"; **not** charged for provider errors, no-face-detected, or rejected-quality images).
- **Pre-flight gate** (cheap, before any image processing): client active → at least one license `Active` and within `[StartsAt, ExpiresAt)` → `Remaining ≥ cost`. Failing codes: `LICENSE_NOT_FOUND`, `LICENSE_EXPIRED`, `LICENSE_SUSPENDED`, `LICENSE_INSUFFICIENT_BALANCE`.
- **Deduction** (after the engine succeeds, one DB transaction):
  1. `UPDATE Licenses SET ConsumedCredits += @cost WHERE Id=@id AND Status='Active' AND @now >= StartsAt AND @now < ExpiresAt AND TotalCredits - ConsumedCredits >= @cost` → must affect exactly 1 row, otherwise try next license / fail with the right code (no negative balance is possible, even under 1,000 concurrent calls).
  2. `INSERT LicenseTransactions` (append-only, hash-chained, idempotency key unique).
  3. `UPDATE FaceRecognitionRequests SET Status, CreditsCharged, LicenseTransactionId`.
- **Idempotency**: `Idempotency-Key` header on billable endpoints; replay returns the stored result and **never** double-charges.
- **Compensation**: a charge is never edited or deleted; refunds/adjustments are new `Refund`/`Adjustment` ledger rows with reason and actor.
- **Immutability**: ledger and audit tables — the application DB login has `INSERT, SELECT` only (`DENY UPDATE, DELETE`), plus an `INSTEAD OF UPDATE, DELETE` trigger that raises an error; each row stores `PrevHash`/`RowHash` for tamper evidence.
- **Expiry** is evaluated at use-time from timestamps (correct even if the sweeper is down); a sweeper job only flips `Status` to `Expired`, writes an `ExpiryWriteOff` ledger row and raises notifications.

## 8. Face recognition module design

```
EnrollmentService / VerificationService / IdentificationService
        │  (license gate, validation, orchestration, persistence, audit)
        ▼
  IFaceEngine ───────── provider-agnostic contract
   ├─ DetectAsync(image)            → faces + quality + landmarks
   ├─ ExtractTemplateAsync(face)    → float[] embedding + {Provider, ModelVersion, Dim}
   └─ CompareAsync(a, b)            → similarity 0..1
  ITemplateMatcher ─── 1:N search over a client's templates (in-memory cached, per client,
                       swap for a vector index via IFaceIndex without touching services)
  IBlobStore ───────── optional image retention (encrypted, off by default)
```

- **Providers**: `Mock` (deterministic, used in tests and demos), `OnnxLocal` (ArcFace-class model via ONNX Runtime — first real provider, runs on-prem so no biometrics leave the deployment), later `AzureFace`, `AwsRekognition`. Selected via configuration (`FaceEngine:Provider`); a client may be pinned to a provider through settings.
- **Templates are provider-bound**: matching only compares templates with the same `Provider` + `ModelVersion`. A background `TemplateMigrationJob` can re-extract from retained images when the provider changes.
- **Outcome vocabulary** (stable API contract): `Enrolled`, `Matched`, `NoMatch`, `NoFaceDetected`, `MultipleFaces`, `LowQuality`, `ProviderError`, `Rejected`. Dashboards group these into *Successful* (Enrolled, Matched), *No match* (valid answer), *Failed* (the rest).
- **Configurable per client** (within platform min/max): match threshold, max faces per image, min quality score, allow image retention, retention days, max profiles, top-K for identify.
- **Biometric data is special-category personal data** (GDPR Art. 9, BIPA/CCPA-like laws): each profile records consent reference, optional retention date, and supports erasure (`DELETE /faces/profiles/{id}` hard-deletes templates + blobs and writes an audit entry). See `docs/04` §9.

## 9. Frontend architecture (Blazor + MudBlazor)

- **Layouts**: `AuthLayout`, `AdminLayout` (platform nav), `ClientLayout` (tenant nav). Sidebar is permission-trimmed; the same permission keys the API uses (via `Contracts.Permissions`).
- **State**: scoped services (`ThemeService`, `CurrentUserState`, `NotificationHub` for toasts); data fetched via typed clients with a shared `ApiResult<T>` wrapper so every page handles *loading / empty / error / success* identically.
- **Reusable components** (build once, used everywhere): `PageHeader`, `StatCard`, `ChartCard`, `DataTable<T>` (server-side paging/sort/filter, responsive column collapse), `FilterBar`, `StatusChip`, `ConfirmDialog` (typed-confirmation variant for destructive actions), `SecretRevealDialog` (show-once + copy), `EmptyState`, `ErrorState`, `SkeletonCard/Table`, `FaceCapture` (camera JS-interop), `LicenseGauge`, `ActivityFeed`.
- **Theme**: single `MudTheme` with light and dark palettes, 8-pt spacing scale, one type scale; user choice persisted per user; respects `prefers-color-scheme`.
- **UX rules**: no full-page reloads (enhanced navigation + interactive components); optimistic UI only for non-financial actions; every mutating action → confirm (if destructive) → loading state → snackbar with outcome; forms validated inline with the same rules as the API (DataAnnotations mirrored from Contracts); accessible (labels, focus order, contrast, keyboard).

See `docs/06-ui-information-architecture.md` for the sitemap and dashboard definitions.

## 10. Non-functional targets

| Area | Target (v1) |
|---|---|
| Latency | p95 ≤ 800 ms verify, ≤ 1.5 s identify over 10k templates (provider-dependent; budget excludes network) |
| Throughput | 50 req/s sustained per API instance; stateless API → scale horizontally |
| Consistency | License balance strongly consistent; dashboards eventually consistent ≤ 60 s |
| Availability | Health endpoints (`/health/live`, `/health/ready`); graceful degradation when webhook/email targets fail |
| Data growth | Request/API logs monthly-partitioned with retention purge; rollups keep dashboards O(days) not O(requests) |
| Observability | Correlation id on every log/response, OpenTelemetry traces + metrics, audit trail, alerting on error-rate, 5xx, license-exhaustion |
| Testability | Clock, ID generation, key provider and face engine are injected; ≥ 85 % line coverage on `Application` + `Domain`; 100 % of license state-machine transitions covered |
