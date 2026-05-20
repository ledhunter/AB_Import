# 📤 Автозагрузка бюджета в Visary FileStorage + TypedJournal-импорт

## 📋 Описание

**Статус**: 🟢 v1.3 — заливка идёт **автоматически в Apply-фазе** + polling статуса.
**Дата**: 2026-05-19 (v1.3), 2026-05-15 (v1.1/v1.2), 2026-05-14 (v1.0).
**Зависит от**: [78-budget-xlsx-export.md](78-budget-xlsx-export.md) (генерация XLSX).
**Связанная документация**: [94-finmodel-auto-budget-before-gf.md](94-finmodel-auto-budget-before-gf.md) — почему бюджет должен закончиться раньше, чем стартует ГФ Главы 1.
**Источники API**: `Context/har файл по загрузке бюджета в папку ФХ.txt`, `Context/har импорт бюджета.txt`.

### 📌 История версий

| | v1.0 (2026-05-14) | v1.1 (2026-05-15) | v1.2 (2026-05-15) | v1.3 (2026-05-19) |
|---|---|---|---|---|
| `drive_id` / `directory_id` | константы из `Visary:BudgetUpload:DriveId/DirectoryId` | парсятся из `ConstructionProject.ProjectFolder` (`«32,40110»`) | то же | то же |
| Кол-во HTTP-вызовов в `UploadAsync` | 3 | **4** (+`GET constructionproject/{id}`) | 4 | 4 + N polling-итераций |
| `BudgetUploadOptions` | `DriveId`/`DirectoryId`/`ImportType` | только `ImportType` | только `ImportType` | только `ImportType` |
| Парсинг ответа `/file_link/by_id` | `link/result/value/token` через `TryGetProperty` (case-sensitive) | то же | **case-insensitive** (`Link`/`Result`/…) | то же |
| Триггер загрузки | ручная кнопка `kind="budget-upload"` | то же | то же | **авто в Apply-фазе финмодели** (кнопка снята, endpoint удалён) |
| Ожидание завершения Visary | нет, fire-and-forget | то же | то же | **polling `typedimportwbs` каждые 3 сек, дедлайн 5 мин** |

⚠️ **Регрессия v1.1→v1.2**: Visary вернул JSON `{"Link":"…"}` в **PascalCase**, `TryGetProperty("link")` его не нашёл, `ExtractLinkToken` свалился в fallback `return raw` и отдал backend-у сериализованный JSON-объект целиком. Это уехало в Visary как `File` → Visary распарсил как Base-64 → 500 «not a valid Base-64 string». Фикс — итерация `EnumerateObject()` + `OrdinalIgnoreCase`.

🔄 **v1.3**: ручная кнопка «Загрузить бюджет в Visary» и `POST /api/imports/{id}/budget-upload` удалены. По правильному порядку бизнес-процесса бюджет должен залиться до создания ГФ Главы 1 (WBS-узлы для `CostItem` появляются в ИСР именно по результатам импорта бюджета). Поэтому теперь `FinModelImportMapper.ApplyAsync` сам вызывает `BudgetVisaryUploader.UploadAndWaitAsync` (`UploadAsync` + polling `GET /api/visary/crud/typedimportwbs/{id}`); ГФ запускается только при статусах «Закончен успешно» / «Закончен с предупреждениями». См. [doc 94](94-finmodel-auto-budget-before-gf.md).

В разделе «Сформированные файлы» после Apply остаётся **только** кнопка **«Скачать»** — `Бюджет_{id}.xlsx` для проверки/back-up. Action-кнопок UI больше не показывает.

---

## 🌐 API Visary, который мы вызываем

Все запросы — `Authorization: Bearer <JWT>` (тот же токен из корневого `.env`).

### 0) Получение папки проекта *(новое в v1.1)*

```http
GET {visary}/api/visary/crud/constructionproject/{projectId}
```

Из ответа нас интересует **строковое** поле `ProjectFolder`:

```json
{
  "ID": 4584,
  "Title": "...",
  "ProjectFolder": "32,40110",
  ...
}
```

- Формат — `"<driveId>,<directoryId>"` (две положительных целых через запятую).
- В `ICrudClient` уже есть `GetProjectByIdFullAsync(id)` → `ConstructionProjectFull` со строковым `ProjectFolder` (см. [Dto/Generated/ConstructionProjectFull.cs](../Visary.Api.Client/Dto/Generated/ConstructionProjectFull.cs)).
- Парсинг и валидация — `BudgetVisaryUploader.ParseProjectFolder`. Fallback на конфиг **отсутствует**: пустое/невалидное значение — это конфигурационная ошибка в карточке проекта, а не повод заливать в случайную папку.

