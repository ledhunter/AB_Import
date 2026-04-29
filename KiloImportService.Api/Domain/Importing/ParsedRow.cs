namespace KiloImportService.Api.Domain.Importing;

/// <summary>
/// Распарсенная строка из исходного файла (плоский словарь column_name → string).
/// Все парсеры (CSV/XLS/XLSX/XLSB) возвращают единый формат — это база для маппинга.
/// </summary>
/// <param name="SourceRowNumber">Номер строки в исходном файле, 1-based (как в Excel).</param>
/// <param name="Cells">
/// Колонка → значение в виде строки. Пустые ячейки → <c>""</c>.
/// Имена колонок берутся из первой строки файла (header).
/// </param>
public record ParsedRow(int SourceRowNumber, IReadOnlyDictionary<string, string> Cells);

/// <summary>Результат парсинга одного файла.</summary>
public record ParseResult(
    IReadOnlyList<string> Headers,
    IReadOnlyList<ParsedRow> Rows,
    IReadOnlyList<ParseError> Errors
);

/// <summary>Ошибка парсинга на уровне файла или строки.</summary>
public record ParseError(int? RowNumber, string Message);
