# 🏗️ Привязка организации-Застройщика к объекту через `projectmanagement`

## 📋 Описание

В импорте «Помещения» по ПИНу застройщика из строки файла нужно найти
организацию в Visary и убедиться, что она привязана к выбранному объекту
строительства **с ролью «Застройщик»**. Связь живёт не напрямую в `organization
↔ constructionsite`, а через промежуточную сущность **`projectmanagement`**
(связка «Проект ↔ Организация ↔ Роль»), потому что одна организация может
быть в разных ролях.

Раньше код делал прямой `LinkOrganizationToSite` (через
`/manytomany/organization/link`) — это не выставляло роль и могло приводить
к дубликатам в «Участниках Объекта» без атрибута роли. Теперь — корректный
flow через `projectmanagement`.

---

## 🔄 Поток (5 шагов)

```
ПИН застройщика в строке файла  ──►  ① POST /api/visary/listview/organization
                                       Filter ["ClientID","=","PIN123"]
                                       → orgId
                                              ▼
                                     ② POST /api/visary/listview/constructionsite
                                                 /manytomany/projectmanagement
                                                 ?associationId={siteId}
                                       → список PM сайта
                                              ▼
                            ┌──── PM с (Organization=orgId, Role=Застройщик)
                            │     уже привязан к этому САЙТУ?
                            │       └─ да: пропуск ──► к следующей строке
                            │
                            └─── нет:
                                     ③ POST /api/visary/listview/projectmanagement
                                                 /onetomany/Project?associationId={projectId}
                                       Filter ["Organization","contains","ID:{orgId}"]
                                              and ["Role","contains","ID:10"]
                                       → список PM в проекте
                                              ▼
                            ┌──── PM с (Organization=orgId, Role=Застройщик)
                            │     есть В ПРОЕКТЕ (на другом объекте)?
                            │     └─ да: берём PM с max(ID) ──► переходим к ⑤
                            │
                            └─── нет:
                                     ④ POST /api/visary/crud/projectmanagement
                                       { Project: {ID: projectId},
                                         Organization: {ID: orgId},
                                         Role: {ID: 10, Title: "Застройщик"},
                                         Affiliation: 0 }
                                       → newPmId
                                              ▼
                                     ⑤ POST /api/visary/listview/constructionsite
                                                 /manytomany/projectmanagement/link
                                                 ?associationId={siteId}&ids={pmId}
```

Кэшируется per-session: список PM сайта (шаг ②) грузится **один раз**; поиск в
проекте (шаг ③) делается **только когда** на сайте нет нужной записи и для
конкретного `orgId` ещё не пробовали (т.к. cache по `orgId` пополняется в ⑤).

---

## ✅ Правильная реализация

### Visary.Api.Client — новые методы в общей библиотеке

```csharp
// IListViewClient
Task<ListViewResponse<ProjectManagementRaw>> GetProjectManagementsBySiteAsync(
    int siteId, CancellationToken ct = default);

Task<ListViewResponse<ProjectManagementRaw>> GetProjectManagementsByProjectAsync(
    int projectId, int? organizationId = null, int? roleId = null,
    CancellationToken ct = default);  // 👈 фильтр через ["Organization","contains","ID:{id}"]

// ICrudClient
Task<ProjectManagementRaw> CreateProjectManagementAsync(
    ProjectManagementCreateRequest request, CancellationToken ct = default);

Task<bool> LinkProjectManagementToSiteAsync(
    int siteId, int projectManagementId, CancellationToken ct = default);
```

**Helper для фильтра ссылочного поля через `contains`:**

```csharp
// ListViewClient.cs — приватный helper
private static string FilterByRefIdContains(string field, int id)
    => JsonSerializer.Serialize(new object[] { field, "contains", $"ID:{id}" });
// → ["Organization","contains","ID:4500"]
```

Visary для ссылочных полей (`VisaryRef`) не работает с `"="` — нужно `contains`
по подстроке `"ID:{id}"`. Это подсмотрено в реальном UI-запросе и проверено на
стенде.

```csharp
// VisaryMnemonics.cs — добавлена константа
public const string ProjectManagement = "projectmanagement";
```

