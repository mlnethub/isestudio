using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceDocumentBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "source_document_binding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    missing_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_document_binding", x => x.id);
                    table.ForeignKey(
                        name: "FK_source_document_binding_document_document_id",
                        column: x => x.document_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_source_document_binding_source_source_id",
                        column: x => x.source_id,
                        principalTable: "source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_source_document_binding_document_id",
                table: "source_document_binding",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ux_source_document_binding_source_external_key",
                table: "source_document_binding",
                columns: new[] { "source_id", "external_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_document_binding");
        }
    }
}
