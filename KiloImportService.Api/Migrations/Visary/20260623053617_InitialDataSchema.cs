using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KiloImportService.Api.Migrations.Visary
{
    /// <inheritdoc />
    public partial class InitialDataSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Data");

            migrationBuilder.CreateTable(
                name: "ConstructionProject",
                schema: "Data",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: true),
                    IdentifierKK = table.Column<string>(type: "text", nullable: true),
                    IdentifierZPLM = table.Column<string>(type: "text", nullable: true),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConstructionProject", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "ConstructionSite",
                schema: "Data",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: true),
                    ConstructionProjectID = table.Column<int>(type: "integer", nullable: true),
                    ConstructionPermissionNumber = table.Column<string>(type: "text", nullable: true),
                    ConstructionProjectNumber = table.Column<string>(type: "text", nullable: true),
                    StageNumber = table.Column<string>(type: "text", nullable: true),
                    RegionID = table.Column<int>(type: "integer", nullable: true),
                    TownID = table.Column<int>(type: "integer", nullable: true),
                    Address = table.Column<string>(type: "text", nullable: true),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishingMaterialId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConstructionSite", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "Room",
                schema: "Data",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: true),
                    SiteID = table.Column<int>(type: "integer", nullable: false),
                    SectionID = table.Column<int>(type: "integer", nullable: true),
                    Number = table.Column<string>(type: "text", nullable: true),
                    Floor = table.Column<string>(type: "text", nullable: true),
                    KindID = table.Column<int>(type: "integer", nullable: false),
                    RoomsNumber = table.Column<int>(type: "integer", nullable: true),
                    IsStudio = table.Column<bool>(type: "boolean", nullable: false),
                    TotalArea = table.Column<double>(type: "double precision", nullable: true),
                    LivingArea = table.Column<double>(type: "double precision", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Cost = table.Column<decimal>(type: "numeric", nullable: true),
                    IsSeparateEntrance = table.Column<string>(type: "text", nullable: true),
                    IsShowcaseWindows = table.Column<string>(type: "text", nullable: true),
                    TotalAreaWithoutSummerRoom = table.Column<double>(type: "double precision", nullable: true),
                    SummerRoomArea = table.Column<double>(type: "double precision", nullable: true),
                    CostForOne = table.Column<decimal>(type: "numeric", nullable: true),
                    ExplicationNumber = table.Column<string>(type: "text", nullable: true),
                    BuildingSection = table.Column<string>(type: "text", nullable: true),
                    UniqueNumber = table.Column<string>(type: "text", nullable: true),
                    ProjectArea = table.Column<double>(type: "double precision", nullable: false),
                    RoomPurpose = table.Column<string>(type: "text", nullable: true),
                    ParkingPlaceType = table.Column<string>(type: "text", nullable: true),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Room", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "RoomKind",
                schema: "Data",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomKind", x => x.ID);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConstructionProject",
                schema: "Data");

            migrationBuilder.DropTable(
                name: "ConstructionSite",
                schema: "Data");

            migrationBuilder.DropTable(
                name: "Room",
                schema: "Data");

            migrationBuilder.DropTable(
                name: "RoomKind",
                schema: "Data");
        }
    }
}
