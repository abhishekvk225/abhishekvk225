using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Configurations;

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.ToTable("Plans", "licensing", t =>
        {
            t.HasCheckConstraint("CK_Plans_Defaults", "[DefaultCredits] >= 0 AND [DefaultDurationDays] > 0");
        });
        b.Property(x => x.Code).HasMaxLength(30).IsUnicode(false).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class LicenseConfiguration : IEntityTypeConfiguration<License>
{
    public void Configure(EntityTypeBuilder<License> b)
    {
        b.ToTable("Licenses", "licensing", t =>
        {
            t.HasCheckConstraint("CK_Licenses_Credits", "[TotalCredits] >= 0 AND [ConsumedCredits] >= 0 AND [ConsumedCredits] <= [TotalCredits]");
            t.HasCheckConstraint("CK_Licenses_Period", "[ExpiresAt] > [StartsAt]");
        });
        b.Property(x => x.LicenseKey).HasMaxLength(40).IsUnicode(false).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.SuspendedReason).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.LicenseKey).IsUnique();
        b.HasIndex(x => new { x.ClientId, x.Status, x.ExpiresAt });
        b.HasIndex(x => new { x.Status, x.ExpiresAt });
    }
}

internal sealed class CostRuleConfiguration : IEntityTypeConfiguration<CostRule>
{
    public void Configure(EntityTypeBuilder<CostRule> b)
    {
        b.ToTable("CostRules", "licensing", t => t.HasCheckConstraint("CK_CostRules_Credits", "[Credits] BETWEEN 0 AND 1000"));
        b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PlanId, x.Operation, x.EffectiveFrom });
    }
}

internal sealed class ClientCostRuleConfiguration : IEntityTypeConfiguration<ClientCostRule>
{
    public void Configure(EntityTypeBuilder<ClientCostRule> b)
    {
        b.ToTable("ClientCostRules", "licensing", t => t.HasCheckConstraint("CK_ClientCostRules_Credits", "[Credits] BETWEEN 0 AND 1000"));
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.Operation, x.EffectiveFrom });
    }
}

internal sealed class LicenseTransactionConfiguration : IEntityTypeConfiguration<LicenseTransaction>
{
    public void Configure(EntityTypeBuilder<LicenseTransaction> b)
    {
        b.ToTable("LicenseTransactions", "licensing", t =>
        {
            t.HasCheckConstraint("CK_LicenseTransactions_Balance", "[BalanceAfter] = [BalanceBefore] + [Credits] AND [BalanceAfter] >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsUnicode(false);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.ActorType).HasMaxLength(20).IsUnicode(false).IsRequired();
        b.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.PrevHash).HasColumnType("binary(32)").IsRequired();
        b.Property(x => x.RowHash).HasColumnType("binary(32)").IsRequired();
        b.HasOne<License>().WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LicenseTransaction>().WithMany().HasForeignKey(x => x.ReferenceTransactionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.LicenseId, x.Id });
        b.HasIndex(x => new { x.LicenseId, x.PrevHash }).IsUnique(); // a chain cannot fork: one successor per previous hash
        // Dashboards and usage reports aggregate by day: covering indexes keep them to an index range scan (per client, and platform-wide).
        b.HasIndex(x => new { x.ClientId, x.CreatedAt }).IncludeProperties(x => new { x.Type, x.Credits, x.Operation });
        b.HasIndex(x => x.CreatedAt).IncludeProperties(x => new { x.ClientId, x.Type, x.Credits, x.Operation });
        b.HasIndex(x => new { x.ClientId, x.IdempotencyKey }).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
        b.HasIndex(x => x.ReferenceTransactionId).IsUnique().HasFilter("[ReferenceTransactionId] IS NOT NULL AND [Type] = 'Refund'");
    }
}

internal sealed class LicenseAlertConfiguration : IEntityTypeConfiguration<LicenseAlert>
{
    public void Configure(EntityTypeBuilder<LicenseAlert> b)
    {
        b.ToTable("LicenseAlerts", "licensing");
        b.Property(x => x.Bucket).HasMaxLength(60).IsUnicode(false).IsRequired();
        b.Property(x => x.Title).HasMaxLength(150).IsRequired();
        b.Property(x => x.Message).HasMaxLength(400).IsRequired();
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.SubjectId, x.AlertType, x.Bucket }).IsUnique(); // the de-duplication guarantee
        b.HasIndex(x => new { x.ClientId, x.CreatedAt }).IsDescending(false, true); // the notification feed
    }
}
