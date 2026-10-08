# Security review: M8a (Blazor portal BFF + admin screens)

Reviewer: Security Agent (independent). Date: 2026-10-08. Method: static review, no Docker, no product code changed.
Baseline: docs/04-security-strategy.md, docs/ui-notes.md "M8a: BFF and session design", deploy/CONFIG.md "Blazor portal".

Scope read in full: `src/Web/Blazor/Security/*`, `Services/Api/*`, `Program.cs`, `Pages/Login.razor`, `Pages/ChangePassword.razor`,
`Pages/ForgotPassword|ResetPassword.razor`, `App.razor`, `Routes.razor`, layouts, all `Pages/*/_Imports.razor` and `[Authorize]` attributes,
`Components/Admin|Forms|Can|ClientPicker|UserMenu|SecretRevealDialog|FaceCapture|ErrorState`, `Services/CurrentUserState`, `deploy/CONFIG.md`,
`tests/Api.IntegrationTests/PortalBffEndToEndTests.cs`; API cross-checks: `AuthService.LoginAsync`, CSV controller/`CsvFormat`, rate-limit settings.

Tools: `dotnet list package --vulnerable --include-transitive` on `NexaVerify.Web`: no vulnerable packages (MudBlazor only direct dependency).
gitleaks binary is not installed here (CI runs `gitleaks-action` in `.github/workflows/ci.yml:50`); manual grep for secrets/hardcoded credentials in `src/Web/Blazor`: none.
Grep for `MarkupString`, `AddMarkupContent`, `innerHTML`, `IgnoreQueryFilters`, raw SQL: none in the portal. Not run: dynamic tests, bUnit/WAF suites (no DB/Docker).

## Verdict: PASS-WITH-CONDITIONS

No Critical or High finding. The core BFF promises hold in code: the token pair lives only in the encrypted server-side store, the cookie holds a
Data-Protection-protected opaque id, login rotates to a new random id, sign-out/refresh-rejection/idle/absolute expiry are enforced on every request
and every 30 s per circuit. Conditions before production use with many users: fix M-1 and M-2; the Lows are tracked hardening.

## Findings

| ID | Sev | Location | Summary |
|----|-----|----------|---------|
| M-1 | Medium | `Services/Api/ApiGateway.cs:140`, `Security/TokenRefresh.cs:37`, `Services/Api/AuthApiClient.cs:15-19` | Only sign-in forwards the end-user IP. Forgot/reset-password and token refresh reach the API from the portal's address, so one anonymous client can exhaust the shared per-IP credential-endpoint bucket for every user. |
| M-2 | Medium | `Program.cs:61-77` | Portal `ForwardedHeaders` accepts a `/0` network (the API rejects it); an over-broad trust list lets a client spoof its IP into the lockout/rate-limit history sent to the API. Also unguarded `Parse` calls. |
| L-1 | Low | `Security/AuthEndpoints.cs:51,110-121`, `Components/UserMenu.razor:41` | Sign-out is a state-changing GET: cross-site top-level navigation clears the victim's cookie (forced logout, "logout CSRF"). |
| L-2 | Low | `Security/PortalAuthentication.cs:157-161`, `Layouts/AdminLayout.razor:12-18` | Permission/portal claims are a snapshot for the whole session and circuit; revalidation checks only existence. Role removal or user disable is not reflected in menus/page policies until re-login. |
| L-3 | Low | `Services/Api/ApiGateway.cs:170`, `Security/PortalServiceExtensions.cs:55-57` | HttpClients follow redirects by default (login body and bearer token could be replayed to a redirect target). |
| L-4 | Low | `Security/PortalServiceExtensions.cs:43-47` | Data Protection key ring has no at-rest protection and no startup guard that a path/persistence is configured. |
| L-5 | Low | `Security/SessionStore.cs:46,135`, `Security/TokenRefresh.cs:67` | Per-session semaphores are never removed for sessions that expire passively (slow memory growth); removal while held can allow two concurrent writers. |
| L-6 | Low | `Program.cs:44`, `Security/SecurityOptions.cs:21-27`, `Security/PortalServiceExtensions.cs:33` | Startup guards are partial: nothing refuses `RequireSecure=false`, `SameSite=None`, `Headers:Enabled=false` in Production; `AllowedHosts` guard covers Production only; `Testing`/`UiDemo` environment names relax https and stub rules. |
| L-7 | Low | `Session:CookieName` default `nv.session` (`SessionOptions.cs:11`) | Cookie lacks the `__Host-` prefix (cookie tossing from a sibling subdomain). |
| I-1 | Info | `Security/AuthEndpoints.cs:60-80` | No portal-side throttling on `/auth/login` or the anonymous circuits; relies fully on the API. |
| I-2 | Info | `Security/SecurityHeadersMiddleware.cs:65` | CSP keeps `style-src 'unsafe-inline'` (documented MudBlazor need); no `Cache-Control: no-store` on authenticated HTML shells. |
| I-3 | Info | `tests/.../PortalBffEndToEndTests.cs` | Token-leak scan covers HTTP responses only; no test covers SignalR frames, logout CSRF, forwarded-header spoofing, or the `/0` guard. |

