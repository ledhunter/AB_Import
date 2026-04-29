namespace KiloImportService.Api.Domain.Importing;

/// <summary>
/// Стратегия парсинга файла одного формата (CSV / XLS / XLSX / XLSB).
/// Каждая реализация отвечает только за чтение байтов и нормализацию в <see cref="ParseResult"/>.
/// Доменная логика (валидация, маппинг) живёт в <c>IImportMapper</c>.
/// </summary>
public interface IFileParser
{
    /// <summary>Формат, который поддерживает данная реализация (один файл = один формат).</summary>
    FileFormat Format { get; }

    /// <summary>
    /// Прочитать поток и вернуть распарсенные строки.
    /// Реализации не должны бросать исключения по доменным ошибкам — все проблемы
    /// возвращаются в <see cref="ParseResult.Errors"/>. Бросать допустимо только
    /// при катастрофических сбоях (повреждённый файл, неподдерживаемый формат).
    /// </summary>
    Task<ParseResult> ParseAsync(Stream stream, CancellationToken ct = default);
}
