using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiloImportService.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSheetToStagedRowAndError : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_staged_rows_ImportSessionId_SourceRowNumber",
                schema: "import",
                table: "staged_rows");

            migrationBuilder.DropIndex(
                name: "IX_import_errors_ImportSessionId_SourceRowNumber",
                schema: "import",
                table: "import_errors");

            migrationBuilder.AddColumn<string>(
                name: "Sheet",
                schema: "import",
                table: "staged_rows",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Sheet",
                schema: "import",
                table: "import_errors",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_staged_rows_ImportSessionId_Sheet_SourceRowNumber",
                schema: "import",
                table: "staged_rows",
                columns: new[] { "ImportSessionId", "Sheet", "SourceRowNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_import_errors_ImportSessionId_Sheet_SourceRowNumber",
                schema: "import",
                table: "import_errors",
                columns: new[] { "ImportSessionId", "Sheet", "SourceRowNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_staged_rows_ImportSessionId_Sheet_SourceRowNumber",
                schema: "import",
                table: "staged_rows");

            migrationBuilder.DropIndex(
                name: "IX_import_errors_ImportSessionId_Sheet_SourceRowNumber",
                schema: "import",
                table: "import_errors");

            migrationBuilder.DropColumn(
                name: "Sheet",
                schema: "import",
                table: "staged_rows");

            migrationBuilder.DropColumn(
                name: "Sheet",
                schema: "import",
                table: "import_errors");

            migrationBuilder.CreateIndex(
                name: "IX_staged_rows_ImportSessionId_SourceRowNumber",
                schema: "import",
                table: "staged_rows",
                columns: new[] { "ImportSessionId", "SourceRowNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_import_errors_ImportSessionId_SourceRowNumber",
                schema: "import",
                table: "import_errors",
                columns: new[] { "ImportSessionId", "SourceRowNumber" });
        }
    }
}
