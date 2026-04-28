namespace KiloImportService.Api.Data.Visary.Entities;

/// <summary>
/// Корневая сущность Visary — проект строительства.
/// Таблица <c>Data."ConstructionProject"</c>.
///
/// Минимальная модель для MVP — содержит только нужные поля.
/// При необходимости расширяется по полному списку колонок из 02-missing-roots.sql.
/// </summary>
public class ConstructionProject
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? IdentifierKK { get; set; }
    public string? IdentifierZPLM { get; set; }
    public bool Hidden { get; set; }
}
