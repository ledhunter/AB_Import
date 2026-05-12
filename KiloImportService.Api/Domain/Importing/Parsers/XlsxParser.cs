using ClosedXML.Excel;

namespace KiloImportService.Api.Domain.Importing.Parsers;

/// <summary>
/// Парсер XLSX через ClosedXML.
/// Поддерживает две раскладки:
///   • <see cref="Tabular"/> — обходит ВСЕ листы файла, у каждого читает свои
///     заголовки (первая строка) и эмитит <see cref="ParsedRow"/> с именем листа
///     в поле <c>Sheet</c>. Маппер сам решает, какие листы фильтровать
///     (например, «Справочник»). Пустые листы пропускаются без ошибки.
///   • <see cref="KeyValueVertical"/> — лист по имени, параметры в столбце-ключе,
///     значения в одной или нескольких колонках-этапах справа.
/// </summary>
public sealed class XlsxParser : IFileParser
{
    public FileFormat Format => FileFormat.Xlsx;

    public Task<ParseResult> ParseAsync(Stream stream, FileLayoutHint? layout = null, CancellationToken ct = default)
    {
        layout ??= FileLayoutHint.Default;

        try
        {
            using var workbook = new XLWorkbook(stream);
            return Task.FromResult(layout switch
            {
                KeyValueVertical kv => ParseKeyValueVertical(workbook, kv, ct),
                _ => ParseTabular(workbook, ct),
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ParseResult(
                [], [], [new ParseError(null, $"Не удалось прочитать XLSX: {ex.Message}")]));
        }
    }

    private static ParseResult ParseTabular(XLWorkbook workbook, CancellationToken ct)
    {
        // Объединённые заголовки по всем листам (для совместимости с UI/маппером —
        // у каждого листа может быть свой набор колонок, например в файле помещений
        // лист «Квартиры» имеет «Колич. комнат», а «Машиноместа» — нет).
        var allHeaders = new List<string>();
        var rows = new List<ParsedRow>();
        var errors = new List<ParseError>();

        var sheets = workbook.Worksheets.ToList();
        if (sheets.Count == 0)
        {
            errors.Add(new ParseError(null, "Файл не содержит ни одного листа."));
            return new ParseResult(allHeaders, rows, errors);
        }

        int processedSheets = 0;
        foreach (var sheet in sheets)
        {
            ct.ThrowIfCancellationRequested();
            var sheetName = sheet.Name ?? string.Empty;

            var range = sheet.RangeUsed();
            if (range is null)
            {
                // Пустой лист — например, шаблонный «Справочник» оставленный без данных.
                // Не считаем ошибкой: в файле могут быть другие листы с данными.
                continue;
            }

            // Заголовки конкретного листа — для корректного маппинга его строк.
            var sheetHeaders = new List<string>();
            var firstRow = range.FirstRow();
            foreach (var cell in firstRow.Cells())
            {
                sheetHeaders.Add(cell.GetString().Trim());
            }
            if (sheetHeaders.Count == 0)
            {
                continue;
            }

            // Накопительный union заголовков (без дубликатов, case-insensitive).
            foreach (var h in sheetHeaders)
            {
                if (!allHeaders.Contains(h, StringComparer.OrdinalIgnoreCase))
                    allHeaders.Add(h);
            }

            var totalRows = range.RowCount();
            bool anyDataInSheet = false;
            for (int rowIndex = 2; rowIndex <= totalRows; rowIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var row = range.Row(rowIndex);
                var cells = new Dictionary<string, string>(sheetHeaders.Count, StringComparer.Ordinal);
                bool isEmpty = true;
                for (int c = 0; c < sheetHeaders.Count; c++)
                {
                    var cell = row.Cell(c + 1);
                    var value = cell.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) isEmpty = false;
                    cells[sheetHeaders[c]] = value ?? string.Empty;
                }
                if (isEmpty) continue; // пропускаем полностью пустые строки

                // ⚠️ SourceRowNumber — индекс строки В ПРЕДЕЛАХ листа (как в Excel).
                // Между листами возможны коллизии (строка 5 встречается в каждом
                // листе), но маппер всегда логирует Sheet рядом с SourceRowNumber,
                // так что неоднозначности в логах нет.
                rows.Add(new ParsedRow(rowIndex, sheetName, cells));
                anyDataInSheet = true;
            }

            if (anyDataInSheet) processedSheets++;
        }

        if (processedSheets == 0)
        {
            errors.Add(new ParseError(null,
                "В файле нет ни одного листа с данными для импорта."));
        }

        return new ParseResult(allHeaders, rows, errors);
    }

