using ClosedXML.Excel;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Importing.Parsers;

namespace KiloImportService.Api.Tests.Importing;

/// <summary>
/// Интеграционные тесты XlsxParser. Создают тестовые XLSX через ClosedXML,
/// поэтому требуют доступного scan'а системных шрифтов (SkiaSharp).
///
/// ⚠️ На некоторых Windows-машинах ClosedXML 0.104.x падает при создании XLSX
/// из-за того, что SkiaSharp пытается перечислить <c>C:\Windows\Fonts\*</c>
/// и натыкается на каталог без прав (наблюдалось:
/// <c>Access to the path 'C:\WINDOWS\Fonts\Mysql' is denied</c>).
/// На таких машинах эти тесты — Skip; на CI и большинстве рабочих машин — пройдут.
/// Альтернатива в долгую — мигрировать тесты на DocumentFormat.OpenXml
/// (без font scanning) или обновить ClosedXML.
/// </summary>
public class XlsxParserTests
{
    /// <summary>
    /// Если SkiaSharp/font-scan недоступен — кэшируем причину Skip.
    /// Probe делает то же, что реальный BuildXlsx (несколько колонок) — иначе
    /// проблема может не воспроизвестись на упрощённом примере.
    /// </summary>
    private static readonly Lazy<string?> _skipReason = new(TryProbeClosedXml);

    private static string? SkipReason => _skipReason.Value;

    private static string? TryProbeClosedXml()
    {
        // Полный цикл: Save → Load. Ошибка может возникнуть на любом из этапов
        // (на этой машине именно Load триггерит font-scan SkiaSharp).
        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("probe");
            ws.Cell(1, 1).Value = "Header1";
            ws.Cell(2, 1).Value = "Value1";
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            ms.Position = 0;
            using var loaded = new XLWorkbook(ms);
            var range = loaded.Worksheets.First().RangeUsed();
            _ = range?.RowCount();
            return null;
        }
        catch (Exception ex)
        {
            return $"ClosedXML/SkiaSharp не работает на этой машине ({ex.GetType().Name}): {ex.Message}";
        }
    }

    private readonly XlsxParser _parser = new();

    /// <summary>Создаёт XLSX-файл в памяти для теста.</summary>
    private static Stream BuildXlsx(string sheetName, string[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(sheetName);
        for (int r = 0; r < rows.Length; r++)
        {
            for (int c = 0; c < rows[r].Length; c++)
            {
                ws.Cell(r + 1, c + 1).Value = rows[r][c];
            }
        }
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Format_Is_Xlsx() => Assert.Equal(FileFormat.Xlsx, _parser.Format);

    [SkippableFact]
    public async Task ParsesHeadersAndRows()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        await using var stream = BuildXlsx("Реестр", new[]
        {
            new[] { "Number", "Floor", "Тип" },
            new[] { "101", "1", "Квартира" },
            new[] { "102", "1", "Машиноместо" },
        });

        var result = await _parser.ParseAsync(stream);

        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "Number", "Floor", "Тип" }, result.Headers);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Реестр", result.Rows[0].Sheet);
        Assert.Equal("101", result.Rows[0].Cells["Number"]);
        Assert.Equal("Квартира", result.Rows[0].Cells["Тип"]);
        Assert.Equal(2, result.Rows[0].SourceRowNumber);
    }

    [SkippableFact]
    public async Task SkipsEmptyRowsBetweenData()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        await using var stream = BuildXlsx("Sheet1", new[]
        {
            new[] { "A", "B" },
            new[] { "1", "2" },
            new[] { "", "" },
            new[] { "3", "4" },
        });

        var result = await _parser.ParseAsync(stream);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("3", result.Rows[1].Cells["A"]);
    }

    [SkippableFact]
    public async Task EmptySheet_ReturnsError()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        // ClosedXML удаляет лист с одним cell, но `RangeUsed()` возвращает null
        // для совсем пустого листа.
        using var wb = new XLWorkbook();
        wb.Worksheets.Add("Empty");
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Rows);
    }

    [SkippableFact]
    public async Task TrimsHeaderWhitespace()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        await using var stream = BuildXlsx("S", new[]
        {
            new[] { "  Name  ", "  Age" },
            new[] { "Alice", "30" },
        });
        var result = await _parser.ParseAsync(stream);
        Assert.Equal(new[] { "Name", "Age" }, result.Headers);
        Assert.Equal("Alice", result.Rows[0].Cells["Name"]);
    }

    [SkippableFact]
    public async Task UsesFirstSheet_WhenMultipleSheets()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var ws1 = wb.Worksheets.Add("Первый");
        ws1.Cell(1, 1).Value = "Header1";
        ws1.Cell(2, 1).Value = "FromFirst";
        var ws2 = wb.Worksheets.Add("Второй");
        ws2.Cell(1, 1).Value = "Header2";
        ws2.Cell(2, 1).Value = "FromSecond";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms);
        Assert.Single(result.Rows);
        Assert.Equal("Первый", result.Rows[0].Sheet);
        Assert.Equal("FromFirst", result.Rows[0].Cells["Header1"]);
    }
}
