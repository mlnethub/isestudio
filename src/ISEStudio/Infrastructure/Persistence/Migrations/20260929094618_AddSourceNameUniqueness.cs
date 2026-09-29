using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceNameUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_source_knowledge_system_name",
                table: "source",
                columns: new[] { "knowledge_system_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_source_knowledge_system_name",
                table: "source");
        }
    }
}
