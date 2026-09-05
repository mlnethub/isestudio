using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DiagnoseDocumentVersionModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_document_version_document_id",
                table: "document_version");

            migrationBuilder.CreateIndex(
                name: "IX_document_version_document_id_knowledge_system_id",
                table: "document_version",
                columns: new[] { "document_id", "knowledge_system_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_document_version_document_id_knowledge_system_id",
                table: "document_version");

            migrationBuilder.CreateIndex(
                name: "IX_document_version_document_id",
                table: "document_version",
                column: "document_id");
        }
    }
}
