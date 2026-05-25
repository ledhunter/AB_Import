using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiloImportService.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSecondaryFileToSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecondaryFileName",
                schema: "import",
                table: "import_file_snapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecondaryRelativePath",
                schema: "import",
                table: "import_file_snapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SecondarySizeBytes",
                schema: "import",
                table: "import_file_snapshots",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecondaryFileName",
                schema: "import",
                table: "import_file_snapshots");

            migrationBuilder.DropColumn(
                name: "SecondaryRelativePath",
                schema: "import",
                table: "import_file_snapshots");

            migrationBuilder.DropColumn(
                name: "SecondarySizeBytes",
                schema: "import",
                table: "import_file_snapshots");
        }
    }
}
