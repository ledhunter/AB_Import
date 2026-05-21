# 🤝 Финмодель → ensure-сделка в проекте

## 📋 Описание

До любых записей в Объект (FK Site, привязка Организации/ГК, показатели) импорт «Финмодели»
гарантирует, что в выбранном проекте Visary есть **сделка** (`deal`) с заданным
`DocNumber`. С v1.3 значение читается **не** из таблицы Inputs, а из управляющего листа
«Control»: парсер находит на нём строку «Номер КД» (в той же `F=key, G=value`-раскладке,
что и «Выбрать количество этапов») и подставляет значение в `Cells["Номер договора"]`
каждого ParsedRow через новый хинт <see cref="ControlValueRef"/>.

LmID **не используется** во flow (по запросу заказчика 2026-05-21): фильтр Visary
listview/deal и payload `CreateDealAsync` идут только по `DocNumber`.

Поведение **ensure-семантика** (с v1.3):

1. Если сделка найдена в этом проекте по `DocNumber` → продолжаем, в журнал
   «Сделка найдена в проекте: ID=…».
2. Если в проекте нет — пробуем найти её в **общем** `listview/deal` с тем же
   `[["DocNumber","=",Y]]`-фильтром:
   - Найдена глобально → row-error **`deal_in_other_project`** на каждой param-строке
     с указанием чужого проекта (`Title`+ID) + skip `ApplyParametersAsync`.
     Дубликат не создаём.
   - Не найдена нигде → **создаём** её через `POST /api/visary/crud/deal`, в журнал
     «Сделка создана в проекте: ID=…».
3. Если listview-вызов (в проекте или глобальный) или create-вызов упали (5xx, network) →
   row-error (`deal_check_error` / `deal_create_error`) на каждой param-строке + skip
   `ApplyParametersAsync`.

Бюджет и ГФ Главы 1 идут отдельным flow (через `ConstructionProject`/`WBS`,
не `Site`) и не зависят от ensure-сделки.

Поле **опционально**: шаблоны без строки «Номер КД» на листе Control продолжают
работать без чека (`ControlValueRef`-подстановка молча skip-ается, в Inputs колонки
«Номер договора» нет → `fileDocNumberCol == null` → `EnsureDealExistsInProjectAsync`
возвращает `true` без сетевых вызовов).

---

## 🎯 Зачем нужно

Без ensure-шага импорт мог записать ИНН/Группу/Показатели на Site, к которому в Visary
вообще не существует сделки — нарушение бизнес-инварианта «параметры заёмщика
прикладываются к Объекту только если есть согласованная сделка в проекте».
Ensure-flow гарантирует наличие сделки **до** PATCH'ей: находит существующую или
создаёт новую с минимальными данными из файла.

---

## ✅ Правильная реализация

### 0. Парсер — `ControlValueRef` (v1.3)

«Номер КД» подставляется в каждый `ParsedRow` через новый хинт парсера. В
`KeyValueVertical` объявлено:

```csharp
ControlValues: new[]
{
    new ControlValueRef(
        SheetName:     "Control",
        KeyColumn:     "F",
        ValueColumn:   "G",
        ParameterName: "Номер КД",
        OutputKey:     "Номер договора"),
}
```

Парсер находит на листе `Control` строку, где в колонке `F` (Trim, case-insensitive)
лежит текст «Номер КД», берёт значение из `G` той же строки и записывает его в
`Cells["Номер договора"]` всех ParsedRow листа `Inputs` — независимо от этапа,
значение одинаковое.

Если лист скрыт/отсутствует или строки «Номер КД» нет — подстановка **молча
пропускается** (как у `SingleValueOverride`), `ApplyAsync` определит отсутствие
колонки через `FindColumn` и skip-нёт pre-check.

### 1. Визари-клиент — фильтр

```csharp
// Visary.Api.Client/ListView/ListViewClient.cs:469-498
public Task<ListViewResponse<DealRaw>> GetDealsByProjectAsync(
    int projectId, string? lmIdFilter, string? docNumberFilter, CancellationToken ct)
{
    var parts = new List<string>(2);
    if (!string.IsNullOrWhiteSpace(lmIdFilter))
        parts.Add(FilterByString("LmID", lmIdFilter));         // 👈 ["LmID","=",X]
    if (!string.IsNullOrWhiteSpace(docNumberFilter))
        parts.Add(FilterByString("DocNumber", docNumberFilter)); // 👈 ["DocNumber","=",Y]
    string? filter = parts.Count == 0 ? null
        : parts.Aggregate((a, b) => FilterAnd(a, b));           // 👈 [...,"and",...]
    // body → POST listview/deal/onetomany/ConstructionProject?associationId={projectId}
}
```

