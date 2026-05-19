using System.Text.Json;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Importing.Parsers;
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
/// Покрытие ГФ-потока (Chapter 1 Schedule) <see cref="FinModelImportMapper"/>:
/// • разбор schedule-секции (header-row с датами + статьи Этапа 1) в MappedRow;
/// • маппинг Title → Code через справочник + явный алиас «Прочие затраты» → 1.8;
/// • per-cell сообщения в журнале при отсутствии WBS-статьи в ИСР;
/// • идемпотентный POST/PATCH/skip CostItem по совпадению PlanPeriod.Start.
/// </summary>
public class FinModelChapter1ScheduleTests : IDisposable
{
    private readonly FinModelImportMapper _mapper;
    private readonly VisaryDbContext _dbContext;
    private readonly Mock<ICrudClient> _mockCrud;
    private readonly Mock<IListViewClient> _mockListView;

    private const int SiteId = 4585;
    private const int ProjectId = 4584;
    private const int Wbs11Id = 50001;
    private const int Wbs16Id = 50016;
    private const int Wbs18Id = 50018;

    public FinModelChapter1ScheduleTests()
    {
        _mockCrud = new Mock<ICrudClient>();
        _mockListView = new Mock<IListViewClient>();

        // Минимальные справочники — чтобы Validate-поток параметров не валился раньше.
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
            .UseInMemoryDatabase($"FinModelScheduleTest_{Guid.NewGuid()}")
            .Options;
        _dbContext = new VisaryDbContext(options);
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

    private static ImportContext Ctx() => new(Guid.NewGuid(), ProjectId, SiteId, null);

    // Helper: schedule-строка с Sheet="Inputs (schedule)". Колонки: C — Title (или sentinel),
    // H..K — три квартальные ячейки (для краткости фикстур; в реале их до CU).
    private static ParsedRow ScheduleRow(int rowNum, string title,
        string? h = null, string? i = null, string? j = null, string? k = null)
        => new(rowNum, "Inputs (schedule)", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["C"] = title,
            ["H"] = h ?? "",
            ["I"] = i ?? "",
            ["J"] = j ?? "",
            ["K"] = k ?? "",
        });

