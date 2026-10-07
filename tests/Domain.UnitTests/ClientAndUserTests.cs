using NexaVerify.Domain.Common;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Domain.UnitTests;

public class ClientAndUserTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Client NewClient() => Client.Create("acme", "Acme", "a@acme.test", "UTC", Now);

    [Fact]
    public void New_clients_are_active_with_a_normalised_code()
    {
        var client = NewClient();

        client.Code.ShouldBe("ACME");
        client.Status.ShouldBe(ClientStatus.Active);
        client.CanSignIn.ShouldBeTrue();
        client.IsSystem.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("0123456789012345678901234567890")]
    public void Invalid_codes_are_rejected(string code)
    {
        Should.Throw<DomainException>(() => Client.Create(code, "N", "a@b.test", "UTC", Now)).Code.ShouldBe("CLIENT_CODE_INVALID");
    }

    [Fact]
    public void Suspend_requires_a_reason_and_records_who_and_when()
    {
        var client = NewClient();
        var actor = Guid.NewGuid();

        Should.Throw<DomainException>(() => client.Suspend(" ", actor, Now)).Code.ShouldBe("CLIENT_SUSPEND_REASON_REQUIRED");
        client.Suspend("overdue", actor, Now.AddDays(1));

        client.Status.ShouldBe(ClientStatus.Suspended);
        client.StatusReason.ShouldBe("overdue");
        client.StatusChangedBy.ShouldBe(actor);
        client.StatusChangedAt.ShouldBe(Now.AddDays(1));
        client.CanSignIn.ShouldBeFalse();
    }

    [Fact]
    public void Transitions_to_the_current_status_are_rejected_and_activation_clears_the_reason()
    {
        var client = NewClient();
        Should.Throw<DomainException>(() => client.Activate(null, Now)).Code.ShouldBe("CLIENT_INVALID_TRANSITION");

        client.Deactivate("paused", null, Now);
        client.Activate(null, Now);

        client.Status.ShouldBe(ClientStatus.Active);
        client.StatusReason.ShouldBeNull();
    }

    [Fact]
    public void Client_users_cannot_belong_to_the_platform_tenant_or_to_nobody()
    {
        Should.Throw<DomainException>(() => User.Create("a@b.test", "A", "hash", PlatformTenant.ClientId, false, false)).Code.ShouldBe("USER_CLIENT_INVALID");
        Should.Throw<DomainException>(() => User.Create("a@b.test", "A", "hash", Guid.Empty, false, false)).Code.ShouldBe("USER_CLIENT_INVALID");
        User.Create("a@b.test", "A", "hash", Guid.Empty, true, false).ClientId.ShouldBe(PlatformTenant.ClientId); // platform staff always belong to the platform tenant
    }

    [Fact]
    public void Lockout_does_not_stop_a_user_from_holding_sessions()
    {
        var user = User.Create("a@b.test", "A", "hash", Guid.NewGuid(), false, false);

        user.CanSignIn().ShouldBeTrue();
        user.Deactivate();
        user.CanSignIn().ShouldBeFalse();
        user.SecurityVersion.ShouldBe(2);
    }

    [Fact]
    public void Setting_a_password_ends_existing_sessions_but_upgrading_the_hash_does_not()
    {
        var user = User.Create("a@b.test", "A", "hash", Guid.NewGuid(), false, true);

        user.UpgradeHash("stronger");
        user.SecurityVersion.ShouldBe(1);

        user.SetPassword("new-hash", Now, mustChangePassword: false);
        user.SecurityVersion.ShouldBe(2);
        user.MustChangePassword.ShouldBeFalse();
        user.PasswordHash.ShouldBe("new-hash");
    }

    [Fact]
    public void Role_permissions_must_match_the_role_scope()
    {
        var platformRole = Role.Create("Ops", RoleScope.Platform, false, null);
        var clientPermission = Permission.Create("faces.verify", "Faces", PermissionScope.Client, "x");
        var both = Permission.Create("usage.read", "Usage", PermissionScope.Both, "x");

        Should.Throw<DomainException>(() => platformRole.GrantPermission(clientPermission)).Code.ShouldBe("ROLE_PERMISSION_SCOPE_MISMATCH");
        platformRole.GrantPermission(both);
        platformRole.GrantPermission(both); // idempotent
        platformRole.Permissions.Count.ShouldBe(1);
        Role.Create("Sys", RoleScope.Platform, true, null).Invoking(r => r.EnsureEditable()).ShouldThrow<DomainException>().Code.ShouldBe("ROLE_IMMUTABLE");
    }

    [Fact]
    public void Refresh_tokens_cap_their_expiry_at_the_family_limit_and_revoke_once()
    {
        var user = User.Create("a@b.test", "A", "hash", Guid.NewGuid(), false, false);
        var token = RefreshToken.Issue(user, [1], Guid.NewGuid(), Now, TimeSpan.FromDays(7), Now.AddDays(3), "127.0.0.1", null);

        token.ExpiresAt.ShouldBe(Now.AddDays(3));
        token.IsUsable(Now.AddDays(2)).ShouldBeTrue();
        token.IsUsable(Now.AddDays(3)).ShouldBeFalse();

        token.Revoke(Now, "first");
        token.Revoke(Now.AddHours(1), "second");
        token.RevokedReason.ShouldBe("first");
        token.RevokedAt.ShouldBe(Now);
    }
}

internal static class ShouldExtensions
{
    public static TException ShouldThrow<TException>(this Action action)
        where TException : Exception => Should.Throw<TException>(action);

    public static Action Invoking<T>(this T subject, Action<T> call) => () => call(subject);
}