```csharp
// Dto/ProjectManagementDtos.cs — DTO + захардкоженный role-id
public static class ProjectManagementRoles
{
    public const int Developer = 10;          // 👈 ID роли «Застройщик» в Visary
    public const string DeveloperTitle = "Застройщик";
}
```

### Интеграция в импорт «Помещения»

```csharp
// RoomsFormImportMapper.ApplyAsync — блок 1 (Organization-застройщик)
var devPin = GetStringOrNull(v, "DeveloperPin");
if (!string.IsNullOrWhiteSpace(devPin))
{
    // (1) PIN → orgId (с кэшем per-session)
    if (!orgCache.TryGetValue(devPin, out var orgId))
    {
        var orgs = await _listView.GetOrganizationsByClientIdAsync(devPin, ct);
        orgId = orgs.Data.FirstOrDefault()?.ID;
        orgCache[devPin] = orgId;
    }

    if (orgId is not null)
    {
        // (2) Один раз за сессию — прочитать PM-список сайта.
        if (!pmListLoaded)
        {
            var pmList = await _listView.GetProjectManagementsBySiteAsync(siteId, ct);
            foreach (var pm in pmList.Data)
                if (pm.Organization?.ID is int existing
                    && pm.Role?.ID == ProjectManagementRoles.Developer)
                    developerPmByOrg[existing] = pm.ID;
            pmListLoaded = true;
        }

        // (3+4+5) Reuse-then-create flow.
        if (!developerPmByOrg.ContainsKey(orgId.Value))
        {
            projectId ??= (await _crud.GetSiteByIdFullAsync(siteId, ct)).Project?.ID;
            if (projectId is null) { /* warning, пропуск */ }
            else
            {
                // (3) Сначала ищем существующий PM в рамках всего проекта.
                var inProject = await _listView.GetProjectManagementsByProjectAsync(
                    projectId.Value, orgId.Value, ProjectManagementRoles.Developer, ct);
                var reusable = inProject.Data
                    .Where(pm => pm.Organization?.ID == orgId.Value
                                 && pm.Role?.ID == ProjectManagementRoles.Developer)
                    .OrderByDescending(pm => pm.ID)   // 👈 max ID при нескольких подходящих
                    .FirstOrDefault();

                int pmIdToLink;
                if (reusable is not null)
                {
                    pmIdToLink = reusable.ID;
                }
                else
                {
                    // (4) В проекте нет — создаём новую PM-запись.
                    var created = await _crud.CreateProjectManagementAsync(
                        new ProjectManagementCreateRequest { /* ... */ }, ct);
                    pmIdToLink = created.ID;
                }

                // (5) Линкуем найденную/созданную запись с сайтом.
                await _crud.LinkProjectManagementToSiteAsync(siteId, pmIdToLink, ct);
                developerPmByOrg[orgId.Value] = pmIdToLink;
            }
        }
    }
}
```

### ⚠️ Важно

- **PM-список сайта загружается один раз** за `ApplyAsync` (`pmListLoaded`-флаг).
  Без этого — N запросов на N строк файла на каждое чтение.
- **Поиск в проекте делается ТОЛЬКО когда** на сайте нужного PM нет,
  и только для тех `orgId`, по которым ещё не закэширован результат.
  При повторной встрече этого `orgId` в файле — берём из `developerPmByOrg`.
- **При нескольких PM в проекте** — выбираем с **максимальным `ID`** (самая
  свежая запись). После сервер-side фильтра ещё прогоняем локальный
  `.Where(pm.Organization.ID == orgId && pm.Role.ID == 10)` —
  Visary `contains` может матчить по подстроке слишком широко.
- **`projectId` резолвится лениво**: сначала из `ImportContext.VisaryProjectId`,
  если null — через `GetSiteByIdFullAsync(siteId).Project.ID`. Кэшируется тут же.
- **Не падаем на ошибках PM**: PM — это enrichment, не блокер для создания
  комнат. Логируем warning, продолжаем строку.
- **Идемпотентность**: повторный импорт не плодит PM-дубликаты — `developerPmByOrg`
  предотвращает create в текущей сессии, а `pmList` из Visary — в межсессионном
  смысле.
- **Role ID = 10** захардкожен. В будущем — динамический справочник `role`
  через listview (см. doc 64 как образец для FinishingMaterial).

---

## ❌ Типичные ошибки

