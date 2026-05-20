# 🔁 Финмодель → автоматический бюджет перед ГФ Главы 1

## 📋 Описание

**Статус**: 🟢 v1.3 — реализовано.
**Дата**: 2026-05-19 (v1.0) · 2026-05-20 (v1.1, v1.2, v1.3).

**v1.3 (2026-05-20)** — `typedimportwbs.Status` приходит **числом**, заказчик
подтвердил кодовую таблицу:

| Код | Семантика | Класс |
|-----|-----------|-------|
| 10 | Новый | in-progress |
| 20 | Закончен успешно | **success** (ГФ запускаем) |
| 30 | Закончен с ошибками | **failure** |
| 40 | В обработке | in-progress |
| 50 | Закончен с предупреждением | **success** (ГФ запускаем) |

После v1.2 polling уже не падал на JsonException, но `ExtractStatusText(Number)`
возвращал `"50"` — и `IsSuccessStatus` искал в нём корень `"успеш"`, не находил,
крутил до тайм-аута (инцидент typedimportwbs ID=9443: Visary импорт за секунды,
у нас 5 мин в `budget_upload_timeout` с «последний статус: «50»»).

Фикс v1.3 — словарь `StatusCodeLabels` + `HashSet SuccessCodes={20,50}` /
`FailureCodes={30}` в [BudgetVisaryUploader.cs](../KiloImportService.Api/Budget/BudgetVisaryUploader.cs):

- `IsSuccessStatus`/`IsFailureStatus` сначала пытаются прочитать числовой код
  (`TryGetStatusCode`: либо `Number`, либо `Object.ID` — на случай обёртки `{ID,Title}`),
  применяют таблицу. Если значение не числовое — fallback на старый текстовый
  матч по корням слов (страховка: Visary вдруг начал слать строку).
- `ExtractStatusText` для числа отдаёт человекочитаемое название из таблицы
  (для лога и UI: вместо «50» теперь «Закончен с предупреждением»). Неизвестный
  код → «Код N» (видно, что Visary добавил статус).
- `FinalStatus` в `BudgetVisaryUploadAndWaitResult` — это уже расшифрованный
  текст, попадает в `budget_upload_*` row-error.

**v1.2 (2026-05-20)** — критический фикс polling-а статуса `typedimportwbs`. Visary
шлёт поле `Status` **не строкой**: наблюдались число и объект-обёртка. Старое DTO
`TypedImportWbsRaw.Status: string?` валилось с `JsonException` при каждом GET →
`catch (Exception ex)` молча гасил исключение, snapshot оставался `null`, polling
крутился до тайм-аута (5 мин) и пользователь получал `budget_upload_timeout`, хотя в
Visary импорт давно «Закончен успешно». Инцидент: typedimportwbs ID=9442, ≈100
безмолвных провалов в логах backend.

Фикс:
- `TypedImportWbsRaw.Status` теперь `System.Text.Json.JsonElement?` — тот же паттерн,
  что в проекте уже применён для `RoomFull.RoomCategory`, `ConstructionSiteFull.Status`
  (см. [doc 56](56-visary-dto-deserialization-pitfalls.md)).
- `BudgetVisaryUploader.ExtractStatusText(JsonElement?)` достаёт текстовое
  представление: строка → как есть; объект `{Title}/{Name}/{Caption}` → значение
  этого поля; число → строковое представление; иначе — `null`. Классификаторы
  `IsSuccessStatus`/`IsFailureStatus` работают поверх извлечённого текста (корни
  слов остались те же).
- Каждая итерация polling-а пишет в Debug raw-форму статуса (`GetRawText()` + `ValueKind`)
  — при будущем изменении контракта Visary видно сразу, в какой форме приходит поле.
- При ошибке опроса логируется тип и текст исключения — без этого до v1.2 видели
  только обезличенный warning «попробуем снова», что и затянуло диагностику.

