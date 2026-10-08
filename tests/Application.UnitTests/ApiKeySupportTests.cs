using NexaVerify.Application.Api;

namespace NexaVerify.Application.UnitTests;

public class ApiKeySupportTests
{
    [Fact]
    public void Generated_keys_have_the_documented_shape_and_hash()
    {
        var (raw, prefix, hash) = ApiKeyMaterial.Generate();

        raw.Length.ShouldBe(ApiKeyMaterial.KeyLength);
        raw.ShouldStartWith(prefix + "_");
        prefix.Length.ShouldBe(ApiKeyMaterial.PrefixLength);
        ApiKeyMaterial.ParsePrefix(raw).ShouldBe(prefix);
        ApiKeyMaterial.Hash(raw).ShouldBe(hash);
        ApiKeyMaterial.Generate().Raw.ShouldNotBe(raw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nxv_live_abcdefgh")]
    [InlineData("nxv_test_abcdefgh_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("nxv_live_abcdefghXAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_keys_have_no_prefix(string? raw) => ApiKeyMaterial.ParsePrefix(raw).ShouldBeNull();

    [Theory]
    [InlineData("203.0.113.7", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("::1", true)]
    [InlineData("198.51.100.1", false)]
    [InlineData("10.2.0.1", false)]
    [InlineData("::2", false)]
    [InlineData("not-an-ip", false)]
    [InlineData(null, false)]
    public void An_allow_list_matches_addresses_and_ranges(string? ip, bool allowed) =>
        IpRules.Allows(["203.0.113.7", "10.1.0.0/16", "::1"], ip).ShouldBe(allowed);

    [Fact]
    public void An_empty_allow_list_means_no_restriction() => IpRules.Allows([], "198.51.100.1").ShouldBeTrue();

    [Theory]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("::/129", false)]
    [InlineData("1.2.3.4/abc", false)]
    [InlineData("1.2.3.4/5/6", false)]
    [InlineData("hello", false)]
    [InlineData("192.168.1.1", true)]
    public void Rules_are_validated(string rule, bool valid) => IpRules.IsValid(rule).ShouldBe(valid);

    [Fact]
    public void Only_face_scopes_are_assignable()
    {
        ApiKeyScopes.Assignable.ShouldAllBe(s => s.StartsWith("faces.", StringComparison.Ordinal));
        ApiKeyScopes.Assignable.ShouldNotContain("apikeys.manage");
    }
}
