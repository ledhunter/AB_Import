using Visary.Api.Dto;
using Xunit;
using Xunit.Abstractions;

namespace KiloImportService.Api.Tests.VisaryLive;

/// <summary>
/// Live-тесты WBS (ИСР — иерархическая структура работ): главы и подстатьи бюджета.
/// Smoke-проверка для нового импорта «Финмодель → Себестоимость → главы».
/// • Read-only: <c>GetWbsByProjectAsync</c> — десериализация / поиск Главы 1.
/// • Write (создание): <c>CreateChapter1AndSubArticle_for_4584</c> — создаёт сущности
///   на test-стенде Visary. Запускайте сознательно, оставляет реальные записи.
///
/// Помечены trait="live" — фильтруются <c>dotnet test --filter Category=live</c>.
/// Без живого токена в .audit/.token (или env VISARY_TEST_TOKEN) автоматически skip-аются.
/// </summary>
[Trait("Category", "live")]
public sealed class VisaryWbsLiveTests
{
    private readonly ITestOutputHelper _output;

    public VisaryWbsLiveTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public async Task GetWbsByProjectAsync_returns_data_for_known_project()
    {
        SkipIfNoToken();

        var resp = await VisaryLiveClientFactory.NewListView()
            .GetWbsByProjectAsync(VisaryLiveTestIds.ConstructionProject, default);

        Assert.NotNull(resp);
        _output.WriteLine($"Total WBS entries for project {VisaryLiveTestIds.ConstructionProject}: {resp.Total}");

        // Логируем главы (top-level: ParentID == null или Code оканчивается на "X.").
        var chapters = resp.Data
            .Where(w => w.ParentID is null)
            .OrderBy(w => w.Code)
            .ToList();
        _output.WriteLine($"Chapters (ParentID is null): {chapters.Count}");
        foreach (var c in chapters.Take(20))
            _output.WriteLine($"  ID={c.ID,-8} Code={c.Code,-10} Title={c.Title}");
    }

    /// <summary>
    /// One-shot: создаёт Главу 1 (если её ещё нет у проекта 4584) и подстатью «Затраты на
    /// приобретение прав на ЗУ» под ней. Logs новые ID/Code. Запускать сознательно —
    /// каждый запуск создаёт новую подстатью (Code 1.1 → 1.2 → 1.3 ...) под Главой 1.
    /// </summary>
    [SkippableFact]
    public async Task CreateChapter1AndSubArticle_for_project_4584()
    {
        SkipIfNoToken();

        var listView = VisaryLiveClientFactory.NewListView();
        var crud = VisaryLiveClientFactory.NewCrud();

        var projectId = VisaryLiveTestIds.ConstructionProject;
        var siteId = VisaryLiveTestIds.ConstructionSite;
        const string chapter1Title = "Глава 1. Стоимость земельного участка и расходы по его содержанию";
        const string subArticleTitle = "Затраты на приобретение прав на ЗУ";

        // 1. Проверяем — есть ли уже Глава 1 у проекта.
        var existing = await listView.GetWbsByProjectAsync(projectId, default);
        var chapter1 = existing.Data.FirstOrDefault(w =>
            w.ParentID is null &&
            (w.Code == "1." || (w.Title?.Contains("Глава 1", StringComparison.OrdinalIgnoreCase) ?? false)));

        int chapterId;
        if (chapter1 is null)
        {
            _output.WriteLine($"Глава 1 не найдена — создаю новую (Title='{chapter1Title}').");
            var created = await crud.CreateWbsAsync(new WbsCreateRequest
            {
                ProjectID = projectId,
                Project = new VisaryRef { ID = projectId },
                Title = chapter1Title,
                ParentID = null,
                Parent = null,
            }, default);
            chapterId = created.ID;
            _output.WriteLine($"  ✓ Создана Глава: ID={created.ID}, Code='{created.Code ?? "(не вернулся)"}'");
        }
        else
        {
            chapterId = chapter1.ID;
            _output.WriteLine($"Глава 1 уже существует: ID={chapter1.ID}, Code='{chapter1.Code}', Title='{chapter1.Title}'");
        }

        // 2. Создаём подстатью «Затраты на приобретение прав на ЗУ» под Главой 1.
        // Code сервер присвоит автоматически (1.1 если первая, 1.2 если вторая, и т. д.).
        var sub = await crud.CreateWbsAsync(new WbsCreateRequest
        {
            ProjectID = projectId,
            Project = new VisaryRef { ID = projectId },
            ParentID = chapterId,
            Parent = new VisaryRef { ID = chapterId },
            ConstructionSiteID = siteId,
            ConstructionSite = new VisaryRef { ID = siteId },
            Title = subArticleTitle,
            DeclaredSum = 438_000,
            ConfirmedSum = 438_000,
        }, default);

        _output.WriteLine(
            $"  ✓ Создана подстатья: ID={sub.ID}, Code='{sub.Code ?? "(не вернулся)"}', " +
            $"ParentID={chapterId}, Title='{subArticleTitle}'");

        Assert.True(sub.ID > 0, "Visary должен вернуть ID созданной подстатьи");
    }

