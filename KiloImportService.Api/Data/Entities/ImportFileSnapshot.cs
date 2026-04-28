namespace KiloImportService.Api.Data.Entities;

/// <summary>
/// Бинарь оригинального файла (для аудита и переимпорта).
/// Связь 1:1 с <see cref="ImportSession"/>.
/// </summary>
public class ImportFileSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImportSessionId { get; set; }
    public ImportSession Session { get; set; } = null!;

    /// <summary>
    /// Относительный путь файла на диске (внутри <c>ImportStorage:Path</c>).
    /// Альтернатива — хранение в <c>bytea</c>, но для больших файлов это плохо.
    /// </summary>
    public string RelativePath { get; set; } = null!;

    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