```csharp
// ❌ НЕПРАВИЛЬНО: использовать LinkOrganizationToSite напрямую
await _crud.LinkOrganizationToSiteAsync(siteId, orgId, ct);
// Проблема: связь без роли. Visary видит организацию в «Участниках Объекта»,
// но не различает Застройщика / Подрядчика / Тех.заказчика — все смешаны.
```

```csharp
// ❌ НЕПРАВИЛЬНО: грузить PM-список на каждой строке
foreach (var row in rows)
{
    var pmList = await _listView.GetProjectManagementsBySiteAsync(siteId, ct);  // 2782 раза!
    // ...
}
// Правильно: один await до цикла, локальный словарь developerPmByOrg.
```

```csharp
// ❌ НЕПРАВИЛЬНО: создать PM без Project
await _crud.CreateProjectManagementAsync(new ProjectManagementCreateRequest
{
    Organization = new VisaryRef { ID = orgId },
    Role         = new VisaryRef { ID = 10 },
    // Project опущен → 422 Unprocessable Entity от Visary
});
```

```csharp
// ❌ НЕПРАВИЛЬНО: создать PM, забыть link
var pm = await _crud.CreateProjectManagementAsync(req, ct);
// Без LinkProjectManagementToSiteAsync связь существует в БД Visary,
// но не отображается на странице объекта — пользователь не увидит.
```

---

## 📍 Применение в проекте

| Компонент | Файл | Назначение |
|-----------|------|------------|
| **Mnemonic** | `Visary.Api.Client/Common/VisaryMnemonics.cs` — `ProjectManagement` | `"projectmanagement"` |
| **DTO Raw** | `Visary.Api.Client/Dto/ProjectManagementDtos.cs` — `ProjectManagementRaw` | для listview ответов |
| **CREATE request** | `Visary.Api.Client/Dto/ProjectManagementDtos.cs` — `ProjectManagementCreateRequest` | тело POST /crud |
| **Role-id константы** | `Visary.Api.Client/Dto/ProjectManagementDtos.cs` — `ProjectManagementRoles` | `Developer=10` + `DeveloperTitle` |
| **ListView columns** | `Visary.Api.Client/ListView/ListViewClient.cs` — `ProjectManagementColumns` | `[ID, Project, Role, Organization, …]` |
| **ListView method** | `Visary.Api.Client/ListView/ListViewClient.cs` — `GetProjectManagementsBySiteAsync` | manytomany чтения |
| **CRUD create** | `Visary.Api.Client/CRUD/CrudClient.cs` — `CreateProjectManagementAsync` | POST /crud/projectmanagement |
| **CRUD link** | `Visary.Api.Client/CRUD/CrudClient.cs` — `LinkProjectManagementToSiteAsync` | manytomany link |
| **Импорт rooms** | `KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs` — `ApplyAsync` блок 1 | per-row резолв ПИНа + PM-flow |

---

## 🎯 Чек-лист «добавить новую роль PM (например, Технический заказчик)»

- [ ] Добавить константу в `ProjectManagementRoles` (например, `TechCustomer = 11`)
- [ ] Если в маппере появляется новая колонка с ПИНом тех. заказчика — добавить
      alias в `*Aliases` (рядом с `DeveloperPinAliases`)
- [ ] Завести отдельный кэш `techCustomerPmByOrg` или сделать общий
      `pmByOrgByRole: Dictionary<(int orgId, int roleId), int pmId>`
- [ ] Логика загрузки PM-списка остаётся той же — фильтровать по нужному
      `pm.Role?.ID` при заполнении словаря
- [ ] Тесты: новый case в `RoomsFormImportMapperTests`

## 🎯 Чек-лист «как переиспользовать методы projectmanagement в другом импорте»

- [x] `IListViewClient.GetProjectManagementsBySiteAsync` — DI-injected
- [x] `ICrudClient.CreateProjectManagementAsync` + `LinkProjectManagementToSiteAsync` — DI-injected
- [x] Mnemonic + DTO — в общей библиотеке `Visary.Api.Client`
- [ ] При использовании из нового импорта — повторить паттерн с per-session кэшем
      (`developerPmByOrg` + `pmListLoaded`-флаг) или вынести в helper-сервис
      `IProjectManagementResolver`, если flow повторится в 3+ местах
