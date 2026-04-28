namespace KiloImportService.Api.Domain.Pipeline;

/// <summary>
/// Хранилище загруженных файлов (для аудита и переимпорта).
/// MVP-реализация — локальная файловая система; в проде заменяется на S3/blob storage.
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Сохранить файл и вернуть его относительный путь (стабильный идентификатор).
    /// </summary>
    Task<string> SaveAsync(Stream content, string originalFileName, CancellationToken ct);

    /// <summary>Открыть поток для чтения по ранее сохранённому пути.</summary>
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct);
}

/// <summary>
/// Реализация на локальной FS. Структура: <c>{root}/{yyyy}/{MM}/{dd}/{guid}_{original}</c>.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;
    private readonly ILogger<LocalFileStorage> _log;

    public LocalFileStorage(IConfiguration cfg, ILogger<LocalFileStorage> log)
    {
        _root = cfg["ImportStorage:Path"] ?? "./.local-storage";
        _log = log;
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(Stream content, string originalFileName, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rel = Path.Combine(now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"),
            $"{Guid.NewGuid():N}_{Sanitize(originalFileName)}");
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        await using var fs = File.Create(full);
        content.Position = 0;
        await content.CopyToAsync(fs, ct);
        _log.LogInformation("File stored: {RelativePath} ({Size} bytes)", rel, content.Length);
        return rel.Replace('\\', '/');
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return Task.FromResult<Stream>(File.OpenRead(full));
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
