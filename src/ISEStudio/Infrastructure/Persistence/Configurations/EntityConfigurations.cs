using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Infrastructure.Persistence.Configurations;

// =============================================================================
// Auth
// =============================================================================

/// <summary>EF Core mapping for the Python <c>User</c> SQLModel.</summary>
public sealed class UserEntityConfiguration : IEntityTypeConfiguration<UserEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("users");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.Property(x => x.Username).HasMaxLength(255).IsRequired();
        builder.HasIndex(x => x.Username).IsUnique().HasDatabaseName("ux_users_username");

        builder.Property(x => x.DisplayName).HasMaxLength(255);
        builder.Property(x => x.PasswordHash).HasMaxLength(255).IsRequired();
        builder.Property(x => x.IsAdmin).IsRequired().HasDefaultValue(false);
        builder.Property(x => x.Active).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.CreatedAt).IsRequired();
        // Keycloak sub for SSO users; unique across non-null values so
        // SsoUserSyncService's first-vs-relogin lookup is consistent and
        // a duplicate subject (concurrent first-login) surfaces as a
        // DbUpdateException the sync can recover from via re-query.
        builder.Property(x => x.SubjectId).HasMaxLength(255);
        builder.HasIndex(x => x.SubjectId)
            .IsUnique()
            .HasFilter("\"SubjectId\" IS NOT NULL")
            .HasDatabaseName("ux_users_subject_id");
    }
}

/// <summary>EF Core mapping for the Python <c>AuthSession</c> SQLModel.</summary>
public sealed class AuthSessionEntityConfiguration : IEntityTypeConfiguration<AuthSessionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuthSessionEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("authsession");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.Property(x => x.Token).HasMaxLength(255).IsRequired();
        builder.HasIndex(x => x.Token).IsUnique().HasDatabaseName("ux_authsession_token");

        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_authsession_user_id");

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>KSGrant</c> SQLModel.</summary>
public sealed class KSGrantEntityConfiguration : IEntityTypeConfiguration<KSGrantEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<KSGrantEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("ksgrant");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_ksgrant_knowledge_system_id");
        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_ksgrant_user_id");

        builder.Property(x => x.Role).HasMaxLength(32).IsRequired().HasDefaultValue("viewer");
        builder.Property(x => x.CreatedAt).IsRequired();

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>KnowledgePromptOverride</c> SQLModel.</summary>
public sealed class KnowledgePromptOverrideEntityConfiguration : IEntityTypeConfiguration<KnowledgePromptOverrideEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<KnowledgePromptOverrideEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("knowledgepromptoverride");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_kpo_knowledge_system_id");
        builder.HasIndex(x => x.PromptKey).HasDatabaseName("ix_kpo_prompt_key");

        builder.HasIndex(x => new { x.KnowledgeSystemId, x.PromptKey })
            .IsUnique()
            .HasDatabaseName("ux_kpo_knowledge_system_id_prompt_key");

        builder.Property(x => x.PromptKey).HasMaxLength(255).IsRequired();
        builder.Property(x => x.Content).IsRequired();
        builder.Property(x => x.UpdatedByName).HasMaxLength(255).IsRequired().HasDefaultValue("");
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UpdatedById).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>KnowledgeApiToken</c> SQLModel.</summary>
public sealed class KnowledgeApiTokenEntityConfiguration : IEntityTypeConfiguration<KnowledgeApiTokenEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<KnowledgeApiTokenEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("knowledgeapitoken");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_kat_knowledge_system_id");
        builder.HasIndex(x => x.Name).HasDatabaseName("ix_kat_name");

        builder.Property(x => x.Name).HasMaxLength(255).IsRequired();
        builder.Property(x => x.TokenPrefix).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        builder.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_kat_token_hash");

        builder.Property(x => x.SecretCiphertext);
        builder.Property(x => x.Scopes);

        builder.HasIndex(x => x.CreatedById).HasDatabaseName("ix_kat_created_by");
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ExpiresAt);
        builder.Property(x => x.LastUsedAt);
        builder.Property(x => x.RevokedAt);

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne(x => x.KnowledgeSystem).WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>McpUserToken</c> SQLModel.</summary>
public sealed class McpUserTokenEntityConfiguration : IEntityTypeConfiguration<McpUserTokenEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<McpUserTokenEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("mcpusertoken");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_mcp_knowledge_system_id");
        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_mcp_user_id");
        builder.HasIndex(x => x.Name).HasDatabaseName("ix_mcp_name");

        builder.Property(x => x.Name).HasMaxLength(255).IsRequired();
        builder.Property(x => x.TokenPrefix).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        builder.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_mcp_token_hash");

        builder.Property(x => x.Scopes);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.LastUsedAt);
        builder.Property(x => x.RevokedAt);

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne(x => x.KnowledgeSystem).WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

    }
}

// =============================================================================
// Workspace
// =============================================================================

