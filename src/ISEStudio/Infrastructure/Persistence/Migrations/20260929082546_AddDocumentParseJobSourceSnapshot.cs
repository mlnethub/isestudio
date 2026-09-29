using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentParseJobSourceSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "source_id",
                table: "document_parse_job",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_parse_job_source",
                table: "document_parse_job",
                column: "source_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_document_parse_job_source",
                table: "document_parse_job");

            migrationBuilder.DropColumn(
                name: "source_id",
                table: "document_parse_job");
        }
    }
}