    private static ParseResult ParseKeyValueVertical(XLWorkbook workbook, KeyValueVertical layout, CancellationToken ct)
    {
        var headers = new List<string>();
        var rows = new List<ParsedRow>();
        var errors = new List<ParseError>();

        // Лист ищем по имени case-insensitive — Excel допускает любой регистр.
        var sheet = workbook.Worksheets.FirstOrDefault(w =>
            string.Equals(w.Name, layout.SheetName, StringComparison.OrdinalIgnoreCase));
        if (sheet is null)
        {
            var available = string.Join(", ", workbook.Worksheets.Select(w => $"'{w.Name}'"));
            errors.Add(new ParseError(null,
                $"Лист '{layout.SheetName}' не найден. Доступные листы: {available}."));
            return new ParseResult(headers, rows, errors);
        }

        var sheetName = sheet.Name ?? string.Empty;

        var range = sheet.RangeUsed();
        if (range is null)
        {
            errors.Add(new ParseError(null, $"Лист '{sheetName}' пустой — нет данных для импорта."));
            return new ParseResult(headers, rows, errors);
        }

        // Преобразуем буквенные имена колонок в 1-based индексы.
        if (!TryParseColumnLetter(layout.KeyColumn, out var keyCol))
        {
            errors.Add(new ParseError(null, $"Некорректное имя колонки-ключа: '{layout.KeyColumn}'."));
            return new ParseResult(headers, rows, errors);
        }
        if (!TryParseColumnLetter(layout.ValueStartColumn, out var valueStartCol))
        {
            errors.Add(new ParseError(null, $"Некорректное имя колонки-значения: '{layout.ValueStartColumn}'."));
            return new ParseResult(headers, rows, errors);
        }

        var lastRow = range.LastRow().RowNumber();
        var lastCol = range.LastColumn().ColumnNumber();

        // Собираем карту {row → key_text} по непустым значениям в колонке-ключе.
        // Это «вертикальные заголовки»: они станут ключами в Cells каждого ParsedRow.
        var keyByRow = new List<(int Row, string Key)>();
        for (int r = 1; r <= lastRow; r++)
        {
            ct.ThrowIfCancellationRequested();
            var key = sheet.Cell(r, keyCol).GetString().Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;
            keyByRow.Add((r, key));
            if (!headers.Contains(key, StringComparer.OrdinalIgnoreCase))
                headers.Add(key);
        }

        if (keyByRow.Count == 0)
        {
            errors.Add(new ParseError(null,
                $"В колонке-ключе '{layout.KeyColumn}' листа '{sheetName}' не найдено ни одного названия параметра."));
            return new ParseResult(headers, rows, errors);
        }

        if (valueStartCol > lastCol)
        {
            errors.Add(new ParseError(null,
                $"Колонки со значениями (от '{layout.ValueStartColumn}') отсутствуют в листе '{sheetName}'."));
            return new ParseResult(headers, rows, errors);
        }

        // Если задан StageCount — читаем число этапов из управляющей ячейки.
        // Это ограничивает диапазон колонок-значений: H, I, …, H+N-1, не дальше.
        // Без ограничения парсер пройдёт до конца использованного диапазона и
        // насобирает мусор из колонок, которые шаблон оставил пустыми «про запас».
        int? maxStages = null;
        if (layout.StageCount is { } sc)
        {
            var stagesResult = ReadStageCount(workbook, sc);
            if (stagesResult.Error is not null)
            {
                errors.Add(stagesResult.Error);
                return new ParseResult(headers, rows, errors);
            }
            maxStages = stagesResult.Count;
        }

        // Определяем, до какой колонки идти.
        // С maxStages — ровно N колонок (даже если правее есть данные «про запас»).
        // Без maxStages — до lastCol включительно.
        int stopCol = maxStages.HasValue
            ? Math.Min(lastCol, valueStartCol + maxStages.Value - 1)
            : lastCol;

        for (int c = valueStartCol; c <= stopCol; c++)
        {
            ct.ThrowIfCancellationRequested();
            var cells = new Dictionary<string, string>(keyByRow.Count, StringComparer.Ordinal);
            bool anyValue = false;
            foreach (var (rowNum, key) in keyByRow)
            {
                var value = sheet.Cell(rowNum, c).GetString();
                if (!string.IsNullOrWhiteSpace(value)) anyValue = true;
                cells[key] = value ?? string.Empty;
            }
            // Если число этапов задано явно — выпускаем ParsedRow всегда (даже на пустую
            // колонку), чтобы маппер показал value_empty для конкретного этапа.
            // Без явного N — пропускаем пустые, иначе насобираем «фантомных» строк.
            if (!maxStages.HasValue && !anyValue) continue;

            var letter = ColumnLetter(c);
            // SourceRowNumber = индекс колонки-значения (для трассировки в отчёте).
            // Sheet несёт буквенный идентификатор колонки, чтобы пользователь видел,
            // из какого этапа взяты значения.
            rows.Add(new ParsedRow(c, $"{sheetName} ({letter})", cells));
        }

        if (rows.Count == 0)
        {
            errors.Add(new ParseError(null,
                $"В листе '{sheetName}' нет ни одной заполненной колонки-значения от '{layout.ValueStartColumn}' и правее."));
        }

        // ─── Бюджетная секция (опционально) ──────────────────────────────────
        // Если в layout задан BudgetSectionHint — пройдёмся по тому же листу
        // ниже маркера StartMarker и эмитим строки с буквенными ключами в Cells.
        // Если ничего не нашли — добавляем file-level error (StartMarker не найден),
        // но главный поток валидации (стадии) уже отработал.
        if (layout.Budget is { } budget)
        {
            ExtractBudgetSection(sheet, sheetName, budget, rows, errors, ct);
        }

        return new ParseResult(headers, rows, errors);
    }

