using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

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

    private static Dictionary<string, string?> Production(string? apiBase = "https://api.nexaverify.example", bool stubs = false) => new()
    {
        ["AllowedHosts"] = "portal.nexaverify.example",
        ["Api:BaseUrl"] = apiBase,
        ["Ui:UseStubClients"] = stubs ? "true" : "false",
    };

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
        using var factory = Factory("Production", Production(stubs: true));

        Should.Throw<InvalidOperationException>(() => factory.CreateClient()).Message.ShouldContain("only allowed in Development or UiDemo");
    }

    [Fact]
    public void Production_needs_an_https_api_address()
    {
        Should.Throw<InvalidOperationException>(() => Factory("Production", Production(apiBase: "http://api.internal")).CreateClient())
            .Message.ShouldContain("https");
        Should.Throw<InvalidOperationException>(() => Factory("Production", Production(apiBase: null)).CreateClient())
            .Message.ShouldContain("Api:BaseUrl");
    }

    [Fact]
    public void Development_may_use_plain_http_to_a_local_api()
    {
        using var factory = Factory("Development", new() { ["Api:BaseUrl"] = "http://localhost:5101" });

        using var client = factory.CreateClient();

        client.ShouldNotBeNull();
    }

    [Fact]
    public void The_stub_clients_work_in_the_design_review_environment()
    {
        using var factory = Factory("UiDemo", new() { ["Ui:UseStubClients"] = "true", ["Api:BaseUrl"] = "http://localhost:5101" });

        using var client = factory.CreateClient();

        client.ShouldNotBeNull();
    }
}
