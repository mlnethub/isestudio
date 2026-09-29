using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_key",
                table: "document",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_manual_upload",
                table: "document",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "missing_since",
                table: "document",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_id",
                table: "document",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "source",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    icon = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sync_interval_minutes = table.Column<int>(type: "integer", nullable: true),
                    sync_cron = table.Column<string>(type: "text", nullable: true),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_sync_error = table.Column<string>(type: "text", nullable: true),
                    last_sync_added = table.Column<int>(type: "integer", nullable: false),
                    ingest_token_ciphertext = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source", x => x.id);
                    table.CheckConstraint("ck_source_kind", "kind IN ('folder','url','rss','custom','github_issues','jira_issues','s3','azure_blob','gcs','webdav','notion','api','statements','memory','upload')");
                    table.CheckConstraint("ck_source_last_sync_status", "last_sync_status IN ('never','queued','running','ok','failed')");
                    table.ForeignKey(
                        name: "FK_source_knowledgesystem_knowledge_system_id",
                        column: x => x.knowledge_system_id,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_document_source_external_key",
                table: "document",
                columns: new[] { "source_id", "external_key" },
                unique: true,
                filter: "external_key IS NOT NULL AND source_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_source_knowledge_system_id",
                table: "source",
                column: "knowledge_system_id");

            migrationBuilder.AddForeignKey(
                name: "FK_document_source_source_id",
                table: "document",
                column: "source_id",
                principalTable: "source",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

                        if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                        {
                                migrationBuilder.Sql("""
                                        INSERT INTO source (id, knowledge_system_id, kind, name, config, last_sync_status, last_sync_added, created_at)
                                        SELECT gen_random_uuid(), k.id, 'folder', 'Default', '{}'::jsonb, 'never', 0, k."CreatedAt"
                                        FROM knowledgesystem k
                                        WHERE NOT EXISTS (SELECT 1 FROM source s WHERE s.knowledge_system_id = k.id);

                                        UPDATE document d SET source_id = s.id
                                        FROM source s
                                        WHERE d."KnowledgeSystemId" = s.knowledge_system_id
                                            AND s.kind = 'folder' AND d.source_id IS NULL;
                                        """);
                        }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_document_source_source_id",
                table: "document");

            migrationBuilder.DropTable(
                name: "source");

            migrationBuilder.DropIndex(
                name: "ux_document_source_external_key",
                table: "document");

            migrationBuilder.DropColumn(
                name: "external_key",
                table: "document");

            migrationBuilder.DropColumn(
                name: "is_manual_upload",
                table: "document");

            migrationBuilder.DropColumn(
                name: "missing_since",
                table: "document");

            migrationBuilder.DropColumn(
                name: "source_id",
                table: "document");
        }
    }
}
