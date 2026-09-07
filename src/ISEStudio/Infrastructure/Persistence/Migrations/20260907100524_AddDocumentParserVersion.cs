using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentParserVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParserVersion",
                table: "document",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParserVersion",
                table: "document");
        }
    }
}
