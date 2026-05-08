using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Mapping;
using KiloImportService.Api.Domain.Mapping.Budget;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Visary.Api.CRUD;
using Visary.Api.Dto;
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

        var budgetRef = new BudgetReferenceProvider(NullLogger<BudgetReferenceProvider>.Instance);
        _mapper = new FinModelImportMapper(
            NullLogger<FinModelImportMapper>.Instance,
            _mockCrud.Object,
            _mockListView.Object,
            budgetRef);

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

    public void Dispose() => _dbContext?.Dispose();

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
    public async Task ValidateAsync_BudgetRowsAggregateAcrossStages()
    {
        // Глава 1 + одна и та же подстатья на двух «этапах» (E=300, E=138) → сумма 438.
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

        // Должна получиться ровно одна валидная mapped-строка: подстатья «Затраты на приобретение прав на ЗУ» (1.1.).
        Assert.Single(result.Rows);
        Assert.True(result.Rows[0].IsValid);

        var root = result.Rows[0].MappedValues.RootElement;
        Assert.Equal("budget", root.GetProperty("Kind").GetString());
        Assert.Equal("1.", root.GetProperty("ChapterCode").GetString());
        Assert.Equal("1.1.", root.GetProperty("ArticleCode").GetString());
        Assert.Equal("Затраты на приобретение прав на ЗУ", root.GetProperty("ArticleTitle").GetString());
        Assert.Equal(438.0, root.GetProperty("DeclaredSum").GetDouble(), precision: 4);
        Assert.Equal(438.0, root.GetProperty("ConfirmedSum").GetDouble(), precision: 4);
    }

    [Fact]
    public async Task ValidateAsync_BudgetRows_UnknownTitle_SkippedSilently()
    {
        // «Прочие затраты» отсутствует в эталонном справочнике (Глава 1 не имеет такой
        // статьи) → строка молча пропускается; валидных нет, file-level errors нет.
        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(481, c: "Прочие затраты", e: "100"),
        };

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        Assert.Empty(result.Rows);
        Assert.Empty(result.FileLevelErrors);
    }

    [Fact]
    public async Task ApplyAsync_Budget_CreatesChapterAndArticle_WhenNothingExists()
    {
        // Visary возвращает пустой WBS → маппер должен создать главу + подстатью.
        _mockListView.Setup(c => c.GetWbsByProjectAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<WbsRaw> { Data = [], Total = 0 });

        // Сервер возвращает разные ID для главы и подстатьи (по порядку вызовов).
        _mockCrud
            .SetupSequence(c => c.CreateWbsAsync(It.IsAny<WbsCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WbsRaw { ID = 9001, Code = "1.", Title = "Глава 1...", ParentID = null })
            .ReturnsAsync(new WbsRaw { ID = 9002, Code = "1.1.", Title = "Затраты...", ParentID = 9001 });

        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(481, c: "Затраты на приобретение прав на ЗУ", e: "438000"),
        };

        var validation = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);
        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        Assert.Equal(1, apply.AppliedCount);
        Assert.Empty(apply.Errors);

        // Глава: ParentID == null, привязка к проекту.
        _mockCrud.Verify(c => c.CreateWbsAsync(
            It.Is<WbsCreateRequest>(r =>
                r.ProjectID == ProjectId && r.ParentID == null
                && r.Title == "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Подстатья: ParentID == ID главы, ConstructionSiteID = SiteId, суммы переданы.
        _mockCrud.Verify(c => c.CreateWbsAsync(
            It.Is<WbsCreateRequest>(r =>
                r.ParentID == 9001 && r.ConstructionSiteID == SiteId
                && r.Title == "Затраты на приобретение прав на ЗУ"
                && r.DeclaredSum == 438000 && r.ConfirmedSum == 438000),
            It.IsAny<CancellationToken>()), Times.Once);

        _mockCrud.Verify(c => c.PatchWbsAsync(It.IsAny<int>(), It.IsAny<WbsPatchRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyAsync_Budget_PatchesArticle_WhenExistsWithDifferentSums()
    {
        // Глава и подстатья УЖЕ существуют в Visary. Импорт повторный — суммы поменялись.
        // Идемпотентность: маппер не создаёт ничего, а PATCH-ает суммы у существующей подстатьи.
        var existing = new ListViewResponse<WbsRaw>
        {
            Data =
            [
                new WbsRaw
                {
                    ID = 8001, Code = "1.", ParentID = null,
                    Title = "Глава 1. Стоимость земельного участка и расходы по его содержанию",
                },
                new WbsRaw
                {
                    ID = 8002, Code = "1.1.", ParentID = 8001,
                    Title = "Затраты на приобретение прав на ЗУ",
                    DeclaredSum = 100, ConfirmedSum = 100,
                },
            ],
            Total = 2,
        };
        _mockListView.Setup(c => c.GetWbsByProjectAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _mockCrud.Setup(c => c.PatchWbsAsync(It.IsAny<int>(), It.IsAny<WbsPatchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(481, c: "Затраты на приобретение прав на ЗУ", e: "500"),
        };

        var validation = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);
        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        Assert.Equal(1, apply.AppliedCount);
        Assert.Empty(apply.Errors);

        // Никакого Create — ни главы, ни статьи.
        _mockCrud.Verify(c => c.CreateWbsAsync(It.IsAny<WbsCreateRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        // Patch на 8002 с новыми суммами.
        _mockCrud.Verify(c => c.PatchWbsAsync(8002,
            It.Is<WbsPatchRequest>(r => r.DeclaredSum == 500 && r.ConfirmedSum == 500),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyAsync_Budget_IsNoOp_WhenSumsAlreadyMatch()
    {
        // Если в Visary уже стоит та же сумма — мы не делаем PATCH (избегаем фантомных
        // обновлений и лишней нагрузки). AppliedCount = 0.
        _mockListView.Setup(c => c.GetWbsByProjectAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<WbsRaw>
            {
                Data =
                [
                    new WbsRaw
                    {
                        ID = 7001, Code = "1.", ParentID = null,
                        Title = "Глава 1. Стоимость земельного участка и расходы по его содержанию",
                    },
                    new WbsRaw
                    {
                        ID = 7002, Code = "1.1.", ParentID = 7001,
                        Title = "Затраты на приобретение прав на ЗУ",
                        DeclaredSum = 438000, ConfirmedSum = 438000,
                    },
                ],
                Total = 2,
            });

        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(481, c: "Затраты на приобретение прав на ЗУ", e: "438000"),
        };

        var validation = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);
        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        Assert.Equal(0, apply.AppliedCount);
        Assert.Empty(apply.Errors);
        _mockCrud.Verify(c => c.CreateWbsAsync(It.IsAny<WbsCreateRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _mockCrud.Verify(c => c.PatchWbsAsync(It.IsAny<int>(), It.IsAny<WbsPatchRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyAsync_Budget_ReusesExistingChapter_AndCreatesArticle()
    {
        // Глава уже есть, подстатьи — нет. Маппер должен взять ID существующей главы
        // (по Code "1.") и создать только подстатью.
        _mockListView.Setup(c => c.GetWbsByProjectAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<WbsRaw>
            {
                Data =
                [
                    new WbsRaw
                    {
                        ID = 6001, Code = "1.", ParentID = null,
                        Title = "Глава 1. Стоимость земельного участка и расходы по его содержанию",
                    },
                ],
                Total = 1,
            });

        _mockCrud.Setup(c => c.CreateWbsAsync(It.IsAny<WbsCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WbsRaw { ID = 6002, Code = "1.1.", ParentID = 6001 });

        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(481, c: "Затраты на приобретение прав на ЗУ", e: "200"),
        };

        var validation = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);
        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        Assert.Equal(1, apply.AppliedCount);
        // CreateWbsAsync вызывается ровно один раз — на подстатью, c ParentID = ID существующей главы.
        _mockCrud.Verify(c => c.CreateWbsAsync(
            It.Is<WbsCreateRequest>(r => r.ParentID == 6001 && r.Title == "Затраты на приобретение прав на ЗУ"),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockCrud.Verify(c => c.CreateWbsAsync(
            It.Is<WbsCreateRequest>(r => r.ParentID == null),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyAsync_Budget_NoProjectId_ReportsError()
    {
        // Site без ConstructionProjectId в локальном зеркале и без передачи projectId
        // в ImportContext → бюджет применить невозможно.
        const int orphanSiteId = 999;
        _dbContext.ConstructionSites.Add(new ConstructionSite
        {
            Id = orphanSiteId,
            Title = "Без проекта",
            ConstructionProjectId = null,
            Hidden = false,
        });
        _dbContext.SaveChanges();

        var rows = new[]
        {
            BudgetRow(475, c: "Глава 1. Стоимость земельного участка и расходы по его содержанию"),
            BudgetRow(481, c: "Затраты на приобретение прав на ЗУ", e: "200"),
        };

        var validation = await _mapper.ValidateAsync(Ctx(siteId: orphanSiteId), rows, _dbContext, default);
        var apply = await _mapper.ApplyAsync(Ctx(siteId: orphanSiteId), _dbContext, validation.Rows, default);

        Assert.Equal(0, apply.AppliedCount);
        Assert.Contains(apply.Errors, e => e.ErrorCode == "project_required");
        _mockCrud.Verify(c => c.CreateWbsAsync(It.IsAny<WbsCreateRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
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
