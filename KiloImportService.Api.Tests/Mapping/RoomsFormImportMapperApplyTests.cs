using KiloImportService.Api.Data;
using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.ListView;
using Xunit;

namespace KiloImportService.Api.Tests.Mapping;

/// <summary>
/// Integration-тесты <see cref="RoomsFormImportMapper.ApplyAsync"/> с замоканным
/// Visary client'ом и in-memory <see cref="ImportServiceDbContext"/>. Покрывает
/// инварианты doc 96-rooms-incremental-parallel-apply:
///   • RowActionLog заполняется с правильным <c>Sheet</c>+<c>SourceRowNumber</c>
///     (doc 85, doc 89);
///   • первый Apply создаёт snapshot и метит строки как «Помещение обновлено»/
///     «ДДУ найден…»;
///   • повторный Apply того же файла skip-ает все строки по хэшу (метка
///     «Без изменений — пропуск (snapshot)»), не делая дополнительных PATCH'ей.
///
/// Не покрывает: рейс-кондишены параллелизма (для этого нужен реальный Visary
/// или сложный counting-mock); они отнесены к e2e-тестам.
/// </summary>
public class RoomsFormImportMapperApplyTests : IDisposable
{
    private const int SiteId = 7777;
    private const int ProjectId = 1234;
    private const int SectionId = 5000;
    private const int RoomKindIdApartment = 100;
    private const int CreatedRoomId = 9001;
    private const int CreatedSaId = 9501;

    /// <summary>doc 113 v1.4: ShareAgreement.Date — ISO-строка <c>yyyy-MM-dd</c>.
    /// Visary UI шлёт `"Date":"2026-05-26"` строкой — числовой Excel-serial
    /// не принимается.</summary>
    private const string Doc113ExpectedDateIso = "2026-04-01";

    private readonly Mock<ICrudClient> _mockCrud = new();
    private readonly Mock<IListViewClient> _mockListView = new();
    private readonly RoomsFormImportMapper _mapper;
    private readonly ImportServiceDbContext _importDb;
    private readonly VisaryDbContext _visaryDb;
    private readonly ServiceProvider _sp;

