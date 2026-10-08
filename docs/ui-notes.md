# UI notes (phase UI-1, M8a)

Project: `src/Web/Blazor/NexaVerify.Web.csproj` (Blazor Web App, Interactive Server, no prerender; references `Contracts` only).
Tests: `tests/Web.ComponentTests` (bUnit, unit tests of the BFF pieces, WebApplicationFactory for headers/startup guards) and
`tests/Api.IntegrationTests/PortalBffEndToEndTests.cs` (the portal against the real API on a real database).

## Run
```
dotnet run --project src/Api                 # https://localhost:7101
dotnet run --project src/Web/Blazor          # http://localhost:5102 (Development, Api:BaseUrl = https://localhost:7101)
dotnet test tests/Web.ComponentTests
```
Routes: `/login`, `/forgot-password`, `/reset-password?email=..&token=..`, `/change-password`, `/forbidden`, `/admin/**` (platform users only),
`/client` (client users only), `/styleguide` (Development only).
Design review without an API: `Ui:UseStubClients=true` (Development or `UiDemo` only, accepts any password; an email containing "admin" opens the admin console, `fail@...` shows the error state). Any other environment refuses to start with it.

## What exists
- `Theme/NexaTheme.cs` single MudTheme (light + dark, AA-checked pairs); `Services/ThemeService` (localStorage + prefers-color-scheme).
- Layouts: `AuthLayout`, `AdminLayout`, `ClientLayout` (both use `Components/PortalShell`: responsive drawer, theme menu, notifications placeholder, user menu, permission-trimmed nav from `Services/NavigationCatalog`). The layouts fill `CurrentUserState` from the authenticated principal.
- Components (`Components/`): PageHeader, StatCard, ChartCard (+SimpleChart, SeriesTable), LicenseGauge (+LicenseHealth thresholds), DataTable<T>/DataColumn<T>, FilterBar, StatusChip (+StatusMap), ConfirmDialog, FormDialog<T>, SecretRevealDialog (+`DialogExtensions`), EmptyState, ErrorState, Skeleton{Card,Table,Chart}, ActivityFeed, FaceCapture (+`wwwroot/js/face-capture.js`), PageState<T>, AlertList, RangeSelector, DashboardSkeleton.
- M8a additions: `Can`, `DetailList`/`DetailItem`, `PermissionMatrix`, `ClientPicker`, `AccessDenied`, `RedirectToLogin`, `UiResults` (success/error snackbar ending for every mutation), form-field components under `Components/Forms/*`, admin building blocks under `Components/Admin/*` (client tabs, `LicenseTable`, `CostRuleList`, `ClientAuditPanel`). `StatusMap` learned the API's status names; `ErrorState` offers "Sign in again" for a dead session.
- Security: `Security/SecurityHeadersMiddleware` (CSP with per-request nonce, Permissions-Policy, etc.), config under `Security:*` in appsettings.

## M8a: BFF and session design

**Goal:** the browser never holds the access token or the refresh token. XSS, a browser extension, a leaked HAR file or a stolen cookie jar therefore cannot yield a bearer credential.

**Sign-in** (`Security/AuthEndpoints.cs`, `Pages/Login.razor`)
- `/login` is a *static* server-rendered page: a plain HTML form (`method=post action=/auth/login`) with an antiforgery token. A SignalR circuit cannot set cookies; only a normal HTTP response can. `POST /auth/login` validates the antiforgery token, calls `POST /api/v1/auth/login`, then `GET /auth/me`, creates the server-side session and sets the cookie.
- Failures redirect back with a code (`invalid`, `throttled`, `blocked`, `unavailable`, `expired`) and a sanitised correlation reference. 400/401/404 all read "The email or password is not correct" (no account enumeration). The end user's address is sent as `X-Forwarded-For` so the API's lockout and rate limits see the person.
- `returnUrl` must be a local path (`ReturnUrl.IsSafe`: no scheme or host, no `//`, backslash, control characters or encoded tricks) **and** inside the user's own portal (`ResolveForPortal`); otherwise the portal home is used.
- `MustChangePassword` is a flag on the session: `ForcePasswordChangeMiddleware` redirects page requests to `/change-password`, and the portal/permission policies fail until the API has confirmed the change (the page then does a full load so the principal is rebuilt).

**Session storage** (`Security/SessionStore.cs`) - a server-side store keyed by an opaque 256-bit id, *not* tokens inside the cookie. Why: tokens never leave the server, a session can be killed instantly (sign-out, reuse detection, password change), and idle plus absolute expiry are enforced in one place.
- Cookie `nv.session`: `HttpOnly`, `Secure`, `SameSite=Strict`, no `Expires`. The ticket holds only the session id; every request `SessionCookieEvents.ValidateAsync` loads the session and rebuilds the principal (roles, permissions, portal, flags) or rejects the cookie.
- `ISessionStore` runs over `IDistributedCache` (in-memory by default; plug Redis/SQL in for several nodes), payloads encrypted with ASP.NET Data Protection. Sliding idle window (`Session:IdleTimeoutMinutes`, advanced by API calls and page requests) and an absolute lifetime (`Session:AbsoluteTimeoutHours`) that no update can extend. Writes are serialised per session so a "touch" can never overwrite a freshly rotated refresh token.
- Open circuits: `SessionAuthenticationStateProvider` (a `RevalidatingServerAuthenticationStateProvider`) re-checks the session every 30 s, so idle timeout, sign-out elsewhere or a rejected refresh take effect without a page load (`AuthorizeRouteView` then sends the user to `/login`).

