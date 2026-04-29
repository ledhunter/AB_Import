namespace KiloImportService.Api.Data.Entities;

/// <summary>
/// Кэш проектов Visary, синхронизированных по HTTP ListView API.
/// Источник истины — Visary; локальная копия нужна для быстрого поиска с
/// автодополнением в UI без хождения в Visary на каждое нажатие клавиши.
///
/// См. doc_project/18-projects-cache.md.
/// </summary>
public class CachedProject
{
    /// <summary>ID из Visary (не auto-generate).</summary>
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? IdentifierKK { get; set; }

    public string? IdentifierZPLM { get; set; }

    /// <summary>Когда строка была загружена/обновлена из Visary.</summary>
    public DateTimeOffset LastSyncedAt { get; set; }
}
