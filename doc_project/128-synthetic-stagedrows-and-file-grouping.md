# 🗂️ Synthetic StagedRow для FinModel + группировка отчёта по файлам

## 📋 Описание

FinModel-импорт делает множество CRUD-операций ВНЕ парсера:
- создание `fmmodel`, `fmmodelversion`
- N inputdata-точек плана (per `тип помещения × квартал`)
- M inputdata-точек факта (per `тип помещения` × текущий квартал)
- Fact-блок чтения с листа Outputs primary-файла
- бюджет (XLSX upload + ГФ Главы 1) — частично уже шёл через мапер

Раньше эти операции были **невидимы в отчёте**: построчный отчёт показывал только то, что прошло через `ParsedRow → MappedRow → StagedRow`. Пользователь видел всего 11 строк (params + budget + schedule), а полсотни inputdata-точек и сам факт создания Финмодели скрывались в file-level errors (или вообще тишина).

**Решение**: мапер возвращает в `ApplyResult.SyntheticRows` «виртуальные» строки с синтетическими именами листов; Pipeline инсертит их в `staged_rows` как обычные строки; UI группирует листы по файлу-источнику (`fileLabel`) с синим заголовком-разделителем.

**Парный с**: [doc 126](./126-finmodel-fact-inputdata-from-outputs.md) (Fact-блок), [doc 110](./110-finmodel-plan-and-fmmodel.md) (Plan), [doc 127](./127-report-error-severity.md) (severity).

---

## 🏗️ Архитектура

```
FinModelImportMapper.ApplyAsync(...)
    │
    ├── synthetic = new SyntheticRowEmitter()
    │
    ├── EnsureFmModelAsync(...) ──┐
    │     │                        ├── synthetic.Emit("Финмодель", Applied,
    │     │                        │   ["Финмодель: создана id=48"])
    │     │                        │
    │     ├── EnsureFmModelVersionAndInputDataAsync(...) 
    │     │     │              ├── synthetic.Emit("Финмодель", Applied,
    │     │     │              │   ["Версия Финмодели: создана"])
    │     │     │              │
    │     │     └── for each point in planData.InputDataPoints:
    │     │           synthetic.Emit("План — Общий график", Applied,
    │     │             ["План [2026Q1, 010 Квартиры]: создан (...)"])
    │     │
    │     └── EnsureFmModelVersionFactInputDataAsync(...)
    │           └── for each point in factData.Points:
    │                 synthetic.Emit("Outputs — Факт", Applied,
    │                   ["Факт [2026Q1, 011 Квартиры (факт)]: создан (...)"])
    │
    └── return ApplyResult(applied, errors, rowActions, synthetic.Rows)

ImportPipeline.ApplyCoreAsync(...) ──→ инсертит каждый SyntheticStagedRow
                                       как StagedRow с указанными
                                       Sheet/SourceRowNumber/Status/Actions

ImportsController.GetReport(...) ──→ sheetTotals дополняется fileLabel
                                     через ResolveFileLabel(sheet)

Frontend ──→ группирует по fileLabel, рисует «📄 Файл: Параметры» /
             «📄 Файл: План» над группами листов одного файла
```

---

## ✅ Правильная реализация

### Backend: `SyntheticStagedRow` + emitter

```csharp
// IImportMapper.cs — новый тип в ApplyResult.
public record ApplyResult(
    int AppliedCount,
    IReadOnlyList<RowError> Errors,
    IReadOnlyList<RowActionLog>? RowActions = null,
    IReadOnlyList<SyntheticStagedRow>? SyntheticRows = null);

public record SyntheticStagedRow(
    string Sheet,            // 👈 синтетическое имя, например «Финмодель»
    int SourceRowNumber,     // 👈 уникален в пределах Sheet (1..N)
    StagedRowStatus Status,  // Applied / Failed / Invalid
    IReadOnlyList<string> Actions,
    string? MappedValuesJson = null);

// FinModelImportMapper.cs — helper-эмитор с авто-нумерацией строк.
internal sealed class SyntheticRowEmitter
{
    private readonly Dictionary<string, int> _nextRowBySheet = new();
    private readonly List<SyntheticStagedRow> _rows = new();
    public IReadOnlyList<SyntheticStagedRow> Rows => _rows;
    public void Emit(string sheet, StagedRowStatus status,
                     IReadOnlyList<string> actions, string? mappedJson = null)
    {
        var next = _nextRowBySheet.TryGetValue(sheet, out var n) ? n + 1 : 1;
        _nextRowBySheet[sheet] = next;
        _rows.Add(new SyntheticStagedRow(sheet, next, status, actions, mappedJson));
    }
}
```

### Backend: Pipeline инсертит в `staged_rows`

