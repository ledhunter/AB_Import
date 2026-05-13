# 💰 Импорт «Финмодель → Себестоимость» (бюджет ОКСу, WBS v0.2)

> ⚠️ **Статус на 2026-05-13: CRUD-путь импорта бюджета ОТКЛЮЧЁН.**
> Visary не поддерживает запрос существующего WBS-списка проекта (500 на
> `listview/wbs/onetomany/ConstructionProject`), а полное дерево
> `ProjectRoot → SiteRoot → Глава → Подстатья` сложно поднимать CRUD-ом.
> Бюджет теперь **выгружается отдельным XLSX по эталонному шаблону «Бюджет_А4.1»**
> и импортируется в Visary вручную. См. **[78-budget-xlsx-export.md](78-budget-xlsx-export.md)**.
> Парсер секции «Себестоимость», `BudgetReferenceProvider`, `BudgetSectionHint`,
> агрегация по `(ChapterCode, ArticleCode)` — всё это **продолжает использоваться**:
> mapped budget rows записываются в `staged_rows` и оттуда читаются экспортером.
> Отключён только финальный шаг `ApplyBudgetAsync` (CRUD к Visary WBS).

## 📋 Описание

**Статус**: 🟡 Парсер активен, apply через CRUD отключён (см. [78-budget-xlsx-export.md](78-budget-xlsx-export.md)).
**Дата**: 2026-05-08 (v0.2) → 2026-05-13 (apply заменён на XLSX-экспорт)
**Связано с**: [70-wbs-api-foundation.md](70-wbs-api-foundation.md) (фундамент клиента WBS, тоже архивирован).

При импорте «Финмодели» из секции **«Себестоимость»** листа `Inputs` теперь
создаются (или обновляются) главы и подстатьи бюджета (WBS / ИСР) объекта
строительства в Visary. Title из Excel резолвится в Code (КБК) через эталонный
справочник, суммы подстатьи `DeclaredSum`/`ConfirmedSum` копируются из колонки `E`.
Повторный импорт **не плодит дубликаты** — суммы PATCH-аются у существующих записей.

---

## 🏗️ Архитектура

```
Excel (FinModel/Параметры к переносу в АБ.xlsx → Inputs → ниже маркера «Себестоимость»)
   │
   │  ┌─ Глава 1. Стоимость земельного участка...
   │  │   ┌─ Этап 1
   │  │   │   ├─ Затраты на приобретение прав на ЗУ + сумма (E=438 000)
   │  │   │   ├─ Затраты на изменение ВРИ ...
   │  │   │   └─ Итого
   │  │   ├─ Этап 2
   │  │   └─ ...
   │  └─ Глава 2. Стоимость СМР
   ▼
XlsxParser (KeyValueVertical + BudgetSectionHint)
   │   эмитит ParsedRow со специальным Sheet-суффиксом «(budget)»
   ▼
FinModelImportMapper.ValidateBudget
   │   • walkthrough в порядке строк
   │   • track currentChapter (через BudgetReferenceProvider.FindByTitle)
   │   • aggregate (chapter, article) → сумма по этапам
   │   • emit MappedRow Kind="budget"
   ▼
FinModelImportMapper.ApplyBudgetAsync
   │   • GetWbsByProjectAsync (один раз) — список существующих WBS проекта
   │   • EnsureChapterAsync — find by Code, иначе CreateWbsAsync (ParentID=null)
   │   • UpsertArticleAsync per подстатью:
   │      ├─ есть в Visary && суммы совпадают → no-op
   │      ├─ есть в Visary && суммы изменились → PatchWbsAsync
   │      └─ нет → CreateWbsAsync (ParentID=ID главы, ConstructionSiteID=siteId)
   ▼
Visary CRUD (POST + PATCH)  +  Visary ListView (GET)
```

---

## ✅ Правильная реализация

### 1. Подключение бюджетной секции в layout-hint

```csharp
// FinModelImportMapper.cs
public FileLayoutHint LayoutHint { get; } = new KeyValueVertical(
    SheetName: "Inputs",
    KeyColumn: "C",
    ValueStartColumn: "H",
    StageCount: new StageCountReference(...),
    Budget: new BudgetSectionHint(    // 👈 новое
        MarkerColumn: "C",
        StartMarker: "Себестоимость",
        EndMarkers: ["Историческая фин. отчетность", "Бухгалтерский баланс", ...],
        LastIncludedColumn: "G"));
```

### 2. Эмиссия бюджетных строк парсером

`XlsxParser.ExtractBudgetSection` идёт по листу от строки `StartMarker` до первой
строки с любым из `EndMarkers` и эмитит `ParsedRow` с буквенными ключами
(`A`,`B`,`C`,`D`,`E`,`F`,`G`) и `Sheet = "Inputs (budget)"`. Маппер отличает их
от обычных стадийных строк по этому суффиксу:

```csharp
private static bool IsBudgetRow(ParsedRow row)
    => row.Sheet?.EndsWith("(budget)", StringComparison.Ordinal) == true;
```

### 3. Резолвинг Title → Code

Эталонный справочник статей зашит в [BudgetReferenceProvider](../KiloImportService.Api/Domain/Mapping/Budget/BudgetReferenceProvider.cs)
как массив `(string Code, string Title)[]` (~100 строк, выгрузка из
`Context/Бюджет_А4.1.xlsx`). Title нормализуется: lower-case + схлопывание
любых пробелов/переносов/табов в один пробел. Это решает проблему
многострочных заголовков из Excel:

```csharp
// "Затраты на\nприобретение  прав\tна ЗУ" → "затраты на приобретение прав на зу"
var entry = budgetRef.FindByTitle(rawTitle);
```

### 4. Агрегация по этапам

Подстатья в Excel может встречаться в каждом «Этапе» (1, 2, 3, …). Маппер
суммирует значения колонки `E` всех её вхождений в пределах одной главы:

```csharp
var key = $"{chapter.Code}|{entry.Code}";
if (aggregated.TryGetValue(key, out var bucket))
    bucket.Sum += sum;
else
    aggregated[key] = new BudgetAggregateBucket(chapter, entry, sum, row.SourceRowNumber);
```

### 5. Идемпотентный apply (find/create/patch)

```csharp
// 1) Один батч-запрос — список ВСЕХ WBS-записей проекта.
var existing = await _listViewClient.GetWbsByProjectAsync(projectId, ct);

// 2) Глава: найти по Code "1." или Title; если нет — создать.
var chapter = FindChapter(existing.Data, chapterTitle, chapterCode)
              ?? await _visaryClient.CreateWbsAsync(new WbsCreateRequest
                 { ProjectID = projectId, Title = chapterTitle, ParentID = null }, ct);

// 3) Подстатья: per article — find by ParentID + Title.
var match = existing.Data.FirstOrDefault(w =>
    w.ParentID == chapterId
    && BudgetReferenceEntry.NormalizeTitle(w.Title) == titleNorm);

if (match is null)
    await _visaryClient.CreateWbsAsync(new WbsCreateRequest { ... });          // CREATE
else if (NearlyEqual(match.DeclaredSum, declared)
      && NearlyEqual(match.ConfirmedSum, confirmed))
    /* no-op — суммы уже совпадают */;                                         // SKIP
else
    await _visaryClient.PatchWbsAsync(match.ID, new WbsPatchRequest            // PATCH
        { DeclaredSum = declared, ConfirmedSum = confirmed }, ct);
```

### 6. PatchWbsAsync — `forceUpdate=true` (как Room/ShareAgreement)

```csharp
// Visary.Api.Client/CRUD/CrudClient.cs
public Task<bool> PatchWbsAsync(int wbsId, WbsPatchRequest request, CancellationToken ct)
{
    request.ID = null;          // 👈 forceUpdate=true ⇒ убираем из тела
    request.RowVersion = null;  //    (иначе 500 «Can not add property RowVersion to JObject»)
    return PatchAndReportAsync(
        $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Wbs}/{wbsId}?forceUpdate=true", ...);
}
```

### ⚠️ Важно

- **Бюджет привязывается к проекту, не к ОКСу**. Главы и иерархия WBS живут на
  уровне `ConstructionProject`. ОКС указывается у подстатьи через
  `ConstructionSiteID`/`ConstructionSite`. Если у Site нет `ConstructionProjectId`
  в локальном зеркале и `VisaryProjectId` не передан в `ImportContext` — apply
  бюджета вернёт ошибку `project_required` (параметрический поток при этом
  отрабатывает нормально).

- **Не смешивайте Excel-главу и Visary-главу по Title**. В Visary сначала
  ищем по `Code` (`"1."`), и только если код ещё не присвоен — fallback по
  нормализованному Title. Это страхует от ситуации, когда пользователь
  переименовал главу в Visary.

- **`forceUpdate=true` в PATCH WBS** — сознательный компромисс. Альтернатива
  (`forceUpdate=false` + GET ради `RowVersion`) даёт защиту от concurrent
  обновлений, но требует двух round-trip'ов на каждую подстатью. При импорте
  бюджета (десятки подстатей) это удваивает время и нагрузку на стенд.

- **No-op при совпадении сумм**. Если в Visary уже стоят те же `DeclaredSum`
  и `ConfirmedSum` — PATCH не вызывается (избегаем фантомных событий ROW_VERSION
  bump-а в Visary и шумовых записей в audit log). Сравнение через `NearlyEqual`
  с допуском 0.005 (Visary хранит суммы с 2 знаками).

---

## ❌ Типичные ошибки