    public RoomsFormImportMapperApplyTests()
    {
        // ── Visary mock: справочник RoomKind, Site full, пустые списки ───
        _mockListView.Setup(c => c.ListRoomKindsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomKindRaw>
            {
                Data = [new RoomKindRaw { ID = RoomKindIdApartment, Title = "Квартира", RoomCategory = 0 }],
                Total = 1,
            });

        _mockCrud.Setup(c => c.GetSiteByIdFullAsync(SiteId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConstructionSiteFull
            {
                ID = SiteId,
                ConstructionProjectNumber = "PRJ-1",
                StageNumber = 1,
                ConstructionPermissionNumber = "RNS-1",
                RowVersion = 0,
                Project = new VisaryRef { ID = ProjectId },
            });

        // Section: пусто → CREATE; затем тест проверит что cache даёт ID.
        _mockListView.Setup(c => c.GetSectionsBySiteAsync(SiteId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ConstructionSectionRaw>
            {
                Data = [new ConstructionSectionRaw { ID = SectionId, Title = "1.1" }],
                Total = 1,
            });

        // Room: пусто → CREATE.
        _mockListView.Setup(c => c.GetRoomsBySectionAsync(SectionId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomRaw>
            {
                Data = [],
                Total = 0,
            });

        _mockCrud.Setup(c => c.CreateRoomAsync(It.IsAny<RoomCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoomRaw { ID = CreatedRoomId });

        // ShareAgreement: пусто → CREATE.
        _mockListView.Setup(c => c.GetShareAgreementsByRoomAsync(CreatedRoomId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw>
            {
                Data = [],
                Total = 0,
            });

        _mockCrud.Setup(c => c.CreateShareAgreementAsync(It.IsAny<ShareAgreementCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareAgreementRaw { ID = CreatedSaId });

        // PM/Organization: пусто — devPin не задан в тестовых строках, эта ветка не сработает.
        _mockListView.Setup(c => c.GetProjectManagementsBySiteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ProjectManagementRaw>
            {
                Data = [],
                Total = 0,
            });

        // Validate-фаза: per-row resolve Site через listview по (НПС, Этап) — doc 101.
        // Дефолтная пара (PRJ-1, 1) → SiteId; (PRJ-2, 2) → SiteIdB (см. multi-site тест).
        _mockListView.Setup(c => c.GetSitesByProjectAndKeysAsync(
                ProjectId, "PRJ-1", "1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ConstructionSiteRaw>
            {
                Data = [new ConstructionSiteRaw
                {
                    ID = SiteId,
                    ConstructionProjectNumber = "PRJ-1",
                    StageNumber = "1",
                    ConstructionPermissionNumber = "RNS-1",
                }],
                Total = 1,
            });

        // ── ImportServiceDbContext in-memory + DI для RoomApplySnapshotStore ───
        // КРИТИЧНО: имя in-memory БД вычисляется ОДИН раз вне делегата — иначе
        // каждый scope получит свою БД, и snapshot, записанный в первом Apply,
        // не будет виден на втором (diff-skip перестанет работать). Это и было
        // первой ошибкой в этом тесте: $"...{Guid.NewGuid()}" внутри лямбды.
        var dbName = $"RoomsApply_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ImportServiceDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<RoomApplySnapshotStore>();
        _sp = services.BuildServiceProvider();
        var scopeFactory = _sp.GetRequiredService<IServiceScopeFactory>();
        _importDb = _sp.GetRequiredService<ImportServiceDbContext>();

        _visaryDb = new VisaryDbContext(new DbContextOptionsBuilder<VisaryDbContext>()
            .UseInMemoryDatabase($"RoomsApply_Visary_{Guid.NewGuid()}")
            .Options);

        _mapper = new RoomsFormImportMapper(
            NullLogger<RoomsFormImportMapper>.Instance,
            _mockListView.Object,
            _mockCrud.Object,
            scopeFactory);
    }

    public void Dispose()
    {
        _visaryDb?.Dispose();
        _sp?.Dispose();
    }

    private static ParsedRow MakeParsedRow(int row, string sheet, string projectNum, string stage,
        string roomNumber, string sectionTitle = "1.1", string permission = "RNS-1")
    {
        var cells = new Dictionary<string, string>
        {
            ["Номер проекта"] = projectNum,
            ["Этап"] = stage,
            ["Номер разрешения"] = permission,
            ["Номер помещения/Квартира/Номер квартиры"] = roomNumber,
            ["Тип/Название/Вид"] = "Квартира",
            ["№ стр/корп"] = sectionTitle,
            ["Подъезд/Секция"] = "1",
            ["Этаж"] = "5",
            ["Колич. комнат"] = "1",
            ["Площадь"] = "42",
        };
        return new ParsedRow(row, sheet, cells);
    }

    [Fact]
    public async Task ApplyAsync_NonResidential_WritesTotalAreaFromFile_NotProjectArea()
    {
        // doc 101 v1.1: для нежилых (RoomCategory != 0) площадь идёт в TotalArea
        // из колонки «Общая площадь, кв.м.»; ProjectArea = 0. Без этого фикса
        // в Visary улетал только `"ProjectArea":0`, TotalArea оставался пустым.
        const int ParkingKindId = 4;       // Машиноместо
        const int ParkingCategory = 2;     // RoomCategory.ParkingPlace
        const int CreatedParkingRoomId = 9100;

        _mockListView.Setup(c => c.GetRoomsBySectionAsync(SectionId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomRaw> { Data = [], Total = 0 });
        _mockCrud.Setup(c => c.CreateRoomAsync(It.IsAny<RoomCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoomRaw { ID = CreatedParkingRoomId });

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[]
        {
            MakeRowWithCategory(10, "Машиноместо", "1",
                kindId: ParkingKindId, roomCategory: ParkingCategory,
                projectArea: null, totalArea: 13.5),
        };

        var result = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);
        Assert.Equal(1, result.AppliedCount);

        _mockCrud.Verify(c => c.CreateRoomAsync(
            It.Is<RoomCreateRequest>(r =>
                r.ProjectArea == 0 &&
                r.TotalArea == 13.5),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyAsync_NonResidential_FallsBackToProjectArea_WhenTotalAreaEmpty()
    {
        // Бизнес-логика fallback: если файл нежилого помещения не содержит
        // «Общая площадь», но содержит «Площадь» — берём её как TotalArea
        // (а не теряем). ProjectArea при этом всё равно 0.
        const int StorageKindId = 5;       // Кладовая
        const int OtherNonResCategory = 3;
        const int CreatedStorageRoomId = 9200;

        _mockListView.Setup(c => c.GetRoomsBySectionAsync(SectionId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomRaw> { Data = [], Total = 0 });
        _mockCrud.Setup(c => c.CreateRoomAsync(It.IsAny<RoomCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoomRaw { ID = CreatedStorageRoomId });

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[]
        {
            MakeRowWithCategory(10, "Кладовая", "1",
                kindId: StorageKindId, roomCategory: OtherNonResCategory,
                projectArea: 4.2, totalArea: null),
        };

        var result = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);
        Assert.Equal(1, result.AppliedCount);

        _mockCrud.Verify(c => c.CreateRoomAsync(
            It.Is<RoomCreateRequest>(r =>
                r.ProjectArea == 0 &&
                r.TotalArea == 4.2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidateAsync_NoProjectInContext_ReturnsFileErrorProjectRequired()
    {
        // doc 101: Project обязателен (Site больше не выбирается в UI).
        var ctx = new ImportContext(Guid.NewGuid(), VisaryProjectId: null, VisarySiteId: null, UserId: null);
        var rows = new[] { MakeParsedRow(2, "Квартира", "PRJ-1", "1", "1") };

        var result = await _mapper.ValidateAsync(ctx, rows, _visaryDb, default);

        Assert.Empty(result.Rows);
        Assert.Single(result.FileLevelErrors);
        Assert.Equal("project_required", result.FileLevelErrors[0].ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_ResolvesSiteByProjectNumberAndStage_AndStoresSiteIdInMappedValues()
    {
        // Резолв (PRJ-1, 1) через GetSitesByProjectAndKeysAsync → 1 match → SiteId
        // фиксируется в MappedValues и строка валидна.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[] { MakeParsedRow(2, "Квартира", "PRJ-1", "1", "1") };

        var result = await _mapper.ValidateAsync(ctx, rows, _visaryDb, default);

        var mapped = Assert.Single(result.Rows);
        Assert.True(mapped.IsValid, "row должна быть валидной — site найден");
        var siteIdProp = mapped.MappedValues.RootElement.GetProperty("SiteId").GetInt32();
        Assert.Equal(SiteId, siteIdProp);
    }

    [Fact]
    public async Task ValidateAsync_SiteNotFoundInProject_ReturnsRowErrorSiteNotFound()
    {
        // (PRJ-Unknown, 1) → 0 match → row-error site_not_found_in_project.
        _mockListView.Setup(c => c.GetSitesByProjectAndKeysAsync(
                ProjectId, "PRJ-Unknown", "9", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ConstructionSiteRaw> { Data = [], Total = 0 });

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[] { MakeParsedRow(2, "Квартира", "PRJ-Unknown", "9", "1") };

        var result = await _mapper.ValidateAsync(ctx, rows, _visaryDb, default);

        var mapped = Assert.Single(result.Rows);
        Assert.False(mapped.IsValid);
        Assert.Contains(mapped.Errors, e => e.ErrorCode == "site_not_found_in_project");
    }

    [Fact]
    public async Task ValidateAsync_NonResidential_ReadsTotalAreaFromColumnPloshchadKvm()
    {
        // Регрессия: в файле Репино-Парк колонка нежилых называется «Площадь, кв.м»
        // (без «Общая»). Алиас «Общая площадь, кв.м.» её не матчил, и колонка
        // «Площадь» из ProjectAreaAliases тоже (точное сравнение).
        // TotalAreaAliases расширен — теперь читается, и Apply отправляет
        // в Visary `"TotalArea": 13.5` для машиноместа.
        _mockListView.Setup(c => c.ListRoomKindsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomKindRaw>
            {
                Data = [
                    new RoomKindRaw { ID = RoomKindIdApartment, Title = "Квартира", RoomCategory = 0 },
                    new RoomKindRaw { ID = 4, Title = "Машиноместо", RoomCategory = 2 },
                ],
                Total = 2,
            });

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var cells = new Dictionary<string, string>
        {
            ["Номер проекта"] = "PRJ-1",
            ["Этап"] = "1",
            ["Номер помещения/Квартира/Номер квартиры"] = "1",
            ["Тип/Название/Вид"] = "Машиноместо",
            ["№ стр/корп"] = "1.1",
            ["Подъезд/Секция"] = "1",
            ["Этаж"] = "-1",
            ["Площадь, кв.м"] = "13,5", // 👈 реальный заголовок Репино-Парк
        };
        var rows = new[] { new ParsedRow(10, "Машиноместо", cells) };

        var result = await _mapper.ValidateAsync(ctx, rows, _visaryDb, default);

        var mapped = Assert.Single(result.Rows);
        Assert.True(mapped.IsValid,
            "row должна быть валидной — TotalArea читается из «Площадь, кв.м»");
        var ta = mapped.MappedValues.RootElement.GetProperty("TotalArea").GetDouble();
        Assert.Equal(13.5, ta);
    }

    [Fact]
    public async Task ValidateAsync_ExcelErrorInShareAgreementColumn_IsTreatedAsEmpty()
    {
        // doc 101 v1.1: «#N/A» в колонке «№ ДДУ» обнуляется в Validate.
        // Иначе Apply создаст SA с Number="#N/A" и при следующих строках будет
        // реанимировать тот же глобальный ДДУ → Visary 500.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var cells = new Dictionary<string, string>
        {
            ["Номер проекта"] = "PRJ-1",
            ["Этап"] = "1",
            ["Номер разрешения"] = "RNS-1",
            ["Номер помещения/Квартира/Номер квартиры"] = "1",
            ["Тип/Название/Вид"] = "Квартира",
            ["№ стр/корп"] = "1.1",
            ["Подъезд/Секция"] = "1",
            ["Этаж"] = "5",
            ["Колич. комнат"] = "1",
            ["Площадь"] = "42",
            ["№ ДДУ"] = "#N/A",
        };
        var rows = new[] { new ParsedRow(10, "Квартира", cells) };

        var result = await _mapper.ValidateAsync(ctx, rows, _visaryDb, default);

        var mapped = Assert.Single(result.Rows);
        Assert.True(mapped.IsValid);
        // ShareAgreementNumber в MappedValues пустой / null — Apply пропустит SA-ветку.
        var v = mapped.MappedValues.RootElement;
        var sa = v.TryGetProperty("ShareAgreementNumber", out var saProp)
            ? (saProp.ValueKind == System.Text.Json.JsonValueKind.Null ? null : saProp.GetString())
            : null;
        Assert.True(string.IsNullOrEmpty(sa),
            $"ShareAgreementNumber должен быть пуст после Excel-фильтра, получено '{sa}'");
    }

    [Fact]
    public async Task ValidateAsync_AmbiguousSites_ReturnsRowErrorSiteAmbiguous()
    {
        // 2+ кандидата → row-error site_ambiguous с перечислением ID.
        _mockListView.Setup(c => c.GetSitesByProjectAndKeysAsync(
                ProjectId, "PRJ-DUP", "5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ConstructionSiteRaw>
            {
                Data =
                [
                    new ConstructionSiteRaw { ID = 1001, ConstructionProjectNumber = "PRJ-DUP", StageNumber = "5" },
                    new ConstructionSiteRaw { ID = 1002, ConstructionProjectNumber = "PRJ-DUP", StageNumber = "5" },
                ],
                Total = 2,
            });

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[] { MakeParsedRow(2, "Квартира", "PRJ-DUP", "5", "1") };

        var result = await _mapper.ValidateAsync(ctx, rows, _visaryDb, default);

        var mapped = Assert.Single(result.Rows);
        Assert.False(mapped.IsValid);
        var err = Assert.Single(mapped.Errors, e => e.ErrorCode == "site_ambiguous");
        Assert.Contains("1001", err.Message);
        Assert.Contains("1002", err.Message);
    }

    private static MappedRow MakeRowWithCategory(int row, string sheet, string roomNumber,
        int kindId, int? roomCategory, double? projectArea, double? totalArea)
    {
        // Для тестов раскладки площадей — отдельная фабрика без ДДУ/PIN/ProjectNumber,
        // чтобы не дёргать ShareAgreement/Developer ветки.
        var mapped = new Dictionary<string, object?>
        {
            ["Sheet"] = sheet,
            ["SiteId"] = SiteId,
            ["RoomNumber"] = roomNumber,
            ["RoomKindId"] = kindId,
            ["RoomKindTitle"] = "X",
            ["RoomCategory"] = roomCategory,
            ["SectionTitle"] = "1.1",
            ["SectionTitleNumeric"] = "1.1",
            ["BuildingSection"] = "",
            ["Floor"] = "1",
            ["RoomsCount"] = null,
            ["ProjectArea"] = projectArea,
            ["TotalArea"] = totalArea,
            ["CostForOne"] = null,
            ["MarketCostPerM"] = null,
            ["ZalogCostPerM"] = null,
            ["ShareAgreementNumber"] = null,
            ["StageNumber"] = 1,
            ["StageNumberRaw"] = "1",
            ["ProjectNumber"] = "PRJ-1",
            ["PermissionNumber"] = "RNS-1",
        };
        return new MappedRow(row, sheet, true,
            JsonSerializer.SerializeToDocument(mapped), []);
    }

    private static MappedRow MakeRow(int row, string sheet, string roomNumber, string buildingSection, double area,
        int siteId = SiteId, string projectNumber = "PRJ-1", string stageNumberRaw = "1", string sectionTitle = "1.1")
    {
        // Apply теперь группирует строки по SiteId (резолвится в Validate). Тесты
        // передают siteId напрямую в MappedValues — Validate-фазу не вызываем.
        var mapped = new Dictionary<string, object?>
        {
            ["Sheet"] = sheet,
            ["SiteId"] = siteId,
            ["RoomNumber"] = roomNumber,
            ["RoomKindId"] = RoomKindIdApartment,
            ["RoomKindTitle"] = "Квартира",
            ["RoomCategory"] = 0,
            ["SectionTitle"] = sectionTitle,
            ["SectionTitleNumeric"] = sectionTitle,
            ["BuildingSection"] = buildingSection,
            ["Floor"] = "5",
            ["RoomsCount"] = 1,
            ["ProjectArea"] = area,
            ["CostForOne"] = 100000.0,
            ["MarketCostPerM"] = 120000.0,
            ["ZalogCostPerM"] = 90000.0,
            ["ShareAgreementNumber"] = $"ДДУ-{roomNumber}",
            ["StageNumber"] = int.TryParse(stageNumberRaw, out var s) ? (object)s : null,
            ["StageNumberRaw"] = stageNumberRaw,
            ["ProjectNumber"] = projectNumber,
            ["PermissionNumber"] = "RNS-1",
        };
        return new MappedRow(row, sheet, true,
            JsonSerializer.SerializeToDocument(mapped),
            []);
    }

    [Fact]
    public async Task ApplyAsync_FirstRun_CreatesRoomAndShareAgreement_AndFillsRowActionLog()
    {
        // На первом запуске snapshot пуст → каждая строка проходит CREATE Room + CREATE SA.
        // RowActionLog должен содержать «Корпус найден», «Помещение создано», «ДДУ создан»
        // с корректными Sheet+SourceRowNumber (для UI-отчёта doc 85, doc 95).
        // Контекст содержит только ProjectId; SiteId — в MappedValues каждой строки.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[] { MakeRow(10, "Квартира", "1", "1", 42.5) };

        var result = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);

        Assert.Equal(1, result.AppliedCount);
        Assert.Empty(result.Errors);

        Assert.NotNull(result.RowActions);
        var log = Assert.Single(result.RowActions);
        Assert.Equal("Квартира", log.Sheet);
        Assert.Equal(10, log.SourceRowNumber);
        Assert.Contains(log.Actions, a => a.Contains("Корпус найден"));
        Assert.Contains(log.Actions, a => a.Contains("Помещение создано"));
        Assert.Contains(log.Actions, a => a.Contains("ДДУ создан"));

        _mockCrud.Verify(c => c.CreateRoomAsync(It.IsAny<RoomCreateRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockCrud.Verify(c => c.CreateShareAgreementAsync(It.IsAny<ShareAgreementCreateRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyAsync_SecondRun_SameRows_SkipsByHash_NoExtraPatchOrCreate()
    {
        // Сценарий повторного импорта того же файла, КОГДА сущности живы в Visary:
        //   первый Apply — создаёт RoomApplySnapshot;
        //   второй Apply с тем же MappedValues — hash совпадает И revalidation
        //   нашла Room+ДДУ в Visary → строка skip-ается с меткой «Без изменений
        //   — пропуск (snapshot)»; никакого CREATE/PATCH не происходит.
        //
        // doc 106: snapshot-revalidation требует, чтобы во втором запуске Visary
        // ВЕРНУЛ существующие сущности, иначе snapshot будет признан устаревшим
        // и flow перейдёт в reuse/recreate.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[] { MakeRow(10, "Квартира", "1", "1", 42.5) };

        var first = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);
        Assert.Equal(1, first.AppliedCount);

        // Sanity-check: snapshot реально доехал до БД, иначе diff-skip ниже бессмыслен.
        using (var diag = _sp.CreateScope())
        {
            var db = diag.ServiceProvider.GetRequiredService<ImportServiceDbContext>();
            var saved = db.RoomApplySnapshots.AsNoTracking().Where(s => s.VisarySiteId == SiteId).ToList();
            Assert.Single(saved);
            Assert.Equal(64, saved[0].MappedHash.Length);
        }

        _mockCrud.Invocations.Clear();

        // ── Симулируем, что во втором запуске Room и ДДУ ВСЁ ЕЩЁ существуют в Visary.
        // Без этого revalidation решит, что snapshot устарел, и flow пойдёт по
        // обычному пути find-or-create (CreateRoomAsync вызовется).
        _mockListView.Setup(c => c.GetRoomsBySectionAsync(SectionId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomRaw>
            {
                Data = [new RoomRaw { ID = CreatedRoomId, Number = "1", ExplicationNumber = "1", BuildingSection = "1", Kind = new VisaryRef { ID = RoomKindIdApartment } }],
                Total = 1,
            });
        _mockListView.Setup(c => c.GetShareAgreementsByRoomAsync(CreatedRoomId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw>
            {
                Data = [new ShareAgreementRaw { ID = CreatedSaId, Number = "ДДУ-1", Room = new VisaryRef { ID = CreatedRoomId } }],
                Total = 1,
            });

        // На втором Apply мы НЕ ожидаем CreateRoom/CreateShareAgreement.
        // GetRoomsBySectionAsync будет вызван (он внутри parallel-цикла), но это OK —
        // именно через diff-skip мы экономим CREATE/PATCH, а не listview-чтения.
        var second = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);

        Assert.Equal(1, second.AppliedCount); // applied включает skip-ы
        Assert.Empty(second.Errors);

        var log = Assert.Single(second.RowActions!);
        Assert.Contains(log.Actions, a => a.Contains("Без изменений") && a.Contains("snapshot"));

        _mockCrud.Verify(c => c.CreateRoomAsync(It.IsAny<RoomCreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCrud.Verify(c => c.PatchRoomAsync(It.IsAny<int>(), It.IsAny<RoomPatchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCrud.Verify(c => c.CreateShareAgreementAsync(It.IsAny<ShareAgreementCreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCrud.Verify(c => c.PatchShareAgreementAsync(It.IsAny<int>(), It.IsAny<ShareAgreementPatchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyAsync_SecondRun_RoomDeletedInVisary_SnapshotStale_RecreatesRoom()
    {
        // doc 106: snapshot-revalidation. Между первым и вторым импортом
        // пользователь удалил Room в Visary (например, очистил тестовый сайт).
        // Snapshot.VisaryRoomId не находится в свежем GetRoomsBySectionAsync →
        // hash-match не должен приводить к skip; маппер обязан пересоздать помещение.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[] { MakeRow(10, "Квартира", "1", "1", 42.5) };

        // Первый запуск — стандартный мок (Section/Rooms пусты → CREATE Room, CREATE SA).
        var first = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);
        Assert.Equal(1, first.AppliedCount);

        _mockCrud.Invocations.Clear();

        // ── Симулируем удаление Room в Visary: GetRoomsBySectionAsync вернёт пусто,
        //    хотя snapshot.VisaryRoomId = CreatedRoomId. Это и есть «помещение удалено».
        _mockListView.Setup(c => c.GetRoomsBySectionAsync(SectionId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomRaw> { Data = [], Total = 0 });
        // ДДУ тоже формально нет — он привязывался к удалённой комнате.
        _mockListView.Setup(c => c.GetShareAgreementsByRoomAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw> { Data = [], Total = 0 });

        // CreateRoom должен вернуть НОВЫЙ ID — это и есть «пересоздание».
        const int RecreatedRoomId = 9011;
        _mockCrud.Setup(c => c.CreateRoomAsync(It.IsAny<RoomCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoomRaw { ID = RecreatedRoomId });
        _mockCrud.Setup(c => c.CreateShareAgreementAsync(It.IsAny<ShareAgreementCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareAgreementRaw { ID = 9511 });

        var second = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);

        Assert.Equal(1, second.AppliedCount);
        Assert.Empty(second.Errors);

        // Журнал должен явно сигнализировать про stale-snapshot и про пересоздание.
        var log = Assert.Single(second.RowActions!);
        Assert.Contains(log.Actions, a => a.Contains("Snapshot устарел"));
        Assert.Contains(log.Actions, a => a.Contains("Помещение создано"));
        Assert.DoesNotContain(log.Actions, a => a.Contains("Без изменений"));

        _mockCrud.Verify(c => c.CreateRoomAsync(It.IsAny<RoomCreateRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        // Snapshot должен обновиться с новым VisaryRoomId.
        using var diag = _sp.CreateScope();
        var db = diag.ServiceProvider.GetRequiredService<ImportServiceDbContext>();
        var saved = db.RoomApplySnapshots.AsNoTracking().Single(s => s.VisarySiteId == SiteId);
        Assert.Equal(RecreatedRoomId, saved.VisaryRoomId);
    }

    [Fact]
    public async Task ApplyAsync_SecondRun_ShareAgreementDeletedInVisary_SnapshotStale_RecreatesShareAgreement()
    {
        // doc 106: revalidation для ДДУ. Помещение в Visary осталось (с тем же ID),
        // но ДДУ удалён. Без проверки ДДУ маппер skip-нул бы строку, и удалённый
        // ДДУ не восстановился бы.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[] { MakeRow(10, "Квартира", "1", "1", 42.5) };

        var first = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);
        Assert.Equal(1, first.AppliedCount);

        _mockCrud.Invocations.Clear();

        // ── Room на месте, ДДУ удалён ─────────────────────────────────────
        _mockListView.Setup(c => c.GetRoomsBySectionAsync(SectionId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomRaw>
            {
                Data = [new RoomRaw { ID = CreatedRoomId, Number = "1", ExplicationNumber = "1", BuildingSection = "1", Kind = new VisaryRef { ID = RoomKindIdApartment } }],
                Total = 1,
            });
        // Возвращаем ПУСТО — ДДУ удалён. Глобальный поиск тоже пуст (или его не делаем).
        _mockListView.Setup(c => c.GetShareAgreementsByRoomAsync(CreatedRoomId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw> { Data = [], Total = 0 });
        _mockListView.Setup(c => c.FindShareAgreementsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw> { Data = [], Total = 0 });

        const int RecreatedSaId = 9512;
        _mockCrud.Setup(c => c.CreateShareAgreementAsync(It.IsAny<ShareAgreementCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareAgreementRaw { ID = RecreatedSaId });

        var second = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);

        Assert.Equal(1, second.AppliedCount);
        Assert.Empty(second.Errors);

        var log = Assert.Single(second.RowActions!);
        Assert.Contains(log.Actions, a => a.Contains("Snapshot устарел") && a.Contains("ДДУ"));
        Assert.Contains(log.Actions, a => a.Contains("ДДУ создан"));
        // Room НЕ должен пересоздаваться — только PATCH (он жив).
        _mockCrud.Verify(c => c.CreateRoomAsync(It.IsAny<RoomCreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCrud.Verify(c => c.PatchRoomAsync(CreatedRoomId, It.IsAny<RoomPatchRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockCrud.Verify(c => c.CreateShareAgreementAsync(It.IsAny<ShareAgreementCreateRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        using var diag = _sp.CreateScope();
        var db = diag.ServiceProvider.GetRequiredService<ImportServiceDbContext>();
        var saved = db.RoomApplySnapshots.AsNoTracking().Single(s => s.VisarySiteId == SiteId);
        Assert.Equal(RecreatedSaId, saved.VisaryShareAgreementId);
    }

    [Fact]
    public async Task ApplyAsync_SecondRun_ChangedArea_TriggersPatchRoom()
    {
        // Если хоть одно поле, входящее в HashedMappedFields, изменилось — diff-skip
        // не сработает, PATCH должен пройти. Это гарантирует, что snapshot не «маскирует»
        // реальные изменения площади/стоимости.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);

        await _mapper.ApplyAsync(ctx, _visaryDb, new[] { MakeRow(10, "Квартира", "1", "1", 42.5) }, default);

        // На втором запуске: после CREATE Room → roomsInSection пуст → нужно вернуть существующую,
        // чтобы PatchRoom (а не Create) сработал.
        _mockListView.Setup(c => c.GetRoomsBySectionAsync(SectionId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomRaw>
            {
                Data =
                [
                    new RoomRaw
                    {
                        ID = CreatedRoomId,
                        Number = "1",
                        ExplicationNumber = "1",
                        BuildingSection = "1",
                        Kind = new VisaryRef { ID = RoomKindIdApartment },
                    }
                ],
                Total = 1,
            });
        _mockListView.Setup(c => c.GetShareAgreementsByRoomAsync(CreatedRoomId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw>
            {
                Data =
                [
                    new ShareAgreementRaw
                    {
                        ID = CreatedSaId,
                        Number = "ДДУ-1",
                        Room = new VisaryRef { ID = CreatedRoomId },
                        RoomKindRef = new VisaryRef { ID = RoomKindIdApartment },
                    }
                ],
                Total = 1,
            });
        _mockCrud.Invocations.Clear();
        var changedRows = new[] { MakeRow(10, "Квартира", "1", "1", 50.0) }; // площадь изменилась

        var second = await _mapper.ApplyAsync(ctx, _visaryDb, changedRows, default);

        Assert.Equal(1, second.AppliedCount);
        Assert.Empty(second.Errors);

        var log = Assert.Single(second.RowActions!);
        Assert.DoesNotContain(log.Actions, a => a.Contains("Без изменений"));
        Assert.Contains(log.Actions, a => a.Contains("Помещение обновлено"));
        _mockCrud.Verify(c => c.PatchRoomAsync(CreatedRoomId, It.IsAny<RoomPatchRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyAsync_MultipleSites_GroupsRowsBySiteId_AndCreatesPerSite()
    {
        // doc 101: один файл может содержать строки разных ОКС в рамках проекта.
        // Apply группирует валидные строки по SiteId (положен в MappedValues
        // Validate-фазой), и каждый сайт получает свой pre-pass (snapshot/секции).
        // Проверяем, что CreateRoom вызван для обоих сайтов с корректным SiteID.
        const int SiteIdB = 7778;
        const int SectionIdB = 5001;
        const int CreatedRoomIdB = 9002;

        _mockCrud.Setup(c => c.GetSiteByIdFullAsync(SiteIdB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConstructionSiteFull
            {
                ID = SiteIdB,
                ConstructionProjectNumber = "PRJ-2",
                StageNumber = 2,
                ConstructionPermissionNumber = null,
                RowVersion = 0,
                Project = new VisaryRef { ID = ProjectId },
            });
        _mockListView.Setup(c => c.GetSectionsBySiteAsync(SiteIdB, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ConstructionSectionRaw>
            {
                Data = [new ConstructionSectionRaw { ID = SectionIdB, Title = "2.1" }],
                Total = 1,
            });
        _mockListView.Setup(c => c.GetRoomsBySectionAsync(SectionIdB, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomRaw> { Data = [], Total = 0 });
        // CreateRoomAsync уже замокан на возврат CreatedRoomId — оба сайта используют тот же возврат.
        // Для второго сайта мы только проверяем факт вызова с корректным SiteID.
        _mockListView.Setup(c => c.GetShareAgreementsByRoomAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw> { Data = [], Total = 0 });
        _mockCrud.Setup(c => c.CreateRoomAsync(It.IsAny<RoomCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RoomCreateRequest req, CancellationToken _) =>
                new RoomRaw { ID = req.SiteID == SiteIdB ? CreatedRoomIdB : CreatedRoomId });

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[]
        {
            MakeRow(10, "Квартира", "1", "1", 42.5,
                siteId: SiteId,  projectNumber: "PRJ-1", stageNumberRaw: "1", sectionTitle: "1.1"),
            MakeRow(20, "Квартира", "1", "1", 50.0,
                siteId: SiteIdB, projectNumber: "PRJ-2", stageNumberRaw: "2", sectionTitle: "2.1"),
        };

        var result = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);

        Assert.Equal(2, result.AppliedCount);
        Assert.Empty(result.Errors);
        // По одному CreateRoom на сайт — с правильным SiteID.
        _mockCrud.Verify(c => c.CreateRoomAsync(
            It.Is<RoomCreateRequest>(r => r.SiteID == SiteId), It.IsAny<CancellationToken>()), Times.Once);
        _mockCrud.Verify(c => c.CreateRoomAsync(
            It.Is<RoomCreateRequest>(r => r.SiteID == SiteIdB), It.IsAny<CancellationToken>()), Times.Once);
        // Section.find-or-create отрабатывает per-site.
        _mockListView.Verify(c => c.GetSectionsBySiteAsync(SiteId,  "1.1", It.IsAny<CancellationToken>()), Times.Once);
        _mockListView.Verify(c => c.GetSectionsBySiteAsync(SiteIdB, "2.1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyAsync_MultipleSheets_RowActionLogPreservesSheetPerRow()
    {
        // Многолистовой файл (doc 80, doc 89): каждый RowActionLog должен нести имя своего листа,
        // иначе pipeline не сможет связать Actions с правильной StagedRow в БД и UI не
        // отрисует actions в строках.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[]
        {
            MakeRow(10, "Квартира", "1", "1", 42.5),
            MakeRow(11, "Квартира", "2", "1", 50.0),
        };

        var result = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);

        Assert.Equal(2, result.AppliedCount);
        Assert.Equal(2, result.RowActions!.Count);
        Assert.All(result.RowActions, log => Assert.Equal("Квартира", log.Sheet));
        Assert.Equal([10, 11], result.RowActions.Select(l => l.SourceRowNumber).OrderBy(x => x));
    }

    [Fact]
    public async Task ApplyAsync_OrphanShareAgreement_IsReusedAndRelinked_NotDuplicated()
    {
        // doc 76 v1.1: ДДУ может существовать в Visary ДО загрузки помещений — отвязанная
        // от Room/Project/Stage (например, заведена сотрудником вручную или отстёгнута
        // системно). Строгий поиск по 5-полям (Number+Kind+Cond+Stage+Project) её не находит,
        // потому что Stage/Project у орфана NULL. Без loose-fallback'а каждый импорт плодит
        // новый ДДУ-дубликат, а orphan остаётся невидимым. Этот тест фиксирует обратное.
        const int OrphanSaId = 9777;

        // Per-room search: пусто (Room только что создан).
        _mockListView.Setup(c => c.GetShareAgreementsByRoomAsync(
                CreatedRoomId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw> { Data = [], Total = 0 });

        // Strict find (stage+project заполнены) — пусто. Орфан не матчится строгим Visary `=`.
        _mockListView.Setup(c => c.FindShareAgreementsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.Is<string?>(s => !string.IsNullOrWhiteSpace(s)),
                It.Is<string?>(p => !string.IsNullOrWhiteSpace(p)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw> { Data = [], Total = 0 });

        // Loose find (stage+project null) — отдаём orphan ДДУ: Number+RoomKind+Cond матчат,
        // Room == null (главный признак, что можно безопасно реанимировать).
        _mockListView.Setup(c => c.FindShareAgreementsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                null, null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw>
            {
                Data =
                [
                    new ShareAgreementRaw
                    {
                        ID                = OrphanSaId,
                        Number            = "ДДУ-1",
                        ConditionalNumber = "1",
                        RoomKindRef       = new VisaryRef { ID = RoomKindIdApartment },
                        Room              = null,    // ← orphan-маркер (JSON null)
                        Project           = null,
                        Site              = null,
                        StageNumber       = null,
                    }
                ],
                Total = 1,
            });

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[] { MakeRow(10, "Квартира", "1", "1", 42.5) };

        var result = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);

        Assert.Equal(1, result.AppliedCount);
        Assert.Empty(result.Errors);

        // Новый ДДУ НЕ создан — orphan переиспользован.
        _mockCrud.Verify(c => c.CreateShareAgreementAsync(
            It.IsAny<ShareAgreementCreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // Orphan заpatch-ан: Room/Project/Site/Stage/Project заполнены текущими значениями.
        _mockCrud.Verify(c => c.PatchShareAgreementAsync(
            OrphanSaId,
            It.Is<ShareAgreementPatchRequest>(r =>
                r.RoomID == CreatedRoomId &&
                r.Room!.ID == CreatedRoomId &&
                r.Site!.ID == SiteId &&
                r.Project!.ID == ProjectId &&
                r.RoomKindRef!.ID == RoomKindIdApartment &&
                r.ConditionalNumber == "1" &&
                r.StageNumber == "1" &&
                r.ProjectNumber == "PRJ-1"),
            It.IsAny<CancellationToken>()), Times.Once);

        // В журнале строки — метка о глобальной находке + привязке.
        var log = Assert.Single(result.RowActions!);
        Assert.Contains(log.Actions, a => a.Contains("ДДУ найден глобально"));

        // Snapshot привязан к orphan-ID (а не к новому ID).
        using var diag = _sp.CreateScope();
        var db = diag.ServiceProvider.GetRequiredService<ImportServiceDbContext>();
        var saved = db.RoomApplySnapshots.AsNoTracking().Single(s => s.VisarySiteId == SiteId);
        Assert.Equal(OrphanSaId, saved.VisaryShareAgreementId);
    }

    [Fact]
    public async Task ApplyAsync_OrphanShareAgreement_WithZeroRoomId_IsAlsoTreatedAsOrphan()
    {
        // Регрессия из реального стенда: Visary часто сериализует «нет связи» как
        // {"Room": {"ID": 0, "Title": ""}}, а не {"Room": null}. С учётом того что
        // VisaryRef.ID — non-nullable int (default = 0), `a.Room?.ID is null` это
        // НЕ ловит. Проверка `a.Room is null || a.Room.ID <= 0` должна работать
        // в обоих случаях. См. скриншот заказчика «Лавандовый раф» — ДДУ 833/834/835
        // с пустыми «Помещение»/«Объект»/«Проект».
        const int OrphanSaIdZeroRoom = 9779;

        _mockListView.Setup(c => c.GetShareAgreementsByRoomAsync(
                CreatedRoomId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw> { Data = [], Total = 0 });

        _mockListView.Setup(c => c.FindShareAgreementsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.Is<string?>(s => !string.IsNullOrWhiteSpace(s)),
                It.Is<string?>(p => !string.IsNullOrWhiteSpace(p)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw> { Data = [], Total = 0 });

        _mockListView.Setup(c => c.FindShareAgreementsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                null, null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw>
            {
                Data =
                [
                    new ShareAgreementRaw
                    {
                        ID                = OrphanSaIdZeroRoom,
                        Number            = "ДДУ-1",
                        ConditionalNumber = "1",
                        RoomKindRef       = new VisaryRef { ID = RoomKindIdApartment },
                        // ← Visary шлёт {"ID":0}, не null — как в реальных данных:
                        Room              = new VisaryRef { ID = 0, Title = string.Empty },
                    }
                ],
                Total = 1,
            });

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[] { MakeRow(10, "Квартира", "1", "1", 42.5) };

        var result = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);

        Assert.Equal(1, result.AppliedCount);
        _mockCrud.Verify(c => c.CreateShareAgreementAsync(
            It.IsAny<ShareAgreementCreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCrud.Verify(c => c.PatchShareAgreementAsync(
            OrphanSaIdZeroRoom,
            It.Is<ShareAgreementPatchRequest>(r =>
                r.RoomID == CreatedRoomId && r.Room!.ID == CreatedRoomId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidateAsync_ReadsDoc113Columns_AndPlacesThemInMappedValues()
    {
        // doc 113: новые опциональные колонки «Вывод (да/нет)», «Сумма депонирования»,
        // «Сумма на эскроу», «Дата ДДУ», «ФИО покупателя» + переиспользуемый
        // «ПИН застройщика». Validate должен распарсить их и положить в
        // MappedValues; поиск по этим полям не производится.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var cells = new Dictionary<string, string>
        {
            ["Номер проекта"] = "PRJ-1",
            ["Этап"] = "1",
            ["Номер разрешения"] = "RNS-1",
            ["Номер помещения/Квартира/Номер квартиры"] = "1",
            ["Тип/Название/Вид"] = "Квартира",
            ["№ стр/корп"] = "1.1",
            ["Подъезд/Секция"] = "1",
            ["Этаж"] = "5",
            ["Колич. комнат"] = "1",
            ["Площадь"] = "42",
            // ── doc 113 ──
            ["Вывод\n(да/нет)"]            = "да",
            ["Сумма депонирования, руб."]  = "3422700",
            ["Сумма на эскроу"]            = "3422700",
            ["Дата ДДУ"]                   = "01.04.2026",
            ["ФИО покупателя"]             = "Иванов И.И.",
            ["ПИН застройщика"]            = "UBCFBE",
        };
        var rows = new[] { new ParsedRow(10, "Квартира", cells) };

        var result = await _mapper.ValidateAsync(ctx, rows, _visaryDb, default);

        var mapped = Assert.Single(result.Rows);
        Assert.True(mapped.IsValid, "row должна быть валидной (новые поля опциональные)");
        var v = mapped.MappedValues.RootElement;

        Assert.True(v.GetProperty("IsWithdrawn").GetBoolean());
        Assert.Equal(3422700.0, v.GetProperty("ShareAgreementCost").GetDouble());
        Assert.Equal(3422700.0, v.GetProperty("ShareAgreementDepositedAmount").GetDouble());
        // doc 113 v1.4: «Дата ДДУ» хранится в MappedValues как ISO-строка
        // `yyyy-MM-dd` — именно так Visary UI шлёт `"Date":"2026-05-26"`.
        Assert.Equal(Doc113ExpectedDateIso, v.GetProperty("ShareAgreementDate").GetString());
        Assert.Equal("Иванов И.И.", v.GetProperty("ShareAgreementDepositorFullName").GetString());
        Assert.Equal("UBCFBE", v.GetProperty("ShareAgreementDeveloperPin").GetString());
    }

    [Theory]
    // doc 113 v1.3: реальные файлы заказчика дают заголовки с разной формой
    // whitespace внутри. NormalizeHeader сворачивает любую whitespace-
    // последовательность к одному пробелу и матчит alias независимо от
    // переноса/таба/двойных пробелов. Раньше эти заголовки не матчились с
    // alias-листом и поля молча оставались пустыми (Visary не получал
    // IsWithdrawn/Cost/Date/etc).
    [InlineData("Вывод\r\n(да/нет)")]
    [InlineData("Вывод\t(да/нет)")]
    [InlineData("Вывод  (да/нет)")]
    [InlineData("Вывод (да/нет)")]
    [InlineData("Вывод\n(да/нет)")]
    public async Task ValidateAsync_Doc113Headers_WithVariousWhitespace_AreMatched(string headerForm)
    {
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var cells = new Dictionary<string, string>
        {
            ["Номер проекта"] = "PRJ-1",
            ["Этап"] = "1",
            ["Номер разрешения"] = "RNS-1",
            ["Номер помещения/Квартира/Номер квартиры"] = "1",
            ["Тип/Название/Вид"] = "Квартира",
            ["№ стр/корп"] = "1.1",
            ["Подъезд/Секция"] = "1",
            ["Этаж"] = "5",
            ["Колич. комнат"] = "1",
            ["Площадь"] = "42",
            [headerForm] = "да",
        };
        var rows = new[] { new ParsedRow(10, "Квартира", cells) };

        var result = await _mapper.ValidateAsync(ctx, rows, _visaryDb, default);

        var mapped = Assert.Single(result.Rows);
        Assert.True(mapped.IsValid);
        Assert.True(
            mapped.MappedValues.RootElement.GetProperty("IsWithdrawn").GetBoolean(),
            $"Заголовок '{headerForm}' должен матчиться whitespace-insensitive");
    }

    [Fact]
    public async Task ValidateThenApply_IsWithdrawnDa_EndToEnd_SendsTrueToVisaryRoom()
    {
        // Заказчик: «Если в файле в поле "Вывод (да/нет)" указано "Да",
        // то передавать в Помещение значение "IsWithdrawn":true».
        // Регрессионный тест полного pipeline'а: Validate (alias-match
        // header'а + TryParseBoolYesNo("Да") → true → MappedValues) →
        // Apply (GetBoolOrNull → RoomCreateRequest.IsWithdrawn → JSON
        // payload). DTO PascalCase + WhenWritingNull сохраняют true в
        // payload как `"IsWithdrawn": true`.
        RoomCreateRequest? capturedRoom = null;
        _mockCrud.Setup(c => c.CreateRoomAsync(It.IsAny<RoomCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<RoomCreateRequest, CancellationToken>((r, _) => capturedRoom = r)
            .ReturnsAsync(new RoomRaw { ID = CreatedRoomId });
        _mockCrud.Setup(c => c.CreateShareAgreementAsync(It.IsAny<ShareAgreementCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareAgreementRaw { ID = CreatedSaId });

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var cells = new Dictionary<string, string>
        {
            ["Номер проекта"] = "PRJ-1",
            ["Этап"] = "1",
            ["Номер разрешения"] = "RNS-1",
            ["Номер помещения/Квартира/Номер квартиры"] = "1",
            ["Тип/Название/Вид"] = "Квартира",
            ["№ стр/корп"] = "1.1",
            ["Подъезд/Секция"] = "1",
            ["Этаж"] = "5",
            ["Колич. комнат"] = "1",
            ["Площадь"] = "42",
            ["Вывод (да/нет)"] = "Да",
        };
        var rows = new[] { new ParsedRow(10, "Квартира", cells) };

        var validate = await _mapper.ValidateAsync(ctx, rows, _visaryDb, default);
        var mapped = Assert.Single(validate.Rows);
        Assert.True(mapped.IsValid);
        Assert.True(mapped.MappedValues.RootElement.GetProperty("IsWithdrawn").GetBoolean());

        var apply = await _mapper.ApplyAsync(ctx, _visaryDb, validate.Rows, default);
        Assert.Equal(1, apply.AppliedCount);
        Assert.Empty(apply.Errors);

        Assert.NotNull(capturedRoom);
        Assert.True(capturedRoom!.IsWithdrawn, "IsWithdrawn должно быть true для значения 'Да'");
    }

    [Fact]
    public async Task ValidateAsync_SaCost_SlashCombinedHeader_IsMatched()
    {
        // doc 113 v1.5: реальный шаблон заказчика объединяет «Стоимость ДКП, руб»
        // и «Сумма депонирования, руб.» в одной ячейке через `,/` — это форма,
        // которой не было в alias-листе SaCostAliases. Без slash-aware fallback'а
        // в ReadString ShareAgreement.Cost молча оставался null и Visary не получал
        // значение. Тест регрессионный.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var cells = new Dictionary<string, string>
        {
            ["Номер проекта"] = "PRJ-1",
            ["Этап"] = "1",
            ["Номер разрешения"] = "RNS-1",
            ["Номер помещения/Квартира/Номер квартиры"] = "1",
            ["Тип/Название/Вид"] = "Квартира",
            ["№ стр/корп"] = "1.1",
            ["Подъезд/Секция"] = "1",
            ["Этаж"] = "5",
            ["Колич. комнат"] = "1",
            ["Площадь"] = "42",
            ["Стоимость ДКП, руб,/Сумма депонирования, руб."] = "1234567",
        };
        var rows = new[] { new ParsedRow(10, "Квартира", cells) };

        var result = await _mapper.ValidateAsync(ctx, rows, _visaryDb, default);

        var mapped = Assert.Single(result.Rows);
        Assert.True(mapped.IsValid);
        Assert.Equal(1234567.0,
            mapped.MappedValues.RootElement.GetProperty("ShareAgreementCost").GetDouble());
    }

    [Fact]
    public async Task ApplyAsync_NewRow_WithDoc113Fields_SendsThemToVisaryRoomAndShareAgreement()
    {
        // Apply должен прокинуть IsWithdrawn в RoomCreateRequest и
        // Cost/DepositedAmount/Date/DepositorFullName/DeveloperPIN — в
        // ShareAgreementCreateRequest. Это интеграционный регресс-тест на
        // полный путь «MappedValues → CRUD-payload».
        RoomCreateRequest? capturedRoom = null;
        ShareAgreementCreateRequest? capturedSa = null;
        _mockCrud.Setup(c => c.CreateRoomAsync(It.IsAny<RoomCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<RoomCreateRequest, CancellationToken>((r, _) => capturedRoom = r)
            .ReturnsAsync(new RoomRaw { ID = CreatedRoomId });
        _mockCrud.Setup(c => c.CreateShareAgreementAsync(It.IsAny<ShareAgreementCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ShareAgreementCreateRequest, CancellationToken>((s, _) => capturedSa = s)
            .ReturnsAsync(new ShareAgreementRaw { ID = CreatedSaId });

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[] { MakeRowWithDoc113Fields(10, "Квартира", "1", "1", 42.5) };

        var result = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);

        Assert.Equal(1, result.AppliedCount);
        Assert.Empty(result.Errors);

        Assert.NotNull(capturedRoom);
        Assert.True(capturedRoom!.IsWithdrawn);

        Assert.NotNull(capturedSa);
        Assert.Equal(3422700.0, capturedSa!.Cost);
        Assert.Equal(3300000.0, capturedSa.DepositedAmount);
        Assert.Equal(Doc113ExpectedDateIso, capturedSa.Date);
        Assert.Equal("Иванов И.И.", capturedSa.DepositorFullName);
        Assert.Equal("UBCFBE", capturedSa.DeveloperPIN);
    }

    [Fact]
    public async Task ApplyAsync_ExistingRoomAndSa_WithDoc113Fields_PatchesBothEntitiesWithNewValues()
    {
        // Сценарий повторного импорта: Room и ДДУ существуют — Apply должен
        // отправить IsWithdrawn в PATCH Room и Cost/Date/… в PATCH SA.
        _mockListView.Setup(c => c.GetRoomsBySectionAsync(SectionId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<RoomRaw>
            {
                Data =
                [
                    new RoomRaw
                    {
                        ID = CreatedRoomId, Number = "1", ExplicationNumber = "1",
                        BuildingSection = "1",
                        Kind = new VisaryRef { ID = RoomKindIdApartment },
                    }
                ],
                Total = 1,
            });
        _mockListView.Setup(c => c.GetShareAgreementsByRoomAsync(CreatedRoomId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw>
            {
                Data =
                [
                    new ShareAgreementRaw
                    {
                        ID = CreatedSaId, Number = "ДДУ-1",
                        Room = new VisaryRef { ID = CreatedRoomId },
                        RoomKindRef = new VisaryRef { ID = RoomKindIdApartment },
                    }
                ],
                Total = 1,
            });

        RoomPatchRequest? roomPatch = null;
        ShareAgreementPatchRequest? saPatch = null;
        _mockCrud.Setup(c => c.PatchRoomAsync(CreatedRoomId, It.IsAny<RoomPatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<int, RoomPatchRequest, CancellationToken>((_, r, _) => roomPatch = r)
            .ReturnsAsync(true);
        _mockCrud.Setup(c => c.PatchShareAgreementAsync(CreatedSaId, It.IsAny<ShareAgreementPatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<int, ShareAgreementPatchRequest, CancellationToken>((_, r, _) => saPatch = r)
            .ReturnsAsync(true);

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[] { MakeRowWithDoc113Fields(10, "Квартира", "1", "1", 42.5) };

        var result = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);

        Assert.Equal(1, result.AppliedCount);
        Assert.NotNull(roomPatch);
        Assert.True(roomPatch!.IsWithdrawn);

        Assert.NotNull(saPatch);
        Assert.Equal(3422700.0, saPatch!.Cost);
        Assert.Equal(3300000.0, saPatch.DepositedAmount);
        Assert.Equal(Doc113ExpectedDateIso, saPatch.Date);
        Assert.Equal("Иванов И.И.", saPatch.DepositorFullName);
        Assert.Equal("UBCFBE", saPatch.DeveloperPIN);
    }

    private static MappedRow MakeRowWithDoc113Fields(int row, string sheet, string roomNumber,
        string buildingSection, double area)
    {
        // Вариант MakeRow с заполненными «новыми» полями doc 113.
        var mapped = new Dictionary<string, object?>
        {
            ["Sheet"] = sheet,
            ["SiteId"] = SiteId,
            ["RoomNumber"] = roomNumber,
            ["RoomKindId"] = RoomKindIdApartment,
            ["RoomKindTitle"] = "Квартира",
            ["RoomCategory"] = 0,
            ["SectionTitle"] = "1.1",
            ["SectionTitleNumeric"] = "1.1",
            ["BuildingSection"] = buildingSection,
            ["Floor"] = "5",
            ["RoomsCount"] = 1,
            ["IsStudio"] = false,
            ["ProjectArea"] = area,
            ["CostForOne"] = 100000.0,
            ["MarketCostPerM"] = 120000.0,
            ["ZalogCostPerM"] = 90000.0,
            ["ShareAgreementNumber"] = $"ДДУ-{roomNumber}",
            ["StageNumber"] = 1,
            ["StageNumberRaw"] = "1",
            ["ProjectNumber"] = "PRJ-1",
            ["PermissionNumber"] = "RNS-1",
            // doc 113 — заполнено
            ["IsWithdrawn"] = true,
            ["ShareAgreementCost"] = 3422700.0,
            ["ShareAgreementDepositedAmount"] = 3300000.0,
            // doc 113 v1.4: ISO-строка `yyyy-MM-dd` — именно так Visary UI шлёт
            // `"Date":"2026-05-26"`. До v1.4 был Excel-serial (double).
            ["ShareAgreementDate"] = Doc113ExpectedDateIso,
            ["ShareAgreementDepositorFullName"] = "Иванов И.И.",
            ["ShareAgreementDeveloperPin"] = "UBCFBE",
        };
        return new MappedRow(row, sheet, true,
            JsonSerializer.SerializeToDocument(mapped), []);
    }

    [Fact]
    public async Task ApplyAsync_LooseFind_SkipsNonOrphan_DoesNotStealFromAnotherRoom()
    {
        // Anti-pattern #2 из doc 76: если loose-find вернёт ДДУ с тем же Number/Cond/Kind,
        // но привязанный к ДРУГОЙ комнате (a.Room?.ID != null) — это не наш ДДУ, а легитимная
        // запись из соседнего этапа/проекта. Угонять её нельзя — создаём свой новый ДДУ.
        _mockListView.Setup(c => c.GetShareAgreementsByRoomAsync(
                CreatedRoomId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw> { Data = [], Total = 0 });

        _mockListView.Setup(c => c.FindShareAgreementsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.Is<string?>(s => !string.IsNullOrWhiteSpace(s)),
                It.Is<string?>(p => !string.IsNullOrWhiteSpace(p)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw> { Data = [], Total = 0 });

        // Loose-find отдаёт ДДУ, привязанный к чужой комнате (Room != null) — фильтр должен
        // отсеять его, и mapper создаст НОВЫЙ.
        _mockListView.Setup(c => c.FindShareAgreementsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                null, null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ShareAgreementRaw>
            {
                Data =
                [
                    new ShareAgreementRaw
                    {
                        ID          = 9888,
                        Number      = "ДДУ-1",
                        ConditionalNumber = "1",
                        RoomKindRef = new VisaryRef { ID = RoomKindIdApartment },
                        Room        = new VisaryRef { ID = 88888 }, // ← чужая комната
                    }
                ],
                Total = 1,
            });

        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, null, null);
        var rows = new[] { MakeRow(10, "Квартира", "1", "1", 42.5) };

        var result = await _mapper.ApplyAsync(ctx, _visaryDb, rows, default);

        Assert.Equal(1, result.AppliedCount);
        // Создан НОВЫЙ ДДУ — чужой не угнан.
        _mockCrud.Verify(c => c.CreateShareAgreementAsync(
            It.IsAny<ShareAgreementCreateRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        // PATCH чужого ДДУ не делали.
        _mockCrud.Verify(c => c.PatchShareAgreementAsync(
            9888, It.IsAny<ShareAgreementPatchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
