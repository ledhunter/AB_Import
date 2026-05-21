# 🏢 Финмодель: привязка Организации-Застройщика к Группе компаний

## 📋 Описание

**Статус**: ✅ Реализовано (v1)
**Дата**: 2026-05-21
**Маппер**: `FinModelImportMapper` (код типа `"finmodel"`)
**Дополняет**: [doc 99 — Organization+ProjectManagement по ИНН](99-finmodel-organization-link.md)

После того как [doc 99](99-finmodel-organization-link.md) находит/создаёт организацию-
застройщика по ИНН и привязывает её к объекту через `projectmanagement`, импорт
дополнительно пытается проставить у этой организации **поле `Group`** (материнская
группа компаний — справочник `companygroup`).

В шаблоне «Параметры к переносу в АБ.xlsx» строка **14** листа `Inputs`:

| Координата | Содержимое | Назначение |
|------------|------------|------------|
| `C14` | `Группа компаний` | название параметра (ключ) |
| `E14` | `ГК Строитель` | значение — наименование ГК |

⚠️ **Особенность**: значение лежит в **E**, а не в стандартной колонке этапа `H`
(как остальные параметры финмодели). ГК — это одно значение для всех этапов
сразу, у неё нет «этапной разметки». Под это сделан новый механизм
[`SingleValueOverride`](#singlevalueoverride-в-парсере) в `KeyValueVertical`-парсере.

Колонка **опциональна**: шаблоны без строки `Группа компаний` (и старые без раздела
«Основные данные» целиком) продолжают работать без ошибок.

---

## 🔄 Поток (3 шага после doc 99)

```
LinkBorrowerOrganizationAsync завершён → orgId известен (найден или создан)
       │
       ▼
① GET /api/visary/crud/organization/{orgId}     → OrganizationFull
       │
       ├─ Group != null  → ✅ skip (организация уже привязана к ГК — идемпотентность)
       │
       └─ Group == null:
            ② POST /api/visary/listview/companygroup
               Filter ["Title","=","{companyGroupTitle}"]
                  │
                  ├─ 0 записей  → ❌ row-error «ГК не найдена, тк в Visary нет записи …» → продолжаем
                  ├─ >1 записи  → ❌ row-error «ГК не найдена, тк найдено N записей …» → продолжаем
                  └─ 1 запись →
                       ③ PATCH /api/visary/crud/organization/{orgId}?forceUpdate=false
                          { ID, RowVersion, Group: {ID, Title, Hidden:false} }
                          → ✅ Group привязан
```

**Принцип ошибок**: row-error от ГК-флоу **не отменяет** уже применённые
`FinishingMaterial` / `EstateClass` / `Address` / `Organization` / `Indicators`.
Та же non-transactional семантика, что в [doc 99](99-finmodel-organization-link.md).

**Привязка к строке (v1.1, 2026-05-21)**: `RowError` от Apply-фазы получает
опциональные поля `SourceRowNumber` и `Sheet`. ГК-ошибки (`company_group_not_found`,
`company_group_multiple_found`, `company_group_link_error`) и заодно
`organization_link_error`, `indicator_*`, `visary_*` привязываются к
**`firstRow.Sheet` + `firstRow.SourceRowNumber`** (params-строке, которую
Apply берёт из `ValidateAsync`). Фронт группирует по `(Sheet, RowNumber)` и
рендерит ошибку внутри таблицы листа `Inputs (E)` рядом с применёнными
строками — НЕ в верхнем блоке «Ошибки уровня файла». Этот блок остаётся для
Validate-ошибок (отсутствие колонок, неверный лист и т.п.), у которых
`SourceRowNumber=null` (Pipeline пишет `0`, фронт это и означает «file-level»).

---

## ✅ Правильная реализация

### `SingleValueOverride` в парсере

Существующий `KeyValueVertical`-парсер берёт значения из колонок этапов (H, I, J,…).
Для «Группы компаний» значение лежит в одной фиксированной колонке `E14`. Чтобы не
ломать общую раскладку, добавлен опциональный механизм:

```csharp
// FileLayoutHint.cs
public sealed record SingleValueOverride(string KeyText, string ValueColumn);

public sealed record KeyValueVertical(
    string SheetName,
    string KeyColumn,
    string ValueStartColumn,
    StageCountReference? StageCount = null,
    BudgetSectionHint? Budget = null,
    ChapterScheduleHint? ChapterSchedule = null,
    IReadOnlyList<SingleValueOverride>? SingleValues = null) : FileLayoutHint;
```

Парсер (`XlsxParser.ParseKeyValueVertical`) для каждого override:
1. Находит строку в `keyByRow` по `KeyText` (case-insensitive).
2. Читает `sheet.Cell(row, ValueColumn)`.
3. Подставляет это значение в `Cells[KeyText]` для **каждого** эмитируемого `ParsedRow`
   (значение одинаково для всех этапов).

Если строки с таким `KeyText` нет — override молча игнорируется (обратная совместимость).

### LayoutHint в `FinModelImportMapper`

```csharp
public FileLayoutHint LayoutHint { get; } = new KeyValueVertical(
    SheetName: "Inputs",
    KeyColumn: "C",
    ValueStartColumn: "H",
    // ... StageCount / Budget / ChapterSchedule ...
    SingleValues: new[]
    {
        new SingleValueOverride(KeyText: "Группа компаний", ValueColumn: "E"),
    });
```

### Алиасы колонки

```csharp
// FinModelImportMapper.cs
private static readonly string[] CompanyGroupAliases =
    ["Группа компаний", "ГК", "CompanyGroup", "Group"];
```

### Парсинг значения

```csharp
// ValidateParametersAsync — детект колонки (НЕ обязательная):
var fileCompanyGroupCol = FindColumn(allColumns, CompanyGroupAliases);

// В цикле строки — мягкое чтение без row-error (опциональность):
string? companyGroupValue = null;
if (fileCompanyGroupCol is not null
    && row.Cells.TryGetValue(fileCompanyGroupCol, out var cgRaw)
    && !string.IsNullOrWhiteSpace(cgRaw))
{
    companyGroupValue = cgRaw.Trim();
}

// В MappedRow JSON — рядом с Inn / BorrowerTitle:
var mappedJson = JsonSerializer.Serialize(new
{
    // ...
    Inn               = innValue,
    BorrowerTitle     = borrowerTitleValue,
    CompanyGroupTitle = companyGroupValue,
    Indicators        = indicatorValues,
});
```

### Apply: `LinkCompanyGroupAsync`

```csharp
// ApplyParametersAsync — после LinkBorrowerOrganizationAsync:
int? linkedOrgId = null;
if (!string.IsNullOrWhiteSpace(inn) && !string.IsNullOrWhiteSpace(borrowerTitle))
{
    try { linkedOrgId = await LinkBorrowerOrganizationAsync(siteId, inn!, borrowerTitle!, ct); }
    catch (Exception ex) { /* row-error organization_link_error */ }
}

if (linkedOrgId is int orgIdForGroup && !string.IsNullOrWhiteSpace(companyGroupTitle))
{
    try
    {
        await LinkCompanyGroupAsync(orgIdForGroup, companyGroupTitle!, errors, ct);
    }
    catch (Exception ex)
    {
        errors.Add(new RowError(null, "company_group_link_error",
            $"ГК не найдена, тк ошибка обновления организации: {ex.Message}"));
    }
}
```

Сам метод (4 исхода, ошибки идут в `errors`, исключения только на технических сбоях):

```csharp
private async Task LinkCompanyGroupAsync(
    int orgId, string companyGroupTitle, List<RowError> errors, CancellationToken ct)
{
    // (1) Уже привязана?
    var orgFull = await _visaryClient.GetOrganizationByIdAsync(orgId, ct);
    if (orgFull.Group is { ID: var existingGroupId, Title: var existingTitle })
        return; // skip — успех без действий

    // (2) Поиск по точному Title.
    var groups = await _listViewClient.GetCompanyGroupsByTitleAsync(companyGroupTitle, ct);
    var needle = companyGroupTitle.Trim();
    var matches = groups.Data
        .Where(g => string.Equals(g.Title?.Trim(), needle, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (matches.Count == 0)
    {
        errors.Add(new RowError(null, "company_group_not_found",
            $"ГК не найдена, тк в Visary нет записи companygroup с Title='{companyGroupTitle}'."));
        return;
    }
    if (matches.Count > 1)
    {
        var ids = string.Join(", ", matches.Select(g => g.ID));
        errors.Add(new RowError(null, "company_group_multiple_found",
            $"ГК не найдена, тк в Visary найдено несколько записей companygroup с Title='{companyGroupTitle}' (ID: {ids}). Однозначно сопоставить нельзя."));
        return;
    }

    // (3) PATCH organization.Group.
    var group = matches[0];
    await _visaryClient.UpdateOrganizationGroupAsync(orgId, group.ID, group.Title ?? companyGroupTitle, ct);
}
```

### `UpdateOrganizationGroupAsync` в `CrudClient`

Тот же паттерн, что в [doc 63](63-site-finishing-material-update-crud.md) /
[doc 69](69-finmodel-address.md): GET `/crud/organization/{id}` для актуального
`RowVersion`, затем PATCH `?forceUpdate=false`:

```csharp
public async Task<bool> UpdateOrganizationGroupAsync(
    int organizationId, int groupId, string groupTitle, CancellationToken ct)
{
    var current = await GetCrudByIdAsync<OrganizationFull>(
        VisaryMnemonics.Organization, organizationId, ct);

    var body = new
    {
        ID = organizationId,
        current.RowVersion,
        Group = new { ID = groupId, Title = groupTitle, Hidden = false },
    };
    await PatchCrudAsync(
        $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Organization}/{organizationId}?forceUpdate=false",
        body, $"{VisaryMnemonics.Organization}/{organizationId}", ct);
    return true;
}
```

### ⚠️ Важно

- **Колонка опциональна (одиночная, не пара).** Без неё ГК-flow пропускается.
  В отличие от пары ИНН + Borrower (doc 99), `Группа компаний` не требует
  «согласованной пары» — row-error на её пустоту не выдаётся.
- **`SingleValueOverride` применяется ко всем этапам.** Если шаблон с N этапами,
  то Cells["Группа компаний"] = E14 в каждой эмитируемой строке. Это сделано
  потому, что ГК у организации одна — не зависит от этапа.
- **Visary `Filter=["Title","=",X]` иногда матчит подстроку с пробелами** —
  фильтруем локально через `Trim()+OrdinalIgnoreCase` (тот же паттерн, что
  в [doc 76 ДДУ](76-share-agreement-dedup.md) и
  [doc 99 Organization](99-finmodel-organization-link.md)).
- **4 исхода — взаимоисключающие**:
  - skip — `Group != null` (идемпотентность);
  - linked — PATCH успешно;
  - not-found — `matches.Count == 0`, row-error `company_group_not_found`;
  - multiple-found — `matches.Count > 1`, row-error `company_group_multiple_found`.
- **Технические ошибки PATCH** (Visary 5xx, timeout) — отдельный код ошибки
  `company_group_link_error`, ловится в caller (`ApplyParametersAsync`).
- **PATCH передаёт `Group: { ID, Title, Hidden:false }`** — как в Visary UI.
  ID — авторитативен; `Title` дублируется, чтобы лог Visary показывал понятное
  «к чему привязали».
- **`OrganizationRaw` расширен полем `Group: VisaryRef?`** — в `OrganizationColumns`
  оно уже запрашивалось из listview, но в DTO не было. Добавили — теперь listview-
  отклик содержит привязку.

---

## ❌ Типичные ошибки

### Ошибка 1: брать значение из колонки этапа `H`

```csharp
// ❌ НЕПРАВИЛЬНО — без SingleValueOverride значение «Группы компаний» попало бы в H14
// (вместе с другими параметрами этапа 1), а в файле там пусто → ГК всегда пустая.
```

**Симптом**: парсер вернёт `Cells["Группа компаний"] = ""` (значение из H14, которое
пустое). Маппер пропустит шаг.

### Ошибка 2: требовать колонку как обязательную

```csharp
// ❌ НЕПРАВИЛЬНО — column_not_found на любом старом шаблоне без раздела «Основные данные»
if (fileCompanyGroupCol is null)
    fileErrors.Add(BuildColumnNotFoundError(...));
```

**Симптом**: ВСЕ старые импорты Финмодели начинают валиться. Колонка — опциональна.

### Ошибка 3: бросать исключение при `matches.Count == 0`

```csharp
// ❌ НЕПРАВИЛЬНО — отменит остальные параметры (Indicators, etc.) внутри try-catch
if (matches.Count == 0) throw new KeyNotFoundException(...);
```

Правильно — `errors.Add(...)` и `return`. Маппер продолжает с показателями.

### Ошибка 4: PATCH'ить без чтения текущего `RowVersion`

```csharp
// ❌ НЕПРАВИЛЬНО — optimistic-lock конфликт, потому что RowVersion=0
await PatchCrudAsync(url, new { ID = orgId, Group = ... }, ...);
```

В Visary `RowVersion` обязателен при `forceUpdate=false`. Если его нет в body или
он не совпадает с актуальным — Visary вернёт 409 Conflict.

### Ошибка 5: использовать `forceUpdate=true`

```csharp
// ❌ НЕПРАВИЛЬНО — Visary внутри пытается «дописать» RowVersion в загруженный
// объект и падает с "Property RowVersion already exists" (та же грабля, что
// в doc 63 для UpdateSiteFinishingMaterialAsync).
```

Всегда `forceUpdate=false`. Конфликты — это нормально, перезапрашиваем GET.

---

## 📍 Применение в проекте

| Артефакт | Файл | Ключевые места |
|----------|------|----------------|
| Мнемоника | [VisaryMnemonics.cs](../Visary.Api.Client/Common/VisaryMnemonics.cs) | `CompanyGroup = "companygroup"` |
| DTO | [VisaryEntities.cs](../Visary.Api.Client/Dto/VisaryEntities.cs) | `CompanyGroupRaw` + `OrganizationRaw.Group` |
| ListView метод | [ListViewClient.cs](../Visary.Api.Client/ListView/ListViewClient.cs) | `GetCompanyGroupsByTitleAsync` + `CompanyGroupColumns` |
| CRUD метод | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | `UpdateOrganizationGroupAsync` |
| Layout-механизм | [FileLayoutHint.cs](../KiloImportService.Api/Domain/Importing/FileLayoutHint.cs) | `SingleValueOverride`, `KeyValueVertical.SingleValues` |
| Парсер | [XlsxParser.cs](../KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs) | `ParseKeyValueVertical`: overrideValues + замена `Cells[key]` |
| Алиасы + хинт | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `CompanyGroupAliases`, `SingleValues` в LayoutHint |
| Apply | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `LinkCompanyGroupAsync` + изменённая сигнатура `LinkBorrowerOrganizationAsync→int` |
| Тесты | [FinModelImportMapperTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelImportMapperTests.cs) | 5 новых тестов (absent / already-set / found-one / not-found / multiple / patch-fails) |

---

## 🧪 Тесты (5 новых, всё проходит — 226/226 в полном прогоне)

| Тест | Что проверяет |
|------|---------------|
| `ApplyAsync_CompanyGroupColumnAbsent_NoCallToVisary` | Без колонки — нет вызовов `companygroup` / PATCH |
| `ApplyAsync_CompanyGroupAlreadySet_SkipsLookupAndPatch` | `OrganizationFull.Group != null` → ни lookup, ни PATCH |
| `ApplyAsync_CompanyGroupFoundExactlyOne_PatchesOrganization` | 1 match → `UpdateOrganizationGroupAsync(orgId, groupId, title)` |
| `ApplyAsync_CompanyGroupNotFound_RowErrorButContinues` | 0 matches → row-error `company_group_not_found`, FK всё равно применены |
| `ApplyAsync_CompanyGroupMultipleFound_RowErrorButContinues` | >1 matches → row-error `company_group_multiple_found` с ID в сообщении |
| `ApplyAsync_CompanyGroupPatchFails_RowErrorButContinues` | Visary 5xx на PATCH → row-error `company_group_link_error` |

---

## 🎯 Чек-лист «добавить новое одиночное поле в KV-vertical-шаблон»

(Пригодится если завтра захочется парсить ещё что-то из E16/F18/etc.):

- [ ] В файле появилась строка с текстом-ключом в колонке `C` и значением в
      специальной колонке (не в этапной `H`)
- [ ] Добавить алиасы для текста-ключа (массив `XxxAliases`)
- [ ] В `LayoutHint` мапера добавить элемент в массив `SingleValues:
      new SingleValueOverride("Текст-ключ", "БукваКолонки")`
- [ ] В `ValidateParametersAsync` — детект колонки через `FindColumn(allColumns, XxxAliases)`
      и парсинг значения (если опционально — мягкое чтение без row-error)
- [ ] В `mappedJson` — добавить поле для значения
- [ ] В Apply-flow — прочитать поле из `MappedRow.MappedValues.RootElement` и вызвать
      нужный Visary-метод
- [ ] Тесты на 4 ветви: column-absent / value-empty / happy-path / Visary-fails
- [ ] Доку (новый файл `doc_project/NN-…md` + ссылка в README)

---

**Версия**: 1.1
**Дата**: 2026-05-21
**Изменения v1.1**: ГК-ошибки (как и `organization_link_error` / `indicator_*` / `visary_*`)
теперь привязываются к params-строке (`Sheet="Inputs (E)"`, `SourceRowNumber` из
firstRow). Без этого они попадали в верхний блок «Ошибки уровня файла» — теперь
рендерятся прямо в строке таблицы листа. Для этого расширен
[`RowError`](../KiloImportService.Api/Domain/Mapping/IImportMapper.cs) опциональными
полями `SourceRowNumber: int?` и `Sheet: string?`, а
[`ImportPipeline.RunApplyAsync`](../KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs)
использует их вместо хардкода `Sheet=""`, `SourceRowNumber=0` (последний остаётся
для file-level ошибок Validate-фазы).