/// <summary>EF Core mapping for the Python <c>KnowledgeSystem</c> SQLModel.</summary>
public sealed class KnowledgeSystemEntityConfiguration : IEntityTypeConfiguration<KnowledgeSystemEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<KnowledgeSystemEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("knowledgesystem");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.Property(x => x.PublicId).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.PublicId).IsUnique().HasDatabaseName("ux_ks_public_id");

        builder.Property(x => x.Name).HasMaxLength(255).IsRequired();
        builder.HasIndex(x => x.Name).HasDatabaseName("ix_ks_name");

        builder.Property(x => x.Description).IsRequired().HasDefaultValue("");
        builder.HasIndex(x => x.OwnerId).HasDatabaseName("ix_ks_owner_id");

        builder.Property(x => x.GraphIri).HasMaxLength(1024).IsRequired().HasDefaultValue("");
        builder.Property(x => x.BaseIri).HasMaxLength(1024).IsRequired().HasDefaultValue("");

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.Property(x => x.ClassCount).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.PropertyCount).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.AxiomCount).IsRequired().HasDefaultValue(0);

        builder.Property(x => x.LlmModel).HasMaxLength(255);
        builder.Property(x => x.LlmProviderId);
        builder.Property(x => x.EmbeddingProviderId);
        builder.Property(x => x.EmbeddingModel).HasMaxLength(255);

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProviderEntity>().WithMany().HasForeignKey(x => x.LlmProviderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProviderEntity>().WithMany().HasForeignKey(x => x.EmbeddingProviderId).OnDelete(DeleteBehavior.Restrict);

    }
}

