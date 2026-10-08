using System.Net;
using NexaVerify.Application.Api;
using NexaVerify.Infrastructure.Platform;

namespace NexaVerify.Infrastructure.UnitTests.Faces;

public class WebhookUrlGuardTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.255", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("203.0.113.9", false)]
    [InlineData("198.51.100.9", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("2002:0a00:0001::1", false)]                    // 6to4 embedding 10.0.0.1
    [InlineData("2002:0808:0808::1", false)]                    // 6to4 is refused even when the embedded address is public
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", false)] // Teredo
    [InlineData("2001:1::1", false)]                            // 2001::/23 IETF protocol assignments
    [InlineData("::10.0.0.1", false)]                           // IPv4-compatible
    [InlineData("::8.8.8.8", false)]
    [InlineData("0:0:0:0:0:0:a00:1", false)]
    [InlineData("fe80::5efe:10.0.0.1", false)]                  // link-local ISATAP
    [InlineData("2a00:1450:4001:81b::200e", true)]              // an ordinary global address
    [InlineData("3fff::1", false)]                              // documentation 3fff::/20
    [InlineData("192.88.99.1", false)]                          // 6to4 relay anycast
    [InlineData("2620:0:ccc::2", true)]
    public void Only_public_unicast_addresses_pass(string address, bool expected) => WebhookUrlGuard.IsPublic(IPAddress.Parse(address)).ShouldBe(expected);

    [Theory]
    [InlineData("https://user:pass@example.com/hook")]
    [InlineData("https://user@example.com/hook")]
    [InlineData("https://:secret@example.com/hook")]
    [InlineData("https://example.com@evil.example.org/hook")]
    public async Task Urls_with_a_username_or_password_are_refused(string url)
    {
        var guard = new WebhookUrlGuard(Microsoft.Extensions.Options.Options.Create(new WebhookOptions()));

        (await guard.ValidateAsync(url, CancellationToken.None)).ShouldBe("The URL must not contain a username or password.");
    }

    [Fact]
    public async Task Urls_with_a_username_are_refused_even_when_unsafe_targets_are_allowed()
    {
        var guard = new WebhookUrlGuard(Microsoft.Extensions.Options.Options.Create(new WebhookOptions { AllowUnsafeTargets = true }));

        (await guard.ValidateAsync("http://user:pass@localhost/hook", CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public void Webhook_signatures_are_stable_and_bound_to_time_and_body()
    {
        var sig = WebhookSigning.Sign("whsec_x", 1700000000, "{\"a\":1}");

        sig.ShouldBe(WebhookSigning.Sign("whsec_x", 1700000000, "{\"a\":1}"));
        sig.ShouldStartWith("t=1700000000,v1=");
        sig.ShouldNotBe(WebhookSigning.Sign("whsec_x", 1700000001, "{\"a\":1}"));
        sig.ShouldNotBe(WebhookSigning.Sign("whsec_x", 1700000000, "{\"a\":2}"));
        sig.ShouldNotBe(WebhookSigning.Sign("whsec_y", 1700000000, "{\"a\":1}"));
    }
}
