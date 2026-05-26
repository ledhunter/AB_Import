# 🗂️ Финмодель → второй файл «План» + создание `fmmodel` в Visary

## 📋 Описание

FinModel-импорт принимает **два файла**:

| # | Файл | Обяз. | Что содержит |
|---|------|-------|--------------|
| 1 | основной | ✅ | то же, что и раньше: листы `Inputs` (параметры/бюджет) + `Control` (этапы / `Номер КД`) |
| 2 | «План» | ❌ опц. | XLSX с листом `План` — краевые квартальные значения для создания сущности `fmmodel` в Visary |

После применения параметров/бюджета/ГФ маппер дополнительно вызывает `EnsureFmModelAsync`:
1. Если файла №2 нет → info `fmmodel_skipped_no_plan_file`, ничего не создаём.
2. Открываем XLSX через `IFileStorage`, ищем лист `План`, читаем строки `Год` (обычно r3) и `Квартал` (обычно r5).
3. Сканируем колонки `C..` слева направо с forward-fill года (год лежит только в первой колонке группы из 4). **Первая** и **последняя** валидная пара (Год, Квартал) → `PeriodStart` / `PeriodEnd` в формате `"{Year}Q{N}"`.
4. Pre-check `listview/fmmodel` с фильтром `(ABProjectID=…, ABConstructionSiteID=…)` — есть запись → skip с `fmmodel_skipped_already_exists`.
5. POST `/crud/fmmodel` с `Title="Модель из эксель файла"`, `ProjectCode=<Title проекта>`, `ABProjectID`, `ABConstructionSiteID`, `PeriodStart`, `PeriodEnd`.

---

## ✅ Правильная реализация

### Visary client

[VisaryMnemonics.cs](../Visary.Api.Client/Common/VisaryMnemonics.cs):
```csharp
public const string FmModel = "fmmodel";
```

[VisaryEntities.cs](../Visary.Api.Client/Dto/VisaryEntities.cs) + [VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs):
```csharp
public sealed class FmModelRaw { /* ID, Title, ProjectCode, ABProjectID, ABConstructionSiteID, PeriodStart, PeriodEnd, RowVersion */ }
public sealed class FmModelCreateRequest { /* Title, ProjectCode, ABProjectID, ABConstructionSiteID, PeriodStart, PeriodEnd */ }
```

[ICrudClient.CreateFmModelAsync](../Visary.Api.Client/CRUD/CrudClient.cs) — `POST /api/visary/crud/fmmodel`.
[IListViewClient.FindFmModelsAsync](../Visary.Api.Client/ListView/ListViewClient.cs) — `listview/fmmodel` с `Filter [["ABProjectID","=",X],"and",["ABConstructionSiteID","=",Y]]`.

### Storage слой

[ImportFileSnapshot](../KiloImportService.Api/Data/Entities/ImportFileSnapshot.cs) расширен 3 опциональными полями: `SecondaryRelativePath`, `SecondaryFileName`, `SecondarySizeBytes`. Миграция `AddSecondaryFileToSnapshot` — 3 nullable-колонки на таблицу `import.import_file_snapshots`.

[ImportContext](../KiloImportService.Api/Domain/Mapping/IImportMapper.cs) получил опциональное поле `SecondaryFileRelativePath`. Pipeline берёт его из `session.FileSnapshot.SecondaryRelativePath` при создании контекста для `ValidateAsync` и `ApplyAsync`.

### API + Pipeline

[ImportsController.Upload](../KiloImportService.Api/Controllers/ImportsController.cs):
```csharp
[FromForm] IFormFile? secondaryFile,
```

[ImportPipeline.UploadAsync](../KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs):
```csharp
Stream? secondaryFileStream = null,
string? secondaryFileName = null
```
Pipeline сохраняет второй файл в `IFileStorage` отдельным объектом и записывает путь/имя/размер в `ImportFileSnapshot`.

### Маппер

[FinModelImportMapper](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs):
- Конструктор принимает `IFileStorage` (Singleton-safe).
- `EnsureFmModelAsync` вызывается **в начале `ApplyAsync`**, ДО проверки `validRows.Count == 0` — создание `fmmodel` ортогонально mapped-строкам, должно работать даже если параметрический поток пуст.
- `ReadPlanPeriods(Stream)` — internal-static, тестируемый изолированно. Использует `ClosedXML`.
- `ParseQuarter(string)` — internal-static, парсит «1 кв» / «2 квартал» / «3кв» → 1..4.

