# 📄 Экспорт сессий импорта в PDF

## 📋 Описание

На странице «История импортов» пользователь может **выбрать одну или несколько
сессий чекбоксами** и нажать **«Выгрузить в PDF»**. Backend генерирует единый
PDF-документ (одна A4-страница на сессию, page-break между ними) и отдаёт как
`application/pdf` для скачивания.

Используется в сценариях:
- Передать клиенту/коллеге отчёт об импорте без доступа к UI
- Подшить в архив или приложить к согласованию
- Сравнить две сессии (один PDF, два раздела)

---

## 🏗️ Архитектура

```
HistorySessionsTable (чекбоксы)
   ↓ selectedIds: Set<string>
ImportHistoryPage (handleExport)
   ↓ POST /api/imports/export-pdf { sessionIds: [...] }
ImportsController.ExportPdf
   ↓
ImportPdfReportService.GenerateAsync(sessionIds, ct)
   ├── _db.Sessions.Where(s => sessionIds.Contains(s.Id))
   ├── _db.Errors.Where(...).GroupBy(SessionId)
   └── MigraDoc → PdfDocumentRenderer → byte[]
       (шрифт через PdfFontResolver → /usr/share/fonts/dejavu/DejaVuSans.ttf)
   ↓ application/pdf
downloadBlob(blob, "imports-YYYYMMDD-HHMMSS.pdf")
```

---

## ✅ Правильная реализация

### Backend: библиотека и шрифт

```xml
<!-- KiloImportService.Api.csproj -->
<PackageReference Include="PDFsharp" Version="6.2.4" />
<PackageReference Include="PDFsharp-MigraDoc" Version="6.2.4" />
<!--                          ⬆️ дефис, не точка! NuGet ID именно такой -->
```

```dockerfile
# KiloImportService.Api/Dockerfile (runtime stage)
RUN apk add --no-cache curl icu-libs ttf-dejavu fontconfig && ...
#                                    ⬆️ TTF с кириллицей для PDF
```

### Backend: FontResolver (одноразовая регистрация)

```csharp
// Pdf/PdfFontResolver.cs
public sealed class PdfFontResolver : IFontResolver
{
    public byte[]? GetFont(string faceName)
    {
        // /usr/share/fonts/dejavu/DejaVuSans.ttf (Alpine)
        // /usr/share/fonts/truetype/dejavu/... (Debian)
        // C:\Windows\Fonts\arial.ttf (Windows fallback для локального dev)
        foreach (var path in candidatePaths) { ... }
    }
    public FontResolverInfo? ResolveTypeface(string family, bool bold, bool italic)
        => new FontResolverInfo(bold ? "DejaVuSans-Bold" : "DejaVuSans");
}

// ImportPdfReportService — идемпотентная регистрация под lock:
private static void EnsureFontResolver()
{
    if (_fontInitialized) return;
    lock (FontInitLock)
    {
        if (_fontInitialized) return;
        GlobalFontSettings.FontResolver = new PdfFontResolver();  // ОДИН раз на процесс
        _fontInitialized = true;
    }
}
```

### Backend: эндпоинт

```csharp
// ImportsController.cs
[HttpPost("export-pdf")]
public async Task<IActionResult> ExportPdf(
    [FromBody] ExportPdfRequest? request,
    [FromServices] ImportPdfReportService pdfService,
    CancellationToken ct)
{
    if (request?.SessionIds is null or { Count: 0 })
        return BadRequest(new { error = "Не передано ни одного sessionId." });
    if (request.SessionIds.Count > 100)
        return BadRequest(new { error = "Можно выгрузить не более 100 сессий за раз." });

    var bytes = await pdfService.GenerateAsync(request.SessionIds, ct);
    return File(bytes, "application/pdf", $"imports-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");
}
```

### Frontend: бинарный fetch (НЕ через fetchJson)

```ts
// services/importsService.ts
export async function exportImportsPdf(sessionIds: string[]): Promise<Blob> {
  const response = await fetch('/api/imports/export-pdf', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionIds }),
  });
  if (!response.ok) throw new ImportsApiError(..., response.status, ...);
  return await response.blob();   // 👈 НЕ .json()! ответ — PDF
}
```

### Frontend: скачивание через временный `<a download>`

```ts
// utils/downloadBlob.ts
export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  setTimeout(() => URL.revokeObjectURL(url), 1000);  // 👈 освобождаем blob
}
```

### Frontend: selection state в Set, не Array

```tsx
// ImportHistoryPage.tsx
const [selectedIds, setSelectedIds] = useState<Set<string>>(() => new Set());

const toggleOne = useCallback((sessionId: string) => {
  setSelectedIds((prev) => {
    const next = new Set(prev);   // 👈 ВАЖНО: новый Set, не мутация
    if (next.has(sessionId)) next.delete(sessionId);
    else next.add(sessionId);
    return next;
  });
}, []);
```

### ⚠️ Важно

- **Регистрация FontResolver — ровно один раз** на процесс через `lock + флаг`.
  PDFsharp хранит resolver в глобальном `GlobalFontSettings.FontResolver`,
  повторная установка из разных потоков → race condition.
- **Set vs Array** для selection. Set даёт O(1) на `has`/`delete` — критично
  на странице с 50+ строками. Если использовать Array — каждая отрисовка
  делает `selected.includes(id)` для каждой строки = O(N²).
- **Иммутабельный update** через `new Set(prev)` — иначе React не увидит
  изменения по ссылке и не перерисует.
- **`URL.revokeObjectURL`** после клика — иначе blob висит в памяти.
  `setTimeout 1s` нужен, чтобы браузер успел инициировать download прежде,
  чем мы освободим URL.
- **`stopPropagation` на ячейке чекбокса** — клик по чекбоксу не должен
  открывать детальный view (вся строка кликабельна).
