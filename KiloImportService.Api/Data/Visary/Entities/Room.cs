namespace KiloImportService.Api.Data.Visary.Entities;

/// <summary>
/// Помещение — целевая сущность импорта <c>rooms</c>.
/// Таблица <c>Data."Room"</c>. Структура взята из CSV-экспорта (минимум полей для MVP).
///
/// Обязательные поля (NOT NULL): <see cref="SiteId"/>, <see cref="KindId"/>,
/// <see cref="IsStudio"/>, <see cref="ProjectArea"/>.
/// Остальные могут быть <c>null</c> и заполняются по мере данных в файле.
/// </summary>
public class Room
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public int SiteId { get; set; }
    public int? SectionId { get; set; }
    public string? Number { get; set; }
    public string? Floor { get; set; }
    public int KindId { get; set; }
    public int? RoomsNumber { get; set; }
    public bool IsStudio { get; set; }
    public double? TotalArea { get; set; }
    public double? LivingArea { get; set; }
    public string? Description { get; set; }
    public decimal? Cost { get; set; }
    public string? IsSeparateEntrance { get; set; }
    public string? IsShowcaseWindows { get; set; }
    public double? TotalAreaWithoutSummerRoom { get; set; }
    public double? SummerRoomArea { get; set; }
    public decimal? CostForOne { get; set; }
    public string? ExplicationNumber { get; set; }
    public string? BuildingSection { get; set; }
    public string? UniqueNumber { get; set; }
    public double ProjectArea { get; set; }
    public string? RoomPurpose { get; set; }
    public string? ParkingPlaceType { get; set; }
    public bool Hidden { get; set; }
}
