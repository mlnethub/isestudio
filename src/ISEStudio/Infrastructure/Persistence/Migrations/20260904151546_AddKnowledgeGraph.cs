using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entity_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entity_types", x => x.id);
                    table.ForeignKey(
                        name: "FK_entity_types_knowledgesystem_knowledge_system_id",
                        column: x => x.knowledge_system_id,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "relation_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relation_types", x => x.id);
                    table.ForeignKey(
                        name: "FK_relation_types_knowledgesystem_knowledge_system_id",
                        column: x => x.knowledge_system_id,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "entity_type_parents",
                columns: table => new
                {
                    entity_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_entity_type_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entity_type_parents", x => new { x.entity_type_id, x.parent_entity_type_id });
                    table.ForeignKey(
                        name: "FK_entity_type_parents_entity_types_entity_type_id",
                        column: x => x.entity_type_id,
                        principalTable: "entity_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_entity_type_parents_entity_types_parent_entity_type_id",
                        column: x => x.parent_entity_type_id,
                        principalTable: "entity_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "graph_entities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_entities", x => x.id);
                    table.ForeignKey(
                        name: "FK_graph_entities_entity_types_entity_type_id",
                        column: x => x.entity_type_id,
                        principalTable: "entity_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_graph_entities_knowledgesystem_knowledge_system_id",
                        column: x => x.knowledge_system_id,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "relation_type_domains",
                columns: table => new
                {
                    relation_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relation_type_domains", x => new { x.relation_type_id, x.entity_type_id });
                    table.ForeignKey(
                        name: "FK_relation_type_domains_entity_types_entity_type_id",
                        column: x => x.entity_type_id,
                        principalTable: "entity_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_relation_type_domains_relation_types_relation_type_id",
                        column: x => x.relation_type_id,
                        principalTable: "relation_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "relation_type_ranges",
                columns: table => new
                {
                    relation_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relation_type_ranges", x => new { x.relation_type_id, x.entity_type_id });
                    table.ForeignKey(
                        name: "FK_relation_type_ranges_entity_types_entity_type_id",
                        column: x => x.entity_type_id,
                        principalTable: "entity_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_relation_type_ranges_relation_types_relation_type_id",
                        column: x => x.relation_type_id,
                        principalTable: "relation_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "facts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    predicate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    object_value = table.Column<string>(type: "jsonb", nullable: true),
                    confidence = table.Column<decimal>(type: "numeric", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    invalidated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    supersedes_fact_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_facts", x => x.id);
                    table.CheckConstraint("ck_facts_confidence_unit_interval", "(confidence >= 0 AND confidence <= 1)");
                    table.CheckConstraint("ck_facts_exactly_one_object", "((object_entity_id IS NULL) <> (object_value IS NULL))");
                    table.CheckConstraint("ck_facts_valid_window", "(valid_to IS NULL OR valid_from IS NULL OR valid_from <= valid_to)");
                    table.ForeignKey(
                        name: "FK_facts_facts_supersedes_fact_id",
                        column: x => x.supersedes_fact_id,
                        principalTable: "facts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_facts_graph_entities_object_entity_id",
                        column: x => x.object_entity_id,
                        principalTable: "graph_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_facts_graph_entities_subject_entity_id",
                        column: x => x.subject_entity_id,
                        principalTable: "graph_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_facts_knowledgesystem_knowledge_system_id",
                        column: x => x.knowledge_system_id,
                        principalTable: "knowledgesystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_facts_relation_types_predicate_id",
                        column: x => x.predicate_id,
                        principalTable: "relation_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fact_conflicts",
                columns: table => new
                {
                    fact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conflict_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fact_conflicts", x => new { x.fact_id, x.conflict_id });
                    table.ForeignKey(
                        name: "FK_fact_conflicts_conflict_conflict_id",
                        column: x => x.conflict_id,
                        principalTable: "conflict",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_fact_conflicts_facts_fact_id",
                        column: x => x.fact_id,
                        principalTable: "facts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fact_evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    fact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_chunk_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote = table.Column<string>(type: "text", nullable: false),
                    predicate = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fact_evidence", x => x.id);
                    table.ForeignKey(
                        name: "FK_fact_evidence_chunk_source_chunk_id",
                        column: x => x.source_chunk_id,
                        principalTable: "chunk",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_fact_evidence_facts_fact_id",
                        column: x => x.fact_id,
                        principalTable: "facts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_entity_type_parents_parent_entity_type_id",
                table: "entity_type_parents",
                column: "parent_entity_type_id");

            migrationBuilder.CreateIndex(
                name: "ux_entity_types_knowledge_system_id_key",
                table: "entity_types",
                columns: new[] { "knowledge_system_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fact_conflicts_conflict_id",
                table: "fact_conflicts",
                column: "conflict_id");

            migrationBuilder.CreateIndex(
                name: "ix_fact_evidence_fact_id",
                table: "fact_evidence",
                column: "fact_id");

            migrationBuilder.CreateIndex(
                name: "IX_fact_evidence_source_chunk_id",
                table: "fact_evidence",
                column: "source_chunk_id");

            migrationBuilder.CreateIndex(
                name: "ix_facts_knowledge_system_id_object_entity_id_active",
                table: "facts",
                columns: new[] { "knowledge_system_id", "object_entity_id" },
                filter: "invalidated_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_facts_knowledge_system_id_subject_entity_id_active",
                table: "facts",
                columns: new[] { "knowledge_system_id", "subject_entity_id" },
                filter: "invalidated_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_facts_object_entity_id",
                table: "facts",
                column: "object_entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_facts_predicate_id",
                table: "facts",
                column: "predicate_id");

            migrationBuilder.CreateIndex(
                name: "IX_facts_subject_entity_id",
                table: "facts",
                column: "subject_entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_facts_supersedes_fact_id",
                table: "facts",
                column: "supersedes_fact_id");

            migrationBuilder.CreateIndex(
                name: "IX_graph_entities_entity_type_id",
                table: "graph_entities",
                column: "entity_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_graph_entities_knowledge_system_id",
                table: "graph_entities",
                column: "knowledge_system_id");

            migrationBuilder.CreateIndex(
                name: "IX_relation_type_domains_entity_type_id",
                table: "relation_type_domains",
                column: "entity_type_id");

            migrationBuilder.CreateIndex(
                name: "IX_relation_type_ranges_entity_type_id",
                table: "relation_type_ranges",
                column: "entity_type_id");

            migrationBuilder.CreateIndex(
                name: "ux_relation_types_knowledge_system_id_key",
                table: "relation_types",
                columns: new[] { "knowledge_system_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entity_type_parents");

            migrationBuilder.DropTable(
                name: "fact_conflicts");

            migrationBuilder.DropTable(
                name: "fact_evidence");

            migrationBuilder.DropTable(
                name: "relation_type_domains");

            migrationBuilder.DropTable(
                name: "relation_type_ranges");

            migrationBuilder.DropTable(
                name: "facts");

            migrationBuilder.DropTable(
                name: "graph_entities");

            migrationBuilder.DropTable(
                name: "relation_types");

            migrationBuilder.DropTable(
                name: "entity_types");
        }
    }
}
