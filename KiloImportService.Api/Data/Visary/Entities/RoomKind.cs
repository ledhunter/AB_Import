namespace KiloImportService.Api.Data.Visary.Entities;

/// <summary>
/// Справочник видов помещений (Квартира, Машиноместо, Кладовая, …).
/// Таблица <c>Data."RoomKind"</c>.
/// </summary>
public class RoomKind
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public bool Hidden { get; set; }
}
