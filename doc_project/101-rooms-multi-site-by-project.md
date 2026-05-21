# 🏢 Импорт «Помещения» — Site резолвится per-row внутри Project (без выбора в UI)

## 📋 Описание

До этой ревизии импорт `rooms` требовал от пользователя выбрать **Проект** _и_ **Объект строительства (Site)** в UI. Маппер per-row проверял, что НПС/Этап файла совпадают с выбранным Site (`site_mismatch` иначе).

Заказчик: реальные файлы вроде `2025.12.04 UB5PT1 _СЗ Метрикс Мега ЖК Ритм.xlsx` содержат строки **разных** ОКС в рамках одного проекта (разные НПС и/или Этап). Жёсткая привязка к одному Site не позволяла залить такой файл одной сессией — приходилось руками отделять данные по ОКС.

Новая модель: пользователь выбирает только **Проект**. Маппер для каждой строки резолвит Site внутри проекта по ключам `(ConstructionProjectNumber, StageNumber)` через listview-эндпоинт Visary, и Apply группирует валидные строки по найденному `SiteId`.

---

## ✅ Правильная реализация

### 1. Visary endpoint

```csharp
// Visary.Api.Client/ListView/ListViewClient.cs
// POST listview/constructionsite/onetomany/Project?associationId={projectId}
// Body содержит Filter [["ConstructionProjectNumber","=",X],"and",["StageNumber","=",Y]]
public Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAndKeysAsync(
    int projectId, string? projectNumber, string? stageNumber, CancellationToken ct)
{
    var parts = new List<string>(2);
    if (!string.IsNullOrWhiteSpace(projectNumber))
        parts.Add(FilterByString("ConstructionProjectNumber", projectNumber));
    if (!string.IsNullOrWhiteSpace(stageNumber))
        parts.Add(FilterByString("StageNumber", stageNumber));
    string? filter = parts.Count == 0 ? null
        : parts.Aggregate((a, b) => FilterAnd(a, b));
    // ...
}
```

### 2. ValidateAsync — pre-pass резолва per уникальной (НПС, Этап)

```csharp
// Один listview-запрос на КАЖДУЮ уникальную пару — не на строку!
var uniqueKeys = new HashSet<(string ProjectNum, string StageRaw)>();
foreach (var pr in dataRows) { /* собираем уникальные пары */ }

var siteByKey = new Dictionary<(string, string), SiteResolution>();
foreach (var (pn, sn) in uniqueKeys)
{
    var resp = await _listView.GetSitesByProjectAndKeysAsync(projectId, pn, sn, ct);
    var matches = resp.Data
        .Where(s => string.Equals((s.ConstructionProjectNumber ?? "").Trim(), pn,
            StringComparison.OrdinalIgnoreCase))
        .Where(s => string.Equals((s.StageNumber ?? "").Trim(), sn,
            StringComparison.OrdinalIgnoreCase))
        .ToList(); // 👈 локальная страховка: Visary "=" нечувствителен к whitespace
    siteByKey[(pn, sn)] = new SiteResolution(matches);
}

// Per-row: lookup из кэша, исходов три:
//   matches.Count == 1  → SiteId в MappedValues, IsValid=true
//   matches.Count == 0  → row-error "site_not_found_in_project"
//   matches.Count >  1  → row-error "site_ambiguous" со списком ID
```

### 3. ApplyAsync — группировка по SiteId

```csharp
// Каждый сайт получает свой pre-pass (snapshot/секции/developer).
// Главный Parallel.ForEachAsync — по группам (SiteId, Sheet, Section).
var rowsBySite = validRows
    .GroupBy(mr => GetIntOrNull(mr.MappedValues.RootElement, "SiteId") ?? 0)
    .Where(g => g.Key > 0)
    .ToDictionary(g => g.Key, g => g.ToList());

foreach (var (sid, siteRows) in rowsBySite)
{
    await TryUpdateSitePermissionNumberAsync(sid, siteRows, ct);
    // Sections find-or-create per-site, sectionCache ключ — (SiteId, Title)
    foreach (var sectionTitle in sectionTitlesNeeded) { ... }
    projectId = await ResolveDeveloperLinksAsync(sid, projectId, siteRows, Log, ct);
}

// Главный цикл (Parallel) — ключ группы теперь содержит SiteId.
var groupsByKey = validRows.GroupBy(mr =>
{
    var v = mr.MappedValues.RootElement;
    return (SiteId: GetIntOrNull(v, "SiteId") ?? 0,
            Sheet: GetStringOrNull(v, "Sheet") ?? "<unknown>",
            Section: GetStringOrNull(v, "SectionTitleNumeric")
                  ?? GetStringOrNull(v, "SectionTitle") ?? "");
}).Where(g => g.Key.SiteId > 0).ToList();
```