Сериализуется в Visary как (v1.3, без LmID):

```json
{"Filter":"[\"DocNumber\",\"=\",\"DN-7\"]"}
```

### 2. Маппер — алиасы колонки

Алиасы лежат в `FinModelImportMapper`. С v1.3 нужен только DocNumber:

```csharp
private static readonly string[] DocNumberAliases =
    ["Номер договора", "№ договора", "Номер Договора", "DocNumber", "Doc Number"];
// LmIdAliases удалён в v1.3.
```

В `ValidateParametersAsync` ключ «Номер договора» находится в `Cells` каждой строки
благодаря `ControlValueRef`-подстановке:

```csharp
var fileDocNumberCol = FindColumn(allColumns, DocNumberAliases);
// ...
if (fileDocNumberCol is not null)
{
    docNumberValue = ReadCellTrimmed(row, fileDocNumberCol,
                                     DocNumberAliases, "Номер договора", rowErrors);
}
```

Значение уезжает в `MappedValues.DocNumber`. `MappedValues.LmId` больше не пишется.

### 3. ApplyAsync — pre-check ПЕРЕД ApplyParametersAsync

```csharp
// FinModelImportMapper.cs:484-501
bool paramsApplied = false;
if (paramRows.Count > 0)
{
    var dealOk = await EnsureDealExistsInProjectAsync(
        siteId, paramRows, visaryDb, errors, rowActions, ct);
    if (dealOk)
    {
        var paramApply = await ApplyParametersAsync(siteId, paramRows, errors, ct);
        applied += paramApply;
        paramsApplied = paramApply > 0;
    }
}
```

### 4. EnsureDealExistsInProjectAsync — алгоритм (v1.3)

```csharp
// FinModelImportMapper.cs (см. секцию EnsureDealExistsInProjectAsync)
// 1. Берём первую param-строку (все этапы — один Site).
// 2. Если DocNumber пуст → return true (skip).
// 3. projectId = visaryDb.ConstructionSites.Where(s => s.Id == siteId)
//                   .Select(s => s.ConstructionProjectId).FirstOrDefaultAsync(ct);
// 4. POST listview/deal/onetomany/ConstructionProject?associationId={projectId}
//    Filter: ["DocNumber","=",Y]    ← lmIdFilter передаём null
//    Локальный точный match Trim+OrdinalIgnoreCase.
// 5. Найден в проекте → RowActionLog "Сделка найдена в проекте: ID=…", return true.
// 6. Нет в проекте → fallback на ГЛОБАЛЬНЫЙ POST listview/deal
//    с тем же фильтром ["DocNumber","=",Y] (без onetomany).
// 7. Найден глобально (= сделка живёт в другом проекте) →
//        row-error "deal_in_other_project" на каждой param-строке
//        с указанием other ConstructionProject (Title+ID), return false.
// 8. Нигде не найден → CreateDealAsync(new DealCreateRequest {
//        ConstructionProjectID = projectId,
//        ConstructionProject   = new VisaryRef { ID = projectId },
//        DocNumber,
//        Title = "-",   // ⚠️ временный костыль, см. блок «Title hack» ниже
//        // LmID не присваиваем (v1.3)
//    });
//    → RowActionLog "Сделка создана…", return true.
// 9. ListView (in-project или global) либо Create бросают → row-error
//    на каждой param-строке, return false.
```

### 4а. fallback-listview/deal — что приходит от Visary

Запрос:

```json
POST /api/visary/listview/deal
{
  "Mnemonic":"deal","PageSkip":0,"PageSize":50,
  "Columns":["ID","Title","LmID","DocNumber","ConstructionProject",
             "Organization","GroupName","CreditSum",
             "DealStartDate","DealEndDate"],
  "Filter":"[\"DocNumber\",\"=\",\"номер\"]"
}
```

Visary возвращает `Data[i].ConstructionProject = { ID, Title }` — этого хватает,
чтобы в row-error написать понятный пользователю текст:

```
Сделка (№ «номер») связана с проектом «Жилой комплекс ABC» (ID=7001).
Импорт параметров пропущен.
```

