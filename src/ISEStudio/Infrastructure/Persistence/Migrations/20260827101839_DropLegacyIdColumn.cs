using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyIdColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. AddColumn IsSingleton on systemconfig
            migrationBuilder.AddColumn<bool>(
                name: "IsSingleton",
                table: "systemconfig",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // 2. CreateIndex partial unique on systemconfig(IsSingleton)
            migrationBuilder.CreateIndex(
                name: "ux_systemconfig_singleton",
                table: "systemconfig",
                column: "IsSingleton",
                unique: true,
                filter: "\"IsSingleton\" = TRUE");

            // 3. Backfill: if seed row exists, mark it as singleton
            migrationBuilder.Sql("UPDATE systemconfig SET \"IsSingleton\" = TRUE WHERE id = (SELECT id FROM systemconfig LIMIT 1);");

            // 4. DropColumn legacy_id on 24 tables
            migrationBuilder.DropColumn(name: "legacy_id", table: "validationdecision");
            migrationBuilder.DropColumn(name: "legacy_id", table: "users");
            migrationBuilder.DropColumn(name: "legacy_id", table: "termproposal");
            migrationBuilder.DropColumn(name: "legacy_id", table: "tboxreconciliation");
            migrationBuilder.DropColumn(name: "legacy_id", table: "systemconfig");
            migrationBuilder.DropColumn(name: "legacy_id", table: "releasestatementprovenance");
            migrationBuilder.DropColumn(name: "legacy_id", table: "releasedeployment");
            migrationBuilder.DropColumn(name: "legacy_id", table: "provider");
            migrationBuilder.DropColumn(name: "legacy_id", table: "ontologyrelease");
            migrationBuilder.DropColumn(name: "legacy_id", table: "mcpusertoken");
            migrationBuilder.DropColumn(name: "legacy_id", table: "ksgrant");
            migrationBuilder.DropColumn(name: "legacy_id", table: "knowledgesystem");
            migrationBuilder.DropColumn(name: "legacy_id", table: "knowledgepromptoverride");
            migrationBuilder.DropColumn(name: "legacy_id", table: "knowledgeapitoken");
            migrationBuilder.DropColumn(name: "legacy_id", table: "extractionjob");
            migrationBuilder.DropColumn(name: "legacy_id", table: "exportjob");
            migrationBuilder.DropColumn(name: "legacy_id", table: "entityresolution");
            migrationBuilder.DropColumn(name: "legacy_id", table: "document");
            migrationBuilder.DropColumn(name: "legacy_id", table: "conflict");
            migrationBuilder.DropColumn(name: "legacy_id", table: "chunk");
            migrationBuilder.DropColumn(name: "legacy_id", table: "axiomprovenance");
            migrationBuilder.DropColumn(name: "legacy_id", table: "authsession");
            migrationBuilder.DropColumn(name: "legacy_id", table: "auditevent");
            migrationBuilder.DropColumn(name: "legacy_id", table: "aboxprovenance");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // WARNING: this Down() will fail to recreate the legacy_id column if any
            // rows were inserted post-Phase-3. The column is dropped; recreating it as
            // bigint NOT NULL with no DEFAULT will fail on existing data unless you
            // manually backfill legacy_id first. See Phase 2 Down() WARNING for parallel
            // pattern.
            migrationBuilder.DropIndex(
                name: "ux_systemconfig_singleton",
                table: "systemconfig");

            migrationBuilder.DropColumn(
                name: "IsSingleton",
                table: "systemconfig");

            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "validationdecision", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "users", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "termproposal", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "tboxreconciliation", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "systemconfig", type: "bigint", nullable: false, defaultValue: 1L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "releasestatementprovenance", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "releasedeployment", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "provider", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "ontologyrelease", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "mcpusertoken", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "ksgrant", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "knowledgesystem", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "knowledgepromptoverride", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "knowledgeapitoken", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "extractionjob", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "exportjob", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "entityresolution", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "document", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "conflict", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "chunk", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "axiomprovenance", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "authsession", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "auditevent", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "legacy_id", table: "aboxprovenance", type: "bigint", nullable: false, defaultValue: 0L);
        }
    }
}
