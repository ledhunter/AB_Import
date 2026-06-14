using ClosedXML.Excel;
using KiloImportService.Api.Budget;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
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
/// Покрытие Fact-каскада импорта Финмодели (doc 126): после создания версии
/// плановыми InputData мапер опционально дочитывает блок «Доходы поэтапно»/«Этап 1»
/// с листа Outputs основного файла и доливает фактические значения в ту же версию
/// под отдельными Fact-кодами справочника fmcode (011/021/031/041/211/221/231/051).
/// </summary>
public class FinModelFactInputDataTests : IDisposable
{
    private const int ProjectId = 4584;
    private const int SiteId = 7890;
    private const int FmModelId = 48;
    private const int VersionId = 217;

    // Plan- и Fact-коды справочника fmcode. ID — синтетические; тесты проверяют,
    // что в payload CreateInputDataAsync уходит правильный ID + FMPeriod.
    private const int CodeApartmentPlanId        = 20;
    private const int CodeApartmentFactId        = 120;
    private const int CodeStoreroomFactId        = 131;
    private const int CodeParkingFactId          = 141;
    private const int CodeApartHotelFactId       = 161;
    private const int CodeKindergartenFactId     = 221;
    private const int CodeSchoolFactId           = 211;
    private const int CodeClinicFactId           = 231;
    private const int CodeSportFactId            = 251;

    private readonly FinModelImportMapper _mapper;
    private readonly VisaryDbContext _dbContext;
    private readonly Mock<ICrudClient> _mockCrud;
    private readonly Mock<IListViewClient> _mockListView;
    private readonly TestFileStorage _fileStorage;
    private readonly ServiceProvider _serviceProvider;
    private int _nextInputDataId = 100_000;

    public FinModelFactInputDataTests()
    {
        _mockCrud = new Mock<ICrudClient>();
        _mockListView = new Mock<IListViewClient>();

        // Минимальные справочники / pre-checks (как в FinModelInputDataTests).
        _mockListView.Setup(c => c.ListFinishingMaterialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FinishingMaterialRaw> { Data = [], Total = 0 });
        _mockListView.Setup(c => c.ListEstateClassesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<EstateClassRaw> { Data = [], Total = 0 });
        _mockListView.Setup(c => c.GetWbsBySiteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<WbsRaw> { Data = [], Total = 0 });
        _mockCrud.Setup(c => c.GetProjectByIdFullAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConstructionProjectFull { ID = ProjectId, Title = "Тест ДОУ" });

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

        // fmcode мокаем как Plan-, так и Fact-коды — Apply-каскад зовёт оба.
        SetupFmCode(FinModelImportMapper.FmCodeApartment,        CodeApartmentPlanId,    "010 Продажа квартиры (план)");
        SetupFmCode(FinModelImportMapper.FmCodeApartmentFact,    CodeApartmentFactId,    "011 Продажа квартиры (факт)");
        SetupFmCode(FinModelImportMapper.FmCodeStoreroomFact,    CodeStoreroomFactId,    "031 Продажа иные нежилые (кладовки) (факт)");
        SetupFmCode(FinModelImportMapper.FmCodeParkingFact,      CodeParkingFactId,      "041 Продажа м/м (факт)");
        SetupFmCode(FinModelImportMapper.FmCodeKindergartenFact, CodeKindergartenFactId, "221 ДОУ (факт)");
        SetupFmCode(FinModelImportMapper.FmCodeSchoolFact,       CodeSchoolFactId,       "211 СОШ (факт)");
        SetupFmCode(FinModelImportMapper.FmCodeClinicFact,       CodeClinicFactId,       "231 Поликлиника (факт)");
        SetupFmCode(FinModelImportMapper.FmCodeSportFact,        CodeSportFactId,        "051 ФОК (факт)");
        SetupFmCode(FinModelImportMapper.FmCodeApartHotelFact,   CodeApartHotelFactId,   "061 Продажа апартаменты (факт)");
        // Нежилые Fact-код мокаем тоже — на случай, если фикстура подсунет «ПСН»/«нежил».
        SetupFmCode(FinModelImportMapper.FmCodeNonResidentialFact, 121, "021 Продажа нежилые (ком) ПСН (факт)");

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
            .Setup(c => c.CreateInputDataAsync(
                It.IsAny<InputDataCreateRequest>(), It.IsAny<CancellationToken>()))
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
            .UseInMemoryDatabase($"FinModelFactInputDataTest_{Guid.NewGuid()}")
            .Options;
        _dbContext = new VisaryDbContext(options);
        _dbContext.ConstructionSites.Add(new ConstructionSite
        {
            Id = SiteId, Title = "Тестовый объект", ConstructionProjectId = ProjectId,
        });
        _dbContext.SaveChanges();
    }

