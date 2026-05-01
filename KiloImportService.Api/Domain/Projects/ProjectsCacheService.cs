using KiloImportService.Api.Data;
using KiloImportService.Api.Data.Entities;
using KiloImportService.Api.Domain.Visary;
using Microsoft.EntityFrameworkCore;

namespace KiloImportService.Api.Domain.Projects;

/// <summary>
/// Сервис кэша проектов Visary в локальной БД.
///
/// Логика:
///   1. <see cref="SyncAllAsync"/> — постранично читает все проекты из Visary
///      ListView API и upsert'ит в <c>import.cached_projects</c>.
///   2. <see cref="SearchAsync"/> — сначала ищет в локальном кэше по
///      Title/IdentifierKK/IdentifierZPLM (ILIKE %query%). Если ничего не
///      найдено и поисковая строка непустая — делает запрос в Visary с
///      <c>SearchString</c>, upsert'ит результат и возвращает.
///
/// См. doc_project/18-projects-cache.md.
/// </summary>
public interface IProjectsCacheService
{
    /// <summary>Полная синхронизация. Возвращает количество обновлённых записей.</summary>
    Task<SyncResult> SyncAllAsync(CancellationToken ct);

    /// <summary>Поиск с fallback в Visary при пустом локальном результате.</summary>
    Task<SearchResult> SearchAsync(string query, int limit, CancellationToken ct);
}

public sealed record SyncResult(int Total, int Upserted, long DurationMs);

public sealed record SearchResult(
    IReadOnlyList<CachedProject> Items,
    bool FromFallback);

public sealed class ProjectsCacheService : IProjectsCacheService
{
    private const int DefaultSearchLimit = 50;
    private const int FallbackPageSize = 50;

    private readonly ImportServiceDbContext _db;
    private readonly IVisaryListViewClient _visary;
    private readonly VisaryApiOptions _options;
    private readonly ILogger<ProjectsCacheService> _log;

    public ProjectsCacheService(
        ImportServiceDbContext db,
        IVisaryListViewClient visary,
        Microsoft.Extensions.Options.IOptions<VisaryApiOptions> options,
        ILogger<ProjectsCacheService> log)
    {
        _db = db;
        _visary = visary;
        _options = options.Value;
        _log = log;
    }

    public async Task<SyncResult> SyncAllAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pageSize = Math.Max(1, _options.SyncPageSize);
        var skip = 0;
        var total = 0;
        var upserted = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _visary.FetchProjectsAsync(skip, pageSize, string.Empty, ct);
            total = page.TotalRows;
            if (page.Rows.Count == 0) break;

            upserted += await UpsertAsync(page.Rows, ct);
            skip += page.Rows.Count;
            if (skip >= total) break;
        }

        sw.Stop();
        _log.LogInformation(
            "ProjectsCacheService.SyncAllAsync: total={Total} upserted={Upserted} duration={Ms}ms",
            total, upserted, sw.ElapsedMilliseconds);

        return new SyncResult(total, upserted, sw.ElapsedMilliseconds);
    }

    public async Task<SearchResult> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var take = limit <= 0 ? DefaultSearchLimit : Math.Min(limit, 200);
        var trimmed = (query ?? string.Empty).Trim();

        var local = await SearchLocalAsync(trimmed, take, ct);
        if (local.Count > 0 || string.IsNullOrEmpty(trimmed))
        {
            _log.LogDebug(
                "ProjectsCacheService.SearchAsync: local hit query='{Q}' count={Count}",
                trimmed, local.Count);
            return new SearchResult(local, FromFallback: false);
        }

        // Fallback: ищем в Visary, upsert'им, возвращаем.
        _log.LogInformation(
            "ProjectsCacheService.SearchAsync: local miss query='{Q}' → fallback to Visary",
            trimmed);

        var page = await _visary.FetchProjectsAsync(0, FallbackPageSize, trimmed, ct);
        if (page.Rows.Count == 0)
        {
            return new SearchResult(Array.Empty<CachedProject>(), FromFallback: true);
        }

        await UpsertAsync(page.Rows, ct);
        var fallback = await SearchLocalAsync(trimmed, take, ct);
        return new SearchResult(fallback, FromFallback: true);
    }

    // ─── helpers ───

    private async Task<List<CachedProject>> SearchLocalAsync(
        string query, int take, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(query))
        {
            return new List<CachedProject>();
        }

        // Кейс-инсенситивный contains. Провайдер-агностично:
        //   - Npgsql транслирует .Contains(value, OrdinalIgnoreCase) → STRPOS(LOWER(...)).
        //   - InMemory исполняет на стороне .NET (нужно для unit-тестов).
        // Чистый ILIKE даёт лучшие планы на больших выборках — переехать через
        // citext/функциональный индекс можно позже, см. doc_project/18-projects-cache.md.
        var lowered = query.ToLowerInvariant();
        return await _db.CachedProjects
            .Where(p =>
                EF.Functions.Like(p.Title.ToLower(), $"%{lowered}%") ||
                (p.IdentifierKK != null && EF.Functions.Like(p.IdentifierKK.ToLower(), $"%{lowered}%")) ||
                (p.IdentifierZPLM != null && EF.Functions.Like(p.IdentifierZPLM.ToLower(), $"%{lowered}%")))
            .OrderBy(p => p.Title)
            .Take(take)
            .ToListAsync(ct);
    }

    private async Task<int> UpsertAsync(IEnumerable<ConstructionProjectRaw> rows, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var list = rows.Where(r => r.ID > 0).ToList();
        if (list.Count == 0) return 0;

        var ids = list.Select(r => r.ID).ToHashSet();
        var existing = await _db.CachedProjects
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var r in list)
        {
            // ⚠️ Используем ?? только тут — если Title null, сохраняем заглушку,
            // т.к. колонка NOT NULL. Логика fallback'а в UI остаётся (там через ||).
            var title = string.IsNullOrEmpty(r.Title) ? $"Проект #{r.ID}" : r.Title!;
            if (existing.TryGetValue(r.ID, out var entity))
            {
                entity.Title = title;
                entity.IdentifierKK = r.IdentifierKK;
                entity.IdentifierZPLM = r.IdentifierZPLM;
                entity.LastSyncedAt = now;
            }
            else
            {
                _db.CachedProjects.Add(new CachedProject
                {
                    Id = r.ID,
                    Title = title,
                    IdentifierKK = r.IdentifierKK,
                    IdentifierZPLM = r.IdentifierZPLM,
                    LastSyncedAt = now,
                });
            }
        }

        return await _db.SaveChangesAsync(ct);
    }
}
