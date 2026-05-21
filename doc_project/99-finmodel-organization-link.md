# 🏢 Финмодель: привязка Организации (Заёмщик/Застройщик) к объекту по ИНН

## 📋 Описание

**Статус**: ✅ Реализовано (v1)
**Дата**: 2026-05-20
**Маппер**: `FinModelImportMapper` (код типа `"finmodel"`)

В шаблоне «Параметры к переносу в АБ.xlsx» лист **`Inputs`** содержит раздел
**«Основные данные»** (см. C11). В нём — две строки, важные для интеграции:

| Лист | Строка C | Колонка H (значение) | Назначение |
|------|---------|----------------------|------------|
| Inputs | `Заемщик/Застройщик` | `ООО СЗ Скай` | наименование юр. лица |
| Inputs | `ИНН` | `6319038948` | ClientID организации в Visary |

Маппер по ИНН ищет (или создаёт) запись `organization` в Visary, затем — по
аналогии с импортом «Помещения» (doc [75](75-projectmanagement-developer-link.md)) —
создаёт/переиспользует `projectmanagement` (связку Проект ↔ Организация ↔ Роль)
и привязывает её к выбранному `constructionsite`.

Пара колонок **опциональна**: старые шаблоны без раздела «Основные данные» продолжают
работать без ошибки. Если найдена только одна колонка из двух — row-error
`value_empty` на отсутствующее значение.

---

## 🔄 Поток (4 шага)

```
ИНН + наименование из листа «Inputs» (раздел «Основные данные»)
       │
       ▼
① POST /api/visary/listview/organization
   Filter ["ClientID","=","6319038948"]
       │
       ├─ found:    orgId = существующий ID
       │
       └─ not found:
            ② POST /api/visary/crud/organization
               { Title: "ООО СЗ Скай", ClientID: "6319038948", INN: "6319038948" }
                                  ▼
                              orgId = newId
       │
       ▼
③ POST /api/visary/listview/constructionsite/manytomany/projectmanagement
   ?associationId={siteId}
       │
       ├─ Organization.ID == orgId уже в списке → skip, организация уже привязана
       │
       └─ нет:
            ④ POST /api/visary/listview/projectmanagement/onetomany/Project
               ?associationId={projectId}
               Filter ["Organization","contains","ID:{orgId}"]
                                  ▼
                ┌── есть PM (любая роль): берём max(ID), переиспользуем
                │
                └── нет:
                     POST /api/visary/crud/projectmanagement
                     { Project: {ID: projectId},
                       Organization: {ID: orgId},
                       Role: {ID: 10, Title: "Застройщик"} }
                                  ▼
                       POST /api/visary/listview/constructionsite/manytomany/projectmanagement/link
                       ?associationId={siteId}&ids={pmId}
```

---

## ✅ Правильная реализация

### Алиасы колонок (раздел «Основные данные»)

```csharp
// FinModelImportMapper.cs
private static readonly string[] InnAliases =
    ["ИНН", "INN", "ИНН организации", "ИНН Застройщика", "ИНН Заемщика", "ИНН Заёмщика"];

// В файле точная строка C17 — «Заемщик/Застройщик» (через слэш, буква «е» а не «ё»).
private static readonly string[] BorrowerOrganizationAliases =
    [
        "Заемщик/Застройщик", "Заёмщик/Застройщик",
        "Заемщик / Застройщик", "Заёмщик / Застройщик",
        "Застройщик/Заемщик", "Застройщик/Заёмщик",
        "Застройщик", "Заемщик", "Заёмщик",
        "Borrower", "Developer", "BorrowerTitle",
    ];
```

### Маппер: опциональная пара колонок

```csharp
// ValidateParametersAsync — детект колонок (НЕ выдаёт column_not_found):
var fileInnCol      = FindColumn(allColumns, InnAliases);
var fileBorrowerCol = FindColumn(allColumns, BorrowerOrganizationAliases);

// Если хоть одна из двух найдена — читаем обе. Это даёт row-error «value_empty»,
// если пользователь заполнил только одну (мы требуем согласованную пару).
string? innValue = null;
string? borrowerTitleValue = null;
if (fileInnCol is not null || fileBorrowerCol is not null)
{
    innValue           = ReadCellTrimmed(row, fileInnCol ?? "ИНН", InnAliases, "ИНН", rowErrors);
    borrowerTitleValue = ReadCellTrimmed(row, fileBorrowerCol ?? "Заемщик/Застройщик",
                                         BorrowerOrganizationAliases, "Заемщик/Застройщик", rowErrors);
}

// В MappedRow JSON — рядом с FinishingMaterialId / EstateClassId / Address:
var mappedJson = JsonSerializer.Serialize(new
{
    Kind                   = "params",
    FinishingMaterialId    = finishingEntry!.Value.Id,
    EstateClassId          = estateEntry!.Value.Id,
    Address                = addressValue,
    Inn                    = innValue,
    BorrowerTitle          = borrowerTitleValue,
    Indicators             = indicatorValues,
    // ...
});
```

