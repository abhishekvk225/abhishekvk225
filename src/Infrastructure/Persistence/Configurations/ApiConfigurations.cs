using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Configurations;

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.ToTable("ApiKeys", "api");
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.KeyPrefix).HasMaxLength(20).IsUnicode(false).IsRequired();
        b.Property(x => x.KeyHash).HasColumnType("binary(32)").IsRequired();
        b.Property(x => x.Scopes).HasMaxLength(1000).IsUnicode(false).IsRequired();
        b.Property(x => x.AllowedIps).HasMaxLength(2000).IsUnicode(false).IsRequired();
        b.Property(x => x.LastUsedIp).HasMaxLength(45).IsUnicode(false);
        b.Property(x => x.RevokedReason).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.ScopeList);
        b.Ignore(x => x.AllowedIpList);
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.KeyPrefix).IsUnique();
        b.HasIndex(x => new { x.ClientId, x.Status });
    }
}

internal sealed class ApiRequestLogConfiguration : IEntityTypeConfiguration<ApiRequestLog>
{
    public void Configure(EntityTypeBuilder<ApiRequestLog> b)
    {
        b.ToTable("ApiRequestLogs", "api");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Method).HasMaxLength(8).IsUnicode(false).IsRequired();
        b.Property(x => x.RouteTemplate).HasMaxLength(200).IsUnicode(false).IsRequired();
        b.Property(x => x.IpAddress).HasMaxLength(45).IsUnicode(false);
        b.Property(x => x.UserAgent).HasMaxLength(200);
        b.Property(x => x.ErrorCode).HasMaxLength(60).IsUnicode(false);
        b.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false);
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.CreatedAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.ApiKeyId, x.CreatedAt });
        b.HasIndex(x => x.CreatedAt); // retention purge
    }
}
