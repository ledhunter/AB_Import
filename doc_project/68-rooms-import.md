# 🏠 Импорт «Помещения» (rooms)

## 📋 Описание

**Статус**: ✅ Реализовано (v1)
**Дата**: 2026-05-07
**Маппер**: `RoomsFormImportMapper` (код типа `"rooms"`)

Импорт реестра помещений (квартир, машиномест, кладовых) из Excel-файла
по шаблону **«Пример импорта.xlsx»** / **«Единая форма 3»** в Visary через
HTTP API.

**Контракт UX**:
1. Пользователь выбирает в UI **тип импорта** «Помещения», **проект** и **объект строительства** (ОКС).
2. Загружает файл (`*.xlsx`).
3. Маппер per-row проверяет, что строка принадлежит выбранному ОКСу,
   находит/создаёт корпус, помещение и (опционально) ДДУ.

В отличие от прежней реализации, маппер **не ищет** Site по содержимому
строки — Site уже выбран в UI, а строки только валидируются против него.

---

## 🏗️ Поток обработки

```
ParsedRow[] (по листам) ──► ValidateAsync ──► MappedRow[] ──► ApplyAsync
                              │                                    │
                              ├─ загрузка ОКСа из Visary           ├─ группировка по Sheet
                              ├─ загрузка RoomKind из Visary       ├─ Section.find_or_create
                              ├─ per-row сверка ОКСа                ├─ Room.find_or_create
                              ├─ резолв Kind (column → sheet)       ├─ ShareAgreement.find_or_create
                              └─ MappedRow.IsValid                  └─ apply_failed на строку
```

### Validate phase

1. **Site обязателен** в `ImportContext` (иначе `file_error: site_required`).
2. **Загрузить выбранный ОКС** через `_crud.GetSiteByIdFullAsync(siteId)`.
   Используется `ConstructionSiteFull.StageNumber` (`int?`),
   `ConstructionProjectNumber` / `ConstructionPermissionNumber` (`string?`).
3. **Загрузить справочник RoomKind** из живого Visary через
   `_listView.ListRoomKindsAsync()` (НЕ из локальной visary_db — см. ⚠️ ниже).
4. **Резолвить ожидаемый вид помещения по имени листа** один раз
   (`Квартиры`→`Квартира`, `Машиноместа`→`Машиноместо`).
5. Для каждой строки:
   - Прочитать НПС / Этап / РНС из строки.
   - Сверить с ОКСом: **НПС и Этап** обязаны совпадать, **РНС** опционально
     (только если в файле непустое).
   - При несовпадении → `IsValid=false` с кодом `site_mismatch`,
     сообщение `"для строки файла {N} не подходит выбранный объект"`.
   - Резолвить Kind: приоритет «Тип/Название/Вид» из колонки;
     если пусто → fallback по имени листа.
   - Накопить остальные поля (площадь, стоимость, № ДДУ и т. д.).

### Apply phase

1. Сгруппировать `IsValid` строки по `Sheet` (порядок листов сохраняется).
2. **РНС в Site, если он там пустой** — один раз на сессию, до основного цикла:
   - собрать distinct непустые `PermissionNumber` из валидных строк;
   - перечитать Site через `GetSiteByIdFullAsync` (свежий `RowVersion`);
   - если `site.ConstructionPermissionNumber` пустой — `PatchSiteAsync` с первым кандидатом;
   - при наличии в файле разных РНС — лог-warn и берётся первый;
   - при ошибке PATCH — лог-warn, импорт продолжается (помещения создаются всё равно).
3. Для каждого листа залогировать заголовок:
   ```
   RoomsForm.Apply: ───── Лист 'Квартиры' — N валидных строк ─────
   ```