Если `ConstructionProject` пуст/`null` (теоретически возможный edge-case Visary) —
fallback-формулировка «связана с другим проектом» без идентификаторов.

### 5. POST /api/visary/crud/deal — payload (v1.3 без LmID)

```json
{
  "ConstructionProjectID": 4584,
  "ConstructionProject": { "ID": 4584 },
  "DocNumber": "номер договора",
  "Title": "-"
}
```

Visary требует указания проекта **в двух местах**: scalar `ConstructionProjectID`
и nested ref `ConstructionProject:{ID}`. Это особенность серверного API — нельзя
выбрать одно из двух.

### ⚠️ Title hack — временный

Visary сейчас возвращает 400, если в payload `Title` `null`/отсутствует.
Заказчик подтвердил, что в будущем требование снимется. До тех пор отправляем
`Title: "-"` (символ-заглушка) и помечаем как костыль в трёх местах:

- `DealCreateRequest.Title` — XML-doc предупреждает о временности
- `FinModelImportMapper.EnsureDealExistsInProjectAsync` — `TODO`-комментарий рядом с присваиванием
- Memory entry `project_finmodel_deal_create_title_hack` (контекст для следующих сессий)

**Что делать, когда Visary снимет требование:**
1. Удалить присваивание `Title = "-"` в `EnsureDealExistsInProjectAsync`.
2. Удалить поле `Title` из `DealCreateRequest` (если оно нигде больше не используется).
3. Снять memory entry `project_finmodel_deal_create_title_hack`.

### ⚠️ Важно

- **Перебор серверного «=» дополняется локальным точным сравнением Trim+Ordinal.**
  Visary `=` теоретически точный, но клиент `RoomsFormImportMapper`/`Indicators` уже
  ловили хвостовые пробелы — перестраховка.
- **Row-error прицепляется к ВСЕМ param-строкам**, не только к firstRow. Это сделано
  чтобы пользователь увидел проблему ровно на тех ячейках, куда смотрит, независимо
  от номера активного этапа.
- **Бюджет/ГФ продолжаются** — pre-check блокирует только `ApplyParametersAsync`.
- **`ConstructionProjectId` берётся из локального зеркала** (`visaryDb.ConstructionSites`),
  чтобы не делать лишний CRUD-вызов; зеркало синхронизируется `SitesSyncService`.

---

## ❌ Типичные ошибки

```csharp
// ❌ НЕПРАВИЛЬНО (v1.3): читать «Номер договора» из колонки на Inputs
var fileDocNumberCol = FindColumn(allColumns, DocNumberAliases);
// в Inputs его НЕТ — он лежит на управляющем листе Control в строке «Номер КД».
// Без ControlValueRef-подстановки FindColumn вернёт null, pre-check молча skip-нётся,
// и сделка не создастся при импорте новых шаблонов.

// ❌ НЕПРАВИЛЬНО (v1.3): передавать LmID в фильтр/payload
await GetDealsByProjectAsync(projectId, lmIdFilter: lmId, docNumberFilter: docNumber, ct);
await CreateDealAsync(new DealCreateRequest { ..., LmID = lmId });
// Заказчик 2026-05-21 попросил не передавать LmID. Если/когда понадобится вернуть
// LmID — добавить алиасы и колонку обратно, методы GetDeals*/DealCreateRequest
// уже принимают опциональный LmID-параметр.

// ❌ НЕПРАВИЛЬНО: row-error только на firstRow
errors.Add(new RowError(..., paramRows[0].SourceRowNumber, ...));
// При множестве этапов пользователь увидит ошибку только на 1-м, остальные смотрят
// «впустую». UI группирует ошибки по (Sheet, SourceRowNumber).

// ❌ НЕПРАВИЛЬНО: чек блокирует БЮДЖЕТ и ГФ
if (!dealOk) return new ApplyResult(0, errors); // ← всё прекращаем
// Бюджет и ГФ работают через ConstructionProject/WBS, они независимы от Site-параметров.
// Блокируем только ApplyParametersAsync — пользователь хочет видеть ошибки на ВСЕХ
// этапах конвейера сразу.

// ❌ НЕПРАВИЛЬНО: чек на каждой param-строке
foreach (var pr in paramRows) await GetDealsByProjectAsync(...);
// N сетевых вызовов на одну сделку. Один запрос на firstRow + локальный матч хватит.

// ❌ НЕПРАВИЛЬНО: дефолт Title="-" в самом DTO
public sealed class DealCreateRequest { public string Title { get; set; } = "-"; }
// Костыль должен «торчать наружу» — в маппере, не в DTO. Иначе после снятия требования
// со стороны Visary не очевидно, где именно его удалять; и любой другой вызывающий
// этот DTO будет молча тащить заглушку дальше.

// ❌ НЕПРАВИЛЬНО: при не-найденной сделке только row-error без создания
if (match is null) { errors.Add(...); return false; }
// Так было в v1.0; v1.1 (по уточнению заказчика) — сделку нужно СОЗДАВАТЬ, а
// row-error добавлять только если create-вызов сам упал (5xx, network).

// ❌ НЕПРАВИЛЬНО (v1.2): создавать сделку, не проверив глобальный listview
if (matchInProject is null)
{
    await CreateDealAsync(...);   // ← Visary вернёт 5xx или дубль-сделку,
                                  // если (LmID, DocNumber) уже занята другим проектом
    return true;
}
// Уникальность пары (LmID, DocNumber) хранится глобально в Visary; в чужом проекте
// сделку можно только увидеть, а перепривязать через импорт нельзя. Поэтому fallback
// listview/deal — обязательный шаг ПЕРЕД CreateDealAsync.

// ❌ НЕПРАВИЛЬНО: ловить fallback-not-found как ту же "deal_check_error"
// errors.Add(new RowError(..., "deal_check_error", ...)); // ← путает UI и аналитику
// «Сделка живёт в чужом проекте» — это бизнес-исход, а не сетевой сбой. Отдельный
// ErrorCode `deal_in_other_project` нужен, чтобы:
//   1) фильтр в отчёте отличал «сделка не там» от «Visary 503»;
//   2) в журнале строка не считалась как Applied (см. doc 98).
```

