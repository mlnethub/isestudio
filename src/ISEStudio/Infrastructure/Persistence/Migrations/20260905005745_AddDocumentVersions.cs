using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_version",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_sha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    chunk_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_version", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_version_document_document_id",
                        column: x => x.document_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_document_version_knowledgesystem_knowledge_system_id",
                        column: x => x.knowledge_system_id,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_version_chunk",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idx = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    char_start = table.Column<int>(type: "integer", nullable: false),
                    char_end = table.Column<int>(type: "integer", nullable: false),
                    token_estimate = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_version_chunk", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_version_chunk_document_version_document_version_id",
                        column: x => x.document_version_id,
                        principalTable: "document_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_version_document_id",
                table: "document_version",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_version_knowledge_system_document",
                table: "document_version",
                columns: new[] { "knowledge_system_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ux_document_version_knowledge_system_document_sha256",
                table: "document_version",
                columns: new[] { "knowledge_system_id", "document_id", "content_sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_document_version_chunk_version_idx",
                table: "document_version_chunk",
                columns: new[] { "document_version_id", "idx" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_version_chunk");

            migrationBuilder.DropTable(
                name: "document_version");
        }
    }
}