### Apply: `LinkBorrowerOrganizationAsync`

```csharp
// ApplyParametersAsync — после UpdateSiteAddressAsync, перед Indicators:
if (!string.IsNullOrWhiteSpace(inn) && !string.IsNullOrWhiteSpace(borrowerTitle))
{
    try
    {
        await LinkBorrowerOrganizationAsync(siteId, inn!, borrowerTitle!, ct);
    }
    catch (Exception ex)
    {
        // Не отменяем уже применённые FK/Address — добавляем row-error.
        errors.Add(new RowError(null, "organization_link_error", ...));
    }
}
```

Сам метод:

```csharp
private async Task LinkBorrowerOrganizationAsync(
    int siteId, string inn, string borrowerTitle, CancellationToken ct)
{
    // (1) Поиск Organization по ClientID=ИНН
    var orgs = await _listViewClient.GetOrganizationsByClientIdAsync(inn, ct);
    var existingOrg = orgs.Data.FirstOrDefault(o =>
        string.Equals(o.ClientID?.Trim(), inn.Trim(), StringComparison.Ordinal));

    int orgId;
    if (existingOrg is not null)
        orgId = existingOrg.ID;
    else
    {
        var created = await _visaryClient.CreateOrganizationAsync(new OrganizationCreateRequest
        {
            Title = borrowerTitle,
            ClientID = inn,
            INN = inn,
        }, ct);
        orgId = created.ID;
    }

    // (2) Уже привязана к сайту?
    var siteSPm = await _listViewClient.GetProjectManagementsBySiteAsync(siteId, ct);
    if (siteSPm.Data.Any(pm => pm.Organization?.ID == orgId)) return;   // 👈 идемпотентность

    // (3) Поиск PM в проекте — БЕЗ фильтра по Role (берём любую).
    var siteFull = await _visaryClient.GetSiteByIdFullAsync(siteId, ct);
    var projectId = siteFull.Project?.ID
        ?? throw new InvalidOperationException("У объекта не задан Project");

    var inProject = await _listViewClient.GetProjectManagementsByProjectAsync(
        projectId, orgId, roleId: null, ct);   // 👈 roleId: null
    var reusable = inProject.Data
        .Where(pm => pm.Organization?.ID == orgId)
        .OrderByDescending(pm => pm.ID)
        .FirstOrDefault();

    int pmIdToLink;
    if (reusable is not null)
        pmIdToLink = reusable.ID;
    else
    {
        var createdPm = await _visaryClient.CreateProjectManagementAsync(new ProjectManagementCreateRequest
        {
            Project = new VisaryRef { ID = projectId },
            Organization = new VisaryRef { ID = orgId },
            Role = new VisaryRef
            {
                ID = ProjectManagementRoles.Developer,         // 👈 10 = Застройщик по умолчанию
                Title = ProjectManagementRoles.DeveloperTitle,
            },
            Affiliation = 0,
        }, ct);
        pmIdToLink = createdPm.ID;
    }

    await _visaryClient.LinkProjectManagementToSiteAsync(siteId, pmIdToLink, ct);
}
```

### Новые методы в `Visary.Api.Client`

```csharp
// Visary.Api.Client/Dto/VisaryCrudRequests.cs
public sealed class OrganizationCreateRequest
{
    public string? Title { get; set; }
    public string? ClientID { get; set; }   // 👈 ИНН — он же в Visary ClientID для поиска
    public string? INN { get; set; }
    public string? KPP { get; set; }
    public string? OGRN { get; set; }
}

// Visary.Api.Client/CRUD/CrudClient.cs
public async Task<OrganizationRaw> CreateOrganizationAsync(
    OrganizationCreateRequest request, CancellationToken ct)
{
    return await PostCrudAsync<OrganizationRaw>(
        $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Organization}",
        request, VisaryMnemonics.Organization, ct);
}
```

### ⚠️ Важно

- **Колонки опциональны как пара.** Старые файлы без раздела «Основные данные»
  продолжают работать — никакого `column_not_found` на отсутствие ИНН/Borrower.
