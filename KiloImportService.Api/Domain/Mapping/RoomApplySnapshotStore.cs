using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KiloImportService.Api.Data;
using KiloImportService.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KiloImportService.Api.Domain.Mapping;

/// <summary>
/// Бизнес-ключ снапшота строки импорта «rooms». Совпадает с unique-key Room
/// (doc 77): VisarySiteId + Sheet + SectionTitle + RoomKindId + RoomNumber +
/// BuildingSection. Строки нормализованы (Trim()+ToLowerInvariant), kindId
/// null → 0 — иначе Postgres трактует NULL как «не равно NULL» в unique index.
/// </summary>
public readonly record struct RoomSnapshotKey(
    int VisarySiteId,
    string Sheet,
    string SectionTitle,
    int RoomKindId,
    string RoomNumber,
    string BuildingSection);

/// <summary>
/// Репозиторий снапшотов «последнее применённое состояние» по строкам импорта
/// «Помещения». Используется маппером для инкрементального импорта: pre-load
/// один раз на сессию (батч по Site) → сравнить хэш → skip PATCH, если ничего
/// не изменилось → upsert после успешного CREATE/PATCH.
///
/// Хэш канонизирует только те поля <c>MappedValues</c>, которые реально пишутся
/// в Visary (Room + ShareAgreement). Это позволяет не реагировать на перестановки
/// колонок в файле, изменения формата чисел и т. п. — диффятся именно записываемые
/// данные.
///
/// Сервис зарегистрирован как Scoped (зависит от <see cref="ImportServiceDbContext"/>),
/// маппер достаёт его через <see cref="IServiceScopeFactory"/> — captive dependency,
/// см. <c>BudgetVisaryUploader</c> в FinModelImportMapper.
/// </summary>
public sealed class RoomApplySnapshotStore
{
    /// <summary>
    /// Поля <c>MappedValues</c>, которые влияют на запись в Visary. Перечень
    /// зафиксирован отдельно от MappedRow.MappedValues, чтобы добавление в маппер
    /// диагностических полей (типа «SourceCellRef») не ломало хэш-сравнение.
    /// </summary>
    private static readonly string[] HashedMappedFields =
    [
        "RoomNumber", "RoomKindId", "RoomKindTitle", "RoomCategory",
        "SectionTitle", "SectionTitleNumeric",
        "BuildingSection", "Floor",
        "RoomsCount",
        "ProjectArea", "TotalArea",
        "CostForOne", "MarketCostPerM", "ZalogCostPerM",
        "ShareAgreementNumber",
        "StageNumberRaw", "StageNumber", "ProjectNumber",
    ];

    private readonly ImportServiceDbContext _db;
    private readonly ILogger<RoomApplySnapshotStore> _log;

