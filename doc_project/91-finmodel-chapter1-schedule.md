# 📊 Финмодель → ГФ Главы 1 (CostItem)

## 📋 Описание

Импорт «Финмодель» дополнительно строит **график финансирования** (ГФ) для подстатей
Главы 1 ИСР объекта строительства. На листе `Inputs` файла «Параметры к переносу в АБ.xlsx»
есть квартальная таблица: даты начала кварталов в строке 7, суммы в **тыс. руб.** в
строках статей (`Этап 1` в типовом файле — строки 481–483) и колонках H..CU (23 квартала).

Каждой непустой квартальной ячейке соответствует одна запись `CostItem` в Visary,
привязанная к подстатье WBS того же объекта строительства (`1.1.`, `1.6.`, `1.8.`).
Сумма сохраняется в рублях (×1000), период — закрытый интервал квартала
(`Q3 2026 = 2026-07-01..2026-09-30`), статус — `70` (Plan).

---

## ✅ Правильная реализация

### Чтение (парсер)

`KeyValueVertical` теперь принимает опциональный `ChapterScheduleHint` —
параллельный `BudgetSectionHint`-у. Парсер на том же листе:

```csharp
new KeyValueVertical(
    SheetName: "Inputs",
    KeyColumn: "C",
    ValueStartColumn: "H",
    StageCount: ...,
    Budget: new BudgetSectionHint(...),
    ChapterSchedule: new ChapterScheduleHint(
        MarkerColumn: "C",
        StartMarker: "Глава 1.",
        EndMarker: "Глава 2.",
        QuarterHeaderRow: 7,
        FirstQuarterColumn: "H",
        LastQuarterColumn: "CU"));
```

Парсер эмитит `Sheet = "{sheetName} (schedule)"`:

1. **Header-row** — sentinel `Cells["C"] = "__quarters__"` (константа
   `XlsxParser.ChapterScheduleQuartersSentinel`), `Cells["H"]..` = ISO-даты начала
   кварталов (через `cell.TryGetValue<DateTime>` → `"yyyy-MM-dd"`). Маппер по
   sentinel-у отличает header от обычных article-строк.
2. **Article-rows** — по одной на каждую непустую строку между `StartMarker` и
   `EndMarker`. `Cells["C"]` = Title из MarkerColumn; `Cells["H"]..` = текстовые
   значения квартальных колонок. `SourceRowNumber` — абсолютный Excel-row, нужен
   для per-cell сообщений в журнале (`H481`).

### Маппинг (FinModelImportMapper)

Шаг `ValidateChapter1Schedule`:

- Берёт **только Этап 1** (от маркера «Этап 1» до следующего «Этап»/«Итого»).
  Этап 2/3 повторяют те же статьи — суммируем их **нельзя** (это другой план/факт).
- Title → Code:
  1. Сначала явный алиас `Chapter1TitleAliases` — `«Прочие затраты» → 1.8.`
     (`BudgetReferenceProvider.FindByTitle` не сработает: в файле короче справочного).
  2. Затем `BudgetReferenceProvider.FindByTitle` (с фильтром по `Code` начинается на `1.`).
- Эмитит `MappedRow` с `Kind="schedule_article"` + словарём непустых квартальных
  сумм; `Kind="schedule_quarters"` для header-row.

Шаг `ApplyChapter1ScheduleAsync` (в `ApplyAsync`):

1. `_listViewClient.GetWbsBySiteAsync(siteId, ct)` — загружаем WBS объекта.
2. Строим `WbsByCode` (`"1.1."` → `WbsRaw`). Если статьи нет → per-cell `RowActionLog`
   с сообщением заказчика:
   ```
   для ячейки H481 не была добавлена информация для ГФ, тк статья 1.1 отсутствует в ИСР
   ```
3. Иначе `_listViewClient.GetCostItemsByWbsAsync(wbsId)` — pre-check существующих.
4. Для каждой квартальной ячейки:
   - `existing[PlanPeriod.Start.Date]` — найден и сумма совпадает (±1 коп.) → **skip**.
   - Найден, сумма отличается → `PatchCostItemAsync(id, { PlanSum })`.
   - Не найден → `CreateCostItemAsync({ WBSID, WBS, PlanSum, PlanPeriod, Status:70 })`.

### ⚠️ Важно

- **×1000**: лист в тыс. руб., в Visary храним рубли. `Math.Round(thousands * 1000, 2, AwayFromZero)`.
- **PlanQuarter/PlanYear** — derived на стороне Visary; в POST НЕ передаём.
- **PlanPeriod.Start**: первый день квартала, `DateTimeKind.Utc` (Visary хранит ISO-UTC).
- **PlanPeriod.End**: `quarterStart.AddMonths(3).AddDays(-1)` (`Q3 2026 → 2026-09-30`).
- **Дедупликация на сервере отсутствует**: повторный POST с тем же `(WBSID, PlanPeriod)`
  создаст дубликат. Pre-check обязателен.
