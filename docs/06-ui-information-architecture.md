# 06 — UI Information Architecture & Design System

> Owner: Blazor/MudBlazor UI Developer Agent · Status: **Draft v1**

## 1. Sitemap

**Auth** (`AuthLayout`): Sign in · Forgot password · Reset password · Change password (forced) · Suspended-account notice

**Admin portal** (`AdminLayout`, Super Admin) — sidebar groups
| Group | Pages |
|---|---|
| Overview | **Dashboard** (KPI cards, requests trend, license consumption trend, client-wise usage, expiring licenses, recent activity, system alerts) |
| Clients | Client list (search/filter/status chips) → **Client detail** tabs: Overview · Users · Licenses · Usage · Settings · Activity · Login history. Dialogs: create/edit, suspend (reason), reset password |
| Licensing | Licenses list · License detail (utilisation gauge, ledger table, actions: renew/adjust/suspend…) · Plans · Cost rules |
| Insights | Reports (usage/licenses/clients, CSV) · Audit logs |
| System | System settings · Roles & permissions · Platform users · Health |

**Client portal** (`ClientLayout`, Client Admin / Client User) — items trimmed by permission
| Group | Pages |
|---|---|
| Overview | **Dashboard** (license balance gauge, consumption, expiry countdown, request totals, success/no-match/failed donut, usage chart, recent recognitions, API usage, alerts, account card) |
| Recognition | Enroll · Verify · Identify (camera or upload) · Profiles (list/detail/erase) · History |
| Developer | API keys · API logs · API documentation · Webhooks |
| Account | License & usage (balance, expiry, ledger) · Users · Settings (Company, Security, Recognition, Notifications, Integration) · Activity & login history · Notifications |

## 2. Dashboard widgets (shared `ChartCard`/`StatCard` contract)
- KPI row: 4–6 `StatCard`s (value, delta vs previous period, icon, optional spark-line).
- Time-range selector (7d / 30d / 90d / custom) drives every widget through one `DashboardRangeState`.
- Charts via MudBlazor `MudChart` (line, bar, donut); colour tokens from the theme; always paired with an accessible data table toggle.
- **License gauge** for non-technical users: "**842 of 1,000 credits left · expires in 23 days**" with colour state (green > 30 %, amber 10–30 %, red < 10 % or < 7 days) and plain-language help text.

## 3. Component library (built once in M1/M2, reused everywhere)
`PageHeader` · `StatCard` · `ChartCard` · `LicenseGauge` · `DataTable<T>` (server paging/sort/filter, column chooser, responsive stack on mobile) · `FilterBar` · `StatusChip` (client/license/key/outcome → colour+icon, never colour alone) · `ConfirmDialog` (+ type-to-confirm variant) · `FormDialog<T>` · `SecretRevealDialog` (show once, copy button, "I stored it" confirmation) · `EmptyState` · `ErrorState` (retry) · `SkeletonCard/Table/Chart` · `ActivityFeed` · `FaceCapture` (getUserMedia, preview, retake, size/format guard) · `ApiResult<T>` handling helper · `AppSnackbar` service (success/info/warn/error, correlation id on errors).

## 4. Design tokens & theme
- Single `MudTheme`: light + dark palettes (WCAG AA contrast), primary/secondary/semantic colours, 8-px spacing scale, radius 8–12 px, elevation ≤ 2, Inter (or system) typography with a fixed type scale (H1–H6, body, caption).
- Theme preference stored per user (+ `prefers-color-scheme` default). Density option for tables.
- Icons: Material (MudBlazor Icons) only — one icon per concept (documented map: Client=Business, License=VpnKey/Badge, Credits=Toll, Face=Face, API=Api, Audit=History…).

## 5. UX rules (acceptance checklist for every page)
1. **States**: skeleton while loading → empty state with call-to-action → error state with retry + correlation id → success.
2. **Forms**: labels, helper text, inline validation using the same rules as the API, disabled-while-submitting, server field errors mapped back to fields.
3. **Destructive/irreversible** actions (suspend, revoke key, erase profile, adjust credits) → `ConfirmDialog` stating consequence; high-impact ones need typed confirmation + reason.
4. **Feedback**: every mutation ends in a snackbar; no silent failures; no full page reload (interactive components, enhanced nav).
5. **Responsive**: sidebar → drawer on `< md`; tables → card list on `xs`; charts reflow; touch targets ≥ 44 px.
6. **Accessibility**: keyboard reachable, focus trap in dialogs, `aria-live` for toasts, alt text, semantic headings, motion-reduced respects OS setting.
7. **Plain language** for client-facing license/usage text; no jargon like "ledger" in client UI (use "credit history").
8. **Security in UI**: permission-trimmed nav is convenience only (API enforces); secrets never rendered after reveal dialog; no `MarkupString` with user data.

## 6. M14 (planned) — Document verification navigation and screens
Full spec: `07-document-verification.md` §11.

**Client portal** — new sidebar group **Document verification** (after Recognition), items permission-trimmed:
| Item | Route | Permission |
|---|---|---|
| Overview (module catalogue) | `client/verification` | `verification.read` |
| Bank statement | `client/verification/bank-statement` | `verification.bank-statement.submit` |
| PAN | `client/verification/pan` | `verification.pan.submit` |
| Passport | `client/verification/passport` | `verification.passport.submit` |
| Verification history | `client/verification/history` | `verification.read` |

- **Upcoming modules** (Aadhaar, Driving Licence, Voter ID, DigiLocker, additional government documents, KYC and fraud detection) appear only as muted, non-clickable cards with an "Upcoming" chip on the Overview page ("Not available yet. No credits are used."); they are not sidebar entries and have no pages.
- **Submit pages:** file picker with client-side pre-checks (type, size, signature), progress, inline field errors, consent reference, cost hint; after `202` the user lands on the details page, which polls until a terminal state.
- **History** (`DataTable`): request id, type, submitted, status, review flag, provider verification, reference, credits (if authorised). Row action menu: View details, Download report (JSON/HTML, transactions CSV), Retry provider check, Cancel, Delete data. No duplicate sidebar entries for these.
- **Details:** stage stepper (Uploaded · Extracted · Rules checked · Consistency checked · Official verification), review reasons, official-verification card (the only place a positive "verified" appears), check results table, field matches, extracted fields (masked identifiers; transactions paged), credits, timestamps/correlation id. Wording comes from one table; "authentic/genuine/valid/active" are never shown.

**Admin portal** — new group **Verification**: `admin/verification` (volume, success/failure/review, provider errors, credits, queue depth; modules table with enable/disable, provider configuration status without secrets, credit cost per operation linking to Cost rules), `admin/verification/requests` (tenant-scoped, metadata-only investigation), `admin/verification/audit` (audit history of administrative changes). Permissions `verification.admin.read` / `verification.admin.configure`.

New shared components: `DocumentUpload`, `VerificationStatusChip`, `ExternalStateChip`, `StageStepper`, `CheckResultsTable`, `FieldMatchList`, `ReviewReasonsAlert`, `ExtractedFieldsCard`, `UpcomingModuleCard`, `ModuleCard`.
