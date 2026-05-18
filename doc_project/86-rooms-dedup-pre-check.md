# 🔁 Импорт «Помещения»: pre-check дедупа для Section / Room / ShareAgreement

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-18
**Дополняет**: [68-rooms-import.md](68-rooms-import.md), [76-share-agreement-dedup.md](76-share-agreement-dedup.md), [77-room-uniqueness-building-section.md](77-room-uniqueness-building-section.md)

В пользовательском окружении наблюдались дубликаты ДДУ: для одного помещения
(«Машиноместо 3_1.2_») и одного номера ДДУ («№ маш 2 -1-3») в одном проекте
существовали ДВА `ShareAgreement` с разными ID. Хотя глобальный
`FindShareAgreementsAsync` (см. [76-share-agreement-dedup.md](76-share-agreement-dedup.md))
уже был, на части кейсов он не находил существующую запись — обычно из-за
хвостовых пробелов в `Number` или специфики Visary `=`-фильтра по строкам.

Теперь перед созданием **любой из трёх сущностей** (`Section`, `Room`,
`ShareAgreement`) маппер делает «жёсткий» pre-check c `Trim()` +
`OrdinalIgnoreCase` на стороне приложения, а для ДДУ — двухступенчатый
поиск (сначала в комнате, потом глобально).

---

## ✅ Правильная реализация

### 1. Section — Trim в локальном фильтре

```csharp
var existing = await _listView.GetSectionsBySiteAsync(siteId, sectionTitle, ct);
var sectionTitleTrim = sectionTitle.Trim();
var match = existing.Data.FirstOrDefault(x =>
    string.Equals((x.Title ?? string.Empty).Trim(), sectionTitleTrim,
        StringComparison.OrdinalIgnoreCase));
```

Без `Trim()` `«1.1»` (из файла) и `«1.1 »` (в Visary) считались разными —
второй импорт плодил дубликат корпуса.

### 2. Room — Trim для номера, BuildingSection уже был

```csharp
var roomNumberTrim = roomNumber.Trim();
var buildingSectionTrim = buildingSection.Trim();
var match = roomsInSection.Data.FirstOrDefault(r =>
    (kindId is null || r.Kind?.ID == kindId.Value)
    && (string.Equals((r.ExplicationNumber ?? "").Trim(), roomNumberTrim, OrdinalIgnoreCase) ||
        string.Equals((r.Number ?? "").Trim(),            roomNumberTrim, OrdinalIgnoreCase))
    && string.Equals((r.BuildingSection ?? "").Trim(), buildingSectionTrim, OrdinalIgnoreCase));
```

### 3. ShareAgreement — **двухступенчатый pre-check**

```csharp
var saNumberTrim = saNumber.Trim();
ShareAgreementRaw? saMatch = null;
bool matchedInRoom = false;

// (1) PRE-CHECK В КОМНАТЕ — самая частая причина дубликатов: повторный
//     импорт того же файла в ту же комнату. Тянем ВСЕ ДДУ комнаты (без
//     серверного фильтра по Number — Visary `=` чувствителен к whitespace),
//     потом локально сравниваем с Trim()+OrdinalIgnoreCase.
try
{
    var byRoom = await _listView.GetShareAgreementsByRoomAsync(roomId.Value, null, ct);
    saMatch = byRoom.Data
        .Where(a => string.Equals((a.Number ?? "").Trim(), saNumberTrim, OrdinalIgnoreCase))
        .OrderByDescending(a => a.ID)
        .FirstOrDefault();
    if (saMatch is not null) matchedInRoom = true;
}
catch (Exception roomFindEx) { _log.LogWarning(roomFindEx, "…"); }

// (2) Если в комнате нет — глобальный поиск (orphan-ДДУ из других комнат).
if (saMatch is null)
{
    try
    {
        var found = await _listView.FindShareAgreementsAsync(
            number: saNumber, roomKindId: kindId,
            conditionalNumber: roomNumber, stageNumber: stageNumberForSa,
            projectNumber: projectNumberForSa, ct);
        saMatch = found.Data
            .Where(a => string.Equals((a.Number ?? "").Trim(), saNumberTrim, OrdinalIgnoreCase))
            .Where(a => kindId is null || a.RoomKindRef?.ID == kindId)
            .OrderByDescending(a => a.ID)
            .FirstOrDefault();
    }
    catch (Exception findEx) { _log.LogWarning(findEx, "…"); }
}

if (saMatch is null) CreateShareAgreement(...);   // только если оба find пусты
else                PatchShareAgreement(saMatch.ID, ...);
```