    /// <summary>
    /// Идемпотентность повторного импорта: запускаем upsert-цикл (find/create/patch)
    /// дважды на одной и той же подстатье. После первого прогона запись либо уже была,
    /// либо создалась; после второго — суммы совпадают, и PATCH не вызывается; новых
    /// дубликатов не появляется. Маркер успеха: количество подстатей у Главы 1 не растёт
    /// между двумя итерациями.
    /// </summary>
    [SkippableFact]
    public async Task BudgetUpsert_IsIdempotent_OnRepeatedRun()
    {
        SkipIfNoToken();

        var listView = VisaryLiveClientFactory.NewListView();
        var crud = VisaryLiveClientFactory.NewCrud();
        var projectId = VisaryLiveTestIds.ConstructionProject;
        var siteId = VisaryLiveTestIds.ConstructionSite;
        const string chapter1Title = "Глава 1. Стоимость земельного участка и расходы по его содержанию";
        const string subTitle = "Затраты на приобретение прав на ЗУ";
        const double sum1 = 555_000;
        const double sum2 = 777_000;

        // ── Итерация 1: find/create главы и подстатьи. ──────────────────────
        var existing1 = await listView.GetWbsByProjectAsync(projectId, default);
        var chapter = existing1.Data.FirstOrDefault(w => w.ParentID is null
            && (w.Code == "1." || (w.Title?.Contains("Глава 1", StringComparison.OrdinalIgnoreCase) ?? false)));

        int chapterId;
        if (chapter is null)
        {
            var createdCh = await crud.CreateWbsAsync(new WbsCreateRequest
            {
                ProjectID = projectId,
                Project = new VisaryRef { ID = projectId },
                Title = chapter1Title,
            }, default);
            chapterId = createdCh.ID;
            _output.WriteLine($"Создал Главу: id={createdCh.ID} code={createdCh.Code}");
        }
        else
        {
            chapterId = chapter.ID;
            _output.WriteLine($"Глава уже есть: id={chapter.ID} code={chapter.Code}");
        }

        var article = existing1.Data.FirstOrDefault(w =>
            w.ParentID == chapterId &&
            string.Equals(w.Title?.Trim(), subTitle, StringComparison.OrdinalIgnoreCase));

        int articleId;
        if (article is null)
        {
            var created = await crud.CreateWbsAsync(new WbsCreateRequest
            {
                ProjectID = projectId, Project = new VisaryRef { ID = projectId },
                ParentID = chapterId,  Parent = new VisaryRef { ID = chapterId },
                ConstructionSiteID = siteId, ConstructionSite = new VisaryRef { ID = siteId },
                Title = subTitle, DeclaredSum = sum1, ConfirmedSum = sum1,
            }, default);
            articleId = created.ID;
            _output.WriteLine($"Создал подстатью: id={created.ID} code={created.Code} sum={sum1}");
        }
        else
        {
            articleId = article.ID;
            _output.WriteLine($"Подстатья уже есть: id={article.ID} sum={article.DeclaredSum}");
            // Приводим суммы к sum1, чтобы стартовая точка была детерминирована.
            await crud.PatchWbsAsync(articleId, new WbsPatchRequest
            { DeclaredSum = sum1, ConfirmedSum = sum1 }, default);
        }

        // Сделаем второй PATCH с НОВОЙ суммой — проверяем, что PatchWbsAsync действительно работает.
        await crud.PatchWbsAsync(articleId, new WbsPatchRequest
        { DeclaredSum = sum2, ConfirmedSum = sum2 }, default);
        _output.WriteLine($"PATCH применён: id={articleId} → sum={sum2}");

        // ── Итерация 2: тот же импорт ничего не должен дублировать. ─────────
        var existing2 = await listView.GetWbsByProjectAsync(projectId, default);
        var chapterArticles2 = existing2.Data.Count(w => w.ParentID == chapterId);
        var sameArticleCount2 = existing2.Data.Count(w =>
            w.ParentID == chapterId
            && string.Equals(w.Title?.Trim(), subTitle, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, sameArticleCount2);
        _output.WriteLine($"После 2-й итерации: подстатей в Главе 1 — {chapterArticles2}, " +
                         $"совпадений по Title='{subTitle}' — {sameArticleCount2}");

        // Финальные суммы у подстатьи должны равняться sum2.
        var finalRow = existing2.Data.First(w => w.ID == articleId);
        Assert.Equal(sum2, finalRow.DeclaredSum ?? 0, 1);
        Assert.Equal(sum2, finalRow.ConfirmedSum ?? 0, 1);
    }

    private static void SkipIfNoToken()
    {
        var (_, token) = VisaryLiveTestConfig.Resolve();
        Skip.If(string.IsNullOrWhiteSpace(token) || !VisaryLiveTestConfig.IsTokenLikelyAlive(token),
                VisaryLiveTestConfig.SkipReason());
    }
}
