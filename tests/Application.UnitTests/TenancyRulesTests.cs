using System.Text.Json;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Identity;
using NexaVerify.Application.Tenancy;
using NexaVerify.Contracts.Tenancy;

namespace NexaVerify.Application.UnitTests;

public class TenancyRulesTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Every_definition_has_a_valid_default_within_its_own_bounds()
    {
        foreach (var d in SettingCatalog.All)
        {
            var (json, error) = SettingCatalog.Normalize(d, SettingCatalog.ToJson(d.Default));
            error.ShouldBeNull($"{d.Key}: {error}");
            SettingCatalog.IsDefault(d, json!).ShouldBeTrue(d.Key);
        }

        SettingCatalog.All.Select(d => d.Key).Distinct().Count().ShouldBe(SettingCatalog.All.Count);
        SettingCatalog.All.Where(d => d.ManagedBy == SettingManager.Client).ShouldAllBe(d => d.ClientEditPermission != null);
    }

    [Theory]
    [InlineData(SettingKeys.Face.MatchThreshold, 0.29, false)]
    [InlineData(SettingKeys.Face.MatchThreshold, 0.30, true)]
    [InlineData(SettingKeys.Face.MatchThreshold, 0.99, true)]
    [InlineData(SettingKeys.Face.MatchThreshold, 1.0, false)]
    [InlineData(SettingKeys.Face.RetentionDays, 0, false)]
    [InlineData(SettingKeys.Face.RetentionDays, 3650, true)]
    [InlineData(SettingKeys.Face.RetentionDays, 3651, false)]
    [InlineData(SettingKeys.Face.RetentionDays, 1.5, false)]
    public void Numeric_bounds_and_whole_numbers_are_enforced(string key, double value, bool valid)
    {
        var (_, error) = SettingCatalog.Normalize(SettingCatalog.Find(key)!, Json(value));

        (error is null).ShouldBe(valid);
    }

    [Fact]
    public void Wrong_types_are_rejected()
    {
        SettingCatalog.Normalize(SettingCatalog.Find(SettingKeys.Face.RetainImages)!, Json("yes")).Error.ShouldNotBeNull();
        SettingCatalog.Normalize(SettingCatalog.Find(SettingKeys.Face.MatchThreshold)!, Json("0.5")).Error.ShouldNotBeNull();
        SettingCatalog.Normalize(SettingCatalog.Find(SettingKeys.Integration.AllowedIps)!, Json("1.2.3.4")).Error.ShouldNotBeNull();
        SettingCatalog.Normalize(SettingCatalog.Find(SettingKeys.Integration.AllowedIps)!, Json(new object?[] { null })).Error.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("203.0.113.7", true)]
    [InlineData("203.0.113.0/24", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("2001:db8::/129", false)]
    [InlineData("203.0.113.0/33", false)]
    [InlineData("999.1.1.1", false)]
    [InlineData("1.2.3.4/5/6", false)]
    [InlineData("localhost", false)]
    public void Allowed_ips_accept_addresses_and_cidr_ranges_only(string value, bool valid)
    {
        var (_, error) = SettingCatalog.Normalize(SettingCatalog.Find(SettingKeys.Integration.AllowedIps)!, Json(new[] { value }));

        (error is null).ShouldBe(valid);
    }

    [Theory]
    [InlineData("https://app.example.com", true)]
    [InlineData("https://app.example.com:8443", true)]
    [InlineData("http://app.example.com", false)]
    [InlineData("https://app.example.com/", false)]
    [InlineData("https://app.example.com/path", false)]
    [InlineData("*", false)]
    [InlineData("null", false)]
    public void Allowed_origins_are_explicit_https_origins(string value, bool valid)
    {
        var (_, error) = SettingCatalog.Normalize(SettingCatalog.Find(SettingKeys.Integration.AllowedOrigins)!, Json(new[] { value }));

        (error is null).ShouldBe(valid);
    }

    [Fact]
    public void Lists_are_deduplicated_and_capped()
    {
        var def = SettingCatalog.Find(SettingKeys.Integration.AllowedIps)!;

        var (json, _) = SettingCatalog.Normalize(def, Json(new[] { "10.0.0.1", "10.0.0.1" }));
        JsonSerializer.Deserialize<string[]>(json!)!.Length.ShouldBe(1);
        SettingCatalog.Normalize(def, Json(Enumerable.Range(1, 51).Select(i => $"10.0.0.{i}").ToArray())).Error.ShouldNotBeNull();
    }

    [Fact]
    public void IsDefault_ignores_numeric_formatting_and_list_order()
    {
        SettingCatalog.IsDefault(SettingCatalog.Find(SettingKeys.Face.MatchThreshold)!, "0.6").ShouldBeTrue();
        SettingCatalog.IsDefault(SettingCatalog.Find(SettingKeys.Face.MatchThreshold)!, "0.61").ShouldBeFalse();
        SettingCatalog.IsDefault(SettingCatalog.Find(SettingKeys.Integration.AllowedIps)!, "[]").ShouldBeTrue();
    }

    private static PasswordPolicy Policy() => new(Options.Create(new PasswordPolicyOptions()));

    [Theory]
    [InlineData("short", false)]
    [InlineData("password1234", false)]
    [InlineData("aaaaaaaaaaaaaaaa", false)]
    [InlineData("abababababababab", false)]
    [InlineData("Correct-Horse-Battery-9", true)]
    [InlineData("a long passphrase with spaces ok", true)]
    public void Password_policy_is_length_first_with_a_deny_list(string password, bool valid)
    {
        (Policy().Validate(password).Count == 0).ShouldBe(valid);
    }

    [Fact]
    public void Passwords_derived_from_the_email_name_are_rejected_but_short_names_are_ignored()
    {
        Policy().Validate("Alexander-Hamilton-77", "alexander@x.test").ShouldNotBeEmpty();
        Policy().Validate("Alexander-Hamilton-77", "al@x.test").ShouldBeEmpty();
        Policy().Validate(null).ShouldNotBeEmpty();
        Policy().Validate(new string('x', 200) + "Ab1").ShouldNotBeEmpty();
    }

    [Fact]
    public void Auth_options_validation_rejects_unsafe_reset_links()
    {
        var validator = new AuthOptionsValidator();

        validator.Validate(null, new AuthOptions { PasswordResetUrlTemplate = "https://portal.example.com/reset?email={email}&token={token}" }).Succeeded.ShouldBeTrue();
        validator.Validate(null, new AuthOptions { PasswordResetUrlTemplate = "http://localhost:5000/reset?email={email}&token={token}" }).Succeeded.ShouldBeTrue();
        validator.Validate(null, new AuthOptions { PasswordResetUrlTemplate = "http://portal.example.com/reset?email={email}&token={token}" }).Succeeded.ShouldBeFalse();
        validator.Validate(null, new AuthOptions { PasswordResetUrlTemplate = "https://portal.example.com/reset" }).Succeeded.ShouldBeFalse();
        validator.Validate(null, new AuthOptions { RefreshTokenSlidingDays = 30, RefreshTokenAbsoluteDays = 7 }).Succeeded.ShouldBeFalse();
    }
}
