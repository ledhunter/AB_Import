using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiloImportService.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cached_projects",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IdentifierKK = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    IdentifierZPLM = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cached_projects", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedProject_Title",
                schema: "import",
                table: "cached_projects",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_CachedProject_IdentifierKK",
                schema: "import",
                table: "cached_projects",
                column: "IdentifierKK");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cached_projects",
                schema: "import");
        }
    }
}
