using Microsoft.EntityFrameworkCore;
using ISEStudio.Infrastructure.Persistence.Configurations;
using ISEStudio.Infrastructure.Persistence.Entities;

namespace ISEStudio.Infrastructure.Persistence;

/// <summary>
/// EF Core <see cref="DbContext"/> for ISEStudio's relational metadata store.
/// Maps the 24 tables that mirror the Python backend's SQLModel schema, with
/// <see cref="LegacyAddressableEntity.Id"/> (Guid) as the primary key and
/// <see cref="LegacyAddressableEntity.LegacyId"/> (long) as a unique
/// compatibility column for every business entity.
/// </summary>
/// <remarks>
/// <para>Mapping strategy: Table-Per-Concrete-Type (TPC). Every entity owns
/// its own physical table; no discriminator column is needed because each
/// entity has a discrete identity in the Python contract.</para>
/// </remarks>
public sealed class ISEStudioDbContext : DbContext
{
    /// <summary>Default constructor used by design-time tooling (migrations, scaffolding).</summary>
    public ISEStudioDbContext()
    {
    }

    /// <summary>Constructor used at runtime (DI) and by tests.</summary>
    /// <param name="options">EF Core options pre-configured with the desired provider.</param>
    public ISEStudioDbContext(DbContextOptions<ISEStudioDbContext> options)
        : base(options)
    {
    }

    // ---------------------------------------------------------------------
    // Auth
    // ---------------------------------------------------------------------

    /// <summary>Users (login + admin status).</summary>
    public DbSet<UserEntity> Users => Set<UserEntity>();

    /// <summary>Opaque-token server-side sessions.</summary>
    public DbSet<AuthSessionEntity> AuthSessions => Set<AuthSessionEntity>();

    /// <summary>Per-knowledge-system access grants.</summary>
    public DbSet<KSGrantEntity> KSGrants => Set<KSGrantEntity>();

    /// <summary>Per-knowledge-system prompt overrides.</summary>
    public DbSet<KnowledgePromptOverrideEntity> KnowledgePromptOverrides => Set<KnowledgePromptOverrideEntity>();

    /// <summary>Machine credentials scoped to one knowledge system.</summary>
    public DbSet<KnowledgeApiTokenEntity> KnowledgeApiTokens => Set<KnowledgeApiTokenEntity>();

    /// <summary>User credentials for the MCP transport.</summary>
    public DbSet<McpUserTokenEntity> McpUserTokens => Set<McpUserTokenEntity>();

    // ---------------------------------------------------------------------
    // Workspace
    // ---------------------------------------------------------------------

    /// <summary>Named ontology graphs (1 KS = 1 Oxigraph graph).</summary>
    public DbSet<KnowledgeSystemEntity> KnowledgeSystems => Set<KnowledgeSystemEntity>();

    /// <summary>Uploaded source files, per-KS dedup on (KS, Sha256).</summary>
    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();

    /// <summary>Contiguous text slices of parsed documents.</summary>
    public DbSet<ChunkEntity> Chunks => Set<ChunkEntity>();

    /// <summary>Model endpoint entries (LLM or embedding).</summary>
    public DbSet<ProviderEntity> Providers => Set<ProviderEntity>();

    /// <summary>Singleton runtime configuration (LegacyId == 1).</summary>
    public DbSet<SystemConfigEntity> SystemConfigs => Set<SystemConfigEntity>();

    // ---------------------------------------------------------------------
    // Provenance & jobs
    // ---------------------------------------------------------------------

    /// <summary>One row per TBox/ABox extraction run.</summary>
    public DbSet<ExtractionJobEntity> ExtractionJobs => Set<ExtractionJobEntity>();

    /// <summary>Axiom → chunk/job provenance.</summary>
    public DbSet<AxiomProvenanceEntity> AxiomProvenances => Set<AxiomProvenanceEntity>();

    /// <summary>ABox fact → chunk/job provenance (multi-source by design).</summary>
    public DbSet<AboxProvenanceEntity> AboxProvenances => Set<AboxProvenanceEntity>();

    /// <summary>Append-only change log with optional rollback payloads.</summary>
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    // ---------------------------------------------------------------------
    // Releases, deployments, exports, conflicts & learned queues
    // ---------------------------------------------------------------------