### 1) Загрузка файла в ФХ

```http
POST {visary}/api/files/files/upload?drive_id={drive}&directory_id={dir}
Content-Type: multipart/form-data; boundary=...

------boundary
Content-Disposition: form-data; name="upload"; filename="Бюджет_…xlsx"
Content-Type: application/vnd.openxmlformats-officedocument.spreadsheetml.sheet

<binary xlsx bytes>
------boundary--
```

- **Field name** — `upload` (важно, не `file`).
- **Filename** — UTF-8 кириллица в multipart разрешена.
- **Ответ** — формат непостоянный, наблюдали ТРИ варианта (`ExtractItemId` устойчив ко всем):
  - голое число: `40872`
  - в кавычках: `"40872"`
  - **JSON-массив** с одним элементом: `[40884]` (этот случай словили в проде 2026-05-14, регрессия из `int.TryParse`)
  - теоретически — JSON-объект `{"id":N}`/`{"item_id":N}` (поддержано на всякий случай)
- **Test-окружение**: `drive_id=65`, `directory_id=40870`.

### 2) Получение link-токена для файла

```http
POST {visary}/api/files/link/file_link/by_id?drive_id={drive}&item_id={item}&check_permission=true
```

- Тело пустое.
- Ответ — `string` (либо в кавычках, либо в JSON-обёртке `{"Link":"…"}` — **PascalCase**). `IFileStorageClient.GetFileLinkAsync` обрабатывает оба варианта (см. `ExtractLinkToken`); поиск свойства идёт **case-insensitive**, иначе токен «уезжает» в Visary как сериализованный JSON и валит импорт.
- Этот opaque-токен (Base-64-URL-safe, обычно 200+ символов) идёт в поле `File` запроса typedimportwbs.

### 3) Создание задания импорта

```http
POST {visary}/api/visary/crud/typedimportwbs
Content-Type: application/json

{
  "Project":          { "Title": "...", "ID": <projectId> },
  "ProjectID":        <projectId>,
  "ConstructionSite": { "ID": <siteId> },
  "ConstructionSiteID": <siteId>,
  "ImportType":       10,
  "StartLine":        0,
  "SheetName":        "",
  "File":             "<link-token>"
}
```

- **ImportType=10** — внутренний код Visary для «Бюджет/WBS» (в test-окружении). Конфигурируется через `Visary:BudgetUpload:ImportType` (`BudgetUploadOptions.ImportType`). Это **единственное** значение, оставшееся в `BudgetUploadOptions` после v1.1 — диск/папка теперь приходят из проекта.
- **Ответ** — короткий JSON с `ID` созданной записи.

---

## 🏗️ Архитектура backend

### Поток (v1.3 — авто из Apply финмодели)

```
POST /api/imports/{id}/apply
        │
        ▼
FinModelImportMapper.ApplyAsync
   ├─ ApplyParametersAsync(siteId, paramRows)                ─── params
   │
   ├─ if (budgetRows.Count > 0):                              ─── budget (авто)
   │     using scope (IServiceScopeFactory):
   │        BudgetVisaryUploader.UploadAndWaitAsync(sessionId)
   │           ├─ UploadAsync(sessionId):
   │           │    ├─ 0. CrudClient.GetProjectByIdFullAsync(projectId)      → ProjectFolder
   │           │    ├─ 0.5 ParseProjectFolder("32,40110")                    → (drive, dir)
   │           │    ├─ 1. BudgetXlsxExporter.GenerateAsync(sessionId)        → byte[] xlsx
   │           │    ├─ 2. FileStorageClient.UploadAsync(drive, dir, …)       → itemId
   │           │    ├─ 3. FileStorageClient.GetFileLinkAsync(drive, itemId)  → linkToken
   │           │    └─ 4. CrudClient.CreateTypedImportWbsAsync(…)            → importId
   │           └─ poll loop: CrudClient.GetTypedImportWbsByIdAsync(importId) каждые 3 сек
   │                        дедлайн 5 мин
   │              ⇒ { Success, TimedOut, FinalStatus, CountErrors, CountWarnings }
   │     → budgetUploadOk: bool? (null если budgetRows.Count == 0)
   │
   └─ if (scheduleArticleRows.Count > 0 && quartersRow):       ─── ГФ
         if (budgetUploadOk == false) → пропустить (факт «ГФ не создан» уже в budget_upload_failed)
         else                          → ApplyChapter1ScheduleAsync(siteId, …)
```