- **Лимит 100 сессий за раз** — защита от случайной выгрузки 10 000 сессий,
  которая упрётся в память и time-out HTTP.
- **Внутри сессии — максимум 200 ошибок** в PDF (остальное «… и ещё N»).
  Полный отчёт всегда доступен в UI.

---

## ❌ Типичные ошибки

```csharp
// ❌ НЕПРАВИЛЬНО: регистрировать FontResolver в конструкторе сервиса
public ImportPdfReportService() {
    GlobalFontSettings.FontResolver = new PdfFontResolver();
    // ↑ Каждый запрос создаёт новый resolver и заменяет глобальный.
    //    Под нагрузкой — race condition + потеря кэша шрифтов.
}
```

```ts
// ❌ НЕПРАВИЛЬНО: парсить PDF как JSON
const data = await fetchJson('/api/imports/export-pdf', ...);
// fetchJson вызовет JSON.parse на бинарных байтах → SyntaxError
```

```ts
// ❌ НЕПРАВИЛЬНО: мутировать Set напрямую
setSelectedIds((prev) => {
    prev.add(id);  // та же ссылка → React не перерисует
    return prev;
});
```

```dockerfile
# ❌ НЕПРАВИЛЬНО: забыть apk add ttf-dejavu
# Симптом: PDF генерируется, но русский текст — пустые квадратики (tofu).
# Причина: FontResolver не нашёл TTF, PDFsharp использовал fallback без кириллицы.
```

---

## 📍 Применение в проекте

| Компонент | Файл | Назначение |
|-----------|------|------------|
| **NuGet пакеты** | `KiloImportService.Api/KiloImportService.Api.csproj` | `PDFsharp` + `PDFsharp-MigraDoc` 6.2.4 |
| **Системный шрифт** | `KiloImportService.Api/Dockerfile` | `apk add ttf-dejavu fontconfig` |
| **FontResolver** | `KiloImportService.Api/Pdf/PdfFontResolver.cs` | Linux/Windows fallback пути к TTF |
| **PDF-генератор** | `KiloImportService.Api/Pdf/ImportPdfReportService.cs` | MigraDoc → byte[] |
| **DI-регистрация** | `KiloImportService.Api/Program.cs` | `AddScoped<ImportPdfReportService>()` |
| **REST-эндпоинт** | `KiloImportService.Api/Controllers/ImportsController.cs` — `ExportPdf` | `POST /api/imports/export-pdf` |
| **Сервис-клиент** | `KiloImportService.Web/src/services/importsService.ts` — `exportImportsPdf` | fetch + blob |
| **Утилита** | `KiloImportService.Web/src/utils/downloadBlob.ts` | `<a download>` + `URL.revokeObjectURL` |
| **Selection state** | `KiloImportService.Web/src/components/ImportHistory/ImportHistoryPage.tsx` | `Set<sessionId>` + `toggleOne` / `toggleAllOnPage` |
| **Чекбоксы** | `KiloImportService.Web/src/components/ImportHistory/HistorySessionsTable.tsx` | `<Checkbox>` в шапке (с `indeterminate`) и в каждой строке |
| **Кнопка** | `ImportHistoryPage.tsx` — `handleExport` | «Выгрузить в PDF (N)» |
| **CSS** | `KiloImportService.Web/src/App.css` — `.history-header*`, `.history-row.row--selected` | оформление |

---

## 🚀 Деплой

```bash
# Backend и frontend оба требуют rebuild — изменения в csproj + Dockerfile + src
docker compose build backend frontend
docker compose up -d backend frontend
# Ctrl+F5 в браузере для сброса SPA-кэша.
```

**Smoke-тест endpoint:**
```bash
curl -s -o test.pdf -w "HTTP %{http_code}, bytes=%{size_download}\n" \
  -X POST http://localhost:5000/api/imports/export-pdf \
  -H 'Content-Type: application/json' \
  -d '{"sessionIds":["<существующий-guid>"]}'
file test.pdf   # должно быть: PDF document, version 1.7
```

---

## 🎯 Чек-листы

### «Поменять структуру PDF-отчёта»

- [ ] Изменить `BuildDocument` / `AddSessionBlock` / `AddMetadataTable` / `AddErrorsTable` в `ImportPdfReportService.cs`
- [ ] Не забыть: единый шрифт `PdfFontResolver.DefaultFamily` (иначе при попытке использовать другой font name FontResolver вернёт fallback)
- [ ] Если добавляем графику/SVG — PDFsharp 6 поддерживает только базовые геометрии и растровые картинки

### «Добавить новый формат экспорта (CSV/XLSX)»

- [ ] Новый сервис `ImportXlsxReportService` рядом с `ImportPdfReportService` (использовать существующий `ClosedXML` из парсера)
- [ ] Новый action `[HttpPost("export-xlsx")]` в `ImportsController`
- [ ] Новая функция `exportImportsXlsx` в `importsService.ts`
- [ ] Кнопка-меню (или dropdown) рядом с «Выгрузить в PDF» в `ImportHistoryPage.tsx`
- [ ] **Можно переиспользовать** `downloadBlob` — он формат-агностичен

### «Регулярно проверять при работе с PDF»

- [ ] Шрифт регистрируется один раз (есть lock + флаг)
- [ ] В Dockerfile установлен `ttf-dejavu`
- [ ] `Content-Type: application/pdf` в response (через `File(bytes, "application/pdf", ...)`)
- [ ] `Content-Disposition` с осмысленным именем — `imports-YYYYMMDD-HHMMSS.pdf`
- [ ] Frontend использует `response.blob()`, не `response.json()`
- [ ] `URL.revokeObjectURL` через `setTimeout` после клика
