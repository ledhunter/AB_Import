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
            .Setup(c => c.FindFmModelsAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
    [InlineData("10", null)]     // помесячная колонка (октябрь) — НЕ Q1
    [InlineData("12", null)]     // помесячная колонка (декабрь) — НЕ Q1
    public void ParseQuarter_HandlesCommonInputs(string raw, object? expected)
    {
        Assert.Equal(expected, FinModelImportMapper.ParseQuarter(raw));
    }

    // ─────────── ReadGeneralScheduleData ───────────

    [Fact]
    public void ReadGeneralScheduleData_EdgePicking_FromMultiYearTable()
    {
        // Одна таблица: r3=Год, r4=Квартал/Сумма, r5=План (маркер), r6=Площадь,
        // r7=Стоимость, r8=Доход (Summ). В этом тесте — только заголовок, чтобы
        // проверить краевые периоды (значения нулевые).
        var bytes = BuildGeneralScheduleXlsx(new[]
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
        var data = FinModelImportMapper.ReadGeneralScheduleData(stream);

        Assert.Equal("2024Q1", data.PeriodStart);
        Assert.Equal("2026Q2", data.PeriodEnd);
    }

    [Fact]
    public void ReadGeneralScheduleData_NoSheet_Throws()
    {
        // XLSX без листа «Общий график» → FinModelPlanParseException.
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("ДругойЛист");
            wb.SaveAs(ms);
        }
        ms.Position = 0;
        var ex = Assert.Throws<FinModelImportMapper.FinModelPlanParseException>(
            () => FinModelImportMapper.ReadGeneralScheduleData(ms));
        Assert.Contains("Общий график", ex.Message);
    }

    [Fact]
    public void ReadGeneralScheduleData_NoYearQuarterHeaders_Throws()
    {
        // Лист «Общий график» есть, но без пары строк «Год»/«Квартал».
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Общий график");
            ws.Cell(1, 1).Value = "Что-то совсем не то";
            wb.SaveAs(ms);
        }
        ms.Position = 0;
        var ex = Assert.Throws<FinModelImportMapper.FinModelPlanParseException>(
            () => FinModelImportMapper.ReadGeneralScheduleData(ms));
        Assert.Contains("Общий график", ex.Message);
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
            It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCrud.Verify(c => c.CreateFmModelAsync(
            It.IsAny<FmModelCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ApplyAsync_PlanFile_CallsCreateFmModel_WithEdgePeriods()
    {
        var bytes = BuildGeneralScheduleXlsx(new[]
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
        var bytes = BuildGeneralScheduleXlsx(new[]
        {
            (Year: 2024, Quarter: "1 кв"),
            (Year: 0,    Quarter: "2 кв"),
        });
        _fileStorage.Put("plan.xlsx", bytes);

        // FmModel уже существует с тем же периодом 2024Q1..2024Q2 — pre-check вернул запись.
        // Фильтр по PeriodStart/PeriodEnd должен точно совпадать с распарсенными краями
        // (см. doc 112 v1.3: одну сайт может содержать несколько Финмоделей с разными
        // диапазонами лет).
        _mockListView
            .Setup(c => c.FindFmModelsAsync(ProjectId, SiteId,
                "2024Q1", "2024Q2", It.IsAny<CancellationToken>()))
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
    public async Task ApplyAsync_PlanFile_ExistingFmModelWithDifferentPeriod_CreatesNew()
    {
        // На сайте уже есть Финмодель с периодом 2024Q1..2024Q2, а новый файл
        // содержит 2023Q1..2024Q2 (расширенный диапазон). Pre-check фильтрует по
        // PeriodStart/PeriodEnd ⇒ Visary возвращает пусто ⇒ мы создаём НОВУЮ
        // финмодель, а не реюзаем чужую. Регрессионный тест на сценарий «Репино-Парк»,
        // см. doc 112 v1.3.
        var bytes = BuildGeneralScheduleXlsx(new[]
        {
            (Year: 2023, Quarter: "1 кв"),
            (Year: 0,    Quarter: "2 кв"),
            (Year: 0,    Quarter: "3 кв"),
            (Year: 0,    Quarter: "4 кв"),
            (Year: 2024, Quarter: "1 кв"),
            (Year: 0,    Quarter: "2 кв"),
        });
        _fileStorage.Put("plan.xlsx", bytes);

        // Visary listview/fmmodel с фильтром PeriodStart=2023Q1, PeriodEnd=2024Q2
        // не находит подходящих ⇒ дефолтная пустая выдача из ctor применяется.
        // Чужая 2024Q1..2024Q2-финмодель «прячется» за period-фильтром (matcher на
        // ином PeriodStart/PeriodEnd просто не сработает, дефолтная пустая выдача
        // на It.IsAny<…> вернётся).

        var ctx = new ImportContext(
            Guid.NewGuid(), ProjectId, SiteId, null,
            SecondaryFileRelativePath: "plan.xlsx");
        var result = await _mapper.ApplyAsync(ctx, _dbContext, [], default);

        // Pre-check был вызван именно с новыми границами 2023Q1..2024Q2 (без них
        // мы бы переиспользовали чужую 2024-only финмодель).
        _mockListView.Verify(c => c.FindFmModelsAsync(
            ProjectId, SiteId, "2023Q1", "2024Q2", It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCrud.Verify(c => c.CreateFmModelAsync(
            It.Is<FmModelCreateRequest>(r =>
                r.PeriodStart == "2023Q1" && r.PeriodEnd == "2024Q2"),
            It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "fmmodel_skipped_already_exists");
    }

    [Fact]
    public async Task ApplyAsync_PlanFile_ParseError_AddsErrorAndDoesNotCallCreate()
    {
        // Файл есть, но в нём нет ни «Год», ни «Квартал» → парсер бросает,
        // мапер ловит и пишет одну ошибку.
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("Общий график"); // пустой лист, нет шапки
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
    /// Собирает XLSX-байты с листом «Общий график» по эталонной раскладке
    /// (одна таблица квартир, чтобы парсер нашёл валидную категорию):
    ///   r3 = «Год» в A, годы — в указанных колонках начиная с C
    ///   r4 = «Квартал» в A, «Сумма» в B, квартальные значения с C
    ///   r5 = «План» (маркер)
    ///   r6 = «Квартиры, кв.м» (Amount-строка — резолвит категорию)
    ///   r7 = «Стоимость 1 кв.м» (Cost-строка)
    ///   r8 = «Доход» (Summ-строка)
    /// Если Year=0 — ячейка года остаётся пустой (forward-fill).
    /// </summary>
    private static byte[] BuildGeneralScheduleXlsx((int Year, string Quarter)[] cols)
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Общий график");
            ws.Cell(3, 1).Value = "Год";
            ws.Cell(4, 1).Value = "Квартал";
            ws.Cell(4, 2).Value = "Сумма";
            ws.Cell(5, 1).Value = "План";
            ws.Cell(6, 1).Value = "Квартиры, кв.м";
            ws.Cell(7, 1).Value = "Стоимость 1 кв.м";
            ws.Cell(8, 1).Value = "Доход";
            for (int i = 0; i < cols.Length; i++)
            {
                var c = 3 + i; // первая колонка для данных — C (3)
                if (cols[i].Year != 0) ws.Cell(3, c).Value = cols[i].Year;
                ws.Cell(4, c).Value = cols[i].Quarter;
            }
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }
}