**v1.1 (2026-05-20)** — при провале импорта бюджета в Visary («Закончен с ошибками» /
timeout / сетевой сбой) `FinModelImportMapper` теперь пишет **одну консолидированную
file-level row-error** вместо двух разрозненных (`budget_upload_failed` +
`schedule_skipped_budget_failed`). Сообщение содержит три блока:

1. **Что было сделано до бюджета** — «параметры объекта применены» (если
   `ApplyParametersAsync` вернул успех) либо «не применялись».
2. **Почему импорт бюджета не прошёл** — статус Visary + `CountErrors`/`CountWarnings`
   из `TypedImportWbsRaw` (для failure) / «не завершился за отведённое время» (для
   timeout) / текст исключения (для сетевого/прочего сбоя). Везде — ссылка на
   `typedimportwbs ID=…` для просмотра деталей в Visary.
3. **ГФ Главы 1 не созданы** — если `scheduleArticleRows` были запланированы;
   иначе явно «ГФ не запрашивался».

Удалён `schedule_skipped_budget_failed` (был отдельной row-error) — теперь факт
«ГФ не создан» включён в основное сообщение. Логика skip-а ГФ при failure-е
бюджета сохранена. Помощник: `BuildBudgetFailureSummary(paramsApplied,
schedulePending, typedImportWbsId, finalStatus, countErrors, countWarnings,
timedOut, exceptionMessage)` в [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs).
**Зависит от**:
- [82-visary-file-storage-upload.md](82-visary-file-storage-upload.md) — pipeline заливки XLSX и `typedimportwbs`.
- [91-finmodel-chapter1-schedule.md](91-finmodel-chapter1-schedule.md) — создание `CostItem` (ГФ).

До v1.0 импорт «Финмодели» в Apply-фазе:
1. PATCH-ил параметры объекта (FK + indicators + Address).
2. **Засчитывал** budget rows в `applied`, но в Visary **не лил** — пользователь должен
   был нажать **«Загрузить бюджет в Visary»** в разделе «Сформированные файлы»
   (`kind="budget-upload"`).
3. Сразу запускал `ApplyChapter1ScheduleAsync` — попытка создать `CostItem` на WBS-узлах.

**Проблема**: WBS-узлы для ГФ (`1.1.`, `1.6.`, `1.8.`) создаются в Visary именно
импортом бюджета. До нажатия кнопки их в ИСР нет → `ApplyChapter1ScheduleAsync` ловил
для каждой непустой квартальной ячейки сообщение «статья отсутствует в ИСР» (≈70+
записей в журнале на типовом файле). Корректный порядок — `бюджет → дождаться → ГФ`.

**v1.0**: ручную кнопку убрали, в Apply-фазе теперь автоматически:
1. Залить XLSX бюджета в файловое хранилище Visary.
2. Создать `typedimportwbs`.
3. Опросить статус `GET /api/visary/crud/typedimportwbs/{id}` каждые 3 сек (дедлайн 5 мин).
4. По «Закончен успешно» / «Закончен с предупреждениями» → запустить ГФ.
5. По «Закончен с ошибкой» / timeout → ГФ пропустить, выдать file-level error.

---

## 🌐 Endpoint опроса статуса

```http
GET {visary}/api/visary/crud/typedimportwbs/{id}
```

Ответ (важные поля):

```json
{
  "ID": 12345,
  "Status": "Закончен успешно",
  "CountErrors": 0,
  "CountWarnings": 0,
  "StartDate": "2026-05-19T08:30:00Z",
  "FinishDate": "2026-05-19T08:30:12Z"
}
```

Точный набор значений `Status` в контракте Visary не зафиксирован — наблюдали
«В работе», «Закончен успешно», «Закончен с предупреждениями», «Закончен с ошибкой».
Классифицируем case-insensitive по корням слов (`"успеш"`, `"предупреж"`, `"ошибк"`,
`"fail"`, `"error"`, `"complet"`, `"warning"` для англоязычных локалей).

---

## 🏗️ Архитектура

### Поток Apply финмодели