4. Для каждой строки внутри листа:
   - **Organization-застройщик**: по PIN найти Org, привязать к Site
     (один раз на сессию — кэш `siteOrgLinked`).
   - **Section**: numeric-часть из «№ стр/корп» (`«лит. 1»` → `«1»`);
     `find_or_create` (Section.Type = `МЖД (ID=3)` по умолчанию).
   - **Room**: `find_or_create` в Section по `ExplicationNumber/Number`.
   - **ShareAgreement** (если в строке указан № ДДУ):
     `find_or_create/patch` с `Project`, `RoomKindRef`, `ProjectNumber`, `ConditionalNumber`.

---

## ✅ Правильная реализация (фрагменты)

### Per-row сверка выбранного ОКСа

```csharp
bool projectOk = string.Equals(rowProjectNum, siteProjectNumber, StringComparison.OrdinalIgnoreCase);
bool stageOk   = rowStageNum.HasValue && siteStageNumber.HasValue
              && rowStageNum.Value == siteStageNumber.Value;
// РНС: 3 случая «совпадения» — пусто в файле, равно Site.РНС, либо Site.РНС пуст
// (тогда в Apply один раз PATCH-аем Site через PatchSiteAsync).
bool permissionOk = string.IsNullOrWhiteSpace(rowPermission)
              || string.Equals(rowPermission, sitePermissionNumber, StringComparison.OrdinalIgnoreCase)
              || string.IsNullOrWhiteSpace(sitePermissionNumber);  // 👈 Site пустой → row может его заполнить

if (!projectOk || !stageOk || !permissionOk)
{
    rowErrors.Add(new RowError(null, "site_mismatch",
        $"для строки файла {row.SourceRowNumber} не подходит выбранный объект"));
    mappedRows.Add(new MappedRow(row.SourceRowNumber, IsValid: false, ...));
    continue;
}
```

### Обновление РНС в Site (один раз на сессию)

```csharp
// В начале ApplyAsync, до группировки по листам:
await TryUpdateSitePermissionNumberAsync(siteId, rows, ct);

// Внутри:
var permissionsInFile = rows
    .Where(mr => mr.IsValid)
    .Select(mr => GetStringOrNull(mr.MappedValues.RootElement, "PermissionNumber"))
    .Where(p => !string.IsNullOrWhiteSpace(p))
    .Select(p => p!.Trim())
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();
if (permissionsInFile.Count == 0) return;

// 👇 СВЕЖИЙ RowVersion (не из Validate) — между Validate и Apply Site могли поменять
var current = await _crud.GetSiteByIdFullAsync(siteId, ct);
if (!string.IsNullOrWhiteSpace(current.ConstructionPermissionNumber)) return; // уже не пустой

await _crud.PatchSiteAsync(siteId, new SitePatchRequest
{
    RowVersion                   = current.RowVersion,         // 👈 optimistic lock
    ConstructionPermissionNumber = permissionsInFile[0],
}, ct);
```

### Группировка по листу в Apply

```csharp
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
    foreach (var mr in sheetGroup) { /* ... */ }
}
```

### Формирование `Title` / `UniqueNumber` помещения

Контракт нейминга (применяется как в `CreateRoomAsync`, так и в
`PatchRoomAsync` — чтобы повторный импорт не оставлял старого Title):

```csharp
var uniqueNumber = $"{roomNumber}_{sectionTitle}_{buildingSection}";
//   Пример: «15/16_1.1_1»

var roomTitle = string.IsNullOrWhiteSpace(roomKindTitle)
    ? uniqueNumber
    : $"{roomKindTitle} {uniqueNumber}";
//   Пример: «Машиноместо 15/16_1.1_1»
```

| Источник | Поле в `MappedValues` | Куда подставляется |
|---|---|---|
| Колонка «Номер помещения» | `RoomNumber` | `ExplicationNumber`, первая часть `UniqueNumber` |
| Section.Title (нумерик, напр. «1.1») | `SectionTitleNumeric` → переменная `sectionTitle` в Apply | средняя часть `UniqueNumber` |
| Колонка «Подъезд/Секция» | `BuildingSection` | последняя часть `UniqueNumber`, поле `BuildingSection` |
| Резолв `RoomKind.Title` | `RoomKindTitle` | префикс в `Title` |

