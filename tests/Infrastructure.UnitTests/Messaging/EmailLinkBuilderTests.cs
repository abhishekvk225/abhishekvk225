using NexaVerify.Application.Messaging;
using NexaVerify.Infrastructure.Messaging;

namespace NexaVerify.Infrastructure.UnitTests.Messaging;

public class EmailLinkBuilderTests
{
    private static EmailLinkBuilder Builder(string baseUrl = "https://portal.example.com") => new(baseUrl);

    private static EmailLink Link(string path, params (string Key, string Value)[] query) =>
        new(path, [.. query.Select(q => new KeyValuePair<string, string>(q.Key, q.Value))]);

    [Fact]
    public void A_link_is_built_under_the_base_with_encoded_query_values()
    {
        Builder().Build(Link("reset-password", ("email", "a+b@example.com"), ("token", "x y&z=1")))
            .ShouldBe("https://portal.example.com/reset-password?email=a%2Bb%40example.com&token=x%20y%26z%3D1");
    }

    [Fact]
    public void A_base_path_prefix_is_kept()
    {
        Builder("https://example.com/app/").Build(Link("login")).ShouldBe("https://example.com/app/login");
        Builder("https://example.com/app").Build(Link("client/billing/orders/42")).ShouldBe("https://example.com/app/client/billing/orders/42");
    }

    [Fact]
    public void A_leading_slash_is_tolerated_and_segments_are_encoded()
    {
        Builder().Build(Link("/client/webhooks/0a1b")).ShouldBe("https://portal.example.com/client/webhooks/0a1b");
    }

    [Theory]
    [InlineData("//evil.example.com/x")]
    [InlineData("https://evil.example.com/x")]
    [InlineData("http://evil.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("..")]
    [InlineData("a/../../b")]
    [InlineData("a/./b")]
    [InlineData("a\\b")]
    [InlineData("a?x=1")]
    [InlineData("a#frag")]
    [InlineData("a%2e%2e/b")]
    [InlineData("a b")]
    [InlineData("a\r\nb")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_path_that_could_leave_the_base_is_refused(string path) =>
        Should.Throw<EmailLinkException>(() => Builder().Build(Link(path)));

    [Theory]
    [InlineData("a b")]
    [InlineData("a=b")]
    [InlineData("a&b")]
    [InlineData("")]
    [InlineData("tok\r\nen")]
    public void A_query_key_with_odd_characters_is_refused(string key) =>
        Should.Throw<EmailLinkException>(() => Builder().Build(Link("login", (key, "v"))));

    [Fact]
    public void A_query_value_with_markup_or_line_breaks_is_percent_encoded()
    {
        var url = Builder().Build(Link("login", ("next", "\"><script>\r\nBcc: x")));

        url.ShouldNotContain("<");
        url.ShouldNotContain("\"");
        url.ShouldNotContain("\r");
        url.ShouldNotContain("\n");
        url.ShouldNotContain(" ");
    }

    [Theory]
    [InlineData("https://portal.example.com", true)]
    [InlineData("https://portal.example.com/app", true)]
    [InlineData("http://localhost:7200", true)]
    [InlineData("http://portal.example.com", false)]
    [InlineData("ftp://portal.example.com", false)]
    [InlineData("https://portal.example.com?x=1", false)]
    [InlineData("https://portal.example.com/#f", false)]
    [InlineData("https://user:pw@portal.example.com", false)]
    [InlineData("portal.example.com", false)]
    [InlineData("", false)]
    public void The_base_must_be_an_absolute_https_url_without_query_or_credentials(string value, bool valid) =>
        EmailLinkBuilder.IsValidBase(value).ShouldBe(valid);

    [Fact]
    public void An_invalid_base_is_rejected_at_construction() =>
        Should.Throw<EmailLinkException>(() => new EmailLinkBuilder("http://portal.example.com"));

    [Fact]
    public void The_email_base_url_wins_over_the_portal_one_and_the_portal_one_is_the_fallback()
    {
        var portal = Microsoft.Extensions.Options.Options.Create(new NexaVerify.Application.Public.PortalLinksOptions { PublicBaseUrl = "https://portal.example.com" });

        new EmailLinkBuilder(Microsoft.Extensions.Options.Options.Create(new EmailOptions { PublicBaseUrl = "https://mail-links.example.com" }), portal)
            .Build(Link("login")).ShouldBe("https://mail-links.example.com/login");
        new EmailLinkBuilder(Microsoft.Extensions.Options.Options.Create(new EmailOptions()), portal)
            .Build(Link("login")).ShouldBe("https://portal.example.com/login");
    }
}