```
ImportsController.Apply(sessionId)
  └─ ImportPipeline.ApplyAsync
        └─ FinModelImportMapper.ApplyAsync
              ├─ ApplyParametersAsync(siteId, paramRows)             ─── params
              │
              ├─ if (budgetRows.Count > 0):                           ─── budget (v1.0 — авто!)
              │     UploadBudgetToVisaryAsync(sessionId, ...)
              │        └─ scope:
              │              BudgetVisaryUploader.UploadAndWaitAsync(sessionId)
              │                 ├─ UploadAsync     → typedimportwbs id
              │                 └─ poll loop: GetTypedImportWbsByIdAsync(id) каждые 3 сек,
              │                                дедлайн 5 мин
              │                 ⇒ Success | Failed (FinalStatus) | TimedOut
              │     → budgetUploadOk: bool?
              │
              └─ if (scheduleArticleRows.Count > 0 && quartersRow):    ─── ГФ
                    if (budgetUploadOk == false) → ГФ пропустить, error
                    else                        → ApplyChapter1ScheduleAsync(siteId, …)
```

### Ключевые места кода

| Компонент | Файл | Что |
|---|---|---|
| Polling-обёртка | [BudgetVisaryUploader.cs](../KiloImportService.Api/Budget/BudgetVisaryUploader.cs) | `UploadAndWaitAsync(sessionId, pollInterval=3s, maxWait=5min)`; классификаторы `IsSuccessStatus` / `IsFailureStatus` |
| Endpoint опроса | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | `GetTypedImportWbsByIdAsync(id)` (через общий `GetCrudByIdAsync<T>`) |
| DTO статуса | [VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs) | `TypedImportWbsRaw`: `Status`, `CountErrors`, `CountWarnings`, `StartDate`, `FinishDate` |
| Auto-вызов из Apply | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `UploadBudgetToVisaryAsync(sessionId, …)`; интеграция в `ApplyAsync` между budget и schedule |
| Captive-dependency обход | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `IServiceScopeFactory _scopeFactory` — мапер Singleton, uploader Scoped → мини-scope per Apply |
| Снят endpoint | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) | Удалён `POST /api/imports/{id}/budget-upload` |
| Снят пункт в `generatedFiles` | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) — `BuildGeneratedFilesAsync` | Убран `kind="budget-upload"`; остался только `kind="budget-xlsx"` (back-up) |
| UI — упрощён | [SessionGeneratedFiles.tsx](../KiloImportService.Web/src/components/ImportSession/SessionGeneratedFiles.tsx) | Снят `handleAction`/`buildSuccessMessage`/`successMessage`/«Загрузить» — только «Скачать» |
| API-тип | [api.ts](../KiloImportService.Web/src/types/api.ts) | `actionUrl?: string \| null` помечен как зарезервированный (back-compat) |

---

## ✅ Правильная реализация

### Polling без бесконечного цикла

```csharp
var deadline = DateTimeOffset.UtcNow + maxWait; // 5 мин по умолчанию
while (true)
{
    ct.ThrowIfCancellationRequested();
    try { snapshot = await _crud.GetTypedImportWbsByIdAsync(id, ct); }
    catch (Exception ex) { _log.LogWarning(ex, "..."); /* не фатально, ретраим */ }

    // snapshot.Status — JsonElement? (v1.2). Извлечение текста + классификация — внутри.
    if (IsSuccessStatus(snapshot?.Status)) return new(..., Success: true, ...);
    if (IsFailureStatus(snapshot?.Status)) return new(..., Success: false, ...);
    if (DateTimeOffset.UtcNow >= deadline)  return new(..., TimedOut: true, ...);

    await Task.Delay(pollInterval, ct);
}
```

- Сетевая ошибка опроса **не считается фатальной** — даём следующей итерации шанс.
  Поломанные сети «успокаиваются», а Visary тем временем доделает импорт.
- Дедлайн — на стенных часах, а не «N итераций» — устойчив к замиранию сети
  (после восстановления успеваем кончить, если есть запас).