⚠️ `RoomPatchRequest` обязан содержать `Title` и `UniqueNumber` — без них
PATCH оставит у Room устаревшее имя из предыдущей реализации (просто номер
помещения), даже когда Kind/Section/BuildingSection обновились.

### `StageNumber` в ShareAgreement

При создании ДДУ передаём номер этапа объекта строительства (из колонки
«Этап» в файле). Поле `ShareAgreementCreateRequest.StageNumber` —
строка, поэтому `int` из MappedValues пере-сериализуется в `string`.

```csharp
var stageNumberForSa = GetStringOrNull(v, "StageNumberRaw")
    ?? GetIntOrNull(v, "StageNumber")?.ToString(CultureInfo.InvariantCulture);

await _crud.CreateShareAgreementAsync(new ShareAgreementCreateRequest
{
    ...
    StageNumber       = stageNumberForSa,  // 👈
    ConditionalNumber = roomNumber,
});
```

⚠️ `StageNumberRaw` — это **строка из файла как есть** («1», «1а»),
`StageNumber` — нормализованный `int?`. Для ДДУ предпочтительнее
сырое значение, чтобы не терять буквенный суффикс этапа.

### Прогресс валидации по листам (без потерь)

Событие `StageProgress` шлётся в трёх случаях:
1. **Первая строка каждого листа** (`sheetProcessed == 1`) — гарантирует
   появление листа в `sheetProgress[]` UI, даже если в листе всего 1-2
   строки и throttle бы их пропустил.
2. **Последняя строка каждого листа** (`sheetProcessed == sheetTotal`) —
   финальное «N из N · 100%» по листу.
3. **Throttle** — каждые `notifyEvery = max(1, total/50)` строк.

Без п.1 при 3+ листах разной длины «средние» листы могли вообще не
получить событие → не появлялись в UI. Симптом: на странице валидации
видны только первый и последний листы.

### Площадь по категории помещения

Справочник Visary `RoomCategory` (подтверждено на стенде через
`GET /api/visary/crud/roomkind/3`):

| Значение | Категория |
|---|---|
| `0` | **Residential** — Квартира, Апартамент (единственная жилая) |
| `1` | NonResidential |
| `2` | ParkingPlace (Машиноместо) |
| `3` | OtherNonResidential (Кладовая, Коммерческое и т. п.) |

Поле, в которое попадает «Площадь» из Excel, зависит от этой категории:

| `RoomCategory` | `ProjectArea` | `TotalArea` |
|---|---|---|
| `0` (Residential) или `null` | площадь из файла | `null` |
| `≠ 0` (любое нежилое) | **`0`** | площадь из файла |

```csharp
private const int ResidentialRoomCategory = 0; // справочник Visary RoomCategory

var isNonResidential = roomCategory.HasValue
                    && roomCategory.Value != ResidentialRoomCategory;
double? projectAreaForCrud = isNonResidential ? 0d : areaFromFile;
double? totalAreaForCrud   = isNonResidential ? areaFromFile : null;
```

⚠️ При `RoomCategory == null` (категория не пришла из справочника) намеренно
ведём себя как для жилого — чтобы случайно не положить площадь в неправильное
поле для незнакомого Kind.

⚠️ **Listview-columns для `RoomKind` обязан включать `RoomCategory`**. Общий
`DictionaryColumns = ["ID", "Title", "Hidden"]` ЭТО ПОЛЕ НЕ ВОЗВРАЩАЕТ — без
правки `RoomCategory` у всех Kind пришёл бы `null` и все помещения считались
бы жилыми (симптом: у машиномест заполнена `ProjectArea`, `TotalArea` пустая).
Поэтому в [ListViewClient.cs](../Visary.Api.Client/ListView/ListViewClient.cs)
сделан отдельный массив `RoomKindColumns = ["ID", "Title", "Hidden", "RoomCategory"]`,
и `ListRoomKindsAsync` идёт inline-запросом, а не через общий
`ListDictionaryAsync`. Для проверки — лог `RoomsForm.Validate: RoomCategory по Kind: ...`
показывает фактические значения.

