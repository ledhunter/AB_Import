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

    [SkippableFact]
    public async Task Tabular_HeaderAnchors_DetectShiftedHeaderRow()
    {
        // Регрессия по «Ежевика короткая 1.xlsx»: настоящие имена колонок —
        // в строке 5, выше — подзаголовок «Реестр вывода КВАРТИР» и коэффициенты.
        // Без HeaderAnchors парсер брал бы шапку из строки 1 (пустой), ключи
        // выходили пустыми, и маппер выдавал site_mismatch на каждую строку.
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Квартира");
        ws.Cell(1, 5).Value = "Реестр вывода КВАРТИР";          // подзаголовок
        ws.Cell(2, 5).Value = "Стоимость кв.м/ руб.";           // подсказка
        ws.Cell(3, 5).Value = "Дисконт";                         // подсказка
        // Настоящая шапка — строка 5.
        ws.Cell(5, 1).Value = "ПИН застройщика";
        ws.Cell(5, 2).Value = "Номер разрешения";
        ws.Cell(5, 3).Value = "Номер проекта";
        ws.Cell(5, 4).Value = "Этап";
        ws.Cell(5, 5).Value = "Номер помещения";
        // Строки 6–7 — итоги/служебные.
        ws.Cell(6, 1).Value = "ИТОГО";
        ws.Cell(7, 1).Value = "Сумма с учетом вывода";
        // Данные — с 8-й.
        ws.Cell(8, 1).Value = "UC9NVP";
        ws.Cell(8, 2).Value = "44-27-41-2025";
        ws.Cell(8, 3).Value = "4895";
        ws.Cell(8, 4).Value = "1";
        ws.Cell(8, 5).Value = "1";
        ws.Cell(9, 1).Value = "UC9NVP";
        ws.Cell(9, 2).Value = "44-27-41-2025";
        ws.Cell(9, 3).Value = "4895";
        ws.Cell(9, 4).Value = "1";
        ws.Cell(9, 5).Value = "2";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var layout = new Tabular(HeaderAnchors: new[]
        {
            "ПИН застройщика", "Номер разрешения", "Номер проекта", "Этап",
        });
        var result = await _parser.ParseAsync(ms, layout);

        Assert.Empty(result.Errors);
        Assert.Contains("Номер проекта", result.Headers);
        // Шапка не должна попасть в строки данных. ИТОГО/«Сумма…» — обычные строки
        // с непустой первой ячейкой: парсер их эмитит (фильтрация — забота маппера).
        Assert.True(result.Rows.Count >= 2);
        var dataRow = result.Rows.First(r => r.Cells.TryGetValue("Номер проекта", out var v) && v == "4895");
        Assert.Equal("4895", dataRow.Cells["Номер проекта"]);
        Assert.Equal("1", dataRow.Cells["Этап"]);
        Assert.Equal("44-27-41-2025", dataRow.Cells["Номер разрешения"]);
        Assert.Equal(8, dataRow.SourceRowNumber); // абсолютный Excel-номер
    }

    [SkippableFact]
    public async Task Tabular_HeaderAnchors_StrictSkip_SheetWithoutAnchors()
    {
        // Если анкоры заданы, лист, в котором их нет ≥2 — пропускается ЦЕЛИКОМ
        // (не возвращается ни одной ParsedRow). Так фильтруются «не наши» листы
        // в многолистовых пользовательских файлах: «Общий график», «Итог»,
        // «План» в «Ежевика короткая 1.xlsx».
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var sheetA = wb.Worksheets.Add("Реестр");
        sheetA.Cell(1, 1).Value = "ПИН застройщика";
        sheetA.Cell(1, 2).Value = "Номер проекта";
        sheetA.Cell(2, 1).Value = "PIN";
        sheetA.Cell(2, 2).Value = "4895";
        var sheetB = wb.Worksheets.Add("Общий график");
        sheetB.Cell(1, 1).Value = "Месяц";        // никаких анкоров
        sheetB.Cell(1, 2).Value = "Объём";
        sheetB.Cell(2, 1).Value = "Январь";
        sheetB.Cell(2, 2).Value = "100";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var layout = new Tabular(HeaderAnchors: new[]
        {
            "ПИН застройщика", "Номер проекта",
        });
        var result = await _parser.ParseAsync(ms, layout);
        Assert.Empty(result.Errors);
        Assert.Single(result.Rows);
        Assert.Equal("Реестр", result.Rows[0].Sheet);
        Assert.Equal("4895", result.Rows[0].Cells["Номер проекта"]);
    }

    [SkippableFact]
    public async Task Tabular_SkipsHiddenSheets()
    {
        // Регрессия: пользователь Excel-а может пометить служебный/черновой лист
        // как Hidden (а не удалять). XlsxParser обязан такие листы игнорировать —
        // ParsedRow из них не появляется, ошибок тоже нет (скрытый лист = отсутствующий).
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var visible = wb.Worksheets.Add("Квартиры");
        visible.Cell(1, 1).Value = "Номер";
        visible.Cell(1, 2).Value = "Тип";
        visible.Cell(2, 1).Value = "101";
        visible.Cell(2, 2).Value = "Квартира";
        var hidden = wb.Worksheets.Add("Черновик");
        hidden.Cell(1, 1).Value = "Номер";
        hidden.Cell(1, 2).Value = "Тип";
        hidden.Cell(2, 1).Value = "999";
        hidden.Cell(2, 2).Value = "НЕ_ИМПОРТИРОВАТЬ";
        hidden.Visibility = XLWorksheetVisibility.Hidden;
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms);

        Assert.Empty(result.Errors);
        Assert.Single(result.Rows);
        Assert.Equal("Квартиры", result.Rows[0].Sheet);
        Assert.Equal("101", result.Rows[0].Cells["Номер"]);
        Assert.DoesNotContain(result.Rows, r => r.Sheet == "Черновик");
    }

    [SkippableFact]
    public async Task Tabular_SkipsVeryHiddenSheets()
    {
        // Аналогично Hidden, но VeryHidden — лист, который пользователь даже не
        // увидит в меню «Показать» (его можно сделать видимым только через VBA).
        // Парсер должен трактовать его так же, как Hidden.
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var visible = wb.Worksheets.Add("Квартиры");
        visible.Cell(1, 1).Value = "Номер";
        visible.Cell(2, 1).Value = "101";
        var vh = wb.Worksheets.Add("СовсемСкрытый");
        vh.Cell(1, 1).Value = "Номер";
        vh.Cell(2, 1).Value = "999";
        vh.Visibility = XLWorksheetVisibility.VeryHidden;
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms);
        Assert.Empty(result.Errors);
        Assert.Single(result.Rows);
        Assert.Equal("Квартиры", result.Rows[0].Sheet);
    }

    [SkippableFact]
    public async Task KeyValueVertical_SkipsHiddenInputsSheet()
    {
        // Если целевой лист «Inputs» скрыт — парсер должен сообщить «лист не найден»,
        // а не вернуть данные. В списке доступных листов скрытые не показываем.
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var inputs = wb.Worksheets.Add("Inputs");
        inputs.Cell(5, 3).Value = "Тип отделки";
        inputs.Cell(5, 8).Value = "Черновая";
        inputs.Visibility = XLWorksheetVisibility.Hidden;
        var other = wb.Worksheets.Add("Outputs");
        other.Cell(1, 1).Value = "x";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var result = await _parser.ParseAsync(ms,
            new KeyValueVertical("Inputs", "C", "H"));

        Assert.Empty(result.Rows);
        Assert.NotEmpty(result.Errors);
        var msg = result.Errors[0].Message;
        Assert.Contains("Inputs", msg);    // упомянуто в «не найден»
        Assert.Contains("Outputs", msg);   // видимый — показан
        // Проверяем часть ПОСЛЕ «Доступные листы:»: имя скрытого листа в неё не попадает.
        var marker = "Доступные листы:";
        var idx = msg.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"Не нашли маркер '{marker}' в сообщении: {msg}");
        var availablePart = msg[(idx + marker.Length)..];
        Assert.DoesNotContain("'Inputs'", availablePart);
    }

    [SkippableFact]
    public async Task KeyValueVertical_StageCount_SkipsHiddenControlSheet()
    {
        // Скрытый «Control» = отсутствующий: парсер не должен молча читать
        // число этапов из листа, который пользователь спрятал.
        Skip.IfNot(SkipReason is null, SkipReason);
        using var wb = new XLWorkbook();
        var ctrl = wb.Worksheets.Add("Control");
        ctrl.Cell(4, 6).Value = "Количество этапов";
        ctrl.Cell(4, 7).Value = 3;
        ctrl.Visibility = XLWorksheetVisibility.Hidden;
        var inputs = wb.Worksheets.Add("Inputs");
        inputs.Cell(28, 3).Value = "Тип отделки";
        inputs.Cell(28, 8).Value = "Черновая";
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
    public async Task Tabular_NoAnchors_LegacyBehavior()
    {
        // Если анкоры НЕ заданы — поведение legacy: первая строка = заголовок,
        // лист всегда обрабатывается. Регрессия для существующих мапперов.
        Skip.IfNot(SkipReason is null, SkipReason);
        await using var stream = BuildXlsx("S", new[]
        {
            new[] { "Колонка1", "Колонка2" },
            new[] { "v1", "v2" },
        });
        var result = await _parser.ParseAsync(stream);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "Колонка1", "Колонка2" }, result.Headers);
        Assert.Single(result.Rows);
        Assert.Equal("v1", result.Rows[0].Cells["Колонка1"]);
    }
}
