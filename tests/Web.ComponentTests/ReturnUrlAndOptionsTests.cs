using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

public class ReturnUrlAndOptionsTests
{
    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/clients?page=2&search=acme")]
    [InlineData("/client#top")]
    public void Local_paths_are_accepted(string url) => ReturnUrl.IsSafe(url).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("https://evil.example/")]
    [InlineData("http://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("/%2F%2Fevil.example")]
    [InlineData("/%5Cevil.example")]
    [InlineData("/admin\r\nSet-Cookie: x=1")]
    [InlineData("/admin%0d%0aSet-Cookie:x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/redirect?to=https://evil.example")]
    [InlineData("/login")]
    [InlineData("/login?returnUrl=/admin")]
    [InlineData("/auth/signed-out")]
    public void Anything_else_is_refused(string? url) => ReturnUrl.IsSafe(url).ShouldBeFalse();

    [Fact]
    public void Overlong_urls_are_refused() => ReturnUrl.IsSafe("/" + new string('a', ReturnUrl.MaxLength)).ShouldBeFalse();

    [Theory]
    [InlineData("/admin/clients", PortalKinds.Admin, "/admin/clients")]
    [InlineData("/client/users", PortalKinds.Admin, "/admin")]
    [InlineData("/admin", PortalKinds.Client, "/client")]
    [InlineData("/adminx", PortalKinds.Admin, "/admin")]
    [InlineData("//evil.example", PortalKinds.Client, "/client")]
    [InlineData(null, PortalKinds.Client, "/client")]
    [InlineData("/client/license?x=1", PortalKinds.Client, "/client/license?x=1")]
    public void Return_url_never_leaves_the_users_own_portal(string? url, string portal, string expected) =>
        ReturnUrl.ResolveForPortal(url, portal).ShouldBe(expected);

    [Theory]
    [InlineData("https://api.nexaverify.example", false, "https://api.nexaverify.example/")]
    [InlineData("https://api.nexaverify.example/api/v1", false, "https://api.nexaverify.example/api/v1/")]
    [InlineData("http://localhost:5101", true, "http://localhost:5101/")]
    public void Api_base_url_is_normalised(string input, bool allowInsecure, string expected) =>
        ApiClientOptions.ResolveBaseAddress(input, allowInsecure).ToString().ShouldBe(expected);

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not a url", true)]
    [InlineData("/relative", true)]
    [InlineData("http://api.example", false)]
    [InlineData("ftp://api.example", true)]
    [InlineData("https://user:pass@api.example", false)]
    public void Api_base_url_must_be_an_absolute_https_address_outside_development(string? input, bool allowInsecure) =>
        Should.Throw<InvalidOperationException>(() => ApiClientOptions.ResolveBaseAddress(input, allowInsecure));

    [Theory]
    [InlineData("abc-123_DEF.9:x", true)]
    [InlineData("", false)]
    [InlineData("<script>alert(1)</script>", false)]
    [InlineData("a b", false)]
    public void Correlation_references_are_shown_only_when_harmless(string value, bool shown) =>
        (AuthEndpoints.SanitiseReference(value) is not null).ShouldBe(shown);

    [Theory]
    [InlineData(401, LoginErrors.Invalid)]
    [InlineData(400, LoginErrors.Invalid)]
    [InlineData(404, LoginErrors.Invalid)]
    [InlineData(429, LoginErrors.Throttled)]
    [InlineData(403, LoginErrors.Blocked)]
    [InlineData(503, LoginErrors.Unavailable)]
    [InlineData(500, LoginErrors.Unavailable)]
    public void Sign_in_failures_map_to_non_revealing_codes(int status, string expected) =>
        LoginErrors.FromStatus(status).ShouldBe(expected);

    [Fact]
    public void Login_messages_never_say_which_part_was_wrong()
    {
        var message = LoginErrors.MessageFor(LoginErrors.Invalid);

        message.ToLowerInvariant().ShouldNotContain("no account");
        message.ToLowerInvariant().ShouldNotContain("not found");
        message.ToLowerInvariant().ShouldNotContain("locked");
        LoginErrors.MessageFor("<b>injected</b>").ShouldBe(string.Empty);
    }
}