`RoomCreateRequest` и `RoomPatchRequest` получили поле `TotalArea` —
без него PATCH оставит у Room устаревшее значение.

### Резолв Kind по имени листа (fallback)

«Квартиры» → срезаем `ы` → `Квартир` → не найдено → пробуем `Квартир + а` → **`Квартира`** ✓
«Машиноместа» → срезаем `а` → `Машиномест` → не найдено → пробуем `Машиномест + о` → **`Машиноместо`** ✓

```csharp
if ("аяыиеёАЯЫИЕЁ".Contains(last))
    candidates.Add(name[..^1]);
if (last == 'ы') candidates.Add(name[..^1] + "а");
if (last == 'и') candidates.Add(name[..^1] + "я");
if (last == 'а') candidates.Add(name[..^1] + "о");
```

### Создание Section с обязательным `Type`

```csharp
await _crud.CreateSectionAsync(new SectionCreateRequest
{
    ConstructionSiteID = siteId,
    ConstructionSite   = new VisaryRef { ID = siteId },
    Title              = sectionTitle,                           // «1.1»
    Type               = new VisaryRef { ID = 3, Title = "МЖД" }, // дефолт; парковка позже
}, ct);
```

---

## ⚠️ Важно

### 1. RoomKind берём из ЖИВОГО Visary, а не из локальной visary_db

```csharp
// ✅ ПРАВИЛЬНО: реальные ID Visary (Машиноместо=4, Квартира=…)
var roomKindList = await _listView.ListRoomKindsAsync(ct);
var kindByTitle = roomKindList.Data
    .Where(k => !string.IsNullOrWhiteSpace(k.Title))
    .GroupBy(k => k.Title!.Trim(), StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, g => g.First().ID, StringComparer.OrdinalIgnoreCase);
```

```csharp
// ❌ НЕПРАВИЛЬНО: seed-данные локальной БД могут не совпадать со стендом
var kindByTitle = await visaryDb.RoomKinds.AsNoTracking()...;  // вернёт «Гараж» когда нужно «Квартира»
```

**Симптом ошибки**: импортируется только один лист, а помещения создаются
с типом «Гараж» (или другим случайным из локального seed), потому что
строки с правильным Title из файла не находят свой ID и помечаются `fk_not_found`.

### 2. Substring-fallback для имени листа отключён

`«Машиноместа»`.Contains(`«Машино»`) = true — соблазнительно, но опасно: легко
случайно совпасть с «Машино…» / «Места …». Используем только точное совпадение
+ plural-trim heuristic. Если не подошло — требуем явное «Тип/Название/Вид» в
строке.

### 3. PATCH через `forceUpdate=true` — без `ID`/`RowVersion` в теле

Это не специфично для импорта помещений, но именно здесь оно критично:
`PatchRoomAsync` и `PatchShareAgreementAsync` зануляют эти поля перед сериализацией.
Подробности — в `doc_project/50-visary-api-new-methods.md` (раздел «Логирование запросов в Visary» / «forceUpdate=true|false»).

### 4. Room.find_or_create — уникальность в разрезе `Section × Kind × Number`

В одной секции могут одновременно быть **квартира №3**, **машиноместо №3**
и **кладовая №3** — это три разных помещения. Поэтому матч в `ApplyAsync`
обязан учитывать `Kind`:

```csharp
// ✅ ПРАВИЛЬНО — с учётом Kind
var match = roomsInSection.Data.FirstOrDefault(r =>
    (kindId is null || r.Kind?.ID == kindId.Value)
    && (string.Equals(r.ExplicationNumber, roomNumber, OIC) ||
        string.Equals(r.Number,            roomNumber, OIC)));
```

