using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LegacyIdDefaultZero : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_vd_legacy_id",
                table: "validationdecision");

            migrationBuilder.DropIndex(
                name: "ux_users_legacy_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ux_tp_legacy_id",
                table: "termproposal");

            migrationBuilder.DropIndex(
                name: "ux_tboxr_legacy_id",
                table: "tboxreconciliation");

            migrationBuilder.DropIndex(
                name: "ux_systemconfig_legacy_id",
                table: "systemconfig");

            migrationBuilder.DropIndex(
                name: "ux_rsp_legacy_id",
                table: "releasestatementprovenance");

            migrationBuilder.DropIndex(
                name: "ux_deployment_legacy_id",
                table: "releasedeployment");

            migrationBuilder.DropIndex(
                name: "ux_provider_legacy_id",
                table: "provider");

            migrationBuilder.DropIndex(
                name: "ux_release_legacy_id",
                table: "ontologyrelease");

            migrationBuilder.DropIndex(
                name: "ux_mcp_legacy_id",
                table: "mcpusertoken");

            migrationBuilder.DropIndex(
                name: "ux_ksgrant_legacy_id",
                table: "ksgrant");

            migrationBuilder.DropIndex(
                name: "ux_ks_legacy_id",
                table: "knowledgesystem");

            migrationBuilder.DropIndex(
                name: "ux_kpo_legacy_id",
                table: "knowledgepromptoverride");

            migrationBuilder.DropIndex(
                name: "ux_kat_legacy_id",
                table: "knowledgeapitoken");

            migrationBuilder.DropIndex(
                name: "ux_extractionjob_legacy_id",
                table: "extractionjob");

            migrationBuilder.DropIndex(
                name: "ux_exportjob_legacy_id",
                table: "exportjob");

            migrationBuilder.DropIndex(
                name: "ux_er_legacy_id",
                table: "entityresolution");

            migrationBuilder.DropIndex(
                name: "ux_document_legacy_id",
                table: "document");

            migrationBuilder.DropIndex(
                name: "ux_conflict_legacy_id",
                table: "conflict");

            migrationBuilder.DropIndex(
                name: "ux_chunk_legacy_id",
                table: "chunk");

            migrationBuilder.DropIndex(
                name: "ux_axiomprov_legacy_id",
                table: "axiomprovenance");

            migrationBuilder.DropIndex(
                name: "ux_authsession_legacy_id",
                table: "authsession");

            migrationBuilder.DropIndex(
                name: "ux_auditevent_legacy_id",
                table: "auditevent");

            migrationBuilder.DropIndex(
                name: "ux_aboxprov_legacy_id",
                table: "aboxprovenance");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "validationdecision",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "termproposal",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "tboxreconciliation",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "releasestatementprovenance",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "releasedeployment",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "provider",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "ontologyrelease",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "mcpusertoken",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "ksgrant",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "knowledgesystem",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "knowledgepromptoverride",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "knowledgeapitoken",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "extractionjob",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "exportjob",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "entityresolution",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "document",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "conflict",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "chunk",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "axiomprovenance",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "authsession",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "auditevent",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "aboxprovenance",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "validationdecision",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "users",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "termproposal",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "tboxreconciliation",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "releasestatementprovenance",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "releasedeployment",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "provider",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "ontologyrelease",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "mcpusertoken",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "ksgrant",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "knowledgesystem",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "knowledgepromptoverride",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "knowledgeapitoken",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "extractionjob",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "exportjob",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "entityresolution",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "document",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "conflict",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "chunk",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "axiomprovenance",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "authsession",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "auditevent",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "legacy_id",
                table: "aboxprovenance",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            // WARNING: this Down() will fail to recreate the ux_*_legacy_id UNIQUE indexes
            // if any rows were inserted with legacy_id = 0 post-Phase 2 (the new default).
            // Manual data cleanup is required before rollback: e.g.
            //   UPDATE <table> SET legacy_id = -abs(hashtext(id::text)) WHERE legacy_id = 0;
            // then restore on the way back up.
            migrationBuilder.CreateIndex(
                name: "ux_vd_legacy_id",
                table: "validationdecision",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_legacy_id",
                table: "users",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tp_legacy_id",
                table: "termproposal",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tboxr_legacy_id",
                table: "tboxreconciliation",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_systemconfig_legacy_id",
                table: "systemconfig",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_rsp_legacy_id",
                table: "releasestatementprovenance",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_deployment_legacy_id",
                table: "releasedeployment",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_provider_legacy_id",
                table: "provider",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_release_legacy_id",
                table: "ontologyrelease",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_mcp_legacy_id",
                table: "mcpusertoken",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_ksgrant_legacy_id",
                table: "ksgrant",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_ks_legacy_id",
                table: "knowledgesystem",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_kpo_legacy_id",
                table: "knowledgepromptoverride",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_kat_legacy_id",
                table: "knowledgeapitoken",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_extractionjob_legacy_id",
                table: "extractionjob",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_exportjob_legacy_id",
                table: "exportjob",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_er_legacy_id",
                table: "entityresolution",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_document_legacy_id",
                table: "document",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_conflict_legacy_id",
                table: "conflict",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_chunk_legacy_id",
                table: "chunk",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_axiomprov_legacy_id",
                table: "axiomprovenance",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_authsession_legacy_id",
                table: "authsession",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_auditevent_legacy_id",
                table: "auditevent",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_aboxprov_legacy_id",
                table: "aboxprovenance",
                column: "legacy_id",
                unique: true);
        }
    }
}