### M-1 (Medium) Shared API rate-limit bucket for portal-originated credential calls
- Evidence: `ApiGateway.ExecuteAsync` sets `X-Forwarded-For` only when `ApiCallOptions.ClientIp` is set. Only `AuthApiClient.LoginAsync` sets it (`AuthApiClient.cs:9-10`).
  `ForgotPasswordAsync`/`ResetPasswordAsync` use `ApiCallOptions.None` (no IP) and `HttpRefreshTokenExchange.ExchangeAsync` (TokenRefresh.cs:37) sends none.
  The API limits "credential endpoints (login, refresh, forgot/reset password)" per client IP (`src/Api/Configuration/HostingOptions.cs:41`), and deploy/CONFIG.md:87 already notes the shared bucket for sign-ins.
- Attack: an anonymous attacker opens a Blazor circuit on `/forgot-password` and submits repeatedly (or posts to `/auth/login`, which does forward the attacker IP, so the forgot-password path is the one that hurts). All of those API calls carry the portal's IP, exhaust the portal's bucket, and every signed-in user's `auth/refresh` then gets 429. A 429 keeps the session (`TokenRefresh.cs:99`) but access tokens expire within minutes, so the whole portal degrades; legitimate password resets are blocked too.
- Impact: availability of the entire portal for all users from one unauthenticated client (also audit/IP attribution: authenticated admin calls are recorded with the portal address, not the staff member's).
- Fix: capture the connection's remote address once per circuit (`CircuitHandler` + `IHttpContextAccessor` at circuit start, scoped `IClientAddress`) and pass `ClientIp` for forgot/reset; for refresh, forward the address stored on the session at login (add `ClientIp` to `PortalSession`) or have the API partition refresh limiting by token family/user instead of IP. Add the same for audit attribution if desired. Optionally add a portal-side limiter in front of the anonymous pages.
- Verify: integration test that 40 forgot-password calls from client A do not 429 a refresh for client B.

### M-2 (Medium) Portal forwarded-header trust list not hardened like the API
- Evidence: `Program.cs:61-77` accepts any `KnownNetworks` entry, including `0.0.0.0/0`/`::/0`, and uses `IPAddress.Parse`/`int.Parse(parts[1])` unguarded. API counterpart rejects `/0` (`src/Api/Startup/ServiceCollectionExtensions.cs:164`). CONFIG.md:83 says "same rules as the API", which is not true for `/0`.
- Attack: if an operator (or a templated deploy) trusts `0.0.0.0/0`, any client sends `X-Forwarded-For: <random>` to `/auth/login`; `RemoteIpAddress` becomes attacker-chosen (AuthEndpoints.cs:85) and is forwarded to the API, which trusts the portal as a known proxy. Per-IP login throttling and login-history IPs can then be evaded or framed on a victim IP. (Per-account lockout still holds.) Without the setting the header is ignored; no spoof path exists in the default config.
- Fix: copy the API's validation (reject prefix length 0, validate parse errors with a clear startup message). Add a unit test mirroring the API's.

### L-1 (Low) Logout via GET
- `app.MapGet("/auth/signed-out", ...).AllowAnonymous()`. With `SameSite=Strict` a cross-site request carries no cookie, so the server session is not destroyed (the doc's claim is correct for that part). However the handler always calls `http.SignOutAsync`, which emits a `Set-Cookie` deletion; a cross-site top-level navigation (link/redirect from an attacker page) is processed in the target's first-party context and deletes the victim's cookie. The orphaned server session lives until idle expiry. Result is a forced logout nuisance, not a takeover.
- Fix: make sign-out `POST` with antiforgery (form in `UserMenu` or a tiny static page), or reject requests whose `Sec-Fetch-Site` is `cross-site`. Keep the idle "expired" redirect as a separate GET that does not clear anything.

### L-2 (Low) Permission snapshot staleness
- `SessionAuthenticationStateProvider.ValidateAuthenticationStateAsync` returns true whenever the session exists; claims (`Roles`, `Permissions`, `Portal`) are those captured at login (PortalAuth.cs:48-66) for up to `AbsoluteTimeoutHours` (12 h). `CurrentUserState` is loaded once in the layout's `OnInitializedAsync`.
- Effect: UI shows menus/pages for permissions already removed; every action still fails at the API (it is the enforcement point), so no escalation. A disabled user's portal session also persists until the API refuses the next refresh (access-token TTL), since the portal keeps no user-to-sessions index.
- Fix (optional hardening): re-read `/auth/me` on refresh and update `Permissions`/`Roles`/`Portal` in the session (and terminate the session if `Portal` changes); in revalidation compare a permission hash; reload `CurrentUserState` on auth-state change events.

### L-3 (Low, Potential) Automatic redirects on API clients
- `AddHttpClient` leaves `AllowAutoRedirect=true`. A 307/308 from the API origin (misrouted proxy, HTTP-to-HTTPS rewrite, compromised hop) would replay the login POST body (password) or authenticated request to the redirect target; .NET drops `Authorization` only on some cross-origin cases. Not demonstrable without an attacker-controlled redirector on the API path; confirm by a handler test.
- Fix: `ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })` for both named clients, and treat 3xx as `API_UNAVAILABLE`.

### L-4 (Low) Data Protection keys
- Without `DataProtection:KeyPath` the default per-machine ring is used (ephemeral in containers: all sessions die on restart, acceptable but unannounced). With a path, `PersistKeysToFileSystem` stores keys as plaintext XML next to the process; anyone who can read the directory plus the cache can decrypt session payloads.
- Fix: `ProtectKeysWithCertificate`/platform key store, restrictive directory ACL, and a Production startup warning/guard when `KeyPath` is empty (CONFIG.md already advises it).

### L-5 (Low) Lock dictionary growth
- `DistributedSessionStore._locks` and `TokenRefreshCoordinator._locks` add one `SemaphoreSlim` per session id and remove it only on explicit `RemoveAsync`/end. Sessions that simply expire (cache expiry, browser closed) leak an entry each. `RemoveAsync` also drops a semaphore other waiters may hold, so a concurrent `Update` could briefly proceed under a fresh lock (lost update of a rotated refresh token would end the session through reuse detection).
- Fix: use a striped lock array or a keyed-lock helper with reference counting; evict on expiry.

### L-6 (Low) Startup guards
- Missing guards: Production with `Security:Cookies:RequireSecure=false` or `SameSite=None`/`Lax` (SecurityOptions parse accepts `None`), `Security:Headers:Enabled=false`, `ContentSecurityPolicyEnabled=false`. `AllowedHosts` is enforced only in `IsProduction()` (not Staging). `Testing`/`UiDemo` environment names allow plain-http API and (UiDemo) any-password stubs; setting `ASPNETCORE_ENVIRONMENT=UiDemo` in a real deployment bypasses everything. The stubs themselves are correctly unreachable otherwise (PortalServiceExtensions.cs:23, DI chooses stubs only for Auth and Dashboard clients).
- Fix: fail startup in any non-Development/Testing/UiDemo environment for the three settings above; use `!IsDevelopment()` for `AllowedHosts`; document that UiDemo must never be deployed beyond a design-review host (or require an explicit extra flag).

### L-7 (Low) Cookie prefix
- Use `__Host-nv.session` by default in non-Development (Secure, Path=/, no Domain are already met), and `__Host-nv.af` for antiforgery.

### Info
- I-1: `/auth/login` has no portal limiter; `form` reading uses framework defaults (4 MB). Unauthenticated circuits are unbounded (SignalR). Consider `AddRateLimiter` on `/auth/*` and `MaximumParallelInvocationsPerClient`/circuit retention caps.
- I-2: `style-src 'unsafe-inline'` is a known MudBlazor trade-off; scripts are nonce+self only (good), `frame-ancestors 'none'`, `form-action 'self'`, `object-src 'none'`, `base-uri 'self'` present. Authenticated HTML shell is not `no-store`, but it carries no user data (prerender off, data arrives over the circuit).
- I-3: Add tests: a SignalR/circuit scan for tokens (bUnit test already shows no token in `CurrentUserState`), cross-site `Sec-Fetch-Site` sign-out, `ForwardedHeaders` with `/0`, redirect handling, M-1 bucket isolation.

## Checklist results (items requested)

| Area | Result | Evidence |
|------|--------|----------|
| Token never reaches browser | OK | Cookie ticket holds only `nv:sid` (`PortalAuthentication.cs:18-19`); principal rebuilt server-side (`:21-45`) contains no token; `PortalSession.PrintMembers` redacts tokens from `ToString`/logs (`PortalSession.cs:47-51`); logs record status/code only (`AuthEndpoints.cs:89`, `TokenRefresh.cs:102,108`); `ProblemMapper` never returns raw 5xx text; CSV is streamed without upstream headers; e2e test asserts no JWT/refresh token in login/shell/CSV/logout responses. No `MarkupString`, no JS interop carrying tokens. |
| Cookie flags / fixation | OK | `HttpOnly`, `Secure` (config default true), `SameSite=Strict`, session cookie (`IsPersistent=false`), new 256-bit id per login (`SessionStore.cs:57`, `PortalAuth.cs:50`), previous session removed on re-login (`AuthEndpoints.cs:94-98`). Planted cookie cannot be fixated: id is server-chosen after authentication. See L-7. |
| Session store | OK | Payload encrypted with Data Protection (`SessionStore.cs:157,169`); idle + absolute expiry on each read (`:68`); cache TTL = min(idle+1 min, absolute) (`:170-172`); absolute lifetime cannot be extended by `UpdateAsync` (`:110`); tamper or key loss means "no session" (`:159-164`). In-memory cache by default (single node), documented. See L-4, L-5. |
| CSRF / antiforgery | OK with L-1 | Login POST validates antiforgery explicitly (`AuthEndpoints.cs:67`; endpoint-level `DisableAntiforgery` is deliberate so failures redirect rather than 400); `UseAntiforgery` is in the pipeline; antiforgery cookie is `HttpOnly`/`Secure`/SameSite; all other mutations go through the circuit (not cross-site callable) and the BFF's only other endpoint (CSV) is a read-only GET. Logout CSRF: L-1. |
| Open redirect | OK | `ReturnUrl.IsSafe` rejects non-`/` start, `//`, `/\`, backslash, control chars, decoded `//`, `://`, `/auth/`, `/login`; `ResolveForPortal` confines to `/admin` or `/client`; `returnUrl` is re-validated on the login page render and on POST; `signed-out` has no redirect parameter. |
| XSS | OK | Razor encoding everywhere; no `MarkupString`; API text (messages, correlation ids, names) is rendered as text; `ref` is regex-whitelisted; attribute sinks are portal constants (`PortalShell` hrefs) or sniffed image data URLs (`FaceCapture`, type from `ImageSniffer`, name sanitised). |
| CSP / Blazor Server / clickjacking | OK | nonce on all three script tags, `connect-src 'self' wss:`, dynamic `import("./js/face-capture.js")` allowed by `'self'`, `frame-ancestors 'none'` + `X-Frame-Options: DENY`, `nosniff`, `no-referrer` (protects the reset-link token in the URL), `Permissions-Policy` camera self only. Headers set via `OnStarting`, so they also cover error and re-executed pages. |
| SignalR circuit authz / isolation | OK with L-2 | `AuthenticationStateProvider` scoped per circuit; initial principal is the session-built one; 30 s revalidation (`PortalAuthentication.cs:155`); API calls re-read the session on every request (`TokenRefresh.cs:146`), so a killed session stops API access immediately even while the circuit lives; session id is resolved from the circuit's own auth state, never from a shared singleton. |
| Authorization per page / portal separation | OK | Every page under `Pages/Admin` has `[Authorize(perm:...)]` plus folder-level `PlatformPortal`; `Pages/Client` has `ClientPortal` + perm; `/change-password` is `SignedIn`; `/login`, `/forgot-password`, `/reset-password`, `/not-found`, `/forbidden` are intentionally anonymous; `/styleguide` renders nothing outside Development. Pending password change fails every policy except `SignedIn` (`PortalAuthentication.cs:111-114`). No endpoint outside these exists except `/` and `/error`. `Can`/`NavigationCatalog` are cosmetic; API enforces. |
| BFF CSV endpoint | OK | `RequireAuthorization(perm:reports.read, PlatformPortal)` (both required), anonymous gets 302; `from`/`to` parsed as `yyyy-MM-dd` and re-serialised (no parameter or CRLF injection), fixed filename (no header injection), `no-store`, `nosniff`, `attachment`; API CSV neutralises formula cells (`CsvFormat.Text`). |
| Refresh single-flight / cross-session leak | OK | Lock per session id; late arrivals reuse the new token (`TokenRefresh.cs:86-90`); exchange ignores caller cancellation; 4xx ends the session, 429/5xx keeps it; retry at most once with body replay; token for a request is read by session id passed as a request option, so one circuit cannot use another session's token. See L-5. |
| X-Forwarded-For handling | OK with M-1, M-2 | The IP sent to the API is `Connection.RemoteIpAddress`, never a client-supplied header, unless the proxy trust list is wrong (M-2); header value is a parsed IP so no header injection. |
| SSRF via `Api:BaseUrl` | OK | Operator-only setting, absolute https required outside Development/Testing/UiDemo, credentials in URL rejected, path from code only (ids are `Guid`, query via `Uri.EscapeDataString`, no user-supplied host or absolute URL can reach `HttpRequestMessage`). See L-3 for redirects. |
| Error leakage / enumeration | OK | 400/401/404 login failures are one message; the 403 "account not active" is only returned by the API after a correct password (`AuthService.cs:157-159`); `ref` shows correlation ids only; production exception handler returns a generic problem. Account lockout and unknown-email responses are identical (e2e asserts it). |
| Stub clients in prod | OK | Refused unless Development or UiDemo (`PortalServiceExtensions.cs:23`), covered by `StartupGuardTests`. See L-6 for the UiDemo caveat. |
| Multi-tenant confusion | OK | The portal never supplies a tenant id for client-portal data; admin pages pass explicit `clientId`/ids to platform-only endpoints that the API authorises by permission; `ClientPicker` only lists what `GET admin/clients` returns for that platform user; id route parameters are `Guid` constrained; the session store is keyed by random id so one circuit cannot address another session. Client-portal screens are M8b. |
| Dependencies / CI | OK | No vulnerable NuGet packages; secret scanning and CodeQL/dependency-review workflows exist. |

## Re-review
First review of M8a; no earlier items to re-verify. Re-review requested after M-1 and M-2 are fixed (M-1 needs an integration test, M-2 a unit test).