### Классификаторы статусов — по корням слов (v1.2: поверх `JsonElement?`)

```csharp
// DTO: TypedImportWbsRaw.Status — JsonElement? (не string!), потому что Visary шлёт
// разные ValueKind: String, Number, Object{Title}. См. doc 56.
internal static string? ExtractStatusText(JsonElement? element) => element?.ValueKind switch
{
    JsonValueKind.String => element.Value.GetString()?.Trim(),
    JsonValueKind.Number => element.Value.TryGetInt64(out var n) ? n.ToString() : element.Value.GetRawText(),
    JsonValueKind.Object => TryExtractObjectStatusTitle(element.Value), // ищет {Title}/{Name}/{Caption}
    _ => null,
};

internal static bool IsSuccessStatus(JsonElement? status) {
    var s = ExtractStatusText(status);
    return !string.IsNullOrWhiteSpace(s)
        && (ContainsCi(s, "успеш") || ContainsCi(s, "предупреж")
         || ContainsCi(s, "complet") || ContainsCi(s, "warning"));
}

internal static bool IsFailureStatus(JsonElement? status) {
    var s = ExtractStatusText(status);
    return !string.IsNullOrWhiteSpace(s)
        && (ContainsCi(s, "ошибк") || ContainsCi(s, "fail") || ContainsCi(s, "error"))
        && !IsSuccessStatus(status);
}
```

Visary может слегка править текст («Закончен успешно» → «Завершён успешно») и форму
поля (string ↔ object); корни слов остаются. Меняется текст — правим **здесь**, без
изменения DTO. Меняется ValueKind — DTO `JsonElement?` это переживает.

### Captive-dependency: Singleton-мапер + Scoped-uploader

`BudgetVisaryUploader` зависит от `ImportServiceDbContext` → Scoped. `FinModelImportMapper`
зарегистрирован Singleton (общий регистр стратегий, см. `Program.cs`). Прямая инъекция
uploader-а сломала бы lifetime. Решение — `IServiceScopeFactory`:

```csharp
private async Task<bool> UploadBudgetToVisaryAsync(Guid sessionId, …)
{
    using var scope = _scopeFactory.CreateScope();
    var uploader = scope.ServiceProvider.GetRequiredService<BudgetVisaryUploader>();
    var result = await uploader.UploadAndWaitAsync(sessionId, ct: ct);
    …
}
```

---

## ❌ Типичные ошибки

### 1. Запускать ГФ независимо от статуса бюджета

```csharp
// НЕПРАВИЛЬНО — будут per-cell ошибки «статья отсутствует в ИСР» на каждый квартал,
// потому что WBS-узлов 1.1/1.6/1.8 ещё нет в ИСР (импорт бюджета ещё не закончился /
// упал). Журнал заваливается шумом, юзер не понимает, что чинить.
if (scheduleArticleRows.Count > 0)
    await ApplyChapter1ScheduleAsync(...);
```

```csharp
// ПРАВИЛЬНО — гейтируем ГФ по результату бюджет-upload-а.
if (budgetUploadOk == false) { errors.Add(/* "ГФ пропущен — почините бюджет" */); }
else                          await ApplyChapter1ScheduleAsync(...);
```

### 2. Считать только `"Закончен успешно"` как успех

`"Закончен с предупреждениями"` — по решению заказчика тоже разрешает ГФ. WBS-узлы при
этом созданы, предупреждения (например, «строка пропущена» по одной из подстатей) не
блокируют создание `CostItem` для остальных. Если запрещать — пользователю придётся
руками править XLSX каждый раз, когда Visary жалуется на пустую ячейку.

### 3. Поллинг с фиксированным числом итераций

```csharp
// НЕПРАВИЛЬНО — на «дёрганой» сети 1–2 пропущенных опроса съедают весь бюджет итераций,
// хотя стенных часов ещё много.
for (int i = 0; i < 100; i++) { ... await Task.Delay(3000); }
```

Дедлайн в `DateTimeOffset` (`UtcNow >= deadline`) — корректное решение.

