using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Abstractions;
using NexaVerify.Domain.Common;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.Infrastructure.Persistence.Rls;
using NexaVerify.Infrastructure.Tenancy;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Persistence;

public class TenantModelRulesTests
{
    public sealed class ForgotInterface
    {
        public Guid Id { get; set; }

        public Guid ClientId { get; set; }
    }

    public class BaseOwned : Entity, ITenantOwned
    {
        public Guid ClientId { get; set; }
    }

    public sealed class DerivedStrict : BaseOwned, IStrictTenantOwned
    {
    }

    public sealed class Parent : Entity, ITenantOwned
    {
        public Guid ClientId { get; set; }

        public List<Child> Children { get; set; } = [];
    }

    public sealed class Child
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class WeirdColumn : Entity, ITenantOwned
    {
        public Guid ClientId { get; set; }
    }

    /// <summary>EF caches one model per context type; these tests build a different shape each time.</summary>
    private sealed class UniqueModelKeyFactory : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => new object();
    }

    private sealed class Ctx : AppDbContext
    {
        private readonly Action<ModelBuilder> _configure;

        public Ctx(Action<ModelBuilder> configure)
            : base(new DbContextOptionsBuilder<Ctx>().UseSqlServer("Server=127.0.0.1,1;Database=none")
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, UniqueModelKeyFactory>().Options, new TenantContext(new StubCurrentUser()))
        {
            _configure = configure;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _configure(modelBuilder);
            base.OnModelCreating(modelBuilder);
        }
    }

    private static Exception? Build(Action<ModelBuilder> configure)
    {
        try
        {
            using var context = new Ctx(configure);
            _ = context.Model;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void Entities_with_client_id_must_implement_the_tenant_interface()
    {
        var ex = Build(b => b.Entity<ForgotInterface>().ToTable("Forgot", "test"));

        ex.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("ForgotInterface has a ClientId property but does not implement ITenantOwned");
    }

    [Fact]
    public void Derived_types_must_match_the_strictness_of_their_root()
    {
        var ex = Build(b =>
        {
            b.Entity<BaseOwned>().ToTable("Base", "test");
            b.Entity<DerivedStrict>();
        });

        ex.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("same tenant strictness");
    }

    [Fact]
    public void Owned_collections_in_their_own_table_are_rejected_for_tenant_data()
    {
        var ex = Build(b => b.Entity<Parent>(p =>
        {
            p.ToTable("Parents", "test");
            p.OwnsMany(x => x.Children);
        }));

        ex.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("owned type stored in its own table");
    }

    [Fact]
    public void The_policy_uses_the_mapped_column_name_and_escapes_identifiers()
    {
        using var context = new Ctx(b => b.Entity<WeirdColumn>(e =>
        {
            e.ToTable("We]ird", "te]st");
            e.Property(x => x.ClientId).HasColumnName("Tenant]Col");
        }));

        var policy = RowLevelSecurityScriptBuilder.Build(Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                .GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(context).Model)
            .Single(b => b.StartsWith("CREATE SECURITY POLICY", StringComparison.Ordinal));

        policy.ShouldContain("([Tenant]]Col]) ON [te]]st].[We]]ird]");
    }
}