### 4. UI — `showSiteSelect=false` для rooms

```tsx
// App.tsx
const requiresSite = importType !== 'rooms';
const canSubmit =
  importType !== null &&
  projectId !== null &&
  (!requiresSite || siteId !== null) &&
  // ...

<ImportForm projectId={projectId} siteId={siteId}
  onProjectChange={setProjectId} onSiteChange={setSiteId}
  showSiteSelect={requiresSite} />
```

```tsx
// ImportForm.tsx — Select «Объект» внутри условного блока
{showSiteSelect && (
  <div className="field">
    <Select label="Объект строительства" ... />
  </div>
)}
```

### ⚠️ Важно

- **SiteColumns обязан содержать `StageNumber`** — иначе ответ Visary не доезжает с этим полем, и локальная страховка `s.StageNumber.Trim() == sn` всегда даёт пустой match.
- **Кэш по уникальным парам обязателен**: на крупных файлах (6000+ строк) пары (НПС, Этап) могут повторяться в каждой второй строке. Без кэша — DoS на Visary.
- **РНС больше НЕ блокирует строку**. Раз Site однозначно резолвится по (НПС,Этап), расхождение РНС идёт в Debug-лог. PATCH Site.РНС (когда в Site пусто, а в файле есть) остаётся в `Apply.TryUpdateSitePermissionNumberAsync` и теперь вызывается per-site.
- **`sectionCache` ключуется `(SiteId, Title)`** — две одноимённые секции в разных сайтах НЕ должны переопределять друг друга.
- **`snapshot` уже хранит `VisarySiteId`** — поэтому ключ snapshot-а (`RoomSnapshotKey`) изолирует сайты сам по себе; меняется только pre-load: цикл `LoadForSiteAsync` по всем задействованным сайтам с накоплением в общий `ConcurrentDictionary`.

---

## ❌ Типичные ошибки

### 1. Опциональные параметры вместо нового метода → ломаются позиционные вызовы

```csharp
// НЕПРАВИЛЬНО — существующие вызовы GetSitesByProjectAsync(4584, default)
// получат default в позиционный аргумент `projectNumber: string?` и упадут.
Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAsync(
    int projectId, string? projectNumber = null, string? stageNumber = null,
    CancellationToken ct = default);

// ПРАВИЛЬНО — отдельный метод. Старый сохраняет сигнатуру, новый — для импорта rooms.
Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAndKeysAsync(
    int projectId, string? projectNumber, string? stageNumber, CancellationToken ct = default);
```

### 2. Резолв site внутри foreach-цикла per-row

```csharp
// НЕПРАВИЛЬНО — N×K listview-запросов (K строк на N уникальных пар).
foreach (var row in dataRows)
{
    var resp = await _listView.GetSitesByProjectAndKeysAsync(projectId, pn, sn, ct);
    // ...
}

// ПРАВИЛЬНО — pre-pass: собираем уникальные пары, один запрос на пару, lookup в цикле.
```

### 3. `sectionCache` ключ только по `Title` (без `SiteId`)

```csharp
// НЕПРАВИЛЬНО — секция «1.1» в разных сайтах переопределит ID одна другой.
var sectionCache = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

// ПРАВИЛЬНО — изолировать сайты:
var sectionCache = new ConcurrentDictionary<(int SiteId, string Title), int>();
```

### 4. РНС блокирует строку, хотя сайт уже найден

```csharp
// НЕПРАВИЛЬНО (старый код): row-error если файл.РНС != site.РНС.
bool permissionOk = string.IsNullOrWhiteSpace(rowPermission)
                  || string.Equals(rowPermission, sitePermissionNumber, ...)
                  || string.IsNullOrWhiteSpace(sitePermissionNumber);
if (!projectOk || !stageOk || !permissionOk) { /* site_mismatch */ }

// ПРАВИЛЬНО (doc 101): раз сайт однозначно резолвлен по (НПС,Этап),
// расхождение РНС → только Debug-лог. PATCH РНС в Site остаётся в Apply.
```

### 5. `StageNumber` объявлен `string?`, а listview шлёт число

```jsonc
// Ответ listview/constructionsite/onetomany/Project (Visary):
{ "Data": [{ "ID": 12345, "StageNumber": 1 /* ← Number, не String */ }] }
```