---

## 📍 Применение в проекте

| Компонент                        | Файл                                                    | Метод/строки                       |
|----------------------------------|---------------------------------------------------------|------------------------------------|
| Расширенный listview-фильтр (in-project) | `Visary.Api.Client/ListView/ListViewClient.cs`  | `GetDealsByProjectAsync`           |
| Глобальный listview-фильтр (v1.2 fallback) | `Visary.Api.Client/ListView/ListViewClient.cs` | `GetDealsAsync(lmIdFilter, docNumberFilter, ct)` |
| DTO для создания сделки          | `Visary.Api.Client/Dto/VisaryCrudRequests.cs`           | `DealCreateRequest`                |
| CreateDealAsync                  | `Visary.Api.Client/CRUD/CrudClient.cs`                  | интерфейс + impl                   |
| Алиасы колонок                   | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs` | `DocNumberAliases` (LmIdAliases удалён в v1.3) |
| `ControlValueRef` («Номер КД» с Control, v1.3) | `KiloImportService.Api/Domain/Importing/FileLayoutHint.cs` + `XlsxParser.cs` | `KeyValueVertical.ControlValues` |
| `ControlValueRef` в LayoutHint Финмодели | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs` | LayoutHint init (см. «Номер КД» → «Номер договора») |
| Парсер-тесты ControlValueRef     | `KiloImportService.Api.Tests/Importing/XlsxParserTests.cs` | `KeyValueVertical_ControlValueRef_*` (2 теста) |
| Чтение в MappedValues            | то же                                                   | `ValidateParametersAsync`          |
| Ensure-flow + ветвление Apply    | то же                                                   | `ApplyAsync` (paramRows-блок)      |
| Реализация ensure-flow           | то же                                                   | `EnsureDealExistsInProjectAsync`   |
| Прокси-контроллер                | `KiloImportService.Api/Controllers/VisaryEntitiesController.cs` | `ListDeals`                |
| Контракт-тесты `and`-фильтра     | `KiloImportService.Api.Tests/VisaryClients/ListViewClientContractTests.cs` | `GetDealsByProjectAsync_with_both_filters_*` + `GetDealsAsync_with_both_filters_hits_global_url_with_and_filter` |
| Тесты ensure-flow                | `KiloImportService.Api.Tests/Mapping/FinModelImportMapperTests.cs` | `ApplyAsync_Deal*` (включая `DealLinkedToOtherProject` + `DealGlobalListViewThrows`) |

---

## 🎯 Чек-лист добавления похожего ensure-flow к другим импортам

