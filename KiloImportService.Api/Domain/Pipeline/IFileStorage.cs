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
/// <remarks>
/// Path Traversal hardening (см. doc 144):
///   1) <see cref="Sanitize"/> сначала прогоняет <c>originalFileName</c> через
///      <see cref="Path.GetFileName(string)"/>, чтобы вырезать любые path-составляющие
///      (защита кросс-OS — на Linux '\' НЕ входит в <see cref="Path.GetInvalidFileNameChars"/>,
///      и однострочный Replace из старой реализации пропускал имя <c>..\..\..\evil</c>).
///   2) <see cref="OpenReadAsync"/> явно отвергает абсолютные пути и любые '..' в сегментах
///      пути ДО комбинирования (защита на случай, если в БД попадёт «грязный» путь).
///   3) Оба метода после <see cref="Path.Combine(string,string)"/> резолвят <see cref="Path.GetFullPath(string)"/>
///      и проверяют, что результат остаётся внутри <c>_root</c> (canonical-path containment).
///      Trailing <see cref="Path.DirectorySeparatorChar"/> у root обязателен —
///      без него атакующий мог бы выйти на сосед-каталог с похожим префиксом
///      (<c>/var/lib/import-files-evil</c>).
/// </remarks>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _rootFull;
    private readonly ILogger<LocalFileStorage> _log;

    public LocalFileStorage(IConfiguration cfg, ILogger<LocalFileStorage> log)
    {
        var configured = cfg["ImportStorage:Path"] ?? "./.local-storage";
        Directory.CreateDirectory(configured);
        // Canonical absolute root с trailing separator — фиксируем один раз
        // в конструкторе, чтобы StartsWith-проверки были устойчивы к symlink'ам
        // и относительным префиксам (рабочая директория процесса может сдвинуться).
        _rootFull = EnsureTrailingSeparator(Path.GetFullPath(configured));
        _log = log;
    }

    public async Task<string> SaveAsync(Stream content, string originalFileName, CancellationToken ct)
    {
        var safeName = Sanitize(originalFileName);
        var now = DateTime.UtcNow;
        var rel = Path.Combine(now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"),
            $"{Guid.NewGuid():N}_{safeName}");
        var full = Path.GetFullPath(Path.Combine(_rootFull, rel));
        // Containment-проверка: если санитизация подведёт — резолвим в реальное место
        // и валим до записи. UnauthorizedAccessException — best-fit; контроллер
        // переводит её в 400/403 (см. ExceptionHandler).
        if (!full.StartsWith(_rootFull, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Resolved file path is outside the storage root.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        await using var fs = File.Create(full);
        content.Position = 0;
        await content.CopyToAsync(fs, ct);
        _log.LogInformation("File stored: {RelativePath} ({Size} bytes)", rel, content.Length);
        return rel.Replace('\\', '/');
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("relativePath required", nameof(relativePath));
        // Раннее отсечение: абсолютные пути ('/foo', 'C:\foo', '\\server\share')
        // и любые '..' — даже если путь приходит из БД, defense-in-depth.
        if (Path.IsPathRooted(relativePath)
            || relativePath.Contains("..", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Relative path is not allowed.");
        }
        var combined = Path.Combine(_rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var full = Path.GetFullPath(combined);
        if (!full.StartsWith(_rootFull, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Resolved file path is outside the storage root.");
        return Task.FromResult<Stream>(File.OpenRead(full));
    }

    private static string Sanitize(string name)
    {
        // Шаг 1 — режем любую path-составляющую (кросс-OS). На Linux '\' НЕ входит
        // в GetInvalidFileNameChars, и старая реализация пропускала '..\..\..\evil'.
        name = Path.GetFileName(name ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) name = "unnamed";
        // Шаг 2 — стандартные invalid chars текущей OS (<>:"|?* и т.п.).
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        // Шаг 3 — явный страховой replace кросс-OS path-разделителей и NUL.
        name = name.Replace('/', '_').Replace('\\', '_').Replace('\0', '_');
        return name;
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