    private void SetupFmCode(string code, int id, string title)
    {
        _mockListView
            .Setup(c => c.FindFmCodeByCodeAsync(code, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmCodeRaw>
            {
                Data = [new FmCodeRaw { ID = id, Code = code, Title = title }],
                Total = 1,
            });
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    // ───────────────── ReadOutputsFactData — unit ─────────────────

    [Fact]
    public void ReadOutputsFactData_CustomNumberFormatMarker_RecognizedAsFact()
    {
        // Шаблон заказчика «Параметры к переносу в АБ.xlsx» хранит «Факт» НЕ как
        // текст, а как число 0 с custom number format `[=0]"Факт";[<>0]"Прогноз"`.
        // Без вызова GetFormattedString() парсер видит «0» и пропускает маркер.
        var bytes = BuildOutputsWithCustomFormatFactMarker(year: 2026, quarter: 1);
        using var stream = new MemoryStream(bytes);

        var data = FinModelImportMapper.ReadOutputsFactData(stream);

        Assert.NotNull(data);
        Assert.Equal("2026Q1", data!.FmPeriod);
        // Колонка маркера — там же, где первая ячейка с числовым кодом-«Факт»
        // (в фикстуре это H = 8).
        Assert.Equal(8, data.FactColumn);
        // Sanity: точка по квартирам действительно нашлась.
        Assert.Contains(data.Points,
            p => p.FmCode == FinModelImportMapper.FmCodeApartmentFact);
    }

    [Fact]
    public void ReadOutputsFactData_RealParametersFile_FindsFactMarkerAtH12()
    {
        // Регрессионный тест на реальный шаблон заказчика «Параметры к переносу в АБ.xlsx»:
        // H12 содержит число 0 с custom format → отображается как «Факт».
        // Год H13=2026, квартал H14=1 → FmPeriod="2026Q1".
        var path = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "FinModel", "Параметры к переносу в АБ.xlsx");
        if (!File.Exists(path))
        {
            // Файл может отсутствовать в CI-сборке — тест становится no-op, не fail.
            return;
        }
        using var stream = File.OpenRead(path);

        var data = FinModelImportMapper.ReadOutputsFactData(stream);

        Assert.NotNull(data);
        Assert.Equal("2026Q1", data!.FmPeriod);
        Assert.Equal(8, data.FactColumn); // H = 8
        // Из дампа файла: H167=1007.89 (Квартиры/Площадь), H180=131.96 (Цена),
        // H191=132.99... (Выручка). Точка по квартирам присутствует и содержит
        // все три поля.
        var apt = data.Points.Single(p => p.FmCode == FinModelImportMapper.FmCodeApartmentFact);
        Assert.NotNull(apt.Amount);
        Assert.NotNull(apt.Cost);
        Assert.NotNull(apt.Summ);
        Assert.True(apt.Amount!.Value > 1000d && apt.Amount.Value < 1100d,
            $"Apartment Amount={apt.Amount}, ожидалось ~1007.89");
    }

    [Fact]
    public void ReadOutputsFactData_FactMarkerMissing_ReturnsNull()
    {
        var bytes = BuildOutputsSheetWithoutFactMarker();
        using var stream = new MemoryStream(bytes);

        var data = FinModelImportMapper.ReadOutputsFactData(stream);

        Assert.Null(data);
    }

    [Fact]
    public void ReadOutputsFactData_ReferenceLayout_ResolvesPeriodAndAllRoomTypes()
    {
        var bytes = BuildOutputsWithFactColumnXlsx(
            year: 2026, quarter: 2,
            apartmentAmount: 1008, apartmentCostThousands: 137.46, apartmentSummMillions: 132.99,
            kindergartenAmount: 50,  kindergartenCostThousands: 50, kindergartenSummMillions: 2.5,
            storeroomDash: true);
        using var stream = new MemoryStream(bytes);

        var data = FinModelImportMapper.ReadOutputsFactData(stream);

        Assert.NotNull(data);
        Assert.Equal("2026Q2", data!.FmPeriod);

        // Квартиры — все три поля присутствуют (с конвертацией единиц).
        var apt = data.Points.Single(p => p.FmCode == FinModelImportMapper.FmCodeApartmentFact);
        Assert.Equal(1008d,                  apt.Amount!.Value);
        Assert.Equal(137.46 * 1_000d,        apt.Cost!.Value,  2);
        Assert.Equal(132.99 * 1_000_000d,    apt.Summ!.Value, 1);

        // ДОУ — заполнено отдельной строкой социального объекта.
        var kg = data.Points.Single(p => p.FmCode == FinModelImportMapper.FmCodeKindergartenFact);
        Assert.Equal(50d,                    kg.Amount!.Value);
        Assert.Equal(50d * 1_000d,           kg.Cost!.Value,  2);
        Assert.Equal(2.5d * 1_000_000d,      kg.Summ!.Value, 1);

        // Кладовые — прочерк в Amount-секции; точка с этим Code НЕ создаётся
        // (Amount/Cost/Summ все остались null → builder не накопил ничего).
        // При фактическом use-case заказчик может заполнить только часть полей —
        // здесь подсекции тоже пустые (фикстура BuildOutputsWithFactColumnXlsx),
        // поэтому Storeroom-точки нет.
        Assert.DoesNotContain(data.Points, p => p.FmCode == FinModelImportMapper.FmCodeStoreroomFact);
    }

    [Fact]
    public void ReadOutputsFactData_InvalidYearUnderFactMarker_Throws()
    {
        var bytes = BuildOutputsWithMalformedYear();
        using var stream = new MemoryStream(bytes);

        var ex = Assert.Throws<FinModelImportMapper.FinModelFactParseException>(() =>
            FinModelImportMapper.ReadOutputsFactData(stream));
        Assert.Contains("год", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────── ApplyAsync — integration ─────────────────

    [Fact]
    public async Task ApplyAsync_PrimaryFileWithFact_CreatesPlanAndFactInputData()
    {
        // План на «Общий график» — одна категория (квартиры) с одной Q1-точкой.
        _fileStorage.Put("plan.xlsx",    BuildMinimalPlanXlsx());
        // Outputs — Fact с квартирами (1008 кв.м, 137.46 тыс. руб./кв.м, 132.99 млн руб).
        _fileStorage.Put("primary.xlsx", BuildOutputsWithFactColumnXlsx(
            year: 2026, quarter: 2,
            apartmentAmount: 1008, apartmentCostThousands: 137.46, apartmentSummMillions: 132.99,
            kindergartenAmount: 0, kindergartenCostThousands: 0, kindergartenSummMillions: 0,
            storeroomDash: true));

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx",
            PrimaryFileRelativePath: "primary.xlsx");
        var result = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // Plan-точка (Q1 квартиры, Code=010).
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r =>
                r.FMModelVersionID == VersionId
                && r.FMPeriod == "2024Q1"
                && r.Code != null && r.Code.ID == CodeApartmentPlanId),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Fact-точка (Q2 2026 квартиры, Code=011).
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r =>
                r.FMModelVersionID == VersionId
                && r.FMPeriod == "2026Q2"
                && r.Code != null && r.Code.ID == CodeApartmentFactId
                && r.Amount == 1008
                && Math.Abs(r.Cost - 137.46 * 1_000) < 0.01
                && Math.Abs(r.Summ - 132.99 * 1_000_000) < 1
                && r.Percent == 0d),
            It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "inputdata_fact_codes_unavailable");
        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "inputdata_fact_code_not_found");
        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "inputdata_fact_create_failed");
    }

    [Fact]
    public async Task ApplyAsync_PrimaryFileWithoutFactMarker_SkipsFactCascade_NoErrors()
    {
        _fileStorage.Put("plan.xlsx",    BuildMinimalPlanXlsx());
        _fileStorage.Put("primary.xlsx", BuildOutputsSheetWithoutFactMarker());

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx",
            PrimaryFileRelativePath: "primary.xlsx");
        var result = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // Plan-точка создана.
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r => r.Code != null && r.Code.ID == CodeApartmentPlanId),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Ни одного Fact-Code в payload не должно быть.
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r => r.Code != null && (
                r.Code.ID == CodeApartmentFactId
                || r.Code.ID == CodeKindergartenFactId
                || r.Code.ID == CodeSchoolFactId
                || r.Code.ID == CodeClinicFactId
                || r.Code.ID == CodeSportFactId
                || r.Code.ID == CodeParkingFactId)),
            It.IsAny<CancellationToken>()),
            Times.Never);

        // FindFmCodeByCodeAsync для Fact-кодов вообще не зовётся.
        _mockListView.Verify(c => c.FindFmCodeByCodeAsync(
            FinModelImportMapper.FmCodeApartmentFact, It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "fact_block_parse_error");
    }

    [Fact]
    public async Task ApplyAsync_PrimaryFilePathNull_SkipsFactCascade()
    {
        _fileStorage.Put("plan.xlsx", BuildMinimalPlanXlsx());

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx",
            PrimaryFileRelativePath: null);
        var result = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // Fact-каскад не должен трогать справочник fmcode по Fact-Code'ам.
        _mockListView.Verify(c => c.FindFmCodeByCodeAsync(
            FinModelImportMapper.FmCodeApartmentFact, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ApplyAsync_PrimaryFileWithDashCells_SkipsCellsWithoutError()
    {
        _fileStorage.Put("plan.xlsx",    BuildMinimalPlanXlsx());
        // Outputs: Fact-маркер есть, но в Amount-секции у квартир — прочерк,
        // Cost/Summ заполнены. Точка должна быть создана с Amount=0 (нормализация на payload).
        _fileStorage.Put("primary.xlsx", BuildOutputsWithApartmentDashAmount());

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx",
            PrimaryFileRelativePath: "primary.xlsx");
        var result = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // Fact-точка по квартирам: Amount=0 (прочерк), Cost/Summ — заполнены.
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r =>
                r.Code != null && r.Code.ID == CodeApartmentFactId
                && r.Amount == 0d
                && r.Cost > 0d
                && r.Summ > 0d),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void ReadOutputsFactData_AllZeroRows_SkippedFromPoints()
    {
        // Шаблон заказчика заполняет неиспользуемые типы помещений явными 0 во всех
        // трёх блоках (Площадь/Цена/Выручка) — не прочерками. Раньше парсер создавал
        // InputData(0,0,0) для каждого такого типа; v1.3 — финальный фильтр выкидывает.
        // Остаются только типы хотя бы с одним ненулевым значением.
        var bytes = BuildOutputsWithMixedZeroAndNonZeroRows();
        using var stream = new MemoryStream(bytes);

        var data = FinModelImportMapper.ReadOutputsFactData(stream);

        Assert.NotNull(data);
        // Квартиры (1000/130/130) и Машиноместа (0/1489/0) — есть ненулевые → создаются.
        Assert.Contains(data!.Points,
            p => p.FmCode == FinModelImportMapper.FmCodeApartmentFact);
        Assert.Contains(data.Points,
            p => p.FmCode == FinModelImportMapper.FmCodeParkingFact);
        // ДОУ/СОШ/Поликлиника/ФОК — все нули во всех трёх блоках → skip.
        Assert.DoesNotContain(data.Points,
            p => p.FmCode == FinModelImportMapper.FmCodeKindergartenFact);
        Assert.DoesNotContain(data.Points,
            p => p.FmCode == FinModelImportMapper.FmCodeSchoolFact);
        Assert.DoesNotContain(data.Points,
            p => p.FmCode == FinModelImportMapper.FmCodeClinicFact);
        Assert.DoesNotContain(data.Points,
            p => p.FmCode == FinModelImportMapper.FmCodeSportFact);

        // Машиноместа сохраняют 0 в Amount и Summ, но Cost — реальное значение.
        var park = data.Points.Single(p => p.FmCode == FinModelImportMapper.FmCodeParkingFact);
        Assert.Equal(0d, park.Amount ?? 0d);
        Assert.Equal(1489d * 1_000d, park.Cost!.Value, 2);
        Assert.Equal(0d, park.Summ ?? 0d);
    }

    [Fact]
    public void ReadOutputsFactData_LabelInColumnD_NotJustC_Resolved()
    {
        // Жёсткой привязки к C-колонке для типа помещения быть не должно: на разных
        // шаблонах label иногда уезжает в D/E (например, объединение «Подгруппа»+«Тип»).
        var bytes = BuildOutputsWithLabelsInColumnD();
        using var stream = new MemoryStream(bytes);

        var data = FinModelImportMapper.ReadOutputsFactData(stream);

        Assert.NotNull(data);
        var apt = data!.Points.Single(p => p.FmCode == FinModelImportMapper.FmCodeApartmentFact);
        Assert.Equal(1000d, apt.Amount!.Value);
    }

    [Fact]
    public void ResolveFactFmCode_ApartHotel_Returns061()
    {
        // Через парсер: фикстура с одной Fact-строкой «Апартаменты».
        var bytes = BuildOutputsWithApartHotelRow(amount: 500, costThousands: 200, summMillions: 100);
        using var stream = new MemoryStream(bytes);

        var data = FinModelImportMapper.ReadOutputsFactData(stream);

        Assert.NotNull(data);
        var p = data!.Points.Single(x => x.FmCode == FinModelImportMapper.FmCodeApartHotelFact);
        Assert.Equal("061", p.FmCode);
        Assert.Equal(500d,                 p.Amount!.Value);
        Assert.Equal(200d * 1_000d,        p.Cost!.Value,  2);
        Assert.Equal(100d * 1_000_000d,    p.Summ!.Value, 1);
    }

    [Fact]
    public async Task ApplyAsync_PrimaryFileWithApartHotelRow_PostsFactInputDataWithCode061()
    {
        _fileStorage.Put("plan.xlsx",    BuildMinimalPlanXlsx());
        _fileStorage.Put("primary.xlsx", BuildOutputsWithApartHotelRow(
            amount: 500, costThousands: 200, summMillions: 100));

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx",
            PrimaryFileRelativePath: "primary.xlsx");
        await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r =>
                r.FMModelVersionID == VersionId
                && r.Code != null && r.Code.ID == CodeApartHotelFactId
                && r.Amount == 500d
                && Math.Abs(r.Cost - 200d * 1_000d) < 0.01
                && Math.Abs(r.Summ - 100d * 1_000_000d) < 1
                && r.Percent == 0d),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ───────────────── Фикстуры ─────────────────

    /// <summary>
    /// Минимальный план: одна категория (квартиры), одна непустая ячейка Q1 2024.
    /// Достаточно для прохождения EnsureFmModelAsync + создания версии.
    /// </summary>
    private static byte[] BuildMinimalPlanXlsx()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Общий график");
            ws.Cell(3, 1).Value = "Год";
            ws.Cell(3, 3).Value = 2024;
            ws.Cell(4, 1).Value = "Квартал";
            ws.Cell(4, 3).Value = "1 кв";
            ws.Cell(4, 4).Value = "2 кв";
            ws.Cell(4, 5).Value = "3 кв";
            ws.Cell(4, 6).Value = "4 кв";
            ws.Cell(5, 1).Value = "План";
            ws.Cell(6, 1).Value = "Квартиры, кв.м";
            ws.Cell(6, 3).Value = 1000;
            ws.Cell(7, 1).Value = "Стоимость 1 кв.м";
            ws.Cell(7, 3).Value = 100;
            ws.Cell(8, 1).Value = "Доход";
            ws.Cell(8, 3).Value = 100_000;
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Outputs-лист с Fact-блоком. Маркер «Факт» в колонке F (= факт-колонка),
    /// под ним F+1 — год, F+2 — квартал. Ниже блок «Доходы поэтапно» / «Этап 1»
    /// с тремя подсекциями. Тип помещения — в колонке C.
    /// </summary>
    private static byte[] BuildOutputsWithFactColumnXlsx(
        int year, int quarter,
        double apartmentAmount, double apartmentCostThousands, double apartmentSummMillions,
        double kindergartenAmount, double kindergartenCostThousands, double kindergartenSummMillions,
        bool storeroomDash)
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Outputs");

            // Fact-маркер в F2, год F3, квартал F4. Колонка F = factCol = 6.
            ws.Cell(2, 6).Value = "Факт";
            ws.Cell(3, 6).Value = year;
            ws.Cell(4, 6).Value = quarter;

            int r = 10;
            ws.Cell(r++, 3).Value = "Доходы поэтапно";
            r++;
            ws.Cell(r++, 3).Value = "Этап 1";
            r++;
            ws.Cell(r++, 3).Value = "Площадь реализации, кв.м.";
            ws.Cell(r,   3).Value = "Квартиры";   ws.Cell(r++, 6).Value = apartmentAmount;
            ws.Cell(r,   3).Value = "Машиноместа";
            if (storeroomDash) ws.Cell(r, 6).Value = "-"; else ws.Cell(r, 6).Value = 5;
            r++;
            ws.Cell(r,   3).Value = "ДОУ";         ws.Cell(r++, 6).Value = kindergartenAmount;
            r++;
            ws.Cell(r++, 3).Value = "Цена реализации, тыс. руб./кв.м";
            ws.Cell(r,   3).Value = "Квартиры";   ws.Cell(r++, 6).Value = apartmentCostThousands;
            ws.Cell(r,   3).Value = "ДОУ";         ws.Cell(r++, 6).Value = kindergartenCostThousands;
            r++;
            ws.Cell(r++, 3).Value = "Выручка от реализации, млн руб.";
            ws.Cell(r,   3).Value = "Квартиры";   ws.Cell(r++, 6).Value = apartmentSummMillions;
            ws.Cell(r,   3).Value = "ДОУ";         ws.Cell(r++, 6).Value = kindergartenSummMillions;

            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Outputs с Fact-блоком: Квартиры (полные данные), Машиноместа (только Cost),
    /// ДОУ/СОШ/Поликлиника/ФОК (явные нули во всех трёх подсекциях). Имитирует
    /// раскладку реального шаблона «Параметры к переносу в АБ.xlsx».
    /// </summary>
    private static byte[] BuildOutputsWithMixedZeroAndNonZeroRows()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Outputs");
            ws.Cell(2, 8).Value = "Факт";
            ws.Cell(3, 8).Value = 2026;
            ws.Cell(4, 8).Value = 1;

            int r = 10;
            ws.Cell(r++, 3).Value = "Доходы поэтапно";
            ws.Cell(r++, 3).Value = "Этап 1";

            // Площадь.
            ws.Cell(r++, 3).Value = "Площадь реализации, кв.м.";
            ws.Cell(r,   3).Value = "Квартиры";    ws.Cell(r++, 8).Value = 1000;
            ws.Cell(r,   3).Value = "Машиноместа"; ws.Cell(r++, 8).Value = 0;
            ws.Cell(r,   3).Value = "ДОУ";          ws.Cell(r++, 8).Value = 0;
            ws.Cell(r,   3).Value = "СОШ";          ws.Cell(r++, 8).Value = 0;
            ws.Cell(r,   3).Value = "Поликлиника";  ws.Cell(r++, 8).Value = 0;
            ws.Cell(r,   3).Value = "ФОК";          ws.Cell(r++, 8).Value = 0;
            r++;

            // Цена.
            ws.Cell(r++, 3).Value = "Цена реализации, тыс. руб./кв.м";
            ws.Cell(r,   3).Value = "Квартиры";    ws.Cell(r++, 8).Value = 130;
            ws.Cell(r,   3).Value = "Машиноместа"; ws.Cell(r++, 8).Value = 1489;
            ws.Cell(r,   3).Value = "ДОУ";          ws.Cell(r++, 8).Value = 0;
            ws.Cell(r,   3).Value = "СОШ";          ws.Cell(r++, 8).Value = 0;
            ws.Cell(r,   3).Value = "Поликлиника";  ws.Cell(r++, 8).Value = 0;
            ws.Cell(r,   3).Value = "ФОК";          ws.Cell(r++, 8).Value = 0;
            r++;

            // Выручка.
            ws.Cell(r++, 3).Value = "Выручка от реализации, млн руб.";
            ws.Cell(r,   3).Value = "Квартиры";    ws.Cell(r++, 8).Value = 130;
            ws.Cell(r,   3).Value = "Машиноместа"; ws.Cell(r++, 8).Value = 0;
            ws.Cell(r,   3).Value = "ДОУ";          ws.Cell(r++, 8).Value = 0;
            ws.Cell(r,   3).Value = "СОШ";          ws.Cell(r++, 8).Value = 0;
            ws.Cell(r,   3).Value = "Поликлиника";  ws.Cell(r++, 8).Value = 0;
            ws.Cell(r,   3).Value = "ФОК";          ws.Cell(r++, 8).Value = 0;

            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Outputs с типами помещений в D-колонке (не C). Проверка ослабленной привязки
    /// к C-колонке в парсере подсекций (см. v1.3).
    /// </summary>
    private static byte[] BuildOutputsWithLabelsInColumnD()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Outputs");
            ws.Cell(2, 8).Value = "Факт";
            ws.Cell(3, 8).Value = 2026;
            ws.Cell(4, 8).Value = 1;

            int r = 10;
            // Doхо... и Этап 1 — в C-колонке. Эти заголовки FindRowByLabel сканирует
            // по всем колонкам, так что они работают и в C, и в D — этот тест про
            // строки данных.
            ws.Cell(r++, 3).Value = "Доходы поэтапно";
            ws.Cell(r++, 3).Value = "Этап 1";
            ws.Cell(r++, 3).Value = "Площадь реализации, кв.м.";
            // Тип в D (4), не в C.
            ws.Cell(r,   4).Value = "Квартиры"; ws.Cell(r++, 8).Value = 1000;
            r++;
            ws.Cell(r++, 3).Value = "Цена реализации, тыс. руб./кв.м";
            ws.Cell(r,   4).Value = "Квартиры"; ws.Cell(r++, 8).Value = 130;
            r++;
            ws.Cell(r++, 3).Value = "Выручка от реализации, млн руб.";
            ws.Cell(r,   4).Value = "Квартиры"; ws.Cell(r++, 8).Value = 130;

            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Outputs-лист с маркером «Факт», заданным через custom number format
    /// (как в реальном шаблоне «Параметры к переносу в АБ.xlsx»: H12=0 с форматом
    /// `[=0]"Факт";[<>0]"Прогноз"`). Год/квартал — числовые ячейки сразу под маркером.
    /// </summary>
    private static byte[] BuildOutputsWithCustomFormatFactMarker(int year, int quarter)
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Outputs");

            // Шапка периодов в r12 (Период от начала / маркер фаз):
            //   H12=0 → «Факт», I12..M12=1 → «Прогноз». Custom format на всём диапазоне.
            const string factFormat = "[=0]\"Факт\";[<>0]\"Прогноз\"";
            ws.Cell(12, 8).Value = 0;  // H12
            ws.Cell(12, 8).Style.NumberFormat.Format = factFormat;
            for (int c = 9; c <= 13; c++) // I..M
            {
                ws.Cell(12, c).Value = 1;
                ws.Cell(12, c).Style.NumberFormat.Format = factFormat;
            }

            // Год и квартал в той же колонке (H) сразу под маркером.
            ws.Cell(13, 8).Value = year;
            ws.Cell(14, 8).Value = quarter;

            // Блок «Доходы поэтапно» / «Этап 1» / 3 подсекции.
            int r = 20;
            ws.Cell(r++, 3).Value = "Доходы поэтапно";
            ws.Cell(r++, 3).Value = "Этап 1";
            ws.Cell(r++, 3).Value = "Площадь реализации, кв.м.";
            ws.Cell(r,   3).Value = "Квартиры"; ws.Cell(r++, 8).Value = 1000;
            r++;
            ws.Cell(r++, 3).Value = "Цена реализации, тыс. руб./кв.м";
            ws.Cell(r,   3).Value = "Квартиры"; ws.Cell(r++, 8).Value = 130;
            r++;
            ws.Cell(r++, 3).Value = "Выручка от реализации, млн руб.";
            ws.Cell(r,   3).Value = "Квартиры"; ws.Cell(r++, 8).Value = 130;

            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Outputs-лист без маркера «Факт» — реальный шаблон «Параметры к переносу в АБ.xlsx»
    /// до доработки заказчиком (2026-06-07). В этом случае Fact-каскад должен тихо
    /// пропуститься без row-errors.
    /// </summary>
    private static byte[] BuildOutputsSheetWithoutFactMarker()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Outputs");
            ws.Cell(2, 3).Value = "Сводные данные";
            ws.Cell(3, 3).Value = "Доходы поэтапно";
            ws.Cell(5, 3).Value = "Этап 1";
            ws.Cell(7, 3).Value = "Площадь реализации, кв.м.";
            ws.Cell(8, 3).Value = "Квартиры";
            ws.Cell(8, 6).Value = 999;
            // Никакой ячейки «Факт» нет — Fact-каскад должен вернуть null.
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Outputs с маркером «Факт», но под ним не число — год «xxx». Парсер бросает
    /// FinModelFactParseException, мапер пишет одну row-error «fact_block_parse_error».
    /// </summary>
    private static byte[] BuildOutputsWithMalformedYear()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Outputs");
            ws.Cell(2, 6).Value = "Факт";
            ws.Cell(3, 6).Value = "не-число";
            ws.Cell(4, 6).Value = 2;
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Outputs с Fact-блоком, в котором единственная строка «Апартаменты»
    /// (Plan-код 060 / Fact-код 061, добавлены 2026-06-07). Проверка распознавания
    /// и POST с Code.ID = CodeApartHotelFactId.
    /// </summary>
    private static byte[] BuildOutputsWithApartHotelRow(
        double amount, double costThousands, double summMillions)
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Outputs");
            ws.Cell(2, 6).Value = "Факт";
            ws.Cell(3, 6).Value = 2026;
            ws.Cell(4, 6).Value = 2;

            int r = 10;
            ws.Cell(r++, 3).Value = "Доходы поэтапно";
            ws.Cell(r++, 3).Value = "Этап 1";
            ws.Cell(r++, 3).Value = "Площадь реализации, кв.м.";
            ws.Cell(r,   3).Value = "Апартаменты"; ws.Cell(r++, 6).Value = amount;
            r++;
            ws.Cell(r++, 3).Value = "Цена реализации, тыс. руб./кв.м";
            ws.Cell(r,   3).Value = "Апартаменты"; ws.Cell(r++, 6).Value = costThousands;
            r++;
            ws.Cell(r++, 3).Value = "Выручка от реализации, млн руб.";
            ws.Cell(r,   3).Value = "Апартаменты"; ws.Cell(r++, 6).Value = summMillions;

            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Outputs с Fact-блоком, где у строки «Квартиры» в подсекции Площадь стоит
    /// прочерк «-», а в Цене и Выручке — числа. Проверка: прочерк → Amount=null
    /// → в payload пишется 0 (контракт), Cost/Summ нормально.
    /// </summary>
    private static byte[] BuildOutputsWithApartmentDashAmount()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Outputs");
            ws.Cell(2, 6).Value = "Факт";
            ws.Cell(3, 6).Value = 2026;
            ws.Cell(4, 6).Value = 2;

            int r = 10;
            ws.Cell(r++, 3).Value = "Доходы поэтапно";
            ws.Cell(r++, 3).Value = "Этап 1";
            ws.Cell(r++, 3).Value = "Площадь реализации, кв.м.";
            ws.Cell(r,   3).Value = "Квартиры"; ws.Cell(r++, 6).Value = "-";
            r++;
            ws.Cell(r++, 3).Value = "Цена реализации, тыс. руб./кв.м";
            ws.Cell(r,   3).Value = "Квартиры"; ws.Cell(r++, 6).Value = 137.46;
            r++;
            ws.Cell(r++, 3).Value = "Выручка от реализации, млн руб.";
            ws.Cell(r,   3).Value = "Квартиры"; ws.Cell(r++, 6).Value = 132.99;

            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }
}
