# 🗂️ Многолистовой импорт XLSX

## 📋 Описание

Сквозной обзор поддержки **многолистовых** XLSX-файлов в пайплайне импорта.
Документ собирает в одно место **5 мест кода**, которые обязаны быть
согласованы; при добавлении нового импорта с многолистовым шаблоном
проверить каждое.

Контекст: первая реализация импорта помещений ([68-rooms-import.md](68-rooms-import.md))
читала только первый лист (`Worksheets.FirstOrDefault()`). Доделка раскрутила
цепочку: парсер → ParseRow.Sheet → unique-index БД → Pipeline-прогресс
→ SignalR-хэндлеры UI. Все они должны видеть `Sheet` иначе:
- импорт молча теряет данные с других листов, **или**
- падает на 23505 duplicate key, **или**
- прогресс по «средним» листам пропадает в UI.

**Маршрут данных**:

```
XlsxParser.ParseTabular     — обходит ВСЕ листы, ParsedRow.Sheet = имя листа
        ↓
IImportMapper.ValidateAsync — может (и должен) использовать Sheet для логики
        ↓                     («один лист = один тип», fallback Kind по имени листа)
ImportPipeline               — пишет Sheet в StagedRow / ImportError;
        ↓                     шлёт per-sheet StageProgress (первая/последняя
        ↓                     строка листа + throttle)
PostgreSQL                   — unique index (SessionId, Sheet, SourceRowNumber)
        ↓
SignalR /hubs/imports        — StageProgress { sheet, currentRow, totalRows, … }
        ↓
useImportSession             — setSession(prev => upsert sheetProgress)
        ↓
SessionProgress.tsx          — рендерит список по листам
```

---

## ✅ Правильная реализация

### 1. Парсер: обход ВСЕХ листов

```csharp
// XlsxParser.ParseTabular
foreach (var sheet in workbook.Worksheets)
{
    var range = sheet.RangeUsed();
    if (range is null) continue;           // пустой «Справочник» — не ошибка

    // У каждого листа СВОИ заголовки; накапливаем union для ParseResult.Headers.
    foreach (var cell in range.FirstRow().Cells())
        if (!allHeaders.Contains(...))
            allHeaders.Add(...);

    for (int r = 2; r <= range.RowCount(); r++)
    {
        // ⚠️ SourceRowNumber — индекс В ПРЕДЕЛАХ листа (как в Excel).
        // Между листами возможны коллизии (строка 5 встречается в каждом).
        rows.Add(new ParsedRow(r, sheet.Name, cells));
    }
}
```

### 2. БД: `Sheet` в `StagedRow` / `ImportError`, в unique index

```csharp
// StagedRow.cs + ImportError.cs
public string Sheet { get; set; } = string.Empty;

// ImportServiceDbContext.cs
e.HasIndex(x => new { x.ImportSessionId, x.Sheet, x.SourceRowNumber }).IsUnique();
```

Миграция: `20260512095902_AddSheetToStagedRowAndError` (накатывается
автоматически при старте через `db.Database.MigrateAsync()`).

### 3. Pipeline: per-sheet прогресс с гарантиями

```csharp
// ImportPipeline.ParseAndValidateCoreAsync
var totalsBySheet     = parseResult.Rows
    .GroupBy(r => r.Sheet ?? "", StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
var processedBySheet  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

for (int i = 0; i < validation.Rows.Count; i++)
{
    var raw = parseResult.Rows[i];
    var sheetKey = raw.Sheet ?? "";

    // Сохраняем Sheet вместе со строкой (иначе 23505 duplicate key).
    _serviceDb.StagedRows.Add(new StagedRow {
        ImportSessionId = sessionId,
        Sheet           = sheetKey,              // 👈
        SourceRowNumber = mr.SourceRowNumber,
        ...
    });

    // Per-sheet счётчик: «лист X — N из total(X)».
    var sheetProcessed = processedBySheet.GetValueOrDefault(sheetKey) + 1;
    processedBySheet[sheetKey] = sheetProcessed;
    var sheetTotal = totalsBySheet.GetValueOrDefault(sheetKey, 1);

    // Шлём событие в 3 случаях: первая строка листа (гарантирует появление
    // в UI), последняя строка листа (финальное 100%), либо throttle.
    var isSheetFirstRow = sheetProcessed == 1;
    var isSheetLastRow  = sheetProcessed == sheetTotal;
    if (processed == totalRowsValidate || isSheetFirstRow || isSheetLastRow
        || processed % notifyEvery == 0)
    {
        await _hub.SendAsync("StageProgress", new {
            sessionId, stage = "Validate",
            currentRow = sheetProcessed,         // 👈 per-sheet, не глобально
            totalRows  = sheetTotal,
            percentComplete = sheetTotal == 0 ? 100
                              : (int)Math.Round(sheetProcessed * 100.0 / sheetTotal),
            sheet = raw.Sheet,
        }, ct);
    }
}
```

