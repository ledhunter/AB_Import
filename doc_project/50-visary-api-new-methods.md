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

**Назначение**: найти организацию по `ClientID` (ПИН/ИНН).

| | |
|---|---|
| **Вход** | `clientId` — ПИН или ИНН организации |
| **Выход** | `ListViewResponse<OrganizationRaw>` |
| **Endpoint** | `POST /api/visary/listview/organization` |
| **Фильтр** | `["ClientID","=","<clientId>"]` |

> ℹ️ Используется в импорте «Помещения» (по ПИНу застройщика из строки файла) **и**
> в импорте «Финмодель» (по ИНН из раздела «Основные данные»). См. doc [99](./99-finmodel-organization-link.md).
> Visary `=` иногда матчит подстрокой при пробелах → дополнительный локальный фильтр
> `Trim()+OrdinalIgnoreCase` на стороне вызывающего.

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
| **Вход** | `ConstructionSiteID`, `ConstructionSite`, `Title`, `Type` (обязательно), `BuildingMaterial`, `Stage` (опционально) |
| **Выход** | `ConstructionSectionRaw` с новым `ID` |
| **Endpoint** | `POST /api/visary/crud/constructionsection` |

> ⚠️ **`Type` обязателен.** Без него Visary возвращает `422 Unprocessable Entity`.
> `BuildingMaterial` и `Stage` — необязательные (минимально валидное тело — `Type` + `Title` + связи).
> Дефолт типа корпуса для импорта `rooms`: `{"ID":3,"Title":"МЖД"}`. Парковочный
> вариант (`Паркинг`) — будущая доработка через динамический справочник.

```csharp
// ✅ ПРАВИЛЬНО: минимально достаточное тело для CreateSectionAsync
var request = new SectionCreateRequest
{
    ConstructionSiteID = 7850,
    ConstructionSite   = new VisaryRef { ID = 7850 },
    Title              = "1.1",
    Type               = new VisaryRef { ID = 3, Title = "МЖД" }, // обязательно!
};
var section = await crudClient.CreateSectionAsync(request, ct);
```

```csharp
// ❌ НЕПРАВИЛЬНО: без Type — 422 Unprocessable Entity
var request = new SectionCreateRequest
{
    ConstructionSiteID = 7850,
    ConstructionSite   = new VisaryRef { ID = 7850 },
    Title              = "1.1",
    // Type отсутствует — Visary не примет
};
```

---

### Помещение (Room)

#### `CreateRoomAsync(RoomCreateRequest request)`

| | |
|---|---|
| **Вход** | `SiteID`, `Site`, `Title`, `Kind`, `Section`, `ExplicationNumber`, **`UniqueNumber`**, метрики площади/стоимости |
| **Выход** | `RoomRaw` с новым `ID` |
| **Endpoint** | `POST /api/visary/crud/room` |

> ⚠️ **`UniqueNumber` обязателен.** В импорте `rooms` это та же колонка, что
> идёт в `Title`/`ExplicationNumber` — «Номер помещения/Квартира/Номер квартиры».
> Без `UniqueNumber` Visary считает помещение неуникальным внутри Site.

#### `PatchRoomAsync(int roomId, RoomPatchRequest request)`

| | |
|---|---|
| **Вход** | `roomId` (URL); поля `Kind`, `Section`, `Floor`, `BuildingSection`, `RoomsNumber`, `ProjectArea`, `CostForOne`, `MarketCostPerM`, `ZalogCostPerM` |
| **Выход** | `bool` |
| **Endpoint** | `PATCH /api/visary/crud/room/{roomId}?forceUpdate=true` |

> ⚠️ **При `forceUpdate=true` НЕ передавайте `ID`/`RowVersion` в теле запроса.**
> Иначе Visary падает с **500 Internal Server Error**:
> ```
> "Can not add property RowVersion to Newtonsoft.Json.Linq.JObject.
>  Property with the same name already exists on object."
> ```
> На стороне сервера эти поля наполняются из текущего состояния записи.
>
> В DTO `RoomPatchRequest.ID` / `RowVersion` имеют тип `int?` / `long?` —
> `JsonIgnoreCondition.WhenWritingNull` исключает их из JSON. Метод `PatchRoomAsync`
> явно зануляет оба поля перед сериализацией.