**Calling the API** (`Services/Api/*`, `Security/TokenRefresh.cs`)
- One door: `IApiGateway` turns every outcome into `ApiResult<T>`; the typed clients (`IAuthApiClient`, `IDashboardApiClient`, `IClientsApiClient`, `ILicensingApiClient`, `IAccessApiClient`) sit on it. The session id travels as a request option to `SessionBearerHandler`, a `DelegatingHandler` on the `NexaApi` HttpClient that reads the token from the store (handler scopes are not the circuit's scope). Login, refresh and password reset use the credential-free `NexaApiAnonymous` client.
- Problem mapping: RFC 7807 to `ApiError(code, friendly message, correlationId, status, fieldErrors)`. 5xx always shows a generic message; short 4xx business messages from the API are passed on; network failures and timeouts become `API_UNAVAILABLE`; a caller's own cancellation still cancels. Pages show the correlation id and a retry button.
- **Refresh:** proactive (token within `RefreshSkewSeconds` of expiry) and reactive (401). `TokenRefreshCoordinator` is *single flight per session*: concurrent calls queue behind one lock and late arrivals just pick up the new token, because the API rotates refresh tokens and treats a replay as theft. The exchange and the store update ignore the caller's cancellation (a rotation must never be lost). A 4xx answer from `/auth/refresh` (expired, revoked, **reuse detected**) deletes the session, so the user is signed out; 429/5xx/network errors keep it. A request is retried at most once, with its body replayed.
- **Sign-out** (`GET /auth/signed-out`): refreshes first when the access token has expired (logout needs a valid one), calls `POST /auth/logout` with the refresh token (revokes the family), deletes the session and clears the cookie. It only ever acts on the caller's own cookie, and with `SameSite=Strict` a cross-site request carries no cookie, so it cannot sign anyone out.
- **CSV download** (`GET /bff/reports/usage.csv`): cookie plus `reports.read`; the portal streams `/admin/reports/usage.csv` from the API (dates validated, `no-store`, `attachment`). The browser never sees a bearer.

**Authorization in Blazor:** `AuthorizeRouteView` with the cascading auth state. Pages declare `[Authorize(Policy = "perm:<key>")]`; a folder-level `_Imports.razor` adds `PlatformPortal` (`Pages/Admin`) or `ClientPortal` (`Pages/Client`), so platform users only reach `/admin/**` and client users only `/client/**`. Permission keys come from `Contracts.Identity.Permissions` (`WebPermissions` aliases them so they cannot drift). Anonymous gets `/login?returnUrl=`, signed-in-but-not-allowed gets `AccessDenied`. Menus and buttons are trimmed with `NavigationCatalog.Trim` and `<Can>`; the API still enforces everything.

**Admin screens:** Dashboard (real `GET /admin/dashboard?days=`), Clients (list, search, status filter, paging; create wizard; detail with Overview / Users / Settings / License / Audit tabs; suspend, deactivate, reactivate with a recorded reason; password reset), Licenses (list and filters, issue; detail with renew, adjust, suspend, activate, revoke (type REVOKE), refund, ledger table and per-license integrity check), Plans, Cost rules, Platform users, Roles and permissions (matrix), Audit logs (per client), Reports (CSV through the BFF; platform ledger verification with result). Not built: Settings and Health (no API yet). Client-portal screens other than the dashboard are M8b.

Known gaps and API wishes: see "M8a" in docs/STATUS.md.

## M8b: client portal

Pages under `Pages/Client/*` (all `[Authorize(Policy = "perm:<key>")]` plus the folder-level `ClientPortal` policy; actions are hidden with `<Can>`): `/client/license[/{id}]`, `/client/enroll`, `/client/verify`, `/client/identify`, `/client/profiles[/{id}]`, `/client/history`, `/client/api-keys`, `/client/api-logs`, `/client/webhooks[/{id}]`, `/client/notifications`, `/client/users`, `/client/settings`, `/client/activity`, `/client/api-docs` (open to every client user).

- **Typed clients** (`Services/Api/ClientApiClients.cs`) sit on `IApiGateway`; multipart uploads pass an `HttpContent` and an `Idempotency-Key` header through `ApiCallOptions.Headers`. The bearer handler buffers the body so a token refresh can replay it.
- **Secrets** (API key, webhook signing secret) live in a local variable only between the API response and `SecretRevealDialog`; they are never put in component state, URLs or snackbars.
- **Downloads:** `GET /bff/client/reports/usage.csv` (cookie + `usage.read` + client portal) relays the API's CSV; the admin route stays platform-only.
- **Notifications:** `NotificationBell` polls every 60 s with a `PeriodicTimer` (cancelled on dispose); a failed poll keeps the last count. `NotificationState` shares the unread count with the notifications page.
- **Photos:** `FacePhotoInput` wraps `FaceCapture` (camera or upload, size and magic-byte checks); pages run `PhotoGuard.Check` again before sending and `PhotoGuard.Forget` afterwards.
