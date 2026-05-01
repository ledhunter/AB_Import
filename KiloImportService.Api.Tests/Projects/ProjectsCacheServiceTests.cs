using KiloImportService.Api.Data;
using KiloImportService.Api.Data.Entities;
using KiloImportService.Api.Domain.Projects;
using KiloImportService.Api.Domain.Visary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KiloImportService.Api.Tests.Projects;

/// <summary>
/// Тесты для <see cref="ProjectsCacheService"/>.
///
/// Используем InMemory-провайдер EF Core. Внимание: <c>EF.Functions.ILike</c>
/// не поддерживается InMemory — поэтому тесты, зависящие от поиска по строке,
/// помечены SkippableFact и пропускаются здесь. Полное покрытие даёт
/// интеграционный прогон против реального Postgres.
/// </summary>
public class ProjectsCacheServiceTests
{
    private static (ImportServiceDbContext db, FakeVisaryClient visary, ProjectsCacheService svc) Build(
        VisaryApiOptions? options = null)
    {
        var opt = options ?? new VisaryApiOptions { SyncPageSize = 2 };
        var db = new ImportServiceDbContext(
            new DbContextOptionsBuilder<ImportServiceDbContext>()
                .UseInMemoryDatabase($"projects-{Guid.NewGuid():N}")
                .Options);
        var visary = new FakeVisaryClient();
        var svc = new ProjectsCacheService(
            db,
            visary,
            Options.Create(opt),
            NullLogger<ProjectsCacheService>.Instance);
        return (db, visary, svc);
    }

