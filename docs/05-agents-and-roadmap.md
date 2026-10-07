# 05 — Agents, Development Process & Roadmap

## 1. Agent roster

Agents are defined as Claude Code subagents in [`.claude/agents/`](../.claude/agents) (loaded at session start). Each has a narrow role, an explicit scope of files it may change, and a required output. **Reviewers (Security, QA, Code Review) never fix code they review** — they report findings; the owning developer agent fixes. This keeps the gates independent.

| # | Agent | File | Writes code? | Owns |
|---|---|---|---|---|
| 1 | Solution Architect | `solution-architect.md` | docs only | `docs/01,03,04`, ADRs, module boundaries, final say on technical disputes |
| 2 | Database Developer | `database-developer.md` | yes (`Domain` entity shapes with Backend, `Infrastructure/Persistence`, SQL scripts) | `docs/02`, EF configurations, migrations, indexes, RLS, SQL objects |
| 3 | Backend .NET Developer | `backend-developer.md` | yes (`Domain`, `Contracts`, `Application`, `Infrastructure`, `Api`) | services, APIs, auth, licensing, face providers |
| 4 | Blazor/MudBlazor UI Developer | `blazor-ui-developer.md` | yes (`Web/Blazor`) | layouts, components, pages, theme, UX consistency |
| 5 | Security Reviewer | `security-reviewer.md` | **no** (report + add security tests only) | OWASP/tenant-isolation review, threat-model upkeep |
| 6 | QA / Test | `qa-tester.md` | tests only (`tests/**`) | test plans, unit/integration/E2E, regression, license & isolation suites |
| 7 | Code Reviewer | `code-reviewer.md` | **no** | SOLID/DRY/perf/standards review, refactoring proposals |
| 8 | DevOps | `devops-engineer.md` | yes (`deploy/**`, `.github/**`, build props) | Docker, IIS, CI/CD, config, observability |

The orchestrator (main session) coordinates hand-offs, merges results, keeps the status board in `docs/STATUS.md`, and is the only party that marks a module complete.

## 2. Module delivery pipeline (per module)

```
 ① Architect ─▶ ② Database ─▶ ③ Backend ─▶ ④ UI ─▶ ⑤ Security ─▶ ⑥ QA ─▶ ⑦ Code Review ─▶ ⑧ DevOps ─▶ ⑨ QA regression
 (contract)     (schema+EF)    (APIs+logic)  (Blazor)   (review)      (test)    (review)         (deploy cfg)   (final)
                                   ▲                         │             │           │                            
                                   └───────── findings ──────┴─────────────┴───────────┘  (fix → re-review of the affected gate)
```

Rules:
1. A step starts only when the previous step's output exists in the repo (design doc delta → migration → API + unit tests → UI → …).
2. Findings are written to `docs/reviews/<module>-<gate>.md` with severity (Critical/High/Medium/Low). **Critical/High must be fixed and re-verified by the reviewing agent before the gate passes.** Medium/Low are fixed or consciously deferred with a ticket.
3. Tests are written *with* the code (steps ③/④), not only at step ⑥; QA extends them with edge cases, isolation and concurrency suites.
4. Steps ⑧/⑨ run on module completion for the module's deployable slice and again for the whole system at the end (final regression).

### Definition of Done (module) — all must be true
- [ ] Design deltas merged into `docs/` (and ADRs for new decisions)
- [ ] Migration + EF configuration reviewed; idempotent SQL script generated
- [ ] APIs match `docs/03`; Swagger documented; thin controllers; no business logic outside Application
- [ ] UI: loading, empty, error, validation, confirmation and responsive states for every screen
- [ ] Unit tests (Domain/Application) + integration tests (DB, API) green; **tenant-isolation and authorization tests** for every new endpoint
- [ ] License-affecting code: concurrency, idempotency and ledger-invariant tests green
- [ ] Security review: no open Critical/High
- [ ] Code review: no open Critical/High; duplication/perf notes addressed or ticketed
- [ ] CI green (build, analyzers, tests, CodeQL, secret scan); deployment config updated
- [ ] `docs/STATUS.md` updated by orchestrator

## 3. Roadmap (milestones are vertical slices)

