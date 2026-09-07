using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowOrphanDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_document_version_document_document_id_knowledge_system_id",
                table: "document_version");

            migrationBuilder.DropIndex(
                name: "IX_document_version_document_id_knowledge_system_id",
                table: "document_version");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_document_id_knowledge_system_id",
                table: "document");

            migrationBuilder.AlterColumn<Guid>(
                name: "KnowledgeSystemId",
                table: "document",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "IX_document_version_document_id",
                table: "document_version",
                column: "document_id");

            migrationBuilder.AddForeignKey(
                name: "FK_document_version_document_document_id",
                table: "document_version",
                column: "document_id",
                principalTable: "document",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_document_version_document_document_id",
                table: "document_version");

            migrationBuilder.DropIndex(
                name: "IX_document_version_document_id",
                table: "document_version");

            migrationBuilder.AlterColumn<Guid>(
                name: "KnowledgeSystemId",
                table: "document",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_document_id_knowledge_system_id",
                table: "document",
                columns: new[] { "id", "KnowledgeSystemId" });

            migrationBuilder.CreateIndex(
                name: "IX_document_version_document_id_knowledge_system_id",
                table: "document_version",
                columns: new[] { "document_id", "knowledge_system_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_document_version_document_document_id_knowledge_system_id",
                table: "document_version",
                columns: new[] { "document_id", "knowledge_system_id" },
                principalTable: "document",
                principalColumns: new[] { "id", "KnowledgeSystemId" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
