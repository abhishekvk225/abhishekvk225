# Status board

Gate legend: ✅ passed · 🔄 in progress · ⬜ not started · ❌ failed. A module is **Complete** only when every gate is ✅ (see Definition of Done in `05-agents-and-roadmap.md`).

| Module | Design | DB | Backend | UI | Security | QA | Code review | DevOps | Final regression | Complete |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| M0 Design docs | 🔄 (awaiting your sign-off) | – | – | – | 🔄 | – | 🔄 | – | – | ⬜ |
| M1 Foundation | ✅ | ✅ | ✅ | – | ✅ conditions fixed (re-verify in M2 gate) | ✅ | ✅ changes applied | 🔄 | ⬜ | ⬜ |
| M2 Identity & Access | ✅ | ✅ | ✅ | 🔄 shell done; screens wire to API in M8 | ✅ re-verified: pass-with-conditions (Lows) | ✅ 136 API tests | changes applied, re-review | 🔄 | ⬜ | ⬜ |
| M3 Client Management | ✅ | ✅ | ✅ | ⬜ (M8) | 🔄 pass-with-conditions (M3-M1..M3 open) | ✅ | 🔄 approve-with-changes, Majors fixed | 🔄 | ⬜ | ⬜ |
| M4 Licensing & Metering | ✅ | ✅ | ✅ | ⬜ (M8) | 🔄 pass-with-conditions (see below) | ✅ 25 API + 17 domain tests | 🔄 Majors M-1..M-4 fixed | 🔄 | ⬜ | ⬜ |
| M5 Face Recognition | ✅ | ✅ | ✅ (mock engine; real provider pending) | ⬜ (M8) | 🔄 High fixed; re-verify + conditions below | ✅ 22 API + 8 unit tests | 🔄 Blocker + Majors fixed; rest below | 🔄 | ⬜ | ⬜ |
| M6 API Management | ✅ | ✅ | ✅ keys, auth, limits, logs, webhooks | ⬜ (M8) | 🔄 pass-with-conditions; Mediums M-1(clamp)/M-3/M-4 fixed | ✅ 30 API + 40 unit tests | 🔄 approve-with-changes; Majors M1–M4, M7 fixed | 🔄 | ⬜ | ⬜ |
| M7 Usage & Dashboards | ✅ | ✅ | ✅ dashboards, CSV exports, ledger verification, alerts, notification feed | ⬜ (M8) | ⬜ | 🔄 pass-with-conditions; M2/L1/L4/L7 fixed, M1 → M9 | ✅ 38 targeted + full suite green | 🔄 changes-requested; Majors 1–2 fixed, 3 partial | ⬜ | ⬜ |
| M8 Blazor Portal polish | ⬜ | – | – | 🔄 M8a done (BFF + real dashboards + Platform/Admin screens); M8b client-portal screens pending | ⬜ | 🔄 M8a: 273 component/unit tests + 3 portal-to-API end-to-end tests | ⬜ | ⬜ | ⬜ | ⬜ |
| M9 Hardening | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| M10 Release readiness | – | – | – | – | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |

## Environment notes
- .NET 10 SDK installed in the sandbox via apt (`dotnet-sdk-10.0`); SQL Server 2022 container runs through a manually started `dockerd`. NuGet is reachable.
- M1 verified here: solution builds with warnings-as-errors; Domain/Application/Architecture unit tests, Infrastructure tenant/RLS tests (real SQL Server) and API pipeline tests pass.
- **Not verified here**: `deploy/docker/Dockerfile.api` (build containers have no route to NuGet in this sandbox), CI workflows (need to run on GitHub), compose stack.

## Review outcomes (reports in `docs/reviews/`)
- **Code review M3/M4 — approve-with-changes, no blockers.** Fixed: M-1 admin ops load the license inside the transaction and retry on version conflict (regression: interleave test now requires every adjust to succeed first try); M-2 sweeper clears the tracker on conflict; M-3 cost rules filtered in SQL, `AsNoTracking`, memoised per request; M-4 per-user refresh-token query. Minors tracked for M10 cleanup.
- **Security review M2–M4 — no Critical/High.** Fixed: unique `(LicenseId, PrevHash)` ledger index (migration `LedgerUniqueChain`); client views no longer expose license notes, suspend reasons, staff actor ids or ledger reasons (M4-M4).
- **Open conditions, must land with M5/M6/M7:**
  - M4-M1: M5 derives idempotency keys server-side (or binds key → operation/request, 409 on mismatch).
  - M4-M2: M5 withholds the result when the charge fails after preflight.
  - M4-M3: nightly ledger-verification job + alert (M7); consider keyed HMAC anchor (M9).
  - M3-M1/M2/M3: redact platform-staff data from client audit/profile; target-privilege check in client user management.
  - M4-M5: cap / second approver on `licenses.adjust` (M9).

