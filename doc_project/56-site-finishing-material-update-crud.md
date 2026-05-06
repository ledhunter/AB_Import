# 🔌 Обновление типа отделки через CRUD endpoint (PATCH вместо PUT/listview)

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-06
**Симптом**: при Apply-стадии импорта «Финмодель» Visary возвращал сначала
`405 Method Not Allowed`, потом `500 Internal Server Error`.

`UpdateSiteFinishingMaterialAsync` исторически делал `PUT /api/visary/listview/constructionsite`
с body `{Mnemonic, Data:[…]}`. Listview-эндпоинт принимает только `POST` (это
поисковый ресурс) → 405. Под `forceUpdate=true` через `/crud/` Visary падал с
`Property RowVersion already exists` — она пыталась добавить мои поля к
загруженному JObject записи, в котором уже было `RowVersion`.

> 🔁 См. также: `22-update-finishing-material.md` (frontend-аналог), `51-sites-sync-bugs-and-token-update.md`.

---

## ✅ Правильная реализация

### Алгоритм

```
1. GET   /api/visary/crud/constructionsite/{id}                       → читаем актуальный RowVersion (long)
2. PATCH /api/visary/crud/constructionsite/{id}?forceUpdate=false     с body { ID, RowVersion, FinishingMaterial: { ID } }
```

### Код

```csharp
// Visary.Api.Client/CRUD/CrudClient.cs
public async Task<bool> UpdateSiteFinishingMaterialAsync(
    int siteId, int finishingMaterialId, CancellationToken ct)
{
    // 1. GET → RowVersion (optimistic locking).
    //    Listview возвращает Version:DateTime, а CRUD — RowVersion:long в нужном формате.
    //    Используем переиспользуемый GetCrudByIdAsync<T> из VisaryHttpBase
    //    (тот же, что и для всех остальных Get*ByIdAsync-методов клиента).
    var current = await GetCrudByIdAsync<ConstructionSiteFull>(
        VisaryMnemonics.Site, siteId, ct);
    if (current is null)
        throw new KeyNotFoundException($"ConstructionSite с ID={siteId} не найден в Visary");

    // 2. PATCH с RowVersion + FinishingMaterial как VisaryRef ({ ID }).
    var body = new
    {
        ID = siteId,
        current.RowVersion,
        FinishingMaterial = new { ID = finishingMaterialId },
    };
    await PatchCrudAsync(
        $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Site}/{siteId}?forceUpdate=false",
        body, $"{VisaryMnemonics.Site}/{siteId}", ct);

    return true;
}
```

**Никакого собственного `GetCrudAsync<T>` или приватного `SiteCrudReadResponse` DTO не нужно** — `GetCrudByIdAsync<T>` живёт в [`Visary.Api.Client/Common/VisaryHttpBase.cs`](../Visary.Api.Client/Common/VisaryHttpBase.cs) и используется во **всех** GET-by-id методах клиента (`GetSiteByIdFullAsync`, `GetProjectByIdFullAsync`, `GetFinishingMaterialByIdAsync`, …). У `ConstructionSiteFull` уже есть `RowVersion: long` — auto-generated DTO в `Dto/Generated/ConstructionSiteFull.cs`.

### ⚠️ Важно

- **Используй `forceUpdate=false`, не `true`.** Под `forceUpdate=true` Visary внутри пытается «дописать» наши поля в JObject загруженной из БД записи (где `RowVersion` уже есть) и падает с `Property with the same name already exists`. С `false` сервер делает обычный optimistic-update: сравнивает наш `RowVersion` с актуальным.
- **Listview ≠ CRUD.** `/api/visary/listview/{mnemonic}` — поисковый POST. Для GET/CREATE/UPDATE одной записи — `/api/visary/crud/{mnemonic}/{id}` (GET / PATCH / POST).
- **`Version: DateTime` ≠ `RowVersion: long`.** Listview-ответ отдаёт `Version` как datetime — для PATCH'а он не подходит. Только GET через `/crud/` возвращает `RowVersion` в формате, ожидаемом PATCH'ем.
- **Связи (FK) передаём как `VisaryRef`-объект `{ ID }`**, а не как плоское `FinishingMaterialId: 3`. Visary CRUD-API ожидает именно вложенный объект-ссылку.
- **GET → 404 → KeyNotFoundException → row-level error в маппере.** В `FinModelImportMapper.ApplyAsync` ловим и кладём `visary_site_not_found`.

---

## ❌ Типичная ошибка