```csharp
// ✅ ПРАВИЛЬНО: тело без ID/RowVersion (URL уже содержит roomId)
await crudClient.PatchRoomAsync(20586, new RoomPatchRequest
{
    Kind            = new VisaryRef { ID = 1 },
    Section         = new VisaryRef { ID = 617 },
    Floor           = "1",
    ProjectArea     = 35.67,
    MarketCostPerM  = 1000001,
}, ct);
// JSON в логах: {"Kind":{"ID":1},"Section":{"ID":617},"Floor":"1",...}
// (без ID, без RowVersion)
```

```csharp
// ❌ НЕПРАВИЛЬНО: ID/RowVersion в теле + forceUpdate=true → 500
// (например если DTO имеет int ID = 0; long RowVersion = 0)
// JSON: {"ID":20586,"RowVersion":0,"Kind":...}
// Visary возвращает: "Can not add property RowVersion to JObject..."
```

---

### ДДУ (ShareAgreement)

#### `CreateShareAgreementAsync(ShareAgreementCreateRequest request)`

| | |
|---|---|
| **Вход** | `RoomID`, `Room`, `Project`, `Site`, `Title`, `Number`, **`RoomKindRef`**, `ProjectNumber`, `ConditionalNumber` |
| **Выход** | `ShareAgreementRaw` с новым `ID` |
| **Endpoint** | `POST /api/visary/crud/shareagreement` |

> ⚠️ Минимально полное тело включает `Project` (из контекста импорта),
> `RoomKindRef` (тот же `Kind`, что и у Room), `ProjectNumber` (НПС из строки)
> и `ConditionalNumber` (= номер помещения = `Room.UniqueNumber`).

```csharp
// ✅ ПРАВИЛЬНО: полный набор полей, проверенный в roomsForm-импорте
await crudClient.CreateShareAgreementAsync(new ShareAgreementCreateRequest
{
    RoomID            = 20585,
    Room              = new VisaryRef { ID = 20585 },
    Project           = new VisaryRef { ID = 4584 },         // из ImportContext.VisaryProjectId
    Site              = new VisaryRef { ID = 7850 },         // из ImportContext.VisarySiteId
    RoomKindRef       = new VisaryRef { ID = 4 },            // совпадает с Room.Kind
    Number            = "номер ДДУ",
    Title             = "номер ДДУ",
    ProjectNumber     = "нпс",                                // из строки файла («Номер проекта»)
    ConditionalNumber = "№ првк 1 -1-1",                      // = Room.UniqueNumber
}, ct);
```

#### `PatchShareAgreementAsync(int shareAgreementId, ShareAgreementPatchRequest request)`

| | |
|---|---|
| **Вход** | `shareAgreementId` (URL); поля `Number`, `Title`, `Site`, `Project` |
| **Выход** | `bool` |
| **Endpoint** | `PATCH /api/visary/crud/shareagreement/{id}?forceUpdate=true` |

> ⚠️ Те же грабли с `forceUpdate=true`, что и у `PatchRoomAsync` — `ID`/`RowVersion`
> nullable и принудительно зануляются перед сериализацией.

---

### Организация (Organization)

#### `CreateOrganizationAsync(OrganizationCreateRequest request)`

**Назначение**: создать запись `organization` в Visary, когда поиск по `ClientID`
(ПИН/ИНН) ничего не вернул. Используется импортом «Финмодель» в составе flow
«Основные данные» (см. doc [99](./99-finmodel-organization-link.md)).

| | |
|---|---|
| **Вход** | `OrganizationCreateRequest { Title, ClientID, INN, KPP?, OGRN? }` |
| **Выход** | `OrganizationRaw` (с присвоенным `ID`) |
| **Endpoint** | `POST /api/visary/crud/organization` |

```csharp
var created = await crud.CreateOrganizationAsync(new OrganizationCreateRequest
{
    Title    = "ООО СЗ Скай",
    ClientID = "6319038948",    // ИНН — он же ключ поиска в listview
    INN      = "6319038948",    // дублируем: разные формы Visary читают разные поля
}, ct);
// → created.ID = 9442
```

> ⚠️ **Всегда передавать `ClientID`**, даже если в форме видно отдельное поле `INN`.
> `listview/organization?Filter=["ClientID","=",...]` ищет именно по `ClientID`;
> без него повторный импорт того же ИНН будет создавать дубль за дублем.

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

## ⚠️ Ловушки десериализации listview-ответов