- **Если найдена только одна** из колонок — это row-error `value_empty` на
  отсутствующее значение (см. тест `ValidateAsync_OnlyInnColumnPresent_*`).
  Иначе непонятно как создавать организацию (только ИНН без названия — нельзя
  POST'ить).
- **Поиск Organization** — `listview/organization` с фильтром `["ClientID","=",X]`.
  Дополнительно фильтруем локально по `Trim()+OrdinalIgnoreCase`, потому что
  Visary `=` иногда матчит подстрокой при пробелах (та же фикcа, что и для ДДУ
  в doc [76](76-share-agreement-dedup.md)).
- **При CreateOrganization** передаём и `ClientID`, и `INN` — Visary в разных
  записях использует поле по-разному (ClientID — внешний ID для поиска, INN —
  стандартное поле формы организации). Дублирование безопасно: формы Visary
  показывают первое непустое.
- **PM-поиск в проекте — БЕЗ фильтра по Role.** Одна организация может уже
  присутствовать в проекте в любой роли (Застройщик/Заемщик/Подрядчик); нас
  устраивает любая существующая запись — переиспользуем по `max(ID)`. Это
  отличие от Rooms-импорта (doc 75), где явно требуется именно роль «Застройщик».
- **При создании PM роль = `Developer (10)`.** Захардкожено, как и в Rooms.
  Справочник `role` в Visary не интегрирован; «Заемщик» — это шильдик роли в
  файле, физически в Visary он чаще оформляется как `Developer` (одна организация
  выступает и заёмщиком, и застройщиком одновременно). Будущая итерация —
  динамика по живому справочнику.
- **Не падаем на ошибке Organization/PM.** Уже применённые FK/Address — не
  откатываются (non-transactional семантика, как и для `Indicators`). Ошибка
  попадает в `errors` под кодом `organization_link_error`, а `AppliedCount = 0`
  (потому что `ApplyParametersAsync` возвращает 0, если `errors.Count > 0`).
- **`ApplyParametersAsync` дёргает organization-link только ОДИН РАЗ.** Метод
  берёт первую `params`-строку, а в шаблоне `KeyValueVertical` строк может быть N
  (по числу этапов). ИНН/Borrower одинаковы на всех этапах — повторный вызов
  был бы лишним. Идемпотентность дополнительно обеспечивается шагом (2) —
  «уже привязана к сайту → skip».

---

## ❌ Типичные ошибки

### Ошибка 1: добавить колонки как обязательные

```csharp
// ❌ НЕПРАВИЛЬНО — column_not_found на старых шаблонах без раздела «Основные данные»
if (fileInnCol is null)
    fileErrors.Add(BuildColumnNotFoundError(allColumns, InnAliases, "Не найдена колонка 'ИНН'"));
```

**Симптом**: все импорты «Финмодели» по старому шаблону начинают валиться с
`column_not_found`. Колонки должны быть **опциональной парой**.

### Ошибка 2: фильтровать PM в проекте по Role=Developer

```csharp
// ❌ НЕПРАВИЛЬНО — пропускаем существующую PM с ролью «Заемщик» и создаём дубликат
var inProject = await _listViewClient.GetProjectManagementsByProjectAsync(
    projectId, orgId, roleId: ProjectManagementRoles.Developer, ct);
```

**Почему**: в файле название поля — «**Заемщик/Застройщик**» (через слэш). Одна
организация физически может быть оформлена в Visary как `Borrower` (отдельная
роль ID, не 10) — и фильтр по `Developer` её не найдёт, мы создадим дубликат
PM с ролью `Developer`. Берём ЛЮБУЮ существующую запись (без фильтра по role),
переиспользуем.

### Ошибка 3: передавать в CreateOrganization только Title без ClientID

```csharp
// ❌ НЕПРАВИЛЬНО — Organization создаётся, но при следующем импорте мы её не найдём
var created = await _visaryClient.CreateOrganizationAsync(new OrganizationCreateRequest
{
    Title = borrowerTitle,
    // ClientID опущен — listview/organization?Filter=["ClientID","=",ИНН] вернёт пусто
});
```

**Симптом**: каждый повторный импорт того же файла плодит новые `organization`
с одним и тем же названием, потому что поиск по ClientID не находит ранее
созданную (там пусто). Всегда передавать `ClientID = ИНН`.

### Ошибка 4: затирать `RowVersion` существующей PM

```csharp
// ❌ НЕПРАВИЛЬНО — PATCH PM с пустым RowVersion → optimistic-lock конфликт
await _visaryClient.PatchProjectManagementAsync(pmId, new { Role = ... });
```

Мы вообще не PATCH-им существующую PM — просто **link-аем** её к сайту через
manytomany. Если на сайте уже есть PM с этой Organization (шаг 2) — skip.

---

## 📍 Применение в проекте

| Артефакт | Файл | Ключевые места |
|----------|------|----------------|
| Алиасы колонок | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `InnAliases`, `BorrowerOrganizationAliases` |
| Валидация | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `ValidateParametersAsync` (опциональная пара) |
| Apply-flow | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `ApplyParametersAsync` + `LinkBorrowerOrganizationAsync` |
| CRUD DTO | [VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs) | `OrganizationCreateRequest` |
| CRUD клиент | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | `CreateOrganizationAsync` (POST `/crud/organization`) |
| Role константы | [ProjectManagementDtos.cs](../Visary.Api.Client/Dto/ProjectManagementDtos.cs) | `ProjectManagementRoles.Developer = 10` (уже было) |
| Listview методы | [ListViewClient.cs](../Visary.Api.Client/ListView/ListViewClient.cs) | `GetOrganizationsByClientIdAsync`, `GetProjectManagementsBySiteAsync`, `GetProjectManagementsByProjectAsync` (уже были, см. doc 75) |
| Link метод | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | `LinkProjectManagementToSiteAsync` (уже был, см. doc 75) |
| Тесты | [FinModelImportMapperTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelImportMapperTests.cs) | 9 новых тестов (алиасы, опциональность, found/not-found, идемпотентность, reuse, no-Inn-skip, failure-isolation) |

---

## 🧪 Тесты (9 новых, всё проходит — 63/63)

| Тест | Что проверяет |
|------|---------------|
| `ValidateAsync_InnAndBorrowerColumns_StoredInMappedJson` | значения попадают в `MappedRow.Inn` и `BorrowerTitle` |
| `ValidateAsync_InnColumnAliases_WorkCaseInsensitive` | алиасы `INN` / `инн` / `ИНН организации` |
| `ValidateAsync_BorrowerColumnAliases_WorkCaseInsensitive` | алиасы `Заёмщик/Застройщик` / `Застройщик` / `Borrower` |
| `ValidateAsync_MissingBothOrgColumns_NoErrorBackwardCompatible` | старые шаблоны без раздела работают без file-error |
| `ValidateAsync_OnlyInnColumnPresent_ReturnsRowErrorForBorrower` | непарный ИНН → row-error на наименование |
| `ApplyAsync_OrganizationNotFoundByInn_CreatesOrgAndPm` | full-flow: создание Organization → PM → link |
| `ApplyAsync_OrganizationFoundByInn_DoesNotCreateOrg` | если по ИНН нашлась — Create не вызывается |
| `ApplyAsync_OrgAlreadyLinkedToSite_DoesNotCreateOrLinkPm` | идемпотентность: уже привязана → skip |
| `ApplyAsync_PmExistsInProjectButNotOnSite_ReusesAndLinks` | переиспользуем PM из проекта по max(ID) + link |
| `ApplyAsync_NoInnNoBorrower_SkipsOrgFlow` | без колонок Visary API не вызывается вообще |
| `ApplyAsync_OrgLinkFailure_DoesNotBreakParameterUpdates` | падение Visary в org-flow не отменяет FK/Address |

---

## 🎯 Чек-лист «добавить новую роль в раздел «Основные данные» Финмодели»

(шаблон — если завтра захочется отдельно «Подрядчик» / «Тех. заказчик»):

- [ ] В файле появилась пара колонок «ИНН Подрядчика» + «Подрядчик» — добавить
      алиасы (наряду с `InnAliases` / `BorrowerOrganizationAliases`)
- [ ] Завести константу `ProjectManagementRoles.Contractor = ???` (нужен живой ID
      из Visary). Если ID неизвестен — оставить документ в Roadmap и не плодить
      «магические» 11/12/13
- [ ] В `LinkBorrowerOrganizationAsync` параметризовать roleId (передавать как
      параметр метода) — превратить функцию в `LinkOrganizationToProjectAsync(
      int siteId, string inn, string title, int roleId, string roleTitle, ...)`
- [ ] Раздельный кэш `pmByOrgByRole` или Dictionary<(orgId, roleId), pmId> — если
      flow становится мульти-ролевым (см. doc 75 чек-лист)
- [ ] Тесты: новый case с двумя ролями в одном файле (Заёмщик + Подрядчик)
- [ ] Обновить doc 99 с новой ролью + ссылку в README.md

---

**Версия**: 1.0
**Дата**: 2026-05-20
