using KiloImportService.Api.Budget;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Mapping;
using KiloImportService.Api.Domain.Mapping.Budget;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.FileStorage;
using Visary.Api.ListView;
using Xunit;

namespace KiloImportService.Api.Tests.Mapping;

/// <summary>
/// Покрытие бюджетного потока FinModelImportMapper:
/// • парсинг секции «Себестоимость» (фикстура — ParsedRow с Sheet "Inputs (budget)");
/// • нормализация Title → Code через эталонный справочник;
/// • суммирование по этапам;
/// • идемпотентный apply (find/create/patch) c проверкой PatchWbsAsync.
/// </summary>
public class FinModelBudgetTests : IDisposable
{
    private readonly FinModelImportMapper _mapper;
    private readonly VisaryDbContext _dbContext;
    private readonly Mock<ICrudClient> _mockCrud;
    private readonly Mock<IListViewClient> _mockListView;
    private readonly Mock<IBudgetVisaryUploader> _mockBudgetUploader;
    private readonly ServiceProvider _serviceProvider;

    private const int SiteId = 4585;
    private const int ProjectId = 4584;

    public FinModelBudgetTests()
    {
        _mockCrud = new Mock<ICrudClient>();
        _mockListView = new Mock<IListViewClient>();

        // Бюджетные тесты не должны падать на параметрическом потоке (если Validate
        // вызывается с обоими типами строк) — отдадим минимальные справочники.
        _mockListView.Setup(c => c.ListFinishingMaterialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FinishingMaterialRaw>
            {
                Data = [new FinishingMaterialRaw { ID = 1, Title = "Чистовая" }],
                Total = 1,
            });
        _mockListView.Setup(c => c.ListEstateClassesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<EstateClassRaw>
            {
                Data = [new EstateClassRaw { ID = 1, Title = "Стандарт" }],
                Total = 1,
            });

        // Pre-check 1 (doc 109): ApplyAsync вызывает GetWbsBySiteAsync перед заливкой
        // XLSX. По умолчанию — ИСР пуста (бюджет должен залиться). Тесты, которые
        // проверяют именно skip-by-existing-wbs, переопределяют Setup сами.
        _mockListView.Setup(c => c.GetWbsBySiteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<WbsRaw> { Data = [], Total = 0 });

        var budgetRef = new BudgetReferenceProvider(NullLogger<BudgetReferenceProvider>.Instance);

        // Mock IBudgetVisaryUploader — он зарегистрирован в реальном DI как Scoped.
        // Для теста ApplyAsync_Budget_CountsRowsWithoutCallingWbsCrud по умолчанию
        // отдаём успех — каждый тест может перенастроить _mockBudgetUploader при необходимости.
        _mockBudgetUploader = new Mock<IBudgetVisaryUploader>();
        _mockBudgetUploader
            .Setup(u => u.UploadAndWaitAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BudgetVisaryUploadAndWaitResult(
                Upload: new BudgetVisaryUploadResult(
                    FileStorageItemId: 1, TypedImportWbsId: 999, FileName: "stub.xlsx"),
                Success: true, TimedOut: false,
                FinalStatus: "Закончен успешно", CountErrors: 0, CountWarnings: 0));

        // Собираем мини-DI чтобы FinModelImportMapper мог получить IBudgetVisaryUploader
        // через IServiceScopeFactory (тот же паттерн, что в production).
        var services = new ServiceCollection();
        services.AddSingleton(_mockBudgetUploader.Object);
        _serviceProvider = services.BuildServiceProvider();
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();

        _mapper = new FinModelImportMapper(
            NullLogger<FinModelImportMapper>.Instance,
            _mockCrud.Object,
            _mockListView.Object,
            budgetRef,
            scopeFactory);

        var options = new DbContextOptionsBuilder<VisaryDbContext>()
            .UseInMemoryDatabase($"FinModelBudgetTest_{Guid.NewGuid()}")
            .Options;
        _dbContext = new VisaryDbContext(options);

        // Site → Project FK мы используем из локального зеркала, чтобы маппер мог
        // зарезолвить projectId без передачи в ImportContext.
        _dbContext.ConstructionSites.Add(new ConstructionSite
        {
            Id = SiteId,
            Title = "Тестовый объект",
            ConstructionProjectId = ProjectId,
            Hidden = false,
        });
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        _serviceProvider?.Dispose();
    }

    private static ImportContext Ctx(int? siteId = SiteId, int? projectId = null)
        => new(Guid.NewGuid(), projectId, siteId, null);