### Ключевые места кода

| Компонент | Файл | Что |
|---|---|---|
| `IFileStorageClient` | [FileStorageClient.cs](../Visary.Api.Client/FileStorage/FileStorageClient.cs) | `UploadAsync` + `GetFileLinkAsync`, `ExtractLinkToken` (устойчив к raw-string и JSON-обёртке) |
| `ICrudClient.CreateTypedImportWbsAsync` | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | POST `/api/visary/crud/typedimportwbs` |
| `ICrudClient.GetTypedImportWbsByIdAsync` *(v1.3)* | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | `GET /api/visary/crud/typedimportwbs/{id}` для polling-а статуса |
| DTO запроса/статуса | [VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs) | `TypedImportWbsCreateRequest`, `TypedImportWbsRaw` (+`CountErrors`/`CountWarnings`/`StartDate`/`FinishDate` в v1.3) |
| Мнемоника | [VisaryMnemonics.cs](../Visary.Api.Client/Common/VisaryMnemonics.cs) | `TypedImportWbs = "typedimportwbs"` |
| Конфиг importType | [VisaryOptions.cs](../Visary.Api.Client/VisaryOptions.cs) | `BudgetUploadOptions.ImportType` (дефолт `10`); поля диска/папки удалены в v1.1 |
| Источник drive/dir | [ConstructionProjectFull.cs](../Visary.Api.Client/Dto/Generated/ConstructionProjectFull.cs) → `ProjectFolder` | парсинг — `BudgetVisaryUploader.ParseProjectFolder` |
| DI Visary client | [VisaryClientExtensions.cs](../Visary.Api.Client/VisaryClientExtensions.cs) | `AddHttpClient<IFileStorageClient, FileStorageClient>` |
| Pipeline upload | [BudgetVisaryUploader.cs](../KiloImportService.Api/Budget/BudgetVisaryUploader.cs) | `UploadAsync` оркестрирует 4 HTTP-шага |
| Pipeline upload+wait *(v1.3)* | [BudgetVisaryUploader.cs](../KiloImportService.Api/Budget/BudgetVisaryUploader.cs) | `UploadAndWaitAsync(sessionId, pollInterval=3s, maxWait=5min)`; классификаторы `IsSuccessStatus`/`IsFailureStatus` (case-insensitive по корням слов) |
| Auto-вызов из Apply *(v1.3)* | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `UploadBudgetToVisaryAsync(sessionId, …)` через `IServiceScopeFactory` (мапер Singleton, uploader Scoped) |
| ~~Endpoint~~ *(удалён в v1.3)* | ~~`POST /api/imports/{id}/budget-upload`~~ | теперь не нужен — загрузка происходит автоматически в Apply |
| DI сервиса | [Program.cs](../KiloImportService.Api/Program.cs) | `AddScoped<BudgetVisaryUploader>()` |
| `generatedFiles` *(упрощён в v1.3)* | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) | `BuildGeneratedFilesAsync` — оставлен только `kind="budget-xlsx"` (back-up для проверки); `kind="budget-upload"` снят |

### UI *(упрощён в v1.3)*

- [ApiGeneratedFile](../KiloImportService.Web/src/types/api.ts) — поле `actionUrl?: string \| null` зарезервировано, backend больше не выставляет.
- [SessionGeneratedFiles.tsx](../KiloImportService.Web/src/components/ImportSession/SessionGeneratedFiles.tsx) — только кнопка **«Скачать»** (fetch → blob → `<a download>`). Action-логика (`handleAction`, `buildSuccessMessage`, toast) удалена.

---

## ✅ Правильная реализация

### multipart с правильным именем поля

```csharp
using var form = new MultipartFormDataContent();
var fileContent = new ByteArrayContent(xlsxBytes);
fileContent.Headers.ContentType =
    new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
form.Add(fileContent, "upload", fileName);   // 👈 "upload", не "file"
```

### Ответ upload — толерантный парсер

Visary иногда возвращает голое число (`40872`), иногда — JSON-массив (`[40884]`),
иногда — quoted-строку. Один-в-один шаблон `int.TryParse` ломается на массиве.

