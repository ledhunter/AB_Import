using KiloImportService.Api.Data.Visary;
using KiloImportService.Api.Data.Visary.Entities;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Mapping;
using Microsoft.EntityFrameworkCore;

namespace KiloImportService.Api.Tests.Mapping;

/// <summary>
/// Тесты валидации RoomsImportMapper. Используют InMemory EF Core — это
/// значит, что схема "Data" / маппинги колонок не применяются; для целей
/// валидации это нормально: мапперу нужны только DbSet'ы.
/// </summary>
public class RoomsImportMapperTests
{
    private static VisaryDbContext CreateInMemoryDb(
        IEnumerable<RoomKind>? kinds = null,
        IEnumerable<ConstructionSite>? sites = null)
    {
        var opts = new DbContextOptionsBuilder<VisaryDbContext>()
            .UseInMemoryDatabase("visary-" + Guid.NewGuid().ToString("N"))
            .Options;
        var db = new VisaryDbContext(opts);
        if (kinds is not null) db.RoomKinds.AddRange(kinds);
        if (sites is not null) db.ConstructionSites.AddRange(sites);
        db.SaveChanges();
        return db;
    }

    private static ParsedRow Row(int n, params (string Key, string Value)[] cells)
    {
        var dict = cells.ToDictionary(p => p.Key, p => p.Value);
        return new ParsedRow(n, "Sheet1", dict);
    }

    private static ImportContext Ctx(int? siteId = 100) =>
        new(SessionId: Guid.NewGuid(), VisaryProjectId: 1, VisarySiteId: siteId, UserId: "tester");

    private readonly RoomsImportMapper _mapper = new();

    [Fact]
    public void TypeCode_Is_rooms()
    {
        Assert.Equal("rooms", _mapper.ImportTypeCode);
    }

