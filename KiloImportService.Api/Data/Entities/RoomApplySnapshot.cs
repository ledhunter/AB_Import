using System.Text.Json;

namespace KiloImportService.Api.Data.Entities;

/// <summary>
/// Снапшот последнего применённого состояния одной строки импорта «Помещения»
/// (rooms). Используется для инкрементального импорта: при повторной загрузке
/// маппер сравнивает текущие <c>MappedValues</c> со <see cref="MappedHash"/>
/// и, если ничего не изменилось, пропускает PATCH в Visary.
///
/// Ключ уникальности совпадает с бизнес-ключом Room (см. doc 77):
///   (VisarySiteId, Sheet, SectionTitle, RoomKindId, RoomNumber, BuildingSection)
///
/// Хранится в <c>import_service_db.import.room_apply_snapshots</c>.
/// </summary>
public class RoomApplySnapshot
{
    public long Id { get; set; }

    // ── Бизнес-ключ ───────────────────────────────────────────────────────
    /// <summary>ID объекта строительства Visary, в рамках которого применили строку.</summary>
    public int VisarySiteId { get; set; }

    /// <summary>Имя листа Excel (одновременно — тип помещений: «Квартиры», «Машиноместа»…).</summary>
    public string Sheet { get; set; } = string.Empty;

    /// <summary>Title корпуса (например, «1.1»). Нормализован Trim()+ToLowerInvariant — см. doc 77.</summary>
    public string SectionTitle { get; set; } = string.Empty;

    /// <summary>ID типа помещения (Visary RoomKind.ID). Null допустим — старые записи.</summary>
    public int? RoomKindId { get; set; }

    /// <summary>«Номер квартиры» / «Номер помещения» в файле — нормализован (Trim+ToLower).</summary>
    public string RoomNumber { get; set; } = string.Empty;

    /// <summary>«Подъезд/Секция» — нормализован (Trim+ToLower). Часть unique key Room.</summary>
    public string BuildingSection { get; set; } = string.Empty;

    // ── Снапшот применённого состояния ────────────────────────────────────
    /// <summary>
    /// SHA256 (hex) от канонизированного набора полей <c>MappedValues</c>,
    /// которые реально пишутся в Visary (Room + ShareAgreement). Если хэш совпал —
    /// PATCH не делаем.
    /// </summary>
    public string MappedHash { get; set; } = null!;

    /// <summary>Полный <c>MappedValues</c> на момент Apply — для отладки и расширенного дифф-анализа.</summary>
    public JsonDocument MappedSnapshot { get; set; } = null!;

    // ── Visary IDs ────────────────────────────────────────────────────────
    public int? VisarySectionId { get; set; }
    public int? VisaryRoomId { get; set; }
    public int? VisaryShareAgreementId { get; set; }

    /// <summary>Номер ДДУ (для диагностики; основной бизнес-ключ ДДУ — внутри RoomApplySnapshot.MappedSnapshot).</summary>
    public string? ShareAgreementNumber { get; set; }

    // ── Аудит ─────────────────────────────────────────────────────────────
    /// <summary>Сессия импорта, в которой запись была создана/обновлена в последний раз.</summary>
    public Guid LastAppliedSessionId { get; set; }

    public DateTimeOffset LastAppliedAt { get; set; } = DateTimeOffset.UtcNow;
}
