# 🏛️ WBS (ИСР) API клиент — фундамент для импорта бюджета

## 📋 Описание

**Статус**: 🟢 Фундамент v0.1 готов; встроено в `FinModelImportMapper` в v0.2 — см. [71-finmodel-budget-import.md](71-finmodel-budget-import.md).
**Дата**: 2026-05-07 (v0.1) → 2026-05-08 (v0.2 интеграция)

WBS (ИСР — иерархическая структура работ) в Visary — это **двухуровневая
самоссылающаяся структура** статей бюджета объекта строительства:

```
Глава 1. Стоимость земельного участка...        Code = "1."   ParentID = null
  ├── Затраты на приобретение прав на ЗУ        Code = "1.1." ParentID = ID главы
  ├── Договор освоении территории ...           Code = "1.2." ParentID = ID главы
  └── ...
Глава 2. Стоимость строительства                Code = "2."   ParentID = null
  ├── 2.1. Подготовительный период
  │     └── 2.1.1. Подготовка территории...     ← вложенность может быть >2 уровней
  └── ...
```

**Code (КБК)** присваивается **сервером Visary автоматически** на основе `ParentID` и
порядка создания. Под главой с Code `"1."` первая подстатья получит `"1.1."`, вторая
— `"1.2."`, и т. д. Клиент Code **не передаёт** при POST.

---

## 🏗️ Архитектура

```
Excel (Финмодель → лист Inputs → секция «Себестоимость»)
   │
   │  ┌─ Глава 1. Стоимость земельного участка...  (индикатор начала главы)
   │  │   ┌─ Затраты на приобретение прав на ЗУ + DeclaredSum / ConfirmedSum
   │  │   ├─ Договор освоения... + суммы
   │  │   └─ ...
   ▼
FinModelImportMapper  (планируется в v0.2 — пока не подключено)
   │
   ▼
ICrudClient.CreateWbsAsync  ←───  IListViewClient.GetWbsByProjectAsync
   │                                 (поиск существующей главы — чтобы не плодить)
   ▼
POST /api/visary/crud/wbs
   { ProjectID, Project: { ID }, ParentID?, Parent?: { ID },
     ConstructionSiteID?, ConstructionSite?: { ID },
     Title, DeclaredSum?, ConfirmedSum? }
   ▼
{ ID: 168019, Code: "1.1." }   ← сервер вернул присвоенный Code
```

### Маппинг кодов и названий

Эталонный список статей лежит в [Context/Бюджет_А4.1 (1) (1) (1) (1) (1).xlsx](../Context/) — **столбец A** = Code (КБК), **столбец B** = Title. Глав — 5 (1–5),
подстатей — около 100. При импорте маппер будет резолвить «название из файла →
ожидаемый Code», находить существующую главу/статью у проекта в Visary, и
создавать недостающие подстатьи под нужным родителем.

---

## ✅ Правильная реализация

### Мнемоника

```csharp
// Visary.Api.Client/Common/VisaryMnemonics.cs
public const string Wbs = "wbs";
```

### Создание главы (top-level)

```csharp
var chapter = await crud.CreateWbsAsync(new WbsCreateRequest
{
    ProjectID = projectId,
    Project   = new VisaryRef { ID = projectId },
    Title     = "Глава 1. Стоимость земельного участка и расходы по его содержанию",
    ParentID  = null,   // 👈 главы — top-level
    Parent    = null,
}, ct);
// chapter.Code == "1."  (присвоен сервером)
```

### Создание подстатьи

```csharp
var sub = await crud.CreateWbsAsync(new WbsCreateRequest
{
    ProjectID          = projectId,
    Project            = new VisaryRef { ID = projectId },
    ParentID           = chapter.ID,                 // 👈 ID главы
    Parent             = new VisaryRef { ID = chapter.ID },
    ConstructionSiteID = siteId,                     // привязка к ОКСу
    ConstructionSite   = new VisaryRef { ID = siteId },
    Title              = "Затраты на приобретение прав на ЗУ",
    DeclaredSum        = 438_000,
    ConfirmedSum       = 438_000,
}, ct);
// sub.Code == "1.1."  (если первая под главой), "1.2." и т. д.
```

### Поиск существующей главы

```csharp
var resp = await listView.GetWbsByProjectAsync(projectId, ct);

var chapter1 = resp.Data.FirstOrDefault(w =>
    w.ParentID is null &&
    (w.Code == "1." ||
     (w.Title?.Contains("Глава 1", StringComparison.OrdinalIgnoreCase) ?? false)));
```

### ⚠️ Важно

- **Не присылать `Code`** в `WbsCreateRequest` — Visary его игнорирует при POST
  и присваивает сам. Если бы DTO имел `Code` и слали `"1.1."` — это вызывало бы
  путаницу при чтении кода клиентом.
- **Дублировать `ProjectID` и `Project: { ID }`** — Visary ожидает оба поля
  в payload (см. реальный пример из DevTools пользователя). То же самое для
  `ParentID/Parent` и `ConstructionSiteID/ConstructionSite`.
- **`Parent = null` ≠ `ParentID = 0`**. Для глав в обоих полях должен быть `null`,
  иначе сервер попытается найти родителя с ID=0 и упадёт.
- **При повторном POST с теми же `ParentID` + `Title`** Visary **создаёт дубликат**
  (новая запись с инкрементированным Code, например `"1.2."`). Импорт обязан
  сначала вызывать `GetWbsByProjectAsync` и решать «находить или создавать».

---

## ❌ Типичные ошибки

### Ошибка 1: послать Code от себя

```csharp
// НЕПРАВИЛЬНО — Code присваивается сервером
var sub = await crud.CreateWbsAsync(new WbsCreateRequest
{
    ProjectID = projectId,
    ParentID  = chapter.ID,
    Title     = "Затраты на приобретение прав на ЗУ",
    Code      = "1.1.",   // ← в DTO такого поля нет, и не должно быть
});
```

