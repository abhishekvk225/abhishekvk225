using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Configurations;

internal sealed class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    public void Configure(EntityTypeBuilder<Client> b)
    {
        b.ToTable("Clients", "tenancy");
        b.Property(x => x.Code).HasMaxLength(30).IsUnicode(false).IsRequired();
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.LegalName).HasMaxLength(200);
        b.Property(x => x.ContactEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.ContactPhone).HasMaxLength(40);
        b.Property(x => x.AddressLine1).HasMaxLength(150);
        b.Property(x => x.AddressLine2).HasMaxLength(150);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.State).HasMaxLength(100);
        b.Property(x => x.PostalCode).HasMaxLength(20);
        b.Property(x => x.Country).HasMaxLength(2).IsUnicode(false).IsFixedLength();
        b.Property(x => x.Website).HasMaxLength(200);
        b.Property(x => x.Industry).HasMaxLength(100);
        b.Property(x => x.TimeZone).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.Property(x => x.StatusReason).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => new { x.Status, x.Name });
        b.HasIndex(x => x.Name);
    }
}

internal sealed class ClientSettingConfiguration : IEntityTypeConfiguration<ClientSetting>
{
    public void Configure(EntityTypeBuilder<ClientSetting> b)
    {
        b.ToTable("ClientSettings", "tenancy");
        b.Property(x => x.Key).HasMaxLength(100).IsUnicode(false).IsRequired();
        b.Property(x => x.ValueJson).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.Key }).IsUnique();
    }
}

internal sealed class ClientKeyConfiguration : IEntityTypeConfiguration<ClientKey>
{
    public void Configure(EntityTypeBuilder<ClientKey> b)
    {
        b.ToTable("ClientKeys", "tenancy");
        b.Property(x => x.WrappedDataKey).HasColumnType("varbinary(256)").IsRequired();
        b.Property(x => x.MasterKeyId).HasMaxLength(50).IsUnicode(false).IsRequired();
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.KeyVersion }).IsUnique();
    }
}

internal sealed class ClientUserConfiguration : IEntityTypeConfiguration<ClientUser>
{
    public void Configure(EntityTypeBuilder<ClientUser> b)
    {
        b.ToTable("ClientUsers", "tenancy");
        b.Property(x => x.JobTitle).HasMaxLength(100);
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<NexaVerify.Domain.Identity.User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.UserId).IsUnique(); // v1: one client per user (drop this index to allow several)
        b.HasIndex(x => new { x.ClientId, x.IsActive });
    }
}