public sealed class SourceEntityConfiguration : IEntityTypeConfiguration<SourceEntity>
{
    public void Configure(EntityTypeBuilder<SourceEntity> builder)
    {
        builder.ToTable("source", table =>
        {
            table.HasCheckConstraint("ck_source_kind",
                "kind IN ('folder','url','rss','custom','github_issues','jira_issues','s3','azure_blob','gcs','webdav','notion','api','statements','memory','upload')");
            table.HasCheckConstraint("ck_source_last_sync_status",
                "last_sync_status IN ('never','queued','running','ok','failed')");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.KnowledgeSystemId).HasColumnName("knowledge_system_id");
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        builder.Property(x => x.Config).HasColumnName("config").IsRequired();
        builder.Property(x => x.Icon).HasColumnName("icon").HasMaxLength(255);
        builder.Property(x => x.SyncIntervalMinutes).HasColumnName("sync_interval_minutes");
        builder.Property(x => x.SyncCron).HasColumnName("sync_cron");
        builder.Property(x => x.LastScheduledAt).HasColumnName("last_scheduled_at");
        builder.Property(x => x.LastSyncedAt).HasColumnName("last_synced_at");
        builder.Property(x => x.LastSyncStatus).HasColumnName("last_sync_status").HasMaxLength(32).IsRequired();
        builder.Property(x => x.LastSyncError).HasColumnName("last_sync_error");
        builder.Property(x => x.LastSyncAdded).HasColumnName("last_sync_added").IsRequired();
        builder.Property(x => x.IngestTokenCiphertext).HasColumnName("ingest_token_ciphertext");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_source_knowledge_system_id");
        builder.HasIndex(x => new { x.KnowledgeSystemId, x.Name }).IsUnique()
            .HasDatabaseName("ux_source_knowledge_system_name");
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceSyncJobEntityConfiguration : IEntityTypeConfiguration<SourceSyncJobEntity>
{
    public void Configure(EntityTypeBuilder<SourceSyncJobEntity> builder)
    {
        builder.ToTable("source_sync_job", table =>
            table.HasCheckConstraint("ck_source_sync_job_status",
                "status IN ('queued','running','ok','failed')"));
        builder.HasKey(job => job.Id);
        builder.Property(job => job.Id).HasColumnName("id");
        builder.Property(job => job.SourceId).HasColumnName("source_id").IsRequired();
        builder.Property(job => job.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        builder.Property(job => job.ActiveRunId).HasColumnName("active_run_id");
        builder.Property(job => job.LeaseUntil).HasColumnName("lease_until");
        builder.Property(job => job.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(job => job.StartedAt).HasColumnName("started_at");
        builder.Property(job => job.FinishedAt).HasColumnName("finished_at");
        builder.Property(job => job.Error).HasColumnName("error");
        builder.HasIndex(job => job.SourceId).IsUnique()
            .HasFilter("status IN ('queued','running')")
            .HasDatabaseName("ux_source_sync_job_active_source");
        builder.HasIndex(job => new { job.Status, job.CreatedAt })
            .HasDatabaseName("ix_source_sync_job_status_created");
        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(job => job.SourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SourceSyncRunEntityConfiguration : IEntityTypeConfiguration<SourceSyncRunEntity>
{
    public void Configure(EntityTypeBuilder<SourceSyncRunEntity> builder)
    {
        builder.ToTable("source_sync_run", table =>
            table.HasCheckConstraint("ck_source_sync_run_status",
                "status IN ('running','ok','failed')"));
        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).HasColumnName("id");
        builder.Property(run => run.SourceId).HasColumnName("source_id").IsRequired();
        builder.Property(run => run.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        builder.Property(run => run.StartedAt).HasColumnName("started_at").IsRequired();
        builder.Property(run => run.FinishedAt).HasColumnName("finished_at");
        builder.Property(run => run.AddedCount).HasColumnName("added_count").IsRequired();
        builder.Property(run => run.UpdatedCount).HasColumnName("updated_count").IsRequired();
        builder.Property(run => run.Error).HasColumnName("error");
        builder.HasIndex(run => new { run.SourceId, run.StartedAt, run.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_source_sync_run_source_started_id");
        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(run => run.SourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SourceDocumentBindingEntityConfiguration
    : IEntityTypeConfiguration<SourceDocumentBindingEntity>
{
    public void Configure(EntityTypeBuilder<SourceDocumentBindingEntity> builder)
    {
        builder.ToTable("source_document_binding");
        builder.HasKey(binding => binding.Id);
        builder.Property(binding => binding.Id).HasColumnName("id");
        builder.Property(binding => binding.SourceId).HasColumnName("source_id").IsRequired();
        builder.Property(binding => binding.ExternalKey).HasColumnName("external_key")
            .HasMaxLength(1024).IsRequired();
        builder.Property(binding => binding.DocumentId).HasColumnName("document_id").IsRequired();
        builder.Property(binding => binding.MissingSince).HasColumnName("missing_since");
        builder.HasIndex(binding => new { binding.SourceId, binding.ExternalKey }).IsUnique()
            .HasDatabaseName("ux_source_document_binding_source_external_key");
        builder.HasIndex(binding => binding.DocumentId)
            .HasDatabaseName("ix_source_document_binding_document_id");
        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(binding => binding.SourceId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DocumentEntity>().WithMany().HasForeignKey(binding => binding.DocumentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>EF Core mapping for the Python <c>Document</c> SQLModel.</summary>
public sealed class DocumentEntityConfiguration : IEntityTypeConfiguration<DocumentEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("document");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_document_knowledge_system_id");

        builder.Property(x => x.SourceId).HasColumnName("source_id");
        builder.Property(x => x.ExternalKey).HasColumnName("external_key").HasMaxLength(1024);
        builder.Property(x => x.MissingSince).HasColumnName("missing_since");
        builder.Property(x => x.IsManualUpload).HasColumnName("is_manual_upload").IsRequired().HasDefaultValue(true);
        builder.HasIndex(x => new { x.SourceId, x.ExternalKey }).IsUnique()
            .HasDatabaseName("ux_document_source_external_key")
            .HasFilter("external_key IS NOT NULL AND source_id IS NOT NULL");
        builder.HasOne<SourceEntity>().WithMany().HasForeignKey(x => x.SourceId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.Sha256).HasDatabaseName("ix_document_sha256");

        builder.HasIndex(x => new { x.KnowledgeSystemId, x.Sha256 })
            .IsUnique()
            .HasDatabaseName("ux_document_knowledge_system_id_sha256");

        builder.Property(x => x.OriginalFilename).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.Folder).HasMaxLength(1024).IsRequired().HasDefaultValue("/");
        builder.HasIndex(x => x.Folder).HasDatabaseName("ix_document_folder");

        builder.Property(x => x.Ext).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => x.Ext).HasDatabaseName("ix_document_ext");

        builder.Property(x => x.Mime).HasMaxLength(255);
        builder.Property(x => x.SizeBytes).IsRequired().HasDefaultValue(0L);
        builder.Property(x => x.StoragePath).HasMaxLength(1024).IsRequired().HasDefaultValue("");
        builder.Property(x => x.UploadedAt).IsRequired();

        builder.Property(x => x.ParseStatus).HasMaxLength(32).IsRequired().HasDefaultValue("pending");
        builder.HasIndex(x => x.ParseStatus).HasDatabaseName("ix_document_parse_status");

        builder.Property(x => x.ParserBackend).HasMaxLength(64);
        builder.Property(x => x.ParserVersion).HasMaxLength(128);
        builder.Property(x => x.ParseError);
        builder.Property(x => x.TextCharCount);
        builder.Property(x => x.ChunkCount).IsRequired().HasDefaultValue(0);

        builder.Property(x => x.TboxExtractedAt);
        builder.Property(x => x.AboxExtractedAt);

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>Chunk</c> SQLModel.</summary>
public sealed class ChunkEntityConfiguration : IEntityTypeConfiguration<ChunkEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChunkEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("chunk");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.DocumentId).HasDatabaseName("ix_chunk_document_id");

        builder.Property(x => x.Idx).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.Text).IsRequired();
        builder.Property(x => x.CharStart).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.CharEnd).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.TokenEstimate).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.CreatedAt).IsRequired();

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<DocumentEntity>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>Provider</c> SQLModel.</summary>
public sealed class ProviderEntityConfiguration : IEntityTypeConfiguration<ProviderEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProviderEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("provider");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.Property(x => x.Name).HasMaxLength(255).IsRequired();
        builder.HasIndex(x => x.Name).HasDatabaseName("ix_provider_name");

        builder.Property(x => x.BaseUrl).HasMaxLength(2048).IsRequired().HasDefaultValue("");
        builder.Property(x => x.ApiKey).HasMaxLength(2048).IsRequired().HasDefaultValue("");
        builder.Property(x => x.Model).HasMaxLength(255).IsRequired().HasDefaultValue("");
        builder.Property(x => x.Kind).HasMaxLength(32).IsRequired().HasDefaultValue("llm");
        builder.Property(x => x.ConcurrencyLimit).IsRequired().HasDefaultValue(10);
        builder.Property(x => x.LastTestOk);
        builder.Property(x => x.LastTestedAt);
        builder.Property(x => x.CreatedAt).IsRequired();
    }
}

/// <summary>EF Core mapping for the Python <c>SystemConfig</c> SQLModel (singleton).</summary>
public sealed class SystemConfigEntityConfiguration : IEntityTypeConfiguration<SystemConfigEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SystemConfigEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("systemconfig");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.IsSingleton)
            .IsRequired()
            .HasDefaultValue(false);

        builder.HasIndex(x => x.IsSingleton)
            .HasFilter("\"IsSingleton\" = TRUE")
            .IsUnique()
            .HasDatabaseName("ux_systemconfig_singleton");

        builder.Property(x => x.ExtractModel).HasMaxLength(255);
        builder.Property(x => x.EmbeddingModel).HasMaxLength(255);
        builder.Property(x => x.LlmProviderId);
        builder.Property(x => x.EmbeddingProviderId);
        builder.Property(x => x.ExtractionConcurrency);
        builder.Property(x => x.BaseUrl).HasMaxLength(2048);
        builder.Property(x => x.ApiKey).HasMaxLength(2048);
        builder.Property(x => x.UpdatedAt).IsRequired();

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<ProviderEntity>().WithMany().HasForeignKey(x => x.LlmProviderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProviderEntity>().WithMany().HasForeignKey(x => x.EmbeddingProviderId).OnDelete(DeleteBehavior.Restrict);

    }
}

// =============================================================================
// Provenance & jobs
// =============================================================================

/// <summary>EF Core mapping for the Python <c>ExtractionJob</c> SQLModel.</summary>
public sealed class ExtractionJobEntityConfiguration : IEntityTypeConfiguration<ExtractionJobEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ExtractionJobEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("extractionjob");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_extractionjob_knowledge_system_id");

        builder.Property(x => x.Kind).HasMaxLength(32).IsRequired().HasDefaultValue("tbox");
        builder.HasIndex(x => x.Kind).HasDatabaseName("ix_extractionjob_kind");

        builder.Property(x => x.Status).HasMaxLength(32).IsRequired().HasDefaultValue("pending");
        builder.HasIndex(x => x.Status).HasDatabaseName("ix_extractionjob_status");

        builder.Property(x => x.Model).HasMaxLength(255).IsRequired().HasDefaultValue("");
        builder.Property(x => x.PromptSnapshot).HasConversion(JsonStringValueConverter.Instance);
        builder.Property(x => x.ChunkIds);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.FinishedAt);
        builder.Property(x => x.Log).IsRequired().HasDefaultValue("");
        builder.Property(x => x.Error);

        builder.Property(x => x.TotalChunks).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.ProcessedChunks).IsRequired().HasDefaultValue(0);

        builder.Property(x => x.ClassesAdded).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.PropertiesAdded).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.AxiomsAdded).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.IndividualsAdded).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.AssertionsAdded).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.PendingAdded).IsRequired().HasDefaultValue(0);

        builder.Property(x => x.UnknownClasses).HasConversion(JsonStringValueConverter.Instance);

        builder.Property(x => x.Phase).HasMaxLength(32).IsRequired().HasDefaultValue("");
        builder.HasIndex(x => x.Phase).HasDatabaseName("ix_extractionjob_phase");

        builder.Property(x => x.TermsAdded).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.TermsMapped).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.TerminologyProposals).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.TerminologyError);

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>AxiomProvenance</c> SQLModel.</summary>
public sealed class AxiomProvenanceEntityConfiguration : IEntityTypeConfiguration<AxiomProvenanceEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AxiomProvenanceEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("axiomprovenance");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_axiomprov_knowledge_system_id");
        builder.Property(x => x.AxiomKey).HasMaxLength(1024).IsRequired();
        builder.HasIndex(x => x.AxiomKey).HasDatabaseName("ix_axiomprov_axiom_key");

        builder.HasIndex(x => x.ChunkId).HasDatabaseName("ix_axiomprov_chunk_id");
        builder.HasIndex(x => x.JobId).HasDatabaseName("ix_axiomprov_job_id");
        builder.HasIndex(x => x.AuditEventId).HasDatabaseName("ix_axiomprov_audit_event_id");

        builder.Property(x => x.Method).HasMaxLength(64).IsRequired().HasDefaultValue("extraction");
        builder.Property(x => x.ActorName).HasMaxLength(255).IsRequired().HasDefaultValue("");

        builder.Property(x => x.ReviewRecord).HasConversion(JsonStringValueConverter.Instance);
        builder.Property(x => x.CreatedAt).IsRequired();

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ChunkEntity>().WithMany().HasForeignKey(x => x.ChunkId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExtractionJobEntity>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AuditEventEntity>().WithMany().HasForeignKey(x => x.AuditEventId).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>AboxProvenance</c> SQLModel.</summary>
public sealed class AboxProvenanceEntityConfiguration : IEntityTypeConfiguration<AboxProvenanceEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AboxProvenanceEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("aboxprovenance");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_aboxprov_knowledge_system_id");
        builder.Property(x => x.FactKey).HasMaxLength(1024).IsRequired();
        builder.HasIndex(x => x.FactKey).HasDatabaseName("ix_aboxprov_fact_key");

        builder.HasIndex(x => x.ChunkId).HasDatabaseName("ix_aboxprov_chunk_id");
        builder.HasIndex(x => x.JobId).HasDatabaseName("ix_aboxprov_job_id");
        builder.HasIndex(x => x.AuditEventId).HasDatabaseName("ix_aboxprov_audit_event_id");

        builder.Property(x => x.Method).HasMaxLength(64).IsRequired().HasDefaultValue("extraction");
        builder.Property(x => x.ActorName).HasMaxLength(255).IsRequired().HasDefaultValue("");

        builder.Property(x => x.ReviewRecord).HasConversion(JsonStringValueConverter.Instance);
        builder.Property(x => x.CreatedAt).IsRequired();

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ChunkEntity>().WithMany().HasForeignKey(x => x.ChunkId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExtractionJobEntity>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AuditEventEntity>().WithMany().HasForeignKey(x => x.AuditEventId).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>AuditEvent</c> SQLModel.</summary>
public sealed class AuditEventEntityConfiguration : IEntityTypeConfiguration<AuditEventEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditEventEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("auditevent");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_auditevent_knowledge_system_id");
        builder.HasIndex(x => x.ActorId).HasDatabaseName("ix_auditevent_actor_id");

        builder.Property(x => x.ActorName).HasMaxLength(255).IsRequired().HasDefaultValue("");
        builder.Property(x => x.Action).HasMaxLength(128).IsRequired();
        builder.HasIndex(x => x.Action).HasDatabaseName("ix_auditevent_action");

        builder.Property(x => x.Summary).IsRequired().HasDefaultValue("");
        builder.Property(x => x.Detail).HasConversion(JsonStringValueConverter.Instance);
        builder.Property(x => x.Graph).HasMaxLength(1024);
        builder.Property(x => x.GroupId).HasMaxLength(128);
        builder.HasIndex(x => x.GroupId).HasDatabaseName("ix_auditevent_group_id");

        builder.Property(x => x.Added);
        builder.Property(x => x.Removed);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_auditevent_created_at");

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);

    }
}

