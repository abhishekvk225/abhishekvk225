using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexaVerify.Domain.Tenancy;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Infrastructure.Persistence.Configurations;

// M14 document verification (schema "verification"; docs/07 §9, docs/02 "M14 additions").
// Requests, Events and DownloadGrants are ITenantOwned (platform scope may read the metadata: it holds no personal data).
// Documents, DocumentBlobs, Results and Checks are IStrictTenantOwned: not even platform scope can read them (EF filter, write guard, RLS).
// ModuleSettings is a global switch table (no ClientId, no RLS).

internal sealed class VerificationRequestConfiguration : IEntityTypeConfiguration<VerificationRequest>
{
    public void Configure(EntityTypeBuilder<VerificationRequest> b)
    {
        b.ToTable("Requests", "verification", t =>
        {
            t.HasCheckConstraint("CK_Requests_Counters", "[CreditsCharged] >= 0 AND [ProviderCreditsCharged] >= 0 AND [Attempts] >= 0 AND [ProviderAttempts] >= 0");
            t.HasCheckConstraint("CK_Requests_Lease", "([LeaseToken] IS NULL AND [LeaseUntil] IS NULL) OR ([LeaseToken] IS NOT NULL AND [LeaseUntil] IS NOT NULL)");
        });

        b.Ignore(x => x.ReviewReasonCodes);
        b.Ignore(x => x.IsTerminal);

        b.Property(x => x.ExternalProviderId).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.ExternalReference).HasMaxLength(100).IsUnicode(false);
        b.Property(x => x.ReviewReasonCodesJson).HasMaxLength(1000).IsRequired(); // codes only (no messages, no values)
        b.Property(x => x.ErrorCode).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.ExternalRef).HasMaxLength(100); // the caller's own reference: documented as "no personal data", never logged
        b.Property(x => x.ConsentReference).HasMaxLength(100);
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsUnicode(false);
        b.Property(x => x.DocumentSha256).HasColumnType("binary(32)");
        b.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.Property(x => x.IpAddress).HasMaxLength(45).IsUnicode(false);
        b.Property(x => x.RowVersion).IsRowVersion();

        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Events).WithOne().HasForeignKey(e => e.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Events).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Tenant history list and the dashboard (newest first); covering for the list's type/status columns.
        b.HasIndex(x => new { x.ClientId, x.CreatedAt }).IsDescending(false, true).IncludeProperties(x => new { x.Type, x.Status });
        b.HasIndex(x => new { x.ClientId, x.Status, x.CreatedAt }).IsDescending(false, false, true);
        b.HasIndex(x => new { x.ClientId, x.Type, x.Status, x.CreatedAt }).IsDescending(false, false, false, true);
        // Idempotent submit: one request per (tenant, server-derived key).
        b.HasIndex(x => new { x.ClientId, x.IdempotencyKey }).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
        // Worker claim (platform scope, UPDLOCK/READPAST) and lease recovery. Literal predicates: the claim SQL uses the same literals.
        b.HasIndex(x => new { x.Status, x.NextAttemptAt }).IncludeProperties(x => x.ClientId).HasFilter("[Status] = 'Queued'").HasDatabaseName("IX_Requests_Claim");
        b.HasIndex(x => new { x.Status, x.LeaseUntil }).HasFilter("[Status] = 'Processing'").HasDatabaseName("IX_Requests_Lease");
        // Retention sweeper and the personal-metadata (IpAddress) blanking.
        b.HasIndex(x => x.ExpiresAt).HasFilter("[PurgedAt] IS NULL AND [ExpiresAt] IS NOT NULL").HasDatabaseName("IX_Requests_Retention");
        b.HasIndex(x => x.CreatedAt).HasFilter("[IpAddress] IS NOT NULL").HasDatabaseName("IX_Requests_IpAddress");
        // Duplicate-upload hint.
        b.HasIndex(x => new { x.ClientId, x.DocumentSha256 }).HasFilter("[DocumentSha256] IS NOT NULL");
        // Platform-wide admin statistics: a range scan over the period without touching the base table.
        b.HasIndex(x => x.CreatedAt).IncludeProperties(x => new
        {
            x.ClientId, x.Type, x.Status, x.ReviewRequired, x.ExternalState, x.ExternalProviderId, x.ErrorCode, x.CreditsCharged, x.ProviderCreditsCharged, x.StartedAt, x.CompletedAt,
        }).HasDatabaseName("IX_Requests_Stats");
    }
}

