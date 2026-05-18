# 📒 Построчный журнал действий Apply (per-row actions)

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-18
**Дополняет**: [14-imports-backend-integration.md](14-imports-backend-integration.md), [68-rooms-import.md](68-rooms-import.md)

Раньше в построчном отчёте сессии импорта по успешным строкам было видно
только статус `Applied`. Чего конкретно маппер достиг в Visary — создал
корпус или нашёл существующий, обновил помещение или создал новое, ДДУ
подтянул или заводил с нуля — оставалось только в backend-логах.

Теперь маппер собирает **журнал действий per-row**, и UI рендерит его в
отчёте рядом со статусом и (если есть) ошибками.

---

## ✅ Правильная реализация

### 1. DTO в маппере

`Domain/Mapping/IImportMapper.cs`:

```csharp
public record ApplyResult(
    int AppliedCount,
    IReadOnlyList<RowError> Errors,
    IReadOnlyList<RowActionLog>? RowActions = null);

public record RowActionLog(int SourceRowNumber, string Sheet, IReadOnlyList<string> Actions);
```

`RowActions` опциональный — старые мапперы (FinModel, …) могут не
заполнять, и поведение для них не меняется.

### 2. Сбор действий в `RoomsFormImportMapper.ApplyAsync`

Локальный лямбда-хелпер копит метки по ключу `(Sheet, SourceRowNumber)`
и в финале превращается в список `RowActionLog`.

```csharp
var actionsByRow = new Dictionary<(string Sheet, int Row), List<string>>();
void Log(string sheet, int row, string action)
{
    var key = (sheet, row);
    if (!actionsByRow.TryGetValue(key, out var list))
        actionsByRow[key] = list = new();
    list.Add(action);
}
// …
// в местах find/create/patch:
Log(sheetForRow, mr.SourceRowNumber, $"Корпус создан ({sectionTitle})");
Log(sheetForRow, mr.SourceRowNumber, $"Помещение обновлено (№{roomNumber})");
Log(sheetForRow, mr.SourceRowNumber, $"ДДУ найден (не создан, №{saNumber})");
```

Метки, которые сейчас пишутся для импорта `rooms`:

| Стадия            | Когда                                                     | Метка                                                  |
|-------------------|-----------------------------------------------------------|--------------------------------------------------------|
| Застройщик (PM)   | Найден PM в проекте — переиспользован                     | `Застройщик переиспользован`                           |
| Застройщик (PM)   | Создан новый PM                                            | `Застройщик создан`                                    |
| Застройщик (PM)   | После create/reuse — линк к сайту                          | `Застройщик привязан к объекту`                        |
| Section (корпус)  | Найден по Title                                            | `Корпус найден (…)`                                     |
| Section (корпус)  | Не нашёлся — создан                                        | `Корпус создан (…)`                                     |
| Room (помещение)  | Не нашёлся — создан                                        | `Помещение создано (№…)`                                |
| Room (помещение)  | Нашёлся — PATCH                                            | `Помещение обновлено (№…)`                              |
| ShareAgreement    | Создан новый                                                | `ДДУ создан (№…)`                                       |
| ShareAgreement    | Нашёлся в этой же комнате — PATCH                          | `ДДУ найден (не создан, №…)`                            |
| ShareAgreement    | Нашёлся орфанный/в другой комнате — PATCH с привязкой     | `ДДУ найден (привязан к новому помещению, №…)`         |

### 3. Хранение в БД

`Data/Entities/StagedRow.cs`:

```csharp
public JsonDocument? Actions { get; set; }
```

Миграция `20260518085520_AddActionsToStagedRow` добавляет `actions jsonb NULL`
в `import.staged_rows`. Маппинг для Npgsql — нативный jsonb; для InMemory —
конвертер `JsonDocConverter` (см. `ImportServiceDbContext`).

Pipeline после Apply-фазы переносит RowActions в StagedRow:

```csharp
var actionsByKey = (applyResult.RowActions ?? []).ToDictionary(a => (a.Sheet, a.SourceRowNumber));
foreach (var r in staged)
{
    if (applyResult.AppliedCount > 0) r.Status = StagedRowStatus.Applied;
    if (actionsByKey.TryGetValue((r.Sheet, r.SourceRowNumber), out var log))
        r.Actions = JsonSerializer.SerializeToDocument(log.Actions);
}
```

Важно: actions сохраняем **всегда**, даже если массово Apply упал —
журнал частично-выполненных действий (корпус создан → потом упал room)
сам по себе диагностически ценный.

### 4. API + UI

`GET /api/imports/{id}/report` теперь возвращает поле `actions` (string[]
или null) в каждом элементе `rows`.

