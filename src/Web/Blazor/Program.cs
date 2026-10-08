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
    options.Cookie.Name = cookieOptions.RequireSecure ? "__Host-nv.af" : "nv.af"; // __Host-: refused from sibling subdomains
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

// BFF: server-side sessions, the opaque cookie, typed API clients. Stub clients are opt-in (Ui:UseStubClients) and refused outside
// Development/UiDemo; every other environment must have a valid https Api:BaseUrl or the app does not start.
builder.Services.AddPortalBff(builder.Configuration, builder.Environment);

// Behind a TLS-terminating proxy the real client address and scheme arrive in X-Forwarded-* headers. Only proxies you list are trusted.
var forwardedEnabled = ForwardedHeadersSetup.Configure(builder.Services, builder.Configuration);

var app = builder.Build();

if (forwardedEnabled)
{
    app.UseForwardedHeaders();
}

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
app.UseAuthentication();
app.UseMiddleware<ForcePasswordChangeMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/", (HttpContext http) =>
    Results.Redirect(http.User.FindFirst(PortalClaims.Portal)?.Value is { } portal ? ReturnUrl.HomeFor(portal) : "/login"));
app.MapPortalAuth();
app.MapPortalDownloads();
app.MapGet("/error", () => Results.Problem(title: "Something went wrong", statusCode: 500));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory.</summary>
public partial class Program;
