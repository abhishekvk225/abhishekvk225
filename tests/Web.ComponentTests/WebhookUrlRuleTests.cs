using NexaVerify.Web.Services;

namespace NexaVerify.Web.ComponentTests;

public class WebhookUrlRuleTests
{
    [Theory]
    [InlineData("https://hooks.acme.test/nv", true)]
    [InlineData("  https://hooks.acme.test/nv  ", true)]
    [InlineData("", true)] // emptiness is the Required rule's job
    [InlineData("http://hooks.acme.test/nv", false)]
    [InlineData("https://user:pass@hooks.acme.test/nv", false)]
    [InlineData("https://user@hooks.acme.test/nv", false)]
    [InlineData("https://:secret@hooks.acme.test/nv", false)]
    [InlineData("ftp://hooks.acme.test/nv", false)]
    [InlineData("not a url", false)]
    public void Only_plain_https_addresses_without_credentials_pass(string url, bool expected) =>
        new HttpsRequiredAttribute().IsValid(url).ShouldBe(expected);
}
