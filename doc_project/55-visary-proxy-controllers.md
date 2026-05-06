# 🔌 Visary Proxy-контроллеры: registry-pattern для масштабирования

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-06
**Тесты**: 124 unit + 38 live = 162

Бэкенд проксирует Visary наружу под URL `/api/visary/*` со слитными именами (как мнемоники Visary).
Это нужно фронту, чтобы получать справочники и сущности **через наш API**, а не идти в Visary напрямую
(глобальный токен, единый origin, контроль логов).

Архитектура — **два контроллера разной природы**:

- **`VisaryDictionariesController`** — registry-pattern. **8 справочников через 1 контроллер**.
  Добавление нового справочника = одна строка в `Program.cs`, контроллер не трогаем.
- **`VisaryEntitiesController`** — явные actions для **11 основных сущностей**.
  У них разные query-параметры (`projectId`/`siteId`/`clientId`/`dealId`), универсализация бесполезна.

---

## ✅ Правильная реализация: справочники (registry)

### Регистрация в `Program.cs` — 1 строка на справочник

```csharp
builder.Services
    .AddVisaryDictionary<TownRaw>("towns",
        (lv, q, ct) => lv.ListTownsAsync(q, ct),
        (cr, id, ct) => cr.GetTownByIdAsync(id, ct))
    .AddVisaryDictionary<RegionRaw>("regions",
        (lv, q, ct) => lv.ListRegionsAsync(q, ct),
        (cr, id, ct) => cr.GetRegionByIdAsync(id, ct))
    .AddVisaryDictionary<ProjectTypeRaw>("projecttypes",
        (lv, _, ct) => lv.ListProjectTypesAsync(ct),
        (cr, id, ct) => cr.GetProjectTypeByIdAsync(id, ct));
// ... остальные 5 справочников аналогично
```

### Контроллер — общий, не редактируется

```csharp
[ApiController]
[Route("api/visary")]
public sealed class VisaryDictionariesController : ControllerBase
{
    private readonly VisaryDictionaryRegistry _registry;

    [HttpGet("{name}")]
    public async Task<IActionResult> List(string name, [FromQuery] string? titleFilter, CancellationToken ct)
    {
        if (!_registry.TryGet(name, out var handler))
            return NotFound(new { error = $"Справочник '{name}' не зарегистрирован.",
                                  available = _registry.RegisteredNames });
        return Ok(await handler.ListAsync(titleFilter, ct));
    }

    [HttpGet("{name}/{id:int}")]
    public async Task<IActionResult> GetById(string name, int id, CancellationToken ct)
    {
        if (!_registry.TryGet(name, out var handler))
            return NotFound(...);
        return Ok(await handler.GetByIdAsync(id, ct));
    }
}
```

### Реестр через DI — собирает все регистрации в один словарь

```csharp
public sealed class VisaryDictionaryRegistry
{
    private readonly Dictionary<string, IVisaryDictionaryHandler> _handlers;

    // DI инжектит ВСЕ зарегистрированные IVisaryDictionaryRegistration → собираем словарь
    public VisaryDictionaryRegistry(IEnumerable<IVisaryDictionaryRegistration> registrations)
    {
        _handlers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var r in registrations) _handlers[r.UrlName] = r.Handler;
    }

    public bool TryGet(string urlName, out IVisaryDictionaryHandler handler) =>
        _handlers.TryGetValue(urlName, out handler!);
}
```

### ⚠️ Важно

- **Случай-нечувствительный lookup** — `StringComparer.OrdinalIgnoreCase`.
- **Handler использует `IServiceScopeFactory`** для получения scoped HTTP-клиентов
  (singleton-handler не должен держать scoped-зависимости — в DI это сразу assertion).
- **404 с человеко-читаемым ответом** — список доступных справочников в JSON-теле,
  чтобы фронт мог показать дев-режиме «available endpoints».
- **Конфликт маршрутов невозможен**: явные routes `VisaryEntitiesController`
  (`/api/visary/constructionprojects`) выигрывают у параметрического `/api/visary/{name}` —
  ASP.NET Core всегда предпочитает literal над parameter.

---

## ✅ Правильная реализация: основные сущности (явные actions)

