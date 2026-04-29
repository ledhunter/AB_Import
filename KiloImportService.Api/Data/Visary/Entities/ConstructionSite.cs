namespace KiloImportService.Api.Data.Visary.Entities;

/// <summary>
/// Объект строительства Visary. Таблица <c>Data."ConstructionSite"</c>.
/// </summary>
public class ConstructionSite
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public int? ConstructionProjectId { get; set; }
    public string? ConstructionPermissionNumber { get; set; }
    public string? ConstructionProjectNumber { get; set; }
    public string? StageNumber { get; set; }
    public bool Hidden { get; set; }
}
