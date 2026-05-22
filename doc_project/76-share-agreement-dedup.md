# 🔁 Дедупликация ДДУ при импорте «Помещения»

> **v1.1 (2026-05-22)** — добавлен **loose-fallback** для orphan-ДДУ. Раньше глобальный
> поиск шёл только строгим фильтром по 5-полям (`Number+Kind+Cond+Stage+Project`). Orphan
> ДДУ — записи с пустыми `Stage`/`Project`/`Room` — серверным `=`-фильтром Visary отсекались,
> повторный импорт плодил новый дубликат рядом с орфаном. Третья ступень снимает Stage/Project,
> но принимает **только** строки, у которых Room не указывает на реальное помещение
> (anti-pattern #2 ниже).
>
> **v1.2 (2026-05-22)** — расширён orphan-фильтр: `a.Room is null || a.Room.ID <= 0`. Visary
> сериализует «нет связи с помещением» как `{"Room": {"ID": 0, "Title": ""}}` (а не `null`),
> а `VisaryRef.ID` — non-nullable int (дефолт 0) → проверка `a.Room?.ID is null` ловила только
> чистый JSON-null и пропускала реальные orphan'ы из Visary. Симптом: повторный импорт после
> ручного «отвязать ДДУ от помещения» в Visary UI всё равно создавал дубликат. Добавлен
> `_log.LogInformation` в loose-find, который показывает, **что именно** Visary вернул и какие
> `Room.ID` у кандидатов — без этой диагностики симптом «ничего не нашлось» не отличишь от
> «нашлось, но всё отфильтровано как non-orphan».

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

### RoomsFormImportMapper — flow (v1.1 — три ступени)

```csharp
// Блок 4 в ApplyAsync — find / reuse / create
//
// Ступень 1 — per-room search (doc 86): дешёво, ловит уже-привязанные ДДУ.
var byRoom = await _listView.GetShareAgreementsByRoomAsync(roomId, null, ct);
saMatch = byRoom.Data.FirstOrDefault(a => /* Number+Trim match */);

// Ступень 2 — глобальный STRICT по 5-полям. Дедуп с тем же Project+Stage:
// безопасен от «угона» из соседнего проекта.
if (saMatch is null)
{
    var foundStrict = await _listView.FindShareAgreementsAsync(
        saNumber, kindId, roomNumber, stageNumberForSa, projectNumberForSa, ct);
    saMatch = foundStrict.Data
        .Where(/* Number + Kind */)
        .OrderByDescending(a => a.ID)
        .FirstOrDefault();

    // Ступень 3 — глобальный LOOSE без Stage/Project, но ТОЛЬКО orphan (Room == null).
    // Зачем: ДДУ может существовать в Visary до загрузки помещений (заведена вручную
    // или отвязана системно). У такого orphan'а Stage/Project NULL, и Visary `=`-фильтр
    // его отсекает. Без 3-й ступени каждый импорт создаёт дубликат, а orphan остаётся
    // невидимым.
    if (saMatch is null)
    {
        var foundLoose = await _listView.FindShareAgreementsAsync(
            saNumber, kindId, roomNumber,
            stageNumber: null, projectNumber: null, ct);
        saMatch = foundLoose.Data
            .Where(/* Number + Kind */)
            .Where(a => a.Room is null || a.Room.ID <= 0)   // ← orphan-only (v1.2)
            .OrderByDescending(a => a.ID)
            .FirstOrDefault();
    }
}

if (saMatch is null)
{
    await _crud.CreateShareAgreementAsync(new ShareAgreementCreateRequest { /* ... */ }, ct);
}
else
{
    // Нашли — PATCH'им на текущую комнату (даже если saMatch уже был привязан
    // к другой Room или Room=null). Это «реанимация» orphan-ДДУ.
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
// ❌ НЕПРАВИЛЬНО: фильтровать только по Number/Cond+Kind БЕЗ orphan-only пост-фильтра
var sas = await _listView.FindShareAgreementsAsync(saNumber, kindId, roomNumber, null, null, ct);
var match = sas.Data.OrderByDescending(a => a.ID).First();
// Если в системе два ДДУ с одинаковыми Number+Cond+Kind в разных проектах/этапах,
// возьмём не тот — «угоним» чужую запись (saMatch.Room != null, привязан к чужой комнате).
// Loose-find на это случай ОБЯЗАН фильтровать `.Where(a => a.Room?.ID is null)` —
// только орфаны безопасно реанимировать. См. v1.1 выше.
```

```csharp
// ❌ НЕПРАВИЛЬНО: strict-only поиск без loose-fallback'а
var found = await _listView.FindShareAgreementsAsync(
    saNumber, kindId, roomNumber, stageNumberForSa, projectNumberForSa, ct);
// Если в системе живёт orphan-ДДУ (Stage=NULL, Project=NULL) с тем же Number/Cond/Kind —
// строгий Visary `=` его не вернёт (NULL не равен запрошенной строке). Каждый
// импорт создаст рядом новый ДДУ-дубликат, orphan останется невидимым.
// Лечение: после strict-miss'а сделать loose-find без Stage/Project + orphan-only filter.
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

- [ ] В Visary есть orphan ДДУ (Room=null, Project=null, Stage=null) с известными Number/Kind/Cond.
- [ ] Импорт строки помещения с теми же Number+Cond+Kind.
- [ ] В логе backend появляется
      `RoomsForm.Apply: найден орфанный/несоответствующий ДДУ id=... — привязываем к roomId=...`
- [ ] В Visary ДДУ теперь привязан к новой комнате; дубликат **не создан**.
- [ ] Повторный импорт той же строки — никаких изменений (идемпотентность).
- [ ] При двух подходящих ДДУ в системе — привязывается тот, у которого `ID` больше.
- [ ] **Safety:** если в Visary есть ДДУ с теми же Number+Cond+Kind, но привязанный к ЧУЖОЙ
      комнате (другой проект/этап) — он НЕ должен быть «угнан»; импорт создаст новый ДДУ.
      Покрыто тестом `ApplyAsync_LooseFind_SkipsNonOrphan_DoesNotStealFromAnotherRoom`.

## 📍 Тесты (doc 76 v1.1 + v1.2)

| Тест | Проверяет |
|---|---|
| `ApplyAsync_OrphanShareAgreement_IsReusedAndRelinked_NotDuplicated` (v1.1) | Strict-find возвращает 0, loose-find возвращает orphan c `Room = null` → mapper делает `PatchShareAgreementAsync(orphanId, …)` с заполненными Room/Project/Site/Stage; `CreateShareAgreementAsync` НЕ вызывается; snapshot держит orphan ID |
| `ApplyAsync_OrphanShareAgreement_WithZeroRoomId_IsAlsoTreatedAsOrphan` (v1.2) | Visary шлёт `{"Room": {"ID": 0, "Title": ""}}` для отвязанного помещения (а не JSON-null). Фильтр `Room is null \|\| Room.ID <= 0` ловит оба формата → orphan реанимируется, дубликат не создаётся |
| `ApplyAsync_LooseFind_SkipsNonOrphan_DoesNotStealFromAnotherRoom` (v1.1) | Loose-find отдаёт строку с `Room != null` (ID=88888) → orphan-only фильтр её отсекает; mapper создаёт новый ДДУ, PATCH чужой записи НЕ делает |
