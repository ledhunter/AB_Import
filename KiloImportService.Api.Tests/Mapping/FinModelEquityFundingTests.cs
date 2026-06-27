using ClosedXML.Excel;
using KiloImportService.Api.Budget;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Importing.Parsers;
using KiloImportService.Api.Domain.Mapping;
using KiloImportService.Api.Domain.Mapping.Budget;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.ListView;
using Xunit;

namespace KiloImportService.Api.Tests.Mapping;

/// <summary>
/// Покрытие потока «Вложение собственных средств» (раздел «Финансирование: Этап 1»
/// листа Inputs) импорта Финмодели: парсер <see cref="XlsxParser"/> с
/// <see cref="EquityFundingHint"/>, <c>ValidateEquityFunding</c>, и каскад
/// <c>EnsureEquityFundingInputDataAsync</c> создаёт inputdata с fmcode=604.
/// См. doc 146.
/// </summary>
public class FinModelEquityFundingTests : IDisposable
{
    private const int ProjectId = 4584;
    private const int SiteId = 7890;
    private const int FmModelId = 48;
    private const int VersionId = 217;
    private const int CodeApartmentId = 20;
    private const int CodeEquityId = 604_777;

    private readonly FinModelImportMapper _mapper;
    private readonly VisaryDbContext _dbContext;
    private readonly Mock<ICrudClient> _mockCrud;
    private readonly Mock<IListViewClient> _mockListView;
    private readonly TestFileStorage _fileStorage;
    private readonly ServiceProvider _serviceProvider;
    private int _nextInputDataId = 50_000;

