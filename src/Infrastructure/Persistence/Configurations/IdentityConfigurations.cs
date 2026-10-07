using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("Users", "iam");
        b.Property(x => x.Email).HasMaxLength(256).IsRequired();
        b.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
        b.Property(x => x.FullName).HasMaxLength(150).IsRequired();
        b.Property(x => x.PhoneNumber).HasMaxLength(40);
        b.HasIndex(x => x.NormalizedEmail).IsUnique();
        b.HasIndex(x => new { x.ClientId, x.Status });
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("Roles", "iam");
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.NormalizedName).HasMaxLength(60).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.NormalizedName).IsUnique();
        b.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Permissions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> b)
    {
        b.ToTable("Permissions", "iam");
        b.Property(x => x.Key).HasMaxLength(80).IsRequired();
        b.Property(x => x.Group).HasMaxLength(40).IsRequired();
        b.Property(x => x.Description).HasMaxLength(300).IsRequired();
        b.HasIndex(x => x.Key).IsUnique();
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("RolePermissions", "iam");
        b.HasKey(x => new { x.RoleId, x.PermissionId });
        b.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ToTable("UserRoles", "iam");
        b.HasKey(x => new { x.UserId, x.RoleId });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.UserId });
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("RefreshTokens", "iam");
        b.Property(x => x.TokenHash).HasColumnType("varbinary(32)").IsRequired();
        b.Property(x => x.RevokedReason).HasMaxLength(100);
        b.Property(x => x.CreatedByIp).HasMaxLength(45).IsUnicode(false);
        b.Property(x => x.UserAgent).HasMaxLength(300);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => new { x.UserId, x.RevokedAt });
        b.HasIndex(x => x.FamilyId);
        b.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> b)
    {
        b.ToTable("PasswordResetTokens", "iam");
        b.Property(x => x.TokenHash).HasColumnType("varbinary(32)").IsRequired();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => new { x.UserId, x.UsedAt });
        b.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class LoginHistoryConfiguration : IEntityTypeConfiguration<LoginHistory>
{
    public void Configure(EntityTypeBuilder<LoginHistory> b)
    {
        b.ToTable("LoginHistory", "iam");
        b.Property(x => x.EmailAttempted).HasMaxLength(256).IsRequired();
        b.Property(x => x.FailureReason).HasMaxLength(200);
        b.Property(x => x.IpAddress).HasMaxLength(45).IsUnicode(false);
        b.Property(x => x.UserAgent).HasMaxLength(300);
        b.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false);
        b.HasIndex(x => new { x.ClientId, x.OccurredAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.UserId, x.OccurredAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.IpAddress, x.OccurredAt });
    }
}