```csharp
// ❌ НЕПРАВИЛЬНО — без Kind
var match = roomsInSection.Data.FirstOrDefault(r =>
    string.Equals(r.ExplicationNumber, roomNumber, OIC) ||
    string.Equals(r.Number,            roomNumber, OIC));
// → файл с 6 квартирами и 3 машиноместами в одной секции (номера 1..3 пересекаются)
//   создаст 3 машиноместа, PATCH-нет их же как квартиры (меняя Kind), и потеряет 3 строки.
```

**Симптом**: из файла «6 квартир + 3 машиноместа» создаётся 3+3=6 помещений,
а 3 квартиры «исчезают».

### 5. Полиморфные поля DTO `RoomRaw` / `ShareAgreementRaw`

`Active*ShareAgreement`, `*EscrowAccount`, `ValidityStatus` приходят разными
типами — в DTO это `JsonElement?`. См. `doc_project/56-visary-dto-deserialization-pitfalls.md`.

### 6. Парсер обходит ВСЕ листы файла

`XlsxParser.ParseTabular` итерирует по всем worksheets рабочей книги; каждая
`ParsedRow.Sheet` содержит имя своего листа, а маппер группирует строки по
`Sheet` в `ApplyAsync`. Это критично для шаблонов «Пример импорта.xlsx» /
«Единая форма 3», у которых разные виды помещений лежат на разных листах
(«Квартиры», «Машиноместа», «Кладовые»).

```csharp
// ✅ ПРАВИЛЬНО — обходим все листы, пустые молча пропускаем
foreach (var sheet in workbook.Worksheets)
{
    var range = sheet.RangeUsed();
    if (range is null) continue;            // напр. пустой «Справочник»
    // ... читаем заголовки и строки данного листа,
    //     эмитим ParsedRow с sheet.Name
}
```

```csharp
// ❌ НЕПРАВИЛЬНО — раньше парсер читал только первый лист
var sheet = workbook.Worksheets.FirstOrDefault();
// → если первый лист «Квартиры», то «Машиноместа» и «Кладовые» молча терялись;
//   если первый лист пуст или «Справочник» — импорт падал «нет данных».
```

**Союз заголовков**: у листов могут быть разные колонки («Колич. комнат» есть
только в «Квартирах»). В `ParseResult.Headers` собирается **union** — это нужно
UI; в `ParsedRow.Cells` каждой строки только ключи **своего** листа.

**Коллизии `SourceRowNumber`**: строка 5 встречается в каждом листе.
`StagedRow` и `ImportError` хранят `Sheet` отдельным полем; уникальный индекс —
`(ImportSessionId, Sheet, SourceRowNumber)`. Миграция:
`20260512095902_AddSheetToStagedRowAndError`.

```csharp
// ✅ Pipeline пишет Sheet вместе со строкой
_serviceDb.StagedRows.Add(new StagedRow
{
    ImportSessionId = sessionId,
    Sheet           = raw.Sheet ?? string.Empty,  // 👈
    SourceRowNumber = mr.SourceRowNumber,
    ...
});
```

```csharp
// ❌ Без Sheet — 23505 duplicate key violation на втором листе
// "duplicate key value violates unique constraint
//  IX_staged_rows_ImportSessionId_SourceRowNumber"
```

⚠️ `ExcelDataReaderParser` (для `.xls`/`.xlsb`) **пока** читает только первый
лист. Если в файле помещений будет XLS-формат с несколькими листами — нужно
дополнить и его (через `reader.NextResult()`); для актуального шаблона xlsx
этого не требуется.

### 7. PATCH Site через `forceUpdate=false` — с актуальным `RowVersion`

`PatchSiteAsync` использует `forceUpdate=false` (optimistic locking), в отличие
от `PatchRoom/PatchShareAgreement/PatchWbs` с `forceUpdate=true`. Поэтому **обязательно**
читать свежий `RowVersion` через `GetSiteByIdFullAsync` непосредственно перед PATCH —
тот RowVersion, что был получен в Validate, может уже устареть. Сценарий — на стенде
пользователь параллельно редактирует ОКС в UI: Validate прочитал RowVersion=10,
пользователь сохранил изменения → RowVersion=11, Apply послал бы 10 → **409 Conflict**.