    public FinModelEquityFundingTests()
    {
        _mockCrud = new Mock<ICrudClient>();
        _mockListView = new Mock<IListViewClient>();

        // Базовые pre-checks (как в FinModelInputDataTests).
        _mockListView.Setup(c => c.ListFinishingMaterialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FinishingMaterialRaw> { Data = [], Total = 0 });
        _mockListView.Setup(c => c.ListEstateClassesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<EstateClassRaw> { Data = [], Total = 0 });
        _mockListView.Setup(c => c.GetWbsBySiteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<WbsRaw> { Data = [], Total = 0 });
        _mockCrud.Setup(c => c.GetProjectByIdFullAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConstructionProjectFull { ID = ProjectId, Title = "Тест" });

        _mockListView
            .Setup(c => c.FindFmModelsAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmModelRaw> { Data = [], Total = 0 });
        _mockCrud
            .Setup(c => c.CreateFmModelAsync(It.IsAny<FmModelCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FmModelCreateRequest req, CancellationToken _) => new FmModelRaw
            {
                ID = FmModelId, Title = req.Title,
                ABProjectID = req.ABProjectID, ABConstructionSiteID = req.ABConstructionSiteID,
                PeriodStart = req.PeriodStart, PeriodEnd = req.PeriodEnd,
            });

        // Plan-fmcode: только Apartment, чтобы избежать missing-code-ошибок на
        // фикстуре, где План отсутствует. Остальные — пустой ответ.
        _mockListView
            .Setup(c => c.FindFmCodeByCodeAsync(
                FinModelImportMapper.FmCodeApartment, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmCodeRaw>
            {
                Data = [new FmCodeRaw { ID = CodeApartmentId, Code = FinModelImportMapper.FmCodeApartment, Title = "010 Продажа квартиры (план)" }],
                Total = 1,
            });
        _mockListView
            .Setup(c => c.FindFmCodeByCodeAsync(
                It.Is<string>(s => s != FinModelImportMapper.FmCodeApartment
                                && s != FinModelImportMapper.FmCodeEquityInvestment),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmCodeRaw> { Data = [], Total = 0 });

        // Equity-fmcode: успешный резолв (по умолчанию). Тесты-фейлы переопределяют.
        _mockListView
            .Setup(c => c.FindFmCodeByCodeAsync(
                FinModelImportMapper.FmCodeEquityInvestment, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmCodeRaw>
            {
                Data = [new FmCodeRaw
                {
                    ID = CodeEquityId,
                    Code = FinModelImportMapper.FmCodeEquityInvestment,
                    Title = "604 Вложение собственных средств",
                }],
                Total = 1,
            });

        _mockListView
            .Setup(c => c.GetFmModelVersionsByModelAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmModelVersionRaw> { Data = [], Total = 0 });
        _mockCrud
            .Setup(c => c.CreateFmModelVersionAsync(
                It.IsAny<FmModelVersionCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FmModelVersionCreateRequest req, CancellationToken _) => new FmModelVersionRaw
            {
                ID = VersionId, FMModelID = req.FMModelID, Title = req.Title,
            });

        _mockListView
            .Setup(c => c.GetInputDataByVersionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<InputDataRaw> { Data = [], Total = 0 });

        _mockCrud
            .Setup(c => c.CreateInputDataAsync(It.IsAny<InputDataCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InputDataCreateRequest req, CancellationToken _) => new InputDataRaw
            {
                ID = Interlocked.Increment(ref _nextInputDataId),
                FMModelVersionID = req.FMModelVersionID, FMPeriod = req.FMPeriod,
                Code = req.Code, Summ = req.Summ, Amount = req.Amount, Cost = req.Cost,
                Percent = req.Percent,
            });

        _mockCrud
            .Setup(c => c.LinkInputDataToVersionAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _fileStorage = new TestFileStorage();
        var budgetRef = new BudgetReferenceProvider(NullLogger<BudgetReferenceProvider>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IBudgetVisaryUploader>());
        _serviceProvider = services.BuildServiceProvider();

        _mapper = new FinModelImportMapper(
            NullLogger<FinModelImportMapper>.Instance,
            _mockCrud.Object, _mockListView.Object, budgetRef,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _fileStorage);

        var options = new DbContextOptionsBuilder<VisaryDbContext>()
            .UseInMemoryDatabase($"FinModelEquityFundingTest_{Guid.NewGuid()}")
            .Options;
        _dbContext = new VisaryDbContext(options);
        _dbContext.ConstructionSites.Add(new ConstructionSite
        {
            Id = SiteId, Title = "Тестовый объект", ConstructionProjectId = ProjectId,
        });
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    // ─────────── Unit-тесты helper-функции ───────────

    [Theory]
    [InlineData(null, 1d)]
    [InlineData("", 1d)]
    [InlineData("руб.", 1d)]
    [InlineData("руб", 1d)]
    [InlineData("тыс. руб.", 1000d)]
    [InlineData("Тыс. руб.", 1000d)]
    [InlineData("ТЫС.РУБ.", 1000d)]
    [InlineData("тыс руб", 1000d)]
    [InlineData("млн. руб.", 1_000_000d)]
    [InlineData("млн руб.", 1_000_000d)]
    [InlineData("млрд руб.", 1_000_000_000d)]
    [InlineData("МЛРД. руб.", 1_000_000_000d)]
    public void ResolveUnitMultiplier_KnownUnits_ReturnsExpectedScale(string? unit, double expected)
    {
        Assert.Equal(expected, FinModelImportMapper.ResolveUnitMultiplier(unit));
    }

    [Fact]
    public void ResolveUnitMultiplier_UnknownUnit_DefaultsToOne()
    {
        Assert.Equal(1d, FinModelImportMapper.ResolveUnitMultiplier("ракушки"));
    }

    // ─────────── XlsxParser — извлечение equity-funding-секции ───────────

    [Fact]
    public async Task XlsxParser_EquityFundingHint_EmitsHeaderAndDataRows()
    {
        // Эталонная раскладка под фрагмент Inputs основного файла:
        // • строка 7 — даты начала кварталов (Q1..Q4 2026) в H..K;
        // • строка 10 — маркер «Финансирование: Этап 1» в C;
        // • строка 12 — заголовок «Вложение собственных средств» (без единицы) — должна быть проигнорирована;
        // • строка 14 — собственно строка данных: «Вложение собственных средств» в C, «тыс. руб.» в D,
        //   значения в H..K (Q1=238000, Q3=100000).
        var bytes = BuildInputsXlsx(unitText: "тыс. руб.", q1: 238_000, q2: 0, q3: 100_000, q4: 0);
        using var stream = new MemoryStream(bytes);

        var layout = new KeyValueVertical(
            SheetName: "Inputs", KeyColumn: "C", ValueStartColumn: "H",
            EquityFunding: new EquityFundingHint(
                MarkerColumn: "C",
                StartMarker: "Финансирование: Этап 1",
                KeyName: "Вложение собственных средств",
                UnitColumn: "D",
                QuarterHeaderRow: 7,
                FirstQuarterColumn: "H",
                LastQuarterColumn: "K"));

        var parser = new XlsxParser();
        var result = await parser.ParseAsync(stream, layout, default);

        // 2 строки с суффиксом "(equity-funding)" — header + data.
        var equityRows = result.Rows.Where(r => r.Sheet?.EndsWith("(equity-funding)") == true).ToList();
        Assert.Equal(2, equityRows.Count);

        var header = equityRows.Single(r => r.Cells["C"] == XlsxParser.EquityFundingQuartersSentinel);
        Assert.Equal(7, header.SourceRowNumber);
        Assert.Equal("2026-01-01", header.Cells["H"]);
        Assert.Equal("2026-04-01", header.Cells["I"]);
        Assert.Equal("2026-07-01", header.Cells["J"]);
        Assert.Equal("2026-10-01", header.Cells["K"]);

        var data = equityRows.Single(r => r.Cells["C"] == "Вложение собственных средств");
        Assert.Equal(14, data.SourceRowNumber);
        Assert.Equal("тыс. руб.", data.Cells["D"]);
        // Числа в Cells приходят как ToString InvariantCulture — главное, что они там есть.
        Assert.Equal("238000", data.Cells["H"]);
        Assert.Equal("0", data.Cells["I"]);
        Assert.Equal("100000", data.Cells["J"]);
        Assert.Equal("0", data.Cells["K"]);
    }

    [Fact]
    public async Task XlsxParser_EquityFundingHint_NoStartMarker_EmitsNothing()
    {
        // Файл без раздела «Финансирование: Этап 1» (KeyName-строка тоже отсутствует) →
        // парсер не эмитит equity-funding строки, ошибок не добавляет.
        var bytes = BuildInputsXlsxWithoutEquity();
        using var stream = new MemoryStream(bytes);

        var layout = new KeyValueVertical(
            SheetName: "Inputs", KeyColumn: "C", ValueStartColumn: "H",
            EquityFunding: new EquityFundingHint(
                MarkerColumn: "C",
                StartMarker: "Финансирование: Этап 1",
                KeyName: "Вложение собственных средств",
                UnitColumn: "D",
                QuarterHeaderRow: 7,
                FirstQuarterColumn: "H",
                LastQuarterColumn: "K"));

        var parser = new XlsxParser();
        var result = await parser.ParseAsync(stream, layout, default);

        Assert.DoesNotContain(result.Rows, r => r.Sheet?.EndsWith("(equity-funding)") == true);
        Assert.DoesNotContain(result.Errors, e =>
            e.Message.Contains("EquityFunding", StringComparison.OrdinalIgnoreCase));
    }

    // ─────────── ValidateEquityFunding — две mapped-строки ───────────

    [Fact]
    public async Task Validate_EquityFundingRows_ProducesQuartersAndDataMappedRows()
    {
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, SiteId, null);
        var rows = BuildEquityFundingParsedRows("тыс. руб.",
            ("H", "2026-01-01", "238000"),
            ("I", "2026-04-01", "0"),
            ("J", "2026-07-01", "100000")).ToList();

        var result = await _mapper.ValidateAsync(ctx, rows, _dbContext, default);

        var quartersMapped = result.Rows.FirstOrDefault(r =>
            r.MappedValues.RootElement.TryGetProperty("Kind", out var k)
            && k.GetString() == "equity_funding_quarters");
        Assert.NotNull(quartersMapped);

        var dataMapped = result.Rows.FirstOrDefault(r =>
            r.MappedValues.RootElement.TryGetProperty("Kind", out var k)
            && k.GetString() == "equity_funding_data");
        Assert.NotNull(dataMapped);

        // Кварталы: 3 даты (Q1/Q2/Q3 2026).
        var quarters = quartersMapped!.MappedValues.RootElement
            .GetProperty("Quarters").EnumerateArray().ToList();
        Assert.Equal(3, quarters.Count);

        // Data: unit + scale + points (нулевой Q2 отбрасывается).
        var dataRoot = dataMapped!.MappedValues.RootElement;
        Assert.Equal("тыс. руб.", dataRoot.GetProperty("Unit").GetString());
        Assert.Equal(1000d, dataRoot.GetProperty("ScaleMultiplier").GetDouble());

        var points = dataRoot.GetProperty("Points").EnumerateArray().ToList();
        Assert.Equal(2, points.Count); // 238000 и 100000; 0 пропускается
        var p0 = points[0];
        Assert.Equal("H", p0.GetProperty("Col").GetString());
        Assert.Equal(238_000d, p0.GetProperty("ValueRaw").GetDouble());
        Assert.Equal(238_000_000d, p0.GetProperty("Value").GetDouble()); // ×1000
    }

    [Fact]
    public async Task Validate_EquityFundingRows_NoHeader_ReturnsEmpty()
    {
        // Парсер по какой-то причине эмитил только data-row без header — маппер не
        // должен ругаться и просто не создаёт equity-funding mapped-rows.
        var dataRow = new ParsedRow(14, "Inputs (equity-funding)",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["C"] = "Вложение собственных средств",
                ["D"] = "тыс. руб.",
                ["H"] = "238000",
            });

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, SiteId, null);
        var result = await _mapper.ValidateAsync(ctx, [dataRow], _dbContext, default);

        Assert.DoesNotContain(result.Rows, r =>
            r.MappedValues.RootElement.TryGetProperty("Kind", out var k)
            && k.GetString() is "equity_funding_quarters" or "equity_funding_data");
    }

    // ─────────── ApplyAsync — каскад fmcode=604 ───────────

    [Fact]
    public async Task ApplyAsync_EquityFunding_CreatesInputDataWithFmCode604_OnlySumm()
    {
        // План отсутствует (нет secondary файла) — этот тест не про Plan-каскад.
        // Equity-funding mapped-rows приходят через основной flow ValidateAsync.
        var bytes = BuildPlanXlsxApartmentsOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var inputs = BuildEquityFundingParsedRows("тыс. руб.",
            ("H", "2026-01-01", "238000"),
            ("I", "2026-04-01", "0"),
            ("J", "2026-07-01", "100000"),
            ("K", "2026-10-01", "0")).ToList();

        var validation = await _mapper.ValidateAsync(ctx, inputs, _dbContext, default);
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, validation.Rows, default);

        // Equity-funding: 2 непустых квартала → 2 inputdata с Code.ID=604_777.
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r =>
                r.FMModelVersionID == VersionId
                && r.Code != null && r.Code.ID == CodeEquityId
                && r.Amount == 0d && r.Cost == 0d && r.Percent == 0d),
            It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // Q1 — Summ = 238000 × 1000 = 238_000_000.
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r =>
                r.Code != null && r.Code.ID == CodeEquityId
                && r.FMPeriod == "2026Q1" && r.Summ == 238_000_000d),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Q3 — Summ = 100000 × 1000 = 100_000_000.
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r =>
                r.Code != null && r.Code.ID == CodeEquityId
                && r.FMPeriod == "2026Q3" && r.Summ == 100_000_000d),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Линковка ровно 2 раза для созданных equity-точек (Plan-точек тут 4, итого 6).
        _mockCrud.Verify(c => c.LinkInputDataToVersionAsync(
            VersionId, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2));

        Assert.DoesNotContain(apply.Errors, e => e.ErrorCode == "equity_funding_code_not_found");
        Assert.DoesNotContain(apply.Errors, e => e.ErrorCode == "equity_funding_codes_unavailable");
        Assert.DoesNotContain(apply.Errors, e => e.ErrorCode == "equity_funding_create_failed");
    }

    [Fact]
    public async Task ApplyAsync_EquityFunding_RublesUnit_NoScaling()
    {
        var bytes = BuildPlanXlsxApartmentsOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var inputs = BuildEquityFundingParsedRows("руб.",
            ("H", "2026-01-01", "238000000")).ToList();

        var validation = await _mapper.ValidateAsync(ctx, inputs, _dbContext, default);
        await _mapper.ApplyAsync(ctx, _dbContext, validation.Rows, default);

        // Единица «руб.» — множитель 1. 238_000_000 руб = 238_000_000.
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r =>
                r.Code != null && r.Code.ID == CodeEquityId
                && r.FMPeriod == "2026Q1" && r.Summ == 238_000_000d),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyAsync_EquityFunding_MillionRubles_ScalesByMillion()
    {
        var bytes = BuildPlanXlsxApartmentsOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var inputs = BuildEquityFundingParsedRows("млн. руб.",
            ("H", "2026-01-01", "238")).ToList();

        var validation = await _mapper.ValidateAsync(ctx, inputs, _dbContext, default);
        await _mapper.ApplyAsync(ctx, _dbContext, validation.Rows, default);

        // 238 × 1_000_000 = 238_000_000.
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r =>
                r.Code != null && r.Code.ID == CodeEquityId
                && r.FMPeriod == "2026Q1" && r.Summ == 238_000_000d),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyAsync_EquityFunding_NoEquityRows_SkipsCascadeWithoutError()
    {
        // Файл без раздела «Финансирование: Этап 1» — equity-funding mapped-rows
        // отсутствуют в ValidationResult, каскад тихо пропущен.
        var bytes = BuildPlanXlsxApartmentsOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // CreateInputDataAsync вызван ТОЛЬКО для Plan-точек (4 квартала квартир).
        // Если бы Equity-каскад работал — мы бы увидели CodeEquityId в каком-либо запросе.
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r => r.Code != null && r.Code.ID == CodeEquityId),
            It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.DoesNotContain(apply.Errors, e =>
            e.ErrorCode is "equity_funding_code_not_found"
                or "equity_funding_codes_unavailable"
                or "equity_funding_create_failed");
    }

    [Fact]
    public async Task ApplyAsync_EquityFunding_FmCode604NotFound_AddsRowError()
    {
        // Visary вернул пустой ответ на listview/fmcode?Code=604 — каскад skip с
        // row-error «equity_funding_code_not_found», Plan-точки не страдают.
        _mockListView
            .Setup(c => c.FindFmCodeByCodeAsync(
                FinModelImportMapper.FmCodeEquityInvestment, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmCodeRaw> { Data = [], Total = 0 });

        var bytes = BuildPlanXlsxApartmentsOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var inputs = BuildEquityFundingParsedRows("тыс. руб.",
            ("H", "2026-01-01", "238000")).ToList();

        var validation = await _mapper.ValidateAsync(ctx, inputs, _dbContext, default);
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, validation.Rows, default);

        Assert.Contains(apply.Errors, e => e.ErrorCode == "equity_funding_code_not_found");
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r => r.Code != null && r.Code.ID == CodeEquityId),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ApplyAsync_EquityFunding_FmCodeListViewThrows_AddsRowError()
    {
        _mockListView
            .Setup(c => c.FindFmCodeByCodeAsync(
                FinModelImportMapper.FmCodeEquityInvestment, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("500 Internal Server Error"));

        var bytes = BuildPlanXlsxApartmentsOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var inputs = BuildEquityFundingParsedRows("тыс. руб.",
            ("H", "2026-01-01", "238000")).ToList();

        var validation = await _mapper.ValidateAsync(ctx, inputs, _dbContext, default);
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, validation.Rows, default);

        Assert.Contains(apply.Errors, e => e.ErrorCode == "equity_funding_codes_unavailable");
    }

    [Fact]
    public async Task ApplyAsync_EquityFunding_AllZeroValues_SkipsCascade()
    {
        // Все ячейки равны 0 — точек нет, ни один POST не должен идти, ошибок нет.
        var bytes = BuildPlanXlsxApartmentsOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var inputs = BuildEquityFundingParsedRows("тыс. руб.",
            ("H", "2026-01-01", "0"),
            ("I", "2026-04-01", "0")).ToList();

        var validation = await _mapper.ValidateAsync(ctx, inputs, _dbContext, default);
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, validation.Rows, default);

        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r => r.Code != null && r.Code.ID == CodeEquityId),
            It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.DoesNotContain(apply.Errors, e =>
            e.ErrorCode is "equity_funding_code_not_found"
                or "equity_funding_codes_unavailable"
                or "equity_funding_create_failed");
    }

    [Fact]
    public async Task ApplyAsync_EquityFunding_PostFails_LogsRowError()
    {
        // Сеть отваливается на POST inputdata: ожидаем «equity_funding_create_failed».
        var sawEquityRequest = false;
        _mockCrud
            .Setup(c => c.CreateInputDataAsync(It.IsAny<InputDataCreateRequest>(), It.IsAny<CancellationToken>()))
            .Returns<InputDataCreateRequest, CancellationToken>((req, _) =>
            {
                if (req.Code?.ID == CodeEquityId)
                {
                    sawEquityRequest = true;
                    throw new HttpRequestException("502 Bad Gateway");
                }
                return Task.FromResult(new InputDataRaw
                {
                    ID = Interlocked.Increment(ref _nextInputDataId),
                    FMModelVersionID = req.FMModelVersionID, FMPeriod = req.FMPeriod,
                    Code = req.Code, Summ = req.Summ,
                });
            });

        var bytes = BuildPlanXlsxApartmentsOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var inputs = BuildEquityFundingParsedRows("тыс. руб.",
            ("H", "2026-01-01", "238000")).ToList();

        var validation = await _mapper.ValidateAsync(ctx, inputs, _dbContext, default);
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, validation.Rows, default);

        Assert.True(sawEquityRequest);
        Assert.Contains(apply.Errors, e => e.ErrorCode == "equity_funding_create_failed");
    }

    // ─────────── Фикстуры ───────────

    /// <summary>
    /// Эмитим равно ту же пару ParsedRow, которую кладёт <see cref="XlsxParser"/>
    /// после <see cref="EquityFundingHint"/>: header-row с sentinel + data-row с
    /// единицей и значениями. Sheet вида <c>"Inputs (equity-funding)"</c>.
    /// </summary>
    private static IEnumerable<ParsedRow> BuildEquityFundingParsedRows(
        string unitText,
        params (string Col, string IsoDate, string Value)[] quarters)
    {
        var header = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["C"] = XlsxParser.EquityFundingQuartersSentinel,
        };
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["C"] = "Вложение собственных средств",
            ["D"] = unitText,
        };
        foreach (var (col, iso, val) in quarters)
        {
            header[col] = iso;
            data[col] = val;
        }
        yield return new ParsedRow(7, "Inputs (equity-funding)", header);
        yield return new ParsedRow(841, "Inputs (equity-funding)", data);
    }

    /// <summary>
    /// Минимальный XLSX листа Inputs с разделом «Финансирование: Этап 1» и
    /// строкой «Вложение собственных средств» с заданными квартальными ячейками.
    /// Используется для прямой проверки <see cref="XlsxParser"/>.
    /// </summary>
    private static byte[] BuildInputsXlsx(string unitText, double q1, double q2, double q3, double q4)
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Inputs");
            // 7-я строка — даты кварталов (как в реальном файле).
            ws.Cell(7, 8).Value = new DateTime(2026, 1, 1);
            ws.Cell(7, 9).Value = new DateTime(2026, 4, 1);
            ws.Cell(7, 10).Value = new DateTime(2026, 7, 1);
            ws.Cell(7, 11).Value = new DateTime(2026, 10, 1);

            // Чтобы ParseKeyValueVertical не падал на отсутствии ключей — добавим
            // одну фиктивную строку с key в C.
            ws.Cell(9, 3).Value = "Параметр-заглушка";
            ws.Cell(9, 8).Value = "value";

            // Раздел.
            ws.Cell(10, 3).Value = "Финансирование: Этап 1";
            // Заголовок параметра (без единицы) — должен быть проигнорирован.
            ws.Cell(12, 3).Value = "Вложение собственных средств";
            // Строка данных.
            ws.Cell(14, 3).Value = "Вложение собственных средств";
            ws.Cell(14, 4).Value = unitText;
            ws.Cell(14, 8).Value = q1;
            ws.Cell(14, 9).Value = q2;
            ws.Cell(14, 10).Value = q3;
            ws.Cell(14, 11).Value = q4;

            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    private static byte[] BuildInputsXlsxWithoutEquity()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Inputs");
            ws.Cell(7, 8).Value = new DateTime(2026, 1, 1);
            ws.Cell(9, 3).Value = "Параметр";
            ws.Cell(9, 8).Value = "value";
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Минимальный лист «Общий график» с одной таблицей квартир (4 квартала 2026).
    /// План создаётся «без шапки сверху» (layout-2 эталона из FinModelInputDataTests).
    /// Нужен только чтобы Plan-каскад дошёл до создания версии — далее Apply
    /// продолжает Equity-каскадом.
    /// </summary>
    private static byte[] BuildPlanXlsxApartmentsOnly()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Общий график");
            // Шапка года и кварталов (layout-2).
            ws.Cell(3, 1).Value = "Год";
            ws.Cell(3, 3).Value = 2026;
            ws.Cell(5, 1).Value = "Квартал";
            ws.Cell(5, 3).Value = "1 кв";
            ws.Cell(5, 4).Value = "2 кв";
            ws.Cell(5, 5).Value = "3 кв";
            ws.Cell(5, 6).Value = "4 кв";

            // Квартиры — Amount/Cost/Summ-строки.
            ws.Cell(6, 1).Value = "Площадь, кв.м (квартиры)";
            ws.Cell(6, 3).Value = 100;
            ws.Cell(6, 4).Value = 100;
            ws.Cell(6, 5).Value = 100;
            ws.Cell(6, 6).Value = 100;
            ws.Cell(7, 1).Value = "Стоимость 1 кв.м (квартиры)";
            ws.Cell(7, 3).Value = 10_000;
            ws.Cell(7, 4).Value = 10_000;
            ws.Cell(7, 5).Value = 10_000;
            ws.Cell(7, 6).Value = 10_000;
            ws.Cell(8, 1).Value = "Сумма от продажи квартир";
            ws.Cell(8, 3).Value = 1_000_000;
            ws.Cell(8, 4).Value = 1_000_000;
            ws.Cell(8, 5).Value = 1_000_000;
            ws.Cell(8, 6).Value = 1_000_000;
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }
}
