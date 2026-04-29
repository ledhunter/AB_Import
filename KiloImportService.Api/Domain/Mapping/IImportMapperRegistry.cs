namespace KiloImportService.Api.Domain.Mapping;

/// <summary>
/// Реестр зарегистрированных мапперов (по коду типа импорта).
/// </summary>
public interface IImportMapperRegistry
{
    IImportMapper GetByTypeCode(string code);
    IReadOnlyCollection<string> SupportedTypeCodes { get; }
}

public sealed class ImportMapperRegistry : IImportMapperRegistry
{
    private readonly IReadOnlyDictionary<string, IImportMapper> _mappers;

    public ImportMapperRegistry(IEnumerable<IImportMapper> mappers)
    {
        _mappers = mappers.ToDictionary(m => m.ImportTypeCode, StringComparer.OrdinalIgnoreCase);
    }

    public IImportMapper GetByTypeCode(string code)
    {
        if (_mappers.TryGetValue(code, out var m)) return m;
        throw new NotSupportedException(
            $"Тип импорта '{code}' не поддерживается. Доступные: {string.Join(", ", _mappers.Keys)}.");
    }

    public IReadOnlyCollection<string> SupportedTypeCodes => _mappers.Keys.ToArray();
}