// =============================================================================
// Releases, deployments, exports, conflicts & learned queues
// =============================================================================

/// <summary>EF Core mapping for the Python <c>OntologyRelease</c> SQLModel.</summary>
public sealed class OntologyReleaseEntityConfiguration : IEntityTypeConfiguration<OntologyReleaseEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OntologyReleaseEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("ontologyrelease");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_release_knowledge_system_id");

        builder.Property(x => x.Version).HasMaxLength(128).IsRequired();
        builder.HasIndex(x => x.Version).HasDatabaseName("ix_release_version");

        // Unique only when published — multiple in-flight drafts may share a
        // version label until publish materialises a unique final version
        // (Draft rows are filtered out of the unique constraint at the DB
        // level via the PostgreSQL partial index below.)
        builder.HasIndex(x => new { x.KnowledgeSystemId, x.Version })
            .IsUnique()
            .HasFilter("status <> 'draft'")
            .HasDatabaseName("ux_release_knowledge_system_id_version");

        builder.Property(x => x.Status).HasMaxLength(32).IsRequired().HasDefaultValue("draft");
        builder.HasIndex(x => x.Status).HasDatabaseName("ix_release_status");

        builder.Property(x => x.Title).HasMaxLength(255).IsRequired().HasDefaultValue("");
        builder.Property(x => x.Notes).IsRequired().HasDefaultValue("");
        builder.Property(x => x.SnapshotDir).HasMaxLength(1024).IsRequired().HasDefaultValue("");

        builder.Property(x => x.Manifest).HasConversion(JsonStringValueConverter.Instance);

        builder.Property(x => x.CreatedByName).HasMaxLength(255).IsRequired().HasDefaultValue("");
        builder.Property(x => x.ReviewedByName).HasMaxLength(255).IsRequired().HasDefaultValue("");
        builder.Property(x => x.PublishedByName).HasMaxLength(255).IsRequired().HasDefaultValue("");

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_release_created_at");
        builder.Property(x => x.ReviewedAt);
        builder.Property(x => x.PublishedAt);

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ReviewedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.PublishedById).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>ReleaseDeployment</c> SQLModel.</summary>
public sealed class ReleaseDeploymentEntityConfiguration : IEntityTypeConfiguration<ReleaseDeploymentEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ReleaseDeploymentEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("releasedeployment");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_deployment_knowledge_system_id");
        builder.HasIndex(x => x.ReleaseId).IsUnique().HasDatabaseName("ux_deployment_release_id");

        builder.Property(x => x.Status).HasMaxLength(32).IsRequired().HasDefaultValue("provisioning");
        builder.HasIndex(x => x.Status).HasDatabaseName("ix_deployment_status");

        builder.Property(x => x.TboxGraphIri).HasMaxLength(1024).IsRequired().HasDefaultValue("");
        builder.Property(x => x.VocabularyGraphIri).HasMaxLength(1024).IsRequired().HasDefaultValue("");
        builder.Property(x => x.AboxGraphIri).HasMaxLength(1024).IsRequired().HasDefaultValue("");

        builder.Property(x => x.StatementCount).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.ProvenanceCount).IsRequired().HasDefaultValue(0);

        builder.Property(x => x.Error);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_deployment_created_at");
        builder.Property(x => x.ActivatedAt);
        builder.Property(x => x.StoppedAt);

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OntologyReleaseEntity>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>ReleaseStatementProvenance</c> SQLModel.</summary>
public sealed class ReleaseStatementProvenanceEntityConfiguration : IEntityTypeConfiguration<ReleaseStatementProvenanceEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ReleaseStatementProvenanceEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("releasestatementprovenance");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_rsp_knowledge_system_id");
        builder.HasIndex(x => x.ReleaseId).HasDatabaseName("ix_rsp_release_id");

        builder.Property(x => x.Layer).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => x.Layer).HasDatabaseName("ix_rsp_layer");

        builder.Property(x => x.StatementKey).IsRequired();
        builder.HasIndex(x => x.StatementKey).HasDatabaseName("ix_rsp_statement_key");

        builder.Property(x => x.Payload).HasConversion(JsonStringValueConverter.Instance);

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OntologyReleaseEntity>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>ExportJob</c> SQLModel.</summary>
public sealed class ExportJobEntityConfiguration : IEntityTypeConfiguration<ExportJobEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ExportJobEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("exportjob");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_exportjob_knowledge_system_id");
        builder.HasIndex(x => x.ReleaseId).HasDatabaseName("ix_exportjob_release_id");

        builder.Property(x => x.Layer).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => x.Layer).HasDatabaseName("ix_exportjob_layer");

        builder.Property(x => x.Format).HasMaxLength(64).IsRequired().HasDefaultValue("nquads");
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired().HasDefaultValue("pending");
        builder.HasIndex(x => x.Status).HasDatabaseName("ix_exportjob_status");

        builder.Property(x => x.ShardSize).IsRequired().HasDefaultValue(100_000);
        builder.Property(x => x.ProcessedStatements).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.TotalStatements).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.OutputDir).HasMaxLength(1024).IsRequired().HasDefaultValue("");

        builder.Property(x => x.Files).HasConversion(JsonStringValueConverter.Instance);
        builder.Property(x => x.Error);

        builder.Property(x => x.CreatedByName).HasMaxLength(255).IsRequired().HasDefaultValue("");

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_exportjob_created_at");
        builder.Property(x => x.StartedAt);
        builder.Property(x => x.FinishedAt);

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OntologyReleaseEntity>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>Conflict</c> SQLModel.</summary>
public sealed class ConflictEntityConfiguration : IEntityTypeConfiguration<ConflictEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ConflictEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("conflict");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_conflict_knowledge_system_id");

        builder.Property(x => x.Signature).HasMaxLength(1024).IsRequired();
        builder.HasIndex(x => x.Signature).HasDatabaseName("ix_conflict_signature");

        builder.Property(x => x.Ctype).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.Ctype).HasDatabaseName("ix_conflict_ctype");

        builder.Property(x => x.Severity).HasMaxLength(32).IsRequired().HasDefaultValue("error");

        builder.Property(x => x.Status).HasMaxLength(32).IsRequired().HasDefaultValue("open");
        builder.HasIndex(x => x.Status).HasDatabaseName("ix_conflict_status");

        builder.Property(x => x.Title).HasMaxLength(255).IsRequired().HasDefaultValue("");
        builder.Property(x => x.Detail).IsRequired().HasDefaultValue("");
        builder.Property(x => x.Payload).HasConversion(JsonStringValueConverter.Instance);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.ResolvedAt);
        builder.Property(x => x.Resolution).HasMaxLength(64);

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>EntityResolution</c> SQLModel.</summary>
public sealed class EntityResolutionEntityConfiguration : IEntityTypeConfiguration<EntityResolutionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<EntityResolutionEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("entityresolution");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_er_knowledge_system_id");

        builder.Property(x => x.SurfaceForm).HasMaxLength(1024).IsRequired();
        builder.HasIndex(x => x.SurfaceForm).HasDatabaseName("ix_er_surface_form");

        builder.Property(x => x.ClassIri).HasMaxLength(1024);
        builder.HasIndex(x => x.ClassIri).HasDatabaseName("ix_er_class_iri");

        builder.Property(x => x.Status).HasMaxLength(32).IsRequired().HasDefaultValue("pending");
        builder.HasIndex(x => x.Status).HasDatabaseName("ix_er_status");

        builder.Property(x => x.IndividualIri).HasMaxLength(1024);
        builder.Property(x => x.Confidence);
        builder.Property(x => x.ResolvedBy).HasMaxLength(255);

        builder.HasIndex(x => x.SourceChunkId).HasDatabaseName("ix_er_source_chunk_id");
        builder.Property(x => x.Context).HasConversion(JsonStringValueConverter.Instance);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_er_created_at");
        builder.Property(x => x.ResolvedAt);

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ChunkEntity>().WithMany().HasForeignKey(x => x.SourceChunkId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>EF Core mapping for the Python <c>TermProposal</c> SQLModel.</summary>
public sealed class TermProposalEntityConfiguration : IEntityTypeConfiguration<TermProposalEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TermProposalEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("termproposal");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_tp_knowledge_system_id");
        builder.Property(x => x.Signature).HasMaxLength(1024).IsRequired();
        builder.HasIndex(x => x.Signature).HasDatabaseName("ix_tp_signature");

        builder.Property(x => x.Action).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => x.Action).HasDatabaseName("ix_tp_action");

        builder.Property(x => x.Term).HasMaxLength(1024).IsRequired().HasDefaultValue("");
        builder.HasIndex(x => x.Term).HasDatabaseName("ix_tp_term");

        builder.Property(x => x.TargetIri).HasMaxLength(1024);
        builder.HasIndex(x => x.TargetIri).HasDatabaseName("ix_tp_target_iri");

        builder.Property(x => x.Status).HasMaxLength(32).IsRequired().HasDefaultValue("pending");
        builder.HasIndex(x => x.Status).HasDatabaseName("ix_tp_status");

        builder.Property(x => x.Payload).HasConversion(JsonStringValueConverter.Instance);
        builder.Property(x => x.Confidence);
        builder.Property(x => x.Reason);

        builder.Property(x => x.Evidence).HasConversion(JsonStringValueConverter.Instance);
        builder.Property(x => x.SourceChunkIds).HasConversion(JsonStringValueConverter.Instance);

        builder.HasIndex(x => x.ExtractionJobId).HasDatabaseName("ix_tp_extraction_job_id");

        builder.Property(x => x.ProposedBy).HasMaxLength(255).IsRequired().HasDefaultValue("terminology-agent");
        builder.Property(x => x.ResolvedBy).HasMaxLength(255);
        builder.Property(x => x.ResolutionNote);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_tp_created_at");
        builder.Property(x => x.ResolvedAt);

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExtractionJobEntity>().WithMany().HasForeignKey(x => x.ExtractionJobId).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>TboxReconciliation</c> SQLModel.</summary>
public sealed class TboxReconciliationEntityConfiguration : IEntityTypeConfiguration<TboxReconciliationEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TboxReconciliationEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("tboxreconciliation");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_tboxr_knowledge_system_id");

        builder.Property(x => x.Slot).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => x.Slot).HasDatabaseName("ix_tboxr_slot");

        builder.Property(x => x.PropertyLabel).HasMaxLength(255).IsRequired();
        builder.HasIndex(x => x.PropertyLabel).HasDatabaseName("ix_tboxr_property_label");

        builder.Property(x => x.PropertyIri).HasMaxLength(1024);
        builder.Property(x => x.Candidates).HasConversion(JsonStringValueConverter.Instance);
        builder.Property(x => x.Choice).HasMaxLength(64).IsRequired().HasDefaultValue("");
        builder.Property(x => x.ChosenLabel).HasMaxLength(255);
        builder.Property(x => x.Reason);

        builder.Property(x => x.ResolvedBy).HasMaxLength(255);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_tboxr_created_at");

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);

    }
}

