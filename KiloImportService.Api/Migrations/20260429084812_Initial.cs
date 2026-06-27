using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KiloImportService.Api.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "import");

            migrationBuilder.CreateTable(
                name: "import_sessions",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportTypeCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    FileFormat = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    FileSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    VisaryProjectId = table.Column<int>(type: "integer", nullable: true),
                    VisarySiteId = table.Column<int>(type: "integer", nullable: true),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TotalRows = table.Column<int>(type: "integer", nullable: false),
                    SuccessRows = table.Column<int>(type: "integer", nullable: false),
                    ErrorRows = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "import_errors",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImportSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRowNumber = table.Column<int>(type: "integer", nullable: false),
                    ColumnName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_errors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_import_errors_import_sessions_ImportSessionId",
                        column: x => x.ImportSessionId,
                        principalSchema: "import",
                        principalTable: "import_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_file_snapshots",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelativePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_file_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_import_file_snapshots_import_sessions_ImportSessionId",
                        column: x => x.ImportSessionId,
                        principalSchema: "import",
                        principalTable: "import_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_session_stages",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImportSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsSuccess = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_session_stages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_import_session_stages_import_sessions_ImportSessionId",
                        column: x => x.ImportSessionId,
                        principalSchema: "import",
                        principalTable: "import_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staged_rows",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImportSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRowNumber = table.Column<int>(type: "integer", nullable: false),
                    RawValues = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    MappedValues = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AppliedTargetId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staged_rows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_staged_rows_import_sessions_ImportSessionId",
                        column: x => x.ImportSessionId,
                        principalSchema: "import",
                        principalTable: "import_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_import_errors_ImportSessionId_SourceRowNumber",
                schema: "import",
                table: "import_errors",
                columns: new[] { "ImportSessionId", "SourceRowNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_import_file_snapshots_ImportSessionId",
                schema: "import",
                table: "import_file_snapshots",
                column: "ImportSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_import_session_stages_ImportSessionId",
                schema: "import",
                table: "import_session_stages",
                column: "ImportSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_import_sessions_StartedAt",
                schema: "import",
                table: "import_sessions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_import_sessions_Status",
                schema: "import",
                table: "import_sessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "UX_ImportSession_TypeAndSha",
                schema: "import",
                table: "import_sessions",
                columns: new[] { "ImportTypeCode", "FileSha256" },
                unique: true,
                filter: "\"Status\" NOT IN ('Failed','Cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_staged_rows_ImportSessionId_SourceRowNumber",
                schema: "import",
                table: "staged_rows",
                columns: new[] { "ImportSessionId", "SourceRowNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staged_rows_ImportSessionId_Status",
                schema: "import",
                table: "staged_rows",
                columns: new[] { "ImportSessionId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_errors",
                schema: "import");

            migrationBuilder.DropTable(
                name: "import_file_snapshots",
                schema: "import");

            migrationBuilder.DropTable(
                name: "import_session_stages",
                schema: "import");

            migrationBuilder.DropTable(
                name: "staged_rows",
                schema: "import");

            migrationBuilder.DropTable(
                name: "import_sessions",
                schema: "import");
        }
    }
}
