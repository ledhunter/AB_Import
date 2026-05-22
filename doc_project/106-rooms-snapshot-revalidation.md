# 106 · 🔄 Snapshot revalidation против реального Visary в импорте «Помещения»

> Дата: 2026-05-22
>
> Связано с: [96-rooms-incremental-parallel-apply](./96-rooms-incremental-parallel-apply.md), [97-rooms-apply-tests-and-budget-uploader-interface.md](./97-rooms-apply-tests-and-budget-uploader-interface.md), [77-room-uniqueness-building-section](./77-room-uniqueness-building-section.md), [85-per-row-action-log](./85-per-row-action-log.md)

## 📋 Описание

[doc 96](./96-rooms-incremental-parallel-apply.md) ввёл `room_apply_snapshots`: при повторном импорте маппер сравнивает SHA256(`MappedValues`) с `prev.MappedHash` и, если хэш совпал, пропускает PATCH/CREATE Room+ShareAgreement.

**Проблема, обнаруженная в эксплуатации.** Если пользователь удалил Room или ДДУ в Visary между двумя импортами (например, очистил тестовый сайт), локальный snapshot всё ещё содержит тот же хэш → маппер skip-ает строку → удалённое помещение **не восстанавливается**. Файл загружается, отчёт зелёный, в Visary пусто.

Hash-match говорит только про **входные данные импорта** — не про живость сущностей на той стороне. Snapshot — это локальный «мемоиз», и он может разойтись с реальностью.

---

## ✅ Правильная реализация

```csharp
// RoomsFormImportMapper.cs — внутри group-лямбды
// roomsInSection уже загружен ОДНИМ запросом на (Sheet, Section) — стоимость 0.
var saByRoomCache = new Dictionary<int, List<ShareAgreementRaw>>();

if (snapshotsByKey.TryGetValue(snapKey, out var prev)
    && string.Equals(prev.MappedHash, hash, StringComparison.Ordinal))
{
    var (revalidated, staleReason) = await RevalidateSnapshotAsync(
        prev, roomsInSection, saByRoomCache, gct);

    if (revalidated)
    {
        Log(sheetForRow, mr.SourceRowNumber, "Без изменений — пропуск (snapshot)");
        Interlocked.Increment(ref skipped);
        Interlocked.Increment(ref applied);
        continue;
    }

    Log(sheetForRow, mr.SourceRowNumber,
        $"Snapshot устарел ({staleReason}) — пересоздаём");
    // ... continue с обычным flow find-or-create.
}
```

```csharp
private async Task<(bool Live, string? StaleReason)> RevalidateSnapshotAsync(
    RoomApplySnapshot prev,
    IReadOnlyList<RoomRaw> roomsInSection,
    Dictionary<int, List<ShareAgreementRaw>> saByRoomCache,
    CancellationToken ct)
{
    // ── 1. Room-existence (cheap) ────────────────────────────────────
    if (prev.VisaryRoomId is int prevRoomId)
    {
        if (!roomsInSection.Any(r => r.ID == prevRoomId))
            return (false, $"помещение №{prev.RoomNumber} удалено в Visary");
    }

    // ── 2. ShareAgreement-existence (опционально) ────────────────────
    if (prev.VisaryShareAgreementId is int prevSaId
        && prev.VisaryRoomId is int rid)
    {
        try
        {
            if (!saByRoomCache.TryGetValue(rid, out var saList))
            {
                var byRoom = await _listView.GetShareAgreementsByRoomAsync(rid, null, ct);
                saList = byRoom.Data.ToList();
                saByRoomCache[rid] = saList;  // 👈 переиспользуется в основном flow
            }
            if (!saList.Any(a => a.ID == prevSaId))
                return (false, $"ДДУ №{prev.ShareAgreementNumber ?? "?"} удалён в Visary");
        }
        catch (Exception saCheckEx)
        {
            // Сетевая ошибка ≠ удаление. Не инвалидируем snapshot, иначе временный
            // сбой Visary запустил бы full-rewrite всей сессии.
            _log.LogWarning(saCheckEx, "Revalidate: проверка ДДУ saId={SaId} не удалась — считаем snapshot валидным.", prevSaId);
        }
    }

    return (true, null);
}
```

### ⚠️ Важно

