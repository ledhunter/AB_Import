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
/// Покрытие создания Финмодели (<c>fmmodel</c>) из второго (опционального) файла
/// FinModel-импорта — листа «План». Сценарии: edge-picking, идемпотентный skip,
/// предупреждение при отсутствии файла. См. doc_project/110-finmodel-plan-and-fmmodel.md.
/// </summary>
public class FinModelFmModelTests : IDisposable
{
    private const int ProjectId = 4584;
    private const int SiteId = 7890;

    private readonly FinModelImportMapper _mapper;
    private readonly VisaryDbContext _dbContext;
    private readonly Mock<ICrudClient> _mockCrud;
    private readonly Mock<IListViewClient> _mockListView;
    private readonly TestFileStorage _fileStorage;
    private readonly ServiceProvider _serviceProvider;

    public FinModelFmModelTests()
    {
        _mockCrud = new Mock<ICrudClient>();
        _mockListView = new Mock<IListViewClient>();

        // Default — справочники минимальны, listview/wbs пуст (Pre-check 1 doc 109).
        _mockListView.Setup(c => c.ListFinishingMaterialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FinishingMaterialRaw> { Data = [], Total = 0 });
        _mockListView.Setup(c => c.ListEstateClassesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<EstateClassRaw> { Data = [], Total = 0 });
        _mockListView.Setup(c => c.GetWbsBySiteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<WbsRaw> { Data = [], Total = 0 });
        _mockCrud.Setup(c => c.GetProjectByIdFullAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConstructionProjectFull { ID = ProjectId, Title = "Тест ДОУ" });

        // FmModel: по умолчанию pre-check возвращает пусто → создаём.
        _mockListView
            .Setup(c => c.FindFmModelsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmModelRaw> { Data = [], Total = 0 });
        _mockCrud
            .Setup(c => c.CreateFmModelAsync(It.IsAny<FmModelCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FmModelCreateRequest req, CancellationToken _) => new FmModelRaw
            {
                ID = 47,
                Title = req.Title,
                ProjectCode = req.ProjectCode,
                ABProjectID = req.ABProjectID,
                ABConstructionSiteID = req.ABConstructionSiteID,
                PeriodStart = req.PeriodStart,
                PeriodEnd = req.PeriodEnd,
            });

        _fileStorage = new TestFileStorage();
        var budgetRef = new BudgetReferenceProvider(NullLogger<BudgetReferenceProvider>.Instance);

        var services = new ServiceCollection();
        // BudgetVisaryUploader не используется в этих тестах (budgetRows = пустые),
        // но регистрация нужна для IServiceScopeFactory captive-pattern маппера.
        services.AddSingleton(Mock.Of<IBudgetVisaryUploader>());
        _serviceProvider = services.BuildServiceProvider();

        _mapper = new FinModelImportMapper(
            NullLogger<FinModelImportMapper>.Instance,
            _mockCrud.Object,
            _mockListView.Object,
            budgetRef,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _fileStorage);

        var options = new DbContextOptionsBuilder<VisaryDbContext>()
            .UseInMemoryDatabase($"FinModelFmModelTest_{Guid.NewGuid()}")
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

    // ─────────── ParseQuarter ───────────

    [Theory]
    [InlineData("1 кв", 1)]
    [InlineData("2 кв", 2)]
    [InlineData("3 кв", 3)]
    [InlineData("4 кв", 4)]
    [InlineData("1кв", 1)]
    [InlineData("1 квартал", 1)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("кв", null)]
    [InlineData("5 кв", null)]   // вне 1..4
    public void ParseQuarter_HandlesCommonInputs(string raw, object? expected)
    {
        Assert.Equal(expected, FinModelImportMapper.ParseQuarter(raw));
    }

    // ─────────── ReadPlanPeriods ───────────

    [Fact]
    public void ReadPlanPeriods_EdgePicking_FromMultiYearSheet()
    {
        // Эталонная раскладка: r3=«Год», r5=«Квартал», B=«Сумма», C+ = первый квартал
        // первого года; года стоят только в первой колонке группы из 4.
        var bytes = BuildPlanXlsx(new[]
        {
            (Year: 2024, Quarter: "1 кв"),
            (Year: 0,    Quarter: "2 кв"),
            (Year: 0,    Quarter: "3 кв"),
            (Year: 0,    Quarter: "4 кв"),
            (Year: 2025, Quarter: "1 кв"),
            (Year: 0,    Quarter: "2 кв"),
            (Year: 0,    Quarter: "3 кв"),
            (Year: 0,    Quarter: "4 кв"),
            (Year: 2026, Quarter: "1 кв"),
            (Year: 0,    Quarter: "2 кв"),
        });

        using var stream = new MemoryStream(bytes);
        var periods = FinModelImportMapper.ReadPlanPeriods(stream);

        Assert.Equal("2024Q1", periods.PeriodStart);
        Assert.Equal("2026Q2", periods.PeriodEnd);
    }

    [Fact]
    public void ReadPlanPeriods_NoSheetNamed_План_Throws()
    {
        // Создаём XLSX без листа «План» — должен бросить FinModelPlanParseException.
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("ДругойЛист");
            wb.SaveAs(ms);
        }
        ms.Position = 0;
        var ex = Assert.Throws<FinModelImportMapper.FinModelPlanParseException>(
            () => FinModelImportMapper.ReadPlanPeriods(ms));
        Assert.Contains("План", ex.Message);
    }