    [Fact]
    public async Task SyncAllAsync_PaginatesUntilTotal_AndUpserts()
    {
        var (db, visary, svc) = Build(new VisaryApiOptions { SyncPageSize = 2 });

        // 5 проектов в Visary, страницы по 2 → 3 запроса (2 + 2 + 1).
        visary.Pages = new[]
        {
            Page(total: 5, ids: new[] { 1, 2 }),
            Page(total: 5, ids: new[] { 3, 4 }),
            Page(total: 5, ids: new[] { 5 }),
        };

        var result = await svc.SyncAllAsync(CancellationToken.None);

        Assert.Equal(5, result.Total);
        Assert.True(result.Upserted >= 5);
        Assert.Equal(3, visary.Calls.Count);
        Assert.Equal(0, visary.Calls[0].pageSkip);
        Assert.Equal(2, visary.Calls[1].pageSkip);
        Assert.Equal(4, visary.Calls[2].pageSkip);

        var stored = await db.CachedProjects.OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, stored.Select(p => p.Id));
    }

    [Fact]
    public async Task SyncAllAsync_StopsWhenServerReturnsEmptyPage()
    {
        var (db, visary, svc) = Build(new VisaryApiOptions { SyncPageSize = 10 });

        visary.Pages = new[]
        {
            Page(total: 100, ids: new[] { 1, 2, 3 }),
            Page(total: 100, ids: Array.Empty<int>()), // защита от бесконечного цикла
        };

        var result = await svc.SyncAllAsync(CancellationToken.None);

        Assert.Equal(2, visary.Calls.Count);
        Assert.Equal(3, await db.CachedProjects.CountAsync());
    }

    [Fact]
    public async Task SyncAllAsync_UpdatesExistingProjectsById()
    {
        var (db, visary, svc) = Build(new VisaryApiOptions { SyncPageSize = 10 });
        db.CachedProjects.Add(new CachedProject
        {
            Id = 7,
            Title = "Старое название",
            IdentifierKK = "old-kk",
            LastSyncedAt = DateTimeOffset.UtcNow.AddDays(-7),
        });
        await db.SaveChangesAsync();

        visary.Pages = new[]
        {
            new ListViewResponse<ConstructionProjectRaw>
            {
                Total = 1,
                Data = new()
                {
                    new() { ID = 7, Title = "Новое название", IdentifierKK = "new-kk" },
                },
            },
        };

        await svc.SyncAllAsync(CancellationToken.None);

        var entity = await db.CachedProjects.SingleAsync(p => p.Id == 7);
        Assert.Equal("Новое название", entity.Title);
        Assert.Equal("new-kk", entity.IdentifierKK);
        Assert.Equal(1, await db.CachedProjects.CountAsync());
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmptyListWithoutVisary()
    {
        var (db, visary, svc) = Build();
        db.CachedProjects.AddRange(
            new CachedProject { Id = 1, Title = "Бета", LastSyncedAt = DateTimeOffset.UtcNow },
            new CachedProject { Id = 2, Title = "Альфа", LastSyncedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var result = await svc.SearchAsync(string.Empty, limit: 10, CancellationToken.None);

        Assert.False(result.FromFallback);
        Assert.Empty(result.Items); // Пустой запрос → пустой список
        Assert.Empty(visary.Calls);
    }

    [Fact]
    public async Task SearchAsync_FallbackToVisary_WhenLocalEmpty_AndUpserts()
    {
        var (db, visary, svc) = Build();
        // Локальная БД пуста → должен сработать fallback по непустому запросу.
        visary.Pages = new[]
        {
            new ListViewResponse<ConstructionProjectRaw>
            {
                Total = 1,
                Data = new()
                {
                    new() { ID = 42, Title = "Жилой комплекс Север", IdentifierKK = "ZK-42" },
                },
            },
        };

        var result = await svc.SearchAsync("север", limit: 10, CancellationToken.None);

        Assert.True(result.FromFallback);
        // Fallback upsert произошёл: запись теперь в локальной БД.
        var stored = await db.CachedProjects.SingleAsync();
        Assert.Equal(42, stored.Id);
        Assert.Single(visary.Calls);
        Assert.Equal("север", visary.Calls[0].search);
    }

    [Fact]
    public async Task SearchAsync_FallbackReturnsNothing_WhenVisaryEmpty()
    {
        var (db, visary, svc) = Build();
        visary.Pages = new[]
        {
            new ListViewResponse<ConstructionProjectRaw> { Total = 0, Data = new() },
        };

        var result = await svc.SearchAsync("несуществующий", limit: 10, CancellationToken.None);

        Assert.True(result.FromFallback);
        Assert.Empty(result.Items);
        Assert.Equal(0, await db.CachedProjects.CountAsync());
    }

    [Fact]
    public async Task SyncAllAsync_DropsRowsWithInvalidId()
    {
        var (db, visary, svc) = Build();
        visary.Pages = new[]
        {
            new ListViewResponse<ConstructionProjectRaw>
            {
                Total = 2,
                Data = new()
                {
                    new() { ID = 0, Title = "битый" },             // должен быть отфильтрован
                    new() { ID = 100, Title = "корректный" },
                },
            },
        };

        await svc.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, await db.CachedProjects.CountAsync());
        Assert.True(await db.CachedProjects.AnyAsync(p => p.Id == 100));
    }

    [Fact]
    public async Task SyncAllAsync_FillsTitlePlaceholder_WhenNullOrEmpty()
    {
        var (db, visary, svc) = Build();
        visary.Pages = new[]
        {
            new ListViewResponse<ConstructionProjectRaw>
            {
                Total = 1,
                Data = new()
                {
                    new() { ID = 5, Title = null }, // должно стать "Проект #5"
                },
            },
        };

        await svc.SyncAllAsync(CancellationToken.None);

        var entity = await db.CachedProjects.SingleAsync();
        Assert.Equal("Проект #5", entity.Title);
    }

    // ─── helpers ───

    private static ListViewResponse<ConstructionProjectRaw> Page(int total, int[] ids) => new()
    {
        Total = total,
        Data = ids.Select(id => new ConstructionProjectRaw
        {
            ID = id,
            Title = $"Проект {id}",
            IdentifierKK = $"KK-{id}",
        }).ToList(),
    };

    private sealed class FakeVisaryClient : IVisaryListViewClient
    {
        public IReadOnlyList<ListViewResponse<ConstructionProjectRaw>> Pages { get; set; } =
            Array.Empty<ListViewResponse<ConstructionProjectRaw>>();

        public List<(int pageSkip, int pageSize, string search)> Calls { get; } = new();

        public Task<ListViewResponse<ConstructionProjectRaw>> FetchProjectsAsync(
            int pageSkip, int pageSize, string searchString, CancellationToken ct)
        {
            Calls.Add((pageSkip, pageSize, searchString));
            var idx = Math.Min(Calls.Count - 1, Pages.Count - 1);
            var page = idx >= 0 && idx < Pages.Count
                ? Pages[idx]
                : new ListViewResponse<ConstructionProjectRaw> { Total = 0, Data = new() };
            return Task.FromResult(page);
        }
    }
}
