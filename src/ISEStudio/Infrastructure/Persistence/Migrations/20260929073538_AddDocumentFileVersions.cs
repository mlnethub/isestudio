using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISEStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentFileVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_file_version",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    doc_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_file_version", x => x.id);
                    table.CheckConstraint("ck_document_file_version_size", "size_bytes >= 0");
                    table.CheckConstraint("ck_document_file_version_version", "version > 0");
                    table.ForeignKey(
                        name: "FK_document_file_version_document_document_id",
                        column: x => x.document_id,
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_file_version_snapshot",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_file_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_version_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_file_version_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_file_version_snapshot_document_file_version_docume~",
                        column: x => x.document_file_version_id,
                        principalTable: "document_file_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_document_file_version_snapshot_document_version_document_ve~",
                        column: x => x.document_version_id,
                        principalTable: "document_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_document_file_version_document_version",
                table: "document_file_version",
                columns: new[] { "document_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_file_version_snapshot_document_version_id",
                table: "document_file_version_snapshot",
                column: "document_version_id");

            migrationBuilder.CreateIndex(
                name: "ux_document_file_version_snapshot_pair",
                table: "document_file_version_snapshot",
                columns: new[] { "document_file_version_id", "document_version_id" },
                unique: true);

            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    INSERT INTO document_file_version (id, document_id, version, sha256, size_bytes, created_at)
                    SELECT gen_random_uuid(), d.id, 1, d."Sha256", d."SizeBytes", d."UploadedAt"
                    FROM document d WHERE d."Sha256" <> ''
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_file_version_snapshot");

            migrationBuilder.DropTable(
                name: "document_file_version");
        }
    }
}