    /// <summary>
    /// Сканирует лист от строки-маркера <see cref="BudgetSectionHint.StartMarker"/>
    /// до первой строки с любым из <see cref="BudgetSectionHint.EndMarkers"/> и
    /// добавляет в <paramref name="rows"/> по одной <see cref="ParsedRow"/> на каждую
    /// непустую строку (ключи ячеек — буквы колонок, до <see cref="BudgetSectionHint.LastIncludedColumn"/>
    /// включительно). Маркер начала и конца не входят в эмитируемый набор.
    /// </summary>
    private static void ExtractBudgetSection(
        IXLWorksheet sheet, string sheetName, BudgetSectionHint hint,
        List<ParsedRow> rows, List<ParseError> errors, CancellationToken ct)
    {
        if (!TryParseColumnLetter(hint.MarkerColumn, out var markerCol))
        {
            errors.Add(new ParseError(null, $"BudgetSectionHint: некорректная колонка-маркер '{hint.MarkerColumn}'."));
            return;
        }
        if (!TryParseColumnLetter(hint.LastIncludedColumn, out var lastCol))
        {
            errors.Add(new ParseError(null, $"BudgetSectionHint: некорректная LastIncludedColumn '{hint.LastIncludedColumn}'."));
            return;
        }

        var range = sheet.RangeUsed();
        if (range is null) return;
        var lastRow = range.LastRow().RowNumber();

        // Ищем строку StartMarker (case-insensitive substring) в колонке MarkerColumn.
        int? startRow = null;
        for (int r = 1; r <= lastRow; r++)
        {
            ct.ThrowIfCancellationRequested();
            var text = sheet.Cell(r, markerCol).GetString();
            if (!string.IsNullOrWhiteSpace(text)
                && text.Contains(hint.StartMarker, StringComparison.OrdinalIgnoreCase))
            {
                startRow = r;
                break;
            }
        }
        if (startRow is null)
        {
            errors.Add(new ParseError(null,
                $"BudgetSectionHint: маркер начала '{hint.StartMarker}' не найден " +
                $"в колонке '{hint.MarkerColumn}' листа '{sheetName}'."));
            return;
        }

        var budgetSheetTag = $"{sheetName} {hint.SheetMarker}";
        for (int r = startRow.Value + 1; r <= lastRow; r++)
        {
            ct.ThrowIfCancellationRequested();

            var marker = sheet.Cell(r, markerCol).GetString();
            // Стоп: первый маркер из EndMarkers (любая часть текста ячейки).
            if (!string.IsNullOrWhiteSpace(marker) && hint.EndMarkers.Any(m =>
                    marker.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }

            // Собираем все ячейки от A до LastIncludedColumn — ключи Cells = буквы.
            var cells = new Dictionary<string, string>(lastCol, StringComparer.Ordinal);
            bool any = false;
            for (int c = 1; c <= lastCol; c++)
            {
                var v = sheet.Cell(r, c).GetString();
                if (!string.IsNullOrWhiteSpace(v)) any = true;
                cells[ColumnLetter(c)] = v ?? string.Empty;
            }
            if (!any) continue;
            rows.Add(new ParsedRow(r, budgetSheetTag, cells));
        }
    }

    /// <summary>
    /// Возвращает количество этапов (>= 1), извлечённое из управляющей ячейки,
    /// либо <see cref="ParseError"/>, если что-то пошло не так. Маппер не должен видеть
    /// «спокойный fallback» — недоступная управляющая ячейка для шаблона с N этапами
    /// означает, что мы не знаем, какие колонки читать, и продолжать нельзя.
    /// </summary>
    private static (int Count, ParseError? Error) ReadStageCount(XLWorkbook workbook, StageCountReference sc)
    {
        var ctrl = workbook.Worksheets.FirstOrDefault(w =>
            string.Equals(w.Name, sc.SheetName, StringComparison.OrdinalIgnoreCase));
        if (ctrl is null)
        {
            var available = string.Join(", ", workbook.Worksheets.Select(w => $"'{w.Name}'"));
            return (0, new ParseError(null,
                $"Лист '{sc.SheetName}' не найден (нужен для определения количества этапов). Доступные листы: {available}."));
        }
        if (!TryParseColumnLetter(sc.KeyColumn, out var keyCol))
            return (0, new ParseError(null, $"Некорректное имя колонки-ключа управляющей ссылки: '{sc.KeyColumn}'."));
        if (!TryParseColumnLetter(sc.ValueColumn, out var valCol))
            return (0, new ParseError(null, $"Некорректное имя колонки-значения управляющей ссылки: '{sc.ValueColumn}'."));

        var range = ctrl.RangeUsed();
        if (range is null)
            return (0, new ParseError(null, $"Лист '{sc.SheetName}' пуст — невозможно определить количество этапов."));

        var lastRow = range.LastRow().RowNumber();
        for (int r = 1; r <= lastRow; r++)
        {
            var name = ctrl.Cell(r, keyCol).GetString().Trim();
            if (!name.Equals(sc.ParameterName, StringComparison.OrdinalIgnoreCase)) continue;
            var raw = ctrl.Cell(r, valCol).GetString().Trim();
            if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
                && !int.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out n))
            {
                return (0, new ParseError(null,
                    $"Не удалось распарсить '{sc.ParameterName}' на листе '{sc.SheetName}' (строка {r}, колонка '{sc.ValueColumn}'): значение '{raw}'."));
            }
            if (n <= 0)
            {
                return (0, new ParseError(null,
                    $"Количество этапов на листе '{sc.SheetName}' должно быть положительным, получено: {n}."));
            }
            return (n, null);
        }
        return (0, new ParseError(null,
            $"Не найден параметр '{sc.ParameterName}' в колонке '{sc.KeyColumn}' листа '{sc.SheetName}'."));
    }

    /// <summary>Парсит "C" → 3, "AA" → 27 (1-based, как в Excel).</summary>
    private static bool TryParseColumnLetter(string letter, out int index)
    {
        index = 0;
        if (string.IsNullOrWhiteSpace(letter)) return false;
        var trimmed = letter.Trim().ToUpperInvariant();
        foreach (var ch in trimmed)
        {
            if (ch < 'A' || ch > 'Z') { index = 0; return false; }
            index = index * 26 + (ch - 'A' + 1);
        }
        return index > 0;
    }

    /// <summary>Обратное преобразование: 3 → "C", 27 → "AA".</summary>
    private static string ColumnLetter(int index)
    {
        var chars = new Stack<char>();
        while (index > 0)
        {
            int rem = (index - 1) % 26;
            chars.Push((char)('A' + rem));
            index = (index - 1) / 26;
        }
        return new string(chars.ToArray());
    }
}
