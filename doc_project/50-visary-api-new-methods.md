# 🔌 Новые методы Visary API Client

## 📋 Описание

**Статус**: ✅ Реализовано  
**Дата**: 2026-05-06  
**Тесты**: 69/69 backend (все проходят)

Расширение библиотеки `Visary.Api.Client` новыми методами для работы с 9 сущностями Visary.  
Используется принцип **общего базового класса** `VisaryHttpBase<T>` — вся HTTP-обвязка (аутентификация, обработка ошибок, логирование) в одном месте.

---

## 🏗️ Архитектура

```
Visary.Api.Client/
├── Common/
│   └── VisaryHttpBase<T>.cs      ← NEW: базовый класс с HTTP-хелперами
├── Dto/
│   ├── VisaryDtos.cs             ← VisaryRef + существующие DTO
│   ├── VisaryEntities.cs         ← NEW: read-DTO для 9 новых сущностей
│   └── VisaryCrudRequests.cs     ← NEW: request-DTO для CRUD
├── ListView/
│   └── ListViewClient.cs         ← +11 новых методов поиска
└── CRUD/
    └── CrudClient.cs             ← +12 новых методов создания/обновления
```

### Базовый класс `VisaryHttpBase<T>`

Наследуется обоими клиентами (`ListViewClient`, `CrudClient`). Предоставляет:

| Метод | Описание |
|-------|----------|
| `NewRequest(method, url)` | Создаёт `HttpRequestMessage` с Bearer-заголовком и проверкой конфига |
| `HandleAuthError(response, ct)` | Бросает `VisaryAuthException` при 401/403 |
| `HandleConflict(response, ct, ctx)` | Бросает при 409 Conflict (устаревший RowVersion) |
| `HandleError(response, ct)` | Бросает `HttpRequestException` при любой другой ошибке |
| `EnsureConfig()` | Проверяет `BaseUrl` и `BearerToken` |

### Принцип Default Interface Methods (DIM)

Новые методы в `IListViewClient` и `ICrudClient` объявлены с реализацией по умолчанию:
```csharp
Task<ListViewResponse<RoomRaw>> GetRoomsBySiteAsync(...)
    => throw new NotImplementedException(...);
```
**Это позволяет**: существующие fake-реализации в тестах (`FakeListViewClient`, `Mock<ICrudClient>`) **компилируются без изменений** — им не нужно реализовывать новые методы.

---

## 📖 Методы IListViewClient (поиск/чтение)

### Проекты

#### `GetProjectByIdAsync(int projectId)`

**Назначение**: получить полные данные одного проекта по его ID.

| | |
|---|---|
| **Вход** | `projectId` — ID проекта в Visary |
| **Выход** | `ListViewResponse<ConstructionProjectRaw>` — список с 0 или 1 элементом |
| **Endpoint** | `POST /api/visary/listview/constructionproject` |
| **Фильтр** | `["ID","=",<projectId>]` |

---

### ТЭПы (Технико-Экономические Показатели)

#### `GetIndicatorsBySiteAsync(int siteId, string? titleFilter = null)`

**Назначение**: получить список показателей (ТЭПов) для объекта строительства.

| | |
|---|---|
| **Вход** | `siteId` — ID объекта строительства; `titleFilter` — опциональная фильтрация по названию |
| **Выход** | `ListViewResponse<ConstructionSiteIndicatorRaw>` |
| **Endpoint** | `POST /api/visary/listview/constructionsiteindicator/onetomany/ConstructionSite?associationId={siteId}` |
| **Пример** | `titleFilter = "Площадь стоянки"` |

**Поля ответа**: `ID`, `Title`, `ConstructionSite`, `GoalValue`, `GoalDate`, `Indicator`, `Group`, `Project`, `Comment`, `SortOrder`, `MainValue`, `LastPlanValue`, `LastForecastValue`, `LastValue`, `Version`

---

#### `GetIndicatorValuesByIndicatorAsync(int indicatorId)`

**Назначение**: получить все значения конкретного показателя (по стадиям).

| | |
|---|---|
| **Вход** | `indicatorId` — ID показателя (`ConstructionSiteIndicator.ID`) |
| **Выход** | `ListViewResponse<ConstructionSiteIndicatorValueRaw>` |
| **Endpoint** | `POST /api/visary/listview/constructionsiteindicatorvalue/onetomany/ConstructionSiteIndicator?associationId={indicatorId}` |

**Поля ответа**: `ID`, `Date`, `Value`, `PlanValue`, `ForecastValue`, `Stage`, `IsUnlimited`, `IndicatorGroup`, `TextValue`, `Site`, `SortOrder`, `Version`

