using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCompatibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false, defaultValue: ""),
                    ApiKey = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false, defaultValue: ""),
                    Model = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "llm"),
                    ConcurrencyLimit = table.Column<int>(type: "integer", nullable: false, defaultValue: 10),
                    LastTestOk = table.Column<bool>(type: "boolean", nullable: true),
                    LastTestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PasswordHash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "systemconfig",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExtractModel = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    LlmProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmbeddingProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExtractionConcurrency = table.Column<int>(type: "integer", nullable: true),
                    BaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ApiKey = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_systemconfig", x => x.id);
                    table.ForeignKey(
                        name: "FK_systemconfig_provider_EmbeddingProviderId",
                        column: x => x.EmbeddingProviderId,
                        principalTable: "provider",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_systemconfig_provider_LlmProviderId",
                        column: x => x.LlmProviderId,
                        principalTable: "provider",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "authsession",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authsession", x => x.id);
                    table.ForeignKey(
                        name: "FK_authsession_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledgesystem",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    GraphIri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, defaultValue: ""),
                    BaseIri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, defaultValue: ""),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClassCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    PropertyCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    AxiomCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LlmModel = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    LlmProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmbeddingProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledgesystem", x => x.id);
                    table.ForeignKey(
                        name: "FK_knowledgesystem_provider_EmbeddingProviderId",
                        column: x => x.EmbeddingProviderId,
                        principalTable: "provider",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_knowledgesystem_provider_LlmProviderId",
                        column: x => x.LlmProviderId,
                        principalTable: "provider",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_knowledgesystem_users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "auditevent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Detail = table.Column<string>(type: "jsonb", nullable: true),
                    Graph = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    GroupId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Added = table.Column<byte[]>(type: "bytea", nullable: true),
                    Removed = table.Column<byte[]>(type: "bytea", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auditevent", x => x.id);
                    table.ForeignKey(
                        name: "FK_auditevent_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_auditevent_users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conflict",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Signature = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Ctype = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "error"),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "open"),
                    Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    Detail = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Payload = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conflict", x => x.id);
                    table.ForeignKey(
                        name: "FK_conflict_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OriginalFilename = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Folder = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, defaultValue: "/"),
                    Ext = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Mime = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    StoragePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, defaultValue: ""),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ParseStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    ParserBackend = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ParseError = table.Column<string>(type: "text", nullable: true),
                    TextCharCount = table.Column<int>(type: "integer", nullable: true),
                    ChunkCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TboxExtractedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AboxExtractedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "extractionjob",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "tbox"),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    Model = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    PromptSnapshot = table.Column<string>(type: "jsonb", nullable: true),
                    ChunkIds = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Log = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Error = table.Column<string>(type: "text", nullable: true),
                    TotalChunks = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ProcessedChunks = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ClassesAdded = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    PropertiesAdded = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    AxiomsAdded = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    IndividualsAdded = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    AssertionsAdded = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    PendingAdded = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    UnknownClasses = table.Column<string>(type: "jsonb", nullable: true),
                    Phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: ""),
                    TermsAdded = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TermsMapped = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TerminologyProposals = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TerminologyError = table.Column<string>(type: "text", nullable: true),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extractionjob", x => x.id);
                    table.ForeignKey(
                        name: "FK_extractionjob_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledgeapitoken",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TokenPrefix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SecretCiphertext = table.Column<string>(type: "text", nullable: true),
                    Scopes = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledgeapitoken", x => x.id);
                    table.ForeignKey(
                        name: "FK_knowledgeapitoken_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_knowledgeapitoken_users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledgepromptoverride",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromptKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledgepromptoverride", x => x.id);
                    table.ForeignKey(
                        name: "FK_knowledgepromptoverride_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_knowledgepromptoverride_users_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ksgrant",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "viewer"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ksgrant", x => x.id);
                    table.ForeignKey(
                        name: "FK_ksgrant_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ksgrant_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mcpusertoken",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TokenPrefix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Scopes = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcpusertoken", x => x.id);
                    table.ForeignKey(
                        name: "FK_mcpusertoken_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_mcpusertoken_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ontologyrelease",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "draft"),
                    Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    Notes = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    SnapshotDir = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, defaultValue: ""),
                    Manifest = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    ReviewedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedByName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    PublishedById = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedByName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ontologyrelease", x => x.id);
                    table.ForeignKey(
                        name: "FK_ontologyrelease_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ontologyrelease_users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ontologyrelease_users_PublishedById",
                        column: x => x.PublishedById,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ontologyrelease_users_ReviewedById",
                        column: x => x.ReviewedById,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tboxreconciliation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slot = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PropertyLabel = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PropertyIri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Candidates = table.Column<string>(type: "jsonb", nullable: true),
                    Choice = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: ""),
                    ChosenLabel = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tboxreconciliation", x => x.id);
                    table.ForeignKey(
                        name: "FK_tboxreconciliation_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "validationdecision",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyLabel = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PropertyIri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    XsdType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: ""),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validationdecision", x => x.id);
                    table.ForeignKey(
                        name: "FK_validationdecision_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "chunk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Idx = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Text = table.Column<string>(type: "text", nullable: false),
                    CharStart = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CharEnd = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TokenEstimate = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chunk", x => x.id);
                    table.ForeignKey(
                        name: "FK_chunk_document_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "termproposal",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Signature = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Term = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, defaultValue: ""),
                    TargetIri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    Payload = table.Column<string>(type: "jsonb", nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    Evidence = table.Column<string>(type: "jsonb", nullable: true),
                    SourceChunkIds = table.Column<string>(type: "jsonb", nullable: true),
                    ExtractionJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProposedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: "terminology-agent"),
                    ResolvedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ResolutionNote = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_termproposal", x => x.id);
                    table.ForeignKey(
                        name: "FK_termproposal_extractionjob_ExtractionJobId",
                        column: x => x.ExtractionJobId,
                        principalTable: "extractionjob",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_termproposal_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "exportjob",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Layer = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Format = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "nquads"),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    ShardSize = table.Column<int>(type: "integer", nullable: false, defaultValue: 100000),
                    ProcessedStatements = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TotalStatements = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    OutputDir = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, defaultValue: ""),
                    Files = table.Column<string>(type: "jsonb", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exportjob", x => x.id);
                    table.ForeignKey(
                        name: "FK_exportjob_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_exportjob_ontologyrelease_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "ontologyrelease",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_exportjob_users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "releasedeployment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "provisioning"),
                    TboxGraphIri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, defaultValue: ""),
                    VocabularyGraphIri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, defaultValue: ""),
                    AboxGraphIri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, defaultValue: ""),
                    StatementCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ProvenanceCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StoppedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_releasedeployment", x => x.id);
                    table.ForeignKey(
                        name: "FK_releasedeployment_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_releasedeployment_ontologyrelease_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "ontologyrelease",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "releasestatementprovenance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Layer = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StatementKey = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: true),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_releasestatementprovenance", x => x.id);
                    table.ForeignKey(
                        name: "FK_releasestatementprovenance_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_releasestatementprovenance_ontologyrelease_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "ontologyrelease",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "aboxprovenance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ChunkId = table.Column<Guid>(type: "uuid", nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Method = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "extraction"),
                    ActorName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    AuditEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewRecord = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aboxprovenance", x => x.id);
                    table.ForeignKey(
                        name: "FK_aboxprovenance_auditevent_AuditEventId",
                        column: x => x.AuditEventId,
                        principalTable: "auditevent",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_aboxprovenance_chunk_ChunkId",
                        column: x => x.ChunkId,
                        principalTable: "chunk",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_aboxprovenance_extractionjob_JobId",
                        column: x => x.JobId,
                        principalTable: "extractionjob",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_aboxprovenance_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "axiomprovenance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    AxiomKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ChunkId = table.Column<Guid>(type: "uuid", nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Method = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "extraction"),
                    ActorName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, defaultValue: ""),
                    AuditEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewRecord = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_axiomprovenance", x => x.id);
                    table.ForeignKey(
                        name: "FK_axiomprovenance_auditevent_AuditEventId",
                        column: x => x.AuditEventId,
                        principalTable: "auditevent",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_axiomprovenance_chunk_ChunkId",
                        column: x => x.ChunkId,
                        principalTable: "chunk",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_axiomprovenance_extractionjob_JobId",
                        column: x => x.JobId,
                        principalTable: "extractionjob",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_axiomprovenance_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "entityresolution",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    SurfaceForm = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ClassIri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    IndividualIri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SourceChunkId = table.Column<Guid>(type: "uuid", nullable: true),
                    Context = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entityresolution", x => x.id);
                    table.ForeignKey(
                        name: "FK_entityresolution_chunk_SourceChunkId",
                        column: x => x.SourceChunkId,
                        principalTable: "chunk",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_entityresolution_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_aboxprov_audit_event_id",
                table: "aboxprovenance",
                column: "AuditEventId");

            migrationBuilder.CreateIndex(
                name: "ix_aboxprov_chunk_id",
                table: "aboxprovenance",
                column: "ChunkId");

            migrationBuilder.CreateIndex(
                name: "ix_aboxprov_fact_key",
                table: "aboxprovenance",
                column: "FactKey");

            migrationBuilder.CreateIndex(
                name: "ix_aboxprov_job_id",
                table: "aboxprovenance",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "ix_aboxprov_knowledge_system_id",
                table: "aboxprovenance",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ux_aboxprov_legacy_id",
                table: "aboxprovenance",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_auditevent_action",
                table: "auditevent",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "ix_auditevent_actor_id",
                table: "auditevent",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "ix_auditevent_created_at",
                table: "auditevent",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_auditevent_group_id",
                table: "auditevent",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "ix_auditevent_knowledge_system_id",
                table: "auditevent",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ux_auditevent_legacy_id",
                table: "auditevent",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_authsession_user_id",
                table: "authsession",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ux_authsession_legacy_id",
                table: "authsession",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_authsession_token",
                table: "authsession",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_axiomprov_audit_event_id",
                table: "axiomprovenance",
                column: "AuditEventId");

            migrationBuilder.CreateIndex(
                name: "ix_axiomprov_axiom_key",
                table: "axiomprovenance",
                column: "AxiomKey");

            migrationBuilder.CreateIndex(
                name: "ix_axiomprov_chunk_id",
                table: "axiomprovenance",
                column: "ChunkId");

            migrationBuilder.CreateIndex(
                name: "ix_axiomprov_job_id",
                table: "axiomprovenance",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "ix_axiomprov_knowledge_system_id",
                table: "axiomprovenance",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ux_axiomprov_legacy_id",
                table: "axiomprovenance",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chunk_document_id",
                table: "chunk",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "ux_chunk_legacy_id",
                table: "chunk",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_conflict_ctype",
                table: "conflict",
                column: "Ctype");

            migrationBuilder.CreateIndex(
                name: "ix_conflict_knowledge_system_id",
                table: "conflict",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_conflict_signature",
                table: "conflict",
                column: "Signature");

            migrationBuilder.CreateIndex(
                name: "ix_conflict_status",
                table: "conflict",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "ux_conflict_legacy_id",
                table: "conflict",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_ext",
                table: "document",
                column: "Ext");

            migrationBuilder.CreateIndex(
                name: "ix_document_folder",
                table: "document",
                column: "Folder");

            migrationBuilder.CreateIndex(
                name: "ix_document_knowledge_system_id",
                table: "document",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_document_parse_status",
                table: "document",
                column: "ParseStatus");

            migrationBuilder.CreateIndex(
                name: "ix_document_sha256",
                table: "document",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "ux_document_knowledge_system_id_sha256",
                table: "document",
                columns: new[] { "KnowledgeSystemId", "Sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_document_legacy_id",
                table: "document",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_er_class_iri",
                table: "entityresolution",
                column: "ClassIri");

            migrationBuilder.CreateIndex(
                name: "ix_er_created_at",
                table: "entityresolution",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_er_knowledge_system_id",
                table: "entityresolution",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_er_source_chunk_id",
                table: "entityresolution",
                column: "SourceChunkId");

            migrationBuilder.CreateIndex(
                name: "ix_er_status",
                table: "entityresolution",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "ix_er_surface_form",
                table: "entityresolution",
                column: "SurfaceForm");

            migrationBuilder.CreateIndex(
                name: "ux_er_legacy_id",
                table: "entityresolution",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_exportjob_created_at",
                table: "exportjob",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_exportjob_CreatedById",
                table: "exportjob",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "ix_exportjob_knowledge_system_id",
                table: "exportjob",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_exportjob_layer",
                table: "exportjob",
                column: "Layer");

            migrationBuilder.CreateIndex(
                name: "ix_exportjob_release_id",
                table: "exportjob",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "ix_exportjob_status",
                table: "exportjob",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "ux_exportjob_legacy_id",
                table: "exportjob",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_extractionjob_kind",
                table: "extractionjob",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "ix_extractionjob_knowledge_system_id",
                table: "extractionjob",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_extractionjob_phase",
                table: "extractionjob",
                column: "Phase");

            migrationBuilder.CreateIndex(
                name: "ix_extractionjob_status",
                table: "extractionjob",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "ux_extractionjob_legacy_id",
                table: "extractionjob",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kat_created_by",
                table: "knowledgeapitoken",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "ix_kat_knowledge_system_id",
                table: "knowledgeapitoken",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_kat_name",
                table: "knowledgeapitoken",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "ux_kat_legacy_id",
                table: "knowledgeapitoken",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_kat_token_hash",
                table: "knowledgeapitoken",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledgepromptoverride_UpdatedById",
                table: "knowledgepromptoverride",
                column: "UpdatedById");

            migrationBuilder.CreateIndex(
                name: "ix_kpo_knowledge_system_id",
                table: "knowledgepromptoverride",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_kpo_prompt_key",
                table: "knowledgepromptoverride",
                column: "PromptKey");

            migrationBuilder.CreateIndex(
                name: "ux_kpo_knowledge_system_id_prompt_key",
                table: "knowledgepromptoverride",
                columns: new[] { "KnowledgeSystemId", "PromptKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_kpo_legacy_id",
                table: "knowledgepromptoverride",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledgesystem_EmbeddingProviderId",
                table: "knowledgesystem",
                column: "EmbeddingProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledgesystem_LlmProviderId",
                table: "knowledgesystem",
                column: "LlmProviderId");

            migrationBuilder.CreateIndex(
                name: "ix_ks_name",
                table: "knowledgesystem",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "ix_ks_owner_id",
                table: "knowledgesystem",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "ux_ks_legacy_id",
                table: "knowledgesystem",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_ks_public_id",
                table: "knowledgesystem",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ksgrant_knowledge_system_id",
                table: "ksgrant",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_ksgrant_user_id",
                table: "ksgrant",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ux_ksgrant_legacy_id",
                table: "ksgrant",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_knowledge_system_id",
                table: "mcpusertoken",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_name",
                table: "mcpusertoken",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_user_id",
                table: "mcpusertoken",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ux_mcp_legacy_id",
                table: "mcpusertoken",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_mcp_token_hash",
                table: "mcpusertoken",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ontologyrelease_CreatedById",
                table: "ontologyrelease",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ontologyrelease_PublishedById",
                table: "ontologyrelease",
                column: "PublishedById");

            migrationBuilder.CreateIndex(
                name: "IX_ontologyrelease_ReviewedById",
                table: "ontologyrelease",
                column: "ReviewedById");

            migrationBuilder.CreateIndex(
                name: "ix_release_created_at",
                table: "ontologyrelease",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_release_knowledge_system_id",
                table: "ontologyrelease",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_release_status",
                table: "ontologyrelease",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "ix_release_version",
                table: "ontologyrelease",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "ux_release_knowledge_system_id_version",
                table: "ontologyrelease",
                columns: new[] { "KnowledgeSystemId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_release_legacy_id",
                table: "ontologyrelease",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_name",
                table: "provider",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "ux_provider_legacy_id",
                table: "provider",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_created_at",
                table: "releasedeployment",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_knowledge_system_id",
                table: "releasedeployment",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_status",
                table: "releasedeployment",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "ux_deployment_legacy_id",
                table: "releasedeployment",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_deployment_release_id",
                table: "releasedeployment",
                column: "ReleaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rsp_knowledge_system_id",
                table: "releasestatementprovenance",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_rsp_layer",
                table: "releasestatementprovenance",
                column: "Layer");

            migrationBuilder.CreateIndex(
                name: "ix_rsp_release_id",
                table: "releasestatementprovenance",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "ix_rsp_statement_key",
                table: "releasestatementprovenance",
                column: "StatementKey");

            migrationBuilder.CreateIndex(
                name: "ux_rsp_legacy_id",
                table: "releasestatementprovenance",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_systemconfig_EmbeddingProviderId",
                table: "systemconfig",
                column: "EmbeddingProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_systemconfig_LlmProviderId",
                table: "systemconfig",
                column: "LlmProviderId");

            migrationBuilder.CreateIndex(
                name: "ux_systemconfig_legacy_id",
                table: "systemconfig",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tboxr_created_at",
                table: "tboxreconciliation",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_tboxr_knowledge_system_id",
                table: "tboxreconciliation",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_tboxr_property_label",
                table: "tboxreconciliation",
                column: "PropertyLabel");

            migrationBuilder.CreateIndex(
                name: "ix_tboxr_slot",
                table: "tboxreconciliation",
                column: "Slot");

            migrationBuilder.CreateIndex(
                name: "ux_tboxr_legacy_id",
                table: "tboxreconciliation",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tp_action",
                table: "termproposal",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "ix_tp_created_at",
                table: "termproposal",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_tp_extraction_job_id",
                table: "termproposal",
                column: "ExtractionJobId");

            migrationBuilder.CreateIndex(
                name: "ix_tp_knowledge_system_id",
                table: "termproposal",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_tp_signature",
                table: "termproposal",
                column: "Signature");

            migrationBuilder.CreateIndex(
                name: "ix_tp_status",
                table: "termproposal",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "ix_tp_target_iri",
                table: "termproposal",
                column: "TargetIri");

            migrationBuilder.CreateIndex(
                name: "ix_tp_term",
                table: "termproposal",
                column: "Term");

            migrationBuilder.CreateIndex(
                name: "ux_tp_legacy_id",
                table: "termproposal",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_legacy_id",
                table: "users",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_username",
                table: "users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vd_created_at",
                table: "validationdecision",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_vd_knowledge_system_id",
                table: "validationdecision",
                column: "KnowledgeSystemId");

            migrationBuilder.CreateIndex(
                name: "ix_vd_property_iri",
                table: "validationdecision",
                column: "PropertyIri");

            migrationBuilder.CreateIndex(
                name: "ix_vd_property_label",
                table: "validationdecision",
                column: "PropertyLabel");

            migrationBuilder.CreateIndex(
                name: "ux_vd_legacy_id",
                table: "validationdecision",
                column: "legacy_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "aboxprovenance");

            migrationBuilder.DropTable(
                name: "authsession");

            migrationBuilder.DropTable(
                name: "axiomprovenance");

            migrationBuilder.DropTable(
                name: "conflict");

            migrationBuilder.DropTable(
                name: "entityresolution");

            migrationBuilder.DropTable(
                name: "exportjob");

            migrationBuilder.DropTable(
                name: "knowledgeapitoken");

            migrationBuilder.DropTable(
                name: "knowledgepromptoverride");

            migrationBuilder.DropTable(
                name: "ksgrant");

            migrationBuilder.DropTable(
                name: "mcpusertoken");

            migrationBuilder.DropTable(
                name: "releasedeployment");

            migrationBuilder.DropTable(
                name: "releasestatementprovenance");

            migrationBuilder.DropTable(
                name: "systemconfig");

            migrationBuilder.DropTable(
                name: "tboxreconciliation");

            migrationBuilder.DropTable(
                name: "termproposal");

            migrationBuilder.DropTable(
                name: "validationdecision");

            migrationBuilder.DropTable(
                name: "auditevent");

            migrationBuilder.DropTable(
                name: "chunk");

            migrationBuilder.DropTable(
                name: "ontologyrelease");

            migrationBuilder.DropTable(
                name: "extractionjob");

            migrationBuilder.DropTable(
                name: "document");

            migrationBuilder.DropTable(
                name: "knowledgesystem");

            migrationBuilder.DropTable(
                name: "provider");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