```csharp
// ✅ Свежий read прямо в Apply
var current = await _crud.GetSiteByIdFullAsync(siteId, ct);
await _crud.PatchSiteAsync(siteId, new SitePatchRequest
{
    RowVersion = current.RowVersion,           // 👈 актуальный
    ConstructionPermissionNumber = candidate,
}, ct);

// ❌ Кэш из Validate — RowVersion может устареть
await _crud.PatchSiteAsync(siteId, new SitePatchRequest
{
    RowVersion = siteFromValidate.RowVersion,  // ⚠️ риск 409 Conflict
    ...
}, ct);
```

---

## ❌ Типичные ошибки

### Ошибка 1: 422 при создании Section

```text
[INF] Visary → POST .../api/visary/crud/constructionsection
       body={"ConstructionSiteID":7850,"ConstructionSite":{"ID":7850},"Title":"1"}
[ERR] Visary error 422: <тело>
```

**Причина**: отсутствует `Type`. Добавить `Type = new VisaryRef { ID = 3, Title = "МЖД" }`.

### Ошибка 2: 500 «Can not add property RowVersion to JObject»

```text
[INF] Visary → PATCH .../api/visary/crud/room/20586?forceUpdate=true
       body={"ID":20586,"RowVersion":0,"Kind":...}
[ERR] Visary error 500: "Can not add property RowVersion to Newtonsoft.Json.Linq.JObject..."
```

**Причина**: `forceUpdate=true` + ID/RowVersion в теле. Сделать поля `int?`/`long?`,
до сериализации присвоить `null`.

### Ошибка 3: «не подходит выбранный объект» для всех строк

**Причина**: пользователь выбрал неподходящий ОКС (НПС/Этап в файле и в ОКСе расходятся).
Лог в Validate показывает: `файл(НПС='нпс', Этап='1', РНС='рнс') vs site(НПС='', Этап=, РНС='')` —
видно, какие значения в выбранном ОКСе пустые/не совпадают.

### Ошибка 4: импортирован только один лист с типом «Гараж»

