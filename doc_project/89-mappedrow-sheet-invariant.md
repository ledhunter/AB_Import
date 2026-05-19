# 🧩 Инвариант `MappedRow.Sheet`: маппер сам несёт лист

## 📋 Описание

При импорте `(Sheet, SourceRowNumber)` — составной ключ `StagedRow` (см.
[72-multi-sheet-import.md](72-multi-sheet-import.md), уникальный индекс
`IX_staged_rows_ImportSessionId_Sheet_SourceRowNumber`). Маппер
(`RoomsFormImportMapper.ValidateAsync` / `FinModelImportMapper.ValidateAsync`)
возвращает список `MappedRow`. Пайплайн (`ImportPipeline.ParseAndValidateCoreAsync`)
сохраняет каждую `MappedRow` как `StagedRow` и должен знать, в каком листе она
жила. До этой правки лист брался **по индексу** из `parseResult.Rows[i].Sheet` —
скрытый bug-by-design.

> 🔗 Связано с фильтрацией листов на других слоях:
> [88-xlsx-skip-hidden-sheets.md](88-xlsx-skip-hidden-sheets.md) (парсер) и
> [90-rooms-skip-unknown-kind-sheets.md](90-rooms-skip-unknown-kind-sheets.md)
> (маппер) — последний устраняет первопричину «расхождения индексов»
> для rooms-импорта (там silent-skip сводных строк остаётся, но количество
> «не наших» листов резко сокращается). Тем не менее инвариант `MappedRow.Sheet`
> нужен **всегда**, потому что мапперы и в дальнейшем могут пропускать строки
> (`continue` без `Add`).

### Кейс, поломавший прод (2026-05-18, файл «UC9NVP_Ежевика_01.04.2026.xlsx»)

- Парсер прочитал **6985** строк из 6 видимых листов (`Квартира` + `Кв_01.04.26` +
  `Кв_01.03.26 (2)`; «Общий график»/«Итог»/«План» strict-skip-нулись по анкорам).
- Маппер вернул **310 MappedRow** — остальные ~6675 были тихо пропущены ветками:
  - «нет НПС/РНС/Этапа → служебная сводная строка»
  - (потенциально другие `continue` без `Add(...)`)
- Пайплайн в `for (int i = 0; i < validation.Rows.Count; i++)`:
  - `mr = validation.Rows[i]` → `SourceRowNumber` из MappedRow (правильный, скажем 800)
  - `raw = parseResult.Rows[i]` → `Sheet` из **i-той ParsedRow** (на индексе 5 уже не та строка)
- В БД летел `(Sheet="Квартира", SourceRowNumber=800)`, а через 100 итераций
  снова `(Sheet="Квартира", SourceRowNumber=800)` — из настоящей квартирной 800-й
  строки. `IX_staged_rows_...` упал с **23505 duplicate key**, сессия осталась
  висеть в статусе `Validating` (controller-level error не транслируется в `Failed`).

---

## ✅ Правильная реализация

### 1) `MappedRow` хранит `Sheet` (обязательное поле)

```csharp
public record MappedRow(
    int SourceRowNumber,
    string Sheet,           // 👈 маппер обязан заполнить
    bool IsValid,
    JsonDocument MappedValues,
    IReadOnlyList<RowError> Errors
);
```

### 2) Маппер берёт `Sheet` из исходной `ParsedRow`

```csharp
// RoomsFormImportMapper.ValidateAsync
mappedRows.Add(new MappedRow(
    row.SourceRowNumber,
    row.Sheet ?? string.Empty,   // 👈 ВСЕГДА переносим
    rowErrors.Count == 0,
    JsonSerializer.SerializeToDocument(mapped),
    rowErrors));
```

Для агрегатов (FinModel budget, где много `ParsedRow` сворачиваются в одну
`MappedRow`) лист берём один раз из первой строки агрегата — все бюджетные
строки приходят из одного листа `KeyValueVertical`:

```csharp
var budgetSheet = ordered[0].Sheet ?? string.Empty;
// ... в каждом mapped.Add(new MappedRow(..., budgetSheet, ...))
```

### 3) Пайплайн использует `mr.Sheet` (НЕ индекс в parseResult)

