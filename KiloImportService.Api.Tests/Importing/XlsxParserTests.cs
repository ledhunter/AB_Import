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
    public async Task KeyValueVertical_EmitsRowPerStageColumn()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        // Структура «Inputs»: C — название параметра, H/I/J — этапы.
        // Заполним C28="Тип отделки", C29="Площадь", и три этапа со значениями.
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Inputs");
        ws.Cell(28, 3).Value = "Тип отделки";
        ws.Cell(29, 3).Value = "Площадь";
        ws.Cell(28, 8).Value = "Черновая";   // H
        ws.Cell(29, 8).Value = "100";
        ws.Cell(28, 9).Value = "Чистовая";   // I
        ws.Cell(29, 9).Value = "120";
        ws.Cell(28, 10).Value = "Чистовая";  // J
        ws.Cell(29, 10).Value = "120";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms,
            new KeyValueVertical("Inputs", "C", "H"));

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(new[] { "Тип отделки", "Площадь" }, result.Headers);

        Assert.Equal("Inputs (H)", result.Rows[0].Sheet);
        Assert.Equal("Черновая", result.Rows[0].Cells["Тип отделки"]);
        Assert.Equal("100", result.Rows[0].Cells["Площадь"]);
        Assert.Equal(8, result.Rows[0].SourceRowNumber); // колонка H = 8

        Assert.Equal("Inputs (I)", result.Rows[1].Sheet);
        Assert.Equal("Чистовая", result.Rows[1].Cells["Тип отделки"]);
        Assert.Equal(9, result.Rows[1].SourceRowNumber);
    }

    [SkippableFact]
    public async Task KeyValueVertical_FindsSheetCaseInsensitive()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("INPUTS");
        ws.Cell(5, 3).Value = "Тип отделки";
        ws.Cell(5, 8).Value = "Черновая";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms,
            new KeyValueVertical("inputs", "C", "H"));

        Assert.Empty(result.Errors);
        Assert.Single(result.Rows);
        Assert.Equal("Черновая", result.Rows[0].Cells["Тип отделки"]);
    }

    [SkippableFact]
    public async Task KeyValueVertical_SheetMissing_ReturnsFileError()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        wb.Worksheets.Add("Outputs"); // не Inputs
        var ws = wb.Worksheets.First();
        ws.Cell(1, 1).Value = "x";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms,
            new KeyValueVertical("Inputs", "C", "H"));

        Assert.Empty(result.Rows);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("Inputs", result.Errors[0].Message);
        Assert.Contains("Outputs", result.Errors[0].Message); // показывает доступные
    }

    [SkippableFact]
    public async Task KeyValueVertical_StageCount_LimitsValueColumnsRead()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        // Control: F=параметр, G=значение. «Количество этапов»=1 → читаем только H,
        // даже если I и J заполнены «про запас».
        using var wb = new XLWorkbook();
        var ctrl = wb.Worksheets.Add("Control");
        ctrl.Cell(3, 6).Value = "Какой-то другой параметр"; // F3
        ctrl.Cell(4, 6).Value = "Количество этапов";        // F4
        ctrl.Cell(4, 7).Value = 1;                           // G4
        var ws = wb.Worksheets.Add("Inputs");
        ws.Cell(28, 3).Value = "Тип отделки";
        ws.Cell(28, 8).Value = "Черновая";  // H — единственный валидный этап
        ws.Cell(28, 9).Value = "Чистовая";  // I — должна быть проигнорирована
        ws.Cell(28, 10).Value = "Чистовая"; // J — должна быть проигнорирована
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms,
            new KeyValueVertical("Inputs", "C", "H",
                StageCount: new StageCountReference("Control", "F", "G", "Количество этапов")));

        Assert.Empty(result.Errors);
        Assert.Single(result.Rows); // ровно 1 этап
        Assert.Equal("Inputs (H)", result.Rows[0].Sheet);
        Assert.Equal("Черновая", result.Rows[0].Cells["Тип отделки"]);
    }

    [SkippableFact]
    public async Task KeyValueVertical_StageCount_ThreeStages_ReadsHIJ()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var ctrl = wb.Worksheets.Add("Control");
        ctrl.Cell(4, 6).Value = "Количество этапов";
        ctrl.Cell(4, 7).Value = 3;
        var ws = wb.Worksheets.Add("Inputs");
        ws.Cell(28, 3).Value = "Тип отделки";
        ws.Cell(28, 8).Value = "Черновая";   // H
        ws.Cell(28, 9).Value = "Предчистовая"; // I
        ws.Cell(28, 10).Value = "Чистовая";  // J
        ws.Cell(28, 11).Value = "За пределами"; // K — за границей этапов, должна быть проигнорирована
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms,
            new KeyValueVertical("Inputs", "C", "H",
                StageCount: new StageCountReference("Control", "F", "G", "Количество этапов")));

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("Inputs (H)", result.Rows[0].Sheet);
        Assert.Equal("Inputs (I)", result.Rows[1].Sheet);
        Assert.Equal("Inputs (J)", result.Rows[2].Sheet);
    }

    [SkippableFact]
    public async Task KeyValueVertical_StageCount_MissingControlSheet_ReturnsFileError()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Inputs");
        ws.Cell(28, 3).Value = "Тип отделки";
        ws.Cell(28, 8).Value = "Черновая";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms,
            new KeyValueVertical("Inputs", "C", "H",
                StageCount: new StageCountReference("Control", "F", "G", "Количество этапов")));

        Assert.Empty(result.Rows);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("Control", result.Errors[0].Message);
    }

    [SkippableFact]
    public async Task KeyValueVertical_StageCount_EmitsRowEvenWhenStageEmpty()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        // 2 этапа, но колонка I (этап 2) пустая — всё равно эмитим ParsedRow,
        // чтобы маппер показал value_empty конкретно для этапа 2.
        using var wb = new XLWorkbook();
        var ctrl = wb.Worksheets.Add("Control");
        ctrl.Cell(4, 6).Value = "Количество этапов";
        ctrl.Cell(4, 7).Value = 2;
        var ws = wb.Worksheets.Add("Inputs");
        ws.Cell(28, 3).Value = "Тип отделки";
        ws.Cell(28, 8).Value = "Черновая"; // H
        // I (col 9) — пусто
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms,
            new KeyValueVertical("Inputs", "C", "H",
                StageCount: new StageCountReference("Control", "F", "G", "Количество этапов")));

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Черновая", result.Rows[0].Cells["Тип отделки"]);
        Assert.Equal(string.Empty, result.Rows[1].Cells["Тип отделки"]);
    }

    [SkippableFact]
    public async Task KeyValueVertical_SkipsEmptyStageColumns()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Inputs");
        ws.Cell(28, 3).Value = "Тип отделки";
        ws.Cell(28, 8).Value = "Черновая";   // H
        // I (9) — пусто, должна быть пропущена
        ws.Cell(28, 10).Value = "Чистовая";  // J
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms,
            new KeyValueVertical("Inputs", "C", "H"));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Inputs (H)", result.Rows[0].Sheet);
        Assert.Equal("Inputs (J)", result.Rows[1].Sheet);
    }

    [SkippableFact]
    public async Task ReadsAllSheets_WhenMultipleSheets()
    {
        // Регрессия: раньше парсер читал только первый лист, и многолистовые
        // шаблоны (например, «Пример импорта.xlsx» с листами «Квартиры»/«Машиноместа»)
        // импортировались только частично. Теперь обходим все листы; маппер
        // фильтрует служебные («Справочник») сам.
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var ws1 = wb.Worksheets.Add("Квартиры");
        ws1.Cell(1, 1).Value = "Номер";
        ws1.Cell(1, 2).Value = "Тип";
        ws1.Cell(2, 1).Value = "101";
        ws1.Cell(2, 2).Value = "Квартира";
        var ws2 = wb.Worksheets.Add("Машиноместа");
        ws2.Cell(1, 1).Value = "Номер";
        ws2.Cell(1, 2).Value = "Тип";
        ws2.Cell(2, 1).Value = "M-1";
        ws2.Cell(2, 2).Value = "Машиноместо";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Квартиры", result.Rows[0].Sheet);
        Assert.Equal("Квартира", result.Rows[0].Cells["Тип"]);
        Assert.Equal("Машиноместа", result.Rows[1].Sheet);
        Assert.Equal("Машиноместо", result.Rows[1].Cells["Тип"]);
        Assert.Equal(new[] { "Номер", "Тип" }, result.Headers);
    }

    [SkippableFact]
    public async Task ReadsSecondSheet_WhenFirstIsEmpty()
    {
        // Пустой первый лист (например, «Справочник») не должен блокировать
        // импорт из последующих листов.
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        wb.Worksheets.Add("Справочник"); // пустой
        var ws = wb.Worksheets.Add("Квартиры");
        ws.Cell(1, 1).Value = "Номер";
        ws.Cell(2, 1).Value = "101";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms);

        Assert.Empty(result.Errors);
        Assert.Single(result.Rows);
        Assert.Equal("Квартиры", result.Rows[0].Sheet);
    }

    [SkippableFact]
    public async Task MultipleSheets_DifferentHeaders_UnionInResultHeaders()
    {
        // Колонки у листов разные («Квартиры» имеет «Колич. комнат», «Машиноместа» — нет).
        // В Result.Headers должен попасть объединённый набор, у каждой ParsedRow.Cells —
        // только те ключи, которые есть в её листе.
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var ws1 = wb.Worksheets.Add("Квартиры");
        ws1.Cell(1, 1).Value = "Номер";
        ws1.Cell(1, 2).Value = "Колич. комнат";
        ws1.Cell(2, 1).Value = "101";
        ws1.Cell(2, 2).Value = "2";
        var ws2 = wb.Worksheets.Add("Машиноместа");
        ws2.Cell(1, 1).Value = "Номер";
        ws2.Cell(1, 2).Value = "Этаж";
        ws2.Cell(2, 1).Value = "M-1";
        ws2.Cell(2, 2).Value = "-1";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new[] { "Номер", "Колич. комнат", "Этаж" }, result.Headers);
        Assert.True(result.Rows[0].Cells.ContainsKey("Колич. комнат"));
        Assert.False(result.Rows[0].Cells.ContainsKey("Этаж")); // листа Машиноместа колонок нет в строке Квартир
        Assert.True(result.Rows[1].Cells.ContainsKey("Этаж"));
        Assert.False(result.Rows[1].Cells.ContainsKey("Колич. комнат"));
    }
}
