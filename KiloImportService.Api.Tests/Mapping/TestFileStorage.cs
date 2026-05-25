using KiloImportService.Api.Domain.Pipeline;

namespace KiloImportService.Api.Tests.Mapping;

/// <summary>
/// In-memory реализация <see cref="IFileStorage"/> для FinModel-тестов второго
/// файла («План»). Тест кладёт XLSX-байты по пути <c>"plan.xlsx"</c>, мапер
/// открывает их через <see cref="IFileStorage.OpenReadAsync"/>. См. doc 110.
/// </summary>
internal sealed class TestFileStorage : IFileStorage
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public void Put(string relativePath, byte[] bytes) => _files[relativePath] = bytes;

    public Task<string> SaveAsync(Stream content, string originalFileName, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        var rel = $"test/{Guid.NewGuid():N}_{originalFileName}";
        _files[rel] = ms.ToArray();
        return Task.FromResult(rel);
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct)
    {
        if (!_files.TryGetValue(relativePath, out var bytes))
            throw new FileNotFoundException($"TestFileStorage: '{relativePath}' not found");
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}

/// <summary>Заглушка-«нет файлов»: каждый <c>OpenReadAsync</c> бросает.</summary>
internal sealed class NoopFileStorage : IFileStorage
{
    public Task<string> SaveAsync(Stream content, string originalFileName, CancellationToken ct)
        => Task.FromResult($"noop/{Guid.NewGuid():N}_{originalFileName}");

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct)
        => throw new FileNotFoundException($"NoopFileStorage: cannot open '{relativePath}'");
}
