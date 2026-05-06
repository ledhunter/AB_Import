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

    public FinModelImportMapperTests()
    {
        var mockCrudClient = new Mock<ICrudClient>();
        mockCrudClient.Setup(c => c.UpdateSiteFinishingMaterialAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Возвращаем тот же набор, что показал бы боевой Visary listview/finishingmaterial.
        var mockListViewClient = new Mock<IListViewClient>();
        mockListViewClient
            .Setup(c => c.GetFinishingMaterialsAsync(It.IsAny<CancellationToken>()))
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

        _mapper = new FinModelImportMapper(
            NullLogger<FinModelImportMapper>.Instance,
            mockCrudClient.Object,
            mockListViewClient.Object
        );
        
        // Создаём in-memory БД для тестов
        var options = new DbContextOptionsBuilder<VisaryDbContext>()
            .UseInMemoryDatabase($"FinModelTest_{Guid.NewGuid()}")
            .Options;
        _dbContext = new VisaryDbContext(options);
        
        // Добавляем тестовый объект строительства
        _dbContext.ConstructionSites.Add(new ConstructionSite
        {
            Id = 123,
            Title = "Тестовый объект",
            Hidden = false
        });
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
    }

    [Fact]
    public async Task TypeCode_Is_finmodel()
    {
        Assert.Equal("finmodel", _mapper.ImportTypeCode);
    }

    [Theory]
    [InlineData("Черновая", 3)]
    [InlineData("Предчистовая", 2)]
    [InlineData("Чистовая", 1)]
    public async Task ValidateAsync_ValidValues_ReturnsCorrectId(string title, int expectedId)
    {
        // Arrange
        var row = new ParsedRow(
            SourceRowNumber: 2,
            Sheet: "inputs",
            Cells: new Dictionary<string, string> { ["Тип отделки"] = title }
        );

        // Act
        var result = await _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, 123, null),
            new[] { row },
            _dbContext,
            CancellationToken.None
        );

        // Assert
        Assert.Single(result.Rows);
        Assert.True(result.Rows[0].IsValid);
        var mappedId = result.Rows[0].MappedValues.RootElement.GetProperty("FinishingMaterialId").GetInt32();
        Assert.Equal(expectedId, mappedId);
    }

    [Fact]
    public async Task ValidateAsync_MissingColumn_ReturnsFileLevelErrorWithDetectedColumns()
    {
        // Arrange — файл без целевой колонки, имитируем неверный шаблон.
        var rows = new[]
        {
            new ParsedRow(
                SourceRowNumber: 2,
                Sheet: "inputs",
                Cells: new Dictionary<string, string>
                {
                    ["Другая колонка"] = "значение",
                    ["Ещё одна"] = "x",
                }),
            new ParsedRow(
                SourceRowNumber: 3,
                Sheet: "inputs",
                Cells: new Dictionary<string, string> { ["Другая колонка"] = "значение2" }),
        };

        // Act
        var result = await _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, 123, null),
            rows,
            _dbContext,
            CancellationToken.None
        );

        // Assert — ровно одна file-level ошибка, без row-spam.
        Assert.Empty(result.Rows);
        Assert.Single(result.FileLevelErrors);
        var err = result.FileLevelErrors[0];
        Assert.Equal("column_not_found", err.ErrorCode);
        Assert.Contains("Другая колонка", err.Message);
        Assert.Contains("Финмодель", err.Message);
    }

    [Fact]
    public async Task ValidateAsync_EmptyValue_ReturnsError()
    {
        // Arrange
        var row = new ParsedRow(
            SourceRowNumber: 2,
            Sheet: "inputs",
            Cells: new Dictionary<string, string> { ["Тип отделки"] = "" }
        );

        // Act
        var result = await _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, 123, null),
            new[] { row },
            _dbContext,
            CancellationToken.None
        );

        // Assert
        Assert.Single(result.Rows);
        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors, e => e.ErrorCode == "value_empty");
    }

    [Fact]
    public async Task ValidateAsync_InvalidValue_ReturnsError()
    {
        // Arrange
        var row = new ParsedRow(
            SourceRowNumber: 2,
            Sheet: "inputs",
            Cells: new Dictionary<string, string> { ["Тип отделки"] = "Неизвестная отделка" }
        );

        // Act
        var result = await _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, 123, null),
            new[] { row },
            _dbContext,
            CancellationToken.None
        );

        // Assert
        Assert.Single(result.Rows);
        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors, e => e.ErrorCode == "invalid_value");
    }

    [Fact]
    public async Task ValidateAsync_NoSiteId_ReturnsFileError()
    {
        // Arrange
        var row = new ParsedRow(
            SourceRowNumber: 2,
            Sheet: "inputs",
            Cells: new Dictionary<string, string> { ["Тип отделки"] = "Черновая" }
        );

        // Act
        var result = await _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, null, null), // siteId = null
            new[] { row },
            _dbContext,
            CancellationToken.None
        );

        // Assert
        Assert.Empty(result.Rows);
        Assert.Contains(result.FileLevelErrors, e => e.ErrorCode == "site_required");
    }

    [Theory]
    [InlineData("FinishingType")]
    [InlineData("Finishing")]
    [InlineData("тип отделки")] // case-insensitive
    public async Task ValidateAsync_ColumnAliases_WorksCorrectly(string columnName)
    {
        // Arrange
        var row = new ParsedRow(
            SourceRowNumber: 2,
            Sheet: "inputs",
            Cells: new Dictionary<string, string> { [columnName] = "Черновая" }
        );

        // Act
        var result = await _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, 123, null),
            new[] { row },
            _dbContext,
            CancellationToken.None
        );

        // Assert
        Assert.Single(result.Rows);
        Assert.True(result.Rows[0].IsValid);
    }
}