```csharp
[ApiController]
[Route("api/visary")]
public sealed class VisaryEntitiesController : ControllerBase
{
    private readonly IListViewClient _lv;
    private readonly ICrudClient _cr;

    // ─── ConstructionProjects ───
    [HttpGet("constructionprojects")]
    public Task<object> ListProjects([FromQuery] string? search, [FromQuery] int pageSize = 200, CancellationToken ct = default)
        => Box(_lv.GetProjectsAsync(search, pageSize, ct));

    [HttpGet("constructionprojects/{id:int}")]
    public Task<object> GetProject(int id, CancellationToken ct)
        => Box(_cr.GetProjectByIdFullAsync(id, ct));   // 👈 *Full DTO для get-by-id

    // ─── Rooms (один из siteId/sectionId обязателен) ───
    [HttpGet("rooms")]
    public async Task<IActionResult> ListRooms(
        [FromQuery] int? siteId, [FromQuery] int? sectionId,
        [FromQuery] string? uniqueNumberFilter, CancellationToken ct)
    {
        if (siteId.HasValue)
            return Ok(await _lv.GetRoomsBySiteAsync(siteId.Value, uniqueNumberFilter, ct));
        if (sectionId.HasValue)
            return Ok(await _lv.GetRoomsBySectionAsync(sectionId.Value, uniqueNumberFilter, ct));
        return BadRequest(new { error = "Укажите siteId или sectionId" });
    }

    // Async-helper, чтобы возвращать строго-типизированный результат, не теряя
    // полиморфизм: сериализатор пишет реальный runtime-тип в JSON.
    private static async Task<object> Box<T>(Task<T> task) => (await task)!;
}
```

### ⚠️ Важно

- **`Box<T>`-helper** вместо `Task<IActionResult>` сохраняет реальный тип DTO в OpenAPI/Swagger,
  но даёт удобство `Task<object>` в сигнатуре. Если возвращать `Task<IActionResult>` — тип теряется.
- **Список** возвращает `ListViewResponse<TRaw>` (легковесный DTO с малым набором колонок).
  **Get-by-id** возвращает `*Full` DTO (полный набор полей через `/crud/{m}/{id}`).
- **`[BindRequired]`** для обязательных query-параметров (например `clientId` у organizations) —
  ASP.NET вернёт 400 c понятной ошибкой, не нужен ручной `if`.

---

## ❌ Типичная ошибка №1 — 19 контроллеров вместо registry

```csharp
// НЕПРАВИЛЬНО: один контроллер на справочник = 8 файлов с одинаковым кодом.
public sealed class TownsController : ControllerBase { /* ... */ }
public sealed class RegionsController : ControllerBase { /* ... */ }
// ... ещё 6 копий
// Добавление новой сущности — копи-паста + дополнительный класс.
```

**Правильно** — один `VisaryDictionariesController` + регистрация одной строкой:
```csharp
.AddVisaryDictionary<NewRaw>("newdict", lv => lv.ListNewAsync, cr => cr.GetNewByIdAsync)
```

## ❌ Типичная ошибка №2 — registry для основных сущностей

```csharp
// НЕПРАВИЛЬНО: пытаемся унифицировать сущности с разными query-параметрами.
public interface IEntityHandler {
    Task<object> ListAsync(string? filter, CancellationToken ct);
}
// Куда тут вписать siteId, projectId, clientId, indicatorId, dealId?
```

**Правильно** — явные actions с `[FromQuery]` параметрами, semantic-ясно для фронта.

## ❌ Типичная ошибка №3 — забытый scope для scoped-клиентов

```csharp
// НЕПРАВИЛЬНО: handler регистрируется как singleton, но IListViewClient — scoped.
public sealed class TownsHandler {
    public TownsHandler(IListViewClient lv) { _lv = lv; }  // 👈 leak HttpClient жизни
}

// ПРАВИЛЬНО: создавать scope на каждый запрос.
public sealed class VisaryDictionaryHandler<TDto> {
    public VisaryDictionaryHandler(IServiceScopeFactory scopes, ...) { ... }

    public async Task<object> ListAsync(string? filter, CancellationToken ct) {
        using var scope = _scopes.CreateScope();
        var lv = scope.ServiceProvider.GetRequiredService<IListViewClient>();
        return await _list(lv, filter, ct);
    }
}
```

---

## 📍 Применение в проекте

| Файл | Что делает |
|------|------------|
| [Controllers/VisaryDictionariesController.cs](../KiloImportService.Api/Controllers/VisaryDictionariesController.cs) | Один контроллер для 8 справочников |
| [Controllers/VisaryEntitiesController.cs](../KiloImportService.Api/Controllers/VisaryEntitiesController.cs) | Явные actions для 11 основных сущностей |
| [Visary/VisaryDictionaryRegistry.cs](../KiloImportService.Api/Visary/VisaryDictionaryRegistry.cs) | Реестр + generic handler |
| [Visary/VisaryDictionaryServiceCollectionExtensions.cs](../KiloImportService.Api/Visary/VisaryDictionaryServiceCollectionExtensions.cs) | DI-extension `AddVisaryDictionary<TDto>(...)` |
| [Program.cs](../KiloImportService.Api/Program.cs) | 8 строк регистрации справочников |

---

## 🗺️ Карта URL-эндпоинтов

### Справочники (через registry)