### Ошибка 1: пытаться парсить бюджет из стадий H+

```csharp
// НЕПРАВИЛЬНО — суммы бюджета лежат в столбце E, а не в столбцах-этапах H/I/J/K.
// Если попытаться читать DeclaredSum как "стадия N", получишь 0 (там пусто).
var declared = row.Cells["H"];   // ← всегда пусто для бюджетных строк
```

**Правильно**: `BudgetSectionHint` эмитит отдельные ParsedRow с буквенными
ключами; бюджетный поток читает `Cells["E"]`.

### Ошибка 2: матчить Title как есть, без нормализации

```csharp
// НЕПРАВИЛЬНО — Excel хранит "Затраты на изменение ВРИ, комплексное развитие\nзастроенной..."
// с переводом строки. Точное равенство со справочником "Затраты на изменение ВРИ" не сработает.
ref.FirstOrDefault(e => e.Title == row.Cells["C"]);
```

**Правильно**: `BudgetReferenceEntry.NormalizeTitle` приводит обе стороны к
форме «один пробел вместо любого whitespace + lower-case + Trim».

### Ошибка 3: каждый импорт создаёт новые подстатьи

```csharp
// НЕПРАВИЛЬНО — без предварительной проверки existing-списка повторный импорт
// получит 1.1., 1.2., 1.3., 1.4. для одного и того же «Затраты на приобретение прав на ЗУ».
foreach (var article in budgetArticles)
    await crud.CreateWbsAsync(new WbsCreateRequest { Title = article.Title, ... });
```

**Правильно**: один `GetWbsByProjectAsync` в начале apply, затем per-article
find-or-(create|patch). См. `ApplyBudgetAsync` / `UpsertArticleAsync`.

### Ошибка 4: класть Code в `WbsPatchRequest`

```csharp
// НЕПРАВИЛЬНО — в WbsPatchRequest вообще нет поля Code (Visary назначает сам).
// Если бы было — попытка PATCH с "новым" Code привела бы к рассинхрону кода и порядка.
new WbsPatchRequest { Code = "1.5.", DeclaredSum = 200 };
```

**Правильно**: `WbsPatchRequest` содержит только редактируемые поля
(`Title`, `DeclaredSum`, `ConfirmedSum`). `ID`/`RowVersion` тоже nullable и
обнуляются перед сериализацией (`forceUpdate=true`).

---

## 📍 Применение в проекте

| Артефакт | Файл | Ключевые места |
|----------|------|----------------|
| DTO PATCH | [VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs) | `WbsPatchRequest` — nullable `ID`/`RowVersion` + `Title`/`DeclaredSum`/`ConfirmedSum` |
| CRUD | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | `PatchWbsAsync` (forceUpdate=true), `CreateWbsAsync` |
| Layout-hint | [FileLayoutHint.cs](../KiloImportService.Api/Domain/Importing/FileLayoutHint.cs) | `BudgetSectionHint` (StartMarker/EndMarkers/LastIncludedColumn) |
| Парсер | [XlsxParser.cs](../KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs) | `ExtractBudgetSection` |
| Эталон Title→Code | [BudgetReferenceProvider.cs](../KiloImportService.Api/Domain/Mapping/Budget/BudgetReferenceProvider.cs) | `RawData[]` (~100 статей), `FindByTitle` / `FindByCode`, `NormalizeTitle` |
| Маппер Validate | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `ValidateBudget`, `ResolveChapterFor`, `IsBudgetSkipLine` |
| Маппер Apply | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `ApplyBudgetAsync`, `EnsureChapterAsync`, `UpsertArticleAsync`, `NearlyEqual` |
| DI | [Program.cs](../KiloImportService.Api/Program.cs) | `IBudgetReferenceProvider` → singleton |
| Unit-тесты | [FinModelBudgetTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelBudgetTests.cs) | 7 тестов: aggregate, unknown-skip, create/patch/no-op/reuse, project_required |
| Live-write | [VisaryWbsLiveTests.cs](../KiloImportService.Api.Tests/VisaryLive/VisaryWbsLiveTests.cs) | `BudgetUpsert_IsIdempotent_OnRepeatedRun` — после 2-го прогона дубликатов нет |

---

## 🧪 Тесты

