# 🕐 Информационные поля даты-времени импорта

## 📋 Описание

**`startedAt`** и **`completedAt`** — это **метаданные сессии импорта**, которые:
- **Фиксируются backend'ом автоматически** (не вводятся пользователем)
- Формат — **`DateTime`** (ISO 8601 от сервера, `DD.MM.YYYY HH:mm:ss` в UI)
- Отображаются **только в отчёте** (read-only)

---

## 🎯 Семантика

| Поле | Когда фиксируется | Назначение |
|------|-------------------|------------|
| `startedAt` | В момент приёма файла (`POST /api/import/upload`) | Когда импорт стартовал на сервере |
| `completedAt` | В момент завершения обработки всех строк | Когда импорт фактически закончился |
| `duration` | Вычисляется как `completedAt - startedAt` | Длительность импорта (`HH:mm:ss`) |

### Применение

- **Аудит** — кто когда импортировал
- **Диагностика** — сколько времени занял конкретный импорт
- **Отчётность** — показать в отчёте «Запущен 15.03.2024 10:00:00, завершён 15.03.2024 10:02:35»

---

## ✅ Правильная реализация

### Типы (TypeScript)

```ts
// src/types/import.ts
export interface ImportReport {
  importId: string;
  status: ImportStatus;
  importType: ImportType;
  fileFormat: FileFormat;
  fileName: string;
  startedAt: string;           // 👈 ISO 8601 от сервера: "2024-03-15T10:00:00Z"
  completedAt: string | null;  // 👈 null пока импорт идёт
  duration: string | null;     // 👈 "HH:mm:ss" (сервер вычисляет)
  // ...
}

// ImportRequest НЕ содержит этих полей — они фиксируются backend'ом
export interface ImportRequest {
  projectId: number;
  siteId: number;
  importType: ImportType;
  file: File;
}
```

### Утилита форматирования

```ts
// src/utils/datetime.ts
export const formatDateTime = (iso: string | null | undefined): string => {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';

  const pad = (n: number) => String(n).padStart(2, '0');
  return (
    `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.${d.getFullYear()} ` +
    `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
  );
};
```

### Использование в `ReportSummary`

```tsx
import { formatDateTime } from '../../utils/datetime';

<div className="report-summary__meta">
  <div className="report-summary__meta-item">
    <Typography.Text view="primary-small" color="secondary" tag="div">
      Начало импорта
    </Typography.Text>
    <Typography.Text view="primary-medium" weight="bold" tag="div">
      {formatDateTime(report.startedAt)}   {/* "15.03.2024 10:00:00" */}
    </Typography.Text>
  </div>
  {/* completedAt и duration аналогично */}
</div>
```

### ⚠️ Важно

- **Никогда не вводятся пользователем** в форме `POST /api/import/upload`
- **`completedAt` может быть `null`** во время `processing` — обрабатывается в `formatDateTime('—')`
- **Формат UI — `DD.MM.YYYY HH:mm:ss`**, не ISO 8601 (который уходит на backend)

---

## ❌ Типичные ошибки

### Ошибка 1: пользователь вводит даты

```tsx
{/* НЕПРАВИЛЬНО — делаем поля вводимыми, создаём путаницу */}
<CalendarInput label="Дата начала" value={startDate} onChange={setStartDate} />
<CalendarInput label="Дата окончания" value={endDate} onChange={setEndDate} />