> ℹ️ Типичный flow: `GetIndicatorsBySiteAsync` → найти нужный ТЭП → `GetIndicatorValuesByIndicatorAsync` → найти нужную стадию → `PatchIndicatorValueAsync`

---

### Сделки

#### `GetDealsByProjectAsync(int projectId, string? lmIdFilter = null)`

**Назначение**: найти сделки внутри проекта (onetomany).

| | |
|---|---|
| **Вход** | `projectId` — ID проекта; `lmIdFilter` — опциональная фильтрация по `LmID` |
| **Выход** | `ListViewResponse<DealRaw>` |
| **Endpoint** | `POST /api/visary/listview/deal/onetomany/ConstructionProject?associationId={projectId}` |

---

#### `GetDealsAsync(string? lmIdFilter = null)`

**Назначение**: найти сделки в общем списке (без привязки к проекту).

| | |
|---|---|
| **Вход** | `lmIdFilter` — опциональная фильтрация по `LmID` |
| **Выход** | `ListViewResponse<DealRaw>` |
| **Endpoint** | `POST /api/visary/listview/deal` |

**Поля ответа**: `ID`, `Title`, `LmID`, `DocNumber`, `ConstructionProject`, `Organization`, `GroupName`, `CreditSum`, `DealStartDate`, `DealEndDate`

---

### Организации

#### `GetOrganizationsByClientIdAsync(string clientId)`

**Назначение**: найти организацию по `ClientID` (ПИН).

| | |
|---|---|
| **Вход** | `clientId` — ПИН организации |
| **Выход** | `ListViewResponse<OrganizationRaw>` |
| **Endpoint** | `POST /api/visary/listview/organization` |
| **Фильтр** | `["ClientID","=","<clientId>"]` |

---

### Помещения

#### `GetRoomsBySiteAsync(int siteId, string? uniqueNumberFilter = null)`

**Назначение**: получить помещения объекта строительства.

| | |
|---|---|
| **Вход** | `siteId` — ID объекта; `uniqueNumberFilter` — опционально, фильтр по `UniqueNumber` |
| **Выход** | `ListViewResponse<RoomRaw>` |
| **Endpoint** | `POST /api/visary/listview/room/onetomany/Site?associationId={siteId}` |

---

#### `GetRoomsBySectionAsync(int sectionId, string? uniqueNumberFilter = null)`

**Назначение**: получить помещения конкретной секции/корпуса.

| | |
|---|---|
| **Вход** | `sectionId` — ID секции; `uniqueNumberFilter` — опциональный фильтр по `UniqueNumber` |
| **Выход** | `ListViewResponse<RoomRaw>` |
| **Endpoint** | `POST /api/visary/listview/room/onetomany/Section?associationId={sectionId}` |

**Поля ответа**: `ID`, `Title`, `Site`, `Section`, `Number`, `Floor`, `Kind`, `RoomsNumber`, `TotalArea`, `LivingArea`, `Cost`, `TotalAreaWithoutSummerRoom`, `SummerRoomArea`, `CostForOne`, `UniqueNumber`, `ProjectArea`, `CadastralNumber`, `CalculatedCostPerM`, `MarketCostPerM`, `ZalogCostPerM`, …

---

### Процентные ставки

#### `GetPercentBetsAsync(string? lmIdFilter = null, int? dealId = null)`

**Назначение**: найти процентные ставки с опциональной фильтрацией.

| | |
|---|---|
| **Вход** | `lmIdFilter` — фильтр по `LmID`; `dealId` — фильтр по ID сделки |
| **Выход** | `ListViewResponse<PercentBetRaw>` |
| **Endpoint** | `POST /api/visary/listview/percentbet` |
| **Фильтр** | Один или оба параметра: `AND`-комбинация `["LmID","=","..."]` и `["Deal","=","ID:<n>"]` |

**Поля ответа**: `ID`, `LmID`, `BaseRateType`, `PercentKind`, `Deal`, `Rate`, `CommissionSum`, `Currency`, `StandardRate`, `SpecialRate`, `StartDate`, `EndDate`, `PaymentCurrency`, `BasePart`, `FloatRateMin`, `FloatRateMax`, `Advance`, `DateCreate`, `ModifiedAt`

---

### Секции/Корпуса

#### `GetSectionsBySiteAsync(int siteId, string? titleFilter = null)`

**Назначение**: получить секции/корпуса объекта строительства.