Visary в listview-ответах возвращает некоторые поля разными типами (то скаляр,
то ссылка, то null). Для DTO этого недостаточно строгого `string?` / `VisaryRef?` —
парсер ломается с `JsonException: The JSON value could not be converted to ...`.

**Решение**: для таких полей в *Raw DTO использовать `JsonElement?`. Если бизнес-логика
не использует поле — этого достаточно. Если использует — разбор на стороне caller-а.

| DTO | Поле | Тип в DTO | Причина |
|-----|------|-----------|---------|
| `RoomRaw` | `RoomCategory` | `JsonElement?` | listview шлёт `int`, crud — `VisaryRef` |
| `RoomRaw` | `ActiveShareAgreement` | `JsonElement?` | непредсказуемая форма |
| `RoomRaw` | `CandidateShareAgreement` | `JsonElement?` | непредсказуемая форма |
| `RoomRaw` | `ActiveEscrowAccount` | `JsonElement?` | непредсказуемая форма |
| `RoomRaw` | `CandidateEscrowAccount` | `JsonElement?` | непредсказуемая форма |
| `ShareAgreementRaw` | `ValidityStatus` | `JsonElement?` | приходит и числом, и строкой |

### ❌ Типичная ошибка

```csharp
// Объявили VisaryRef? а Visary прислал число → 500 ошибка десериализации:
// "The JSON value could not be converted to Visary.Api.Dto.VisaryRef.
//  Path: $.Data[0].ActiveShareAgreement | LineNumber: 0 | BytePositionInLine: 660."
public sealed class RoomRaw
{
    public VisaryRef? ActiveShareAgreement { get; set; }   // ← ломается
}
```

### ✅ Правильно

```csharp
// JsonElement? принимает любую форму без падения парсера
public sealed class RoomRaw
{
    public JsonElement? ActiveShareAgreement { get; set; } // string / int / object / null
}
```

См. также `doc_project/56-visary-dto-deserialization-pitfalls.md`.

---

## 📜 Логирование запросов в Visary

`CrudClient.PostCrudAsync` / `PatchCrudAsync` логируют **полное тело запроса**
на уровне `Information` ДО отправки и тело ошибочного ответа на уровне `Error`.
Это критично при отладке 4xx/5xx — без него непонятно, что именно отвергнуто.

```text
[INF] Visary → POST https://.../api/visary/crud/constructionsection 
       body={"ConstructionSiteID":7850,"ConstructionSite":{"ID":7850},"Title":"1.1","Type":{"ID":3,"Title":"МЖД"}}
[ERR] Visary error 422: <тело ответа Visary>
```

> ⚠️ Тело запроса **сериализуется один раз** (`JsonSerializer.Serialize(body, JsonOptions)`),
> затем переиспользуется через `StringContent`. Использовать `JsonContent.Create(body)`
> напрямую без предварительной сериализации — нельзя, тогда логи увидят только URL,
> но не тело.

### `forceUpdate` — две разные стратегии

| Endpoint | `forceUpdate` | Что в теле |
|----------|---------------|------------|
| `PATCH /constructionsite/{id}` | `=false` | `ID` + актуальный `RowVersion` обязательны |
| `PATCH /constructionproject/{id}` | `=false` | `ID` + `RowVersion` |
| `PATCH /cadastralarea/{id}` | `=false` | `ID` + `RowVersion` |
| `PATCH /constructionsiteindicatorvalue/{id}` | `=true` | **только** изменяемые поля (без `ID`/`RowVersion`) |
| `PATCH /room/{id}` | `=true` | **только** изменяемые поля (без `ID`/`RowVersion`) |
| `PATCH /shareagreement/{id}` | `=true` | **только** изменяемые поля (без `ID`/`RowVersion`) |

При `forceUpdate=true` Visary сам наполняет JObject из текущего состояния записи —
повторно отправлять `ID`/`RowVersion` нельзя (500: "Can not add property … to JObject").

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
- [ ] Для обновления:
  - **`forceUpdate=false`** — получить `RowVersion` через поиск, затем `PatchXxxAsync()` с `ID`+`RowVersion`
  - **`forceUpdate=true`** — отправлять **только** изменяемые поля (без `ID`/`RowVersion`)
- [ ] Справочники (`RoomKind`, `FinishingMaterial`, …) тянуть из живого Visary API
      (`_listView.ListXxxAsync`), а не из локальной visary_db — иначе ID не совпадут
      со стендом.