    public RoomApplySnapshotStore(ImportServiceDbContext db, ILogger<RoomApplySnapshotStore> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Загружает все снапшоты для конкретного ОКСа в Dictionary. Один SELECT на
    /// всю сессию импорта — маппер дальше работает только в памяти.
    /// </summary>
    public async Task<ConcurrentDictionary<RoomSnapshotKey, RoomApplySnapshot>> LoadForSiteAsync(
        int visarySiteId, CancellationToken ct)
    {
        var snapshots = await _db.RoomApplySnapshots
            .AsNoTracking()
            .Where(s => s.VisarySiteId == visarySiteId)
            .ToListAsync(ct);

        var map = new ConcurrentDictionary<RoomSnapshotKey, RoomApplySnapshot>();
        foreach (var s in snapshots)
        {
            var key = BuildKey(
                s.VisarySiteId, s.Sheet, s.SectionTitle,
                s.RoomKindId, s.RoomNumber, s.BuildingSection);
            map[key] = s;
        }

        _log.LogInformation(
            "RoomApplySnapshotStore: pre-loaded {Count} snapshots for siteId={SiteId}",
            map.Count, visarySiteId);
        return map;
    }

    /// <summary>
    /// Атомарный upsert по бизнес-ключу. Использует серверную транзакцию: сначала
    /// SELECT (с трекингом), затем UPDATE или INSERT. При параллельных Apply из
    /// разных потоков может произойти 23505 — caller перехватывает и повторяет.
    /// </summary>
    public async Task UpsertAsync(RoomApplySnapshot snapshot, CancellationToken ct)
    {
        var existing = await _db.RoomApplySnapshots
            .FirstOrDefaultAsync(s =>
                s.VisarySiteId == snapshot.VisarySiteId
                && s.Sheet == snapshot.Sheet
                && s.SectionTitle == snapshot.SectionTitle
                && s.RoomKindId == snapshot.RoomKindId
                && s.RoomNumber == snapshot.RoomNumber
                && s.BuildingSection == snapshot.BuildingSection,
                ct);

        if (existing is null)
        {
            _db.RoomApplySnapshots.Add(snapshot);
        }
        else
        {
            existing.MappedHash = snapshot.MappedHash;
            existing.MappedSnapshot = snapshot.MappedSnapshot;
            existing.VisarySectionId = snapshot.VisarySectionId;
            existing.VisaryRoomId = snapshot.VisaryRoomId;
            existing.VisaryShareAgreementId = snapshot.VisaryShareAgreementId;
            existing.ShareAgreementNumber = snapshot.ShareAgreementNumber;
            existing.LastAppliedSessionId = snapshot.LastAppliedSessionId;
            existing.LastAppliedAt = snapshot.LastAppliedAt;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Bulk upsert — батчево сохраняет N снапшотов одной транзакцией. Используется
    /// маппером в конце Apply-фазы: сначала параллельно обрабатываем строки в памяти,
    /// потом один SaveChangesAsync на всю партию. Если запись с таким бизнес-ключом
    /// уже есть — обновляем поля; иначе вставляем.
    /// </summary>
    public async Task UpsertBatchAsync(
        IReadOnlyCollection<RoomApplySnapshot> snapshots, CancellationToken ct)
    {
        if (snapshots.Count == 0) return;

        // Подгружаем существующие снапшоты по бизнес-ключу пакетно: один SELECT
        // на (VisarySiteId IN …, Sheet IN …) — экономнее, чем N запросов.
        var siteIds = snapshots.Select(s => s.VisarySiteId).Distinct().ToArray();
        var existingForSites = await _db.RoomApplySnapshots
            .Where(s => siteIds.Contains(s.VisarySiteId))
            .ToListAsync(ct);
        var existingByKey = existingForSites.ToDictionary(s => BuildKey(
            s.VisarySiteId, s.Sheet, s.SectionTitle, s.RoomKindId, s.RoomNumber, s.BuildingSection));

        foreach (var s in snapshots)
        {
            var key = BuildKey(
                s.VisarySiteId, s.Sheet, s.SectionTitle,
                s.RoomKindId, s.RoomNumber, s.BuildingSection);

            if (existingByKey.TryGetValue(key, out var existing))
            {
                existing.MappedHash = s.MappedHash;
                existing.MappedSnapshot = s.MappedSnapshot;
                existing.VisarySectionId = s.VisarySectionId;
                existing.VisaryRoomId = s.VisaryRoomId;
                existing.VisaryShareAgreementId = s.VisaryShareAgreementId;
                existing.ShareAgreementNumber = s.ShareAgreementNumber;
                existing.LastAppliedSessionId = s.LastAppliedSessionId;
                existing.LastAppliedAt = s.LastAppliedAt;
            }
            else
            {
                _db.RoomApplySnapshots.Add(s);
                // Чтобы внутри той же партии не вставить дубликат: помечаем как
                // «уже видели». EF трекер сделает то же самое, но dictionary даст
                // raw-O(1) защиту при больших партиях.
                existingByKey[key] = s;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Канонизирует <c>MappedValues</c> и считает SHA256-hex. Сравнение двух
    /// строк хэша → детерминированное «изменилось / не изменилось» для PATCH-skip
    /// логики в маппере.
    /// </summary>
    public static string ComputeMappedHash(JsonElement mappedValues)
    {
        // SortedDictionary для детерминированного порядка ключей — JSON-сериализация
        // по умолчанию не гарантирует сортировку. Значения приводим к invariant-формату
        // через ReadCanonical, чтобы «1.0» и «1» давали один хэш.
        var canonical = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in HashedMappedFields)
        {
            canonical[field] = ReadCanonical(mappedValues, field);
        }

        var json = JsonSerializer.Serialize(canonical);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Возвращает значение свойства в канонической текстовой форме:
    ///   string → Trim()+ToLowerInvariant
    ///   number → invariant string
    ///   bool   → "true"/"false"
    ///   null/undefined → null
    /// </summary>
    private static string? ReadCanonical(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(propertyName, out var prop)) return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => NormalizeString(prop.GetString()),
            JsonValueKind.Number => prop.TryGetInt64(out var l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : prop.GetDouble().ToString("R", CultureInfo.InvariantCulture),
            JsonValueKind.True   => "true",
            JsonValueKind.False  => "false",
            JsonValueKind.Null   => null,
            JsonValueKind.Undefined => null,
            _ => prop.GetRawText(),
        };
    }

    /// <summary>Бизнес-ключ строки — единый Build-метод для load/upsert/lookup.</summary>
    public static RoomSnapshotKey BuildKey(
        int visarySiteId, string sheet, string sectionTitle,
        int? roomKindId, string roomNumber, string buildingSection)
    {
        return new RoomSnapshotKey(
            visarySiteId,
            NormalizeString(sheet) ?? string.Empty,
            NormalizeString(sectionTitle) ?? string.Empty,
            roomKindId ?? 0,
            NormalizeString(roomNumber) ?? string.Empty,
            NormalizeString(buildingSection) ?? string.Empty);
    }

    private static string? NormalizeString(string? s) =>
        s is null ? null : s.Trim().ToLowerInvariant();
}
