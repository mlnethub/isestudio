using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceStatements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workspace_statements_KnowledgeSystemId_Layer_Subject_Predic~",
                table: "workspace_statements");

            migrationBuilder.CreateTable(
                name: "source_statement",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalStatementId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FactKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceNameSnapshot = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_statement", x => x.Id);
                    table.ForeignKey(
                        name: "FK_source_statement_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_source_statement_source_SourceId",
                        column: x => x.SourceId,
                        principalTable: "source",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "source_statement_fact",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceStatementId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_statement_fact", x => x.Id);
                    table.ForeignKey(
                        name: "FK_source_statement_fact_knowledgesystem_KnowledgeSystemId",
                        column: x => x.KnowledgeSystemId,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_source_statement_fact_source_statement_SourceStatementId",
                        column: x => x.SourceStatementId,
                        principalTable: "source_statement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_source_statement_KnowledgeSystemId_FactKey",
                table: "source_statement",
                columns: new[] { "KnowledgeSystemId", "FactKey" });

            migrationBuilder.CreateIndex(
                name: "IX_source_statement_SourceId_ExternalStatementId",
                table: "source_statement",
                columns: new[] { "SourceId", "ExternalStatementId" },
                unique: true,
                filter: "\"SourceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_source_statement_fact_KnowledgeSystemId_FactKey",
                table: "source_statement_fact",
                columns: new[] { "KnowledgeSystemId", "FactKey" });

            migrationBuilder.CreateIndex(
                name: "IX_source_statement_fact_SourceStatementId_FactKey",
                table: "source_statement_fact",
                columns: new[] { "SourceStatementId", "FactKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_statement_fact");

            migrationBuilder.DropTable(
                name: "source_statement");

            migrationBuilder.DropIndex(
                name: "IX_workspace_statements_KnowledgeSystemId_Layer_Subject_Predic~",
                table: "workspace_statements");

            migrationBuilder.CreateIndex(
                name: "IX_workspace_statements_KnowledgeSystemId_Layer_Subject_Predic~",
                table: "workspace_statements",
                columns: new[] { "KnowledgeSystemId", "Layer", "Subject", "Predicate", "Object", "Language", "Datatype" },
                unique: true);
        }
    }
}