| | |
|---|---|
| **Вход** | `siteId` — ID объекта; `titleFilter` — опциональный фильтр по названию |
| **Выход** | `ListViewResponse<ConstructionSectionRaw>` |
| **Endpoint** | `POST /api/visary/listview/constructionsection/onetomany/ConstructionSite?associationId={siteId}` |

---

### ДДУ (Договоры Долевого Участия)

#### `GetShareAgreementsByRoomAsync(int roomId, string? numberFilter = null)`

**Назначение**: получить ДДУ для конкретного помещения.

| | |
|---|---|
| **Вход** | `roomId` — ID помещения; `numberFilter` — опциональный фильтр по номеру договора |
| **Выход** | `ListViewResponse<ShareAgreementRaw>` |
| **Endpoint** | `POST /api/visary/listview/shareagreement/onetomany/Room?associationId={roomId}` |

---

## ✏️ Методы ICrudClient (создание/обновление)

### Объект строительства (ConstructionSite)

#### `PatchSiteAsync(int siteId, SitePatchRequest request)`

**Назначение**: обновить поля объекта строительства.

| | |
|---|---|
| **Вход** | `siteId` — ID объекта; `request.RowVersion` — **обязателен** для защиты от конфликтов |
| **Выход** | `bool` — `true` при успехе |
| **Endpoint** | `PATCH /api/visary/crud/constructionsite/{siteId}?forceUpdate=false` |

```csharp
var request = new SitePatchRequest
{
    RowVersion = 4630021,
    Type = new VisaryRef { ID = 3, Title = "Парковка" },
    FinishingMaterial = new VisaryRef { ID = 3, Title = "Черновая" },
};
await crudClient.PatchSiteAsync(7849, request, ct);
```

> ⚠️ **409 Conflict** — `RowVersion` устарел. Получите актуальную версию через `GetSitesByProjectAsync` и повторите.

---

#### `CreateSiteAsync(SiteCreateRequest request)`

**Назначение**: создать новый объект строительства.

| | |
|---|---|
| **Вход** | `request.ProjectID` + `request.Project` — ID проекта (обязателен) |
| **Выход** | `ConstructionSiteRaw` — созданный объект с присвоенным `ID` |
| **Endpoint** | `POST /api/visary/crud/constructionsite` |

```csharp
var request = new SiteCreateRequest
{
    ProjectID = 4584,
    Project = new VisaryRef { ID = 4584 },
    ConstructionProjectNumber = "нпс",
    ConstructionPermissionNumber = "рнс",
    Type = new VisaryRef { ID = 3, Title = "Парковка" },
    FinishingMaterial = new VisaryRef { ID = 1, Title = "Чистовая" },
};
var created = await crudClient.CreateSiteAsync(request, ct);
// created.ID — ID нового объекта
```

---

### Проект (ConstructionProject)

#### `PatchProjectAsync(int projectId, ProjectPatchRequest request)`

| | |
|---|---|
| **Вход** | `projectId`; `request.RowVersion` — **обязателен** |
| **Выход** | `bool` |
| **Endpoint** | `PATCH /api/visary/crud/constructionproject/{projectId}?forceUpdate=false` |

---

#### `CreateProjectAsync(ProjectCreateRequest request)`

| | |
|---|---|
| **Вход** | `request.Title`, `request.Town`, `request.Type` — обязательные поля |
| **Выход** | `ConstructionProjectRaw` — созданный проект |
| **Endpoint** | `POST /api/visary/crud/constructionproject` |

---

### ТЭП — значение показателя

#### `PatchIndicatorValueAsync(int valueId, IndicatorValuePatchRequest request)`

**Назначение**: обновить числовое значение ТЭПа на конкретной стадии.

| | |
|---|---|
| **Вход** | `valueId` — ID записи `ConstructionSiteIndicatorValue`; `request.Value` — новое значение |
| **Выход** | `bool` |
| **Endpoint** | `PATCH /api/visary/crud/constructionsiteindicatorvalue/{valueId}?forceUpdate=true` |

```csharp
await crudClient.PatchIndicatorValueAsync(823470, new IndicatorValuePatchRequest { Value = 12344 }, ct);
```

> ℹ️ `forceUpdate=true` — обновление без проверки RowVersion (намеренно для этого endpoint).

---

### ЗУ (CadastralArea)

#### `CreateCadastralAreaAsync(CadastralAreaCreateRequest request)`

| | |
|---|---|
| **Вход** | `Area`, `CadastralNum`, `LandCategory`, опционально `UseTypes` |
| **Выход** | `CadastralAreaRaw` с новым `ID` |
| **Endpoint** | `POST /api/visary/crud/cadastralarea` |

---

