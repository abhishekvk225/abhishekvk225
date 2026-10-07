using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexaVerify.Domain.Auditing;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Configurations;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs", "audit");
        b.Property(x => x.ActorEmail).HasMaxLength(256);
        b.Property(x => x.Action).HasMaxLength(80).IsUnicode(false).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(60).IsUnicode(false).IsRequired();
        b.Property(x => x.EntityId).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.Property(x => x.OldValuesJson).HasColumnType("nvarchar(max)");
        b.Property(x => x.NewValuesJson).HasColumnType("nvarchar(max)");
        b.Property(x => x.IpAddress).HasMaxLength(45).IsUnicode(false);
        b.Property(x => x.UserAgent).HasMaxLength(300);
        b.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false);
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.OccurredAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredAt });
        b.HasIndex(x => new { x.ActorId, x.OccurredAt });
        b.HasIndex(x => new { x.Action, x.OccurredAt });
    }
}
