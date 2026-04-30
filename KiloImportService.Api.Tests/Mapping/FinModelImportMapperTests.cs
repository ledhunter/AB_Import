using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KiloImportService.Api.Tests.Mapping;

public class FinModelImportMapperTests : IDisposable
{
    private readonly FinModelImportMapper _mapper;
    private readonly VisaryDbContext _dbContext;

    public FinModelImportMapperTests()
    {
        _mapper = new FinModelImportMapper(NullLogger<FinModelImportMapper>.Instance);
        
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
    public void TypeCode_Is_finmodel()
    {
        Assert.Equal("finmodel", _mapper.ImportTypeCode);
    }

    [Theory]
    [InlineData("Черновая", 3)]
    [InlineData("Предчистовая", 2)]
    [InlineData("Чистовая", 1)]
    public void GetFinishingMaterialId_ValidValues_ReturnsCorrectId(string title, int expectedId)
    {
        // Arrange
        var row = new ParsedRow(
            SourceRowNumber: 2,
            Sheet: "inputs",
            Cells: new Dictionary<string, string> { ["Тип отделки"] = title }
        );

        // Act
        var result = _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, 123, null),
            new[] { row },
            _dbContext,
            CancellationToken.None
        ).Result;

        // Assert
        Assert.Single(result.Rows);
        Assert.True(result.Rows[0].IsValid);
        var mappedId = result.Rows[0].MappedValues.RootElement.GetProperty("FinishingMaterialId").GetInt32();
        Assert.Equal(expectedId, mappedId);
    }

    [Fact]
    public void ValidateAsync_MissingColumn_ReturnsError()
    {
        // Arrange
        var row = new ParsedRow(
            SourceRowNumber: 2,
            Sheet: "inputs",
            Cells: new Dictionary<string, string> { ["Другая колонка"] = "значение" }
        );

        // Act
        var result = _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, 123, null),
            new[] { row },
            _dbContext,
            CancellationToken.None
        ).Result;

        // Assert
        Assert.Single(result.Rows);
        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors, e => e.ErrorCode == "column_not_found");
    }

    [Fact]
    public void ValidateAsync_EmptyValue_ReturnsError()
    {
        // Arrange
        var row = new ParsedRow(
            SourceRowNumber: 2,
            Sheet: "inputs",
            Cells: new Dictionary<string, string> { ["Тип отделки"] = "" }
        );

        // Act
        var result = _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, 123, null),
            new[] { row },
            _dbContext,
            CancellationToken.None
        ).Result;

        // Assert
        Assert.Single(result.Rows);
        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors, e => e.ErrorCode == "value_empty");
    }

    [Fact]
    public void ValidateAsync_InvalidValue_ReturnsError()
    {
        // Arrange
        var row = new ParsedRow(
            SourceRowNumber: 2,
            Sheet: "inputs",
            Cells: new Dictionary<string, string> { ["Тип отделки"] = "Неизвестная отделка" }
        );

        // Act
        var result = _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, 123, null),
            new[] { row },
            _dbContext,
            CancellationToken.None
        ).Result;

        // Assert
        Assert.Single(result.Rows);
        Assert.False(result.Rows[0].IsValid);
        Assert.Contains(result.Rows[0].Errors, e => e.ErrorCode == "invalid_value");
    }

    [Fact]
    public void ValidateAsync_NoSiteId_ReturnsFileError()
    {
        // Arrange
        var row = new ParsedRow(
            SourceRowNumber: 2,
            Sheet: "inputs",
            Cells: new Dictionary<string, string> { ["Тип отделки"] = "Черновая" }
        );

        // Act
        var result = _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, null, null), // siteId = null
            new[] { row },
            _dbContext,
            CancellationToken.None
        ).Result;

        // Assert
        Assert.Empty(result.Rows);
        Assert.Contains(result.FileLevelErrors, e => e.ErrorCode == "site_required");
    }

    [Theory]
    [InlineData("FinishingType")]
    [InlineData("Finishing")]
    [InlineData("тип отделки")] // case-insensitive
    public void ValidateAsync_ColumnAliases_WorksCorrectly(string columnName)
    {
        // Arrange
        var row = new ParsedRow(
            SourceRowNumber: 2,
            Sheet: "inputs",
            Cells: new Dictionary<string, string> { [columnName] = "Черновая" }
        );

        // Act
        var result = _mapper.ValidateAsync(
            new ImportContext(Guid.NewGuid(), null, 123, null),
            new[] { row },
            _dbContext,
            CancellationToken.None
        ).Result;

        // Assert
        Assert.Single(result.Rows);
        Assert.True(result.Rows[0].IsValid);
    }
}