```csharp
// НЕПРАВИЛЬНО — пройдёт только на голом числе, упадёт на "[40884]".
var raw = (await response.Content.ReadAsStringAsync(ct)).Trim().Trim('"');
if (!int.TryParse(raw, out var itemId)) throw …;
```

```csharp
// ПРАВИЛЬНО — пробуем все известные форматы по очереди.
// См. ExtractItemId в FileStorageClient.cs.
var raw = (await response.Content.ReadAsStringAsync(ct)).Trim();
var itemId = ExtractItemId(raw)
    ?? throw new InvalidOperationException(
        $"FileStorage upload: не удалось распарсить ответ '{raw}' как item_id.");

// ExtractItemId:
//   1) int.TryParse(raw)                                       → bare int
//   2) если "<...>"                                            → strip + TryParse
//   3) JsonDocument.Parse(raw):
//        Number                                                → GetInt32
//        Array  → root[0] is Number                            → GetInt32
//        Object → property "id"/"ID"/"item_id"/"itemId" is Number → GetInt32
```

### `File` в typedimportwbs — link-токен, НЕ id и НЕ URL

```csharp
var link = await _fileStorage.GetFileLinkAsync(driveId, itemId, true, ct);
var request = new TypedImportWbsCreateRequest { /* ... */ File = link };
```

---

## ❌ Типичная ошибка

### 1. Передать в `File` идентификатор файла (а не link-токен)

```csharp
// НЕПРАВИЛЬНО — Visary вернёт 400/422 «File link required», импорт не запустится.
request.File = itemId.ToString();
```

```csharp
// ПРАВИЛЬНО — отдельный вызов /api/files/link/file_link/by_id, его результат → File.
request.File = await _fileStorage.GetFileLinkAsync(driveId, itemId, true, ct);
```

### 2. Имя поля multipart `file` вместо `upload`

```csharp
// НЕПРАВИЛЬНО — Visary не находит файл, отвечает 400 «Expected field 'upload'».
form.Add(fileContent, "file", fileName);
```

### 3. Hardcoded или конфиговый `drive_id`/`directory_id`

```csharp
// НЕПРАВИЛЬНО (v1.0, deprecated) — все проекты льются в одну и ту же папку.
// Бизнес-процесс: каждый проект имеет свою папку ФХ, заданную в карточке.
var opt = _visaryOptions.CurrentValue.BudgetUpload;
await _fileStorage.UploadAsync(bytes, name, mime, opt.DriveId, opt.DirectoryId, ct);
```

```csharp
// ПРАВИЛЬНО (v1.1) — drive/dir берём из ProjectFolder выбранного проекта.
var project = await _crud.GetProjectByIdFullAsync(projectId, ct);
var (driveId, directoryId) = ParseProjectFolder(project.ProjectFolder, projectId);
await _fileStorage.UploadAsync(bytes, name, mime, driveId, directoryId, ct);
```

⚠️ Парсер `ParseProjectFolder` не имеет fallback: на пустом/невалидном `ProjectFolder`
бросаем `InvalidOperationException` с указанием `projectId` и ожидаемого формата
(«32,40110»). Это сознательно — лучше явная ошибка, чем заливка в случайную папку.

### 4. Парсить ответ link-эндпоинта как только raw-строку

Visary возвращает либо `"abc..."` (quoted-string), либо `{"Link":"abc..."}` (**PascalCase!**) — зависит от версии/типа диска. `ExtractLinkToken` должен пробовать обе формы И искать свойство объекта **case-insensitive**.

```csharp
// ❌ НЕПРАВИЛЬНО (regress 2026-05-15): TryGetProperty чувствителен к регистру.
//    Ответ {"Link":"abc..."} → не находим "link" → fallback `return raw` отдаёт
//    весь JSON-объект как строку → Visary падает «not valid Base-64».
foreach (var name in new[] { "link", "result", "value", "token" })
    if (root.TryGetProperty(name, out var el) ...) return ...;

// ✅ ПРАВИЛЬНО: итерируем свойства и сравниваем имя case-insensitive.
foreach (var prop in root.EnumerateObject())
{
    if (prop.Value.ValueKind != JsonValueKind.String) continue;
    foreach (var w in wanted)
        if (string.Equals(prop.Name, w, StringComparison.OrdinalIgnoreCase))
            return prop.Value.GetString();
}
```

