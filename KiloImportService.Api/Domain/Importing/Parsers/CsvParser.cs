using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace KiloImportService.Api.Domain.Importing.Parsers;

/// <summary>
/// Парсер CSV через CsvHelper.
/// Поддерживает запятую и точку-с-запятой (DetectDelimiter=true).
/// Кодировка UTF-8 BOM-aware.
/// </summary>
public sealed class CsvParser : IFileParser
{
    public FileFormat Format => FileFormat.Csv;

    public async Task<ParseResult> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        var headers = new List<string>();
        var rows = new List<ParsedRow>();
        var errors = new List<ParseError>();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            BadDataFound = null,                   // плохие кавычки → берём как есть, ошибка попадёт в строку
            MissingFieldFound = null,
        };

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, config);

        try
        {
            // Читаем заголовок.
            await csv.ReadAsync();
            csv.ReadHeader();
            if (csv.HeaderRecord is null || csv.HeaderRecord.Length == 0)
            {
                errors.Add(new ParseError(1, "CSV не содержит заголовков."));
                return new ParseResult(headers, rows, errors);
            }
            headers.AddRange(csv.HeaderRecord.Select(h => h.Trim()));

            // Строки данных. Номер строки в файле = 1-based + 1 (за заголовок).
            int rowIndex = 2;
            while (await csv.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                var cells = new Dictionary<string, string>(headers.Count, StringComparer.Ordinal);
                bool isEmpty = true;
                for (int c = 0; c < headers.Count; c++)
                {
                    string value;
                    try
                    {
                        value = csv.GetField(c) ?? string.Empty;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new ParseError(rowIndex, $"Не удалось прочитать колонку '{headers[c]}': {ex.Message}"));
                        value = string.Empty;
                    }
                    if (!string.IsNullOrWhiteSpace(value)) isEmpty = false;
                    cells[headers[c]] = value;
                }
                if (!isEmpty) rows.Add(new ParsedRow(rowIndex, cells));
                rowIndex++;
            }
        }
        catch (Exception ex)
        {
            errors.Add(new ParseError(null, $"Не удалось прочитать CSV: {ex.Message}"));
        }

        return new ParseResult(headers, rows, errors);
    }
}
