using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseDraftPartialUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_release_knowledge_system_id_version",
                table: "ontologyrelease");

            migrationBuilder.CreateIndex(
                name: "ux_release_knowledge_system_id_version",
                table: "ontologyrelease",
                columns: new[] { "KnowledgeSystemId", "Version" },
                unique: true,
                filter: "\"Status\" <> 'draft'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_release_knowledge_system_id_version",
                table: "ontologyrelease");

            migrationBuilder.CreateIndex(
                name: "ux_release_knowledge_system_id_version",
                table: "ontologyrelease",
                columns: new[] { "KnowledgeSystemId", "Version" },
                unique: true);
        }
    }
}