/// <summary>EF Core mapping for the Python <c>ValidationDecision</c> SQLModel.</summary>
public sealed class ValidationDecisionEntityConfiguration : IEntityTypeConfiguration<ValidationDecisionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ValidationDecisionEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("validationdecision");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");


        builder.HasIndex(x => x.KnowledgeSystemId).HasDatabaseName("ix_vd_knowledge_system_id");

        builder.Property(x => x.PropertyLabel).HasMaxLength(255).IsRequired();
        builder.HasIndex(x => x.PropertyLabel).HasDatabaseName("ix_vd_property_label");

        builder.Property(x => x.PropertyIri).HasMaxLength(1024);
        builder.HasIndex(x => x.PropertyIri).HasDatabaseName("ix_vd_property_iri");

        builder.Property(x => x.XsdType).HasMaxLength(64);
        builder.Property(x => x.Action).HasMaxLength(64).IsRequired().HasDefaultValue("");
        builder.Property(x => x.Reason);
        builder.Property(x => x.ResolvedBy).HasMaxLength(255);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_vd_created_at");

        // Foreign keys (Python foreign_key= parity)
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId).OnDelete(DeleteBehavior.Restrict);

    }
}

public sealed class DocumentParseJobEntityConfiguration : IEntityTypeConfiguration<DocumentParseJobEntity>
{
    public void Configure(EntityTypeBuilder<DocumentParseJobEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("document_parse_job", table => table.HasCheckConstraint(
            "ck_document_parse_job_status", "status IN ('pending', 'running', 'completed', 'failed')"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.KnowledgeSystemId).HasColumnName("knowledge_system_id");
        builder.Property(x => x.DocumentId).HasColumnName("document_id");
        builder.Property(x => x.SourceId).HasColumnName("source_id");
        builder.Property(x => x.DocumentFileVersionId).HasColumnName("document_file_version_id");
        builder.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        builder.Property(x => x.Error).HasColumnName("error");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.FinishedAt).HasColumnName("finished_at");
        builder.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_document_parse_job_pending");
        builder.HasIndex(x => x.DocumentId).HasDatabaseName("ix_document_parse_job_document");
        builder.HasIndex(x => x.SourceId).HasDatabaseName("ix_document_parse_job_source");
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DocumentEntity>().WithMany().HasForeignKey(x => x.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DocumentFileVersionEntity>().WithMany().HasForeignKey(x => x.DocumentFileVersionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DocumentFileVersionEntityConfiguration : IEntityTypeConfiguration<DocumentFileVersionEntity>
{
    public void Configure(EntityTypeBuilder<DocumentFileVersionEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("document_file_version", table =>
        {
            table.HasCheckConstraint("ck_document_file_version_version", "version > 0");
            table.HasCheckConstraint("ck_document_file_version_size", "size_bytes >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DocumentId).HasColumnName("document_id");
        builder.Property(x => x.Version).HasColumnName("version");
        builder.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
        builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        builder.Property(x => x.DocTime).HasColumnName("doc_time");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.HasIndex(x => new { x.DocumentId, x.Version }).IsUnique()
            .HasDatabaseName("ux_document_file_version_document_version");
        builder.HasOne<DocumentEntity>().WithMany().HasForeignKey(x => x.DocumentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DocumentFileVersionSnapshotEntityConfiguration : IEntityTypeConfiguration<DocumentFileVersionSnapshotEntity>
{
    public void Configure(EntityTypeBuilder<DocumentFileVersionSnapshotEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("document_file_version_snapshot");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DocumentFileVersionId).HasColumnName("document_file_version_id");
        builder.Property(x => x.DocumentVersionId).HasColumnName("document_version_id");
        builder.HasIndex(x => new { x.DocumentFileVersionId, x.DocumentVersionId }).IsUnique()
            .HasDatabaseName("ux_document_file_version_snapshot_pair");
        builder.HasOne<DocumentFileVersionEntity>().WithMany().HasForeignKey(x => x.DocumentFileVersionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DocumentVersionEntity>().WithMany().HasForeignKey(x => x.DocumentVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DocumentVersionEntityConfiguration : IEntityTypeConfiguration<DocumentVersionEntity>
{
    public void Configure(EntityTypeBuilder<DocumentVersionEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.KnowledgeSystemId).HasColumnName("knowledge_system_id");
        builder.Property(x => x.DocumentId).HasColumnName("document_id");
        builder.Property(x => x.ContentSha256).HasColumnName("content_sha256").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ChunkCount).HasColumnName("chunk_count").IsRequired().HasDefaultValue(0);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.KnowledgeSystemId, x.DocumentId, x.ContentSha256 })
            .IsUnique()
            .HasDatabaseName("ux_document_version_knowledge_system_document_sha256");
        builder.HasIndex(x => new { x.KnowledgeSystemId, x.DocumentId })
            .HasDatabaseName("ix_document_version_knowledge_system_document");
        builder.HasOne<KnowledgeSystemEntity>().WithMany().HasForeignKey(x => x.KnowledgeSystemId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DocumentEntity>().WithMany().HasForeignKey(x => x.DocumentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DocumentVersionChunkEntityConfiguration : IEntityTypeConfiguration<DocumentVersionChunkEntity>
{
    public void Configure(EntityTypeBuilder<DocumentVersionChunkEntity> builder)
    {
        builder.UseTpcMappingStrategy();
        builder.ToTable("document_version_chunk");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DocumentVersionId).HasColumnName("document_version_id");
        builder.Property(x => x.Idx).HasColumnName("idx").IsRequired();
        builder.Property(x => x.Text).HasColumnName("text").IsRequired();
        builder.Property(x => x.CharStart).HasColumnName("char_start").IsRequired();
        builder.Property(x => x.CharEnd).HasColumnName("char_end").IsRequired();
        builder.Property(x => x.TokenEstimate).HasColumnName("token_estimate").IsRequired();
        builder.HasIndex(x => new { x.DocumentVersionId, x.Idx })
            .IsUnique()
            .HasDatabaseName("ux_document_version_chunk_version_idx");
        builder.HasOne<DocumentVersionEntity>().WithMany().HasForeignKey(x => x.DocumentVersionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}