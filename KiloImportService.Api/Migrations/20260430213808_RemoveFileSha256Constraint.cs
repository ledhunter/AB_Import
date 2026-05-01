using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiloImportService.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveFileSha256Constraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ImportSession_TypeAndSha",
                schema: "import",
                table: "import_sessions");

            migrationBuilder.DropColumn(
                name: "FileSha256",
                schema: "import",
                table: "import_sessions");

            migrationBuilder.CreateIndex(
                name: "IX_ImportSession_Type",
                schema: "import",
                table: "import_sessions",
                column: "ImportTypeCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImportSession_Type",
                schema: "import",
                table: "import_sessions");

            migrationBuilder.AddColumn<string>(
                name: "FileSha256",
                schema: "import",
                table: "import_sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "UX_ImportSession_TypeAndSha",
                schema: "import",
                table: "import_sessions",
                columns: new[] { "ImportTypeCode", "FileSha256" },
                unique: true,
                filter: "\"Status\" NOT IN ('Failed','Cancelled')");
        }
    }
}
