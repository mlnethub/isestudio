using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OnToPilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenEntityNavigations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "KnowledgeSystemId1",
                table: "mcpusertoken",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId1",
                table: "mcpusertoken",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "KnowledgeSystemId1",
                table: "knowledgeapitoken",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_mcpusertoken_KnowledgeSystemId1",
                table: "mcpusertoken",
                column: "KnowledgeSystemId1");

            migrationBuilder.CreateIndex(
                name: "IX_mcpusertoken_UserId1",
                table: "mcpusertoken",
                column: "UserId1");

            migrationBuilder.CreateIndex(
                name: "IX_knowledgeapitoken_KnowledgeSystemId1",
                table: "knowledgeapitoken",
                column: "KnowledgeSystemId1");

            migrationBuilder.AddForeignKey(
                name: "FK_knowledgeapitoken_knowledgesystem_KnowledgeSystemId1",
                table: "knowledgeapitoken",
                column: "KnowledgeSystemId1",
                principalTable: "knowledgesystem",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_mcpusertoken_knowledgesystem_KnowledgeSystemId1",
                table: "mcpusertoken",
                column: "KnowledgeSystemId1",
                principalTable: "knowledgesystem",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_mcpusertoken_users_UserId1",
                table: "mcpusertoken",
                column: "UserId1",
                principalTable: "users",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_knowledgeapitoken_knowledgesystem_KnowledgeSystemId1",
                table: "knowledgeapitoken");

            migrationBuilder.DropForeignKey(
                name: "FK_mcpusertoken_knowledgesystem_KnowledgeSystemId1",
                table: "mcpusertoken");

            migrationBuilder.DropForeignKey(
                name: "FK_mcpusertoken_users_UserId1",
                table: "mcpusertoken");

            migrationBuilder.DropIndex(
                name: "IX_mcpusertoken_KnowledgeSystemId1",
                table: "mcpusertoken");

            migrationBuilder.DropIndex(
                name: "IX_mcpusertoken_UserId1",
                table: "mcpusertoken");

            migrationBuilder.DropIndex(
                name: "IX_knowledgeapitoken_KnowledgeSystemId1",
                table: "knowledgeapitoken");

            migrationBuilder.DropColumn(
                name: "KnowledgeSystemId1",
                table: "mcpusertoken");

            migrationBuilder.DropColumn(
                name: "UserId1",
                table: "mcpusertoken");

            migrationBuilder.DropColumn(
                name: "KnowledgeSystemId1",
                table: "knowledgeapitoken");
        }
    }
}