Поведение `RowActionLog` различает все три исхода:

| Кейс                                                              | Метка                                                       |
|-------------------------------------------------------------------|-------------------------------------------------------------|
| Pre-check в комнате нашёл ДДУ                                     | `ДДУ найден в помещении (не создан, №…)`                    |
| Глобальный find нашёл orphan-ДДУ в другой комнате — PATCH с линком | `ДДУ найден глобально (привязан к новому помещению, №…)`    |
| Глобальный find нашёл в этой же комнате (теоретически: pre-check упал) | `ДДУ найден (не создан, №…)`                            |
| Ни pre-check, ни глобальный find не нашли                         | `ДДУ создан (№…)`                                            |

### ⚠️ Важно

- **Серверный фильтр Visary `["Number","=",value]` хрупкий**: для строки
  с хвостовым пробелом или другой case может вернуть пусто, даже если
  запись есть. Поэтому pre-check тянет ВСЕ ДДУ комнаты (`numberFilter=null`)
  и фильтрует локально. На реальных объёмах (≤сотни ДДУ на комнату)
  это безопасно по сети.
- **Pre-check в комнате первый, глобальный — второй**. Обратный порядок
  работал бы тоже, но global-find делает 5-полевой фильтр на стороне
  Visary и иногда возвращает ложно-пусто — а в той же комнате мы точно
  его найдём.
- Если оба find упали (исключения) — лог `LogWarning` и в итоге создаём
  новый ДДУ. Это сознательный fallback: лучше иметь дубликат, чем
  потерять данные. Пользователь увидит «ДДУ создан» в журнале и сможет
  разобраться.
- Все локальные сравнения теперь по принципу
  `(value ?? "").Trim() + StringComparison.OrdinalIgnoreCase` — это
  единственный надёжный способ при whitespace/case-вариациях Visary.

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — полагаться ТОЛЬКО на глобальный find: при whitespace в Number
// Visary вернёт пусто, и каждый повторный импорт создаст ещё один ДДУ.
var found = await _listView.FindShareAgreementsAsync(number: saNumber, …);
if (!found.Data.Any()) CreateShareAgreement(…);
```

```csharp
// НЕПРАВИЛЬНО — сравнивать без Trim() в локальном пост-фильтре:
// Visary вернул запись с Number="№ маш 2 -1-3 " (хвостовой пробел), а у нас
// в файле "№ маш 2 -1-3" — не совпадёт и плодится дубликат.
.Where(a => string.Equals(a.Number, saNumber, StringComparison.OrdinalIgnoreCase))
```

```csharp
// НЕПРАВИЛЬНО — серверный numberFilter в GetShareAgreementsByRoomAsync.
// Visary `=` чувствителен к whitespace, может вернуть 0 — и мы создадим
// дубликат, хотя запись физически рядом в той же комнате. Тянем null-filter
// и фильтруем локально.
var byRoom = await _listView.GetShareAgreementsByRoomAsync(roomId, saNumber, ct);
```

---

## 📍 Применение в проекте

| Сущность         | Файл / точка                                                          | Что изменилось |
|------------------|-----------------------------------------------------------------------|----------------|
| Section          | `Domain/Mapping/RoomsFormImportMapper.cs::ApplyAsync` (~L 650)        | `Trim()` в локальном match по Title |
| Room             | `Domain/Mapping/RoomsFormImportMapper.cs::ApplyAsync` (~L 700)        | `Trim()` для `ExplicationNumber`/`Number` |
| ShareAgreement   | `Domain/Mapping/RoomsFormImportMapper.cs::ApplyAsync` (~L 800)        | Двухступенчатый pre-check (комната → глобально), Trim()+OrdinalIgnoreCase, 4 разных метки журнала |

---

## 🎯 Чек-лист при изменениях

- [ ] Любое сравнение значения из Excel и значения из Visary-listview — обязательно `(x ?? "").Trim()` + `OrdinalIgnoreCase`.
- [ ] Если добавляешь новый CRUD-вызов в Apply, начни с поиска существующего; локальный пост-фильтр с Trim — обязателен.
- [ ] Для уникальности «по бизнес-ключу» (как ДДУ) — двухступенчатый поиск: сначала в ближайшем контексте (комната, секция), потом глобально. Глобальный find пропускает строки с whitespace в Number.
- [ ] Метка в `RowActionLog` должна отличать «найдено в локальном контексте» от «найдено глобально + привязано» — пользователю важно видеть, что произошла re-attach, а не просто find.
