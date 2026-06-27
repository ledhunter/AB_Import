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
/// Покрытие каскада «Данные клиента» (<c>clientdata</c>) импорта Финмодели:
/// помимо <see cref="InputDataCreateRequest"/> в ту же версию Финмодели маппер
/// создаёт по одной записи <see cref="ClientDataCreateRequest"/> на каждый непустой
/// (Quarter × RoomKind) из листа «Общий график» второго файла. Маппинг префиксов:
/// Квартира→Residential, Нежилое→Nonresidential, Кладовая→Othernonresidential,
/// Машиноместо→Parking. См. doc 150.
/// </summary>
public class FinModelClientDataTests : IDisposable
{
    private const int ProjectId = 4584;
    private const int SiteId = 7890;
    private const int FmModelId = 48;
    private const int VersionId = 217;
    private const int CodeApartmentId      = 20;
    private const int CodeNonResidentialId = 21;
    private const int CodeStoreroomId      = 22;
    private const int CodeParkingId        = 23;

    // Visary RoomKind ID-стек (синтетический): по списку doc 138.
    private const int RkApartmentId   = 3;
    private const int RkNonResId      = 4;
    private const int RkStoreroomId   = 5;
    private const int RkParkingId     = 6;

    private readonly FinModelImportMapper _mapper;
    private readonly VisaryDbContext _dbContext;
    private readonly Mock<ICrudClient> _mockCrud;
    private readonly Mock<IListViewClient> _mockListView;
    private readonly TestFileStorage _fileStorage;
    private readonly ServiceProvider _serviceProvider;
    private int _nextInputDataId = 30_000;
    private int _nextClientDataId = 40_000;

    public FinModelClientDataTests()
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

        // fmmodel: pre-check пусто → создаём.
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