    // Helper: бюджетная ParsedRow с Sheet="Inputs (budget)" и cells по столбцам A..G.
    private static ParsedRow BudgetRow(int rowNum, string? c = null, string? e = null)
    {
        var cells = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A"] = "", ["B"] = "", ["C"] = c ?? "",
            ["D"] = "", ["E"] = e ?? "", ["F"] = "", ["G"] = "",
        };
        return new ParsedRow(rowNum, "Inputs (budget)", cells);
    }

    [Fact]
    public async Task ValidateAsync_BudgetRows_IgnoresRepeatsAfterChapterTotal()
    {
        // В файле финмодели после «Итого» главы 1 идут повторы тех же статей —
        // «Этап 2» или фактические значения (та же таблица для другого среза). Учитываем
        // только данные до «Итого»: одно значение на статью в главе (см. ТЗ от 2026-05-14:
        // 1.8 «Прочие затраты на улучшения и содержание ЗУ» = E483, а не сумма всех вхождений).
        //
        // Параллельно ValidateBudget эмитит «chapter-direct» итог главы как отдельную
        // MappedRow с ArticleCode == ChapterCode (для override-а агрегата в XLSX-exporter-е,
        // см. doc 78 v1.3). Проверяем обе: article (1.1, sum=300) и chapter-direct (sum=300).
        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(479, c: "Этап 1"),
            BudgetRow(481, c: "Затраты на приобретение прав на ЗУ", e: "300"),
            BudgetRow(484, c: "Итого", e: "300"),
            BudgetRow(486, c: "Этап 2"),
            BudgetRow(488, c: "Затраты на приобретение прав на ЗУ", e: "138"),
            BudgetRow(491, c: "Итого:", e: "138"),
        };

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.True(r.IsValid));

        var article = result.Rows.Single(r =>
            r.MappedValues.RootElement.GetProperty("ArticleCode").GetString() == "1.1.");
        var articleRoot = article.MappedValues.RootElement;
        Assert.Equal("budget", articleRoot.GetProperty("Kind").GetString());
        Assert.Equal("1.", articleRoot.GetProperty("ChapterCode").GetString());
        // 300, а не 438 — повтор после «Итого» проигнорирован.
        Assert.Equal(300.0, articleRoot.GetProperty("DeclaredSum").GetDouble(), precision: 4);
        Assert.Equal(300.0, articleRoot.GetProperty("ConfirmedSum").GetDouble(), precision: 4);

        var chapterDirect = result.Rows.Single(r =>
            r.MappedValues.RootElement.GetProperty("ArticleCode").GetString() == "1.");
        Assert.Equal("1.", chapterDirect.MappedValues.RootElement.GetProperty("ChapterCode").GetString());
        Assert.Equal(300.0,
            chapterDirect.MappedValues.RootElement.GetProperty("DeclaredSum").GetDouble(), precision: 4);
    }

    [Fact]
    public async Task ValidateAsync_BudgetRows_ResolvesShortTitleAgainstLongerReference()
    {
        // Короткая форма «Прочие затраты» в файле ↔ длинная «Прочие затраты на улучшения
        // и содержание ЗУ» (1.8) в справочнике. Резолвится через reverse-prefix в пределах
        // текущей главы (см. ТЗ от 2026-05-14).
        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(479, c: "Этап 1"),
            BudgetRow(483, c: "Прочие затраты", e: "2222"),
            BudgetRow(484, c: "Итого", e: "2222"),
        };

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        // 2 строки: article (1.8) + chapter-direct итог (Code "1.", sum 2222).
        Assert.Equal(2, result.Rows.Count);

        var article = result.Rows.Single(r =>
            r.MappedValues.RootElement.GetProperty("ArticleCode").GetString() == "1.8.");
        var root = article.MappedValues.RootElement;
        Assert.Equal("Прочие затраты на улучшения и содержание ЗУ",
            root.GetProperty("ArticleTitle").GetString());
        Assert.Equal(2222.0, root.GetProperty("DeclaredSum").GetDouble(), precision: 4);
    }

    [Fact]
    public async Task ValidateAsync_BudgetRows_UnknownTitle_SkippedSilently()
    {
        // Title, которого нет ни в справочнике, ни как reverse-prefix потомков текущей
        // главы → строка молча пропускается; в mapped — ничего, file-level errors нет.
        // (Используем заведомо «бредовый» Title, который reverse-prefix не покроет.)
        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(481, c: "Хитрая статья без сопоставления в справочнике", e: "100"),
        };

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        Assert.Empty(result.Rows);
        Assert.Empty(result.FileLevelErrors);
    }

    [Fact]
    public async Task ApplyAsync_Budget_CountsRowsWithoutCallingWbsCrud()
    {
        // CRUD-путь записи бюджета в Visary OFF (см. doc 78 v1.3): дерево WBS через
        // POST /api/visary/crud/wbs воспроизвести устойчиво не получилось, вместо этого
        // мапп emit-ит mapped budget rows, а потом BudgetXlsxExporter отдаёт XLSX по
        // эталону «Бюджет_А4.1» для ручной загрузки в Visary.
        //
        // ApplyAsync для бюджета должен:
        //   • НЕ вызывать GetWbsByProjectAsync / CreateWbsAsync / PatchWbsAsync;
        //   • вернуть AppliedCount = число mapped budget rows (нужно UI-у, чтобы сессия
        //     помечалась Applied и появилась кнопка «Скачать XLSX»).
        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(481, c: "Затраты на приобретение прав на ЗУ", e: "438000"),
            BudgetRow(484, c: "Итого", e: "438000"),
        };

        var validation = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);
        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        Assert.True(apply.AppliedCount > 0,
            "AppliedCount должен быть положительным — иначе сессия в UI не считается Applied.");
        Assert.Empty(apply.Errors);

        // Никаких походов в Visary WBS — путь выключен.
        _mockCrud.Verify(c => c.CreateWbsAsync(It.IsAny<WbsCreateRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _mockCrud.Verify(c => c.PatchWbsAsync(It.IsAny<int>(), It.IsAny<WbsPatchRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _mockListView.Verify(c => c.GetWbsByProjectAsync(It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyAsync_Budget_SkipsUploadWhenSiteAlreadyHasWbs()
    {
        // Pre-check 1 (doc 109): listview/wbs/onetomany/ConstructionSite вернул
        // непустой список → bypass залива XLSX в Visary. budgetUploadOk=true,
        // т.е. ГФ ниже всё равно может запуститься. В errors — info-сообщение
        // budget_upload_skipped_wbs_exists.
        _mockListView.Setup(c => c.GetWbsBySiteAsync(SiteId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<WbsRaw>
            {
                Data = [new WbsRaw { ID = 100, Code = "1.", Title = "Глава 1" }],
                Total = 1,
            });

        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(481, c: "Затраты на приобретение прав на ЗУ", e: "438000"),
            BudgetRow(484, c: "Итого", e: "438000"),
        };

        var validation = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);
        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        // Uploader НЕ должен вызываться вообще.
        _mockBudgetUploader.Verify(u => u.UploadAndWaitAsync(
            It.IsAny<Guid>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Сообщение об пропуске присутствует.
        Assert.Contains(apply.Errors, e => e.Code == "budget_upload_skipped_wbs_exists");
    }

    [Fact]
    public async Task ApplyAsync_Budget_UploadsWhenSiteHasNoWbs()
    {
        // Дефолтный setup в конструкторе: GetWbsBySiteAsync → пусто. Заливка должна
        // пройти как раньше.
        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(481, c: "Затраты на приобретение прав на ЗУ", e: "438000"),
            BudgetRow(484, c: "Итого", e: "438000"),
        };

        var validation = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);
        await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        _mockBudgetUploader.Verify(u => u.UploadAndWaitAsync(
            It.IsAny<Guid>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyAsync_Budget_SkipsUploadWhenWbsPrecheckFails()
    {
        // Если listview/wbs упал — заливать XLSX небезопасно (можем породить
        // дубликат ИСР). Pre-check возвращает null → upload + ГФ пропущены.
        _mockListView.Setup(c => c.GetWbsBySiteAsync(SiteId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("listview/wbs is down"));

        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(481, c: "Затраты на приобретение прав на ЗУ", e: "438000"),
            BudgetRow(484, c: "Итого", e: "438000"),
        };

        var validation = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);
        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        _mockBudgetUploader.Verify(u => u.UploadAndWaitAsync(
            It.IsAny<Guid>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(apply.Errors, e => e.Code == "budget_upload_precheck_failed");
    }

    [Fact]
    public void BudgetReferenceProvider_LoadsExpectedEntries()
    {
        // Эталонный справочник должен содержать главы 1, 2, 3 и подстатью 1.1.
        var refProvider = new BudgetReferenceProvider(NullLogger<BudgetReferenceProvider>.Instance);

        var chapter1 = refProvider.FindByCode("1.");
        Assert.NotNull(chapter1);
        Assert.True(chapter1!.IsChapter);
        Assert.StartsWith("Глава 1", chapter1.Title);

        var article11 = refProvider.FindByTitle("Затраты на приобретение прав на ЗУ");
        Assert.NotNull(article11);
        Assert.Equal("1.1.", article11!.Code);
        Assert.Equal("1.", article11.ParentCode);
        Assert.False(article11.IsChapter);

        // Нормализация Title (newlines + лишние пробелы).
        var article11Normalized = refProvider.FindByTitle("  Затраты   на\nприобретение   прав\tна  ЗУ  ");
        Assert.NotNull(article11Normalized);
        Assert.Equal("1.1.", article11Normalized!.Code);
    }
}
