using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexaVerify.Domain.Messaging;

namespace NexaVerify.Infrastructure.Persistence.Configurations;

internal sealed class OutboundEmailConfiguration : IEntityTypeConfiguration<OutboundEmail>
{
    public void Configure(EntityTypeBuilder<OutboundEmail> b)
    {
        // Platform data: not tenant-owned (a mail can concern a visitor who has no tenant yet). Bodies are erased early (one-time links) and removed after the retention period.
        b.ToTable("OutboundEmails", "tenancy");
        b.Property(x => x.ToAddress).HasMaxLength(OutboundEmail.AddressMaxLength).IsRequired();
        b.Property(x => x.ToDomain).HasMaxLength(OutboundEmail.AddressMaxLength).IsRequired();
        b.Property(x => x.ToName).HasMaxLength(OutboundEmail.NameMaxLength);
        b.Property(x => x.Subject).HasMaxLength(OutboundEmail.SubjectMaxLength).IsRequired();
        b.Property(x => x.TextBody).HasColumnType("nvarchar(max)");
        b.Property(x => x.HtmlBody).HasColumnType("nvarchar(max)");
        b.Property(x => x.CorrelationId).HasMaxLength(OutboundEmail.CorrelationMaxLength).IsRequired();
        b.Property(x => x.MessageId).HasMaxLength(OutboundEmail.MessageIdMaxLength).IsRequired();
        b.Property(x => x.LastError).HasMaxLength(OutboundEmail.ErrorMaxLength);
        b.Property(x => x.LeaseToken).IsConcurrencyToken();

        b.HasIndex(x => new { x.Status, x.NextAttemptAt });
        b.HasIndex(x => new { x.Status, x.LeaseUntil });
        b.HasIndex(x => x.LastAttemptAt).HasFilter("[LastAttemptAt] IS NOT NULL"); // the per-minute send budget
        b.HasIndex(x => x.CreatedAt).IsDescending(); // the admin list and the retention purge
        b.HasIndex(x => new { x.ToAddress, x.CreatedAt });
    }
}

internal sealed class EmailSuppressionConfiguration : IEntityTypeConfiguration<EmailSuppression>
{
    public void Configure(EntityTypeBuilder<EmailSuppression> b)
    {
        b.ToTable("EmailSuppressions", "tenancy");
        b.Property(x => x.Address).HasMaxLength(OutboundEmail.AddressMaxLength).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(EmailSuppression.ReasonMaxLength).IsRequired();
        b.HasIndex(x => x.Address).IsUnique();
        b.HasIndex(x => x.CreatedAt);
    }
}