    [Fact]
    public void ReadPlanPeriods_MissingHeaderRows_Throws()
    {
        // Лист «План» есть, но без строк «Год»/«Квартал» в первых 15 строках.
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("План");
            ws.Cell(1, 1).Value = "Что-то совсем не то";
            wb.SaveAs(ms);
        }
        ms.Position = 0;
        var ex = Assert.Throws<FinModelImportMapper.FinModelPlanParseException>(
            () => FinModelImportMapper.ReadPlanPeriods(ms));
        Assert.Contains("Год", ex.Message);
    }

    // ─────────── ApplyAsync — happy / idempotent / no-file ───────────

    [Fact]
    public async Task ApplyAsync_NoSecondaryFile_AddsSkipWarning_And_DoesNotCallCreateFmModel()
    {
        // Без второго файла FinModel-Apply должен:
        // (1) добавить info-метку fmmodel_skipped_no_plan_file в errors;
        // (2) НЕ обращаться ни к FindFmModelsAsync, ни к CreateFmModelAsync.
        var ctx = new ImportContext(Guid.NewGuid(), ProjectId, SiteId, null);
        var result = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        Assert.Contains(result.Errors, e => e.ErrorCode == "fmmodel_skipped_no_plan_file");
        _mockListView.Verify(c => c.FindFmModelsAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCrud.Verify(c => c.CreateFmModelAsync(
            It.IsAny<FmModelCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ApplyAsync_PlanFile_CallsCreateFmModel_WithEdgePeriods()
    {
        var bytes = BuildPlanXlsx(new[]
        {
            (Year: 2024, Quarter: "2 кв"),
            (Year: 0,    Quarter: "3 кв"),
            (Year: 0,    Quarter: "4 кв"),
            (Year: 2025, Quarter: "1 кв"),
            (Year: 0,    Quarter: "2 кв"),
            (Year: 0,    Quarter: "3 кв"),
            (Year: 0,    Quarter: "4 кв"),
            (Year: 2026, Quarter: "1 кв"),
            (Year: 0,    Quarter: "2 кв"),
            (Year: 0,    Quarter: "3 кв"),
            (Year: 0,    Quarter: "4 кв"),
            (Year: 2027, Quarter: "1 кв"),
        });
        _fileStorage.Put("plan.xlsx", bytes);

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");

        var result = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        _mockCrud.Verify(c => c.CreateFmModelAsync(
            It.Is<FmModelCreateRequest>(r =>
                r.Title == "Модель из эксель файла"
                && r.ProjectCode == "Тест ДОУ"
                && r.ABProjectID == ProjectId
                && r.ABConstructionSiteID == SiteId
                && r.PeriodStart == "2024Q2"
                && r.PeriodEnd == "2027Q1"),
            It.IsAny<CancellationToken>()),
            Times.Once);
        // Skip-предупреждение НЕ должно появиться — файл есть.
        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "fmmodel_skipped_no_plan_file");
    }

    [Fact]
    public async Task ApplyAsync_PlanFile_ExistingFmModel_SkipsCreate()
    {
        var bytes = BuildPlanXlsx(new[]
        {
            (Year: 2024, Quarter: "1 кв"),
            (Year: 0,    Quarter: "2 кв"),
        });
        _fileStorage.Put("plan.xlsx", bytes);

        // FmModel уже существует — pre-check вернул запись.
        _mockListView
            .Setup(c => c.FindFmModelsAsync(ProjectId, SiteId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FmModelRaw>
            {
                Data = [new FmModelRaw
                {
                    ID = 47, Title = "Модель из эксель файла",
                    ABProjectID = ProjectId, ABConstructionSiteID = SiteId,
                    PeriodStart = "2024Q1", PeriodEnd = "2024Q2",
                }],
                Total = 1,
            });

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");

        var result = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        _mockCrud.Verify(c => c.CreateFmModelAsync(
            It.IsAny<FmModelCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Contains(result.Errors, e => e.ErrorCode == "fmmodel_skipped_already_exists");
    }

    [Fact]
    public async Task ApplyAsync_PlanFile_ParseError_AddsErrorAndDoesNotCallCreate()
    {
        // Файл есть, но в нём нет ни «Год», ни «Квартал» → парсер бросает,
        // мапер ловит и пишет одну ошибку.
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("План"); // пустой лист, нет шапки
            wb.SaveAs(ms);
        }
        _fileStorage.Put("plan.xlsx", ms.ToArray());

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");

        var result = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        Assert.Contains(result.Errors, e => e.ErrorCode == "fmmodel_plan_parse_error");
        _mockCrud.Verify(c => c.CreateFmModelAsync(
            It.IsAny<FmModelCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─────────── Helpers ───────────

    /// <summary>
    /// Собирает XLSX-байты с одним листом «План»:
    ///   r3 = «Год» в A, годы — в указанных колонках начиная с C
    ///   r4 = «№ столбца» (только заполнитель)
    ///   r5 = «Квартал» в A, «Сумма» в B, квартальные значения с C
    /// Если Year=0 — ячейка года остаётся пустой (forward-fill).
    /// </summary>
    private static byte[] BuildPlanXlsx((int Year, string Quarter)[] cols)
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("План");
            ws.Cell(3, 1).Value = "Год";
            ws.Cell(5, 1).Value = "Квартал";
            ws.Cell(5, 2).Value = "Сумма";
            for (int i = 0; i < cols.Length; i++)
            {
                var c = 3 + i; // первая колонка для данных — C (3)
                if (cols[i].Year != 0) ws.Cell(3, c).Value = cols[i].Year;
                ws.Cell(5, c).Value = cols[i].Quarter;
            }
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }
}
