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
/// Покрытие создания версии Финмодели (<c>fmmodelversion</c>), «Входных данных»
/// (<c>inputdata</c>) и их линковки после создания <c>fmmodel</c>. См. doc 112.
/// </summary>
public class FinModelInputDataTests : IDisposable
{
    private const int ProjectId = 4584;
    private const int SiteId = 7890;
    private const int FmModelId = 48;
    private const int VersionId = 217;

    // ID's справочника inputdatacode (HAR заказчика для квартир: 20). Для остальных
    // — синтетические значения; тесты проверяют что нужные Title → ID попадают
    // в payload `Code`.
    private const int CodeApartmentId       = 20;
    private const int CodeNonResidentialId  = 21;
    private const int CodeParkingId         = 22;

    private readonly FinModelImportMapper _mapper;
    private readonly VisaryDbContext _dbContext;
    private readonly Mock<ICrudClient> _mockCrud;
    private readonly Mock<IListViewClient> _mockListView;
    private readonly TestFileStorage _fileStorage;
    private readonly ServiceProvider _serviceProvider;
    private int _nextInputDataId = 27_665;

    public FinModelInputDataTests()
    {
        _mockCrud = new Mock<ICrudClient>();
        _mockListView = new Mock<IListViewClient>();

        // Минимальные справочники / pre-checks (как в FinModelFmModelTests).
        _mockListView.Setup(c => c.ListFinishingMaterialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FinishingMaterialRaw> { Data = [], Total = 0 });
        _mockListView.Setup(c => c.ListEstateClassesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<EstateClassRaw> { Data = [], Total = 0 });
        _mockListView.Setup(c => c.GetWbsBySiteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<WbsRaw> { Data = [], Total = 0 });
        _mockCrud.Setup(c => c.GetProjectByIdFullAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConstructionProjectFull { ID = ProjectId, Title = "Тест ДОУ" });

        // fmmodel pre-check пусто → создаём
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

        // Справочник fmcode: per-code резолв (поле Code в Visary). Каждый известный
        // Code — 1 строка в ответе с каноничным Title-ом из справочника
        // («010 Продажа квартиры (план)»). Иные — пустой ответ (Total=0).
        // Сетевая ошибка эмулируется в отдельных тестах через ThrowsAsync.
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
                FinModelImportMapper.FmCodeNonResidential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmCodeRaw>
            {
                Data = [new FmCodeRaw { ID = CodeNonResidentialId, Code = FinModelImportMapper.FmCodeNonResidential, Title = "020 Продажа нежилые ( ком) ПСН (план)" }],
                Total = 1,
            });
        _mockListView
            .Setup(c => c.FindFmCodeByCodeAsync(
                FinModelImportMapper.FmCodeParking, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmCodeRaw>
            {
                Data = [new FmCodeRaw { ID = CodeParkingId, Code = FinModelImportMapper.FmCodeParking, Title = "040 Продажа м/м (план)" }],
                Total = 1,
            });

        // Версии Финмодели — по умолчанию пусто → создаём новую.
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

        // InputData в версии — по умолчанию пусто (нет дубликатов).
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
            .UseInMemoryDatabase($"FinModelInputDataTest_{Guid.NewGuid()}")
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

    // ─────────── ReadGeneralScheduleData — категории + skip факт-блока ───────────

    [Fact]
    public void ReadGeneralScheduleData_Reference_FindsThreeCategories_AndAllPeriods()
    {
        // Эталонная раскладка по «Общий график» из spec (Журавли):
        //   Таблица 1 (r3..r14): Квартиры — план r6/r7/r8 + skip r9..r14 (Доход накопл./Факт).
        //   Таблица 2 (r20..r25): Нежилые ПСН — ВСЕ ячейки пустые (новый кейс: skip).
        //   Таблица 3 (r30..r35): Машиноместа — явные нули (план = 0 → точки эмитятся).
        var bytes = BuildReferencePlanXlsx();
        using var stream = new MemoryStream(bytes);

        var data = FinModelImportMapper.ReadGeneralScheduleData(stream);

        // PeriodStart/PeriodEnd берутся из шапки, а НЕ из факт-данных — даже если
        // часть категорий пустая, диапазон планирования сохраняется (см. doc 112 v1.5).
        Assert.Equal("2024Q1", data.PeriodStart);
        Assert.Equal("2024Q4", data.PeriodEnd);
        Assert.Equal(4, data.Columns.Count); // 4 квартала 2024

        Assert.Equal(3, data.Categories.Count);
        Assert.Contains(data.Categories, c => c.FmCode == FinModelImportMapper.FmCodeApartment);
        Assert.Contains(data.Categories, c => c.FmCode == FinModelImportMapper.FmCodeNonResidential);
        Assert.Contains(data.Categories, c => c.FmCode == FinModelImportMapper.FmCodeParking);

        // Квартиры (4 квартала с данными) + м/м (4 квартала с явными 0) = 8 точек.
        // Нежилые с полностью пустыми ячейками — 0 точек (см. doc 112 v1.5).
        Assert.Equal(8, data.InputDataPoints.Count);

        // Q1 квартиры — пример из спеки: Summ=243685102, Amount=2459.85, Cost=99065.03.
        var q1Apt = data.InputDataPoints
            .Single(p => p.FmPeriod == "2024Q1" && p.FmCode == FinModelImportMapper.FmCodeApartment);
        Assert.Equal(243685102, q1Apt.Summ, 1);
        Assert.Equal(2459.85,   q1Apt.Amount, 2);
        Assert.Equal(99065.03,  q1Apt.Cost, 2);

        // ⚠️ КЛЮЧЕВАЯ ПРОВЕРКА: значения факт-блока (r11/r12/r13 в эталоне) — 11111/12345/88888888 —
        // НЕ должны попасть в точки. Парсер обязан остановиться на «Доход накопл.»/«Факт».
        Assert.DoesNotContain(data.InputDataPoints, p => p.Summ == 88_888_888);
        Assert.DoesNotContain(data.InputDataPoints, p => p.Amount == 11111);

        // Машиноместа с явными нулями — точки эмитятся (план = 0 валиден).
        var q1Park = data.InputDataPoints
            .Single(p => p.FmPeriod == "2024Q1" && p.FmCode == FinModelImportMapper.FmCodeParking);
        Assert.Equal(0, q1Park.Summ);
        Assert.Equal(0, q1Park.Amount);
        Assert.Equal(0, q1Park.Cost);

        // Нежилые с полностью пустыми ячейками — точек нет вообще (новое поведение).
        Assert.DoesNotContain(data.InputDataPoints,
            p => p.FmCode == FinModelImportMapper.FmCodeNonResidential);
    }

    [Fact]
    public void ReadGeneralScheduleData_EmptyQuartersSkipped_ExplicitZeroEmitted()
    {
        // Файл с одной таблицей квартир: Q1 заполнен (1000/100/100000), Q2 явные нули,
        // Q3 полностью пустой (skip), Q4 только Summ=5000000 (остальное пустое — точка эмитится,
        // пустые → 0).
        var bytes = BuildPartialEmptyPlanXlsx();
        using var stream = new MemoryStream(bytes);

        var data = FinModelImportMapper.ReadGeneralScheduleData(stream);

        // Диапазон шапки сохранён, даже если Q3 без данных.
        Assert.Equal("2024Q1", data.PeriodStart);
        Assert.Equal("2024Q4", data.PeriodEnd);
        Assert.Equal(4, data.Columns.Count);

        // 3 точки: Q1 (полный), Q2 (нули), Q4 (частичный). Q3 пропущен.
        Assert.Equal(3, data.InputDataPoints.Count);
        Assert.DoesNotContain(data.InputDataPoints, p => p.FmPeriod == "2024Q3");

        var q1 = data.InputDataPoints.Single(p => p.FmPeriod == "2024Q1");
        Assert.Equal(100_000, q1.Summ);
        Assert.Equal(1000, q1.Amount);
        Assert.Equal(100, q1.Cost);

        var q2 = data.InputDataPoints.Single(p => p.FmPeriod == "2024Q2");
        Assert.Equal(0, q2.Summ);
        Assert.Equal(0, q2.Amount);
        Assert.Equal(0, q2.Cost);

        // Q4 — частично заполнен: Summ есть, остальные пустые → подменяются на 0.
        var q4 = data.InputDataPoints.Single(p => p.FmPeriod == "2024Q4");
        Assert.Equal(5_000_000, q4.Summ);
        Assert.Equal(0, q4.Amount);
        Assert.Equal(0, q4.Cost);
    }

    // ─────────── ReadGeneralScheduleData — layout-1 (Репино-Парк) ───────────

    /// <summary>
    /// Раскладка из файла «2025.12.08 UB0DZG__НСИ_ЖК Репино-Парк»:
    /// шапка «Тип помещения = Квартиры/Нежилое/Кладовые/Машиноместа» ВЫШЕ «Год»,
    /// между «Год» и «Квартал» — помесячная подшапка (1 строка с дублированными
    /// годами), Amount-строка обозначена общим «Площадь, кв.м» — категория из шапки.
    /// Без поддержки layout-1 парсер бы выдал «не найдено ни одной таблицы».
    /// </summary>
    [Fact]
    public void ReadGeneralScheduleData_Layout1_ResolvesCategoryFromHeader_AndSkipsMonthsRow()
    {
        var bytes = BuildRepinoParkLayoutXlsx();
        using var stream = new MemoryStream(bytes);

        var data = FinModelImportMapper.ReadGeneralScheduleData(stream);

        // Все 4 категории (Квартиры/Нежилое/Кладовые/Машиноместа) распознаны.
        Assert.Equal(4, data.Categories.Count);
        Assert.Contains(data.Categories, c => c.FmCode == FinModelImportMapper.FmCodeApartment);
        Assert.Contains(data.Categories, c => c.FmCode == FinModelImportMapper.FmCodeNonResidential);
        Assert.Contains(data.Categories, c => c.FmCode == FinModelImportMapper.FmCodeStoreroom);
        Assert.Contains(data.Categories, c => c.FmCode == FinModelImportMapper.FmCodeParking);

        // Колонки = 4 квартала 2024 года.
        Assert.Equal(4, data.Columns.Count);
        Assert.Equal("2024Q1", data.PeriodStart);
        Assert.Equal("2024Q4", data.PeriodEnd);

        // Q1 квартир — Amount=1500, Cost=120000, Summ=180000000 (значения из фикстуры).
        var q1Apt = data.InputDataPoints.Single(p =>
            p.FmPeriod == "2024Q1" && p.FmCode == FinModelImportMapper.FmCodeApartment);
        Assert.Equal(1500, q1Apt.Amount);
        Assert.Equal(120000, q1Apt.Cost);
        Assert.Equal(180_000_000, q1Apt.Summ);

        // Фактический блок (числа 99/98/97 в фикстуре) НЕ должен попасть в точки.
        Assert.DoesNotContain(data.InputDataPoints, p => p.Amount == 99);
        Assert.DoesNotContain(data.InputDataPoints, p => p.Cost == 98);
        Assert.DoesNotContain(data.InputDataPoints, p => p.Summ == 97);
    }

    // ─────────── BuildNextVersionTitle — sequenced titles ───────────

    [Fact]
    public void BuildNextVersionTitle_EmptyList_ReturnsBasePrefix()
    {
        Assert.Equal("Версия - Перенос из Эксель",
            FinModelImportMapper.BuildNextVersionTitle([]));
        Assert.Equal("Версия - Перенос из Эксель",
            FinModelImportMapper.BuildNextVersionTitle(null!));
    }

    [Fact]
    public void BuildNextVersionTitle_BaseExists_Returns2()
    {
        var existing = new[]
        {
            new FmModelVersionRaw { ID = 1, Title = "Версия - Перенос из Эксель" },
        };
        Assert.Equal("Версия - Перенос из Эксель 2",
            FinModelImportMapper.BuildNextVersionTitle(existing));
    }

    [Fact]
    public void BuildNextVersionTitle_BaseAnd2And5_Returns6()
    {
        var existing = new[]
        {
            new FmModelVersionRaw { ID = 1, Title = "Версия - Перенос из Эксель" },
            new FmModelVersionRaw { ID = 2, Title = "Версия - Перенос из Эксель 2" },
            new FmModelVersionRaw { ID = 3, Title = "Версия - Перенос из Эксель 5" },
            new FmModelVersionRaw { ID = 4, Title = "Какая-то другая версия" },
        };
        Assert.Equal("Версия - Перенос из Эксель 6",
            FinModelImportMapper.BuildNextVersionTitle(existing));
    }

    [Fact]
    public void BuildNextVersionTitle_OnlyUnrelatedTitles_ReturnsBase()
    {
        var existing = new[]
        {
            new FmModelVersionRaw { ID = 1, Title = "Финплан 2024" },
            new FmModelVersionRaw { ID = 2, Title = "" },
        };
        Assert.Equal("Версия - Перенос из Эксель",
            FinModelImportMapper.BuildNextVersionTitle(existing));
    }

    // ─────────── ApplyAsync — happy path ───────────

    [Fact]
    public async Task ApplyAsync_PlanFile_CreatesVersionAndInputData_AndLinksThem()
    {
        var bytes = BuildReferencePlanXlsx();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var result = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // fmmodel создан.
        _mockCrud.Verify(c => c.CreateFmModelAsync(
            It.IsAny<FmModelCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Версия создана с правильным Title и FMModelID.
        _mockCrud.Verify(c => c.CreateFmModelVersionAsync(
            It.Is<FmModelVersionCreateRequest>(r =>
                r.FMModelID == FmModelId
                && r.Title == "Версия - Перенос из Эксель"
                && r.FMModel != null && r.FMModel.ID == FmModelId),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // 8 точек inputdata создано: квартиры (4 квартала с данными) +
        // м/м (4 квартала с явными 0). Нежилые в фикстуре полностью пустые —
        // 0 точек (см. doc 112 v1.5).
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.IsAny<InputDataCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(8));

        // Каждая точка — линкуется к версии.
        _mockCrud.Verify(c => c.LinkInputDataToVersionAsync(
            VersionId, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(8));

        // Конкретный пример из спеки (Q1, квартиры) — Code.ID=20, Summ=243685102.
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r =>
                r.FMModelVersionID == VersionId
                && r.FMPeriod == "2024Q1"
                && r.Code != null && r.Code.ID == CodeApartmentId
                && r.Summ == 243685102
                && r.Percent == 0d),
            It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "fmmodel_version_failed");
        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "inputdata_create_failed");
    }

    // ─────────── ApplyAsync — идемпотентность ───────────

    [Fact]
    public async Task ApplyAsync_ExistingVersion_CreatesSecondVersion_WithSequencedTitle()
    {
        var bytes = BuildReferencePlanXlsx();
        _fileStorage.Put("plan.xlsx", bytes);

        // Сценарий: у Финмодели уже есть одна версия с базовым Title. Импорт должен
        // создать ВТОРУЮ версию (Title с суффиксом « 2»), а не переиспользовать
        // существующую. Заказчик хочет историю переносов (см. doc 112 v1.3).
        _mockListView
            .Setup(c => c.GetFmModelVersionsByModelAsync(FmModelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmModelVersionRaw>
            {
                Data = [new FmModelVersionRaw
                {
                    ID = VersionId - 1, FMModelID = FmModelId,
                    Title = "Версия - Перенос из Эксель",
                }],
                Total = 1,
            });

        FmModelVersionCreateRequest? capturedReq = null;
        _mockCrud
            .Setup(c => c.CreateFmModelVersionAsync(
                It.IsAny<FmModelVersionCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FmModelVersionCreateRequest, CancellationToken>((req, _) => capturedReq = req)
            .ReturnsAsync((FmModelVersionCreateRequest req, CancellationToken _) => new FmModelVersionRaw
            {
                ID = VersionId, FMModelID = req.FMModelID, Title = req.Title,
            });

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // НОВАЯ версия создана с Title-суффиксом « 2».
        _mockCrud.Verify(c => c.CreateFmModelVersionAsync(
            It.IsAny<FmModelVersionCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.NotNull(capturedReq);
        Assert.Equal("Версия - Перенос из Эксель 2", capturedReq!.Title);

        // 8 точек inputdata создано в НОВОЙ (заведомо пустой) версии. Pre-check
        // inputdata-by-version больше не выполняется (новая версия = нет дубликатов).
        // Нежилые в фикстуре пустые → 0 точек по этой категории (doc 112 v1.5).
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.Is<InputDataCreateRequest>(r => r.FMModelVersionID == VersionId),
            It.IsAny<CancellationToken>()),
            Times.Exactly(8));
    }

    [Fact]
    public async Task ApplyAsync_RepeatedImport_AlwaysCreatesNewVersion_NoDedupAtPointLevel()
    {
        // Сценарий: уже есть «Версия - Перенос из Эксель» и «Версия - Перенос из Эксель 2».
        // Импорт должен создать «… 3» — даже если в одной из старых версий уже лежат точки
        // (period, codeId) — pre-check inputdata-by-version отключён для новых версий.
        var bytes = BuildReferencePlanXlsx();
        _fileStorage.Put("plan.xlsx", bytes);

        _mockListView
            .Setup(c => c.GetFmModelVersionsByModelAsync(FmModelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmModelVersionRaw>
            {
                Data = [
                    new FmModelVersionRaw { ID = 100, FMModelID = FmModelId, Title = "Версия - Перенос из Эксель" },
                    new FmModelVersionRaw { ID = 101, FMModelID = FmModelId, Title = "Версия - Перенос из Эксель 2" },
                ],
                Total = 2,
            });

        FmModelVersionCreateRequest? capturedReq = null;
        _mockCrud
            .Setup(c => c.CreateFmModelVersionAsync(
                It.IsAny<FmModelVersionCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FmModelVersionCreateRequest, CancellationToken>((req, _) => capturedReq = req)
            .ReturnsAsync((FmModelVersionCreateRequest req, CancellationToken _) => new FmModelVersionRaw
            {
                ID = VersionId, FMModelID = req.FMModelID, Title = req.Title,
            });

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        Assert.NotNull(capturedReq);
        Assert.Equal("Версия - Перенос из Эксель 3", capturedReq!.Title);

        // Все 8 точек создаются в новой версии — pre-check старых версий не влияет.
        // (Квартиры 4 + м/м 4; нежилые в фикстуре пустые. См. doc 112 v1.5.)
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.IsAny<InputDataCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(8));
    }

    // ─────────── ApplyAsync — деградации ───────────

    [Fact]
    public async Task ApplyAsync_InputDataCodesUnavailable_AddsErrorAndSkipsVersion()
    {
        var bytes = BuildReferencePlanXlsx();
        _fileStorage.Put("plan.xlsx", bytes);

        // Любая категория — сетевая ошибка резолва fmcode. Достаточно одного,
        // чтобы маппер вышел с inputdata_codes_unavailable; дальнейшие запросы не идут.
        _mockListView
            .Setup(c => c.FindFmCodeByCodeAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("404 Not Found"));

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var result = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // fmmodel создан (это идёт раньше), а вот версия и inputdata — нет.
        _mockCrud.Verify(c => c.CreateFmModelAsync(
            It.IsAny<FmModelCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCrud.Verify(c => c.CreateFmModelVersionAsync(
            It.IsAny<FmModelVersionCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.IsAny<InputDataCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Contains(result.Errors, e => e.ErrorCode == "inputdata_codes_unavailable");
    }

    [Fact]
    public async Task ApplyAsync_CodeNotInDictionary_SkipsCategoryAndReportsMissing()
    {
        var bytes = BuildReferencePlanXlsx();
        _fileStorage.Put("plan.xlsx", bytes);

        // В справочнике нет Code «040» — Visary возвращает пустой Data,
        // точки этой категории должны быть пропущены, остальные категории — созданы.
        _mockListView
            .Setup(c => c.FindFmCodeByCodeAsync(
                FinModelImportMapper.FmCodeParking, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmCodeRaw> { Data = [], Total = 0 });

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var result = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // Машиноместа пропущены (Title не найден в справочнике). Нежилые в фикстуре —
        // полностью пустые (doc 112 v1.5), точек по ним нет. Остаются только квартиры:
        // 4 квартала × 1 категория = 4 точки.
        _mockCrud.Verify(c => c.CreateInputDataAsync(
            It.IsAny<InputDataCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(4));
        Assert.Contains(result.Errors, e => e.ErrorCode == "inputdata_code_not_found");
    }

    // ─────────── Helpers ───────────

    /// <summary>
    /// Собирает XLSX по эталонной раскладке листа «План» (см. UBCFBE_Журавли):
    ///   r3 A=«Год», C=2024 (год только в первой колонке группы — forward-fill);
    ///   r5 A=«Квартал», B=«Сумма», C..F=«1..4 кв»;
    ///   r6 A=«Площадь, кв.м»               — Amount квартир  ; B/C..F значения.
    ///   r7 A=«Стоимость 1 кв.м»            — Cost квартир.
    ///   r8 A=«Сумма от продажи квартир»    — Summ квартир.
    ///   r9..r11 — нежилые (Площадь / Стоимость / Сумма от продажи нежил. помещений).
    ///   r12..r14 — м/м (Колич-во м/м / Стоимость 1 м/м / Сумма от продажи м/м).
    ///   r15 A=«ВСЕГО ВЫРУЧКА» — игнорируется парсером.
    /// Точные числовые значения для Q1 квартир — из спеки (Summ=243685102, Amount=2459.85, Cost=99065.03).
    /// </summary>
    private static byte[] BuildReferencePlanXlsx()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Общий график");

            // Таблица 1 — Квартиры (r3..r14). r3=Год, r4=Квартал, r5=План,
            // r6/r7/r8=Площадь/Стоимость/Доход (план — что мы и хотим взять),
            // r9=Доход накопл. (skip), r10=Факт-маркер, r11..r14 = факт-данные (skip).
            ws.Cell(3, 1).Value = "Год";
            ws.Cell(3, 3).Value = 2024;
            ws.Cell(4, 1).Value = "Квартал";
            ws.Cell(4, 2).Value = "Сумма";
            ws.Cell(4, 3).Value = "1 кв";
            ws.Cell(4, 4).Value = "2 кв";
            ws.Cell(4, 5).Value = "3 кв";
            ws.Cell(4, 6).Value = "4 кв";
            ws.Cell(5, 1).Value = "План";

            ws.Cell(6, 1).Value = "Квартиры, кв.м";
            ws.Cell(6, 3).Value = 2459.85;
            ws.Cell(6, 4).Value = 882.77;
            ws.Cell(6, 5).Value = 300;
            ws.Cell(6, 6).Value = 350;

            ws.Cell(7, 1).Value = "Стоимость 1 кв.м";
            ws.Cell(7, 3).Value = 99065.03;
            ws.Cell(7, 4).Value = 100315.12;
            ws.Cell(7, 5).Value = 94000;
            ws.Cell(7, 6).Value = 94750;

            ws.Cell(8, 1).Value = "Доход";
            ws.Cell(8, 3).Value = 243685102;
            ws.Cell(8, 4).Value = 88555180;
            ws.Cell(8, 5).Value = 28200000;
            ws.Cell(8, 6).Value = 33162500;

            // Skip-block: накопл. + Факт + 4 факт-строки. Числа здесь подобраны
            // НЕНУЛЕВЫЕ — тест проверяет что парсер их НЕ берёт.
            ws.Cell(9, 1).Value = "Доход накопл. Итогом";
            ws.Cell(9, 3).Value = 999_999_999;
            ws.Cell(10, 1).Value = "Факт";
            ws.Cell(11, 1).Value = "Квартиры, кв.м";
            ws.Cell(11, 3).Value = 11111;
            ws.Cell(12, 1).Value = "Стоимость 1 кв.м";
            ws.Cell(12, 3).Value = 12345;
            ws.Cell(13, 1).Value = "Доход";
            ws.Cell(13, 3).Value = 88_888_888;
            ws.Cell(14, 1).Value = "Доход накопл. Итогом";

            // Таблица 2 — Нежилые ПСН (нулевые значения — план = 0).
            ws.Cell(20, 1).Value = "Год";
            ws.Cell(20, 3).Value = 2024;
            ws.Cell(21, 1).Value = "Квартал";
            ws.Cell(21, 2).Value = "Сумма";
            ws.Cell(21, 3).Value = "1 кв";
            ws.Cell(21, 4).Value = "2 кв";
            ws.Cell(21, 5).Value = "3 кв";
            ws.Cell(21, 6).Value = "4 кв";
            ws.Cell(22, 1).Value = "План";
            ws.Cell(23, 1).Value = "Нежилые помещения, кв.м";
            ws.Cell(24, 1).Value = "Стоимость 1 кв.м";
            ws.Cell(25, 1).Value = "Доход";

            // Таблица 3 — Машиноместа (ЯВНЫЕ нули во всех 4 кварталах — план = 0).
            // С этими нулями точки эмитятся (см. doc 112 v1.5: явный 0 ≠ пустая ячейка).
            ws.Cell(30, 1).Value = "Год";
            ws.Cell(30, 3).Value = 2024;
            ws.Cell(31, 1).Value = "Квартал";
            ws.Cell(31, 2).Value = "Сумма";
            ws.Cell(31, 3).Value = "1 кв";
            ws.Cell(31, 4).Value = "2 кв";
            ws.Cell(31, 5).Value = "3 кв";
            ws.Cell(31, 6).Value = "4 кв";
            ws.Cell(32, 1).Value = "План";
            ws.Cell(33, 1).Value = "Машиноместа, шт.";
            ws.Cell(34, 1).Value = "Стоимость 1 м/м";
            ws.Cell(35, 1).Value = "Доход";
            for (int c = 3; c <= 6; c++)
            {
                ws.Cell(33, c).Value = 0;
                ws.Cell(34, c).Value = 0;
                ws.Cell(35, c).Value = 0;
            }

            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Фикстура для проверки разграничения «пустая ячейка» vs «явный 0».
    /// Одна таблица квартир: Q1 — полные данные, Q2 — явные нули, Q3 — пусто,
    /// Q4 — заполнен только Summ.
    /// </summary>
    private static byte[] BuildPartialEmptyPlanXlsx()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Общий график");

            ws.Cell(3, 1).Value = "Год";
            ws.Cell(3, 3).Value = 2024;
            ws.Cell(4, 1).Value = "Квартал";
            ws.Cell(4, 2).Value = "Сумма";
            ws.Cell(4, 3).Value = "1 кв";
            ws.Cell(4, 4).Value = "2 кв";
            ws.Cell(4, 5).Value = "3 кв";
            ws.Cell(4, 6).Value = "4 кв";
            ws.Cell(5, 1).Value = "План";

            // Amount-строка.
            ws.Cell(6, 1).Value = "Квартиры, кв.м";
            ws.Cell(6, 3).Value = 1000;  // Q1 — заполнен
            ws.Cell(6, 4).Value = 0;     // Q2 — явный 0
            // Q3 (c=5) — НЕ записываем (пустая ячейка)
            // Q4 (c=6) — НЕ записываем

            // Cost-строка.
            ws.Cell(7, 1).Value = "Стоимость 1 кв.м";
            ws.Cell(7, 3).Value = 100;
            ws.Cell(7, 4).Value = 0;
            // Q3, Q4 — пусто

            // Summ-строка.
            ws.Cell(8, 1).Value = "Доход";
            ws.Cell(8, 3).Value = 100_000;
            ws.Cell(8, 4).Value = 0;
            // Q3 — пусто
            ws.Cell(8, 6).Value = 5_000_000;  // Q4 — только Summ заполнен

            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Раскладка из реального файла заказчика «Репино-Парк». Отличия от
    /// <see cref="BuildReferencePlanXlsx"/>:
    /// <list type="bullet">
    ///   <item>Шапка таблицы: A=«НПС»/«Этап»/«Тип помещения» с категорией в B-колонке
    ///     ВЫШЕ строки «Год».</item>
    ///   <item>Между «Год» и «Квартал» — помесячная подшапка (строка с дублированными
    ///     годами в C/D…).</item>
    ///   <item>Amount-строка имеет ОБЩИЙ A-текст «Площадь, кв.м» / «Машиноместа, шт.»,
    ///     без маркера категории.</item>
    /// </list>
    /// 4 таблицы, по одной на вид помещения; в каждой — 4 квартала 2024.
    /// </summary>
    private static byte[] BuildRepinoParkLayoutXlsx()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Общий график");
            int row = 1;

            void EmitTable(string kindTitle, double amountQ1, double costQ1, double summQ1)
            {
                ws.Cell(row, 1).Value = "НПС"; ws.Cell(row, 2).Value = 504; row++;
                ws.Cell(row, 1).Value = "Этап"; ws.Cell(row, 2).Value = 1; row++;
                ws.Cell(row, 1).Value = "Тип помещения";
                ws.Cell(row, 2).Value = kindTitle; row++;

                int yearRow = row;
                ws.Cell(yearRow, 1).Value = "Год";
                ws.Cell(yearRow, 3).Value = 2024;   // forward-fill — год только в первой колонке группы
                row++;

                // Помесячная подшапка между «Год» и «Квартал» (как в реальном файле).
                int monthsRow = row;
                ws.Cell(monthsRow, 3).Value = 2024;
                ws.Cell(monthsRow, 4).Value = 2024;
                ws.Cell(monthsRow, 5).Value = 2024;
                ws.Cell(monthsRow, 6).Value = 2024;
                row++;

                int quarterRow = row;
                ws.Cell(quarterRow, 1).Value = "Квартал";
                ws.Cell(quarterRow, 2).Value = "Сумма";
                ws.Cell(quarterRow, 3).Value = "1 кв";
                ws.Cell(quarterRow, 4).Value = "2 кв";
                ws.Cell(quarterRow, 5).Value = "3 кв";
                ws.Cell(quarterRow, 6).Value = "4 кв";
                row++;

                ws.Cell(row, 1).Value = "План"; row++; // маркер-разделитель

                // Amount/Cost/Summ — общие A-тексты, БЕЗ маркера категории.
                ws.Cell(row, 1).Value = "Площадь, кв.м";
                ws.Cell(row, 3).Value = amountQ1;
                ws.Cell(row, 4).Value = amountQ1;
                ws.Cell(row, 5).Value = amountQ1;
                ws.Cell(row, 6).Value = amountQ1;
                row++;

                ws.Cell(row, 1).Value = "Стоимость 1 кв.м";
                ws.Cell(row, 3).Value = costQ1;
                ws.Cell(row, 4).Value = costQ1;
                ws.Cell(row, 5).Value = costQ1;
                ws.Cell(row, 6).Value = costQ1;
                row++;

                ws.Cell(row, 1).Value = "Общая сумма";
                ws.Cell(row, 3).Value = summQ1;
                ws.Cell(row, 4).Value = summQ1;
                ws.Cell(row, 5).Value = summQ1;
                ws.Cell(row, 6).Value = summQ1;
                row++;

                // Факт-блок: НЕ должен попасть в точки. Числа 99/98/97 — маркер
                // утечки (см. assertion в тесте).
                ws.Cell(row, 1).Value = "Факт"; row++;
                ws.Cell(row, 1).Value = "Площадь, кв.м";
                ws.Cell(row, 3).Value = 99; row++;
                ws.Cell(row, 1).Value = "Стоимость 1 кв.м";
                ws.Cell(row, 3).Value = 98; row++;
                ws.Cell(row, 1).Value = "Общая сумма";
                ws.Cell(row, 3).Value = 97; row++;

                row++; // визуальный разрыв между таблицами
            }

            EmitTable("Квартиры",    amountQ1: 1500, costQ1: 120000, summQ1: 180_000_000);
            EmitTable("Нежилое",     amountQ1: 800,  costQ1: 70000,  summQ1: 56_000_000);
            EmitTable("Кладовые",    amountQ1: 200,  costQ1: 40000,  summQ1: 8_000_000);
            EmitTable("Машиноместа", amountQ1: 50,   costQ1: 700000, summQ1: 35_000_000);

            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }
}