```csharp
// ImportPipeline.ParseAndValidateCoreAsync
// Lookup ParsedRow по (Sheet, SourceRowNumber) — для RawValues.Cells, не для Sheet.
var parsedByKey = new Dictionary<(string Sheet, int Row), ParsedRow>(parseResult.Rows.Count);
foreach (var pr in parseResult.Rows)
    parsedByKey.TryAdd((pr.Sheet ?? string.Empty, pr.SourceRowNumber), pr);

for (int i = 0; i < validation.Rows.Count; i++)
{
    var mr = validation.Rows[i];
    var sheet = mr.Sheet ?? string.Empty;                 // 👈 единственный источник истины
    var parsedRow = parsedByKey.GetValueOrDefault((sheet, mr.SourceRowNumber));
    _serviceDb.StagedRows.Add(new StagedRow
    {
        Sheet = sheet,
        SourceRowNumber = mr.SourceRowNumber,
        ...
    });
}
```

### 4) `totalsBySheet` для прогресса считаем из `validation.Rows`, не из `parseResult.Rows`

Иначе `sheetProcessed` (растёт по `validation.Rows`) никогда не достигнет
`sheetTotal` (из `parseResult.Rows`), и UI-прогресс «строка X из Y» зависнет на 5%.

```csharp
var totalsBySheet = validation.Rows
    .GroupBy(r => r.Sheet, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
```

### ⚠️ Важно

- `MappedRow.Sheet` — **non-nullable** (тип `string`, не `string?`). Маппер обязан
  заполнить даже для invalid-строк и для агрегатов. Если ставить `null` — пайплайн
  получит `NullReferenceException` при `mr.Sheet ?? string.Empty` цепочки нет
  страха только потому что мы кладём fallback.
- Pipeline.ApplyAsync восстанавливает `MappedRow` из `StagedRow` — `Sheet` тоже
  переносится (`r.Sheet ?? string.Empty`).
- `mr.SourceRowNumber` уникален в пределах **(SessionId, Sheet)**, а не глобально —
  напр. два листа могут иметь строку 42. Lookup `parsedByKey` использует составной
  ключ `(Sheet, Row)`, как и unique index в БД.

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — bug-by-design:
for (int i = 0; i < validation.Rows.Count; i++)
{
    var mr = validation.Rows[i];
    var raw = parseResult.Rows[i];           // ❌ индексы расходятся при silent-skip
    _serviceDb.StagedRows.Add(new StagedRow
    {
        Sheet = raw.Sheet ?? string.Empty,   // ❌ лист «чужой» строки
        SourceRowNumber = mr.SourceRowNumber,
        ...
    });
}
```

```csharp
// НЕПРАВИЛЬНО — маппер «забыл» Sheet в одной из веток
if (rowErrors.Count > 0)
{
    mappedRows.Add(new MappedRow(row.SourceRowNumber, false, ..., rowErrors));
    //                                                ↑ компилятор не подскажет, если
    //                                                  Sheet=string? — поэтому делаем non-nullable
}
```

---

## 📍 Применение в проекте

| Компонент | Файл | Что делает |
|-----------|------|------------|
| `MappedRow` record | [KiloImportService.Api/Domain/Mapping/IImportMapper.cs](../KiloImportService.Api/Domain/Mapping/IImportMapper.cs) | Добавлено non-nullable поле `Sheet` |
| `RoomsFormImportMapper.ValidateAsync` | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | site_mismatch + valid пути переносят `row.Sheet` |
| `FinModelImportMapper.ValidateAsync` | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | Параметры — `row.Sheet`; бюджетные агрегаты — `budgetSheet = ordered[0].Sheet` |
| `ImportPipeline.ParseAndValidateCoreAsync` | [ImportPipeline.cs](../KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs) | `sheet = mr.Sheet` + lookup `parsedByKey` для Cells; `totalsBySheet` из `validation.Rows` |
| `ImportPipeline.ApplyCoreAsync` | там же | `staged.Select(r => new MappedRow(r.SourceRowNumber, r.Sheet ?? "", ...))` |

---

## 🎯 Чек-лист

- [x] `MappedRow.Sheet` — non-nullable `string`
- [x] 6 callsites `new MappedRow(...)` переданы лист (мапперы + Pipeline.Apply)
- [x] Pipeline.Validate: `sheet = mr.Sheet` (НЕ `parseResult.Rows[i].Sheet`)
- [x] `totalsBySheet` для прогресса — из `validation.Rows`
- [x] `parsedByKey` lookup для `RawValues.Cells` по `(Sheet, SourceRowNumber)`
- [x] Apply восстанавливает `MappedRow` из `StagedRow` с `Sheet`
- [ ] (future) Pipeline-level catch-all: при unhandled exception в `ParseAndValidateAsync` фоновую сессию перевести в `Failed`, не оставлять `Validating` навсегда — отдельная задача
