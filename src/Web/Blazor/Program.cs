using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.Http;
using MudBlazor.Services;
using NexaVerify.Web;
using NexaVerify.Web.Security;
using NexaVerify.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

builder.Services.Configure<SecurityHeadersOptions>(builder.Configuration.GetSection(SecurityHeadersOptions.Section));
builder.Services.Configure<CookieSecurityOptions>(builder.Configuration.GetSection(CookieSecurityOptions.Section));
var cookieOptions = builder.Configuration.GetSection(CookieSecurityOptions.Section).Get<CookieSecurityOptions>() ?? new CookieSecurityOptions();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.PreventDuplicates = false;
    config.SnackbarConfiguration.VisibleStateDuration = 5000;
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "nv.af";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = cookieOptions.SameSiteMode;
    options.Cookie.SecurePolicy = cookieOptions.RequireSecure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<CurrentUserState>();
builder.Services.AddScoped<DashboardRangeState>();
builder.Services.AddScoped<IClipboardService, ClipboardService>();
builder.Services.AddScoped<IAppSnackbar, AppSnackbar>();

// UI-1 stubs: they accept any password, so they exist ONLY for local development / the UI demo environment.
// Anywhere else the app refuses to start until the BFF-backed API clients replace them.
if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("UiDemo"))
{
    builder.Services.AddScoped<IAuthApiClient, StubAuthApiClient>();
    builder.Services.AddScoped<IDashboardApiClient, StubDashboardApiClient>();
}
else
{
    throw new InvalidOperationException(
        "The portal's API clients are not wired yet. Run in Development/UiDemo, or provide the real IAuthApiClient/IDashboardApiClient implementations.");
}

if (builder.Environment.IsProduction() && builder.Configuration["AllowedHosts"] is null or "" or "*")
{
    throw new InvalidOperationException("AllowedHosts must list the real host names in Production ('*' disables host-header validation).");
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCookiePolicy(new CookiePolicyOptions
{
    HttpOnly = HttpOnlyPolicy.Always,
    Secure = cookieOptions.RequireSecure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest,
    MinimumSameSitePolicy = cookieOptions.SameSiteMode,
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/", () => Results.Redirect("/login"));
app.MapGet("/error", () => Results.Problem(title: "Something went wrong", statusCode: 500));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory.</summary>
public partial class Program;