- **Room-check бесплатен**: `roomsInSection` уже загружен в начале group-лямбды одним `GetRoomsBySectionAsync` (doc 96). Никаких новых сетевых вызовов.
- **SA-check ленив**: делаем `GetShareAgreementsByRoomAsync(roomId)` только если в snapshot записан `VisaryShareAgreementId` (строка имела ДДУ в прошлый раз). Для строк без ДДУ затрат 0.
- **Локальный кэш `saByRoomCache`** переиспользуется основным flow find-or-create ДДУ — если строка стала «не live» и пошла по обычному пути, второго запроса в Visary не делаем.
- **Сетевая ошибка SA-check → snapshot валиден.** Если Visary временно недоступен, мы НЕ заставляем маппер пересчитывать всё. Прежнее skip-поведение сохраняется при недоступности проверки.
- **Stale-метка в журнале** обязательна: пользователь увидит `Snapshot устарел (помещение №42 удалено в Visary) — пересоздаём` рядом с обычной строкой отчёта. Это диагностический контекст для случаев «почему помещение опять создавалось».

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — skip по hash без проверки реального состояния
if (snapshotsByKey.TryGetValue(snapKey, out var prev)
    && string.Equals(prev.MappedHash, hash, StringComparison.Ordinal))
{
    Log("Без изменений — пропуск (snapshot)");
    continue;
    // ↑ помещение могли удалить в Visary, hash-match это не ловит
}
```

```csharp
// НЕПРАВИЛЬНО — снос snapshot при любой ошибке проверки
catch (Exception saCheckEx)
{
    return (false, "проверка не удалась");
    // ↑ Visary упал на 10 секунд → вся сессия идёт через find-or-create →
    //   тысячи лишних PATCH-ей, проблема, которую doc 96 как раз и решал.
}
```

```csharp
// НЕПРАВИЛЬНО — дополнительный listview/room на каждую строку
var fresh = await _listView.GetRoomByIdAsync(prev.VisaryRoomId);
// ↑ roomsInSection уже на руках; это N лишних round-trip-ов на ровном месте.
```

---

## 📍 Применение в проекте

| Компонент | Файл | Что меняется |
|-----------|------|--------------|
| Маппер «Помещения» | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | `RevalidateSnapshotAsync` + `saByRoomCache` в группе; блок (a') после hash-match |
| Snapshot-сущность | [RoomApplySnapshot.cs](../KiloImportService.Api/Data/Entities/RoomApplySnapshot.cs) | без изменений — `VisaryRoomId`/`VisaryShareAgreementId` уже хранятся |
| Тесты | [RoomsFormImportMapperApplyTests.cs](../KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperApplyTests.cs) | 2 новых теста + обновлённый skip-тест |

---

## 🧪 Тесты

1. **`ApplyAsync_SecondRun_SameRows_SkipsByHash_NoExtraPatchOrCreate`** (обновлён). Во втором запуске моки `GetRoomsBySectionAsync` и `GetShareAgreementsByRoomAsync` теперь возвращают существующие сущности — без этого revalidation признала бы snapshot устаревшим.
2. **`ApplyAsync_SecondRun_RoomDeletedInVisary_SnapshotStale_RecreatesRoom`** (новый). Между запусками `GetRoomsBySectionAsync` начинает возвращать `[]`. Маппер обязан:
   - оставить метку `Snapshot устарел`;
   - вызвать `CreateRoomAsync` ровно один раз;
   - **обновить snapshot.VisaryRoomId** новым ID.
3. **`ApplyAsync_SecondRun_ShareAgreementDeletedInVisary_SnapshotStale_RecreatesShareAgreement`** (новый). Room жив, ДДУ удалён. Маппер делает PATCH Room (не CREATE) + CREATE ДДУ, snapshot.VisaryShareAgreementId обновляется.

---

## 🎯 Чек-лист добавления нового бизнес-ключа в snapshot

Если расширяете `RoomApplySnapshot` (например, добавляете ссылку на ещё одну сущность Visary):

- [ ] Добавить поле в `RoomApplySnapshot.cs` + миграция.
- [ ] В `Apply` записывать новый ID в `snapshotUpserts.Add(...)`.
- [ ] **В `RevalidateSnapshotAsync` добавить проверку существования новой сущности** — иначе мы получим ту же стале-проблему для нового поля.
- [ ] Сетевая ошибка проверки → возвращать `(true, null)`, как для ДДУ.
- [ ] Тест на «сущность удалена в Visary → пересоздание».

---

## 🔒 Инварианты

1. **Hash-match — необходимое, не достаточное условие skip-а.** Это правило теперь центральное; не убирать.
2. **`saByRoomCache` живёт в пределах одной группы (Sheet, Section).** Не выноси на уровень сайта или сессии — между группами roomId-ы не пересекаются, кэш бесполезен.
3. **На сетевой ошибке проверки — snapshot валиден.** Иначе единичный 500 от Visary запустит полный rewrite всех 6000 квартир.
4. **Snapshot переписывается актуальными ID после reuse/recreate.** В батч-upsert (doc 96 ⑥) попадает свежий `VisaryRoomId`/`VisaryShareAgreementId` — иначе следующая сессия снова попадёт в stale-ветку.
5. **Метка «Snapshot устарел» обязательна в RowActions.** Без неё реальная причина пересоздания (что-то удалили в Visary) не дойдёт до отчёта, и пользователь увидит «Помещение создано» вместо «Помещение пересоздано».
