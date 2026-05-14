# 📤 Автозагрузка бюджета в Visary FileStorage + TypedJournal-импорт

## 📋 Описание

**Статус**: 🟢 v1.0 — программная заливка XLSX-бюджета в ФХ Visary + создание `typedimportwbs`.
**Дата**: 2026-05-14
**Зависит от**: [78-budget-xlsx-export.md](78-budget-xlsx-export.md) (генерация XLSX).
**Источники API**: `Context/har файл по загрузке бюджета в папку ФХ.txt`, `Context/har импорт бюджета.txt`.

После Apply сессии «Финмодель» backend умеет **двумя кнопками** в разделе «Сформированные файлы»:
- **Скачать** — отдаёт `Бюджет_{id}.xlsx` для ручного импорта (старое поведение, v1.1).
- **Загрузить** *(новое)* — заливает тот же XLSX в файловое хранилище Visary + создаёт `typedimportwbs` (TypedJournal-задание), которое Visary обрабатывает в фоне.

---

## 🌐 API Visary, который мы вызываем

Все запросы — `Authorization: Bearer <JWT>` (тот же токен из корневого `.env`).

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
- Ответ — `string` (либо в кавычках, либо в JSON-обёртке `{"link":"…"}`). `IFileStorageClient.GetFileLinkAsync` обрабатывает оба варианта (см. `ExtractLinkToken`).
- Этот opaque-токен идёт в поле `File` запроса typedimportwbs.

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

- **ImportType=10** — внутренний код Visary для «Бюджет/WBS» (в test-окружении). Hardcoded в `BudgetUploadOptions.ImportType` и конфигурируется через `Visary:BudgetUpload:ImportType`.
- **Ответ** — короткий JSON с `ID` созданной записи.

---

## 🏗️ Архитектура backend

### Поток

```
POST /api/imports/{id}/budget-upload
        │
        ▼
BudgetVisaryUploader.UploadAsync(sessionId)
   ├─ 1. BudgetXlsxExporter.GenerateAsync(sessionId)        → byte[] xlsx
   ├─ 2. FileStorageClient.UploadAsync(...)                 → int itemId
   ├─ 3. FileStorageClient.GetFileLinkAsync(drive, itemId)  → string linkToken
   └─ 4. CrudClient.CreateTypedImportWbsAsync(...)          → int importId
   ⇒ { fileStorageItemId, typedImportWbsId, fileName }
```

### Ключевые места кода

| Компонент | Файл | Что |
|---|---|---|
| `IFileStorageClient` (НОВЫЙ) | [FileStorageClient.cs](../Visary.Api.Client/FileStorage/FileStorageClient.cs) | `UploadAsync` + `GetFileLinkAsync`, `ExtractLinkToken` (устойчив к raw-string и JSON-обёртке) |
| `ICrudClient.CreateTypedImportWbsAsync` | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | POST `/api/visary/crud/typedimportwbs` |
| DTO запроса | [VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs) | `TypedImportWbsCreateRequest`, `TypedImportWbsRaw` |
| Мнемоника | [VisaryMnemonics.cs](../Visary.Api.Client/Common/VisaryMnemonics.cs) | `TypedImportWbs = "typedimportwbs"` |
| Конфиг drive/dir/importType | [VisaryOptions.cs](../Visary.Api.Client/VisaryOptions.cs) | `BudgetUploadOptions` (дефолты для test-окружения: 65/40870/10) |
| DI Visary client | [VisaryClientExtensions.cs](../Visary.Api.Client/VisaryClientExtensions.cs) | `AddHttpClient<IFileStorageClient, FileStorageClient>` |
| Pipeline | [BudgetVisaryUploader.cs](../KiloImportService.Api/Budget/BudgetVisaryUploader.cs) | `UploadAsync` оркестрирует все 4 шага |
| Endpoint | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) | `POST /api/imports/{id}/budget-upload` (200 → `{fileStorageItemId, typedImportWbsId, fileName}`) |
| DI сервиса | [Program.cs](../KiloImportService.Api/Program.cs) | `AddScoped<BudgetVisaryUploader>()` |
| `generatedFiles` | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) | `BuildGeneratedFilesAsync` — добавлен второй элемент `kind="budget-upload"` рядом с `kind="budget-xlsx"` |

