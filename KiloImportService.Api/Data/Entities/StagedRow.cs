using System.Text.Json;
using KiloImportService.Api.Domain.Importing;

namespace KiloImportService.Api.Data.Entities;

/// <summary>
/// Распарсенная строка из исходного файла. Лежит в служебной БД до момента apply.
/// JSONB поля хранят raw-значения (как из Excel) и mapped-значения (после маппинга колонок).
/// </summary>
public class StagedRow
{
    public long Id { get; set; }
    public Guid ImportSessionId { get; set; }
    public ImportSession Session { get; set; } = null!;

    /// <summary>Номер строки в исходном файле (1-based, как видит пользователь в Excel).</summary>
    public int SourceRowNumber { get; set; }

    /// <summary>Сырые значения колонок: { "Номер квартиры": "12", "Этаж": "3", … }.</summary>
    public JsonDocument RawValues { get; set; } = null!;

    /// <summary>
    /// Преобразованные значения после маппинга колонок Excel → колонки visary_db.
    /// Заполняется на этапе Validate. Например: { "SiteID": 1, "KindID": 1, "Number": "12", "Floor": "3" }.
    /// </summary>
    public JsonDocument? MappedValues { get; set; }

    public StagedRowStatus Status { get; set; } = StagedRowStatus.Pending;

    /// <summary>ID записи в visary_db после успешного apply (Room.ID и т.п.).</summary>
    public int? AppliedTargetId { get; set; }
}
