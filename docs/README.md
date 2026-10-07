# Face Recognition SaaS — Design Documentation

A multi-tenant, commercial face-recognition platform: ASP.NET Core (Clean Architecture) + EF Core + SQL Server + Blazor/MudBlazor.

| Doc | Contents | Owner |
|---|---|---|
| [01 — Architecture](01-architecture.md) | Style, dependency rule, ADRs, folder structure, modules, multi-tenancy, licensing & face-module design, frontend architecture, NFRs | Solution Architect |
| [02 — Database design](02-database-design.md) | Conventions, ERD, every table/index/constraint, RLS, SQL objects, migrations/seed | Database Developer |
| [03 — API specification](03-api-specification.md) | Conventions, error catalogue, all endpoints (auth, admin, client, faces), dashboard metric definitions, rate limits, webhooks | Solution Architect / Backend |
| [04 — Security strategy](04-security-strategy.md) | Threat model, authN/Z, permission catalogue & role matrix, headers/CORS/CSRF, crypto, privacy, OWASP mapping | Security |
| [05 — Agents & roadmap](05-agents-and-roadmap.md) | Agent roster, module pipeline & Definition of Done, milestones, risks, open decisions | Orchestrator |
| [06 — UI information architecture](06-ui-information-architecture.md) | Sitemap, dashboard widgets, component library, theme, UX checklist | UI Developer |
| [STATUS](STATUS.md) | Live module/gate status board | Orchestrator |
| `reviews/` | Per-module security, QA and code-review reports | Reviewer agents |

Agent definitions: [`.claude/agents/`](../.claude/agents).