### 4. Инжектить Scoped в Singleton

```csharp
// НЕПРАВИЛЬНО — captive dependency: BudgetVisaryUploader зависит от DbContext (Scoped),
// FinModelImportMapper — Singleton. Будет одна и та же запись DbContext'а на всё
// приложение → утечки трекинга, гонки.
public FinModelImportMapper(..., BudgetVisaryUploader uploader) { ... }
```

```csharp
// ПРАВИЛЬНО — IServiceScopeFactory создаёт мини-scope на каждый Apply.
public FinModelImportMapper(..., IServiceScopeFactory scopeFactory) { ... }
using var scope = _scopeFactory.CreateScope();
var uploader = scope.ServiceProvider.GetRequiredService<BudgetVisaryUploader>();
```

### 5. Падать на сетевой ошибке опроса

```csharp
// НЕПРАВИЛЬНО — один 502 от Visary убивает весь Apply, хотя сам бюджет уже почти готов.
try { snapshot = await _crud.GetTypedImportWbsByIdAsync(id, ct); }
catch { throw; }
```

```csharp
// ПРАВИЛЬНО — лог-предупреждение и идём к следующей итерации. Дедлайн отработает
// сам, если сеть не вернётся.
try { snapshot = await _crud.GetTypedImportWbsByIdAsync(id, ct); }
catch (Exception ex) { _log.LogWarning(ex, "polling failed — retry in {Interval}", interval); }
```

---

## 📍 Применение в проекте

| Шаг | Файл / endpoint |
|---|---|
| User: «Apply» сессии финмодели | [SessionDetailsPage](../KiloImportService.Web/src/pages/SessionDetailsPage.tsx) → `POST /api/imports/{id}/apply` |
| Оркестратор | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) → `ApplyAsync` |
| Заливка XLSX + polling | [BudgetVisaryUploader.cs](../KiloImportService.Api/Budget/BudgetVisaryUploader.cs) → `UploadAndWaitAsync` |
| Опрос статуса | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) → `GetTypedImportWbsByIdAsync` |
| Создание ГФ | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) → `ApplyChapter1ScheduleAsync` (см. [doc 91](91-finmodel-chapter1-schedule.md)) |

---

## 🎯 Чек-лист

- [ ] При обновлении Visary — пересмотреть тексты `Status` (если поменяются «Закончен …» на что-то другое, обновить корни в `IsSuccessStatus`/`IsFailureStatus` в [BudgetVisaryUploader.cs](../KiloImportService.Api/Budget/BudgetVisaryUploader.cs)). DTO/контракт **трогать не надо** — классификация идёт по строке.
- [ ] Дедлайн 5 мин — sane default для тестового файла (~70 строк бюджета). Если будут массивные бюджеты — параметризовать `maxWait` в `appsettings` (сейчас хардкод в `UploadAndWaitAsync`).
- [ ] Visary импорт упал → одна row-error в файловых ошибках сессии (`budget_upload_failed` для «Закончен с ошибками», `budget_upload_timeout` для дедлайна, `budget_upload_error` для исключения). Текст ошибки содержит: что было сделано до бюджета, причину провала (статус Visary + counts ИЛИ exception message), и фразу про ГФ. Юзер чинит исходный XLSX/настройки проекта и повторно жмёт Apply (`Conflict 409` — только если статус уже `Applied`; иначе из `Validated` повторно идёт весь конвейер). Идемпотентность бюджета — на стороне Visary (`typedimportwbs` создаст новую запись, но WBS-узлы должны быть дозаписаны).
- [ ] При отмене сессии (`CancellationToken`) polling корректно завершается через `ct.ThrowIfCancellationRequested()` в начале каждой итерации и `Task.Delay(ct)` — отдельной cleanup-логики не требуется.
- [ ] Если у сессии нет budget rows (только параметры + ГФ — повторный импорт после ручной правки бюджета в Visary) — upload пропускается, ГФ выполняется как обычно (`budgetUploadOk == null`).