- **PATCH через `forceUpdate=true`** — тот же паттерн, что для Room/ShareAgreement/WBS:
  `ID`/`RowVersion` nullable + `WhenWritingNull`, иначе Visary падает 500
  «Can not add property RowVersion to JObject».

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО — обращаемся напрямую к BudgetReferenceProvider.FindByTitle для «Прочие затраты»
var entry = _budgetRef.FindByTitle("Прочие затраты");
// → null! (reverse-prefix матч в провайдере работает только для file-title длиннее ref-title)
// Правильно — явный алиас Chapter1TitleAliases["Прочие затраты"] = "1.8.".
```

```csharp
// НЕПРАВИЛЬНО — суммируем Этап 1 + Этап 2 + Этап 3 в одну запись CostItem
foreach (var stage in chapter1.Stages) sum += stage.Quarter[q].Amount;
// → дубликат факта (этапы — РАЗНЫЕ планы, не слагаемые). По решению заказчика берём только Этап 1.
```

```csharp
// НЕПРАВИЛЬНО — POST CostItem без pre-check
await _crud.CreateCostItemAsync(req, ct);
// → при повторном импорте Visary молча создаст дубликат (уникальности по (WBSID, PlanPeriod) нет).
```

```csharp
// НЕПРАВИЛЬНО — передаём PlanQuarter/PlanYear в POST
new CostItemCreateRequest { ..., PlanQuarter = 3, PlanYear = 2026 };
// → Visary derives их из PlanPeriod; явное значение порождает рассинхрон при PATCH.
```

---

## 📍 Применение в проекте

| Слой | Файл | Что добавлено |
|------|------|---------------|
| Visary Client | [Visary.Api.Client/Common/VisaryMnemonics.cs](../Visary.Api.Client/Common/VisaryMnemonics.cs) | `CostItem = "costitem"` |
| Visary Client | [Visary.Api.Client/Dto/VisaryEntities.cs](../Visary.Api.Client/Dto/VisaryEntities.cs) | `CostItemRaw`, `CostItemPeriod` |
| Visary Client | [Visary.Api.Client/Dto/VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs) | `CostItemCreateRequest`, `CostItemPatchRequest`, `CostItemStatus.Plan = 70` |
| Visary Client | [Visary.Api.Client/CRUD/CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | `CreateCostItemAsync`, `PatchCostItemAsync` |
| Visary Client | [Visary.Api.Client/ListView/ListViewClient.cs](../Visary.Api.Client/ListView/ListViewClient.cs) | `GetCostItemsByWbsAsync`, `GetWbsBySiteAsync` |
| Парсер | [KiloImportService.Api/Domain/Importing/FileLayoutHint.cs](../KiloImportService.Api/Domain/Importing/FileLayoutHint.cs) | `ChapterScheduleHint` |
| Парсер | [KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs](../KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs) | `ExtractChapterSchedule` + sentinel-константа |
| Маппер | [KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `ValidateChapter1Schedule`, `ApplyChapter1ScheduleAsync`, `Chapter1TitleAliases` |
| Тесты | [KiloImportService.Api.Tests/Mapping/FinModelChapter1ScheduleTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelChapter1ScheduleTests.cs) | 7 тестов (validate + apply, POST/PATCH/skip + per-cell journal) |

---

## 🔌 API Visary (для справки)

Подсмотрено в `Context/har ГФ.txt`.

**POST** `/api/visary/crud/costitem`:
```json
{
  "WBSID": 168482,
  "WBS": { "ID": 168482 },
  "PlanSum": 2222000,
  "PlanPeriod": {
    "Start": "2026-07-01T00:00:00Z",
    "End":   "2026-09-30T00:00:00Z"
  },
  "Status": 70
}
```

**POST** `/api/visary/listview/costitem/onetomany/WBS?associationId={wbsId}`:
```json
{
  "Mnemonic": "costitem",
  "PageSkip": 0, "PageSize": 50,
  "Columns": ["ID","WBS","Snapshot","PlanSum","Status","PlanPeriod",
              "ProjectDoc","Version","PlanMonth","PlanQuarter","PlanYear"],
  "SearchPhrase": null, "Sorts": "null", "Hidden": false, "Summaries": []
}
```

**PATCH** `/api/visary/crud/costitem/{id}?forceUpdate=true`:
```json
{ "PlanSum": 250000 }
```
(`ID` и `RowVersion` в теле НЕ передаём — `forceUpdate=true`.)

---

## 🎯 Чек-лист

- [ ] `Chapter1TitleAliases` синхронизирован с типовым файлом заказчика
- [ ] При появлении новых статей в Главе 1 в файле — обновить `BudgetReferenceProvider`
- [ ] При повторном импорте — журнал содержит «совпадает — без изменений» вместо PATCH
- [ ] Отсутствующая в ИСР статья → per-cell сообщение в журнале, **не** в `errors`
- [ ] Годовые колонки CV..DS (после CU) **не** учитываются (по решению заказчика — только поквартально)
- [ ] Только Этап 1 (по решению заказчика); Этап 2/3 пропускаются
