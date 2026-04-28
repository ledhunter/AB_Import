using ClosedXML.Excel;

namespace KiloImportService.Api.Domain.Importing.Parsers;

/// <summary>
/// Парсер XLSX через ClosedXML.
/// Берёт первый лист книги, первую строку — как заголовки.
/// </summary>
public sealed class XlsxParser : IFileParser
{
    public FileFormat Format => FileFormat.Xlsx;

    public Task<ParseResult> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        var headers = new List<string>();
        var rows = new List<ParsedRow>();
        var errors = new List<ParseError>();

        try
        {
            using var workbook = new XLWorkbook(stream);
            var sheet = workbook.Worksheets.FirstOrDefault();
            if (sheet is null)
            {
                errors.Add(new ParseError(null, "Файл не содержит ни одного листа."));
                return Task.FromResult(new ParseResult(headers, rows, errors));
            }

            var range = sheet.RangeUsed();
            if (range is null)
            {
                errors.Add(new ParseError(null, "Лист пустой — нет данных для импорта."));
                return Task.FromResult(new ParseResult(headers, rows, errors));
            }

            var firstRow = range.FirstRow();
            foreach (var cell in firstRow.Cells())
            {
                headers.Add(cell.GetString().Trim());
            }
            if (headers.Count == 0)
            {
                errors.Add(new ParseError(1, "Не удалось прочитать заголовки колонок (первая строка пустая)."));
                return Task.FromResult(new ParseResult(headers, rows, errors));
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
                rows.Add(new ParsedRow(rowIndex, cells));
            }
        }
        catch (Exception ex)
        {
            errors.Add(new ParseError(null, $"Не удалось прочитать XLSX: {ex.Message}"));
        }

        return Task.FromResult(new ParseResult(headers, rows, errors));
    }
}