    // Header-строка с датами начала кварталов. Маппер ожидает sentinel "__quarters__" в C
    // и ISO-даты в H..K. Помним: парсер сам кладёт sentinel — здесь воспроизводим тот же контракт.
    private static ParsedRow ScheduleHeader(int rowNum,
        string h = "2026-01-01", string i = "2026-04-01", string j = "2026-07-01", string k = "2026-10-01")
        => new(rowNum, "Inputs (schedule)", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["C"] = XlsxParser.ChapterScheduleQuartersSentinel,
            ["H"] = h, ["I"] = i, ["J"] = j, ["K"] = k,
        });

    private static IEnumerable<ParsedRow> WithChapter1Fixture(params ParsedRow[] articles)
    {
        // Минимальный schedule-блок: header + «Этап 1» + статьи.
        // Других KV-строк (params) не нужно — этот тест-набор фокусируется только на ГФ.
        yield return ScheduleHeader(7);
        yield return ScheduleRow(479, "Этап 1");
        foreach (var a in articles) yield return a;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Validate
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_ScheduleRows_ResolvesAllThreeArticlesInChapter1()
    {
        var rows = WithChapter1Fixture(
            ScheduleRow(481, "Затраты на приобретение прав на ЗУ", h: "238000", k: "200000"),
            ScheduleRow(482, "Затраты на изменение ВРИ, комплексное развитие застроенной территории", i: "1111"),
            ScheduleRow(483, "Прочие затраты", j: "2222"))
            .ToList();

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        // 1 quarters + 3 article rows.
        Assert.Equal(4, result.Rows.Count);
        var articles = result.Rows.Where(r =>
            r.MappedValues.RootElement.GetProperty("Kind").GetString() == "schedule_article").ToList();
        Assert.Equal(3, articles.Count);

        // Коды правильные (включая алиас «Прочие затраты» → 1.8).
        var codes = articles.Select(a => a.MappedValues.RootElement.GetProperty("ArticleCode").GetString()).ToList();
        Assert.Contains("1.1.", codes);
        Assert.Contains("1.6.", codes);
        Assert.Contains("1.8.", codes);

        // У 1.1 — два непустых квартала (H+K).
        var a11 = articles.First(a => a.MappedValues.RootElement.GetProperty("ArticleCode").GetString() == "1.1.");
        var quarters11 = a11.MappedValues.RootElement.GetProperty("Quarters").EnumerateArray().ToList();
        Assert.Equal(2, quarters11.Count);
        Assert.Equal(238000d, quarters11[0].GetProperty("AmountThousands").GetDouble());
    }

    [Fact]
    public async Task Validate_ScheduleRows_IgnoresStage2()
    {
        var rows = WithChapter1Fixture(
            ScheduleRow(481, "Затраты на приобретение прав на ЗУ", h: "100"),
            // «Этап 2» обрывает сборку — всё, что ниже, игнорируется.
            ScheduleRow(486, "Этап 2"),
            ScheduleRow(488, "Затраты на приобретение прав на ЗУ", h: "999"),
            ScheduleRow(491, "Прочие затраты", h: "777"))
            .ToList();

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        var articles = result.Rows.Where(r =>
            r.MappedValues.RootElement.GetProperty("Kind").GetString() == "schedule_article").ToList();
        // Только 1 статья Этапа 1 (1.1) с её 100.
        Assert.Single(articles);
        var a = articles[0].MappedValues.RootElement;
        Assert.Equal("1.1.", a.GetProperty("ArticleCode").GetString());
        Assert.Equal(100d, a.GetProperty("Quarters").EnumerateArray().First().GetProperty("AmountThousands").GetDouble());
    }

    [Fact]
    public async Task Validate_ScheduleRows_SkipsArticleWithoutAmounts()
    {
        // Title матчится, но все квартальные ячейки пусты → MappedRow не эмитим (нечего применять).
        var rows = WithChapter1Fixture(
            ScheduleRow(481, "Затраты на приобретение прав на ЗУ" /* без сумм */))
            .ToList();

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        var articles = result.Rows.Where(r =>
            r.MappedValues.RootElement.GetProperty("Kind").GetString() == "schedule_article").ToList();
        Assert.Empty(articles);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Apply
    // ──────────────────────────────────────────────────────────────────────

    private void SetupWbsBySite(params (string Code, int Id)[] articles)
    {
        var wbsList = articles
            .Select(a => new WbsRaw { ID = a.Id, Code = a.Code, Title = $"WBS {a.Code}" })
            .ToList();
        _mockListView.Setup(c => c.GetWbsBySiteAsync(SiteId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<WbsRaw> { Data = wbsList, Total = wbsList.Count });
    }

    private void SetupExistingCostItems(int wbsId, params CostItemRaw[] items)
    {
        _mockListView.Setup(c => c.GetCostItemsByWbsAsync(wbsId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<CostItemRaw> { Data = items.ToList(), Total = items.Length });
    }

    [Fact]
    public async Task Apply_ScheduleArticle_PostsCostItemWhenNoExisting()
    {
        SetupWbsBySite(("1.1.", Wbs11Id));
        SetupExistingCostItems(Wbs11Id);
        _mockCrud.Setup(c => c.CreateCostItemAsync(It.IsAny<CostItemCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostItemRaw { ID = 999, WBSID = Wbs11Id });

        var rows = await ValidateAndCollect(
            ScheduleRow(481, "Затраты на приобретение прав на ЗУ", h: "238000"));

        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, rows, default);

        // 238000 тыс. ₽ → 238 000 000 ₽ ровно, PlanPeriod = 2026-01-01..2026-03-31.
        _mockCrud.Verify(c => c.CreateCostItemAsync(
            It.Is<CostItemCreateRequest>(r =>
                r.WBSID == Wbs11Id
                && r.PlanSum == 238_000_000.0
                && r.Status == CostItemStatus.Plan
                && r.PlanPeriod!.Start.Date == new DateTime(2026, 1, 1)
                && r.PlanPeriod!.End.Date == new DateTime(2026, 3, 31)),
            It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.True(apply.AppliedCount >= 1);
        // RowAction содержит сообщение «создано».
        var rowAction = Assert.Single(apply.RowActions!);
        Assert.Equal(481, rowAction.SourceRowNumber);
        Assert.Contains(rowAction.Actions, a => a.Contains("создано", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Apply_ScheduleArticle_PatchesExistingOnAmountChange()
    {
        SetupWbsBySite(("1.1.", Wbs11Id));
        SetupExistingCostItems(Wbs11Id,
            new CostItemRaw
            {
                ID = 700, WBSID = Wbs11Id, PlanSum = 100_000_000.0, // старое
                PlanPeriod = new CostItemPeriod
                {
                    Start = new DateTime(2026, 1, 1), End = new DateTime(2026, 3, 31),
                },
            });

        var rows = await ValidateAndCollect(
            ScheduleRow(481, "Затраты на приобретение прав на ЗУ", h: "238000"));

        await _mapper.ApplyAsync(Ctx(), _dbContext, rows, default);

        _mockCrud.Verify(c => c.PatchCostItemAsync(
            700,
            It.Is<CostItemPatchRequest>(r => r.PlanSum == 238_000_000.0),
            It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCrud.Verify(c => c.CreateCostItemAsync(It.IsAny<CostItemCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Apply_ScheduleArticle_SkipsWhenAmountUnchanged()
    {
        SetupWbsBySite(("1.1.", Wbs11Id));
        SetupExistingCostItems(Wbs11Id,
            new CostItemRaw
            {
                ID = 700, WBSID = Wbs11Id, PlanSum = 238_000_000.0, // уже та же
                PlanPeriod = new CostItemPeriod
                {
                    Start = new DateTime(2026, 1, 1), End = new DateTime(2026, 3, 31),
                },
            });

        var rows = await ValidateAndCollect(
            ScheduleRow(481, "Затраты на приобретение прав на ЗУ", h: "238000"));

        await _mapper.ApplyAsync(Ctx(), _dbContext, rows, default);

        _mockCrud.Verify(c => c.PatchCostItemAsync(It.IsAny<int>(), It.IsAny<CostItemPatchRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _mockCrud.Verify(c => c.CreateCostItemAsync(It.IsAny<CostItemCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Apply_ScheduleArticle_WbsMissing_EmitsPerCellMessages()
    {
        // 1.1 есть, 1.8 нет — для 1.8 ожидаем per-cell сообщение в журнале.
        SetupWbsBySite(("1.1.", Wbs11Id));
        SetupExistingCostItems(Wbs11Id);
        _mockCrud.Setup(c => c.CreateCostItemAsync(It.IsAny<CostItemCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostItemRaw { ID = 999 });

        var rows = await ValidateAndCollect(
            ScheduleRow(481, "Затраты на приобретение прав на ЗУ", h: "100"),
            ScheduleRow(483, "Прочие затраты", j: "2222", k: "3333"));

        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, rows, default);

        // Для 1.1 — POST вызван.
        _mockCrud.Verify(c => c.CreateCostItemAsync(
            It.Is<CostItemCreateRequest>(r => r.WBSID == Wbs11Id),
            It.IsAny<CancellationToken>()), Times.Once);

        // Для 1.8 — POST НЕ вызван, в RowActions — два per-cell сообщения в нужном формате.
        var actionsFor483 = apply.RowActions!.Single(a => a.SourceRowNumber == 483);
        Assert.Equal(2, actionsFor483.Actions.Count);
        Assert.Contains(actionsFor483.Actions, a =>
            a.Contains("J483", StringComparison.Ordinal)
            && a.Contains("статья 1.8", StringComparison.Ordinal)
            && a.Contains("отсутствует в ИСР", StringComparison.Ordinal));
        Assert.Contains(actionsFor483.Actions, a =>
            a.Contains("K483", StringComparison.Ordinal));
    }

    /// <summary>
    /// Прогоняет фикстуру через Validate и возвращает все mapped-строки (включая
    /// quarters-header) — Apply ожидает их все вместе.
    /// </summary>
    private async Task<List<MappedRow>> ValidateAndCollect(params ParsedRow[] articles)
    {
        var rows = WithChapter1Fixture(articles).ToList();
        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);
        return result.Rows.ToList();
    }
}