### UI

- API-тип [ApiGeneratedFile](../KiloImportService.Web/src/types/api.ts) расширен полем `actionUrl?: string \| null`; `downloadUrl` теперь `string \| null`.
- Компонент [SessionGeneratedFiles.tsx](../KiloImportService.Web/src/components/ImportSession/SessionGeneratedFiles.tsx) распознаёт оба варианта:
  - есть `downloadUrl` → кнопка **«Скачать»** (старый паттерн: fetch → blob → `<a download>`);
  - есть только `actionUrl` → кнопка **«Загрузить»** (primary), POST → toast с ID импорта.

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

### 3. Hardcoded `drive_id`/`directory_id` в коде

```csharp
// НЕПРАВИЛЬНО — на проде ID других папок, требуется пересборка.
const int DriveId = 65;
const int DirId = 40870;
```

```csharp
// ПРАВИЛЬНО — в BudgetUploadOptions, конфигурируются через
// Visary:BudgetUpload:DriveId / DirectoryId / ImportType в appsettings или .env.
var opt = _visaryOptions.CurrentValue.BudgetUpload;
```

### 4. Парсить ответ link-эндпоинта как только raw-строку

Visary может вернуть либо `"abc..."`, либо `{"link":"abc..."}` (зависит от версии). `ExtractLinkToken` пробует обе формы — fallback не должен ломаться.

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
| User: «Загрузить бюджет в Visary» (кнопка) | [SessionGeneratedFiles.tsx](../KiloImportService.Web/src/components/ImportSession/SessionGeneratedFiles.tsx) |
| POST `/api/imports/{id}/budget-upload` | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) — `UploadBudgetToVisary` |
| Оркестратор | [BudgetVisaryUploader.cs](../KiloImportService.Api/Budget/BudgetVisaryUploader.cs) |
| Генерация XLSX | [BudgetXlsxExporter.cs](../KiloImportService.Api/Budget/BudgetXlsxExporter.cs) (см. [doc 78](78-budget-xlsx-export.md)) |
| Заливка в ФХ | [FileStorageClient.cs](../Visary.Api.Client/FileStorage/FileStorageClient.cs) |
| TypedJournal | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) — `CreateTypedImportWbsAsync` |

---

## 🎯 Чек-лист

- [ ] При обновлении Visary — повторно проверить формат ответа `POST /api/files/files/upload` (видели: bare-int, quoted-int, `[id]`-массив). Если появится новый вариант — расширить `ExtractItemId` в [FileStorageClient.cs](../Visary.Api.Client/FileStorage/FileStorageClient.cs).
- [ ] При переходе на prod-окружение: сверить `BudgetUploadOptions.DriveId`, `DirectoryId`, `ImportType` со справочниками Visary; переопределить через `Visary:BudgetUpload:*` в `appsettings.json` или env-переменными `Visary__BudgetUpload__DriveId` и т.д.
- [ ] Token из `.env` имеет права на запись в выбранную папку ФХ (`check_permission=true` — Visary проверит).
- [ ] Не использовать `kind="budget-upload"` для сессий, у которых `visaryProjectId`/`visarySiteId` пустые — `BudgetVisaryUploader.UploadAsync` бросит `InvalidOperationException` (endpoint вернёт 400).
- [ ] Для отладки: статус задания доступен через `GET /api/visary/crud/typedimportwbs/{id}` — пока этот метод не обёрнут в `ICrudClient`; при необходимости добавить по аналогии с `GetWbsByIdAsync`.
- [ ] Поллинг статуса импорта (если потребуется в UI) — отдельная задача; сейчас при успешном создании показываем toast с ID и не отслеживаем дальнейший статус.