```csharp
internal static FinModelPlanPeriods ReadPlanPeriods(Stream xlsxStream)
{
    using var wb = new XLWorkbook(/* copied into MemoryStream */);
    var sheet = wb.Worksheets.FirstOrDefault(ws => string.Equals(ws.Name?.Trim(), "План", StringComparison.OrdinalIgnoreCase))
        ?? throw new FinModelPlanParseException("Лист «План» не найден");
    // Скан первых 15 строк колонки A: ищем «Год» и «Квартал».
    // Forward-fill года, парсинг квартала, краевые пары C..lastUsedColumn.
}
```

### Frontend

[FileUpload.tsx](../KiloImportService.Web/src/components/FileUpload/FileUpload.tsx) расширен опциональными props: `secondaryFile`, `onSecondaryFileSelect`, `secondaryLabel`, `secondaryHint`. Если они переданы — под основным uploader-ом отрисовывается компактный второй слот с кнопкой «Прикрепить файл» (FormData получает поле `secondaryFile`).

[App.tsx](../KiloImportService.Web/src/App.tsx) рендерит секундарный слот **только** при `importType === 'finmodel'`. Для остальных типов impl остаётся идентичной.

[importsService.ts](../KiloImportService.Web/src/services/importsService.ts) — `UploadImportPayload.secondaryFile` опциональный, прокидывается в `FormData.set('secondaryFile', ...)`.

---

## ⚠️ Важно

1. **`EnsureFmModelAsync` НЕ зависит от validRows.** Если параметрический поток пуст, но второй файл загружен — Финмодель всё равно создаётся. Поэтому вызов до `if (validRows.Count == 0) return`.

2. **Идемпотентность — единственная защита от дубликатов.** На сервере Visary нет уникальности по (`ABProjectID`, `ABConstructionSiteID`). Любая ошибка `FindFmModelsAsync` (сеть, 500) → пишем `fmmodel_precheck_failed` и **НЕ создаём** запись — чтобы не породить дубликат.

3. **Опечатка в спецификации.** Заказчик уточнил: формула `{Year}Q{N}` без сдвига (изначальный пример «2024 + 1кв → 2025Q1» был типо). Год берётся как-есть, квартал из «1 кв»..«4 кв» в 1..4.

4. **Колонка B на листе «План» — «Сумма», НЕ квартал.** Сканирование начинается с C (=3). Если бы начинали с B — «Сумма» интерпретировалось бы как первая колонка периода → крах форматирования (`{Year}Q?`).

5. **`ProjectCode` ≈ `ConstructionProject.Title`.** В HAR-примере заказчика `"ProjectCode":"Тест ДОУ"` — это видимое название проекта, не `ConstructionProjectNumber`. Если `GetProjectByIdFullAsync` упал — отправляем `null` (поле опциональное в request DTO).

6. **`IFileStorage` инжектируется напрямую** (Singleton `LocalFileStorage` без scoped-зависимостей). Это отличие от `IBudgetVisaryUploader` (Scoped) — для него используется паттерн `IServiceScopeFactory`. См. doc 97.

7. **`Include(FileSnapshot)` в `ApplyCoreAsync`.** Раньше Apply не подтягивал navigation property `FileSnapshot` — теперь нужно, чтобы `SecondaryRelativePath` попал в `ImportContext`.

8. **External-link cleanup для «План»-файла обязателен.** Шаблоны заказчика часто содержат формулы со ссылками на сетевые файлы (`file:////Alt/intern/.../[XYZ.xls]Sheet`) — ClosedXML на них падает с «Unable to determine token». В `ReadPlanPeriods` тот же паттерн retry, что в [XlsxParser](../KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs) (doc 81): читаем bytes ОДИН раз, пробуем `XLWorkbook`, на `IsExternalLinkError` → `StripExternalLinks(bytes)` → retry. `XlsxParser.IsExternalLinkError` и `XlsxParser.StripExternalLinks` сделаны `internal static` ради переиспользования из мапера (вместо копипасты). Кэшированные `<v>` в ячейках остаются — а «Год»/«Квартал» вообще не формулы.

