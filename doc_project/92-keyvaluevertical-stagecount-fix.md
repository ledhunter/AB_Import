# 🪞 KeyValueVertical + StageCount: ровно N колонок, не «N или меньше»

## 📋 Описание

`XlsxParser.ParseKeyValueVertical` при заданном `StageCount=N` обязан выпустить ровно
`N` `ParsedRow` (по одной на каждый этап шаблона). Это контракт, на который опирается
маппер: пустая ячейка этапа → `value_empty` для конкретной стадии, **не** пропуск
этапа целиком.

До фикса парсер ограничивал диапазон колонок-значений через `Math.Min(lastCol, N)`,
где `lastCol = range.LastColumn().ColumnNumber()`. `RangeUsed()` ClosedXML
возвращает только реально заполненные ячейки — если в шаблоне 2 этапа, а пользователь
заполнил только Этап 1, `lastCol = 8` (буква H), `stopCol = min(8, 9) = 8` → цикл
делает **1** итерацию вместо 2. Этап 2 «терялся».

---

## ✅ Правильная реализация

[`XlsxParser.cs:445-450`](../KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs#L445-L450):

```csharp
// С maxStages — РОВНО N колонок (даже если правее RangeUsed пусто): нам важно
// выпустить ParsedRow на каждый этап шаблона, чтобы маппер показал value_empty
// для конкретного этапа.
int stopCol = maxStages.HasValue
    ? valueStartCol + maxStages.Value - 1
    : lastCol;
```

### ⚠️ Важно

- `sheet.Cell(rowNum, c).GetString()` для ячеек **за пределами `RangeUsed`** в
  ClosedXML возвращает пустую строку (не падает) — это безопасно, цикл уйдёт правее
  фактического данных.
- Без `maxStages` поведение **legacy** не меняем (идём до `lastCol`): для шаблонов
  без управляющего листа «Control» нет способа узнать «сколько столбцов читать»,
  легче ограничиться `RangeUsed`.

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — Math.Min «срезает» этапы, для которых RangeUsed «не дотянулся»
int stopCol = maxStages.HasValue
    ? Math.Min(lastCol, valueStartCol + maxStages.Value - 1)
    : lastCol;
// → шаблон финмодели с N=2: если этап 2 (колонка I) пуст — RangeUsed обрывается
//   на H, цикл крутится 1 раз. Маппер видит только Этап 1, value_empty для Этапа 2
//   не появляется, отчёт «лжёт» — этап 2 как будто и не существовал в шаблоне.
```

---

## 📍 Применение в проекте

| Где задаётся `StageCount` | Файл | Эффект фикса |
|---|---|---|
| Финмодель (Inputs) | [FinModelImportMapper.cs:44-52](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs#L44-L52) | Все N этапов из «Control / Выбрать количество этапов» теперь корректно эмитятся, даже если правые колонки в файле пустые |

Сторожевой тест — [XlsxParserTests.cs](../KiloImportService.Api.Tests/Importing/XlsxParserTests.cs)
`KeyValueVertical_StageCount_EmitsRowEvenWhenStageEmpty` (был сломан с момента
заведения, теперь зелёный).

---

## 🧪 Регрессии в `FinModelBudgetTests` (как часть той же зачистки)

Тесты `FinModelBudgetTests` устарели на фоне эволюции маппера и были обновлены:

| Тест | Старое ожидание | Новая реальность |
|---|---|---|
| `ValidateAsync_BudgetRows_IgnoresRepeatsAfterChapterTotal` | `Single(rows)` | 2 строки: `article` + `chapter-direct итог` (см. [doc 78 v1.3](./78-budget-xlsx-export.md)) |
| `ValidateAsync_BudgetRows_ResolvesShortTitleAgainstLongerReference` | `Single(rows)` | Тоже 2 строки (article + chapter-direct) |
| `ValidateAsync_BudgetRows_UnknownTitle_SkippedSilently` | «Прочие затраты» → не матчится | Теперь матчится через reverse-prefix-in-chapter в `1.8`; заменили на заведомо «бредовый» Title |
| `ApplyAsync_Budget_*` (5 тестов) | проверяли CRUD-путь `CreateWbsAsync`/`PatchWbsAsync` | CRUD-путь WBS **отключён** ([doc 78 v1.3](./78-budget-xlsx-export.md)); заменено на 1 сторожевой `ApplyAsync_Budget_CountsRowsWithoutCallingWbsCrud` |

### ⚠️ Урок

При намеренном отключении flow ([doc 78 v1.3](./78-budget-xlsx-export.md), removal
of CRUD-path → XLSX-only) — **тесты на старый flow надо удалять/переписывать вместе
с кодом**, иначе CI становится «фоновым шумом» и реальные регрессии не видны.

---

## 🎯 Чек-лист

- [ ] При добавлении нового `KeyValueVertical`-шаблона: задан `StageCount` → парсер
      идёт ровно N колонок; не задан → до `RangeUsed`.
- [ ] При намеренном отключении CRUD-flow в маппере — тесты на flow удалять /
      переписывать в той же ветке (не оставлять как «отстающий мусор»).
- [ ] При добавлении новых методов в `IListViewClient` / `ICrudClient` —
      обновлять stub-реализации в `ProjectsCacheServiceTests.FakeListViewClient`
      (иначе тесты не соберутся).
