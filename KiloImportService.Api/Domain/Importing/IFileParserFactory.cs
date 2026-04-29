namespace KiloImportService.Api.Domain.Importing;

/// <summary>
/// Фабрика парсеров: по формату возвращает соответствующую стратегию.
/// </summary>
public interface IFileParserFactory
{
    IFileParser GetParser(FileFormat format);
}

public sealed class FileParserFactory : IFileParserFactory
{
    private readonly IReadOnlyDictionary<FileFormat, IFileParser> _parsers;

    public FileParserFactory(IEnumerable<IFileParser> parsers)
    {
        _parsers = parsers.ToDictionary(p => p.Format);
    }

    public IFileParser GetParser(FileFormat format)
    {
        if (_parsers.TryGetValue(format, out var parser)) return parser;
        throw new NotSupportedException($"Формат '{format}' не поддерживается. " +
            $"Зарегистрированные парсеры: {string.Join(", ", _parsers.Keys)}.");
    }
}
