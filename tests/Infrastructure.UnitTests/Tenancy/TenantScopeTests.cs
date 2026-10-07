using NexaVerify.Application.Common;
using NexaVerify.Infrastructure.Tenancy;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.UnitTests.Tenancy;

public class TenantScopeTests
{
    private readonly StubCurrentUser _user = new();

    private TenantContext Create() => new(_user);

    [Fact]
    public void Disposing_outer_scope_first_cannot_resurrect_it()
    {
        var tenant = Create();
        var platform = tenant.BeginPlatform("outer");
        var inner = tenant.BeginTenant(Guid.NewGuid());

        platform.Dispose(); // out of order
        inner.Dispose();

        tenant.IsPlatform.ShouldBeFalse();
        tenant.ClientId.ShouldBeNull();
    }

    [Fact]
    public async Task Disposing_in_another_execution_context_still_ends_the_scope_for_the_owner()
    {
        var tenant = Create();
        var id = Guid.NewGuid();
        var scope = tenant.BeginTenant(id);
        tenant.ClientId.ShouldBe(id);

        await Task.Run(() => scope.Dispose()); // fire-and-forget style disposal

        tenant.ClientId.ShouldBeNull();
    }

    [Fact]
    public async Task A_scope_entered_inside_an_awaited_helper_does_not_flow_back_to_the_caller()
    {
        var tenant = Create();

        async Task Helper()
        {
            tenant.BeginPlatform("inside helper");
            await Task.Yield();
        }

        await Helper();

        // AsyncLocal changes made inside an awaited async method are not visible to the caller (documented pitfall):
        // always enter and dispose scopes in the same method that runs the scoped work.
        tenant.IsPlatform.ShouldBeFalse();
    }

    [Fact]
    public void A_tenant_principal_cannot_scope_itself_to_another_tenant()
    {
        var own = Guid.NewGuid();
        _user.IsAuthenticated = true;
        _user.ClientId = own;
        var tenant = Create();

        Should.Throw<TenantViolationException>(() => tenant.BeginTenant(Guid.NewGuid()));
        using (tenant.BeginTenant(own))
        {
            tenant.ClientId.ShouldBe(own);
        }
    }

    [Fact]
    public void Platform_scope_requires_a_reason()
    {
        Should.Throw<ArgumentException>(() => Create().BeginPlatform(" "));
    }

    [Fact]
    public void An_authenticated_client_principal_cannot_enter_platform_scope()
    {
        _user.IsAuthenticated = true;
        _user.ClientId = Guid.NewGuid();

        Should.Throw<TenantViolationException>(() => Create().BeginPlatform("escalate"));
    }

    [Fact]
    public void Anonymous_work_and_platform_principals_may_enter_platform_scope()
    {
        using (Create().BeginPlatform("pre-auth lookup"))
        {
        }

        _user.IsAuthenticated = true;
        _user.IsPlatformUser = true;
        using (Create().BeginPlatform("platform job"))
        {
        }
    }
}
