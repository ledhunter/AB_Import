using KiloImportService.Api.Data.Visary.Entities;
using Microsoft.EntityFrameworkCore;

namespace KiloImportService.Api.Data.Visary;

/// <summary>
/// EF Core контекст для целевой БД Visary.
/// Подход «DB-first»: схема создаётся через <c>db/visary/init/*.sql</c>,
/// EF Core НЕ управляет миграциями этой БД.
///
/// Имена таблиц/колонок в исходной схеме — PascalCase в кавычках
/// ("Data"."Room"), поэтому здесь явно мапим столбцы.
/// </summary>
public class VisaryDbContext : DbContext
{
    public const string DataSchema = "Data";

    public VisaryDbContext(DbContextOptions<VisaryDbContext> options) : base(options) { }

    public DbSet<ConstructionProject> ConstructionProjects => Set<ConstructionProject>();
    public DbSet<ConstructionSite> ConstructionSites => Set<ConstructionSite>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<RoomKind> RoomKinds => Set<RoomKind>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ─── ConstructionProject ───
        b.Entity<ConstructionProject>(e =>
        {
            e.ToTable("ConstructionProject", DataSchema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("ID");
            e.Property(x => x.Title).HasColumnName("Title");
            e.Property(x => x.IdentifierKK).HasColumnName("IdentifierKK");
            e.Property(x => x.IdentifierZPLM).HasColumnName("IdentifierZPLM");
            e.Property(x => x.Hidden).HasColumnName("Hidden");
        });

        // ─── ConstructionSite ───
        b.Entity<ConstructionSite>(e =>
        {
            e.ToTable("ConstructionSite", DataSchema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("ID");
            e.Property(x => x.Title).HasColumnName("Title");
            e.Property(x => x.ConstructionProjectId).HasColumnName("ConstructionProjectID");
            e.Property(x => x.ConstructionPermissionNumber).HasColumnName("ConstructionPermissionNumber");
            e.Property(x => x.ConstructionProjectNumber).HasColumnName("ConstructionProjectNumber");
            e.Property(x => x.StageNumber).HasColumnName("StageNumber");
            e.Property(x => x.Hidden).HasColumnName("Hidden");
        });

        // ─── RoomKind ───
        b.Entity<RoomKind>(e =>
        {
            e.ToTable("RoomKind", DataSchema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("ID");
            e.Property(x => x.Title).HasColumnName("Title");
            e.Property(x => x.Hidden).HasColumnName("Hidden");
        });

        // ─── Room ───
        b.Entity<Room>(e =>
        {
            e.ToTable("Room", DataSchema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("ID");
            e.Property(x => x.Title).HasColumnName("Title");
            e.Property(x => x.SiteId).HasColumnName("SiteID");
            e.Property(x => x.SectionId).HasColumnName("SectionID");
            e.Property(x => x.Number).HasColumnName("Number");
            e.Property(x => x.Floor).HasColumnName("Floor");
            e.Property(x => x.KindId).HasColumnName("KindID");
            e.Property(x => x.RoomsNumber).HasColumnName("RoomsNumber");
            e.Property(x => x.IsStudio).HasColumnName("IsStudio");
            e.Property(x => x.TotalArea).HasColumnName("TotalArea");
            e.Property(x => x.LivingArea).HasColumnName("LivingArea");
            e.Property(x => x.Description).HasColumnName("Description");
            e.Property(x => x.Cost).HasColumnName("Cost");
            e.Property(x => x.IsSeparateEntrance).HasColumnName("IsSeparateEntrance");
            e.Property(x => x.IsShowcaseWindows).HasColumnName("IsShowcaseWindows");
            e.Property(x => x.TotalAreaWithoutSummerRoom).HasColumnName("TotalAreaWithoutSummerRoom");
            e.Property(x => x.SummerRoomArea).HasColumnName("SummerRoomArea");
            e.Property(x => x.CostForOne).HasColumnName("CostForOne");
            e.Property(x => x.ExplicationNumber).HasColumnName("ExplicationNumber");
            e.Property(x => x.BuildingSection).HasColumnName("BuildingSection");
            e.Property(x => x.UniqueNumber).HasColumnName("UniqueNumber");
            e.Property(x => x.ProjectArea).HasColumnName("ProjectArea");
            e.Property(x => x.RoomPurpose).HasColumnName("RoomPurpose");
            e.Property(x => x.ParkingPlaceType).HasColumnName("ParkingPlaceType");
            e.Property(x => x.Hidden).HasColumnName("Hidden");
        });

        base.OnModelCreating(b);
    }
}
