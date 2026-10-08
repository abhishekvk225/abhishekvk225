using System.Net;

namespace NexaVerify.Web.ComponentTests;

public class GatewayHeaderTests
{
    private sealed record Payload(int Value);

    [Fact]
    public async Task The_idempotency_key_is_the_only_header_a_page_may_add()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"value\":1}");

        var result = await h.Gateway.SendAsync<Payload>(HttpMethod.Post, "faces/enroll", null, default,
            new ApiCallOptions { Headers = new Dictionary<string, string> { ["idempotency-key"] = "abc" } });

        result.IsSuccess.ShouldBeTrue();
        ApiCallOptions.AllowedHeaderNames.ShouldBe(["Idempotency-Key"]);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Api-Key")]
    [InlineData("Host")]
    [InlineData("Transfer-Encoding")]
    public async Task Any_other_header_is_refused_so_it_cannot_smuggle_credentials_or_forwarding_data(string name)
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"value\":1}");

        await Should.ThrowAsync<ArgumentException>(() => h.Gateway.SendAsync<Payload>(HttpMethod.Post, "x", null, default,
            new ApiCallOptions { Headers = new Dictionary<string, string> { [name] = "evil" } }));

        h.Api.Seen.ShouldBeEmpty("nothing was sent");
    }
}
