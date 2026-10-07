# UI notes (phase UI-1)

Project: `src/Web/Blazor/NexaVerify.Web.csproj` (Blazor Web App, Interactive Server, no prerender; references `Contracts` only).
Tests: `tests/Web.ComponentTests` (bUnit + WebApplicationFactory for headers).

## Run
```
dotnet run --project src/Web/Blazor      # http://localhost:5102 (Development)
dotnet test tests/Web.ComponentTests
```
Routes: `/login` (any password; email containing "admin" -> `/admin`, `fail@...` -> error state, else `/client`), `/forgot-password`,
`/reset-password?token=x`, `/admin`, `/client`, `/styleguide` (Development only).

## What exists
- `Theme/NexaTheme.cs` single MudTheme (light + dark, AA-checked pairs); `Services/ThemeService` (localStorage + prefers-color-scheme).
- Layouts: `AuthLayout`, `AdminLayout`, `ClientLayout` (both use `Components/PortalShell`: responsive drawer, theme menu, notifications placeholder, user menu, permission-trimmed nav from `Services/NavigationCatalog`).
- Components (`Components/`): PageHeader, StatCard, ChartCard (+SimpleChart, SeriesTable), LicenseGauge (+LicenseHealth thresholds), DataTable<T>/DataColumn<T>, FilterBar, StatusChip (+StatusMap), ConfirmDialog, FormDialog<T>, SecretRevealDialog (+`DialogExtensions`), EmptyState, ErrorState, Skeleton{Card,Table,Chart}, ActivityFeed, FaceCapture (+`wwwroot/js/face-capture.js`), PageState<T>, AlertList, RangeSelector, DashboardSkeleton.
- Services: `ApiResult<T>`/`ApiError`, `IAuthApiClient`/`IDashboardApiClient` (stubs now), `IAppSnackbar`, `IClipboardService`, `CurrentUserState`, `WebPermissions`.
- Security: `Security/SecurityHeadersMiddleware` (CSP with per-request nonce, Permissions-Policy, etc.), config under `Security:*` in appsettings.

## Wiring later
Replace the stub clients in `Program.cs` with BFF typed clients returning `ApiResult<T>`; populate `CurrentUserState` from the session;
swap `WebPermissions` for `Contracts.Permissions` when it exists.
