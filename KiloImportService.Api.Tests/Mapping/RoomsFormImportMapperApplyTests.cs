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

    private static MappedRow MakeRow(int row, string sheet, string roomNumber, string buildingSection, double area)
    {
        var mapped = new Dictionary<string, object?>
        {
            ["Sheet"] = sheet,
            ["RoomNumber"] = roomNumber,
            ["RoomKindId"] = RoomKindIdApartment,
            ["RoomKindTitle"] = "Квартира",
            ["RoomCategory"] = 0,
            ["SectionTitle"] = "1.1",
            ["SectionTitleNumeric"] = "1.1",
            ["BuildingSection"] = buildingSection,
            ["Floor"] = "5",
            ["RoomsCount"] = 1,
            ["ProjectArea"] = area,
            ["CostForOne"] = 100000.0,
            ["MarketCostPerM"] = 120000.0,
            ["ZalogCostPerM"] = 90000.0,
            ["ShareAgreementNumber"] = $"ДДУ-{roomNumber}",
            ["StageNumber"] = 1,
            ["StageNumberRaw"] = "1",
            ["ProjectNumber"] = "PRJ-1",
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
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, SiteId, null);
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
        // Сценарий повторного импорта того же файла:
        //   первый Apply — создаёт RoomApplySnapshot;
        //   второй Apply с тем же MappedValues — hash совпадает → строка skip-ается
        //   с меткой «Без изменений — пропуск (snapshot)»; никакого CREATE/PATCH не происходит.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, SiteId, null);
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
    public async Task ApplyAsync_SecondRun_ChangedArea_TriggersPatchRoom()
    {
        // Если хоть одно поле, входящее в HashedMappedFields, изменилось — diff-skip
        // не сработает, PATCH должен пройти. Это гарантирует, что snapshot не «маскирует»
        // реальные изменения площади/стоимости.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, SiteId, null);

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
    public async Task ApplyAsync_MultipleSheets_RowActionLogPreservesSheetPerRow()
    {
        // Многолистовой файл (doc 80, doc 89): каждый RowActionLog должен нести имя своего листа,
        // иначе pipeline не сможет связать Actions с правильной StagedRow в БД и UI не
        // отрисует actions в строках.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, SiteId, null);
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
}
