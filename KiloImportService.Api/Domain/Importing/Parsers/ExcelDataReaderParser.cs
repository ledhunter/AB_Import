using System.Text;
using ExcelDataReader;

namespace KiloImportService.Api.Domain.Importing.Parsers;

/// <summary>
/// Универсальный парсер на ExcelDataReader для legacy-форматов XLS и бинарного XLSB.
/// (XLSX тоже поддерживается ExcelDataReader, но для него предпочтительнее ClosedXML.)
///
/// Этот класс — БАЗА для конкретных стратегий <see cref="XlsParser"/> и <see cref="XlsbParser"/>.
/// </summary>
public abstract class ExcelDataReaderParserBase : IFileParser
{
    static ExcelDataReaderParserBase()
    {
        // Требуется для чтения CP1251 в legacy-XLS.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public abstract FileFormat Format { get; }

    public Task<ParseResult> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        var headers = new List<string>();
        var rows = new List<ParsedRow>();
        var errors = new List<ParseError>();

        try
        {
            using var reader = ExcelReaderFactory.CreateReader(stream);

            // Первый лист, первая строка = заголовки.
            if (!reader.Read())
            {
                errors.Add(new ParseError(null, "Файл пустой."));
                return Task.FromResult(new ParseResult(headers, rows, errors));
            }
            for (int i = 0; i < reader.FieldCount; i++)
            {
                headers.Add((reader.GetValue(i)?.ToString() ?? string.Empty).Trim());
            }
            if (headers.All(string.IsNullOrEmpty))
            {
                errors.Add(new ParseError(1, "Заголовки колонок пустые."));
                return Task.FromResult(new ParseResult(headers, rows, errors));
            }

            int rowIndex = 2;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var cells = new Dictionary<string, string>(headers.Count, StringComparer.Ordinal);
                bool isEmpty = true;
                for (int c = 0; c < headers.Count && c < reader.FieldCount; c++)
                {
                    var raw = reader.GetValue(c);
                    var value = raw switch
                    {
                        null => string.Empty,
                        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
                        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                        _ => raw.ToString() ?? string.Empty
                    };
                    if (!string.IsNullOrWhiteSpace(value)) isEmpty = false;
                    cells[headers[c]] = value;
                }
                if (!isEmpty) rows.Add(new ParsedRow(rowIndex, cells));
                rowIndex++;
            }
        }
        catch (Exception ex)
        {
            errors.Add(new ParseError(null, $"Не удалось прочитать {Format}: {ex.Message}"));
        }

        return Task.FromResult(new ParseResult(headers, rows, errors));
    }
}

/// <summary>Парсер устаревшего бинарного формата XLS (Excel 97-2003).</summary>
public sealed class XlsParser : ExcelDataReaderParserBase
{
    public override FileFormat Format => FileFormat.Xls;
}

/// <summary>Парсер бинарного формата XLSB (Excel Binary Workbook).</summary>
public sealed class XlsbParser : ExcelDataReaderParserBase
{
    public override FileFormat Format => FileFormat.Xlsb;
}
