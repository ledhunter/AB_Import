using System.IO.Compression;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger<XlsxParser> _log;

    public XlsxParser(ILogger<XlsxParser>? log = null)
        => _log = log ?? NullLogger<XlsxParser>.Instance;

    public FileFormat Format => FileFormat.Xlsx;

    public Task<ParseResult> ParseAsync(Stream stream, FileLayoutHint? layout = null, CancellationToken ct = default)
    {
        layout ??= FileLayoutHint.Default;

        try
        {
            // Читаем входной stream В МАССИВ БАЙТ один раз. ClosedXML после неудачи
            // в ctor закрывает переданный Stream, поэтому для retry повторно
            // переиспользовать тот же MemoryStream нельзя — `CopyTo` упадёт с
            // ObjectDisposedException. Каждая попытка получает свежий MemoryStream
            // поверх неизменяемого byte[].
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }

            try
            {
                return Task.FromResult(ParseFromBytes(bytes, layout, ct));
            }
            catch (Exception ex) when (IsExternalLinkError(ex))
            {
                // Известный кейс: external workbook references. Чистим zip и пробуем
                // ещё раз. Warn — чтобы было видно в логах, что cleanup сработал.
                _log.LogWarning(
                    "XlsxParser: external-link parsing error ({ExType}), running StripExternalLinks + retry. File size: {Bytes} bytes.",
                    ex.GetType().Name, bytes.Length);
                var cleaned = StripExternalLinks(bytes);
                return Task.FromResult(ParseFromBytes(cleaned, layout, ct));
            }
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

    /// <summary>
    /// Открывает <see cref="XLWorkbook"/> и применяет нужную раскладку. Любая ошибка
    /// (включая отложенные «Unable to determine token» при `RangeUsed`/`GetString`)
    /// летит наружу — ловит внешний retry в <see cref="ParseAsync"/>.
    /// </summary>
    private static ParseResult ParseFromBytes(byte[] bytes, FileLayoutHint layout, CancellationToken ct)
    {
        // writable: false — ClosedXML гарантированно не испортит байты для retry.
        using var ms = new MemoryStream(bytes, writable: false);
        using var workbook = new XLWorkbook(ms);
        return layout switch
        {
            KeyValueVertical kv => ParseKeyValueVertical(workbook, kv, ct),
            Tabular tab => ParseTabular(workbook, tab, ct),
            _ => ParseTabular(workbook, new Tabular(), ct),
        };
    }

    /// <summary>
    /// Признак «формула ссылается на внешнюю книгу, которую ClosedXML не умеет распарсить».
    /// Сообщение от ClosedXML вида: "Unable to determine token for '…URL…' at index N".
    /// Под этот случай попадают и формулы в ячейках, и defined names с external refs.
    /// </summary>
    private static bool IsExternalLinkError(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("Unable to determine token", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("ExternalLink", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Удаляет из XLSX-архива части, описывающие внешние связи (external workbook links),
    /// чтобы ClosedXML смог открыть файл. Содержимое ячеек (кэшированные значения)
    /// остаётся нетронутым; формулы со ссылками на внешние книги превратятся в
    /// «оторванные» ссылки `[N]`, но их кэш в `&lt;v&gt;` всё равно прочитается через GetString().
    ///
    /// Что вырезаем:
    /// 1) Все части `xl/externalLinks/*.xml` (и их `_rels/`).
    /// 2) Из `xl/workbook.xml` — секцию `&lt;externalReferences&gt;` и `&lt;definedName&gt;`,
    ///    у которых RefersTo содержит URL или `[file]` (т.е. ссылается во вне).
    /// 3) Из `xl/_rels/workbook.xml.rels` — Relationship с типом `…/externalLink`.
    /// </summary>
    private static byte[] StripExternalLinks(byte[] source)
    {
        // Копию байтов кладём в writable MemoryStream — ZipArchiveMode.Update
        // требует возможности расширять/перезаписывать поток. По завершении
        // блока ZipArchive диспозится → буфер содержит готовый zip.
        var copy = new MemoryStream();
        copy.Write(source, 0, source.Length);
        copy.Position = 0;

        using (var zip = new ZipArchive(copy, ZipArchiveMode.Update, leaveOpen: true))
        {
            // 1) Целиком удаляем external link parts + их _rels.
            var toDelete = zip.Entries
                .Where(e =>
                    e.FullName.StartsWith("xl/externalLinks/", StringComparison.Ordinal)
                    || e.FullName.StartsWith("xl/_rels/externalLink", StringComparison.Ordinal))
                .ToList();
            foreach (var e in toDelete) e.Delete();

            // 2) Чистим workbook.xml: <externalReferences> и defined names с URL/[file].
            ReplaceXmlEntry(zip, "xl/workbook.xml", xml =>
            {
                xml = Regex.Replace(xml,
                    "<externalReferences>.*?</externalReferences>", "",
                    RegexOptions.Singleline);
                // <definedName name="...">'https://.../[file]Sheet'!$A$1</definedName>
                xml = Regex.Replace(xml,
                    "<definedName[^>]*>[^<]*(https?://|\\[)[^<]*</definedName>", "",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                return xml;
            });

            // 3) Чистим _rels/workbook.xml.rels: Relationship Type="…/externalLink".
            ReplaceXmlEntry(zip, "xl/_rels/workbook.xml.rels", xml =>
                Regex.Replace(xml,
                    "<Relationship[^>]*externalLink[^>]*/>", "",
                    RegexOptions.IgnoreCase));

            // 4) Чистим <f>...</f> с URL / [file] во ВСЕХ листах книги.
            //    URL встречаются не только в defined names, но и прямо в формулах
            //    ячеек: =VLOOKUP('https://.../[CAPRAPSCHED.xls]Sheet'!$A$1:$M$65536, …).
            //    Кэшированное значение <v> рядом остаётся — данные читаются как и раньше.
            //    Перечисляем имена ДО Delete()/CreateEntry(), т.к. итерация Entries во
            //    время мутации zip ненадёжна.
            var sheetEntries = zip.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)
                            && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                .Select(e => e.FullName)
                .ToList();
            foreach (var name in sheetEntries)
            {
                ReplaceXmlEntry(zip, name, xml =>
                    Regex.Replace(xml,
                        "<f\\b[^>]*>[^<]*(?:https?://|\\[)[^<]*</f>", "",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline));
            }
        }

        return copy.ToArray();
    }

    /// <summary>
    /// Перезаписывает XML-часть в zip-архиве. Если содержимое не изменилось — no-op.
    /// Прямой `Open()` для перезаписи в `ZipArchiveMode.Update` ненадёжен: удаление
    /// + создание гарантирует, что новая длина не сохранит хвост старого файла.
    /// </summary>
    private static void ReplaceXmlEntry(ZipArchive zip, string name, Func<string, string> transform)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return;

        string content;
        using (var s = entry.Open())
        using (var r = new StreamReader(s))
        {
            content = r.ReadToEnd();
        }

        var cleaned = transform(content);
        if (cleaned == content) return;

        entry.Delete();
        var newEntry = zip.CreateEntry(name);
        using var ws = newEntry.Open();
        using var w = new StreamWriter(ws);
        w.Write(cleaned);
    }

    private static ParseResult ParseTabular(XLWorkbook workbook, Tabular layout, CancellationToken ct)
    {
        // Объединённые заголовки по всем листам (для совместимости с UI/маппером —
        // у каждого листа может быть свой набор колонок, например в файле помещений
        // лист «Квартиры» имеет «Колич. комнат», а «Машиноместа» — нет).
        var allHeaders = new List<string>();
        var rows = new List<ParsedRow>();
        var errors = new List<ParseError>();

        // Hidden / VeryHidden листы исключаем целиком: пользователь Excel-а специально
        // прячет «черновые» / «технические» вкладки, и тащить из них строки в импорт
        // = воспроизводить то, что автор файла уже спрятал глазами от себя самого.
        // Решение принимаем ДО RangeUsed/анкоров — никаких ParsedRow, никаких ошибок:
        // скрытый лист в логике парсера эквивалентен отсутствующему.
        var sheets = workbook.Worksheets
            .Where(w => w.Visibility == XLWorksheetVisibility.Visible)
            .ToList();
        if (sheets.Count == 0)
        {
            errors.Add(new ParseError(null, "Файл не содержит ни одного видимого листа."));
            return new ParseResult(allHeaders, rows, errors);
        }

        var anchorSet = layout.HeaderAnchors is { Count: > 0 } anchors
            ? new HashSet<string>(anchors.Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase)
            : null;

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

            int firstAbsRow = range.FirstRow().RowNumber();
            int totalRows = range.RowCount();

            // Поиск строки заголовков. Legacy-поведение (без анкоров) — первая строка
            // RangeUsed. С анкорами — первая среди первых ~30 строк, в которой ≥2
            // ячеек точно совпадают с одним из анкоров (case-insensitive по trim).
            // Так корректно разбираются файлы, где над «настоящей» шапкой сидят
            // подзаголовки/коэффициенты (см. doc_project/68-rooms-import.md).
            //
            // ВАЖНО: если анкоры заданы, но в листе не нашлось — лист пропускаем
            // ЦЕЛИКОМ (никаких ParsedRow). Это нужно для многолистовых файлов вроде
            // «Ежевика короткая 1.xlsx», где помимо «Квартира» есть «Общий график»,
            // «Итог», «План» — у них своя структура (НЕ реестр помещений), и
            // попытка прочитать их с заголовками строки 1 порождает мусорные строки,
            // которые проходят случайные проверки маппера и переполняют отчёт.
            int headerLocalRow = 1;
            bool headerFound = false;
            if (anchorSet is not null)
            {
                int scanLimit = Math.Min(totalRows, 30);
                for (int local = 1; local <= scanLimit; local++)
                {
                    var probeRow = range.Row(local);
                    int count = 0;
                    foreach (var cell in probeRow.Cells())
                    {
                        var text = cell.GetString().Trim();
                        if (text.Length > 0 && anchorSet.Contains(text))
                        {
                            count++;
                            if (count >= 2) break;
                        }
                    }
                    if (count >= 2)
                    {
                        headerLocalRow = local;
                        headerFound = true;
                        break;
                    }
                }

                if (!headerFound)
                {
                    // Скрипт не нашёл шапку → лист не нашего формата. Молча пропускаем.
                    continue;
                }
            }

            // Заголовки текущей раскладки — из выбранной строки.
            var headerRangeRow = range.Row(headerLocalRow);
            var sheetHeaders = new List<string>();
            foreach (var cell in headerRangeRow.Cells())
            {
                sheetHeaders.Add(cell.GetString().Trim());
            }
            // Хвостовые пустые заголовки отрезаем: если RangeUsed заходит за пределы
            // значимых колонок (формулы в правом крыле и т.п.) — иначе мы породим
            // ключи вида "" в Cells, и одна колонка перетрёт другую.
            while (sheetHeaders.Count > 0 && string.IsNullOrEmpty(sheetHeaders[^1]))
                sheetHeaders.RemoveAt(sheetHeaders.Count - 1);
            if (sheetHeaders.Count == 0) continue;

            // Накопительный union заголовков (без дубликатов, case-insensitive).
            foreach (var h in sheetHeaders)
            {
                if (h.Length > 0 && !allHeaders.Contains(h, StringComparer.OrdinalIgnoreCase))
                    allHeaders.Add(h);
            }

            bool anyDataInSheet = false;
            for (int local = headerLocalRow + 1; local <= totalRows; local++)
            {
                ct.ThrowIfCancellationRequested();
                var row = range.Row(local);
                var cells = new Dictionary<string, string>(sheetHeaders.Count, StringComparer.Ordinal);
                bool isEmpty = true;
                for (int c = 0; c < sheetHeaders.Count; c++)
                {
                    var header = sheetHeaders[c];
                    if (header.Length == 0) continue; // пропускаем пустые заголовки внутри ряда
                    var cell = row.Cell(c + 1);
                    var value = cell.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) isEmpty = false;
                    cells[header] = value ?? string.Empty;
                }
                if (isEmpty) continue; // пропускаем полностью пустые строки

                // SourceRowNumber — АБСОЛЮТНЫЙ номер строки в листе (как в Excel).
                // Для большинства файлов RangeUsed начинается с 1, и absolute == local.
                // Но для файлов с пустыми верхними строками absolute != local —
                // отчёт должен показывать настоящий Excel-номер.
                int absRow = firstAbsRow + local - 1;
                rows.Add(new ParsedRow(absRow, sheetName, cells));
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

        // Лист ищем по имени case-insensitive среди ВИДИМЫХ — Excel допускает любой регистр.
        // Скрытые листы игнорируем: если пользователь спрятал «Inputs» в своём шаблоне,
        // нечего по нему импортировать (см. parsetabular выше). В списке «доступные»
        // тоже показываем только видимые — пусть подсказка соответствует тому, что
        // парсер на самом деле увидит.
        var sheet = workbook.Worksheets.FirstOrDefault(w =>
            w.Visibility == XLWorksheetVisibility.Visible
            && string.Equals(w.Name, layout.SheetName, StringComparison.OrdinalIgnoreCase));
        if (sheet is null)
        {
            var available = string.Join(", ", workbook.Worksheets
                .Where(w => w.Visibility == XLWorksheetVisibility.Visible)
                .Select(w => $"'{w.Name}'"));
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
        // Скрытый «Control» = отсутствующий: парсер не должен молча читать число
        // этапов из листа, который пользователь спрятал.
        var ctrl = workbook.Worksheets.FirstOrDefault(w =>
            w.Visibility == XLWorksheetVisibility.Visible
            && string.Equals(w.Name, sc.SheetName, StringComparison.OrdinalIgnoreCase));
        if (ctrl is null)
        {
            var available = string.Join(", ", workbook.Worksheets
                .Where(w => w.Visibility == XLWorksheetVisibility.Visible)
                .Select(w => $"'{w.Name}'"));
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
