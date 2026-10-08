using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

/// <summary>The portal must refuse to start in unsafe configurations rather than limp along.</summary>
public class StartupGuardTests
{
    private static WebApplicationFactory<Program> Factory(string environment, Dictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static Dictionary<string, string?> Production(string? apiBase = "https://api.nexaverify.example") => new()
    {
        ["AllowedHosts"] = "portal.nexaverify.example",
        ["Api:BaseUrl"] = apiBase,
        ["DataProtection:KeyPath"] = Path.Combine(Path.GetTempPath(), "nv-test-keys"),
        ["Session:AllowInMemoryStore"] = "true",
    };

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static void Validate(string environment, Dictionary<string, string?> values) =>
        PortalStartupGuards.Validate(Config(values), new FakeEnv(environment));

    private static Dictionary<string, string?> With(Action<Dictionary<string, string?>> change)
    {
        var values = Production();
        change(values);
        return values;
    }

    [Fact]
    public void A_correctly_configured_production_portal_starts()
    {
        using var factory = Factory("Production", Production());

        using var client = factory.CreateClient();

        client.ShouldNotBeNull();
    }

    [Fact]
    public void Production_refuses_the_any_password_stub_clients()
    {
        using var factory = Factory("Production", With(v => v["Ui:UseStubClients"] = "true"));

        Should.Throw<InvalidOperationException>(() => factory.CreateClient()).Message.ShouldContain("accepts any password");
    }

    [Fact]
    public void Production_needs_an_https_api_address()
    {
        Should.Throw<InvalidOperationException>(() => Factory("Production", Production(apiBase: "http://api.internal")).CreateClient())
            .Message.ShouldContain("https");
        Should.Throw<InvalidOperationException>(() => Factory("Production", Production(apiBase: null)).CreateClient())
            .Message.ShouldContain("Api:BaseUrl");
    }

    private static T Resolve<T>(Dictionary<string, string?> values)
        where T : class
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPortalBff(Config(values), new FakeEnv("Production"));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<T>>().Value; // validation runs on first use (and at host start via ValidateOnStart)
    }

    [Fact]
    public void Bad_numbers_stop_the_start_instead_of_being_silently_corrected()
    {
        Resolve<ApiClientOptions>(Production()).TimeoutSeconds.ShouldBe(30);
        Should.Throw<OptionsValidationException>(() => Resolve<ApiClientOptions>(With(v => v["Api:TimeoutSeconds"] = "0")));
        Should.Throw<OptionsValidationException>(() => Resolve<ApiClientOptions>(With(v => v["Api:LongRunningTimeoutSeconds"] = "99999")));
        Should.Throw<OptionsValidationException>(() => Resolve<SessionOptions>(With(v => v["Session:IdleTimeoutMinutes"] = "9999")));
        Should.Throw<OptionsValidationException>(() => Resolve<SessionOptions>(With(v =>
        {
            v["Session:IdleTimeoutMinutes"] = "600";
            v["Session:AbsoluteTimeoutHours"] = "1";
        }))).Message.ShouldContain("must not exceed");
    }

    [Fact]
    public void Development_may_use_plain_http_to_a_local_api()
    {
        using var factory = Factory("Development", new() { ["Api:BaseUrl"] = "http://localhost:5101" });

        using var client = factory.CreateClient();

        client.ShouldNotBeNull();
    }

    [Fact]
    public void The_stub_clients_work_in_Development()
    {
        using var factory = Factory("Development", new() { ["Ui:UseStubClients"] = "true" });

        using var client = factory.CreateClient();

        client.ShouldNotBeNull();
    }

    // ---- environment names must not switch protections off ----

