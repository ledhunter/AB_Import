namespace Visary.Api.Dto;

/// <summary>
/// Универсальный ответ Visary ListView.
/// Реальный формат: <c>{ Data: T[], Total: number, Summaries: [] }</c> — поддерживаем
/// также camelCase/Items на случай альтернативных эндпоинтов.
/// </summary>
public sealed class ListViewResponse<T>
{
    public List<T>? Data { get; set; }
    public List<T>? Items { get; set; }
    public int? Total { get; set; }
    public int? TotalCount { get; set; }

    public IReadOnlyList<T> Rows => Data ?? Items ?? new List<T>();
    public int TotalRows => Total ?? TotalCount ?? Rows.Count;
}

/// <summary>
/// Сырая строка Visary ListView для mnemonic <c>constructionproject</c>.
/// Имена — PascalCase, как в API. Маппится в <see cref="Data.Entities.CachedProject"/>.
/// </summary>
public sealed class ConstructionProjectRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public string? IdentifierKK { get; set; }
    public string? IdentifierZPLM { get; set; }
}

/// <summary>
/// Сырая строка Visary ListView для mnemonic <c>constructionsite</c>.
/// </summary>
public sealed class ConstructionSiteRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public int? ConstructionProjectId { get; set; }
    public string? ConstructionPermissionNumber { get; set; }
    public string? ConstructionProjectNumber { get; set; }
    public string? StageNumber { get; set; }
    public int? RegionId { get; set; }
    public int? TownId { get; set; }
    public string? Address { get; set; }
    public bool? Hidden { get; set; }
    public DateTime? Version { get; set; }
    public int? FinishingMaterialId { get; set; }
}

/// <summary>
/// Данные для обновления объекта строительства через Visary CRUD API.
/// </summary>
public sealed class SiteUpdateData
{
    public int ID { get; set; }
    public int? FinishingMaterialId { get; set; }
    public DateTime? Version { get; set; }
}