- [ ] Если listview-поле в DTO ломает парсер — заменить тип на `JsonElement?`
      (см. раздел «Ловушки десериализации»).
- [ ] Зарегистрировать маппер через `IImportMapper` в `Program.cs`
- [ ] Написать тест с `Mock<IListViewClient>` / `Mock<ICrudClient>`

---

## 📚 См. также

- `doc_project/08-visary-api-integration.md` — базовая интеграция с Visary
- `doc_project/39-visary-api-refactoring.md` — рефакторинг библиотеки
- `doc_project/23-finmodel-import.md` — пример маппера, использующего CRUD
- `doc_project/44-listview-body-contract.md` — контракт тела ListView запроса
- `doc_project/56-visary-dto-deserialization-pitfalls.md` — полиморфные поля DTO

---

## 📝 История версий

- **1.3** (2026-05-20):
  - Добавлен `ICrudClient.CreateOrganizationAsync` (POST `/api/visary/crud/organization`)
    с DTO `OrganizationCreateRequest { Title, ClientID, INN, KPP?, OGRN? }`. Используется
    импортом Финмодели по разделу «Основные данные»: если по ИНН организация не
    нашлась через `GetOrganizationsByClientIdAsync`, создаём её сами, после чего
    привязываем к объекту через `projectmanagement`. Подробности —
    [99-finmodel-organization-link.md](./99-finmodel-organization-link.md).
  - Дополнен раздел про `GetOrganizationsByClientIdAsync`: теперь явно отмечен
    второй сценарий использования (Финмодель по ИНН, а не только Rooms по ПИНу).
- **1.2** (2026-05-19):
  - Добавлена сущность **`CostItem`** (мнемоника `costitem`) — строка ГФ подстатьи ИСР.
    DTO: `CostItemRaw`, `CostItemPeriod`, `CostItemCreateRequest`, `CostItemPatchRequest`,
    константа `CostItemStatus.Plan = 70`.
  - Методы клиента: `ICrudClient.CreateCostItemAsync` / `PatchCostItemAsync`
    (PATCH `forceUpdate=true` — тот же приём, что для Room/ShareAgreement/WBS),
    `IListViewClient.GetCostItemsByWbsAsync` (POST `listview/costitem/onetomany/WBS?associationId={wbsId}`).
  - Добавлен `IListViewClient.GetWbsBySiteAsync` (POST `listview/wbs/onetomany/ConstructionSite?associationId={siteId}`)
    — для поиска WBS-узлов именно у выбранного объекта (а не у всего проекта).
  - `PlanQuarter` / `PlanYear` / `PlanMonth` — derived на сервере, в POST НЕ передавать.
  - Дедупликации на сервере по `(WBSID, PlanPeriod)` нет — caller обязан pre-check'ить.
  - Подробности маппинга и сценарии использования: [91-finmodel-chapter1-schedule.md](./91-finmodel-chapter1-schedule.md).
- **1.1** (2026-05-07):
  - `CreateSectionAsync`: уточнено, что `Type` обязателен (без него 422); дефолт `МЖД (ID=3)` для импорта `rooms`.
  - `CreateRoomAsync`: явно отмечен обязательный `UniqueNumber`.
  - `CreateShareAgreementAsync`: добавлены `Project`, `RoomKindRef`, `ProjectNumber`, `ConditionalNumber` в минимальное полное тело.
  - Добавлены секции `PatchRoomAsync` / `PatchShareAgreementAsync` с описанием грабли `forceUpdate=true` (нельзя слать `ID`/`RowVersion` — 500 "Can not add property RowVersion to JObject").
  - DTO `RoomPatchRequest` / `ShareAgreementPatchRequest`: `ID`/`RowVersion` стали `int?` / `long?`, `JsonIgnoreCondition.WhenWritingNull` исключает их из JSON.
  - Добавлен раздел «Ловушки десериализации» (`Active*ShareAgreement`, `*EscrowAccount`, `ValidityStatus` → `JsonElement?`).
  - Добавлен раздел «Логирование запросов в Visary»: тело request/response пишется на уровне INFO/ERROR в `PostCrudAsync`/`PatchCrudAsync`.
  - В чек-лист добавлены пункты про справочники из живого API и про нужность ловушек десериализации.
- **1.0** (2026-05-06): первичная версия.

---

**Версия**: 1.1  
**Дата**: 2026-05-07