#### `PatchCadastralAreaAsync(int areaId, CadastralAreaPatchRequest request)`

| | |
|---|---|
| **Вход** | `areaId`; `request.RowVersion` — **обязателен** |
| **Выход** | `bool` |
| **Endpoint** | `PATCH /api/visary/crud/cadastralarea/{areaId}?forceUpdate=false` |

---

#### `LinkCadastralAreaToSiteAsync(int siteId, int areaId)`

**Назначение**: создать связь между ЗУ и объектом строительства (many-to-many).

| | |
|---|---|
| **Вход** | `siteId` — ID объекта; `areaId` — ID земельного участка |
| **Выход** | `bool` |
| **Endpoint** | `POST /api/visary/listview/constructionsite/manytomany/cadastralarea/link?associationId={siteId}&ids={areaId}` |
| **Body** | Пустое тело |

---

### Процентная ставка (PercentBet)

#### `CreatePercentBetAsync(PercentBetCreateRequest request)`

| | |
|---|---|
| **Вход** | `Deal` (ссылка на сделку), `Rate`, `StartDate`, `EndDate`, `Currency` и др. |
| **Выход** | `PercentBetRaw` с новым `ID` |
| **Endpoint** | `POST /api/visary/crud/percentbet` |

---

### Секция/Корпус (ConstructionSection)

#### `CreateSectionAsync(SectionCreateRequest request)`

| | |
|---|---|
| **Вход** | `ConstructionSiteID`, `ConstructionSite`, `Title`, `Type`, `BuildingMaterial`, `Stage` |
| **Выход** | `ConstructionSectionRaw` с новым `ID` |
| **Endpoint** | `POST /api/visary/crud/constructionsection` |

---

### Помещение (Room)

#### `CreateRoomAsync(RoomCreateRequest request)`

| | |
|---|---|
| **Вход** | `SiteID`, `Site`, `Title`, `Kind`, `Section`, `UniqueNumber` и метрики площади/стоимости |
| **Выход** | `RoomRaw` с новым `ID` |
| **Endpoint** | `POST /api/visary/crud/room` |

---

### ДДУ (ShareAgreement)

#### `CreateShareAgreementAsync(ShareAgreementCreateRequest request)`

| | |
|---|---|
| **Вход** | `RoomID`, `Room`, `Project`, `Site`, `Title`, `Number`, `RoomKindRef` |
| **Выход** | `ShareAgreementRaw` с новым `ID` |
| **Endpoint** | `POST /api/visary/crud/shareagreement` |

---

## 🔑 Общий тип VisaryRef

Используется во всех entity и request DTO как ссылка на связанную сущность:

```csharp
public sealed class VisaryRef
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public bool? Hidden { get; set; }
    public long? RowVersion { get; set; }
}
```

**Правило**: при создании/обновлении достаточно передать только `ID`. `Title` добавляйте для читаемости.

---

## 🔧 Фильтры в ListView

Фильтры сериализуются как **строки** в поле `Filter` тела запроса:

| Тип | Пример кода | Результат |
|-----|-------------|-----------|
| По строке | `FilterByString("Title", "Парковка")` | `["Title","=","Парковка"]` |
| По числу | `FilterByInt("ID", 1234)` | `["ID","=",1234]` |
| По ID ссылки | `FilterByRefId("Deal", 9)` | `["Deal","=","ID:9"]` |
| AND-комбинация | `FilterAnd(f1, f2)` | `[f1,"and",f2]` |

---

## 🎯 Чек-лист при добавлении нового импорта

- [ ] Определить нужные сущности Visary (lookup по этому документу)
- [ ] В маппере внедрить `IListViewClient` и/или `ICrudClient` через DI
- [ ] Для поиска — вызвать соответствующий `GetXxxAsync()`
- [ ] Для создания — подготовить `XxxCreateRequest` и вызвать `CreateXxxAsync()`
- [ ] Для обновления — получить `RowVersion` через поиск, затем `PatchXxxAsync()`
- [ ] Зарегистрировать маппер через `IImportMapper` в `Program.cs`
- [ ] Написать тест с `Mock<IListViewClient>` / `Mock<ICrudClient>`

---

## 📚 См. также

- `doc_project/08-visary-api-integration.md` — базовая интеграция с Visary
- `doc_project/39-visary-api-refactoring.md` — рефакторинг библиотеки
- `doc_project/23-finmodel-import.md` — пример маппера, использующего CRUD
- `doc_project/44-listview-body-contract.md` — контракт тела ListView запроса

---

**Версия**: 1.0  
**Дата**: 2026-05-06
