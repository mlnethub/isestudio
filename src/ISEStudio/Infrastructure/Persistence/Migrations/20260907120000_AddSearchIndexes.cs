using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations;

public partial class AddSearchIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE INDEX IF NOT EXISTS ix_document_version_chunk_text_fts
            ON document_version_chunk
            USING gin (to_tsvector('simple', text));
            """);

        migrationBuilder.Sql("""
            CREATE INDEX IF NOT EXISTS ix_document_version_search_scope
            ON document_version (knowledge_system_id, created_at, document_id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_document_version_search_scope;");
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_document_version_chunk_text_fts;");
    }
}