| Milestone | Scope | Exit criteria (beyond DoD) |
|---|---|---|
| **M0 — Design** *(this deliverable)* | Architecture, DB, API, security, roadmap, agent definitions | Reviewed & signed off by you |
| **M1 — Foundation** | Solution + `Directory.*`, building blocks (`Result`, clock, ids), `AppDbContext`, audit interceptor, tenant context + query filters + write guard + **RLS**, Serilog, ProblemDetails, health checks, Swagger, CI skeleton, Docker compose (SQL Server) | Tenant-isolation test harness exists and passes against a real SQL Server container |
| **M2 — Identity & Access** | Identity, login/refresh/logout/reset, JWT, RBAC (permissions, dynamic policies), seed Super Admin, login history, lockout, rate-limit on auth | AuthN/Z test matrix green; token-reuse detection proven |
| **M3 — Client Management** | Clients CRUD + status machine, first Client Admin provisioning, user management, client settings framework, client keys, admin activity/login views | Suspend/deactivate takes effect ≤ 30 s on portal and API |
| **M4 — Licensing & Metering** | Plans, licenses + state machine, cost rules, FEFO atomic deduction, ledger with hash chain + immutability, idempotency, expiry sweeper, low-balance alerts, reconciliation job | 1,000-way concurrent deduction test never over-consumes; ledger verification passes; all four "prevent usage" cases proven |
| **M5 — Face Recognition** | `IFaceEngine` + Mock + first real provider (ONNX), enroll/verify/identify/detect, matcher cache, image pipeline hardening, history, erasure, retention job, per-client config | Provider swap demonstrated by config only; no embedding ever returned; billing integration green |
| **M6 — API Management** | API keys (create/rotate/revoke), key auth handler, rate limiting + quotas, request logging, webhooks (outbox, signing, SSRF guard), in-portal API docs | Key shown once (tested); limits enforced per key/client |
| **M7 — Usage & Dashboards** | Usage rollup job, admin + client dashboard APIs, reports/CSV export, notifications | Dashboards match ledger/request totals (reconciliation test) |
| **M8 — Blazor Portal** *(UI tracks start earlier per module — see §4)* | Component library, themes, Admin & Client apps, all screens, FaceCapture, accessibility pass | E2E critical journeys green on desktop/tablet/mobile viewports |
| **M9 — Hardening** | Full security review, MFA for Super Admin, pen-test fixes, performance tests, backup/restore drill, load test | ASVS-L2 checklist signed; no open Critical/High |
| **M10 — Release readiness** | Docker + IIS packages, environment config, CI/CD to staging, monitoring/alerting dashboards, runbooks; **final QA regression** | Staging soak passes; go/no-go |

## 4. How UI work interleaves
To avoid a "big-bang UI" at the end, the UI Developer agent builds the **shell + component library in M1–M2** (against a stub API), then each backend module's screens immediately after its APIs land (M3 client mgmt screens, M4 license screens, …). Milestone M8 is the polish, consistency, accessibility and E2E pass.

## 5. Risks & mitigations
| Risk | Mitigation |
|---|---|
| Biometric/legal exposure | consent + retention + erasure built in (M5); legal review before launch |
| Real face-engine quality/perf | provider abstraction + Mock; benchmark harness with a labelled set before choosing the default provider |
| Tenant leakage | 5-layer isolation + release-gate tests |
| License double-charging | atomic UPDATE + idempotency + reconciliation job + chaos/concurrency tests |
| Blazor Server scale-out | sticky sessions/Azure SignalR; BFF keeps API stateless; documented in deploy guide |
| Scope size | strict vertical slices, DoD gates, `docs/STATUS.md` visibility |
| No .NET SDK in some sandboxes | CI is the source of truth for build/test; devcontainer provided in M1 |

## 6. Open decisions for you (defaults assumed if you don't object)
| # | Question | Assumed default |
|---|---|---|
| D1 | Face engine for the first real provider | Local ONNX (ArcFace-class), on-prem, no third-party data sharing |
| D2 | Billing policy | 1 credit per Enroll/Verify/Identify, charged when a definitive answer is produced (match or no-match); not charged on errors/no-face/low-quality |
| D3 | Hosting target first | Docker (Linux) primary, IIS supported |
| D4 | Blazor hosting model | Interactive Server as BFF (ADR-06) |
| D5 | Tenants per user | One client per user in v1 (schema allows many later) |
| D6 | Payments/subscriptions | Out of v1; `Plans` table and ledger are ready for it |
| D7 | Repo layout | This repo (currently your GitHub profile repo) hosts the solution under `src/` as requested; say so if you'd rather use a dedicated repo |