    [Fact]
    public async Task Validate_NoSiteInContext_ReturnsFileLevelError()
    {
        using var db = CreateInMemoryDb();
        var rows = new[] { Row(2, ("Тип", "Квартира"), ("Площадь по проекту", "50")) };

        var result = await _mapper.ValidateAsync(Ctx(siteId: null), rows, db, CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Single(result.FileLevelErrors);
        Assert.Equal("site_required", result.FileLevelErrors[0].ErrorCode);
    }

    [Fact]
    public async Task Validate_SiteNotFound_ReturnsFileLevelError()
    {
        using var db = CreateInMemoryDb();
        var rows = new[] { Row(2, ("Тип", "Квартира"), ("Площадь по проекту", "50")) };

        var result = await _mapper.ValidateAsync(Ctx(siteId: 999), rows, db, CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Single(result.FileLevelErrors);
        Assert.Equal("site_not_found", result.FileLevelErrors[0].ErrorCode);
    }

    [Fact]
    public async Task Validate_HiddenSite_TreatedAsNotFound()
    {
        using var db = CreateInMemoryDb(
            sites: new[] { new ConstructionSite { Id = 100, Title = "X", Hidden = true } });
        var rows = new[] { Row(2, ("Тип", "Квартира"), ("Площадь по проекту", "50")) };

        var result = await _mapper.ValidateAsync(Ctx(), rows, db, CancellationToken.None);
        Assert.Equal("site_not_found", result.FileLevelErrors[0].ErrorCode);
    }

    [Fact]
    public async Task Validate_ValidRow_ProducesValidMappedRow()
    {
        using var db = CreateInMemoryDb(
            kinds: new[] { new RoomKind { Id = 1, Title = "Квартира" } },
            sites: new[] { new ConstructionSite { Id = 100, Title = "Корпус 1" } });

        var rows = new[]
        {
            Row(2,
                ("Номер квартиры", "101"),
                ("Тип", "Квартира"),
                ("Площадь по проекту", "50,5"),
                ("Этаж", "1")),
        };

        var result = await _mapper.ValidateAsync(Ctx(), rows, db, CancellationToken.None);

        Assert.Empty(result.FileLevelErrors);
        var mr = Assert.Single(result.Rows);
        Assert.True(mr.IsValid);
        Assert.Empty(mr.Errors);
        Assert.Equal(2, mr.SourceRowNumber);

        var mapped = mr.MappedValues.RootElement;
        Assert.Equal("101", mapped.GetProperty("Number").GetString());
        Assert.Equal(1, mapped.GetProperty("KindID").GetInt32());
        Assert.Equal(50.5, mapped.GetProperty("ProjectArea").GetDouble());
        Assert.Equal(100, mapped.GetProperty("SiteID").GetInt32());
    }

    [Fact]
    public async Task Validate_MissingKind_ReturnsRequiredMissingError()
    {
        using var db = CreateInMemoryDb(
            sites: new[] { new ConstructionSite { Id = 100, Title = "X" } });

        var rows = new[] { Row(2, ("Площадь по проекту", "50")) };
        var result = await _mapper.ValidateAsync(Ctx(), rows, db, CancellationToken.None);

        var mr = Assert.Single(result.Rows);
        Assert.False(mr.IsValid);
        Assert.Contains(mr.Errors, e => e.ErrorCode == "required_missing");
    }

    [Fact]
    public async Task Validate_UnknownKind_ReturnsFkNotFound()
    {
        using var db = CreateInMemoryDb(
            kinds: new[] { new RoomKind { Id = 1, Title = "Квартира" } },
            sites: new[] { new ConstructionSite { Id = 100, Title = "X" } });

        var rows = new[] { Row(2, ("Тип", "НесуществующийТип"), ("Площадь по проекту", "50")) };
        var result = await _mapper.ValidateAsync(Ctx(), rows, db, CancellationToken.None);

        var mr = Assert.Single(result.Rows);
        Assert.False(mr.IsValid);
        Assert.Contains(mr.Errors, e => e.ErrorCode == "fk_not_found");
    }

    [Fact]
    public async Task Validate_InvalidNumber_ReturnsInvalidNumberError()
    {
        using var db = CreateInMemoryDb(
            kinds: new[] { new RoomKind { Id = 1, Title = "Квартира" } },
            sites: new[] { new ConstructionSite { Id = 100, Title = "X" } });

        var rows = new[]
        {
            Row(2,
                ("Тип", "Квартира"),
                ("Площадь по проекту", "abc")),
        };
        var result = await _mapper.ValidateAsync(Ctx(), rows, db, CancellationToken.None);

        var mr = Assert.Single(result.Rows);
        Assert.False(mr.IsValid);
        Assert.Contains(mr.Errors, e => e.ErrorCode == "invalid_number");
    }

    [Fact]
    public async Task Validate_AcceptsCommaAndDotAsDecimalSeparator()
    {
        using var db = CreateInMemoryDb(
            kinds: new[] { new RoomKind { Id = 1, Title = "Квартира" } },
            sites: new[] { new ConstructionSite { Id = 100, Title = "X" } });

        var rows = new[]
        {
            Row(2, ("Тип", "Квартира"), ("Площадь по проекту", "50,5")),
            Row(3, ("Тип", "Квартира"), ("Площадь по проекту", "50.5")),
        };
        var result = await _mapper.ValidateAsync(Ctx(), rows, db, CancellationToken.None);

        Assert.All(result.Rows, mr => Assert.True(mr.IsValid, string.Join(",", mr.Errors.Select(e => e.Message))));
    }

    [Theory]
    [InlineData("Да", true)]
    [InlineData("да", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("истина", true)]
    [InlineData("Нет", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public async Task Validate_ParsesIsStudio_FromVariousFormats(string raw, bool expected)
    {
        using var db = CreateInMemoryDb(
            kinds: new[] { new RoomKind { Id = 1, Title = "Квартира" } },
            sites: new[] { new ConstructionSite { Id = 100, Title = "X" } });

        var rows = new[]
        {
            Row(2,
                ("Тип", "Квартира"),
                ("Площадь по проекту", "50"),
                ("Студия", raw)),
        };
        var result = await _mapper.ValidateAsync(Ctx(), rows, db, CancellationToken.None);

        var mr = Assert.Single(result.Rows);
        Assert.True(mr.IsValid);
        var actual = mr.MappedValues.RootElement.GetProperty("IsStudio").GetBoolean();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Validate_AliasesAreCaseInsensitive()
    {
        using var db = CreateInMemoryDb(
            kinds: new[] { new RoomKind { Id = 1, Title = "Квартира" } },
            sites: new[] { new ConstructionSite { Id = 100, Title = "X" } });

        // Используем альтернативный регистр `тип` вместо `Тип`.
        var rows = new[]
        {
            Row(2, ("тип", "Квартира"), ("площадь по проекту", "50")),
        };
        var result = await _mapper.ValidateAsync(Ctx(), rows, db, CancellationToken.None);

        var mr = Assert.Single(result.Rows);
        Assert.True(mr.IsValid, string.Join(",", mr.Errors.Select(e => e.Message)));
    }

    [Fact]
    public async Task Validate_RespectsCancellationToken()
    {
        using var db = CreateInMemoryDb(
            kinds: new[] { new RoomKind { Id = 1, Title = "Квартира" } },
            sites: new[] { new ConstructionSite { Id = 100, Title = "X" } });

        var rows = Enumerable.Range(2, 100)
            .Select(i => Row(i, ("Тип", "Квартира"), ("Площадь по проекту", "10")))
            .ToArray();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _mapper.ValidateAsync(Ctx(), rows, db, cts.Token));
    }
}
