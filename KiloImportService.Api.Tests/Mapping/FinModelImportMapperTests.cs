using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Mapping;
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

        _mapper = new FinModelImportMapper(
            NullLogger<FinModelImportMapper>.Instance,
            _mockCrud.Object,
            _mockListView.Object);

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

    private static ParsedRow Row(string finishing = "Черновая", string estate = "Комфорт")
        => new(SourceRowNumber: 2, Sheet: "inputs",
            Cells: new Dictionary<string, string>
            {
                ["Тип отделки"] = finishing,
                ["Класс жилья"] = estate,
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
        var rows = new[]
        {
            new ParsedRow(2, "inputs", new Dictionary<string, string> { ["Тип отделки"] = "Черновая" }),
        };

        var result = await _mapper.ValidateAsync(Ctx(), rows, _dbContext, default);

        Assert.Empty(result.Rows);
        Assert.Single(result.FileLevelErrors);
        Assert.Equal("column_not_found", result.FileLevelErrors[0].ErrorCode);
        Assert.Contains("Класс жилья", result.FileLevelErrors[0].Message);
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
        Assert.Single(result.FileLevelErrors);
        Assert.Equal("column_not_found", result.FileLevelErrors[0].ErrorCode);
        Assert.Contains("Тип отделки", result.FileLevelErrors[0].Message);
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
            new Dictionary<string, string> { [colName] = "Черновая", ["Класс жилья"] = "Комфорт" });

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
            new Dictionary<string, string> { ["Тип отделки"] = "Черновая", [colName] = "Комфорт" });

        var result = await _mapper.ValidateAsync(Ctx(), new[] { row }, _dbContext, default);

        Assert.True(result.Rows[0].IsValid);
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
    public async Task ApplyAsync_ValidRow_CallsBothUpdates()
    {
        var validation = await _mapper.ValidateAsync(
            Ctx(), new[] { Row("Черновая", "Премиум") }, _dbContext, default);

        var apply = await _mapper.ApplyAsync(Ctx(), _dbContext, validation.Rows, default);

        Assert.Equal(1, apply.AppliedCount);
        _mockCrud.Verify(c => c.UpdateSiteFinishingMaterialAsync(123, 3, It.IsAny<CancellationToken>()), Times.Once);
        _mockCrud.Verify(c => c.UpdateSiteEstateClassAsync(123, 12, It.IsAny<CancellationToken>()), Times.Once);
    }
}
