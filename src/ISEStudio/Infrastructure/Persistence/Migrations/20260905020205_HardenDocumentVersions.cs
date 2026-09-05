using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenDocumentVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE document_version
                SET content_sha256 = lower(content_sha256);
                """);

            migrationBuilder.AlterColumn<string>(
                name: "content_sha256",
                table: "document_version",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_version_content_sha256_format",
                table: "document_version",
                sql: "content_sha256 ~ '^[0-9a-f]{64}$'");

            migrationBuilder.DropForeignKey(
                name: "FK_document_version_document_document_id",
                table: "document_version");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_document_id_knowledge_system_id",
                table: "document",
                columns: new[] { "id", "KnowledgeSystemId" });

            migrationBuilder.AddForeignKey(
                name: "FK_document_version_document_document_id_knowledge_system_id",
                table: "document_version",
                columns: new[] { "document_id", "knowledge_system_id" },
                principalTable: "document",
                principalColumns: new[] { "id", "KnowledgeSystemId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE FUNCTION prevent_document_version_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'document versions and their chunks are immutable';
                END;
                $$;

                CREATE TRIGGER document_version_immutable
                BEFORE UPDATE OR DELETE ON document_version
                FOR EACH ROW EXECUTE FUNCTION prevent_document_version_mutation();

                CREATE TRIGGER document_version_chunk_immutable
                BEFORE UPDATE OR DELETE ON document_version_chunk
                FOR EACH ROW EXECUTE FUNCTION prevent_document_version_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS document_version_chunk_immutable ON document_version_chunk;
                DROP TRIGGER IF EXISTS document_version_immutable ON document_version;
                DROP FUNCTION IF EXISTS prevent_document_version_mutation();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_document_version_document_document_id_knowledge_system_id",
                table: "document_version");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_document_id_knowledge_system_id",
                table: "document");

            migrationBuilder.AddForeignKey(
                name: "FK_document_version_document_document_id",
                table: "document_version",
                column: "document_id",
                principalTable: "document",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_version_content_sha256_format",
                table: "document_version");

            migrationBuilder.AlterColumn<string>(
                name: "content_sha256",
                table: "document_version",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);
        }
    }
}
