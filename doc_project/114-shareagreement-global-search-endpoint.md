# 🔁 ДДУ: глобальный поиск — отдельный эндпоинт `listview/shareagreementall`

## 📋 Описание

Глобальный AND-поиск ДДУ по бизнес-ключу (`Number` / `RoomKindRef` / `ConditionalNumber` / `StageNumber` / `ProjectNumber`) теперь идёт на отдельный listview-эндпоинт `POST /api/visary/listview/shareagreementall`, а не на «обычный» `listview/shareagreement`.

Per-room-листинг (`onetomany/Room`) и весь CRUD остались на исходной мнемонике `shareagreement`.

**Контекст**: эндпоинт со «всеми» ДДУ — это **серверный** контракт Visary для поиска orphan-ДДУ через границы Site/Project/Stage (см. [doc 76](./76-share-agreement-dedup.md) v1.1 — loose-find без `Stage`/`Project` + orphan-only-фильтр). Обычный `listview/shareagreement` ограничен видимостью текущего сайта; чтобы найти orphan-ДДУ с `Stage=NULL/Project=NULL` для реанимации, нужен `*all`-вариант.

---

## ✅ Правильная реализация

[Visary.Api.Client/ListView/ListViewClient.cs:917-919](../Visary.Api.Client/ListView/ListViewClient.cs#L917-L919)

```csharp
public Task<ListViewResponse<ShareAgreementRaw>> FindShareAgreementsAsync(
    string? number, int? roomKindId, string? conditionalNumber,
    string? stageNumber, string? projectNumber, CancellationToken ct)
{
    // …собираем AND-фильтр…
    var body = new
    {
        Mnemonic = VisaryMnemonics.ShareAgreement,   // 👈 в payload остаётся "shareagreement"
        // …
    };

    return PostListViewAsync<ShareAgreementRaw>(
        $"{BaseUrl}/api/visary/listview/{VisaryMnemonics.ShareAgreement}all",  // 👈 URL: shareagreement + "all"
        body, $"{VisaryMnemonics.ShareAgreement}all find", ct);
}
```

### ⚠️ Важно

- **URL и `Mnemonic` в теле — рассогласованы намеренно**: URL = `shareagreementall`, `body.Mnemonic = "shareagreement"`. `*all` — это маршрут, не мнемоника сущности; DTO/CRUD/onetomany продолжают жить под `shareagreement`.
- `*all`-суффикс зашит **inline** в одной строке. Константу в [VisaryMnemonics.cs](../Visary.Api.Client/Common/VisaryMnemonics.cs) **не** добавляем — это не сущность.
- Меняется **только** `FindShareAgreementsAsync`. `GetShareAgreementsByRoomAsync` (onetomany/Room) и весь CRUD-блок (`POST/PATCH/GET /crud/shareagreement[/{id}]`) — без изменений.

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — заменить константу мнемоники целиком
public const string ShareAgreement = "shareagreementall";  // ✗ сломает CRUD + onetomany/Room
```

Мнемоника `shareagreement` участвует ещё в 5 местах (CREATE/PATCH/GET CRUD, onetomany/Room-listview, manytomany-link'и); их сервер по-прежнему обслуживает под `shareagreement`. Глобальный `*-all` — это **изолированное расширение маршрута listview**.

```csharp
// НЕПРАВИЛЬНО — поменять и URL, и Mnemonic в теле
var body = new { Mnemonic = "shareagreementall", ... };  // ✗ 400
```

Visary валидирует `Mnemonic` в теле против справочника сущностей; `shareagreementall` там нет → ошибка.

```csharp
// НЕПРАВИЛЬНО — пустить per-room поиск на *all
GetShareAgreementsByRoomAsync → /listview/shareagreementall/onetomany/Room  // ✗
```

`onetomany/Room` не работает на `*all`-маршруте (тот — flat global list, без ассоциаций). Сценарий «ДДУ у этой Room» закрыт обычным `shareagreement/onetomany/Room`.

---

## 📍 Применение в проекте

| Сценарий | Метод-обёртка | Visary endpoint |
|----------|---------------|-----------------|
| Глобальный поиск ДДУ по бизнес-ключу (dedup, реанимация orphan'ов) | `FindShareAgreementsAsync` | `POST /listview/shareagreementall` |
| Все ДДУ конкретной комнаты | `GetShareAgreementsByRoomAsync` | `POST /listview/shareagreement/onetomany/Room?associationId={roomId}` |
| Создание | `CreateShareAgreementAsync` | `POST /crud/shareagreement` |
| Обновление | `PatchShareAgreementAsync` | `PATCH /crud/shareagreement/{id}?forceUpdate=true` |
| Чтение по ID | `GetShareAgreementByIdAsync` | `GET /crud/shareagreement/{id}` |

Основной потребитель `FindShareAgreementsAsync` — [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs):
- двухступенчатый dedup ДДУ ([doc 86](./86-rooms-dedup-pre-check.md)): сначала per-room через `onetomany/Room`, затем глобально через `*all` для orphan-кейсов;
- 3-й шаг loose-find без `Stage`/`Project` + orphan-only-фильтр ([doc 76](./76-share-agreement-dedup.md) v1.1) — единственный способ найти orphan-ДДУ с NULL-стадией.

---

## 🎯 Чек-лист

- [ ] При добавлении новых параметров AND-фильтра — расширять `FindShareAgreementsAsync`, не плодить параллельные методы
- [ ] При добавлении нового глобального поискового сценария на `shareagreement` — использовать тот же `*all`-маршрут, не путать с `listview/shareagreement`
- [ ] Не вводить `ShareAgreementAll` в `VisaryMnemonics` — `*all` это маршрут, не сущность
- [ ] Контракт-тесты на URL: pin'ить в строке-литерале именно `/listview/shareagreementall`, чтобы регрессия откатом «`all`» ловилась тестом