```csharp
// ImportPipeline.cs:ApplyCoreAsync — после mapper.ApplyAsync.
if (applyResult.SyntheticRows is { Count: > 0 } synthetic)
{
    foreach (var s in synthetic)
    {
        _serviceDb.StagedRows.Add(new StagedRow
        {
            ImportSessionId = sessionId,
            Sheet = s.Sheet,
            SourceRowNumber = s.SourceRowNumber,
            RawValues = JsonSerializer.SerializeToDocument(
                new Dictionary<string, object?> { ["sheet"] = s.Sheet, ["synthetic"] = true }),
            MappedValues = string.IsNullOrWhiteSpace(s.MappedValuesJson)
                ? JsonDocument.Parse("{}") : JsonDocument.Parse(s.MappedValuesJson),
            Status = s.Status,
            Actions = s.Actions.Count > 0
                ? JsonSerializer.SerializeToDocument(s.Actions) : null,
        });
    }
    await _serviceDb.SaveChangesAsync(ct);
}
```

### Backend: `fileLabel` в `sheetTotals`

```csharp
// ImportsController.cs:GetReport — резолв fileLabel из имени листа.
private static string? ResolveFileLabel(string? sheet)
{
    if (string.IsNullOrEmpty(sheet)) return null;
    if (sheet.Equals("Финмодель",      StringComparison.OrdinalIgnoreCase)) return "Параметры";
    if (sheet.StartsWith("Outputs — ", StringComparison.OrdinalIgnoreCase)) return "Параметры";
    if (sheet.StartsWith("План — ",    StringComparison.OrdinalIgnoreCase)) return "План";
    if (sheet.StartsWith("Inputs",     StringComparison.OrdinalIgnoreCase)) return "Параметры";
    return null;  // 👈 другие импорты (Rooms, ShareAgreements) — без file-разделителя
}

// В GetReport: обогащаем sheetTotals fileLabel'ом.
var sheetTotals = sheetTotalsRaw
    .Select(x => new { x.sheet, x.total, fileLabel = ResolveFileLabel(x.sheet) })
    .ToList();
```

### Frontend: file-разделитель между группами листов

```tsx
// SessionRowsTable.tsx — fileLabelBySheet map + рендер «📄 Файл: …».
const fileLabel = fileLabelBySheet.get(sheetKey) ?? null;
const prevFileLabel = gi > 0
  ? (fileLabelBySheet.get(groups[gi - 1].sheet ?? '') ?? null) : null;
const showFileHeader = hasFileLabels && fileLabel != null && fileLabel !== prevFileLabel;

return (
  <tbody>
    {showFileHeader && (
      <tr className="report-file-header">
        <td colSpan={3}>📄 Файл: {fileLabel}</td>
      </tr>
    )}
    {showSheetHeaders && <SheetHeaderRow sheet={group.sheet} total={total} ... />}
    {group.rows.map(/* ... */)}
  </tbody>
);
```

### ⚠️ Важно
- **Синтетические имена листов** должны **не пересекаться** с реальными (`Inputs`, `Inputs (budget)`, `Inputs (schedule)`) — иначе сломается unique index `(SessionId, Sheet, SourceRowNumber)`. Префиксы `«Финмодель»`, `«План — *»`, `«Outputs — *»` гарантируют изоляцию.
- **`SourceRowNumber` уникален в пределах Sheet** — `SyntheticRowEmitter` авто-нумерует 1..N per Sheet. НЕ разделяй один логический Sheet между несколькими методами — лучше эмить из централизованной точки.
- **Бизнес-язык в Actions**: «Финмодель создана id=48», «План [2026Q1, 010 Квартиры]: создан». **Никаких** `POST /crud/fmmodel`, имён DTO, `forceUpdate`. См. [doc 125](./125-rooms-sa-soft-validation-and-journal-wording.md).
- **`MappedValuesJson` опционален**: используем для inputdata-точек, чтобы UI мог показать структурированные значения (`{FmPeriod, Code, Amount, Cost, Summ}`); для коротких сообщений (вроде «Финмодель создана») — null.
- **Back-compat фронта**: `ApiSheetTotal.fileLabel` опциональное. Если backend старый или импорт не FinModel — все listы возвращают `fileLabel=null` и file-разделители не рисуются.
- **Status в synthetic-row**:
  - `Applied` — операция прошла успешно (создано/обновлено/уже существует/пропуск как ожидаемый случай).
  - `Failed` — операция не удалась (сетевая ошибка/500/контракт).
  - `Invalid` — пропустили по бизнес-логике (нет файла плана / маркер «Факт» не найден).

---

## ❌ Типичная ошибка

### 1. Использовать имя реального листа для synthetic-row

```csharp
// НЕПРАВИЛЬНО — конфликт с парсерной строкой Inputs.
synthetic.Emit("Inputs", StagedRowStatus.Applied, ["Финмодель создана"]);
```

**Почему плохо**: парсер уже эмитит StagedRow с `Sheet="Inputs"` и `SourceRowNumber=4` (например). Synthetic пытается вставить с тем же ключом → unique index violation. Используй префикс `«Финмодель»`/`«План — …»`/`«Outputs — …»`.

### 2. Эмитить технические подробности в Actions

```csharp
// НЕПРАВИЛЬНО — PATCH/POST/имена DTO в журнале.
synthetic.Emit("Финмодель", Applied,
    ["POST /api/visary/crud/fmmodel returned 201, FmModelCreateRequest.ABProjectID=4584"]);
```

