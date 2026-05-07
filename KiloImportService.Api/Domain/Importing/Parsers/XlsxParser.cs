using ClosedXML.Excel;

namespace KiloImportService.Api.Domain.Importing.Parsers;

/// <summary>
/// Парсер XLSX через ClosedXML.
/// Поддерживает две раскладки:
///   • <see cref="Tabular"/> — первый лист, первая строка как заголовки (по умолчанию).
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
        var headers = new List<string>();
        var rows = new List<ParsedRow>();
        var errors = new List<ParseError>();

        var sheet = workbook.Worksheets.FirstOrDefault();
        if (sheet is null)
        {
            errors.Add(new ParseError(null, "Файл не содержит ни одного листа."));
            return new ParseResult(headers, rows, errors);
        }

        var range = sheet.RangeUsed();
        if (range is null)
        {
            errors.Add(new ParseError(null, "Лист пустой — нет данных для импорта."));
            return new ParseResult(headers, rows, errors);
        }

        var sheetName = sheet.Name ?? string.Empty;

        var firstRow = range.FirstRow();
        foreach (var cell in firstRow.Cells())
        {
            headers.Add(cell.GetString().Trim());
        }
        if (headers.Count == 0)
        {
            errors.Add(new ParseError(1, "Не удалось прочитать заголовки колонок (первая строка пустая)."));
            return new ParseResult(headers, rows, errors);
        }

        var totalRows = range.RowCount();
        for (int rowIndex = 2; rowIndex <= totalRows; rowIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var row = range.Row(rowIndex);
            var cells = new Dictionary<string, string>(headers.Count, StringComparer.Ordinal);
            bool isEmpty = true;
            for (int c = 0; c < headers.Count; c++)
            {
                var cell = row.Cell(c + 1);
                var value = cell.GetString();
                if (!string.IsNullOrWhiteSpace(value)) isEmpty = false;
                cells[headers[c]] = value ?? string.Empty;
            }
            if (isEmpty) continue; // пропускаем полностью пустые строки
            rows.Add(new ParsedRow(rowIndex, sheetName, cells));
        }

        return new ParseResult(headers, rows, errors);
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

        return new ParseResult(headers, rows, errors);
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