### 5. Считать ответ upload голым числом (regression 2026-05-14)

```csharp
// НЕПРАВИЛЬНО — `int.TryParse("[40884]")` возвращает false → InvalidOperationException
// «не удалось распарсить ответ '[40884]' как int item_id».
var raw = (await response.Content.ReadAsStringAsync(ct)).Trim().Trim('"');
if (!int.TryParse(raw, out var itemId)) throw …;
```

```csharp
// ПРАВИЛЬНО — ExtractItemId пробует bare-int, quoted-int, JSON-array, JSON-object
// (см. секцию «Правильная реализация» выше).
var itemId = ExtractItemId(raw) ?? throw …;
```

**Урок**: HAR-снимки фиксируют ОДИН формат ответа в конкретный момент; продакшен может вернуть другой (массив вместо скаляра — частый случай при сериализации коллекции). Парсер должен быть толерантен к минорным вариациям формы JSON, иначе ловим интеграционные регрессии после деплоя.

---

## 📍 Применение в проекте

| Шаг | Файл / endpoint |
|---|---|
| User: «Apply» сессии финмодели | [SessionDetailsPage](../KiloImportService.Web/src/pages/SessionDetailsPage.tsx) → `POST /api/imports/{id}/apply` |
| Оркестратор Apply | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) → `ApplyAsync` / `UploadBudgetToVisaryAsync` |
| Upload + polling | [BudgetVisaryUploader.cs](../KiloImportService.Api/Budget/BudgetVisaryUploader.cs) → `UploadAndWaitAsync` |
| Генерация XLSX | [BudgetXlsxExporter.cs](../KiloImportService.Api/Budget/BudgetXlsxExporter.cs) (см. [doc 78](78-budget-xlsx-export.md)) |
| Заливка в ФХ | [FileStorageClient.cs](../Visary.Api.Client/FileStorage/FileStorageClient.cs) |
| TypedJournal create/status | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) — `CreateTypedImportWbsAsync`, `GetTypedImportWbsByIdAsync` |
| Back-up: «Скачать XLSX» | [SessionGeneratedFiles.tsx](../KiloImportService.Web/src/components/ImportSession/SessionGeneratedFiles.tsx) → `GET /api/imports/{id}/budget-xlsx` |

---

## 🎯 Чек-лист

- [ ] При обновлении Visary — повторно проверить формат ответа `POST /api/files/files/upload` (видели: bare-int, quoted-int, `[id]`-массив). Если появится новый вариант — расширить `ExtractItemId` в [FileStorageClient.cs](../Visary.Api.Client/FileStorage/FileStorageClient.cs).
- [ ] При переходе на prod-окружение: сверить только `BudgetUploadOptions.ImportType` со справочниками Visary (`Visary:BudgetUpload:ImportType` / env `Visary__BudgetUpload__ImportType`). Папка диска **не настраивается** — она приходит из `ConstructionProject.ProjectFolder` (поэтому в каждом проекте оно должно быть заполнено корректно).
- [ ] Token из `.env` имеет права на запись в папку из `ProjectFolder` (`check_permission=true` — Visary проверит).
- [ ] Для сессий без `visaryProjectId` / `visarySiteId` — `BudgetVisaryUploader.UploadAsync` бросит `InvalidOperationException`, и `UploadBudgetToVisaryAsync` в маппере добавит **одну** `budget_upload_error` в file-level errors сессии. Сообщение содержит «что было сделано до бюджета» + текст исключения + «ГФ Главы 1 не созданы» — ГФ при этом пропускается без отдельной записи.
- [ ] Если у выбранного проекта `ProjectFolder` пустой / не парсится в `«driveId,directoryId»` — аналогично: file-level error, ГФ пропущен. Это **не баг сервиса импорта**, а конфиг карточки проекта в Visary (поправить в Visary UI или через `PATCH /api/visary/crud/constructionproject/{id}`).
- [ ] При обновлении Visary — пересмотреть тексты `Status` (классификаторы `IsSuccessStatus` / `IsFailureStatus` матчат по корням «успеш»/«предупреж»/«ошибк»/`error`/`fail`/`complet`/`warning`; если корни изменятся — править здесь, DTO не трогать).
- [ ] Поллинг-параметры (3 сек / 5 мин) сейчас хардкод в `UploadAndWaitAsync`. Если будут большие бюджеты — параметризовать через `appsettings`.