### 4. Маппер: группировка по листу в Apply

```csharp
// RoomsFormImportMapper.ApplyAsync
var rowsBySheet = rows
    .Where(mr => mr.IsValid)
    .GroupBy(mr => GetStringOrNull(mr.MappedValues.RootElement, "Sheet") ?? "<unknown>",
             StringComparer.OrdinalIgnoreCase)
    .ToList();

foreach (var sheetGroup in rowsBySheet)
{
    _log.LogInformation(
        "RoomsForm.Apply: ───── Лист '{Sheet}' — {Count} валидных строк ─────",
        sheetGroup.Key, sheetGroup.Count());
    foreach (var mr in sheetGroup) { /* … */ }
}
```

### 5. UI: накопление прогресса по листам через `setState(prev => ...)`

```ts
// useImportSession.ts
onStageProgress: (e) => {
  if (e.sessionId !== sessionIdRef.current) return;
  setSession((prev) => {                    // 👈 функциональный, НЕ ref
    if (!prev) return prev;
    const nextSheetProgress = e.sheet
      ? upsertSheetProgress(prev.sheetProgress, { sheet: e.sheet, ... })
      : prev.sheetProgress;
    return { ...prev, sheetProgress: nextSheetProgress, stageProgress: { ... } };
  });
}
```

```tsx
// SessionProgress.tsx
{session.sheetProgress.map((sp) => (
  <Typography.Text key={sp.sheet} ...>
    {STAGE_LABELS[sp.stage]} · лист «{sp.sheet}»: строка {sp.currentRow} из {sp.totalRows} · {sp.percentComplete}%
  </Typography.Text>
))}
```

---

## ⚠️ Важно

### `SourceRowNumber` — индекс В ПРЕДЕЛАХ листа

Глобальная нумерация ломает UX: пользователь не сможет найти «строку 47» в
Excel. Поэтому `SourceRowNumber` остаётся локальным (1, 2, 3…), а уникальность
обеспечивается комбинацией `(SessionId, Sheet, SourceRowNumber)`.

### Пустые листы парсер пропускает без ошибки

`RangeUsed() == null` → `continue`. Это нужно, чтобы шаблонный «Справочник»
без данных не блокировал импорт.

### Headers — union по всем листам

В `ParseResult.Headers` собирается объединённый набор колонок (потому что у
«Квартиры» может быть «Колич. комнат», а у «Машиноместа» — «Этаж»).
Внутри `ParsedRow.Cells` ключи **только своего листа** — маппер должен
работать через алиасы и `ReadString(row, aliases)`, а не по позиции колонок.

### `setState(prev => ...)` обязателен в SignalR-хэндлерах

См. [11-react-refs-discipline.md, «Ошибка 5»](11-react-refs-discipline.md).
SignalR может отдать события из одной micro-task'и; `latestRef.current`
обновляется через `useEffect` → не успеет, второй хэндлер перетрёт первого.
**Симптом**: в `sheetProgress[]` UI пропадают «средние» листы.

---

## ❌ Типичные ошибки

### Ошибка 1: парсер читает только первый лист

```csharp
// ❌ НЕПРАВИЛЬНО
var sheet = workbook.Worksheets.FirstOrDefault();
foreach (var row in sheet.Rows()) { ... }
```

**Симптом**: импорт молча создаёт записи только из первого листа;
помещения с других листов теряются без ошибки.

### Ошибка 2: unique index без `Sheet`

```csharp
// ❌ НЕПРАВИЛЬНО
e.HasIndex(x => new { x.ImportSessionId, x.SourceRowNumber }).IsUnique();
```

**Симптом**:
```
Npgsql.PostgresException 23505: duplicate key value violates unique constraint
"IX_staged_rows_ImportSessionId_SourceRowNumber"
```
Импорт падает на втором листе (строки 2, 3, 4 уже есть от первого листа).

### Ошибка 3: глобальный `currentRow / totalRows` в `StageProgress`

```csharp
// ❌ НЕПРАВИЛЬНО — глобальный счётчик с привязкой к листу = бессмыслица
await _hub.SendAsync("StageProgress", new {
    currentRow = processed,                  // = 7 (нарастающий)
    totalRows  = totalRowsValidate,          // = 9 (по всем листам)
    sheet      = raw.Sheet,                  // «Квартиры»
});
```

