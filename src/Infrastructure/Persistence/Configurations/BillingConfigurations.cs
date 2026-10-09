using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexaVerify.Domain.Billing;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Configurations;

internal sealed class CreditPackConfiguration : IEntityTypeConfiguration<CreditPack>
{
    public void Configure(EntityTypeBuilder<CreditPack> b)
    {
        b.ToTable("CreditPacks", "billing", t =>
        {
            t.HasCheckConstraint("CK_CreditPacks_Amounts", "[Credits] > 0 AND [ValidityDays] > 0 AND [PriceMinor] > 0");
        });
        b.Property(x => x.Name).HasMaxLength(CreditPack.NameMaxLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(CreditPack.DescriptionMaxLength);
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        b.Property(x => x.Highlights)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<string>>(
                    (a, c) => a != null && c != null && a.SequenceEqual(c),
                    v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                    v => v.ToList()))
            .HasMaxLength(2000)
            .IsRequired();
        b.HasIndex(x => new { x.IsPublic, x.IsActive, x.DisplayOrder });
    }
}

internal sealed class BillingProfileConfiguration : IEntityTypeConfiguration<BillingProfile>
{
    public void Configure(EntityTypeBuilder<BillingProfile> b)
    {
        b.ToTable("BillingProfiles", "billing");
        b.Property(x => x.LegalName).HasMaxLength(BillingProfile.NameMaxLength).IsRequired();
        b.Property(x => x.AddressLine1).HasMaxLength(BillingProfile.LineMaxLength).IsRequired();
        b.Property(x => x.AddressLine2).HasMaxLength(BillingProfile.LineMaxLength);
        b.Property(x => x.City).HasMaxLength(BillingProfile.ShortMaxLength).IsRequired();
        b.Property(x => x.State).HasMaxLength(BillingProfile.ShortMaxLength);
        b.Property(x => x.PostalCode).HasMaxLength(BillingProfile.PostalMaxLength).IsRequired();
        b.Property(x => x.Country).HasMaxLength(2).IsFixedLength().IsUnicode(false).IsRequired();
        b.Property(x => x.TaxId).HasMaxLength(BillingProfile.TaxIdMaxLength).IsUnicode(false);
        b.Property(x => x.BillingEmail).HasMaxLength(BillingProfile.EmailMaxLength).IsRequired();
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ClientId).IsUnique(); // one profile per client; a parallel first save fails instead of creating a second
    }
}

internal sealed class PaymentOrderConfiguration : IEntityTypeConfiguration<PaymentOrder>
{
    public void Configure(EntityTypeBuilder<PaymentOrder> b)
    {
        b.ToTable("PaymentOrders", "billing", t =>
        {
            t.HasCheckConstraint("CK_PaymentOrders_Amounts", "[SubtotalMinor] > 0 AND [TaxMinor] >= 0 AND [TotalMinor] = [SubtotalMinor] + [TaxMinor]");
            t.HasCheckConstraint("CK_PaymentOrders_Refunds", "[RefundedMinor] >= 0 AND [RefundedMinor] <= [TotalMinor] AND [CreditsRevoked] >= 0 AND [CreditsRevoked] <= [Credits]");
        });
        b.Property(x => x.PackName).HasMaxLength(CreditPack.NameMaxLength).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        b.Property(x => x.TaxPercent).HasPrecision(5, 2);
        b.Property(x => x.TaxLabel).HasMaxLength(30).IsRequired();
        b.Property(x => x.Provider).HasMaxLength(20).IsUnicode(false).IsRequired();
        b.Property(x => x.ProviderSessionId).HasMaxLength(200).IsUnicode(false);
        b.Property(x => x.ProviderPaymentId).HasMaxLength(200).IsUnicode(false);
        b.Property(x => x.CheckoutUrl).HasMaxLength(2048).IsUnicode(false);
        b.Property(x => x.InvoiceNumber).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.IdempotencyKey).HasMaxLength(PaymentOrder.IdempotencyKeyMaxLength).IsUnicode(false);
        b.Property(x => x.BuyerJson).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.FailureReason).HasMaxLength(200);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CreditPack>().WithMany().HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<License>().WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.IdempotencyKey }).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
        b.HasIndex(x => x.InvoiceNumber).IsUnique().HasFilter("[InvoiceNumber] IS NOT NULL");
        b.HasIndex(x => new { x.ClientId, x.CreatedAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => x.PackId);
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.ToTable("Refunds", "billing", t => t.HasCheckConstraint("CK_Refunds_Amount", "[AmountMinor] > 0 AND [CreditsRevoked] >= 0"));
        b.Property(x => x.Reason).HasMaxLength(Refund.ReasonMaxLength).IsRequired();
        b.Property(x => x.ProviderRefundId).HasMaxLength(100).IsUnicode(false);
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.ProviderRefundId).IsUnique().HasFilter("[ProviderRefundId] IS NOT NULL");
    }
}

internal sealed class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
{
    public void Configure(EntityTypeBuilder<PaymentEvent> b)
    {
        b.ToTable("PaymentEvents", "billing");
        b.Property(x => x.Provider).HasMaxLength(20).IsUnicode(false).IsRequired();
        b.Property(x => x.EventId).HasMaxLength(100).IsUnicode(false).IsRequired();
        b.Property(x => x.Type).HasMaxLength(30).IsUnicode(false).IsRequired();
        b.Property(x => x.Outcome).HasMaxLength(60).IsUnicode(false);
        b.HasIndex(x => new { x.Provider, x.EventId }).IsUnique(); // the replay guard
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.ReceivedAt);
    }
}

internal sealed class InvoiceSequenceConfiguration : IEntityTypeConfiguration<InvoiceSequence>
{
    public void Configure(EntityTypeBuilder<InvoiceSequence> b)
    {
        b.ToTable("InvoiceSequences", "billing", t => t.HasCheckConstraint("CK_InvoiceSequences_Number", "[LastNumber] >= 0"));
        b.HasKey(x => x.Year);
        b.Property(x => x.Year).ValueGeneratedNever();
    }
}