| Тест | Что проверяет |
|------|---------------|
| `ValidateAsync_BudgetRowsAggregateAcrossStages` | Этап 1+Этап 2 → одна mapped-строка с суммой 438 (300+138). |
| `ValidateAsync_BudgetRows_UnknownTitle_SkippedSilently` | «Прочие затраты» (нет в справочнике) → silent skip, file-level errors=0. |
| `ApplyAsync_Budget_CreatesChapterAndArticle_WhenNothingExists` | Visary пуст → создаются и Глава 1, и подстатья 1.1. с правильными ParentID/SiteID. |
| `ApplyAsync_Budget_PatchesArticle_WhenExistsWithDifferentSums` | Глава+статья есть, суммы изменились → один `PatchWbsAsync`, ноль `Create`. |
| `ApplyAsync_Budget_IsNoOp_WhenSumsAlreadyMatch` | Глава+статья есть, суммы те же → ни Create, ни Patch не вызываются. |
| `ApplyAsync_Budget_ReusesExistingChapter_AndCreatesArticle` | Глава есть, статьи нет → один Create на статью с ParentID существующей главы. |
| `ApplyAsync_Budget_NoProjectId_ReportsError` | У Site `ConstructionProjectId == null` → `project_required`, без Visary-вызовов. |
| `BudgetReferenceProvider_LoadsExpectedEntries` | Справочник содержит Главу 1 и подстатью 1.1.; нормализация переноса строки и табов. |
| `BudgetUpsert_IsIdempotent_OnRepeatedRun` (live) | Реальный стенд проект 4584: 2 прогона upsert не плодят дубликатов; PATCH меняет суммы. |

Запуск:
```powershell
# Только бюджетные unit-тесты:
dotnet test --filter "FullyQualifiedName~FinModelBudgetTests"

# Все unit-тесты FinModel:
dotnet test --filter "FullyQualifiedName~FinModelImportMapperTests|FullyQualifiedName~FinModelBudgetTests"

# Live (требует валидный токен; PATCH-write оставляет след в Visary):
dotnet test --filter "FullyQualifiedName~BudgetUpsert_IsIdempotent_OnRepeatedRun"
```

---

## 🎯 Чек-лист добавления новой главы или подстатьи

- [ ] Добавить запись в `BudgetReferenceProvider.RawData` (Code в формате `"X.Y."`).
- [ ] Если меняется иерархия — проверить, что `Depth`/`ParentCode`/`IsChapter` пересчитываются автоматически (зависит от формата Code).
- [ ] Запустить `BudgetReferenceProvider_LoadsExpectedEntries` или равноценный тест.
- [ ] Если новая подстатья ожидается в Excel под другим Title — добавить тест `ValidateAsync_BudgetRowsAggregateAcrossStages` с этим Title.
- [ ] (Опционально) Прогнать live-тест на test-стенде Visary — убедиться, что дубликаты не появляются.

---

## 🔄 Что дальше (v0.3+, опционально)

- [ ] **Поддержка многоуровневой иерархии Глава 2**: сейчас в input-файле Глава 2
      имеет очень разноструктурированные блоки (Стоимость СМР / Технический расчёт /
      Понесенные затраты / Коммерческие расходы / Субсидия). Большинство строк не
      матчится со справочником и тихо скипается. Нужно: либо расширить эталон под
      реальные Title, либо сделать матчинг fuzzy (substring обоих направлений).
- [ ] **Удаление лишних подстатей**: сейчас при импорте мы только добавляем/обновляем.
      Если подстатья была удалена из Excel, в Visary она остаётся. Можно ли сейчас
      удалять — вопрос продуктовой политики (риск стереть рукопис).
- [ ] **Резерв на непредвиденные расходы** (Code 2.10) — отдельный кейс, в input
      файле он живёт в виде агрегатной строки в Этапах СМР.
- [ ] **Регенерация эталона**: если `Context/Бюджет_А4.1.xlsx` обновится, надо
      перевыгрузить `RawData` через скрипт (см. `BudgetReferenceProvider.cs`).
      Альтернатива — вернуться к embedded resource + читать через
      `DocumentFormat.OpenXml` (без ClosedXML, без шрифтов), но текущий объём
      справочника не оправдывает этой инфраструктуры.

---

## 🔗 Связанные документы

- [70-wbs-api-foundation.md](70-wbs-api-foundation.md) — фундамент клиента WBS (CreateWbsAsync, GetWbsByProjectAsync); v0.2 — продолжение оттуда.
- [22-update-finishing-material.md](22-update-finishing-material.md) — пример PATCH через `forceUpdate=false` + RowVersion.
- [50-visary-api-new-methods.md](50-visary-api-new-methods.md) — реестр API-методов клиента.
- [55-visary-proxy-controllers.md](55-visary-proxy-controllers.md) — фронтовый прокси `/api/visary/*`.
- [57-visary-api-testing.md](57-visary-api-testing.md) — три уровня тестов; live с автоskip при истёкшем токене.
- [62-vertical-keyvalue-layout.md](62-vertical-keyvalue-layout.md) — KeyValueVertical layout (теперь расширен полем `Budget`).
- [66-finmodel-estate-class.md](66-finmodel-estate-class.md), [67-finmodel-indicators.md](67-finmodel-indicators.md), [69-finmodel-address.md](69-finmodel-address.md) — другие параметры Финмодели; бюджет — четвёртый, самый «толстый».
