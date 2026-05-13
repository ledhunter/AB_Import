# 🔁 Дедупликация ДДУ при импорте «Помещения»

## 📋 Описание

При импорте Помещений (rooms-form) каждая строка может содержать №ДДУ
(`ShareAgreementNumber`). Если в Visary уже существует ДДУ с теми же атрибутами,
его нужно **переиспользовать** (PATCH'нуть на новую комнату), а не создавать
дубликат.

**Бизнес-ключ ДДУ:** комбинация
- `Number` — № договора
- `RoomKindRef` — тип помещения (Квартира / Машиноместо / Кладовая / …)
- `ConditionalNumber` — номер квартиры по экспликации
- `StageNumber` — этап
- `ProjectNumber` — НПС (номер проекта строительства)

---

## 🐛 Что было сломано

Старый код искал ДДУ только в **пределах комнаты**:

```csharp
var sas = await _listView.GetShareAgreementsByRoomAsync(roomId, saNumber, ct);
// → /listview/shareagreement/onetomany/Room?associationId={roomId}
```

Проблема: «орфанные» ДДУ — записи, существующие в Visary, но не привязанные
к Room (`shareagreement.Room == null` или указывает на старую/удалённую
комнату) — не находились. Симптом — пустые ячейки «Объект/Проект/Помещение»
в таблице ДДУ (см. скриншот в задаче). Повторный импорт плодил по новому ДДУ
рядом с уже существующим, реальный orphan'ный — оставался невидимым.

---

## ✅ Правильная реализация

### Visary.Api.Client — новый метод глобального поиска

```csharp
// IListViewClient
Task<ListViewResponse<ShareAgreementRaw>> FindShareAgreementsAsync(
    string? number,
    int? roomKindId,
    string? conditionalNumber,
    string? stageNumber,
    string? projectNumber,
    CancellationToken ct = default);
```

POST `/api/visary/listview/shareagreement` с AND-фильтром только по тем
параметрам, что переданы (паттерн `FindSitesAsync`). Для VisaryRef-поля
`RoomKindRef` используется `FilterByRefIdContains` →
`["RoomKindRef","contains","ID:{kid}"]`. Остальные — `FilterByString` (`"="`).

### `ShareAgreementPatchRequest` расширен полями для реанимации

```csharp
public sealed class ShareAgreementPatchRequest
{
    public string? Number { get; set; }
    public string? Title { get; set; }
    public VisaryRef? Site { get; set; }
    public VisaryRef? Project { get; set; }
    // ↓ новое: используется когда нашли orphan ДДУ и нужно привязать к комнате
    public int? RoomID { get; set; }
    public VisaryRef? Room { get; set; }
    public VisaryRef? RoomKindRef { get; set; }
    public string? ConditionalNumber { get; set; }
    public string? StageNumber { get; set; }
    public string? ProjectNumber { get; set; }
}
```

### RoomsFormImportMapper — flow

```csharp
// Блок 4 в ApplyAsync — Find / Reuse / Create
var saMatch = (await _listView.FindShareAgreementsAsync(
    saNumber, kindId, roomNumber, stageNumberForSa, projectNumberForSa, ct))
    .Data
    .Where(a => string.Equals(a.Number, saNumber, StringComparison.OrdinalIgnoreCase))
    .Where(a => kindId is null || a.RoomKindRef?.ID == kindId)
    .OrderByDescending(a => a.ID)   // 👈 max(ID) при нескольких подходящих
    .FirstOrDefault();

if (saMatch is null)
{
    await _crud.CreateShareAgreementAsync(new ShareAgreementCreateRequest { /* ... */ }, ct);
}
else
{
    // Нашли — PATCH'им на текущую комнату (даже если saMatch уже привязан
    // к другой Room или null). Это «реанимация» orphan-ДДУ.
    await _crud.PatchShareAgreementAsync(saMatch.ID, new ShareAgreementPatchRequest
    {
        Number            = saNumber,
        Title             = saNumber,
        Site              = new VisaryRef { ID = siteId },
        Project           = projectId is null ? null : new VisaryRef { ID = projectId },
        RoomID            = roomId.Value,
        Room              = new VisaryRef { ID = roomId.Value },
        RoomKindRef       = kindId is null ? null : new VisaryRef { ID = kindId },
        ConditionalNumber = roomNumber,
        StageNumber       = stageNumberForSa,
        ProjectNumber     = projectNumberForSa,
    }, ct);
}
```

### ⚠️ Важно

- **Локальная пост-фильтрация** после серверного `contains`. Visary
  `RoomKindRef contains "ID:1"` матчит подстроку — может вернуть запись c
  `RoomKindRef.ID = 10` или `100`. Перед взятием max(ID) — точная сверка.
- **При нескольких подходящих** — берём с максимальным `ID` (свежайшая запись).
- **Fallback на старое поведение**: если глобальный listview-поиск падает
  (например, неожиданный 5xx от Visary), пробуем `GetShareAgreementsByRoomAsync`.
  Это сохраняет импорт работоспособным, даже если новый эндпоинт временно
  капризничает.
- **`PatchShareAgreementAsync` использует `forceUpdate=true`** (см.
  `CrudClient.cs:374-385`) — `ID`/`RowVersion` в теле опускаются, что
  избавляет от GET'а ради RowVersion.
- **Поле `Number` мы не меняем** на PATCH, но **передаём** — Visary иногда
  затирает Title до пустоты при partial patch; держим Number+Title парой.

---

## ❌ Типичные ошибки

```csharp
// ❌ НЕПРАВИЛЬНО: искать ДДУ только по комнате
var sas = await _listView.GetShareAgreementsByRoomAsync(roomId, saNumber, ct);
// orphan ДДУ (Room == null) не найдётся → создастся дубликат.
```

```csharp
// ❌ НЕПРАВИЛЬНО: фильтровать только по Number
var sas = await _listView.FindShareAgreementsAsync(saNumber, null, null, null, null, ct);
// Если в системе два ДДУ с одинаковым Number, но в разных проектах/этажах —
// привяжем не тот. Бизнес-ключ ВСЕГДА все 5 полей.
```

```csharp
// ❌ НЕПРАВИЛЬНО: пропустить локальный пост-фильтр
var match = found.Data.OrderByDescending(a => a.ID).First();
// Visary `contains` для VisaryRef матчит подстроку — "ID:1" поймает ID=10, ID=100.
```

```csharp
// ❌ НЕПРАВИЛЬНО: при reuse не передать Room/RoomID в PATCH
await _crud.PatchShareAgreementAsync(saMatch.ID, new ShareAgreementPatchRequest
{
    Site = new VisaryRef { ID = siteId },
    // Room не передан → ДДУ остаётся orphan'ным, проблема не решена.
});
```

---

## 📍 Применение в проекте

| Компонент | Файл | Назначение |
|-----------|------|------------|
| ListView find | `Visary.Api.Client/ListView/ListViewClient.cs` — `FindShareAgreementsAsync` | глобальный поиск по 5 полям |
| Filter helper | `Visary.Api.Client/ListView/ListViewClient.cs` — `FilterByRefIdContains` | `["RoomKindRef","contains","ID:{kid}"]` (использован в 75 и 76) |
| Patch DTO | `Visary.Api.Client/Dto/VisaryCrudRequests.cs` — `ShareAgreementPatchRequest` | расширен: Room/RoomID/RoomKindRef/ConditionalNumber/StageNumber/ProjectNumber |
| Импорт | `KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs` — блок 4 в `ApplyAsync` | Find → Reuse-or-Create flow с fallback |

---

## 🎯 Чек-лист «как протестировать»

- [ ] В Visary есть orphan ДДУ (Room=null) с известными Number/Kind/Cond/Stage/Project.
- [ ] Импорт строки помещения с теми же значениями.
- [ ] В логе backend появляется
      `RoomsForm.Apply: найден орфанный/несоответствующий ДДУ id=... — привязываем к roomId=...`
- [ ] В Visary ДДУ теперь привязан к новой комнате; дубликат **не создан**.
- [ ] Повторный импорт той же строки — никаких изменений (идемпотентность).
- [ ] При двух подходящих ДДУ в системе — привязывается тот, у которого `ID` больше.
