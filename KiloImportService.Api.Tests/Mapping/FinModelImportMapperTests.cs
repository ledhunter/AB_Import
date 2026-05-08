using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Mapping;
using KiloImportService.Api.Domain.Mapping.Budget;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Visary.Api;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.ListView;
using Xunit;

namespace KiloImportService.Api.Tests.Mapping;

public class FinModelImportMapperTests : IDisposable
{
    private readonly FinModelImportMapper _mapper;
    private readonly VisaryDbContext _dbContext;
    private readonly Mock<ICrudClient> _mockCrud;
    private readonly Mock<IListViewClient> _mockListView;

    public FinModelImportMapperTests()
    {
        _mockCrud = new Mock<ICrudClient>();
        _mockCrud.Setup(c => c.UpdateSiteFinishingMaterialAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockCrud.Setup(c => c.UpdateSiteEstateClassAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockCrud.Setup(c => c.UpdateSiteAddressAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockListView = new Mock<IListViewClient>();

        // Возвращаем тот же набор, что показал бы боевой Visary listview/finishingmaterial.
        _mockListView
            .Setup(c => c.ListFinishingMaterialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<FinishingMaterialRaw>
            {
                Data = new List<FinishingMaterialRaw>
                {
                    new() { ID = 3, Title = "Черновая",     Code = "PF" },
                    new() { ID = 2, Title = "Предчистовая", Code = "WB" },
                    new() { ID = 1, Title = "Чистовая",     Code = "FF" },
                },
                Total = 3,
            });

        // Реальный набор Visary listview/estateclass — взят из ответа prod-стенда.
        _mockListView
            .Setup(c => c.ListEstateClassesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<EstateClassRaw>
            {
                Data = new List<EstateClassRaw>
                {
                    new() { ID = 12, Title = "Премиум" },
                    new() { ID = 7,  Title = "Стандарт" },
                    new() { ID = 6,  Title = "Другое" },
                    new() { ID = 5,  Title = "Элитный" },
                    new() { ID = 4,  Title = "Бизнес" },
                    new() { ID = 3,  Title = "Комфорт+" },
                    new() { ID = 2,  Title = "Комфорт" },
                    new() { ID = 1,  Title = "Типовой" },
                },
                Total = 8,
            });

        // Indicator-flow: разделяем моки по titleFilter, чтобы каждый показатель резолвился
        // в свой ID, value-id и RowVersion. Realistic-сценарий — у каждого показателя
        // на сайте свои значения по стадиям.
        _mockListView
            .Setup(c => c.GetIndicatorsBySiteAsync(It.IsAny<int>(), "Площадь застройки", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ConstructionSiteIndicatorRaw>
            {
                Data = new() { new() { ID = 114306, Title = "Площадь застройки" } },
                Total = 1,
            });
        _mockListView
            .Setup(c => c.GetIndicatorsBySiteAsync(It.IsAny<int>(), "Плотность застройки", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ConstructionSiteIndicatorRaw>
            {
                Data = new() { new() { ID = 114307, Title = "Плотность застройки" } },
                Total = 1,
            });

        // По indicatorId возвращаем разные value-наборы (Stage=50 «Экспертиза» — обязательно).
        _mockListView
            .Setup(c => c.GetIndicatorValuesByIndicatorAsync(114306, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ConstructionSiteIndicatorValueRaw>
            {
                Data = new()
                {
                    new() { ID = 823481, Stage = 50, Value = 0 },
                    new() { ID = 823482, Stage = 30, Value = 0 },
                },
                Total = 2,
            });
        _mockListView
            .Setup(c => c.GetIndicatorValuesByIndicatorAsync(114307, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ConstructionSiteIndicatorValueRaw>
            {
                Data = new() { new() { ID = 823491, Stage = 50, Value = 0 } },
                Total = 1,
            });

        // GET CRUD по valueId — свежий RowVersion.
        _mockCrud
            .Setup(c => c.GetIndicatorValueByIdAsync(823481, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConstructionSiteIndicatorValueFull { ID = 823481, RowVersion = 4755619 });
        _mockCrud
            .Setup(c => c.GetIndicatorValueByIdAsync(823491, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConstructionSiteIndicatorValueFull { ID = 823491, RowVersion = 4755700 });

        _mockCrud
            .Setup(c => c.PatchIndicatorValueAsync(It.IsAny<int>(), It.IsAny<IndicatorValuePatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Эталонный справочник статей бюджета грузится из embedded-ресурса
        // KiloImportService.Api → используем реальный provider (без сети, in-process).
        var budgetRef = new BudgetReferenceProvider(
            NullLogger<BudgetReferenceProvider>.Instance);

        _mapper = new FinModelImportMapper(
            NullLogger<FinModelImportMapper>.Instance,
            _mockCrud.Object,
            _mockListView.Object,
            budgetRef);

        var options = new DbContextOptionsBuilder<VisaryDbContext>()
            .UseInMemoryDatabase($"FinModelTest_{Guid.NewGuid()}")
            .Options;
        _dbContext = new VisaryDbContext(options);

        _dbContext.ConstructionSites.Add(new ConstructionSite
        {
            Id = 123,
            Title = "Тестовый объект",
            Hidden = false,
        });
        _dbContext.SaveChanges();
    }

    public void Dispose() => _dbContext?.Dispose();

    private static ParsedRow Row(
        string finishing = "Черновая",
        string estate = "Комфорт",
        string buildingArea = "1234.5",
        string buildingDensity = "0.42",
        string address = "ул. Ленина, 1")
        => new(SourceRowNumber: 2, Sheet: "inputs",
            Cells: new Dictionary<string, string>
            {
                ["Тип отделки"]        = finishing,
                ["Класс жилья"]        = estate,
                ["Площадь застройки"]  = buildingArea,
                ["Плотность застройки"] = buildingDensity,
                ["Строительный адрес"] = address,
            });

    private static ImportContext Ctx(int? siteId = 123)
        => new(Guid.NewGuid(), null, siteId, null);

    [Fact]
    public void TypeCode_Is_finmodel() => Assert.Equal("finmodel", _mapper.ImportTypeCode);

    [Theory]
    [InlineData("Черновая", 3)]
    [InlineData("Предчистовая", 2)]
    [InlineData("Чистовая", 1)]
    public async Task ValidateAsync_FinishingMaterial_MapsToCorrectId(string title, int expectedId)
    {
        var result = await _mapper.ValidateAsync(Ctx(), new[] { Row(finishing: title) }, _dbContext, default);

        Assert.Single(result.Rows);
        Assert.True(result.Rows[0].IsValid);
        Assert.Equal(expectedId, result.Rows[0].MappedValues.RootElement.GetProperty("FinishingMaterialId").GetInt32());
    }

    [Theory]
    [InlineData("Премиум", 12)]
    [InlineData("Стандарт", 7)]
    [InlineData("Комфорт+", 3)]
    [InlineData("Типовой", 1)]
    public async Task ValidateAsync_EstateClass_MapsToCorrectId(string title, int expectedId)
    {
        var result = await _mapper.ValidateAsync(Ctx(), new[] { Row(estate: title) }, _dbContext, default);

        Assert.Single(result.Rows);
        Assert.True(result.Rows[0].IsValid);
        Assert.Equal(expectedId, result.Rows[0].MappedValues.RootElement.GetProperty("EstateClassId").GetInt32());
    }

    [Fact]
    public async Task ValidateAsync_ValidRow_PopulatesBothIds()
    {
        var result = await _mapper.ValidateAsync(
            Ctx(), new[] { Row("Черновая", "Премиум") }, _dbContext, default);

        Assert.True(result.Rows[0].IsValid);
        var root = result.Rows[0].MappedValues.RootElement;
        Assert.Equal(3, root.GetProperty("FinishingMaterialId").GetInt32());
        Assert.Equal(12, root.GetProperty("EstateClassId").GetInt32());
        Assert.Equal("Черновая", root.GetProperty("FinishingMaterialTitle").GetString());
        Assert.Equal("Премиум", root.GetProperty("EstateClassTitle").GetString());
    }

    [Fact]
    public async Task ValidateAsync_NoTargetColumns_ReturnsSingleFileLevelError()
    {
        var rows = new[]
        {
            new ParsedRow(2, "inputs", new Dictionary<string, string> { ["Что-то ещё"] = "x" }),
        };

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        Assert.Empty(result.Rows);
        Assert.Single(result.FileLevelErrors);
        Assert.Equal("column_not_found", result.FileLevelErrors[0].ErrorCode);
        Assert.Contains("Финмодель", result.FileLevelErrors[0].Message);
    }

    [Fact]
    public async Task ValidateAsync_MissingEstateClassColumn_ReturnsFileLevelError()
    {
        // Только «Тип отделки» — отсутствуют «Класс жилья» И «Площадь застройки» →
        // две file-level ошибки про конкретные колонки.
        var rows = new[]
        {
            new ParsedRow(2, "inputs", new Dictionary<string, string> { ["Тип отделки"] = "Черновая" }),
        };

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        Assert.Empty(result.Rows);
        Assert.Contains(result.FileLevelErrors,
            e => e.ErrorCode == "column_not_found" && e.Message.Contains("Класс жилья"));
    }

    [Fact]
    public async Task ValidateAsync_MissingFinishingTypeColumn_ReturnsFileLevelError()
    {
        var rows = new[]
        {
            new ParsedRow(2, "inputs", new Dictionary<string, string> { ["Класс жилья"] = "Премиум" }),
        };

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        Assert.Empty(result.Rows);
        Assert.Contains(result.FileLevelErrors,
            e => e.ErrorCode == "column_not_found" && e.Message.Contains("Тип отделки"));
    }

    [Fact]
    public async Task ValidateAsync_MissingBuildingAreaColumn_ReturnsFileLevelError()
    {
        var rows = new[]
        {
            new ParsedRow(2, "inputs", new Dictionary<string, string>
            {
                ["Тип отделки"] = "Черновая",
                ["Класс жилья"] = "Премиум",
            }),
        };

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        Assert.Empty(result.Rows);
        Assert.Contains(result.FileLevelErrors,
            e => e.ErrorCode == "column_not_found" && e.Message.Contains("Площадь застройки"));
    }

    [Fact]
    public async Task ValidateAsync_EmptyEstateClassValue_ReturnsRowError()
    {
        var result = await _mapper.ValidateAsync(
            Ctx(), new[] { Row(estate: "") }, _dbContext, default);

        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors,
            e => e.ErrorCode == "value_empty" && e.Message.Contains("Класс жилья"));
    }

    [Fact]
    public async Task ValidateAsync_InvalidEstateClassValue_ReturnsRowError()
    {
        var result = await _mapper.ValidateAsync(
            Ctx(), new[] { Row(estate: "Несуществующий класс") }, _dbContext, default);

        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors,
            e => e.ErrorCode == "invalid_value" && e.Message.Contains("Класс жилья"));
    }

    [Fact]
    public async Task ValidateAsync_EmptyFinishingValue_ReturnsRowError()
    {
        var result = await _mapper.ValidateAsync(
            Ctx(), new[] { Row(finishing: "") }, _dbContext, default);

        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors,
            e => e.ErrorCode == "value_empty" && e.Message.Contains("Тип отделки"));
    }

    [Fact]
    public async Task ValidateAsync_InvalidFinishingValue_ReturnsRowError()
    {
        var result = await _mapper.ValidateAsync(
            Ctx(), new[] { Row(finishing: "Неизвестная отделка") }, _dbContext, default);

        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors,
            e => e.ErrorCode == "invalid_value" && e.Message.Contains("Тип отделки"));
    }

    [Fact]
    public async Task ValidateAsync_NoSiteId_ReturnsFileError()
    {
        var result = await _mapper.ValidateAsync(
            Ctx(siteId: null), new[] { Row() }, _dbContext, default);

        Assert.Empty(result.Rows);
        Assert.Contains(result.FileLevelErrors, e => e.ErrorCode == "site_required");
    }

    [Theory]
    [InlineData("FinishingType")]
    [InlineData("Finishing")]
    [InlineData("тип отделки")]
    public async Task ValidateAsync_FinishingColumnAliases_WorkCaseInsensitive(string colName)
    {
        var row = new ParsedRow(2, "inputs",
            new Dictionary<string, string>
            {
                [colName]                = "Черновая",
                ["Класс жилья"]          = "Комфорт",
                ["Площадь застройки"]    = "100",
                ["Плотность застройки"]  = "0.5",
                ["Строительный адрес"]   = "ул. Ленина, 1",
            });

        var result = await _mapper.ValidateAsync(Ctx(), new[] { row }, _dbContext, default);

        Assert.True(result.Rows[0].IsValid);
    }

    [Theory]
    [InlineData("EstateClass")]
    [InlineData("Класс недвижимости")]
    [InlineData("класс жилья")]
    public async Task ValidateAsync_EstateClassColumnAliases_WorkCaseInsensitive(string colName)
    {
        var row = new ParsedRow(2, "inputs",
            new Dictionary<string, string>
            {
                ["Тип отделки"]          = "Черновая",
                [colName]                = "Комфорт",
                ["Площадь застройки"]    = "100",
                ["Плотность застройки"]  = "0.5",
                ["Строительный адрес"]   = "ул. Ленина, 1",
            });

        var result = await _mapper.ValidateAsync(Ctx(), new[] { row }, _dbContext, default);

        Assert.True(result.Rows[0].IsValid);
    }

    [Theory]
    [InlineData("100",      100.0)]
    [InlineData("123.45",   123.45)]
    [InlineData("123,45",   123.45)]   // ru-RU разделитель
    [InlineData("12 345.6", 12345.6)]  // пробел-разделитель тысяч
    public async Task ValidateAsync_BuildingArea_ParsesFlexibleDouble(string raw, double expected)
    {
        var result = await _mapper.ValidateAsync(
            Ctx(), new[] { Row(buildingArea: raw) }, _dbContext, default);

        Assert.True(result.Rows[0].IsValid);
        var indicators = result.Rows[0].MappedValues.RootElement.GetProperty("Indicators");
        Assert.Equal(expected,
            indicators.GetProperty("Площадь застройки").GetDouble(),
            precision: 4);
    }

    [Theory]
    [InlineData("BuildingArea")]
    [InlineData("площадь застройки")]   // case-insensitive
    public async Task ValidateAsync_BuildingAreaColumnAliases_WorkCaseInsensitive(string colName)
    {
        var row = new ParsedRow(2, "inputs",
            new Dictionary<string, string>
            {
                ["Тип отделки"]          = "Черновая",
                ["Класс жилья"]          = "Комфорт",
                [colName]                = "999",
                ["Плотность застройки"]  = "0.5",
                ["Строительный адрес"]   = "ул. Ленина, 1",
            });

        var result = await _mapper.ValidateAsync(Ctx(), new[] { row }, _dbContext, default);

        Assert.True(result.Rows[0].IsValid);
    }

    [Fact]
    public async Task ValidateAsync_BuildingDensity_ParsedAndStoredInIndicators()
    {
        var result = await _mapper.ValidateAsync(
            Ctx(), new[] { Row(buildingDensity: "0.65") }, _dbContext, default);

        Assert.True(result.Rows[0].IsValid);
        var indicators = result.Rows[0].MappedValues.RootElement.GetProperty("Indicators");
        Assert.Equal(0.65, indicators.GetProperty("Плотность застройки").GetDouble(), precision: 4);
    }

    [Fact]
    public async Task ValidateAsync_MissingBuildingDensityColumn_ReturnsFileLevelError()
    {
        var rows = new[]
        {
            new ParsedRow(2, "inputs", new Dictionary<string, string>
            {
                ["Тип отделки"]       = "Черновая",
                ["Класс жилья"]       = "Премиум",
                ["Площадь застройки"] = "100",
            }),
        };

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        Assert.Contains(result.FileLevelErrors,
            e => e.ErrorCode == "column_not_found" && e.Message.Contains("Плотность застройки"));
    }

    [Theory]
    [InlineData("BuildingDensity")]
    [InlineData("плотность застройки")]   // case-insensitive
    public async Task ValidateAsync_BuildingDensityColumnAliases_WorkCaseInsensitive(string colName)
    {
        var row = new ParsedRow(2, "inputs",
            new Dictionary<string, string>
            {
                ["Тип отделки"]        = "Черновая",
                ["Класс жилья"]        = "Комфорт",
                ["Площадь застройки"]  = "100",
                [colName]              = "0.5",
                ["Строительный адрес"] = "ул. Ленина, 1",
            });

        var result = await _mapper.ValidateAsync(Ctx(), new[] { row }, _dbContext, default);

        Assert.True(result.Rows[0].IsValid);
    }

    [Fact]
    public async Task ValidateAsync_BuildingArea_NotANumber_ReturnsRowError()
    {
        var result = await _mapper.ValidateAsync(
            Ctx(), new[] { Row(buildingArea: "не число") }, _dbContext, default);

        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors,
            e => e.ErrorCode == "invalid_value" && e.Message.Contains("Площадь застройки"));
    }

    [Fact]
    public async Task ValidateAsync_BuildingArea_Empty_ReturnsRowError()
    {
        var result = await _mapper.ValidateAsync(
            Ctx(), new[] { Row(buildingArea: "") }, _dbContext, default);

        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors,
            e => e.ErrorCode == "value_empty" && e.Message.Contains("Площадь застройки"));
    }

    [Fact]
    public async Task ValidateAsync_EstateClassDictionaryUnavailable_ReturnsFileError()
    {
        _mockListView
            .Setup(c => c.ListEstateClassesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Visary down"));

        var result = await _mapper.ValidateAsync(Ctx(), new[] { Row() }, _dbContext, default);

        Assert.Empty(result.Rows);
        Assert.Contains(result.FileLevelErrors,
            e => e.ErrorCode == "dictionary_unavailable" && e.Message.Contains("Класс недвижимости"));
    }

    [Fact]
    public async Task ApplyAsync_ValidRow_CallsAllUpdates()
    {
        var validation = await _mapper.ValidateAsync(
            Ctx(), new[] { Row("Черновая", "Премиум", buildingArea: "555.5", buildingDensity: "0.85") },
            _dbContext, default);

        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        Assert.Equal(1, apply.AppliedCount);
        _mockCrud.Verify(c => c.UpdateSiteFinishingMaterialAsync(123, 3, It.IsAny<CancellationToken>()), Times.Once);
        _mockCrud.Verify(c => c.UpdateSiteEstateClassAsync(123, 12, It.IsAny<CancellationToken>()), Times.Once);
        _mockCrud.Verify(c => c.UpdateSiteAddressAsync(123, "ул. Ленина, 1", It.IsAny<CancellationToken>()), Times.Once);

        // Indicator 1: «Площадь застройки» → indicator=114306 → value=823481, Stage=50 → PATCH 555.5
        _mockListView.Verify(c => c.GetIndicatorsBySiteAsync(123, "Площадь застройки", It.IsAny<CancellationToken>()), Times.Once);
        _mockListView.Verify(c => c.GetIndicatorValuesByIndicatorAsync(114306, It.IsAny<CancellationToken>()), Times.Once);
        _mockCrud.Verify(c => c.PatchIndicatorValueAsync(
            823481,
            It.Is<IndicatorValuePatchRequest>(r =>
                r.ID == 823481 && r.RowVersion == 4755619 && r.Value == 555.5),
            It.IsAny<CancellationToken>()), Times.Once);

        // Indicator 2: «Плотность застройки» → indicator=114307 → value=823491, Stage=50 → PATCH 0.85
        _mockListView.Verify(c => c.GetIndicatorsBySiteAsync(123, "Плотность застройки", It.IsAny<CancellationToken>()), Times.Once);
        _mockListView.Verify(c => c.GetIndicatorValuesByIndicatorAsync(114307, It.IsAny<CancellationToken>()), Times.Once);
        _mockCrud.Verify(c => c.PatchIndicatorValueAsync(
            823491,
            It.Is<IndicatorValuePatchRequest>(r =>
                r.ID == 823491 && r.RowVersion == 4755700 && r.Value == 0.85),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyAsync_IndicatorWithTrailingSpaceInTitle_StillMatches()
    {
        // Регрессия: реальный Visary возвращает Title="Площадь застройки " с хвостовым
        // пробелом. ApplyIndicatorAsync должен матчить через Trim()+OrdinalIgnoreCase.
        // Override только для «Площадь застройки» — для «Плотность застройки» используется
        // обычный setup из конструктора (тоже успешно резолвится, чтобы Apply не упал).
        _mockListView
            .Setup(c => c.GetIndicatorsBySiteAsync(It.IsAny<int>(), "Площадь застройки", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ConstructionSiteIndicatorRaw>
            {
                Data = new() { new() { ID = 114306, Title = "Площадь застройки " } }, // ← пробел
                Total = 1,
            });

        var validation = await _mapper.ValidateAsync(
            Ctx(), new[] { Row("Черновая", "Премиум", "777") }, _dbContext, default);
        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        Assert.Equal(1, apply.AppliedCount);
        Assert.Empty(apply.Errors);
        _mockCrud.Verify(c => c.PatchIndicatorValueAsync(
            823481,
            It.Is<IndicatorValuePatchRequest>(r => r.Value == 777),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyAsync_IndicatorNotFound_ReturnsErrorButDoesntFailOtherUpdates()
    {
        // Override обоих indicator-id, чтобы НИ ОДНА стадия Экспертиза не нашлась.
        _mockListView
            .Setup(c => c.GetIndicatorValuesByIndicatorAsync(114306, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ConstructionSiteIndicatorValueRaw>
            {
                Data = new() { new() { ID = 999, Stage = 30 } }, // только другая стадия
                Total = 1,
            });
        _mockListView
            .Setup(c => c.GetIndicatorValuesByIndicatorAsync(114307, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListViewResponse<ConstructionSiteIndicatorValueRaw>
            {
                Data = new() { new() { ID = 998, Stage = 30 } },
                Total = 1,
            });

        var validation = await _mapper.ValidateAsync(
            Ctx(), new[] { Row("Черновая", "Премиум", "555.5") }, _dbContext, default);

        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        Assert.Equal(0, apply.AppliedCount);
        Assert.Contains(apply.Errors, e => e.ErrorCode == "indicator_not_found");
        // FK-обновления всё равно выполнились — non-transactional семантика.
        _mockCrud.Verify(c => c.UpdateSiteFinishingMaterialAsync(123, 3, It.IsAny<CancellationToken>()), Times.Once);
        _mockCrud.Verify(c => c.UpdateSiteEstateClassAsync(123, 12, It.IsAny<CancellationToken>()), Times.Once);
        _mockCrud.Verify(c => c.PatchIndicatorValueAsync(It.IsAny<int>(), It.IsAny<IndicatorValuePatchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Address ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ул. Ленина, 1")]
    [InlineData("г. Москва, Тверская ул., д. 13")]
    public async Task ValidateAsync_Address_StoredAsString(string addr)
    {
        var result = await _mapper.ValidateAsync(
            Ctx(), new[] { Row(address: addr) }, _dbContext, default);

        Assert.True(result.Rows[0].IsValid);
        Assert.Equal(addr, result.Rows[0].MappedValues.RootElement.GetProperty("Address").GetString());
    }

    [Theory]
    [InlineData("Address")]
    [InlineData("Адрес")]
    [InlineData("строительный адрес")]   // case-insensitive
    public async Task ValidateAsync_AddressColumnAliases_WorkCaseInsensitive(string colName)
    {
        var row = new ParsedRow(2, "inputs",
            new Dictionary<string, string>
            {
                ["Тип отделки"]        = "Черновая",
                ["Класс жилья"]        = "Комфорт",
                ["Площадь застройки"]  = "100",
                ["Плотность застройки"] = "0.5",
                [colName]              = "ул. Тверская, 13",
            });

        var result = await _mapper.ValidateAsync(Ctx(), new[] { row }, _dbContext, default);

        Assert.True(result.Rows[0].IsValid);
        Assert.Equal("ул. Тверская, 13",
            result.Rows[0].MappedValues.RootElement.GetProperty("Address").GetString());
    }

    [Fact]
    public async Task ValidateAsync_MissingAddressColumn_ReturnsFileLevelError()
    {
        var rows = new[]
        {
            new ParsedRow(2, "inputs", new Dictionary<string, string>
            {
                ["Тип отделки"]         = "Черновая",
                ["Класс жилья"]         = "Премиум",
                ["Площадь застройки"]   = "100",
                ["Плотность застройки"] = "0.5",
            }),
        };

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        Assert.Empty(result.Rows);
        Assert.Contains(result.FileLevelErrors,
            e => e.ErrorCode == "column_not_found" && e.Message.Contains("Строительный адрес"));
    }

    [Fact]
    public async Task ValidateAsync_EmptyAddressValue_ReturnsRowError()
    {
        var result = await _mapper.ValidateAsync(
            Ctx(), new[] { Row(address: "") }, _dbContext, default);

        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors,
            e => e.ErrorCode == "value_empty" && e.Message.Contains("Строительный адрес"));
    }

    [Fact]
    public async Task ApplyAsync_Address_CallsUpdateSiteAddressAsync()
    {
        var validation = await _mapper.ValidateAsync(
            Ctx(), new[] { Row(address: "г. Уфа, ул. Чернышевского, 88") },
            _dbContext, default);

        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        Assert.Equal(1, apply.AppliedCount);
        _mockCrud.Verify(c => c.UpdateSiteAddressAsync(
            123, "г. Уфа, ул. Чернышевского, 88", It.IsAny<CancellationToken>()), Times.Once);
    }
}