internal sealed class VerificationDocumentConfiguration : IEntityTypeConfiguration<VerificationDocument>
{
    public void Configure(EntityTypeBuilder<VerificationDocument> b)
    {
        b.ToTable("Documents", "verification", t =>
        {
            t.HasCheckConstraint("CK_Documents_Size", "[SizeBytes] > 0 AND [PageCount] >= 0");
        });
        b.Property(x => x.ContentType).HasMaxLength(100).IsUnicode(false).IsRequired();
        b.Property(x => x.Extension).HasMaxLength(10).IsUnicode(false).IsRequired();
        b.Property(x => x.Sha256).HasColumnType("binary(32)").IsRequired();
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VerificationRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.RequestId }).IsUnique(); // one document per request in release 1
        b.HasIndex(x => new { x.ClientId, x.PurgeAfter }).HasFilter("[PurgedAt] IS NULL"); // per-tenant retention sweep
    }
}

internal sealed class VerificationDocumentBlobConfiguration : IEntityTypeConfiguration<VerificationDocumentBlob>
{
    public void Configure(EntityTypeBuilder<VerificationDocumentBlob> b)
    {
        // Ciphertext only (IClientEncryption); kept apart from Documents so list queries never touch LOB pages.
        b.ToTable("DocumentBlobs", "verification");
        b.HasKey(x => x.DocumentId);
        b.Property(x => x.DocumentId).ValueGeneratedNever();
        b.Property(x => x.Content).HasColumnType("varbinary(max)").IsRequired();
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VerificationDocument>().WithOne().HasForeignKey<VerificationDocumentBlob>(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.ClientId);
    }
}

internal sealed class VerificationResultConfiguration : IEntityTypeConfiguration<VerificationResult>
{
    public void Configure(EntityTypeBuilder<VerificationResult> b)
    {
        b.ToTable("Results", "verification");
        b.HasKey(x => x.RequestId);
        b.Property(x => x.RequestId).ValueGeneratedNever();
        b.Property(x => x.DataEnc).HasColumnType("varbinary(max)").IsRequired();
        b.Property(x => x.IdentifierMasked).HasMaxLength(32);
        b.Property(x => x.IdentifierHash).HasColumnType("binary(32)");
        b.Property(x => x.FieldMatchesJson).HasMaxLength(4000).IsRequired();
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VerificationRequest>().WithOne().HasForeignKey<VerificationResult>(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.IdentifierHash }).HasFilter("[IdentifierHash] IS NOT NULL"); // dedupe / velocity lookups
        b.HasIndex(x => new { x.ClientId, x.RetainUntil }); // per-tenant retention sweep
    }
}

internal sealed class VerificationCheckConfiguration : IEntityTypeConfiguration<VerificationCheck>
{
    public void Configure(EntityTypeBuilder<VerificationCheck> b)
    {
        b.ToTable("Checks", "verification");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Code).HasMaxLength(VerificationCheck.MaxCodeLength).IsUnicode(false).IsRequired();
        b.Property(x => x.Field).HasMaxLength(VerificationCheck.MaxFieldLength).IsUnicode(false);
        b.Property(x => x.Message).HasMaxLength(VerificationCheck.MaxMessageLength).IsRequired(); // template text with counts/indices only
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VerificationRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.RequestId, x.Ordinal });
    }
}

internal sealed class VerificationEventConfiguration : IEntityTypeConfiguration<VerificationEvent>
{
    public void Configure(EntityTypeBuilder<VerificationEvent> b)
    {
        // Append-only (IAppendOnly): INSTEAD OF UPDATE/DELETE trigger + DENY for the application login. No personal data.
        b.ToTable("Events", "verification");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Stage).HasMaxLength(30).IsUnicode(false);
        b.Property(x => x.NoteCode).HasMaxLength(60).IsUnicode(false);
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ClientId, x.RequestId, x.Id });
    }
}

internal sealed class VerificationDownloadGrantConfiguration : IEntityTypeConfiguration<VerificationDownloadGrant>
{
    public void Configure(EntityTypeBuilder<VerificationDownloadGrant> b)
    {
        b.ToTable("DownloadGrants", "verification");
        b.Property(x => x.Format).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.TokenHash).HasColumnType("binary(32)").IsRequired(); // SHA-256 of the token; the token itself is never stored
        b.HasOne<Client>().WithMany().HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VerificationRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TokenHash).IsUnique(); // a token maps to exactly one grant (256-bit random: collisions are not a tenancy risk)
        b.HasIndex(x => new { x.ClientId, x.RequestId });
        b.HasIndex(x => x.ExpiresAt); // purge of expired grants
    }
}

internal sealed class VerificationModuleSettingConfiguration : IEntityTypeConfiguration<VerificationModuleSetting>
{
    public void Configure(EntityTypeBuilder<VerificationModuleSetting> b)
    {
        // Global platform switch: no ClientId, deliberately outside tenant isolation (same as Plans / CostRules).
        b.ToTable("ModuleSettings", "verification");
        b.HasKey(x => x.ModuleKey);
        b.Property(x => x.ModuleKey).HasMaxLength(40).IsUnicode(false).ValueGeneratedNever();
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}