**Причина**: справочник RoomKind тянулся из локальной visary_db с устаревшим seed.
Решение — использовать `_listView.ListRoomKindsAsync()` (см. ⚠️ #1).

---

## 📍 Применение в проекте

| Компонент | Файл | Что делает |
|-----------|------|-----------|
| Маппер | `KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs` | Validate + Apply, sheet-группировка, Kind-резолв, площадь по `RoomCategory`, формат `UniqueNumber/Title`, обновление РНС в Site |
| Регистрация типа в UI | `KiloImportService.Api/Controllers/ImportTypesController.cs` | `["rooms"] = ("Помещения", ...)` |
| DI | `KiloImportService.Api/Program.cs` | `AddSingleton<IImportMapper, RoomsFormImportMapper>()` |
| Visary CRUD | `Visary.Api.Client/CRUD/CrudClient.cs` | `CreateSection/Room/ShareAgreement`, `PatchRoom/ShareAgreement`, `PatchSiteAsync` |
| Visary listview | `Visary.Api.Client/ListView/ListViewClient.cs` | `GetSectionsBySite`, `GetRoomsBySection`, `ListRoomKinds` (с `RoomKindColumns` под `RoomCategory`), `GetShareAgreementsByRoom`, `GetOrganizationsByClientId` |
| Request DTO | `Visary.Api.Client/Dto/VisaryCrudRequests.cs` | `SectionCreateRequest`, `RoomCreateRequest/PatchRequest` (с `Title`/`UniqueNumber`/`TotalArea`), `ShareAgreementCreateRequest` (с `StageNumber`), `SitePatchRequest` |
| Response DTO | `Visary.Api.Client/Dto/VisaryEntities.cs` | `RoomRaw` (с `JsonElement?` полями, `Kind: VisaryRef?`, `TotalArea`), `ShareAgreementRaw.ValidityStatus`, `RoomKindRaw.RoomCategory: int?` |
| Хранение | `StagedRow.Sheet` + `ImportError.Sheet` + миграция `20260512095902_AddSheetToStagedRowAndError` | уникальный индекс `(SessionId, Sheet, SourceRowNumber)` |
| Пример файла | `RoomImport/Пример импорта.xlsx` | до 4 листов: «Квартиры», «Машиноместа», «Коммерческое помещение», «Кладовая» |
| Описание методов | `RoomImport/описание методов.txt` | Валидные тела JSON для Section/Room/SA |
| Сценарий | `RoomImport/room_sa_create.puml` | PlantUML-диаграмма пути исходного «roomsForm» (наследник) |

### Удалено в v1 (по сравнению с предыдущими ревизиями)

- `KiloImportService.Api/Domain/Mapping/RoomsImportMapper.cs` — старый «простой» маппер,
  который писал в локальную visary_db. Не использовался в актуальном пайплайне.
- `KiloImportService.Api.Tests/Mapping/RoomsImportMapperTests.cs` — тесты к нему.
- Регистрация в `Program.cs` и meta-запись в `ImportTypesController` для
  `["roomsForm"]` (теперь оба сценария живут под кодом `"rooms"`).

---

## 🎯 Чек-лист отладки

### Импорт упал на 4xx/5xx от Visary

- [ ] Открыть `docker compose logs -f backend | grep "Visary →\|Visary error"`
- [ ] В строке `Visary → POST/PATCH ... body=...` посмотреть ровно то, что ушло
- [ ] В строке `Visary error <code>: <body>` — что Visary ответил
- [ ] Сравнить с эталонами в `RoomImport/описание методов.txt`
- [ ] При 422 — проверить обязательные поля (`Type` в Section, `UniqueNumber` в Room)
- [ ] При 500 «Can not add property RowVersion» — это `forceUpdate=true` + ID/RowVersion в теле

### Импорт прошёл, но помещения не там, где ожидалось

- [ ] Проверить лог `RoomsForm.Validate: загружен справочник RoomKind ... — N записей`:
      убедиться, что там есть нужные виды (`Квартира`, `Машиноместо`).
- [ ] Проверить `RoomsForm.Validate: лист 'X' → ожидаемый вид помещений 'Y' (ID=…)` —
      имя листа резолвится в правильный Kind.
- [ ] Проверить `RoomsForm.Apply: ───── Лист 'X' — N валидных строк` — все ли листы обработаны.
- [ ] При расхождении row.Kind vs sheet.Kind — будет лог-warn,
      доверяем строке (колонка приоритетнее).

### Все строки получили `site_mismatch`

- [ ] Лог в Validate показывает: `файл(НПС=…, Этап=…, РНС=…) vs site(НПС=…, Этап=…, РНС=…)`.
- [ ] У выбранного ОКСа `ConstructionProjectNumber` / `StageNumber` могут быть пустыми
      (синхронизация Sites из Visary могла не подтянуть `StageNumber` — он не входит в
      базовый `SiteColumns` в `ListViewClient`, маппер берёт его через `GetSiteByIdFullAsync`).

---

## 📚 См. также

- `doc_project/50-visary-api-new-methods.md` — методы Visary client (PATCH, forceUpdate, body-логирование)
- `doc_project/56-visary-dto-deserialization-pitfalls.md` — `JsonElement?` для полиморфных полей
- `doc_project/14-imports-backend-integration.md` — общий контур импорта (UI ↔ pipeline)
- `doc_project/15-signalr-progress.md` — прогресс импорта по SignalR
- `RoomImport/описание методов.txt` — эталонные JSON-тела запросов
- `RoomImport/room_sa_create.puml` — диаграмма исходного сценария