**Почему плохо**: пользователь не знает, что такое PATCH и `ABProjectID`. См. [doc 125](./125-rooms-sa-soft-validation-and-journal-wording.md) — журнал на бизнес-языке.

```csharp
// ПРАВИЛЬНО:
synthetic.Emit("Финмодель", Applied,
    ["Финмодель: создана (id=48, период 2024Q1..2027Q4)"]);
```

### 3. Не считать SourceRowNumber уникальным per Sheet

```csharp
// НЕПРАВИЛЬНО — два разных метода эмитят с одним rowNumber.
synthetic.Emit("Финмодель", Applied, [...]); // SourceRowNumber=1
// ... в другом методе:
_rows.Add(new SyntheticStagedRow("Финмодель", 1, ...)); // 👈 коллизия
```

**Почему плохо**: unique-index упадёт. Используй ОДИН `SyntheticRowEmitter` на весь Apply-цикл и эмить только через него.

### 4. Возвращать fileLabel из БД

```csharp
// НЕПРАВИЛЬНО — добавлять колонку в staged_rows.
public class StagedRow { public string? FileLabel { get; set; } }
```

**Почему плохо**: fileLabel — это **представление**, не данные. Меняется по бизнес-договорённости (заказчик переименует группу). Резолв в `ResolveFileLabel(sheet)` на endpoint'е — zero-migration, как severity (см. [doc 127](./127-report-error-severity.md)).

### 5. Жёсткая привязка fileLabel в фронте

```tsx
// НЕПРАВИЛЬНО — фронт решает, к какому файлу относится лист.
const label = sheet.startsWith('Финмодель') ? 'Параметры' : 'План';
```

**Почему плохо**: backend — единственный источник правды. Любое переименование синтетического листа сломает фронт без предупреждения. Используй `report.sheetTotals[].fileLabel`.

---

## 📍 Применение в проекте

| Слой | Файл | Что добавлено |
|---|---|---|
| API контракт | [IImportMapper.cs](../KiloImportService.Api/Domain/Mapping/IImportMapper.cs) | `SyntheticStagedRow` record + `ApplyResult.SyntheticRows` |
| Pipeline | [ImportPipeline.cs](../KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs) | После `mapper.ApplyAsync` инсертит SyntheticRows в `staged_rows` |
| Mapper | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `SyntheticRowEmitter` + эмиссия в `EnsureFmModelAsync`/`EnsureFmModelVersionAndInputDataAsync`/`EnsureFmModelVersionFactInputDataAsync`; синтетические листы `«Финмодель»`/«План — Общий график»/«Outputs — Факт» |
| Helper | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `FormatNumber(double)` — бизнес-формат чисел в action-метках |
| Report endpoint | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) | `ResolveFileLabel(sheet)` + `fileLabel` в `sheetTotals` |
| Frontend types | [api.ts](../KiloImportService.Web/src/types/api.ts) | `ApiSheetTotal.fileLabel?` |
| Frontend render | [SessionRowsTable.tsx](../KiloImportService.Web/src/components/ImportSession/SessionRowsTable.tsx) | `fileLabelBySheet` map + `<tr className="report-file-header">` |
| Frontend CSS | [App.css](../KiloImportService.Web/src/App.css) | `.report-file-header` (синий заливной заголовок) |

### Синтетические листы FinModel

| Sheet | fileLabel | Что лежит | Статус строки |
|---|---|---|---|
| `«Финмодель»` | `Параметры` | fmmodel создана/уже есть, fmmodelversion создана | Applied/Failed/Invalid |
| `«План — Общий график»` | `План` | по одной строке на каждую plan inputdata-точку | Applied/Failed |
| `«Outputs — Факт»` | `Параметры` | по одной строке на каждую fact inputdata-точку | Applied/Failed/Invalid |
| `«Inputs»` | `Параметры` | parsed params-строка | Applied/Invalid |
| `«Inputs (budget)»` | `Параметры` | parsed бюджет-статьи | Applied |
| `«Inputs (schedule)»` | `Параметры` | parsed schedule-статьи (ГФ Главы 1) | Applied |

---

## 🎯 Чек-лист при добавлении новых synthetic-строк

- [ ] Имя `Sheet` НЕ пересекается с парсерными (`Inputs*`/реальными именами листов файла)
- [ ] Эмиссия через `SyntheticRowEmitter` (авто-нумерация SourceRowNumber)
- [ ] Action-метки на бизнес-языке (см. doc 125)
- [ ] `Status` правильно отражает семантику (Applied/Failed/Invalid)
- [ ] `MappedValuesJson` — null или валидный JSON-объект (НЕ массив на верхнем уровне — UI ждёт object)
- [ ] Sheet добавлен в `ResolveFileLabel(sheet)` на endpoint'е (или сознательно null для one-file импортов)
- [ ] Документация (этот файл) — новая строка в таблице «Синтетические листы»
- [ ] Тесты Apply-метода проверяют, что synthetic-rows эмитятся правильно