    /// <summary>Immutable snapshot of the three governed layers for a KS.</summary>
    public DbSet<OntologyReleaseEntity> OntologyReleases => Set<OntologyReleaseEntity>();

    /// <summary>Queryable projection of one published release.</summary>
    public DbSet<ReleaseDeploymentEntity> ReleaseDeployments => Set<ReleaseDeploymentEntity>();

    /// <summary>Release-fixed provenance index.</summary>
    public DbSet<ReleaseStatementProvenanceEntity> ReleaseStatementProvenances => Set<ReleaseStatementProvenanceEntity>();

    /// <summary>Asynchronous stream-written export jobs.</summary>
    public DbSet<ExportJobEntity> ExportJobs => Set<ExportJobEntity>();

    /// <summary>Detected ontology conflicts awaiting user resolution.</summary>
    public DbSet<ConflictEntity> Conflicts => Set<ConflictEntity>();

    /// <summary>Learned ABox entity-resolution memory.</summary>
    public DbSet<EntityResolutionEntity> EntityResolutions => Set<EntityResolutionEntity>();

    /// <summary>Human-in-the-loop terminology governance proposals.</summary>
    public DbSet<TermProposalEntity> TermProposals => Set<TermProposalEntity>();

    /// <summary>Learned TBox domain/range reconciliation memory.</summary>
    public DbSet<TboxReconciliationEntity> TboxReconciliations => Set<TboxReconciliationEntity>();

    /// <summary>Learned datatype-violation fix memory.</summary>
    public DbSet<ValidationDecisionEntity> ValidationDecisions => Set<ValidationDecisionEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply every IEntityTypeConfiguration<> registered in the
        // Configurations namespace. This keeps the DbContext file free of
        // per-entity Fluent-API noise and keeps the mapping discoverable.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(Configurations.UserEntityConfiguration).Assembly);

        // The unit tests run on SQLite which doesn't speak jsonb / bytea /
        // timestamptz. The configurations therefore don't pin those column
        // types. Production targets Npgsql — when that's the case, upgrade
        // the JSON columns to jsonb and the audit blob columns to bytea so
        // they get full PostgreSQL semantics (indexing, query operators).
        if (Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            ApplyPostgresColumnTypes(modelBuilder);
        }
    }

    /// <summary>
    /// Pin PostgreSQL-specific column types for the JSON and binary columns.
    /// Invoked only when the configured provider is Npgsql — SQLite sees the
    /// same model with its default TEXT/BLOB storage and stays portable for
    /// unit tests.
    /// </summary>
    private static void ApplyPostgresColumnTypes(ModelBuilder modelBuilder)
    {
        // ---- JSON columns (text -> jsonb) ----
        modelBuilder.Entity<Entities.KnowledgeApiTokenEntity>().Property(x => x.Scopes).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.McpUserTokenEntity>().Property(x => x.Scopes).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.ExtractionJobEntity>().Property(x => x.PromptSnapshot).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.ExtractionJobEntity>().Property(x => x.ChunkIds).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.ExtractionJobEntity>().Property(x => x.UnknownClasses).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.AxiomProvenanceEntity>().Property(x => x.ReviewRecord).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.AboxProvenanceEntity>().Property(x => x.ReviewRecord).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.AuditEventEntity>().Property(x => x.Detail).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.OntologyReleaseEntity>().Property(x => x.Manifest).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.ReleaseStatementProvenanceEntity>().Property(x => x.Payload).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.ExportJobEntity>().Property(x => x.Files).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.ConflictEntity>().Property(x => x.Payload).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.EntityResolutionEntity>().Property(x => x.Context).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.TermProposalEntity>().Property(x => x.Payload).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.TermProposalEntity>().Property(x => x.Evidence).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.TermProposalEntity>().Property(x => x.SourceChunkIds).HasColumnType("jsonb");
        modelBuilder.Entity<Entities.TboxReconciliationEntity>().Property(x => x.Candidates).HasColumnType("jsonb");

        // ---- Binary columns (bytea) ----
        modelBuilder.Entity<Entities.AuditEventEntity>().Property(x => x.Added).HasColumnType("bytea");
        modelBuilder.Entity<Entities.AuditEventEntity>().Property(x => x.Removed).HasColumnType("bytea");
    }
}