| URL | DTO |
|-----|-----|
| `GET /api/visary/towns?titleFilter=` | `TownRaw[]` |
| `GET /api/visary/towns/{id}` | `TownRaw` |
| `GET /api/visary/regions?titleFilter=` | `RegionRaw[]` |
| `GET /api/visary/regions/{id}` | `RegionRaw` |
| `GET /api/visary/projecttypes` | `ProjectTypeRaw[]` |
| `GET /api/visary/projecttypes/{id}` | `ProjectTypeRaw` |
| `GET /api/visary/inflationcalcmethods` | `InflationCalcMethodRaw[]` |
| `GET /api/visary/inflationcalcmethods/{id}` | `InflationCalcMethodRaw` |
| `GET /api/visary/estateclasses` | `EstateClassRaw[]` |
| `GET /api/visary/estateclasses/{id}` | `EstateClassRaw` |
| `GET /api/visary/buildingmaterials` | `BuildingMaterialRaw[]` |
| `GET /api/visary/buildingmaterials/{id}` | `BuildingMaterialRaw` |
| `GET /api/visary/finishingmaterials` | `FinishingMaterialRaw[]` |
| `GET /api/visary/finishingmaterials/{id}` | `FinishingMaterialRaw` |
| `GET /api/visary/roomkinds` | `RoomKindRaw[]` |
| `GET /api/visary/roomkinds/{id}` | `RoomKindRaw` |

### Основные сущности

| URL | Параметры | DTO |
|-----|-----------|-----|
| `GET /api/visary/constructionprojects` | `?search=&pageSize=` | `ConstructionProjectRaw[]` |
| `GET /api/visary/constructionprojects/{id}` | — | `ConstructionProjectFull` |
| `GET /api/visary/constructionsites` | `?projectId=` *(обязателен)* | `ConstructionSiteRaw[]` |
| `GET /api/visary/constructionsites/{id}` | — | `ConstructionSiteFull` |
| `GET /api/visary/constructionsections` | `?siteId=&titleFilter=` | `ConstructionSectionRaw[]` |
| `GET /api/visary/constructionsections/{id}` | — | `ConstructionSectionFull` |
| `GET /api/visary/constructionsiteindicators` | `?siteId=&titleFilter=` | `ConstructionSiteIndicatorRaw[]` |
| `GET /api/visary/constructionsiteindicators/{id}` | — | `ConstructionSiteIndicatorFull` |
| `GET /api/visary/constructionsiteindicatorvalues` | `?indicatorId=` | `ConstructionSiteIndicatorValueRaw[]` |
| `GET /api/visary/constructionsiteindicatorvalues/{id}` | — | `ConstructionSiteIndicatorValueFull` |
| `GET /api/visary/rooms` | `?siteId=` или `?sectionId=` *(один обязателен)* | `RoomRaw[]` |
| `GET /api/visary/rooms/{id}` | — | `RoomFull` |
| `GET /api/visary/cadastralareas` | `?cadastralNumFilter=` | `CadastralAreaFull[]` |
| `GET /api/visary/cadastralareas/{id}` | — | `CadastralAreaFull` |
| `GET /api/visary/percentbets` | `?lmIdFilter=&dealId=` | `PercentBetRaw[]` |
| `GET /api/visary/percentbets/{id}` | — | `PercentBetFull` |
| `GET /api/visary/shareagreements` | `?roomId=` *(обязателен)* | `ShareAgreementRaw[]` |
| `GET /api/visary/shareagreements/{id}` | — | `ShareAgreementFull` |
| `GET /api/visary/deals` | `?projectId=&lmIdFilter=` | `DealRaw[]` |
| `GET /api/visary/deals/{id}` | — | `DealFull` |
| `GET /api/visary/organizations` | `?clientId=` *(обязателен)* | `OrganizationRaw[]` |
| `GET /api/visary/organizations/{id}` | — | `OrganizationFull` |

---

## 🎯 Чек-лист добавления новой сущности

### Если это справочник (typed «название → ID»)

- [ ] Снять snapshot формы ответа: `pwsh scripts/audit-visary-api.ps1 -Mnemonics newdict`
- [ ] Перегенерировать DTO: `pwsh scripts/generate-visary-dtos.ps1` (создаст `NewdictRaw.cs`)
- [ ] Добавить `VisaryMnemonics.Newdict = "newdict";`
- [ ] Добавить `IListViewClient.ListNewdictsAsync(...)` и `ICrudClient.GetNewdictByIdAsync(...)`
- [ ] Зарегистрировать в `Program.cs`:
  ```csharp
  .AddVisaryDictionary<NewdictRaw>("newdicts",
      (lv, q, ct) => lv.ListNewdictsAsync(q, ct),
      (cr, id, ct) => cr.GetNewdictByIdAsync(id, ct))
  ```
- [ ] Добавить заглушку в `FakeListViewClient` (тесты)
- [ ] Прогнать live-тесты

### Если это основная сущность (с context-параметрами)

- [ ] То же из шагов 1–4 выше
- [ ] **Добавить пару actions** в `VisaryEntitiesController` с явными `[FromQuery]` параметрами
- [ ] Добавить тест в `VisaryEntitiesControllerTests`
- [ ] Прогнать live-тесты

См. также: [56-visary-dto-deserialization-pitfalls.md](./56-visary-dto-deserialization-pitfalls.md), [57-visary-api-testing.md](./57-visary-api-testing.md).
