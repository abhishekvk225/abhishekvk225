using NexaVerify.Application.Abstractions;
using NexaVerify.Infrastructure.Tenancy;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.UnitTests.Tenancy;

public class TenantContextTests
{
    private readonly StubCurrentUser _user = new();

    private TenantContext Create() => new(_user);

    [Fact]
    public void Unauthenticated_has_no_tenant_and_is_not_platform()
    {
        var tenant = Create();

        tenant.ClientId.ShouldBeNull();
        tenant.IsPlatform.ShouldBeFalse();
    }

    [Fact]
    public void Client_user_resolves_to_its_client()
    {
        var clientId = Guid.NewGuid();
        _user.IsAuthenticated = true;
        _user.ClientId = clientId;

        var tenant = Create();

        tenant.ClientId.ShouldBe(clientId);
        tenant.IsPlatform.ShouldBeFalse();
    }

    [Fact]
    public void Platform_user_is_platform_without_client()
    {
        _user.IsAuthenticated = true;
        _user.IsPlatformUser = true;
        _user.ClientId = Guid.NewGuid(); // a platform principal must never be bound to a client

        var tenant = Create();

        tenant.IsPlatform.ShouldBeTrue();
        tenant.ClientId.ShouldBeNull();
    }

    [Fact]
    public void Explicit_scope_overrides_principal_and_restores_on_dispose()
    {
        var tenant = Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        using (tenant.BeginTenant(a))
        {
            tenant.ClientId.ShouldBe(a);
            using (tenant.BeginTenant(b))
            {
                tenant.ClientId.ShouldBe(b);
            }

            tenant.ClientId.ShouldBe(a);
            using (tenant.BeginPlatform("test"))
            {
                tenant.IsPlatform.ShouldBeTrue();
                tenant.ClientId.ShouldBeNull();
            }

            tenant.IsPlatform.ShouldBeFalse();
        }

        tenant.ClientId.ShouldBeNull();
    }

    [Fact]
    public void Disposing_a_scope_twice_does_not_corrupt_the_outer_scope()
    {
        var tenant = Create();
        var outer = Guid.NewGuid();
        using var outerScope = tenant.BeginTenant(outer);

        var inner = tenant.BeginTenant(Guid.NewGuid());
        inner.Dispose();
        inner.Dispose();

        tenant.ClientId.ShouldBe(outer);
    }

    [Fact]
    public void Empty_client_id_is_rejected()
    {
        Should.Throw<ArgumentException>(() => Create().BeginTenant(Guid.Empty));
    }

    [Fact]
    public async Task Scope_does_not_leak_between_concurrent_flows()
    {
        var tenant = Create();
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();

        var results = await Task.WhenAll(ids.Select(async id =>
        {
            using (tenant.BeginTenant(id))
            {
                await Task.Delay(Random.Shared.Next(1, 20));
                await Task.Yield();
                return tenant.ClientId;
            }
        }));

        results.ShouldBe(ids.Select(i => (Guid?)i));
        tenant.ClientId.ShouldBeNull();
    }
}
