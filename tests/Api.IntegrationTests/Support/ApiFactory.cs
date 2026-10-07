using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests.Support;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string ConnectionString { get; init; } = SqlServerFixture.UnreachableConnectionString;

    public string Environment { get; init; } = "Development";

    /// <summary>When true the header-driven test scheme is the default; otherwise the product's JWT bearer scheme stays.</summary>
    public bool UseTestAuth { get; init; } = true;

    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    public Action<IServiceCollection>? ConfigureServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Jwt:AllowEphemeralKey", "true");
        builder.UseSetting("PasswordHashing:IterationCount", "10000");
        builder.UseSetting("RateLimiting:PerIpPerMinute", "100000");
        builder.UseSetting("RateLimiting:AuthPerIpPerMinute", "100000");
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(TestController).Assembly);
            services.AddScoped<IValidator<FluentBody>, FluentBodyValidator>();
            if (UseTestAuth)
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
            }

            ConfigureServices?.Invoke(services);
        });
    }
}

/// <summary>TestServer has no remote IP; this makes requests look like they come from 127.0.0.1 (as behind a local proxy).</summary>
public sealed class FakeRemoteIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            return nextMiddleware(context);
        });
        next(app);
    };
}