{/* В API-запросе */}
fetch('/api/import/upload', {
  body: JSON.stringify({ ..., importStartDate: startDate, importEndDate: endDate })
});
```

**Почему плохо:**
- Пользователь не знает, что вводить (его даты ≠ реальное время импорта)
- Backend всё равно должен зафиксировать реальное время → дубликат данных
- Если пользователь укажет «Дата окончания = 01.01.2024», а импорт завершится 15.03.2024 — что показывать в отчёте?

**Правильно**: backend **сам** записывает `DateTime.UtcNow` в момент приёма файла и в момент завершения обработки.

### Ошибка 2: только `Date` вместо `DateTime`

```ts
{/* НЕПРАВИЛЬНО — потеряем время */}
startedAt: string;  // "2024-03-15" (только дата)
```

**Почему плохо:**
- Для отчётности важно **точное время** (потенциально 100 импортов в день)
- Длительность нельзя посчитать (нужны часы/минуты/секунды)

**Правильно**: ISO 8601 с временем — `"2024-03-15T10:00:00Z"`.

### Ошибка 3: форматирование на backend

```csharp
{/* НЕПРАВИЛЬНО — формат задаётся на сервере */}
public string StartedAt => _startedAt.ToString("dd.MM.yyyy HH:mm:ss");
```

**Почему плохо:**
- Трудно переиспользовать в других UI (англ. локаль)
- Сломается при смене часового пояса клиента
- `new Date(iso)` в JS некорректно распарсит русский формат

**Правильно**: backend отдаёт ISO 8601 (UTC), frontend форматирует через `formatDateTime`.

### Ошибка 4: некорректная обработка null

```tsx
{/* НЕПРАВИЛЬНО — падает при processing (completedAt == null) */}
{report.completedAt.substring(0, 10)}   // ❌ TypeError: Cannot read 'substring' of null
```

**Правильно**: `formatDateTime(null)` → `"—"`.

---

## 🔌 Соответствие backend

### C# DTO

```csharp
public class ImportReport
{
    public Guid ImportId { get; set; }
    public ImportStatus Status { get; set; }
    public string ImportType { get; set; }
    public string FileName { get; set; }
    public DateTime StartedAt { get; set; }         // 👈 зафиксирована при приёме файла
    public DateTime? CompletedAt { get; set; }       // 👈 null пока идёт обработка
    public string? Duration => CompletedAt.HasValue
        ? (CompletedAt.Value - StartedAt).ToString(@"hh\:mm\:ss")
        : null;
    // ...
}
```

### Фиксация в контроллере / сервисе

```csharp
// ImportController.cs
[HttpPost("upload")]
public async Task<IActionResult> Upload([FromForm] ImportRequest req)
{
    var importSession = new ImportSession
    {
        ImportId = Guid.NewGuid(),
        StartedAt = DateTime.UtcNow,   // 👈 фиксируем СРАЗУ при приёме
        Status = ImportStatus.Queued,
        // ...
    };
    // ...
}

// ImportProcessor.cs
public async Task ProcessAsync(ImportSession session)
{
    // ... обработка строк ...
    session.CompletedAt = DateTime.UtcNow;   // 👈 фиксируем в конце
    session.Status = ImportStatus.Completed;
    await _db.SaveChangesAsync();
}
```

---

## 📍 Применение в проекте

| Паттерн | Файл | Описание |
|---------|------|----------|
| Тип `ImportReport` | `KiloImportService.Web/src/types/import.ts` | `startedAt: string, completedAt: string \| null` |
| Утилита | `KiloImportService.Web/src/utils/datetime.ts` | `formatDateTime(iso)` |
| Вывод в UI | `KiloImportService.Web/src/components/ImportReport/ReportSummary.tsx` | Блок `.report-summary__meta` |
| Стили блока | `KiloImportService.Web/src/App.css` | `.report-summary__meta`, `.report-summary__meta-item` |

---

## 🎯 Чек-лист

- [ ] Тип `ImportReport.startedAt: string` (ISO 8601 от backend)
- [ ] Тип `ImportReport.completedAt: string | null` (null во время processing)
- [ ] `ImportRequest` **НЕ содержит** `startedAt`/`completedAt`
- [ ] В форме **НЕТ** `CalendarInput` для этих полей
- [ ] Backend фиксирует `StartedAt = DateTime.UtcNow` при приёме файла
- [ ] Backend фиксирует `CompletedAt = DateTime.UtcNow` по завершении обработки
- [ ] UI форматирует через `formatDateTime(iso)`, результат — `DD.MM.YYYY HH:mm:ss`
- [ ] При `completedAt == null` — UI показывает `—`