`KiloImportService.Web/src/services/importMappers.ts` → `r.actions ?? []`
в `UiReportRow`. Компонент `SessionRowsTable` рендерит блок
`.messages.messages--success` под ошибками (или сам по себе, если ошибок
нет) с меткой «Действие» и текстом из массива.

### ⚠️ Важно

- Ключ хранилища — **`(Sheet, SourceRowNumber)`**, как и уникальный
  индекс `staged_rows`. Без `Sheet` для многолистовых файлов записи
  разных листов с одним Excel-номером строки склеились бы.
- `RowActionLog.Actions` — `IReadOnlyList<string>`, **порядок имеет
  значение**: метки идут в порядке выполнения (Section → Room → SA), и
  UI рендерит так же. Никаких сортировок поверх.
- Сериализация `JsonSerializer.SerializeToDocument(list)` даёт
  JSON-массив строк — экономичнее, чем оборачивать в объект, и совместимо
  с `JsonDocument? Actions` без дополнительной обёртки в DTO.
- Старые сессии (до миграции) имеют `actions = null` в БД → UI получает
  `[]` после `r.actions ?? []` → блок просто не рендерится. Никаких
  fallback-плейсхолдеров — нечего показывать.

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — копить actions в свойстве маппера: маппер singleton-DI,
// разные сессии перетрут друг друга.
public class RoomsFormImportMapper {
    private List<string> _actions = new();   // ← shared state, гонка
    public Task<ApplyResult> ApplyAsync(...) { _actions.Add(...); }
}
```

```csharp
// НЕПРАВИЛЬНО — писать одно «Помещение обновлено» В КАЖДЫЙ catch-блок
// верхнего try. Журнал будет неполным: если упал Room — Section/PM
// действия не запишутся. Логируйте СРАЗУ после каждой реальной операции.
try {
    /* create section */
    /* create room */
    /* create SA */
    Log("Помещение создано");   // ← не дойдёт при падении в SA
} catch { /* … */ }
```

```csharp
// НЕПРАВИЛЬНО — отдавать actions только при applied > 0. Частичный
// журнал ДО падения — самая ценная диагностика. Пайплайн сохраняет
// actions ВСЕГДА.
if (applied > 0) saveActions();
```

```typescript
// НЕПРАВИЛЬНО (Frontend) — рендерить actions внутри блока ошибок: визуально
// не отличишь «было плохо» от «всё хорошо». Стили `.messages--success` —
// зелёные, `.messages--error` — красные. Разные блоки.
{errors.length > 0 && <div className="messages--error">{[...errors, ...actions]}</div>}
```

---

## 📍 Применение в проекте

| Компонент                              | Файл                                                          | Что добавилось |
|----------------------------------------|---------------------------------------------------------------|----------------|
| `ApplyResult.RowActions`               | `Domain/Mapping/IImportMapper.cs`                             | Опциональный список меток |
| `RowActionLog`                         | `Domain/Mapping/IImportMapper.cs`                             | Новый record |
| `StagedRow.Actions`                    | `Data/Entities/StagedRow.cs`                                  | JsonDocument? |
| Миграция                               | `Migrations/…_AddActionsToStagedRow.cs`                       | `actions jsonb NULL` |
| Маппер `rooms`                         | `Domain/Mapping/RoomsFormImportMapper.cs`                     | 8 точек логирования (PM/Section/Room/SA) |
| Pipeline                               | `Domain/Pipeline/ImportPipeline.cs`                           | Перенос `RowActions` → `StagedRow.Actions` |
| Controller                             | `Controllers/ImportsController.cs::GetReport`                 | Поле `actions` в DTO строки |
| Frontend types                         | `KiloImportService.Web/src/types/{api,session}.ts`            | `actions: string[]` |
| Frontend mapper                        | `KiloImportService.Web/src/services/importMappers.ts`         | `r.actions ?? []` |
| UI                                     | `KiloImportService.Web/src/components/ImportSession/SessionRowsTable.tsx` | Блок `.messages--success` |
| CSS                                    | `KiloImportService.Web/src/App.css`                           | `.messages--success` (зелёный) |

---

## 🎯 Чек-лист для другого маппера

Если хочется добавить per-row actions в другой маппер (например, FinModel):

- [ ] Завести в `ApplyAsync` локальный `Dictionary<(string Sheet, int Row), List<string>>`
- [ ] Объявить лямбда-хелпер `Log(sheet, row, message)`
- [ ] **Сразу после** каждого CRUD-вызова Visary писать одну русскую метку
      («что произошло», без технических деталей)
- [ ] В местах find-hit (без сетевого вызова) тоже писать метку — иначе
      строка будет с пустым журналом, а пользователю важно видеть «найдено»
- [ ] В финале вернуть `new ApplyResult(applied, errors, rowActionsList)`
- [ ] Никаких миграций / контроллеров / UI трогать не надо — pipeline
      и фронт уже всё знают
