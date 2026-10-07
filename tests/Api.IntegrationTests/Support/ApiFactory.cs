using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests.Support;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string ConnectionString { get; init; } = SqlServerFixture.UnreachableConnectionString;

    public string Environment { get; init; } = "Development";

    /// <summary>When true the header-driven test scheme is the default; otherwise the product's deny-by-default scheme stays.</summary>
    public bool UseTestAuth { get; init; } = true;

    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
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
        });
    }
}
