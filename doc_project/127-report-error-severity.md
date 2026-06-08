# 🎨 Отчёт импорта — severity у file-level ошибок (error / warning / info)

## 📋 Описание

Не все file-level «ошибки» в отчёте импорта одинаковы по тяжести: некоторые сигналы это **предупреждения** (импорт прошёл, но шаг пропущен из-за уже существующей сущности в Visary) или **инфо** (шаг неприменим в этом запуске). Раньше все они красились одинаково красным и пугали пользователя.

Введён уровень `severity` ∈ `error` / `warning` / `info` — backend выставляет на лету в endpoint'е `/report` по карте кодов ошибок, frontend красит блоки разным цветом и группирует по уровню.

**Триггер**: ⚠️ заказчик отметил два кода как «предупреждения, не ошибки»:
- `fmmodel_skipped_already_exists` — повторный импорт Финмодели, существует.
- `budget_upload_skipped_wbs_exists` — повторный импорт бюджета, ИСР уже сформирована.

---

## ✅ Правильная реализация

### Backend — карта кодов в endpoint'е

```csharp
// ImportsController.cs — резолв на лету, БЕЗ записи в БД.
private static string ResolveErrorSeverity(string? errorCode)
{
    if (string.IsNullOrEmpty(errorCode)) return "error";
    return errorCode switch
    {
        "fmmodel_skipped_already_exists"   => "warning",  // 👈 «уже существует»
        "budget_upload_skipped_wbs_exists" => "warning",  // 👈 «уже существует»
        "fmmodel_skipped_no_plan_file"     => "info",     // 👈 «шаг неприменим»
        _                                  => "error",
    };
}

// В GetReport — добавляем severity в DTO ошибок.
var errors = errorsRaw
    .Select(e => new
    {
        e.SourceRowNumber, e.Sheet, e.ColumnName, e.ErrorCode, e.Message,
        severity = ResolveErrorSeverity(e.ErrorCode),
    })
    .ToList();
```

### Frontend — три блока по уровню, разная заливка

```tsx
// SessionRowsTable.tsx — группируем по severity, рендерим в порядке error → warning → info.
const buckets = [
  { severity: 'error',   title: 'Ошибки уровня файла:',  className: 'messages messages--error' },
  { severity: 'warning', title: 'Предупреждения:',       className: 'messages messages--warning' },
  { severity: 'info',    title: 'Информация:',           className: 'messages messages--info' },
].map(b => ({
  ...b,
  items: report.fileLevelErrors.filter(e => (e.severity ?? 'error') === b.severity)
}));
return <>{buckets.filter(b => b.items.length > 0).map(b => /* ... */)}</>;
```

### CSS-классы (App.css)

```css
.messages--error   { background: #fde9e7; border: 1px solid #fab9b3; }  /* красный   */
.messages--warning { background: #fff5dc; border: 1px solid #ffe187; }  /* оранжевый */
.messages--info    { background: #e8f1fc; border: 1px solid #b9d4f4; }  /* синий     */
.messages--success { background: #e8f7ec; border: 1px solid #bce3c6; }  /* зелёный   */
```

### ⚠️ Важно
- **Zero-migration**: severity вычисляется на лету в controller'е, в БД (`import_errors`) **не сохраняется** — это представление, не данные. При добавлении новых кодов меняется только метод-словарь.
- **Back-compat**: поле `severity` в DTO **опциональное** — старые ответы без него фронт интерпретирует как `error` (`e.severity ?? 'error'`).
- **Группировка строгая по severity**, не по `errorCode` — фронт не должен сам решать, что считать warning'ом.
- **Default = error**: новый код, не прописанный в карте, по умолчанию красный — безопасно для регрессии.

---

## ❌ Типичная ошибка

### 1. Хардкодить severity на фронте

```tsx
// НЕПРАВИЛЬНО — фронт решает за бизнес-логику; новые коды попадают в неправильный цвет.
const isWarning = e.errorCode === 'fmmodel_skipped_already_exists';
return <div className={isWarning ? 'warning' : 'error'}>...</div>;
```

**Почему плохо**: при добавлении нового warning-кода в backend нужно править фронт. Backend — единственный источник правды о бизнес-смысле ошибки.

### 2. Сохранять severity в БД

```csharp
// НЕПРАВИЛЬНО — миграция, новая колонка, синхронизация при изменении карты.
public class ImportError { public string Severity { get; set; } }
```

**Почему плохо**: severity — это **представление**, а не данные. Может меняться с обновлением UI (заказчик передумает классификацию) — править исторические записи в БД глупо. Вычисление в `GetReport` дешёвое (in-memory словарь).

### 3. Не группировать — закрасить только заголовок

```tsx
// НЕПРАВИЛЬНО — пользователь видит «Ошибки уровня файла» с warning-сообщениями внутри.
<div className="messages messages--error">
  <h3>Ошибки уровня файла:</h3>
  {errors.map(e => <div className={`row row--${e.severity}`}>...</div>)}
</div>
```

**Почему плохо**: заголовок «Ошибки» с warning-окраской внутри ломает зрительный паттерн. Лучше — отдельный блок с заголовком «Предупреждения:» и заливкой соответствующего цвета.

---

## 📍 Применение в проекте

| Слой | Файл | Что |
|---|---|---|
| Backend resolve | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) | `ResolveErrorSeverity` — map `errorCode → severity` |
| Backend DTO | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) | поле `severity` в анонимке `errors` |
| Frontend types | [api.ts](../KiloImportService.Web/src/types/api.ts), [session.ts](../KiloImportService.Web/src/types/session.ts) | `severity?: 'error' \| 'warning' \| 'info'` |
| Frontend mapper | [importMappers.ts](../KiloImportService.Web/src/services/importMappers.ts) | `toUiRowError` пробрасывает `severity` |
| Frontend render | [SessionRowsTable.tsx](../KiloImportService.Web/src/components/ImportSession/SessionRowsTable.tsx) | три блока по severity, заголовки «Ошибки/Предупреждения/Информация» |
| Frontend CSS | [App.css](../KiloImportService.Web/src/App.css) | `.messages--info` (новый); `--warning/--error/--success` уже были |

### Текущая карта

| ErrorCode | Severity | Контекст |
|---|---|---|
| `fmmodel_skipped_already_exists` | warning | Финмодель уже существует в Visary (повторный импорт) |
| `budget_upload_skipped_wbs_exists` | warning | ИСР объекта уже сформирована, бюджет не заливаем |
| `fmmodel_skipped_no_plan_file` | info | Второй файл (план) не прикреплён — Финмодель не создавалась |
| прочие коды | error | по умолчанию |

---

## 🎯 Чек-лист при добавлении нового severity-кода

- [ ] Добавить case в `ImportsController.ResolveErrorSeverity`
- [ ] Обновить таблицу «Текущая карта» в этом документе
- [ ] Если новый бизнес-сценарий — соответствующий маппер эмитит этот `errorCode` через `RowError`
- [ ] Default `error` остаётся для всех остальных — НЕ менять без обоснования
- [ ] НЕ добавлять поле в БД (это представление)
- [ ] Фронт `?? 'error'`-fallback — НЕ удалять (back-compat)
