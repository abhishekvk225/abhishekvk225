using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexaVerify.Domain.Faces;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Configurations;

internal sealed class FaceProfileConfiguration : IEntityTypeConfiguration<FaceProfile>
{
    public void Configure(EntityTypeBuilder<FaceProfile> b)
    {
        b.ToTable("FaceProfiles", "face");
        b.Property(x => x.ExternalRef).HasMaxLength(100).IsRequired();
        b.Property(x => x.DisplayNameEnc).HasMaxLength(1024);
        b.Property(x => x.MetadataJson).HasMaxLength(4096);
        b.Property(x => x.ConsentReference).HasMaxLength(200).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.ExternalRef }).IsUnique();
        b.HasIndex(x => new { x.ClientId, x.Status });
        b.HasIndex(x => new { x.ClientId, x.RetentionUntil }).HasFilter("[RetentionUntil] IS NOT NULL");
    }
}

internal sealed class FaceTemplateConfiguration : IEntityTypeConfiguration<FaceTemplate>
{
    public void Configure(EntityTypeBuilder<FaceTemplate> b)
    {
        b.ToTable("FaceTemplates", "face", t => t.HasCheckConstraint("CK_FaceTemplates_Quality", "[QualityScore] BETWEEN 0 AND 1"));
        b.Property(x => x.Provider).HasMaxLength(40).IsUnicode(false).IsRequired();
        b.Property(x => x.ModelVersion).HasMaxLength(40).IsUnicode(false).IsRequired();
        b.Property(x => x.EmbeddingEnc).IsRequired();
        b.Property(x => x.QualityScore).HasPrecision(5, 4);
        b.Property(x => x.ImageSha256).HasColumnType("binary(32)").IsRequired();
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FaceProfile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.ClientId, x.ProfileId });
        b.HasIndex(x => new { x.ClientId, x.Provider, x.ModelVersion, x.Status });
        b.HasIndex(x => new { x.ClientId, x.ImageSha256 });
    }
}

internal sealed class RecognitionRequestConfiguration : IEntityTypeConfiguration<RecognitionRequest>
{
    public void Configure(EntityTypeBuilder<RecognitionRequest> b)
    {
        b.ToTable("RecognitionRequests", "face", t =>
        {
            t.HasCheckConstraint("CK_RecognitionRequests_Credits", "[CreditsCharged] >= 0");
        });
        b.Property(x => x.ErrorCode).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.ThresholdUsed).HasPrecision(5, 4);
        b.Property(x => x.BestScore).HasPrecision(5, 4);
        b.Property(x => x.Provider).HasMaxLength(40).IsUnicode(false).IsRequired();
        b.Property(x => x.ModelVersion).HasMaxLength(40).IsUnicode(false).IsRequired();
        b.Property(x => x.InputImageSha256).HasColumnType("binary(32)").IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsUnicode(false);
        b.Property(x => x.IpAddress).HasMaxLength(45).IsUnicode(false);
        b.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false);
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Matches).WithOne().HasForeignKey(m => m.RequestId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Matches).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasIndex(x => new { x.ClientId, x.CreatedAt }).IsDescending(false, true).IncludeProperties(x => new { x.Operation, x.Outcome, x.CreditsCharged }); // dashboards and usage reports
        b.HasIndex(x => new { x.ClientId, x.Outcome, x.CreatedAt });
        b.HasIndex(x => new { x.ClientId, x.TargetProfileId, x.CreatedAt });
        b.HasIndex(x => new { x.ClientId, x.IdempotencyKey }).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
    }
}

internal sealed class MatchResultConfiguration : IEntityTypeConfiguration<MatchResult>
{
    public void Configure(EntityTypeBuilder<MatchResult> b)
    {
        b.ToTable("MatchResults", "face");
        b.HasKey(x => new { x.RequestId, x.Rank });
        b.Property(x => x.Score).HasPrecision(5, 4);
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.ProfileId });
    }
}
