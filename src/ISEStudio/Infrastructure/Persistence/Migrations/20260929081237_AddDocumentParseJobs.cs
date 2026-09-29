using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentParseJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_parse_job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_file_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_parse_job", x => x.id);
                    table.CheckConstraint("ck_document_parse_job_status", "status IN ('pending', 'running', 'completed', 'failed')");
                    table.ForeignKey(
                        name: "FK_document_parse_job_document_document_id",
                        column: x => x.document_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_document_parse_job_document_file_version_document_file_vers~",
                        column: x => x.document_file_version_id,
                        principalTable: "document_file_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_document_parse_job_knowledgesystem_knowledge_system_id",
                        column: x => x.knowledge_system_id,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_parse_job_document",
                table: "document_parse_job",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_parse_job_document_file_version_id",
                table: "document_parse_job",
                column: "document_file_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_document_parse_job_knowledge_system_id",
                table: "document_parse_job",
                column: "knowledge_system_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_parse_job_pending",
                table: "document_parse_job",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_parse_job");
        }
    }
}
