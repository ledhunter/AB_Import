using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KiloImportService.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomApplySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "room_apply_snapshots",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VisarySiteId = table.Column<int>(type: "integer", nullable: false),
                    Sheet = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SectionTitle = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RoomKindId = table.Column<int>(type: "integer", nullable: true),
                    RoomNumber = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    BuildingSection = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MappedHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MappedSnapshot = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    VisarySectionId = table.Column<int>(type: "integer", nullable: true),
                    VisaryRoomId = table.Column<int>(type: "integer", nullable: true),
                    VisaryShareAgreementId = table.Column<int>(type: "integer", nullable: true),
                    ShareAgreementNumber = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    LastAppliedSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastAppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_apply_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoomApplySnapshot_BusinessKey",
                schema: "import",
                table: "room_apply_snapshots",
                columns: new[] { "VisarySiteId", "Sheet", "SectionTitle", "RoomKindId", "RoomNumber", "BuildingSection" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoomApplySnapshot_Site",
                schema: "import",
                table: "room_apply_snapshots",
                column: "VisarySiteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "room_apply_snapshots",
                schema: "import");
        }
    }
}
