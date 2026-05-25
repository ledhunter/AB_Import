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

    /// <summary>
    /// Опциональный второй файл импорта. Сейчас используется только Финмоделью —
    /// заказчик загружает «файл с планами» (лист «План»), из которого маппер
    /// читает краевые квартальные значения для создания <c>fmmodel</c> в Visary.
    /// Для остальных типов импорта поле остаётся <c>null</c>.
    /// См. doc_project/110-finmodel-plan-and-fmmodel.md.
    /// </summary>
    public string? SecondaryRelativePath { get; set; }

    /// <summary>Имя оригинального второго файла (для UI/диагностики, если есть).</summary>
    public string? SecondaryFileName { get; set; }

    /// <summary>Размер второго файла, байт. <c>null</c> если файла нет.</summary>
    public long? SecondarySizeBytes { get; set; }
}