```csharp
// НЕПРАВИЛЬНО — System.Text.Json падает:
// «The JSON value could not be converted to System.String. Path: $.Data[0].StageNumber»
public string? StageNumber { get; set; }

// ПРАВИЛЬНО — Flexible-конвертер (Common/FlexibleStringJsonConverter.cs)
// нормализует Number/String/Bool → string?, сохраняя `string?` контракт.
[JsonConverter(typeof(FlexibleStringJsonConverter))]
public string? StageNumber { get; set; }
```

**Симптом**: row-error «site_not_found_in_project — в проекте N не удалось получить список объектов: The JSON value could not be converted to System.String. Path: $.Data[0].StageNumber». Lookup-кэш `siteByKey` пуст для ВСЕХ строк файла, импорт безуспешен. Перекликается с doc 56 (JsonElement? для Status/RoomCategory/MainSource) — здесь альтернатива через `JsonConverter<string?>`, чтобы не плодить `JsonElement?` в полях, по смыслу являющихся строками.

### 6. `>1` сайтов на пару → выбрать первый и продолжить

```csharp
// НЕПРАВИЛЬНО — серебряная пуля для коллизии данных в Visary.
var siteId = matches.OrderBy(m => m.ID).First().ID;

// ПРАВИЛЬНО — row-error site_ambiguous со списком ID. Дешевле починить в Visary,
// чем втихую залить ДДУ не в тот ОКС.
```

---

## 📍 Применение в проекте

| Слой | Файл | Что изменилось |
|------|------|----------------|
| Visary API | [Visary.Api.Client/ListView/ListViewClient.cs](../Visary.Api.Client/ListView/ListViewClient.cs) | `SiteColumns` + `StageNumber`; новый `GetSitesByProjectAndKeysAsync` |
| Visary API | [Visary.Api.Client/ListView/IListViewClient](../Visary.Api.Client/ListView/ListViewClient.cs) | Интерфейс расширен |
| Mapper Validate | [KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | `project_required` (вместо `site_required`); pre-pass резолва; `SiteResolution` record |
| Mapper Apply | то же | Группировка по `SiteId`; pre-pass per-site; `sectionCache` по `(SiteId,Title)` |
| MappedValues | то же | Новое поле `SiteId` |
| UI | [KiloImportService.Web/src/components/ImportForm/ImportForm.tsx](../KiloImportService.Web/src/components/ImportForm/ImportForm.tsx) | Prop `showSiteSelect?: boolean` |
| UI | [KiloImportService.Web/src/App.tsx](../KiloImportService.Web/src/App.tsx) | `requiresSite = importType !== 'rooms'` |
| Tests UI | [ImportForm.test.tsx](../KiloImportService.Web/src/components/ImportForm/__tests__/ImportForm.test.tsx) | Тест на `showSiteSelect=false` |
| Tests BE | [RoomsFormImportMapperApplyTests.cs](../KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperApplyTests.cs) | 4 новых Validate-теста + multi-site Apply-тест; `MakeRow` принимает `siteId` |

---

## 🎯 Чек-лист при дальнейших правках rooms-импорта

- [ ] Если добавляете столбец в Site → проверьте `SiteColumns` в `ListViewClient.cs`
- [ ] Если меняете поля Validate → не забудьте про `SiteId` в `MappedValues`
- [ ] Apply: любой новый pre-pass должен быть в цикле `foreach (var (sid, siteRows) in rowsBySite)`
- [ ] `sectionCache` / любые per-site кэши — ключуются `(SiteId, ...)`, не одним `Title`
- [ ] Snapshot-key (`RoomSnapshotKey`) уже включает `VisarySiteId` — менять не нужно
- [ ] Тесты `ApplyAsync` — `ImportContext.VisarySiteId = null`, SiteId передаётся в `MappedValues`

---

## Связанные документы

- [68-rooms-import.md](68-rooms-import.md) — исходная сборка импорта rooms (НПС+Этап+РНС per-row сверка, до перехода на резолв)
- [77-room-uniqueness-building-section.md](77-room-uniqueness-building-section.md) — ключ уникальности Room включает BuildingSection
- [83-rooms-shifted-header-row.md](83-rooms-shifted-header-row.md) — HeaderAnchors, strict-skip листов без анкоров
- [89-mappedrow-sheet-invariant.md](89-mappedrow-sheet-invariant.md) — `MappedRow.Sheet` обязательно для multi-sheet
- [96-rooms-incremental-parallel-apply.md](96-rooms-incremental-parallel-apply.md) — snapshot diff-skip + Parallel.ForEachAsync (база, на которую надстроен multi-site)
- [97-rooms-apply-tests-and-budget-uploader-interface.md](97-rooms-apply-tests-and-budget-uploader-interface.md) — паттерн in-memory DB вне делегата (грабли с `Guid.NewGuid()` в лямбде)