        // Plan-fmcode справочник: 4 поддерживаемых вида.
        SetupFmCode(FinModelImportMapper.FmCodeApartment,      CodeApartmentId,      "010 Продажа квартиры (план)");
        SetupFmCode(FinModelImportMapper.FmCodeNonResidential, CodeNonResidentialId, "020 Продажа нежилые ( ком) ПСН (план)");
        SetupFmCode(FinModelImportMapper.FmCodeStoreroom,      CodeStoreroomId,      "030 Продажа иные нежилые (кладовки) (план)");
        SetupFmCode(FinModelImportMapper.FmCodeParking,        CodeParkingId,        "040 Продажа м/м (план)");
        // Прочие коды — пустой ответ (включая 604 Equity и Fact-коды).
        _mockListView
            .Setup(c => c.FindFmCodeByCodeAsync(
                It.Is<string>(s =>
                    s != FinModelImportMapper.FmCodeApartment
                 && s != FinModelImportMapper.FmCodeNonResidential
                 && s != FinModelImportMapper.FmCodeStoreroom
                 && s != FinModelImportMapper.FmCodeParking),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmCodeRaw> { Data = [], Total = 0 });

        // Версии и inputdata.
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
            });
        _mockCrud
            .Setup(c => c.LinkInputDataToVersionAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // RoomKind-словарь Visary: 4 канонических Title с RoomCategory из памяти doc 138.
        _mockListView
            .Setup(c => c.ListRoomKindsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomKindRaw>
            {
                Data =
                [
                    new RoomKindRaw { ID = RkApartmentId, Title = "Квартира",          RoomCategory = 0 },
                    new RoomKindRaw { ID = RkNonResId,    Title = "Нежилое помещение", RoomCategory = 1 },
                    new RoomKindRaw { ID = RkStoreroomId, Title = "Кладовая",          RoomCategory = 3 },
                    new RoomKindRaw { ID = RkParkingId,   Title = "Машиноместо",       RoomCategory = 2 },
                ],
                Total = 4,
            });

        // Site Title для Site.Title в payload ClientData.
        _mockListView
            .Setup(c => c.GetSiteByProjectAndIdAsync(ProjectId, SiteId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConstructionSiteRaw { ID = SiteId, Title = "Маньчжурский орех рнс0706 1" });

        // ClientData CRUD — успех по умолчанию, тесты переопределяют.
        _mockCrud
            .Setup(c => c.CreateClientDataAsync(It.IsAny<ClientDataCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClientDataCreateRequest req, CancellationToken _) => new ClientDataRaw
            {
                ID = Interlocked.Increment(ref _nextClientDataId),
                Cost = req.Cost, Rates = req.Rates,
                RoomKind = req.RoomKind, Site = req.Site,
                Date = req.Date, PeriodStartDate = req.PeriodStartDate,
            });

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
            .UseInMemoryDatabase($"FinModelClientDataTest_{Guid.NewGuid()}")
            .Options;
        _dbContext = new VisaryDbContext(options);
        _dbContext.ConstructionSites.Add(new ConstructionSite
        {
            Id = SiteId, Title = "Маньчжурский орех рнс0706 1", ConstructionProjectId = ProjectId,
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

    // ─────────── Unit-тесты TryConvertFmPeriodToDate ───────────

    [Theory]
    [InlineData("2026Q1", "2026-01-01")]
    [InlineData("2026Q2", "2026-04-01")]
    [InlineData("2026Q3", "2026-07-01")]
    [InlineData("2027Q4", "2027-10-01")] // Q4 → первый день октября, не января следующего года
    [InlineData("2023Q1", "2023-01-01")]
    public void TryConvertFmPeriodToDate_ValidPeriods_ReturnsCorrectDate(
        string fmPeriod, string expectedStart)
    {
        Assert.True(FinModelImportMapper.TryConvertFmPeriodToDate(
            fmPeriod, out var start));
        Assert.Equal(expectedStart, start.ToString("yyyy-MM-dd"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("2026Q5")]
    [InlineData("Q1")]
    [InlineData("garbage")]
    [InlineData("2026")]
    public void TryConvertFmPeriodToDate_InvalidPeriods_ReturnsFalse(string? fmPeriod)
    {
        Assert.False(FinModelImportMapper.TryConvertFmPeriodToDate(
            fmPeriod!, out _));
    }

    // ─────────── Unit-тесты BuildClientDataRequest ───────────

    [Fact]
    public void BuildClientDataRequest_Apartment_PopulatesResidentialPrefixedFields()
    {
        var siteRef = new VisaryRef { ID = SiteId, Title = "Тест сайт" };
        var roomKind = new RoomKindRaw { ID = RkApartmentId, Title = "Квартира", RoomCategory = 0 };
        var binding = new FinModelImportMapper.ClientDataKindBinding(
            "Квартира", Prefix: "Residential");
        var point = new FinModelImportMapper.FinModelPlanInputDataPoint(
            FmPeriod: "2026Q2", FmCode: FinModelImportMapper.FmCodeApartment,
            Summ: 1_000_000, Amount: 50.5, Cost: 200_000);

        var req = FinModelImportMapper.BuildClientDataRequest(
            siteRef, roomKind, binding, point,
            periodStart: new DateTime(2026, 4, 1));

        Assert.Equal(200_000, req.Cost);
        Assert.Equal(50.5, req.Rates);
        Assert.Equal(RkApartmentId, req.RoomKind.ID);
        Assert.Equal("Квартира", req.RoomKind.Title);
        Assert.Equal(SiteId, req.Site.ID);
        Assert.Equal("2026-04-01", req.PeriodStartDate);
        Assert.Equal("2026-04-01", req.Date);

        // Residential-префикс — заполнено.
        Assert.Equal(200_000, req.ResidentialCost);
        Assert.Equal(50.5, req.ResidentialRates);

        // Остальные prefixed — 0.
        Assert.Equal(0, req.NonresidentialCost);
        Assert.Equal(0, req.NonresidentialRates);
        Assert.Equal(0, req.OtherNonresidentialCost);
        Assert.Equal(0, req.OthernonresidentialRates);
        Assert.Equal(0, req.ParkingCost);
        Assert.Equal(0, req.ParkingRates);

        // ODCount* — все 0.
        Assert.Equal(0, req.ODCount);
        Assert.Equal(0, req.ODCountRes);
        Assert.Equal(0, req.ODCountNonRes);
        Assert.Equal(0, req.ODCountOtherNonRes);
        Assert.Equal(0, req.ODCountParking);
    }

    [Theory]
    [InlineData(FinModelImportMapper.FmCodeNonResidential, "Нежилое помещение", "Nonresidential")]
    [InlineData(FinModelImportMapper.FmCodeStoreroom,      "Кладовая",          "Othernonresidential")]
    [InlineData(FinModelImportMapper.FmCodeParking,        "Машиноместо",       "Parking")]
    public void BuildClientDataRequest_OtherKinds_PopulateOnlyOwnPrefix(
        string fmCode, string kindTitle, string prefix)
    {
        var siteRef = new VisaryRef { ID = SiteId, Title = "T" };
        var roomKind = new RoomKindRaw { ID = 99, Title = kindTitle };
        var binding = new FinModelImportMapper.ClientDataKindBinding(kindTitle, prefix);
        var point = new FinModelImportMapper.FinModelPlanInputDataPoint(
            FmPeriod: "2026Q1", FmCode: fmCode,
            Summ: 999, Amount: 10, Cost: 20);

        var req = FinModelImportMapper.BuildClientDataRequest(
            siteRef, roomKind, binding, point,
            periodStart: new DateTime(2026, 1, 1));

        Assert.Equal(20, req.Cost);
        Assert.Equal(10, req.Rates);
        Assert.Equal("2026-01-01", req.PeriodStartDate);
        Assert.Equal("2026-01-01", req.Date);

        // Свой префикс — числа есть. Остальные prefixed — 0.
        if (prefix == "Nonresidential")
        {
            Assert.Equal(20, req.NonresidentialCost);
            Assert.Equal(10, req.NonresidentialRates);
            Assert.Equal(0, req.ResidentialCost);
            Assert.Equal(0, req.OtherNonresidentialCost);
            Assert.Equal(0, req.ParkingCost);
        }
        else if (prefix == "Othernonresidential")
        {
            Assert.Equal(20, req.OtherNonresidentialCost);
            Assert.Equal(10, req.OthernonresidentialRates);
            Assert.Equal(0, req.ResidentialCost);
            Assert.Equal(0, req.NonresidentialCost);
            Assert.Equal(0, req.ParkingCost);
        }
        else if (prefix == "Parking")
        {
            Assert.Equal(20, req.ParkingCost);
            Assert.Equal(10, req.ParkingRates);
            Assert.Equal(0, req.ResidentialCost);
            Assert.Equal(0, req.NonresidentialCost);
            Assert.Equal(0, req.OtherNonresidentialCost);
        }
    }

    // ─────────── ApplyAsync — happy path ───────────

    [Fact]
    public async Task ApplyAsync_PlanFile_CreatesClientDataPerKindPerQuarter_WithCorrectPrefixes()
    {
        // 4 квартала × 4 вида помещений = 16 точек Plan, 16 ClientData.
        var bytes = BuildPlanXlsxAllFourKinds();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // 16 ClientData = 4 кв × 4 RoomKind.
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.IsAny<ClientDataCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(16));

        // Site всегда передаётся с Title из listview.
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.Is<ClientDataCreateRequest>(r =>
                r.Site != null && r.Site.ID == SiteId
                && r.Site.Title == "Маньчжурский орех рнс0706 1"),
            It.IsAny<CancellationToken>()),
            Times.Exactly(16));

        // Квартиры Q1: ResidentialCost=10000, ResidentialRates=100,
        // PeriodStartDate=Date=2026-01-01 (начало того же квартала).
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.Is<ClientDataCreateRequest>(r =>
                r.RoomKind != null && r.RoomKind.ID == RkApartmentId
                && r.Cost == 10_000d && r.Rates == 100d
                && r.ResidentialCost == 10_000d && r.ResidentialRates == 100d
                && r.NonresidentialCost == 0d && r.ParkingCost == 0d && r.OtherNonresidentialCost == 0d
                && r.PeriodStartDate == "2026-01-01" && r.Date == "2026-01-01"),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Машиноместа Q4: ParkingCost/Rates заполнены,
        // PeriodStartDate=Date=2026-10-01 (БЕЗ перехода в следующий квартал).
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.Is<ClientDataCreateRequest>(r =>
                r.RoomKind != null && r.RoomKind.ID == RkParkingId
                && r.ParkingCost == 50_000d && r.ParkingRates == 5d
                && r.ResidentialCost == 0d && r.NonresidentialCost == 0d && r.OtherNonresidentialCost == 0d
                && r.PeriodStartDate == "2026-10-01" && r.Date == "2026-10-01"),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Нежилые: префикс Nonresidential.
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.Is<ClientDataCreateRequest>(r =>
                r.RoomKind != null && r.RoomKind.ID == RkNonResId
                && r.NonresidentialCost == 30_000d && r.NonresidentialRates == 30d),
            It.IsAny<CancellationToken>()),
            Times.Exactly(4));

        // Кладовые: префикс Othernonresidential.
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.Is<ClientDataCreateRequest>(r =>
                r.RoomKind != null && r.RoomKind.ID == RkStoreroomId
                && r.OtherNonresidentialCost == 40_000d && r.OthernonresidentialRates == 40d),
            It.IsAny<CancellationToken>()),
            Times.Exactly(4));

        Assert.DoesNotContain(apply.Errors, e =>
            e.ErrorCode is "clientdata_roomkind_unavailable"
                or "clientdata_roomkind_not_found"
                or "clientdata_create_failed");
    }

    // ─────────── ApplyAsync — fallback site title ───────────

    [Fact]
    public async Task ApplyAsync_SiteListViewReturnsNull_UsesFallbackTitle()
    {
        _mockListView
            .Setup(c => c.GetSiteByProjectAndIdAsync(ProjectId, SiteId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConstructionSiteRaw?)null);

        var bytes = BuildPlanXlsxApartmentsOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // Title в Site должен быть фолбэк-форматом «Объект #ID».
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.Is<ClientDataCreateRequest>(r =>
                r.Site != null && r.Site.ID == SiteId
                && r.Site.Title == $"Объект #{SiteId}"),
            It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        Assert.DoesNotContain(apply.Errors, e => e.ErrorCode == "clientdata_create_failed");
    }

    // ─────────── ApplyAsync — RoomKind словарь недоступен ───────────

    [Fact]
    public async Task ApplyAsync_RoomKindListViewThrows_AddsRowError_SkipsCascade()
    {
        _mockListView
            .Setup(c => c.ListRoomKindsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("500 Internal Server Error"));

        var bytes = BuildPlanXlsxApartmentsOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        Assert.Contains(apply.Errors, e => e.ErrorCode == "clientdata_roomkind_unavailable");
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.IsAny<ClientDataCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─────────── ApplyAsync — RoomKind отсутствует в словаре ───────────

    [Fact]
    public async Task ApplyAsync_RoomKindMissingFromDictionary_AddsRowError_OthersContinue()
    {
        // Словарь без «Машиноместо» → все Parking-точки пропущены, ошибка собирается в конце.
        _mockListView
            .Setup(c => c.ListRoomKindsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomKindRaw>
            {
                Data =
                [
                    new RoomKindRaw { ID = RkApartmentId, Title = "Квартира", RoomCategory = 0 },
                    // Машиноместо отсутствует
                ],
                Total = 1,
            });

        var bytes = BuildPlanXlsxApartmentsAndParkingOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // 4 квартиры созданы; 4 парковочных — пропущены.
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.Is<ClientDataCreateRequest>(r => r.RoomKind != null && r.RoomKind.ID == RkApartmentId),
            It.IsAny<CancellationToken>()),
            Times.Exactly(4));
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.Is<ClientDataCreateRequest>(r => r.RoomKind != null && r.RoomKind.ID == RkParkingId),
            It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.Contains(apply.Errors, e =>
            e.ErrorCode == "clientdata_roomkind_not_found"
            && e.Message.Contains("Машиноместо"));
    }

    // ─────────── ApplyAsync — alias/plural-trim резолв RoomKind ───────────

    [Fact]
    public async Task ApplyAsync_VisaryReturnsPluralAndQualifiedTitles_StillResolvesCanonical()
    {
        // Регрессия: Visary возвращает Title'ы в формах, отличных от canonical-
        // ClientDataKindByFmCode. Резолвер (alias-map + per-word plural-trim) должен
        // нормализовать их к 4 canonical-именам и найти RoomKind для каждого fmCode.
        _mockListView
            .Setup(c => c.ListRoomKindsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomKindRaw>
            {
                Data =
                [
                    // «Квартира» — точное совпадение (контроль).
                    new RoomKindRaw { ID = RkApartmentId, Title = "Квартира", RoomCategory = 0 },
                    // «Нежилое помещение для коммерческого использования» — alias из RoomKindTitleAliases.
                    new RoomKindRaw { ID = RkNonResId,    Title = "Нежилое помещение для коммерческого использования", RoomCategory = 1 },
                    // «Кладовые» — plural-trim «ые»→«ая».
                    new RoomKindRaw { ID = RkStoreroomId, Title = "Кладовые", RoomCategory = 3 },
                    // «Машино-место» — alias из RoomKindTitleAliases.
                    new RoomKindRaw { ID = RkParkingId,   Title = "Машино-место", RoomCategory = 2 },
                ],
                Total = 4,
            });

        var bytes = BuildPlanXlsxAllFourKinds();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // 16 ClientData = 4 кв × 4 RoomKind; каждый RoomKind смаплен на свой ID.
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.IsAny<ClientDataCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(16));
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.Is<ClientDataCreateRequest>(r => r.RoomKind != null && r.RoomKind.ID == RkApartmentId),
            It.IsAny<CancellationToken>()), Times.Exactly(4));
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.Is<ClientDataCreateRequest>(r => r.RoomKind != null && r.RoomKind.ID == RkNonResId),
            It.IsAny<CancellationToken>()), Times.Exactly(4));
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.Is<ClientDataCreateRequest>(r => r.RoomKind != null && r.RoomKind.ID == RkStoreroomId),
            It.IsAny<CancellationToken>()), Times.Exactly(4));
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.Is<ClientDataCreateRequest>(r => r.RoomKind != null && r.RoomKind.ID == RkParkingId),
            It.IsAny<CancellationToken>()), Times.Exactly(4));

        Assert.DoesNotContain(apply.Errors, e => e.ErrorCode == "clientdata_roomkind_not_found");
    }

    // ─────────── ApplyAsync — POST падает на отдельной точке ───────────

    [Fact]
    public async Task ApplyAsync_PostFailsOnOnePoint_AddsRowError_OthersContinue()
    {
        var seenRequests = 0;
        _mockCrud
            .Setup(c => c.CreateClientDataAsync(It.IsAny<ClientDataCreateRequest>(), It.IsAny<CancellationToken>()))
            .Returns<ClientDataCreateRequest, CancellationToken>((req, _) =>
            {
                var n = Interlocked.Increment(ref seenRequests);
                if (n == 2)
                    throw new HttpRequestException("502 Bad Gateway");
                return Task.FromResult(new ClientDataRaw
                {
                    ID = Interlocked.Increment(ref _nextClientDataId),
                    Cost = req.Cost, Rates = req.Rates,
                });
            });

        var bytes = BuildPlanXlsxApartmentsOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // 4 точки → 3 успешных, 1 ошибка → 1 row-error «clientdata_create_failed».
        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.IsAny<ClientDataCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(4));
        Assert.Contains(apply.Errors, e => e.ErrorCode == "clientdata_create_failed");
    }

    // ─────────── ApplyAsync — точки только-Summ пропускаются ───────────

    [Fact]
    public async Task ApplyAsync_SummOnlyPoints_AreSkipped()
    {
        // Файл с квартирами, у которых заполнен ТОЛЬКО Summ — без Площади и Стоимости.
        // ClientData не имеет смысла без Cost/Rates, такие точки пропускаются.
        var bytes = BuildPlanXlsxApartmentsSummOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.IsAny<ClientDataCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.DoesNotContain(apply.Errors, e => e.ErrorCode == "clientdata_create_failed");
    }

    // ─────────── ApplyAsync — Апартаменты (fmcode=060) пропускаются ───────────

    [Fact]
    public async Task ApplyAsync_ApartHotelFmCode_NotMapped_SkippedSilently()
    {
        // Файл содержит только апартаменты (fmcode=060) — у заказчика для них
        // нет соответствующих полей в payload ClientData, каскад тихо пропускает.
        var bytes = BuildPlanXlsxApartHotelOnly();
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.IsAny<ClientDataCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.DoesNotContain(apply.Errors, e => e.ErrorCode == "clientdata_create_failed");
    }

    // ─────────── ApplyAsync — без secondary file ───────────

    [Fact]
    public async Task ApplyAsync_NoSecondaryFile_NoClientDataCalls()
    {
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, SiteId, null);
        var apply = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        _mockCrud.Verify(c => c.CreateClientDataAsync(
            It.IsAny<ClientDataCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─────────── Фикстуры ───────────

    /// <summary>
    /// Лист «Общий график» с 4 видами помещений (Квартиры/Нежилые/Кладовые/Машиноместа),
    /// 4 квартала 2026. Layout-2 (без шапки «Тип помещения» сверху).
    /// </summary>
    private static byte[] BuildPlanXlsxAllFourKinds()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Общий график");
            // Таблица 1: Квартиры r3..r8.
            BuildPlanTable(ws, startRow: 3,
                amountTitle: "Площадь, кв.м (квартиры)", amount: 100,
                costTitle: "Стоимость 1 кв.м (квартиры)", cost: 10_000,
                summTitle: "Сумма от продажи квартир", summ: 1_000_000);
            // Таблица 2: Нежилые r10..r15.
            BuildPlanTable(ws, startRow: 10,
                amountTitle: "Площадь, кв.м (нежилые)", amount: 30,
                costTitle: "Стоимость 1 кв.м (нежилые)", cost: 30_000,
                summTitle: "Сумма от продажи нежилых помещений", summ: 900_000);
            // Таблица 3: Кладовые r17..r22.
            BuildPlanTable(ws, startRow: 17,
                amountTitle: "Площадь, кв.м (кладовки)", amount: 40,
                costTitle: "Стоимость 1 кв.м (кладовки)", cost: 40_000,
                summTitle: "Сумма от продажи кладовок", summ: 1_600_000);
            // Таблица 4: Машиноместа r24..r29.
            BuildPlanTable(ws, startRow: 24,
                amountTitle: "Колич-во м/м", amount: 5,
                costTitle: "Стоимость 1 м/м", cost: 50_000,
                summTitle: "Сумма от продажи машиномест", summ: 250_000);
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    private static byte[] BuildPlanXlsxApartmentsAndParkingOnly()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Общий график");
            BuildPlanTable(ws, startRow: 3,
                amountTitle: "Площадь, кв.м (квартиры)", amount: 100,
                costTitle: "Стоимость 1 кв.м (квартиры)", cost: 10_000,
                summTitle: "Сумма от продажи квартир", summ: 1_000_000);
            BuildPlanTable(ws, startRow: 10,
                amountTitle: "Колич-во м/м", amount: 5,
                costTitle: "Стоимость 1 м/м", cost: 50_000,
                summTitle: "Сумма от продажи машиномест", summ: 250_000);
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    private static byte[] BuildPlanXlsxApartmentsOnly()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Общий график");
            BuildPlanTable(ws, startRow: 3,
                amountTitle: "Площадь, кв.м (квартиры)", amount: 100,
                costTitle: "Стоимость 1 кв.м (квартиры)", cost: 10_000,
                summTitle: "Сумма от продажи квартир", summ: 1_000_000);
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    private static byte[] BuildPlanXlsxApartmentsSummOnly()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Общий график");
            // Шапка года/квартала.
            var ws2 = ws;
            ws2.Cell(3, 1).Value = "Год";
            ws2.Cell(3, 3).Value = 2026;
            ws2.Cell(5, 1).Value = "Квартал";
            ws2.Cell(5, 3).Value = "1 кв";
            ws2.Cell(5, 4).Value = "2 кв";
            ws2.Cell(5, 5).Value = "3 кв";
            ws2.Cell(5, 6).Value = "4 кв";
            ws2.Cell(6, 1).Value = "Площадь, кв.м (квартиры)";
            // Amount/Cost — пусто; только Summ.
            ws2.Cell(7, 1).Value = "Стоимость 1 кв.м (квартиры)";
            ws2.Cell(8, 1).Value = "Сумма от продажи квартир";
            ws2.Cell(8, 3).Value = 1_000_000;
            ws2.Cell(8, 4).Value = 1_000_000;
            ws2.Cell(8, 5).Value = 1_000_000;
            ws2.Cell(8, 6).Value = 1_000_000;
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    private static byte[] BuildPlanXlsxApartHotelOnly()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Общий график");
            BuildPlanTable(ws, startRow: 3,
                amountTitle: "Площадь, кв.м (апартаменты)", amount: 80,
                costTitle: "Стоимость 1 кв.м (апартаменты)", cost: 15_000,
                summTitle: "Сумма от продажи апартаментов", summ: 1_200_000);
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    private static void BuildPlanTable(
        IXLWorksheet ws, int startRow,
        string amountTitle, double amount,
        string costTitle, double cost,
        string summTitle, double summ)
    {
        // startRow = Год; +2 = Квартал; +3..+5 = Amount/Cost/Summ. 4 квартала 2026.
        ws.Cell(startRow,     1).Value = "Год";
        ws.Cell(startRow,     3).Value = 2026;
        ws.Cell(startRow + 2, 1).Value = "Квартал";
        ws.Cell(startRow + 2, 3).Value = "1 кв";
        ws.Cell(startRow + 2, 4).Value = "2 кв";
        ws.Cell(startRow + 2, 5).Value = "3 кв";
        ws.Cell(startRow + 2, 6).Value = "4 кв";

        ws.Cell(startRow + 3, 1).Value = amountTitle;
        ws.Cell(startRow + 4, 1).Value = costTitle;
        ws.Cell(startRow + 5, 1).Value = summTitle;
        for (int c = 3; c <= 6; c++)
        {
            ws.Cell(startRow + 3, c).Value = amount;
            ws.Cell(startRow + 4, c).Value = cost;
            ws.Cell(startRow + 5, c).Value = summ;
        }
    }
}
