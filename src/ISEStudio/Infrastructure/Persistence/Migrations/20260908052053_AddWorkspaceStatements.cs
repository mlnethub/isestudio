using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceStatements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspace_statements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Layer = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GraphIri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Subject = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Predicate = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Object = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    ObjectKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Datatype = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Payload = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_statements", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_statements_KnowledgeSystemId_Layer",
                table: "workspace_statements",
                columns: new[] { "KnowledgeSystemId", "Layer" });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_statements_KnowledgeSystemId_Layer_Subject_Predic~",
                table: "workspace_statements",
                columns: new[] { "KnowledgeSystemId", "Layer", "Subject", "Predicate", "Object", "Language", "Datatype" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspace_statements");
        }
    }
}
