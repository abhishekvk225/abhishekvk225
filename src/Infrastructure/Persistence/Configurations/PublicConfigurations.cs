using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Configurations;

internal sealed class PendingSignupConfiguration : IEntityTypeConfiguration<PendingSignup>
{
    public void Configure(EntityTypeBuilder<PendingSignup> b)
    {
        b.ToTable("PendingSignups", "iam");
        b.Property(x => x.CompanyName).HasMaxLength(PendingSignup.CompanyNameMaxLength).IsRequired();
        b.Property(x => x.FullName).HasMaxLength(PendingSignup.FullNameMaxLength).IsRequired();
        b.Property(x => x.Email).HasMaxLength(User.EmailMaxLength).IsRequired();
        b.Property(x => x.NormalizedEmail).HasMaxLength(User.EmailMaxLength).IsRequired();
        b.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
        b.Property(x => x.TokenHash).HasColumnType("varbinary(32)").IsRequired();

        // At most one live (unconsumed) sign-up per address; consumed rows are kept briefly and then purged.
        b.Property(x => x.ResendCount).HasDefaultValue(0);
        b.HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("[ConsumedAt] IS NULL");
        b.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class ContactRequestConfiguration : IEntityTypeConfiguration<ContactRequest>
{
    public void Configure(EntityTypeBuilder<ContactRequest> b)
    {
        b.ToTable("ContactRequests", "tenancy");
        b.Property(x => x.Name).HasMaxLength(ContactRequest.NameMaxLength).IsRequired();
        b.Property(x => x.Email).HasMaxLength(User.EmailMaxLength).IsRequired();
        b.Property(x => x.Company).HasMaxLength(ContactRequest.CompanyMaxLength);
        b.Property(x => x.Message).HasMaxLength(ContactRequest.MessageMaxLength).IsRequired();
        b.HasIndex(x => x.CreatedAt);
    }
}
