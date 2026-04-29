namespace KiloImportService.Api.Domain.Visary;

/// <summary>
/// Конфигурация Visary HTTP API.
/// Привязывается к секции <c>Visary</c> в appsettings или
/// переменным окружения <c>Visary__BaseUrl</c> / <c>Visary__BearerToken</c>.
/// </summary>
public sealed class VisaryApiOptions
{
    public const string SectionName = "Visary";

    /// <summary>Например, <c>https://isup-alfa-test.k8s.npc.ba</c>. Без завершающего <c>/</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Bearer-токен. В dev — из <c>.env</c>; в prod — secret manager.</summary>
    public string BearerToken { get; set; } = string.Empty;

    /// <summary>Размер страницы при синхронизации проектов (PageSize в ListView API).</summary>
    public int SyncPageSize { get; set; } = 200;

    /// <summary>Таймаут одного HTTP-запроса.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