**Симптом**: «лист Квартиры: строка 2 из 9» — где 9 это сумма всех строк,
не размер листа Квартиры (6).

### Ошибка 4: `setSession({ ...ref.current, ... })` в SignalR-хэндлерах

```ts
// ❌ НЕПРАВИЛЬНО — race condition при batched events
const prev = sessionLatestRef.current;
setSession({ ...prev, sheetProgress: upsertSheetProgress(prev.sheetProgress, ...) });
```

**Симптом**: в `sheetProgress` UI отображаются только некоторые листы
(чаще всего первый и последний); «средние» листы пришли по SignalR, но были
перезаписаны.

### Ошибка 5: первое событие листа не отправляется

```csharp
// ❌ НЕПРАВИЛЬНО — без isSheetFirstRow короткий лист может не появиться
if (processed == totalRowsValidate || isSheetLastRow || processed % notifyEvery == 0)
```

При листе из 2 строк и `notifyEvery = 5` событие шлётся только на последней
строке листа. На UI лист появляется уже на 100%, без промежуточного
состояния — это не критично, но при большом throttle и коротких листах
лист может не попасть в `sheetProgress` вовсе (если событие проглотится
батчем — см. ошибку 4).

**Правильно**: добавить `isSheetFirstRow` (`sheetProcessed == 1`) в условие.

---

## 📍 Применение в проекте

| Слой | Файл | Что обеспечивает |
|------|------|------------------|
| Парсер | [Visary.Api.Client → XlsxParser.cs](../KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs) | `ParseTabular` обходит все листы; `ParsedRow.Sheet` |
| Entity | [StagedRow.cs](../KiloImportService.Api/Data/Entities/StagedRow.cs), [ImportError.cs](../KiloImportService.Api/Data/Entities/ImportError.cs) | поле `Sheet : string` |
| DbContext | [ImportServiceDbContext.cs](../KiloImportService.Api/Data/ImportServiceDbContext.cs) | unique `(SessionId, Sheet, SourceRowNumber)` |
| Миграция | `Migrations/20260512095902_AddSheetToStagedRowAndError.cs` | добавление колонок + индексов |
| Pipeline | [ImportPipeline.cs](../KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs) | per-sheet прогресс, `Sheet` в StagedRow/ImportError |
| Маппер | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | `GroupBy(Sheet)` в Apply, fallback Kind по имени листа |
| UI типы | [types/session.ts](../KiloImportService.Web/src/types/session.ts) | `UiSheetProgress`, `UiSession.sheetProgress[]` |
| UI хук | [useImportSession.ts](../KiloImportService.Web/src/hooks/useImportSession.ts) | `upsertSheetProgress` + функциональный setState |
| UI вид | [SessionProgress.tsx](../KiloImportService.Web/src/components/ImportSession/SessionProgress.tsx) | рендер списка по листам |
| Маппер RES | [importMappers.ts](../KiloImportService.Web/src/services/importMappers.ts) | инициализация `sheetProgress: []` |

---

## 🎯 Чек-лист добавления нового многолистового импорта

- [ ] Маппер использует **алиасы** (`ReadString(row, aliases)`), а не позицию колонок — разные листы могут иметь разные наборы заголовков.
- [ ] В `ApplyAsync` есть `GroupBy(Sheet)` или другая логика, которая знает, что строки сгруппированы по листам.
- [ ] Если для каждого листа своя «категория сущности» — реализуй резолв (Kind по имени листа, либо приоритет колонки).
- [ ] Скиппируемые служебные листы вынеси в `SkippedSheets` маппера (например, `"Справочник"`).
- [ ] Имена листов в логах **обязательно** упоминаются вместе с `SourceRowNumber` (между листами возможны коллизии номеров).
- [ ] При добавлении новых полей в `MappedValues` — учитывай, что они могут отличаться от листа к листу.
- [ ] Smoke-тест: файл с 3+ листами разной длины, проверить что в UI появляются ВСЕ листы и каждый доходит до `N из N · 100%`.

---

## 🔗 См. также

- [05-file-format-detection.md](05-file-format-detection.md) — автоопределение формата по расширению
- [11-react-refs-discipline.md](11-react-refs-discipline.md) — «Ошибка 5» про SignalR/setState
- [15-signalr-progress.md](15-signalr-progress.md) — общая архитектура SignalR-прогресса
- [62-vertical-keyvalue-layout.md](62-vertical-keyvalue-layout.md) — альтернативная раскладка (key-value vertical, один лист)
- [68-rooms-import.md](68-rooms-import.md) — конкретный потребитель (импорт «Помещения»)