### Ошибка 2: дублирование при повторном импорте

```csharp
// НЕПРАВИЛЬНО — каждый запуск импорта будет создавать новую копию
foreach (var article in articlesFromExcel)
    await crud.CreateWbsAsync(new WbsCreateRequest { Title = article.Title, ... });

// ПРАВИЛЬНО — сначала список существующих, потом diff
var existing = await listView.GetWbsByProjectAsync(projectId, ct);
foreach (var article in articlesFromExcel)
{
    var found = existing.Data.FirstOrDefault(w =>
        w.ParentID == chapter.ID &&
        string.Equals(w.Title?.Trim(), article.Title, StringComparison.OrdinalIgnoreCase));
    if (found is null)
        await crud.CreateWbsAsync(...);
    else
        // PATCH сумм или skip — по требованию
}
```

### Ошибка 3: главу — с ParentID родителя проекта

Главы (Code `"1."`, `"2."`, …) — это **top-level WBS** (`ParentID = null`),
а не дочерние записи `ConstructionProject`. Visary не имеет «корневой» WBS-записи
для проекта; иерархия ведётся через FK `ProjectID`.

---

## 📍 Применение в проекте

| Артефакт | Файл | Ключевые места |
|----------|------|----------------|
| Мнемоника | [VisaryMnemonics.cs](../Visary.Api.Client/Common/VisaryMnemonics.cs) | `Wbs = "wbs"` |
| DTO read | [VisaryEntities.cs](../Visary.Api.Client/Dto/VisaryEntities.cs) | `WbsRaw` (ID, Title, Code, ParentID, ProjectID, ConstructionSite, DeclaredSum, ConfirmedSum) |
| DTO create | [VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs) | `WbsCreateRequest` |
| CRUD | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | `CreateWbsAsync` (использует общий `PostCrudAsync<WbsRaw>`) |
| ListView | [ListViewClient.cs](../Visary.Api.Client/ListView/ListViewClient.cs) | `GetWbsByProjectAsync` через `listview/wbs/onetomany/ConstructionProject` + колонки `WbsColumns` |
| Live-тесты | [VisaryWbsLiveTests.cs](../KiloImportService.Api.Tests/VisaryLive/VisaryWbsLiveTests.cs) | 2 теста: read-only список + создание главы+подстатьи |

---

## 🧪 Smoke-тесты

| Тест | Тип | Что проверяет |
|------|-----|---------------|
| `GetWbsByProjectAsync_returns_data_for_known_project` | read-only | listview/wbs возвращает 200, JSON десериализуется в `WbsRaw`; логирует количество глав и их Code/Title |
| `CreateChapter1AndSubArticle_for_project_4584` | **write** (создаёт реальные записи!) | находит/создаёт `Глава 1`, затем создаёт подстатью «Затраты на приобретение прав на ЗУ» (DeclaredSum=ConfirmedSum=438 000) под ней; ассертит, что вернулся валидный ID |

Запуск:
```powershell
# Только read-only:
dotnet test --filter "FullyQualifiedName~GetWbsByProjectAsync_returns_data_for_known_project"

# С созданием (оставляет след в Visary — сознательно):
dotnet test --filter "FullyQualifiedName~CreateChapter1AndSubArticle_for_project_4584"
```

⚠️ **Каждый запуск write-теста создаёт новую подстатью** под Главой 1 (Code инкрементируется). Это не проблема для test-стенда, но не запускайте бездумно.

---

## 🎯 Что дальше — встройка в маппер

**v0.2 готова — закрыто 2026-05-08.** Подробности и ссылки на код:
[71-finmodel-budget-import.md](71-finmodel-budget-import.md). Краткое резюме:

- ✅ Парсинг секции «Себестоимость» на `Inputs` — `BudgetSectionHint` (StartMarker/EndMarkers/LastIncludedColumn).
- ✅ Парсинг блоков «Глава N» — `FinModelImportMapper.ValidateBudget` отслеживает `currentChapter`.
- ✅ Маппинг Title → Code — `BudgetReferenceProvider` (~100 статей, нормализация переносов/пробелов/регистра).
- ✅ Чтение `DeclaredSum`/`ConfirmedSum` — колонка E (одна сумма на статью), агрегация по этапам.
- ✅ Идемпотентность — `GetWbsByProjectAsync` + `EnsureChapterAsync` + `UpsertArticleAsync`; `PatchWbsAsync` (forceUpdate=true).
- ✅ Политика глав — автосоздание недостающих (главы без `ConstructionSiteID`); подстатьи привязываются к ОКСу.
- 🟡 Открытые хвосты v0.3+: расширение Title-эталона под Главу 2 (СМР), удаление лишних статей при повторном импорте, fuzzy-match Title.

---

## 🔗 Связанные документы

- [22-update-finishing-material.md](22-update-finishing-material.md) — паттерн PATCH через CRUD по `RowVersion` (для будущего PATCH WBS)
- [50-visary-api-new-methods.md](50-visary-api-new-methods.md) — реестр API-методов клиента (туда же добавится `CreateWbsAsync` / `GetWbsByProjectAsync`)
- [55-visary-proxy-controllers.md](55-visary-proxy-controllers.md) — фронтовый прокси `/api/visary/*`
- [57-visary-api-testing.md](57-visary-api-testing.md) — три уровня тестов; live с автоskip при истёкшем токене
- [66-finmodel-estate-class.md](66-finmodel-estate-class.md) / [67-finmodel-indicators.md](67-finmodel-indicators.md) / [69-finmodel-address.md](69-finmodel-address.md) — другие параметры Финмодели; бюджет станет четвёртым типом