- [ ] Алиасы новых колонок добавлены в массив рядом с существующими (паттерн `[A, B, C]`)
- [ ] Колонки попали в `anyFound` + `allAliases` (иначе пустой шаблон в `column_not_found`)
- [ ] Пустые значения дают `value_empty` через `ReadCellTrimmed(..., rowErrors)`
- [ ] Значения сохранены в `MappedValues` (для чтения в Apply)
- [ ] Ensure читает значения из `firstRow.MappedValues.RootElement`
- [ ] При отсутствии хотя бы одного из значений ensure **skip-ается** (return true)
- [ ] `projectId` резолвится из `VisaryDbContext.ConstructionSites` (local mirror)
- [ ] Listview-фильтр собирается через `FilterAnd` для составного `and`
- [ ] Локальный пост-фильтр Trim+OrdinalIgnoreCase (Visary `=` может содержать ws)
- [ ] Не нашли → CRUD-create с минимальным payload (а не row-error)
- [ ] Костыли-заглушки (типа `Title="-"`) ставятся в маппере, а не в DTO; помечаются `TODO` + memory entry
- [ ] На сетевом сбое (listview/create) row-error на **каждой** param-строке (фронт группирует)
- [ ] Журнал `RowActionLog` фиксирует «найдено»/«создано» с ID
- [ ] Тесты покрывают: найдено / создано / create-исключение / listview-исключение / колонок нет / одно значение пусто

---

## 📝 История версий

- **v1.0** (2026-05-21): pre-check без create — отсутствие сделки давало row-error
  `deal_not_found` и блокировало `ApplyParametersAsync`.
- **v1.1** (2026-05-21): по уточнению заказчика — ensure-семантика: при отсутствии
  сделки она **создаётся** через `POST /api/visary/crud/deal` с минимальным payload
  `{ConstructionProjectID, ConstructionProject:{ID}, DocNumber, LmID, Title:"-"}`.
  Введён `DealCreateRequest` DTO + `ICrudClient.CreateDealAsync`. Title — временный
  костыль (см. одноимённый блок выше), будет удалён, когда Visary перестанет
  требовать непустое значение. Row-error остаётся только на сетевых сбоях ListView
  или Create (`deal_check_error` / `deal_create_error`).
- **v1.2** (2026-05-21): между «не найдена в проекте» и `CreateDealAsync` добавлен
  fallback на глобальный `POST listview/deal` с тем же `(LmID, DocNumber)`-фильтром.
  Если совпадение есть глобально — сделка существует в другом проекте Visary
  (пара `(LmID, DocNumber)` уникальна), и импорт **не создаёт** дубликат: пишем
  row-error `deal_in_other_project` на каждой param-строке с указанием чужого
  проекта (`ConstructionProject.Title` + `ID`) и пропускаем `ApplyParametersAsync`.
  Бюджет и ГФ Главы 1 по-прежнему идут (они зависят от `ConstructionProject`, не
  от сделки). `IListViewClient.GetDealsAsync` расширен `docNumberFilter` (тот же
  `FilterAnd`-приём, что и у `GetDealsByProjectAsync`); прокси-контроллер
  `ListDeals` пробрасывает оба фильтра и в без-projectId-ветке.
- **v1.3** (2026-05-21): по запросу заказчика — «Номер договора» теперь читается с
  управляющего листа **Control** (поле «Номер КД»), а не из таблицы Inputs.
  Введён общий хинт парсера `ControlValueRef(SheetName, KeyColumn, ValueColumn,
  ParameterName, OutputKey)`: парсер находит строку по тексту-ключу в
  `(SheetName, KeyColumn)` и подставляет значение из `ValueColumn` в `Cells[OutputKey]`
  каждого ParsedRow. FinModel объявляет
  `ControlValueRef("Control", "F", "G", "Номер КД", "Номер договора")`, так что
  существующий `FindColumn(DocNumberAliases)` находит ключ без правок в Validate-коде.
  **LmID полностью убран из flow**: маппер больше не читает алиасы «ID в LM/KK»,
  не сериализует `LmId` в `MappedValues`, фильтр Visary listview/deal и payload
  `CreateDealAsync` идут только по `DocNumber` (lmIdFilter передаётся `null`).
  Сравнения в текстах ошибок/журнале ужаты до `№ «{DocNumber}»`. Сигнатуры
  `IListViewClient.GetDeals*` и `DealCreateRequest.LmID` оставлены без изменений
  (опциональные) — на случай возврата LmID в будущем.