### 1. PUT/listview вместо PATCH/crud

```csharp
// ❌ Listview не принимает PUT
using var req = NewRequest(HttpMethod.Put,
    $"{BaseUrl}/api/visary/listview/constructionsite");
req.Content = JsonContent.Create(new { Mnemonic = "constructionsite", Data = new[] { siteData } });
// → 405 Method Not Allowed
```

### 2. PATCH с `forceUpdate=true`

```csharp
// ❌ forceUpdate=true триггерит JObject.Add() в Visary
$"{BaseUrl}/api/visary/crud/constructionsite/{siteId}?forceUpdate=true"
// → 500 {"Error":"Can not add property RowVersion to ... Property with the same name already exists"}
```

### 3. Передавать FK как плоский int

```csharp
// ❌ Плоский int — Visary CRUD не понимает
var body = new { ID = siteId, RowVersion = 12345, FinishingMaterialId = 3 };
// FinishingMaterial не обновится — Visary молча игнорирует поле
```

```csharp
// ✅ Вложенный VisaryRef
var body = new { ID = siteId, RowVersion = 12345, FinishingMaterial = new { ID = 3 } };
```

### 4. Использовать `Version: DateTime` из listview как `RowVersion: long`

```csharp
// ❌ Кросс-формат — Visary вернёт 409 или вообще выкинет фейк-RowVersion
var siteData = await ListView.GetSiteByIdAsync(siteId, ct);   // Version: DateTime
var body = new { ID = siteId, RowVersion = siteData.Version.Value.Ticks, ... };
```

---

## 📍 Применение в проекте

| Компонент | Файл | Что делает |
|-----------|------|-----------|
| `UpdateSiteFinishingMaterialAsync` | [Visary.Api.Client/CRUD/CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | GET RowVersion → PATCH с `FinishingMaterial.ID` |
| `GetCrudByIdAsync<T>` helper | [Visary.Api.Client/Common/VisaryHttpBase.cs](../Visary.Api.Client/Common/VisaryHttpBase.cs) | Общий generic GET для всех `/crud/{mnemonic}/{id}` |
| `ConstructionSiteFull` (auto-generated) | [Visary.Api.Client/Dto/Generated/ConstructionSiteFull.cs](../Visary.Api.Client/Dto/Generated/ConstructionSiteFull.cs) | `int ID`, `long RowVersion`, плюс остальные поля (нам нужны только эти два) |
| `VisaryMnemonics.Site` | [Visary.Api.Client/Common/VisaryMnemonics.cs](../Visary.Api.Client/Common/VisaryMnemonics.cs) | Константа `"constructionsite"` |
| `PatchSiteAsync` (для других полей) | там же в `CrudClient.cs` | Тот же паттерн через `SitePatchRequest` |
| Потребитель | [Domain/Mapping/FinModelImportMapper.ApplyAsync](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | Вызывает `_visaryClient.UpdateSiteFinishingMaterialAsync(siteId, materialId, ct)` |

### Удалено

- `LegacyUpdateSiteAsync` (PUT/listview) — мёртв.
- `FetchSiteForUpdateAsync` (POST/listview для чтения Version) — заменён на общий `GetCrudByIdAsync<ConstructionSiteFull>`.
- `private class SiteUpdateData { ID, FinishingMaterialId, Version:DateTime }` — больше не нужен.
- `private class SiteCrudReadResponse { ID, RowVersion }` (была в первой версии фикса до merge) — заменена `ConstructionSiteFull`.

---

## 🎯 Чек-лист (при добавлении нового update для Visary-сущности)

- [ ] **GET** `/api/visary/crud/{mnemonic}/{id}` → читаем `RowVersion: long`. Не используем listview для чтения «под обновление».
- [ ] **PATCH** `/api/visary/crud/{mnemonic}/{id}?forceUpdate=false` с body `{ ID, RowVersion, ...поля }`.
- [ ] FK-поля передаём как `{ID}`-объект (`VisaryRef`), не как плоский int.
- [ ] HTTP-клиент инжектится через DI (`IHttpClientFactory`); никаких `new HttpClient()` в сервисах (см. `51-sites-sync-bugs-and-token-update.md`).
- [ ] 404 при GET → `KeyNotFoundException` → row-level/file-level error в маппере с понятным сообщением.
- [ ] 409 Conflict при PATCH (устаревший RowVersion) ловится `HandleConflict` и тоже превращается в маппер-ошибку.

---

**Версия**: 1.0
**Дата**: 2026-05-06