## M5 review outcomes (`docs/reviews/security-m5.md`, `code-m5.md`)
- **Fixed:** idempotency key now bound to the request target (blocker); image gate is async with a bounded queue and 503 load-shedding, JPEG decoded at reduced size (High, cross-tenant DoS); re-enrolling an existing person needs the recorded consent (insider takeover); NoMatch no longer reveals the score; audit rows no longer keep the person's reference; retention override capped at 10 years; template-cache staleness/single-flight/byte-based size limit; concurrency conflicts map to 409; provider/image failures are logged; one corrupt template no longer breaks identify.
- **Open (before M5 is Complete):** per-credential rate limit on face endpoints (arrives with M6 API keys); `limits.maxProfiles` is a soft limit under concurrent enrolment; shared recognition pipeline refactor (service is large); history rows keep image hash/IP without a purge job; sweeper batch size; real engine provider; gitleaks in CI; security re-verification of the High.

## M6 review outcomes (`docs/reviews/security-m6.md`, `code-m6.md`)
- **Fixed:** regenerate re-checks scopes and the key cap; a key's rate limit can no longer exceed the account's; `Webhooks:AllowUnsafeTargets` refused outside Development/Testing; last-used write is best-effort; poison webhook deliveries (undecryptable secret, bad URL) now count as failed attempts and reach abandonment, shutdown no longer counts as a failure; delivery rows purged after 30 days.
- **Open:** daily quota/rate counters are per node (persist or share for multi-node, M9); no per-endpoint dispatcher fairness/throttle on test+retry (M9); "20 consecutive failures" counts attempts not events; lease/batch not configurable; license/apikey webhook events arrive with M7; Lows L-1..L-9 and code Minors tracked for M10.

## M7 review outcomes (`docs/reviews/security-m7.md`, `code-m7.md`)
- **Fixed:** ledger verification is now exclusive across nodes (SQL application lock) as well as per process; one unreadable license no longer aborts the whole check; report dates are bounded (no more truncated CSV on absurd ranges); CSV neutralisation also covers zero-width/format characters; alert processing checks existing alerts with one set query per client instead of one per alert; wall-clock `Today` in CSV tests computed per call.
- **Open:** ledger hash is unkeyed SHA-256 – a DB-level attacker can recompute the chain, tail truncation undetected (keyed HMAC or external anchor, M9); on-demand verify is one synchronous scan (make async/per-client, M9); persistent break repeats Critical log nightly with no operator alert; no per-principal throttle on dashboards/exports; CSV streaming failure returns truncated body, no BOM; top-clients is gross while other figures are net; remaining test-clock flakiness (fake `TimeProvider` host) and code Minors → M10.

## M8a (portal BFF + admin screens) notes
- **Done:** cookie-session BFF (opaque HttpOnly/Strict/Secure cookie, server-side encrypted session store with idle + absolute expiry, transparent single-flight refresh with rotation/reuse handling, sign-out revokes the refresh token), antiforgery-protected static login, forced password change, open-redirect protection, portal separation + permission policies, typed API clients with ProblemDetails mapping, real admin/client dashboards, admin screens (clients, licenses, plans, cost rules, platform users, roles/permissions matrix, audit viewer, reports + CSV via BFF, ledger verification). Docs: docs/ui-notes.md, deploy/CONFIG.md.
- **API gaps found:**
  1. No platform-wide audit endpoint: the audit viewer works per client (`/admin/clients/{id}/activity|logins`). A global `GET /admin/audit` (filter by actor/action/entity) is needed for the page as specified.
  2. `GET /admin/roles` needs `roles.manage`, so a user with only `users.platform-manage` cannot pick roles when adding a platform user (the dialog shows the 403). Expose a read-only role list under `users.platform-manage`.
  3. The admin dashboard has no recent-activity feed, system alerts or period-over-period deltas (the UI derives alerts from webhook/license/error figures and shows no deltas); the client dashboard has no recent-recognitions list or account data (status, contact email), so the UI shows results by day and uses the session's client name.
  4. `GET /admin/clients` has no `sort` (`ClientListQuery`), so the sort headers on the client table do nothing yet.
  5. No system-settings / health endpoints: those two sidebar entries stay "Soon".
  6. The API's general per-IP limit sees the portal's address for everything except sign-in (which forwards `X-Forwarded-For`): trust the portal as a proxy for all calls or raise `RateLimiting:PerIpPerMinute`.
- **Open (M9):** session store is single-node by default (shared cache + sticky sessions or a distributed lock for multi-node refresh); permissions in the session are a snapshot taken at sign-in (the API enforces the live map, so menus only catch up at the next sign-in); no browser-level (Playwright) test of the interactive circuit.