    [Fact]
    public void The_design_review_environment_gets_no_special_treatment_without_the_explicit_stub_flag()
    {
        Should.Throw<InvalidOperationException>(() => Validate("UiDemo", With(v => v["Ui:UseStubClients"] = "true"))).Message.ShouldContain("Ui:AllowDemoStubs");
        Should.NotThrow(() => Validate("UiDemo", With(v =>
        {
            v["Ui:UseStubClients"] = "true";
            v["Ui:AllowDemoStubs"] = "true";
        })));
        // ... and it still needs the deployment protections
        Should.Throw<InvalidOperationException>(() => Validate("UiDemo", With(v => v["AllowedHosts"] = "*"))).Message.ShouldContain("AllowedHosts");
        Should.Throw<InvalidOperationException>(() => Validate("UiDemo", With(v => v.Remove("DataProtection:KeyPath")))).Message.ShouldContain("DataProtection:KeyPath");
    }

    [Theory]
    [InlineData("Security:Cookies:RequireSecure", "false", "RequireSecure")]
    [InlineData("Security:Cookies:SameSite", "None", "SameSite")]
    [InlineData("Security:Cookies:SameSite", "Lax", "SameSite")]
    [InlineData("Security:Headers:Enabled", "false", "Security:Headers")]
    [InlineData("Security:Headers:ContentSecurityPolicyEnabled", "false", "Security:Headers")]
    [InlineData("Security:Headers:AllowInsecureWebSockets", "true", "Security:Headers")]
    [InlineData("AllowedHosts", "*", "AllowedHosts")]
    [InlineData("AllowedHosts", "", "AllowedHosts")]
    public void Every_environment_but_development_refuses_weakened_security_settings(string key, string value, string expected)
    {
        foreach (var environment in new[] { "Production", "Staging", "UiDemo", "Testing", "Whatever" })
        {
            Should.Throw<InvalidOperationException>(() => Validate(environment, With(v => v[key] = value))).Message.ShouldContain(expected);
        }

        Should.NotThrow(() => Validate("Development", With(v => v[key] = value)));
    }

    [Fact]
    public void Deployment_environments_must_persist_the_key_ring_and_opt_in_to_the_in_memory_store()
    {
        Should.Throw<InvalidOperationException>(() => Validate("Production", With(v => v.Remove("DataProtection:KeyPath")))).Message.ShouldContain("DataProtection:KeyPath");
        Should.Throw<InvalidOperationException>(() => Validate("Staging", With(v => v.Remove("Session:AllowInMemoryStore")))).Message.ShouldContain("Session:AllowInMemoryStore");
        Should.NotThrow(() => Validate("Production", Production()));
        // test hosts only need the basics
        Should.NotThrow(() => Validate("Testing", With(v =>
        {
            v.Remove("DataProtection:KeyPath");
            v.Remove("Session:AllowInMemoryStore");
        })));
    }

    [Fact]
    public void A_host_prefixed_cookie_name_needs_secure_cookies()
    {
        Should.Throw<InvalidOperationException>(() => Validate("Development", new()
        {
            ["Session:CookieName"] = "__Host-nv.session",
            ["Security:Cookies:RequireSecure"] = "false",
        })).Message.ShouldContain("__Host-");
    }

    [Fact]
    public void Cookie_names_get_the_host_prefix_when_secure()
    {
        new SessionOptions().EffectiveCookieName(secure: true).ShouldBe("__Host-nv.session");
        new SessionOptions().EffectiveCookieName(secure: false).ShouldBe("nv.session");
        new SessionOptions { CookieName = "custom" }.EffectiveCookieName(secure: true).ShouldBe("custom");
    }

    // ---- redirects ----

    [Fact]
    public void The_api_clients_never_follow_redirects_and_have_no_client_wide_timeout()
    {
        using var factory = Factory("Development", new() { ["Api:BaseUrl"] = "http://localhost:5101" });
        var options = factory.Services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();
        var http = factory.Services.GetRequiredService<IHttpClientFactory>();

        foreach (var name in new[] { "NexaApi", "NexaApiAnonymous" })
        {
            var builder = new RecordingBuilder(factory.Services);
            foreach (var action in options.Get(name).HttpMessageHandlerBuilderActions)
            {
                action(builder);
            }

            builder.PrimaryHandler.ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeFalse(name);
            http.CreateClient(name).Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class RecordingBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }

        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];

        public override IServiceProvider Services { get; } = services;

        public override HttpMessageHandler Build() => PrimaryHandler;
    }
}