9. **⚠️ `listview/fmmodel` — нестандартный контракт по сравнению с другими listview.** Сервер требует:
   - `Filter` — JSON-**строка** с экранированным массивом условий (не нативный массив, как у `shareagreement`/`costitem`/`companygroup`/...). Голый массив → `400 Bad Request`.
   - `Scope` — обязательный JSON-string с `{"EntityId":<projectId>,"FilterName":"ConstructionProject_ConstructionProject_FMModels_IndirectAssociation"}`. Без него — `400`.
   - `Columns` должны включать ровно тот набор, что шлёт Visary UI (12 полей: `ID, Title, Portfolio, ProjectCode, ConstructionSiteCode, ABProjectID, ABConstructionSiteID, PeriodStart, PeriodEnd, CreditLineCode, ABCreditLineID, CommisioningPeriod`). Минимальный набор (только ID+Title+пары AB) сервер тоже отбивал 400.

   Контракт подсмотрен в HAR Visary UI после первого 400-инцидента. Реализация в [ListViewClient.FindFmModelsAsync](../Visary.Api.Client/ListView/ListViewClient.cs). Поскольку `Scope` уже ограничивает выборку проектом, фильтровать по `ABProjectID` избыточно — оставлены только `Title contains "Модель из эксель файла"` + `ABConstructionSiteID = <siteId>`. Это совпадает с семантикой идемпотентности "по `(ProjectID, SiteID, Title)`" (заказчик подтвердил в #4 уточнений).

   **Lesson learned**: при добавлении новой Visary listview-сущности — сначала снять HAR на боевом UI, ТОЛЬКО потом писать клиент. Сделать «по аналогии с другими» — лотерея на сервере (см. также doc 53 для общего паттерна snapshot-аудита).

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО — вызывать EnsureFmModel в конце ApplyAsync.
public async Task<ApplyResult> ApplyAsync(...)
{
    if (validRows.Count == 0) return new ApplyResult(0, errors); // 💥 короткое замыкание
    ...
    await EnsureFmModelAsync(...);  // 💥 никогда не сработает, если Inputs пуст
    return new ApplyResult(applied, errors, ...);
}
```

```csharp
// НЕПРАВИЛЬНО — на pre-check ошибке создавать без проверки.
try { var existing = await _listViewClient.FindFmModelsAsync(...); ... }
catch { /* ignore */ }
await _visaryClient.CreateFmModelAsync(...);  // 💥 дубликат при flaky сети
```

```csharp
// НЕПРАВИЛЬНО — сканировать с колонки B.
for (int c = 2; c <= last; c++)  // 💥 B="Сумма" не пройдёт ParseQuarter, но идея неверна
```

```csharp
// НЕПРАВИЛЬНО — забыть Include(FileSnapshot) в Apply.
var session = await _serviceDb.Sessions.FirstAsync(...);
// session.FileSnapshot == null → SecondaryRelativePath потерян 💥
```

```csharp
// НЕПРАВИЛЬНО — переиспользовать stream после OpenReadAsync без копирования.
await using var s = await _fileStorage.OpenReadAsync(path, ct);
using var wb = new XLWorkbook(s);  // 💥 ClosedXML требует seek-ability;
                                    //    HTTP/network-stream — forward-only
// Правильно — скопировать в MemoryStream сначала (см. ReadPlanPeriods).
```

```tsx
// НЕПРАВИЛЬНО — слать secondaryFile для всех типов импорта.
secondaryFile: secondaryFile  // 💥 для rooms backend проигнорирует, но семантически грязно
// Правильно — гейтить:
secondaryFile: importType === 'finmodel' ? secondaryFile : null,
```

---

## 📍 Применение в проекте

| Слой | Файл | Метод/блок |
|------|------|------------|
| Visary client (мнемоника) | `Visary.Api.Client/Common/VisaryMnemonics.cs` | `FmModel = "fmmodel"` |
| Visary client (DTO) | `Visary.Api.Client/Dto/VisaryEntities.cs` | `FmModelRaw` |
| Visary client (DTO) | `Visary.Api.Client/Dto/VisaryCrudRequests.cs` | `FmModelCreateRequest` |
| Visary client (CRUD) | `Visary.Api.Client/CRUD/CrudClient.cs` | `CreateFmModelAsync` |
| Visary client (ListView) | `Visary.Api.Client/ListView/ListViewClient.cs` | `FindFmModelsAsync` |
| Entity + миграция | `KiloImportService.Api/Data/Entities/ImportFileSnapshot.cs` + `Migrations/20260525115407_AddSecondaryFileToSnapshot.cs` | `SecondaryRelativePath`, `SecondaryFileName`, `SecondarySizeBytes` |
| ImportContext | `KiloImportService.Api/Domain/Mapping/IImportMapper.cs` | `SecondaryFileRelativePath` (опц.) |
| Controller | `KiloImportService.Api/Controllers/ImportsController.cs` | `Upload` — `IFormFile? secondaryFile` |
| Pipeline | `KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs` | `UploadAsync` (доп. stream/name), `ApplyCoreAsync` (`Include(FileSnapshot)` + проброс пути в ctx) |
| Маппер | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs` | `EnsureFmModelAsync`, `ReadPlanPeriods`, `ParseQuarter` |
| Frontend (UI) | `KiloImportService.Web/src/components/FileUpload/FileUpload.tsx` | secondary props + render-блок |
| Frontend (state) | `KiloImportService.Web/src/App.tsx` | `secondaryFile` state, гейтинг по `finmodel` |
| Frontend (service) | `KiloImportService.Web/src/services/importsService.ts` | `UploadImportPayload.secondaryFile` + `FormData` |
| Тесты | `KiloImportService.Api.Tests/Mapping/FinModelFmModelTests.cs` | ParseQuarter, ReadPlanPeriods edge-picking + missing-sheet/headers, ApplyAsync no-file/happy/existing/parse-error |
| Test helper | `KiloImportService.Api.Tests/Mapping/TestFileStorage.cs` | `TestFileStorage` (in-memory) + `NoopFileStorage` |

---

## 📅 История изменений

- **v1.1 (2026-05-26)** — после создания `fmmodel` импорт автоматически достраивает
  каскад: версия (`fmmodelversion`) + входные данные (`inputdata`) + link. Парсер
  «План» расширен на категории помещений (квартиры/нежилые/м/м) и материализацию
  всех точек (Период × Категория → Summ/Amount/Cost). См.
  [doc 112](./112-finmodel-version-and-inputdata.md).

## 🔗 Связанная документация

- [doc 23 — finmodel-import](./23-finmodel-import.md) — изначальная Финмодель (один файл).
- [doc 112 — finmodel-version-and-inputdata](./112-finmodel-version-and-inputdata.md) — версия + входные данные после создания fmmodel.
- [doc 82 — visary-file-storage-upload](./82-visary-file-storage-upload.md) — образец работы с `IFileStorage` (загрузка XLSX-бюджета в Visary).
- [doc 94 — finmodel-auto-budget-before-gf](./94-finmodel-auto-budget-before-gf.md) — паттерн «вызвать что-то из мапера через captive scope».
- [doc 97 — rooms-apply-tests-and-budget-uploader-interface](./97-rooms-apply-tests-and-budget-uploader-interface.md) — урок про in-memory DB Guid в делегате (тестовые ловушки).
- [doc 109 — finmodel-prechecks-wbs-and-gf](./109-finmodel-prechecks-wbs-and-gf.md) — идемпотентность через pre-check listview.

---

## 🎯 Чек-лист

- [ ] `IFormFile? secondaryFile` опциональный в `POST /api/imports`; пустое поле = 0 файлов
- [ ] `ImportFileSnapshot` хранит путь+имя+размер второго файла (3 nullable-колонки)
- [ ] `ImportContext.SecondaryFileRelativePath` пробрасывается на Validate и Apply (с `Include(FileSnapshot)`)
- [ ] Маппер: `EnsureFmModelAsync` вызывается до проверки `validRows.Count == 0`
- [ ] Без файла №2 → `fmmodel_skipped_no_plan_file`, без вызовов Visary
- [ ] Парсер «План»: ищет «Год» и «Квартал» в первых 15 строках, forward-fill года, скан с колонки C
- [ ] Pre-check `FindFmModelsAsync` обязателен перед `CreateFmModelAsync`; exception → пропуск, не создание
- [ ] Frontend: второй слот появляется ТОЛЬКО при `importType === 'finmodel'`
- [ ] Все 32 FinModel-теста зелёные
