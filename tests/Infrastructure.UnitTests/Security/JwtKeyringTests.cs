using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NexaVerify.Infrastructure.Identity;

namespace NexaVerify.Infrastructure.UnitTests.Security;

public class JwtKeyringTests
{
    private static string NewPrivatePem() => ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem();

    private static string PublicOf(string privatePem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        return key.ExportSubjectPublicKeyInfoPem();
    }

    private static JwtKeyProvider Provider(JwtOptions o) => new(Options.Create(o), NullLogger<JwtKeyProvider>.Instance);

    private static string Sign(JwtKeyProvider keys) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("iss", "aud", [], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), keys.SigningCredentials));

    private static bool Validates(JwtKeyProvider keys, string token)
    {
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = "iss",
            ValidAudience = "aud",
            IssuerSigningKeys = keys.ValidationKeys,
            ValidateLifetime = true,
        };
        return new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _) is not null;
    }

    [Fact]
    public void The_new_key_signs_and_tokens_from_the_previous_key_are_still_accepted_until_it_is_removed()
    {
        var k1 = NewPrivatePem();
        var k2 = NewPrivatePem();
        var before = Provider(new JwtOptions { SigningKeyPem = k1, SigningKeyId = "k1" });
        var oldToken = Sign(before);

        var rotated = Provider(new JwtOptions { Keys = [new() { KeyId = "k1", PublicKeyPem = PublicOf(k1) }, new() { KeyId = "k2", SigningKeyPem = k2 }], CurrentKeyId = "k2" });

        rotated.CurrentKeyId.ShouldBe("k2");
        new JwtSecurityTokenHandler().ReadJwtToken(Sign(rotated)).Header.Kid.ShouldBe("k2");
        Validates(rotated, oldToken).ShouldBeTrue(); // signed by k1, accepted
        Validates(rotated, Sign(rotated)).ShouldBeTrue();

        var retired = Provider(new JwtOptions { Keys = [new() { KeyId = "k2", SigningKeyPem = k2 }], CurrentKeyId = "k2" });
        Should.Throw<SecurityTokenSignatureKeyNotFoundException>(() => Validates(retired, oldToken));
    }

    [Fact]
    public void The_legacy_settings_still_work_alone_and_alongside_the_ring()
    {
        var k1 = NewPrivatePem();
        var k2 = NewPrivatePem();

        Provider(new JwtOptions { SigningKeyPem = k1, SigningKeyId = "k1" }).CurrentKeyId.ShouldBe("k1");

        var mixed = Provider(new JwtOptions
        {
            SigningKeyPem = k2,
            SigningKeyId = "k2",
            AdditionalValidationKeys = [new() { KeyId = "k1", PublicKeyPem = PublicOf(k1) }],
        });
        mixed.CurrentKeyId.ShouldBe("k2");
        mixed.KeyIds.ShouldBe(["k1", "k2"]);
    }

    [Fact]
    public void A_broken_ring_is_refused_at_start()
    {
        var k1 = NewPrivatePem();
        var k2 = NewPrivatePem();

        Should.Throw<InvalidOperationException>(() => Provider(new JwtOptions { Keys = [new() { KeyId = "a", SigningKeyPem = k1 }, new() { KeyId = "a", SigningKeyPem = k2 }], CurrentKeyId = "a" })).Message.ShouldContain("duplicate");
        Should.Throw<InvalidOperationException>(() => Provider(new JwtOptions { Keys = [new() { KeyId = "a", SigningKeyPem = k1 }, new() { KeyId = "b", SigningKeyPem = k2 }] ,SigningKeyId = "zz" })).Message.ShouldContain("CurrentKeyId is required");
        Should.Throw<InvalidOperationException>(() => Provider(new JwtOptions { Keys = [new() { KeyId = "a", SigningKeyPem = k1 }], CurrentKeyId = "nope" })).Message.ShouldContain("not in the JWT keyring");
        Should.Throw<InvalidOperationException>(() => Provider(new JwtOptions { Keys = [new() { KeyId = "a", PublicKeyPem = PublicOf(k1) }], CurrentKeyId = "a" })).Message.ShouldContain("no private signing key");
        Should.Throw<InvalidOperationException>(() => Provider(new JwtOptions { Keys = [new() { KeyId = "a" }] })).Message.ShouldContain("neither");
        Should.Throw<InvalidOperationException>(() => Provider(new JwtOptions { Keys = [new() { KeyId = "a", SigningKeyPem = "garbage" }], CurrentKeyId = "a" }));
        Should.Throw<InvalidOperationException>(() => Provider(new JwtOptions())).Message.ShouldContain("No JWT signing key");
    }
}
