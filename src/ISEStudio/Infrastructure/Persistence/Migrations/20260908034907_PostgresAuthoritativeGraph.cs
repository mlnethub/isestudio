using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PostgresAuthoritativeGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "iri",
                table: "relation_types",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "iri",
                table: "graph_entities",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "iri",
                table: "entity_types",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.Sql("UPDATE entity_types SET iri = 'urn:ontopilot:entity-type:' || knowledge_system_id || ':' || key WHERE iri IS NULL;");
            migrationBuilder.Sql("UPDATE relation_types SET iri = 'urn:ontopilot:relation-type:' || knowledge_system_id || ':' || key WHERE iri IS NULL;");
            migrationBuilder.Sql("UPDATE graph_entities SET iri = 'urn:ontopilot:entity:' || knowledge_system_id || ':' || id WHERE iri IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "iri",
                table: "entity_types",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "iri",
                table: "relation_types",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "iri",
                table: "graph_entities",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "ontology_axioms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_iri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    predicate_iri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    object_iri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    object_value = table.Column<string>(type: "text", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ontology_axioms", x => x.id);
                    table.ForeignKey(
                        name: "FK_ontology_axioms_knowledgesystem_knowledge_system_id",
                        column: x => x.knowledge_system_id,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ontology_release_statements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layer = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    graph_iri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    subject_iri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    predicate_iri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    object_iri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    object_value = table.Column<string>(type: "text", nullable: true),
                    statement_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ontology_release_statements", x => x.id);
                    table.ForeignKey(
                        name: "FK_ontology_release_statements_knowledgesystem_knowledge_syste~",
                        column: x => x.knowledge_system_id,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ontology_release_statements_ontologyrelease_release_id",
                        column: x => x.release_id,
                        principalTable: "ontologyrelease",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_relation_types_knowledge_system_id_iri",
                table: "relation_types",
                columns: new[] { "knowledge_system_id", "iri" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_graph_entities_knowledge_system_id_iri",
                table: "graph_entities",
                columns: new[] { "knowledge_system_id", "iri" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_entity_types_knowledge_system_id_iri",
                table: "entity_types",
                columns: new[] { "knowledge_system_id", "iri" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ontology_axioms_knowledge_system_id_subject_predicate",
                table: "ontology_axioms",
                columns: new[] { "knowledge_system_id", "subject_iri", "predicate_iri" });

            migrationBuilder.CreateIndex(
                name: "IX_ontology_release_statements_knowledge_system_id",
                table: "ontology_release_statements",
                column: "knowledge_system_id");

            migrationBuilder.CreateIndex(
                name: "ux_ontology_release_statements_release_id_statement_hash",
                table: "ontology_release_statements",
                columns: new[] { "release_id", "statement_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ontology_axioms");

            migrationBuilder.DropTable(
                name: "ontology_release_statements");

            migrationBuilder.DropIndex(
                name: "ux_relation_types_knowledge_system_id_iri",
                table: "relation_types");

            migrationBuilder.DropIndex(
                name: "ux_graph_entities_knowledge_system_id_iri",
                table: "graph_entities");

            migrationBuilder.DropIndex(
                name: "ux_entity_types_knowledge_system_id_iri",
                table: "entity_types");

            migrationBuilder.DropColumn(
                name: "iri",
                table: "relation_types");

            migrationBuilder.DropColumn(
                name: "iri",
                table: "graph_entities");

            migrationBuilder.DropColumn(
                name: "iri",
                table: "entity_types");
        }
    }
}
