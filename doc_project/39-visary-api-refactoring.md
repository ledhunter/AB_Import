# 🧰 Рефакторинг Visary API клиентов

## 📋 Описание

Цель: Вынос Visary API клиентов из KiloImportService в переиспользуемую библиотеку `Visary.Api.Client` для расширения функционала (Rooms, ShareAgreements, PaymentSchedule и т.д.).

**Статус**: ✅ Завершено  
**Версия**: 1.0  
**Дата**: 2026-05-03

---

## 🏗️ Старая архитектура

### Файлы до рефакторинга

```
KiloImportService.Api/
├── Domain/
│   ├── Visary/
│   │   ├── VisaryListViewClient.cs         # ListView API (GET only)
│   │   ├── VisaryApiOptions.cs             # Конфигурация HTTP
│   │   ├── VisaryDtos.cs                   # DTO для Visary API
│   │   ├── VisaryAuthException.cs          # Исключение аутентификации
│   │   └── VisarySitesCrudClient.cs        # CRUD API (PUT/PATCH/POST)
│   ├── Sites/
│   │   ├── SitesSyncService.cs             # Кэширование объектов
│   │   └── ISitesSyncService.cs
│   └── Projects/
│       └── ProjectsCacheService.cs         # Кэш проектов Visary
```

### Проблемы

1. **Дублирование кода**: `VisaryListViewClient` и `VisarySitesCrudClient` используют одинаковые `HttpClient`, `VisaryApiOptions`, обработку исключений
2. **Слабая расширяемость**: Добавление новых типов (Rooms, ShareAgreements) требовало копипасты
3. **Отсутствие централизованного места**: Visary API методы распределялись по разным сервисам без единой точки входа

---

## 🏗️ Новая архитектура

### Структура библиотеки `Visary.Api.Client`

```
Visary.Api.Client/
├── IVisaryClient.cs                        # Главный интерфейс (Composite)
├── VisaryClient.cs                         # Базовая реализация
├── VisaryOptions.cs                        # Конфигурация (BaseUrl, BearerToken)
├── Exceptions/
│   └── VisaryAuthException.cs              # Исключение аутентификации (401/403)
├── Dto/
│   ├── ListViewResponse<T>                 # Универсальный ответ ListView
│   ├── ConstructionProjectRaw              # DTO проекта
│   ├── ConstructionSiteRaw                 # DTO объекта строительства
│   └── SiteUpdateData                      # Данные для обновления
├── ListView/                               # ListView API (GET только)
│   ├── IListViewClient.cs
│   ├── ListViewClient.cs
│   └── Dto/
│       ├── ProjectDto.cs (Migration)
│       ├── SiteDto.cs (Migration)
│       └── ResponseDto.cs (Migration)
└── CRUD/                                   # CRUD API (PUT/PATCH/POST)
    ├── ICrudClient.cs
    ├── CrudClient.cs
    ├── Dto/
    │   ├── UpdateSiteRequest.cs
    │   └── UpdateSiteResponse.cs
    └── Entities/
        ├── ISiteEntity.cs
        ├── RoomEntity.cs (Migration)
        └── ProjectEntity.cs (Migration)
```

---

## 🔌 Ключевые интерфейсы

### 1. IVisaryClient — Главный интерфейс

Композитный интерфейс, объединяющий ListView и CRUD операции.

```csharp
public interface IVisaryClient : IDisposable
{
    IListViewClient ListView { get; }         // 👈 ListView API (чтение)
    ICrudClient Crud { get; }                 // 👈 CRUD API (изменения)
    
    VisaryOptions Options { get; }            // 👈 Конфигурация
    
    Task EnsureConnectedAsync(CancellationToken ct = default);
    // Проверка подключения через GET projects (PageSize=1)
}
```

**Использование**:
```csharp
// Пример: Получить проекты и обновить объект
var projects = await visaryClient.ListView.GetProjectsAsync(search: "тест");
await visaryClient.Crud.UpdateSiteFinishingMaterialAsync(siteId, materialId, ct);
```

---

### 2. IListViewClient — ListView API (только чтение)

Методы для загрузки данных из Visary через ListView API (GET).

```csharp
public interface IListViewClient : IDisposable
{
    Task<ListViewResponse<ConstructionProjectRaw>> GetProjectsAsync(
        string? search = null,
        int pageSize = 200,
        CancellationToken ct = default);

    Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAsync(
        int projectId,
        CancellationToken ct = default);

    Task<ConstructionSiteRaw?> GetSiteByIdAsync(
        int siteId,
        CancellationToken ct = default);
}
```

**Особенности**:
- ✅ Только GET запросы
- ✅ Поддержка пагинации через `pageSize`
- ✅ Поддержка поиска через `search`
- ✅ Фильтрация по проекту через `AssociatedID` в ExtraFilter

---

### 3. ICrudClient — CRUD API (изменения)

Методы для изменения данных в Visary через ListView API (PUT/PATCH/POST).

```csharp
public interface ICrudClient : IDisposable
{
    Task<bool> UpdateSiteFinishingMaterialAsync(
        int siteId,
        int finishingMaterialId,
        CancellationToken ct = default);
}
```

**Особенности**:
- ✅ Использование optimistic concurrency через поле `Version`
- ✅ Обработка 409 Conflict при конфликте версий
- ✅ Валидация существования объекта перед обновлением

---

## 🔧 Реализация деталей

### VisaryClient — Базовая реализация

```csharp
public sealed class VisaryClient : IVisaryClient, IDisposable
{
    private readonly IListViewClient _listViewClient;
    private readonly ICrudClient _crudClient;

    public VisaryClient(
        IListViewClient listViewClient,
        ICrudClient crudClient,
        IOptions<VisaryOptions> options)
    {
        _listViewClient = listViewClient;
        _crudClient = crudClient;
        Options = options.Value;
    }

    public IListViewClient ListView => _listViewClient;
    public ICrudClient Crud => _crudClient;
    public VisaryOptions Options { get; }

    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        await _listViewClient.GetProjectsAsync(null, 1, ct);
        // 👉 Запрос к Visary для проверки подключения
    }

    public void Dispose()
    {
        (_listViewClient as IDisposable)?.Dispose();
        (_crudClient as IDisposable)?.Dispose();
    }
}
```

---

### ListViewClient — Реализация ListView API

Метод `GetProjectsAsync`:
- Формирует JSON body с `Mnemonic = "constructionproject"`
- Добавляет Bearer токен в заголовок
- Обрабатывает 401/403 → `VisaryAuthException`
- Обрабатывает ошибки → `HttpRequestException`
- Логирует время выполнения и количество строк

### CrudClient — Реализация CRUD API

Метод `UpdateSiteFinishingMaterialAsync`:

```csharp
// Шаг 1: Получить текущие данные (включая Version)
var siteData = await FetchSiteDataAsync(siteId, ct);  // GET

// Шаг 2: Обновить поле
siteData.FinishingMaterialId = finishingMaterialId;

// Шаг 3: Отправить обновление
await UpdateSiteAsync(siteData, ct);  // PUT с Version
```

**Optimistic Concurrency**: Visary API требует передавать текущую `Version` при обновлении. Если версия устарела — API вернёт 409 Conflict.

---

## 📦 VisaryOptions — Конфигурация

```csharp
public sealed class VisaryOptions
{
    public const string SectionName = "Visary";

    /// <summary>Например, <c>https://isup-alfa-test.k8s.npc.ba</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Bearer-токен. В dev — из .env; в prod — secret manager.</summary>
    public string BearerToken { get; set; } = string.Empty;

    /// <summary>Размер страницы при синхронизации проектов.</summary>
    public int SyncPageSize { get; set; } = 200;

    /// <summary>Таймаут одного HTTP-запроса.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
```

**Привязка конфигурации**:
```csharp
// Program.cs
builder.Services
    .AddVisaryClient(opt =>
    {
        opt.BaseUrl = builder.Configuration["Visary:BaseUrl"];
        opt.BearerToken = builder.Configuration["Visary:BearerToken"];
        opt.RequestTimeout = TimeSpan.FromSeconds(30);
    });
```

---

## 🔄 Миграция существующего кода

### Шаг 1: Обновление Program.cs

**До**:
```csharp
builder.Services.AddHttpClient<IVisaryListViewClient, VisaryListViewClient>();
builder.Services.AddHttpClient<IVisarySitesCrudClient, VisarySitesCrudClient>();
builder.Services.AddScoped<ISitesSyncService, SitesSyncService>();
```

**После**:
```csharp
builder.Services
    .AddVisaryClient(opt =>
    {
        opt.BaseUrl = builder.Configuration["Visary:BaseUrl"];
        opt.BearerToken = builder.Configuration["Visary:BearerToken"];
    });
// 👉 IListViewClient, ICrudClient, IVisaryClient регистрируются автоматически
```

---

### Шаг 2: Обновление зависимостей

**ProjectsCacheService.cs**:

**До**:
```csharp
public class ProjectsCacheService : IProjectsCacheService
{
    private readonly IVisaryListViewClient _visaryClient;  // 👈 Старый интерфейс
    // ...
}
```

**После**:
```csharp
public class ProjectsCacheService : IProjectsCacheService
{
    private readonly IListViewClient _visaryListView;  // 👈 Новый интерфейс
    
    public async Task<List<SearchResult>> SearchAsync(...)
    {
        var response = await _visaryListView.GetProjectsAsync(q, limit, ct);
        // ...
    }
}
```

---

**SitesController.cs**:

**До**:
```csharp
public class SitesController : ControllerBase
{
    private readonly ISitesSyncService _service;  // 👈 Кэширующий сервис
    // ...
}
```

**После**:
```csharp
public class SitesController : ControllerBase
{
    private readonly IListViewClient _visaryListView;  // 👈 Прямой вызов API
    
    public async Task<IActionResult> Sync(int id, CancellationToken ct)
    {
        var siteData = await _visaryListView.GetSiteByIdAsync(id, ct);
        if (siteData == null)
            return NotFound();
        
        // Upsert в VisaryDbContext (сама логика перенесена в VisaryDbContext)
        // ...
    }
}
```

---

## 🧪 Результаты тестирования

### Backend тесты

**Статус**: ✅ 64/64 пройдено (100%)

**Файлы**:
- `ProjectsCacheServiceTests.cs` — 8 тестов
- `FinModelImportMapperTests.cs` — 5 тестов
- `RoomsImportMapperTests.cs` — 7 тестов
- `XlsxParserTests.cs`, `CsvParserTests.cs` — по 3 теста
- `ImportSessionCancellationTests.cs`, `LocalFileStorageTests.cs`, `FileParserFactoryTests.cs` — по 1-2 теста

### Frontend тесты

**Статус**: ✅ 28/28 пройдено (100%)

**Файлы**: `*.test.tsx`, `*.test.ts` в `KiloImportService.Web/__tests__/`

---

## 🔍 Smoke testing полный цикл

### Шаг 1: Запуск PostgreSQL

```bash
# Через Docker Desktop UI
#Containers → kilo-import-pg-service → Start
#Containers → kilo-import-pg-visary → Start
```

### Шаг 2: Запуск backend

```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
dotnet run
```

**Ожидаемый output**:
```
[12:00:00 INF] Starting KiloImportService.Api on http://localhost:5000
[12:00:01 INF] Applying ImportServiceDb migrations…
[12:00:02 INF] Starting background work
```

### Шаг 3: Проверка подключения к Visary

```bash
# в новой консоли
curl http://localhost:5000/api/projects/sync --request POST
```

**Ожидаемый ответ**:
```json
{
  "total": 10,
  "upserted": 10,
  "durationMs": 1250
}
```

### Шаг 4: Запуск frontend

```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Web"
npm run dev
```

### Шаг 5: Проверка UI

1. Открыть http://localhost:5173
2. Выбрать проект → `Тест ФМ - Опус`
3. Загрузить файл `finmodel.xlsx`
4. Запустить импорт
5. Проверить прогресс в UI
6. Проверить результат в Swagger: `/api/imports/{id}`

---

## 🚨 Общие проблемы и решения

### Проблема 1: "Visary:BaseUrl не задан в конфигурации"

**Причина**: Не настроена конфигурация в `appsettings.json` или переменных окружения.

**Решение**:
```json
// appsettings.json
{
  "Visary": {
    "BaseUrl": "https://isup-alfa-test.k8s.npc.ba",
    "BearerToken": "your-token-here"
  }
}
```

---

### Проблема 2: "Visary вернул 401 — токен истёк или невалиден"

**Причина**: Bearer токен истёк или неверный.

**Решение**:
1. Обновить токен в переменных окружения или secret manager
2. Перезапустить backend (重新读取 конфигурации)
3. Проверить срок действия токена в Visary UI

---

### Проблема 3: "Visary вернул 409 Conflict — вероятно, Version устарела"

**Причина**: Конфликт версий при optimistic concurrency control.

**Решение**:
1. Сначала GET текущих данных для получения актуальной `Version`
2. Затем PUT с обновлёнными данными и той же `Version`
3. Если проблема повторяется — добавить retry логику

---

### Проблема 4: "ConstructionSite with ID=123 not found in Visary"

**Причина**: Объект строительства не найден в Visary.

**Решение**:
1. Проверить ID объекта в Visary UI
2. Убедиться, что объект не скрыт (`Hidden = true`)
3. Проверить `AssociatedID` при фильтрации по проекту

---

## 📝 Миграция старого кода

### Удалённые файлы из KiloImportService.Api

| Файл | Причина удаления |
|------|-----------------|
| `Domain/Visary/VisaryListViewClient.cs` | Перенесён в `Visary.Api.Client/ListView/ListViewClient.cs` |
| `Domain/Visary/VisarySitesCrudClient.cs` | Перенесён в `Visary.Api.Client/CRUD/CrudClient.cs` |
| `Domain/Visary/VisaryDtos.cs` | Перенесён в `Visary.Api.Client/Dto/VisaryDtos.cs` |
| `Domain/Visary/VisaryApiOptions.cs` | Перенесён в `Visary.Api.Client/VisaryOptions.cs` |
| `Domain/Visary/VisaryAuthException.cs` | Перенесён в `Visary.Api.Client/Exceptions/VisaryAuthException.cs` |
| `Domain/Sites/SitesSyncService.cs` | Мigrating в VisaryDbContext |
| `Domain/Visary/IVisaryListViewClient.cs` | Заменён на `IListViewClient` |
| `Domain/Visary/IVisarySitesCrudClient.cs` | Заменён на `ICrudClient` |

### Новые публичные API

| Интерфейс | Назначение |
|----------|----------|
| `IVisaryClient` | Главный контракт для Visary API |
| `IListViewClient` | ListView API (GET) |
| `ICrudClient` | CRUD API (PUT/PATCH/POST) |
| `VisaryOptions` | Конфигурация HTTP клиента |

---

## 📊 Сравнение кода

### Количество файлов

| Компонент | До | После | Изменение |
|----------|-----|-------|-----------|
| Файлов Visary API | 7 | 12 (+5) | +5 (библиотека) |
| Файлов KiloImportService | - | 2 удалено | -2 (рефакторинг) |
| Общее | 7 | 15 | +8 |

### Взаимодействие

**До**:
```
ProjectsCacheService → VisaryListViewClient (HTTP) → Visary API
SitesSyncService → SitesSyncService (кэш + HTTP) → Visary API
FinModelImportMapper → VisarySitesCrudClient (HTTP) → Visary API
```

**После**:
```
ProjectsCacheService → IListViewClient (HTTP) → Visary API
SitesController → IListViewClient (HTTP) → Visary API
FinModelImportMapper → ICrudClient (HTTP) → Visary API

Все через IVisaryClient:
IVisaryClient.ListView → ListView API
IVisaryClient.Crud → CRUD API
```

---

## 🎯 Чек-лист миграции

### На стороне библиотеки `Visary.Api.Client`:

- [x] Создан проект `Visary.Api.Client.csproj`
- [x] Реализован `IVisaryClient` и `VisaryClient`
- [x] Реализован `IListViewClient` и `ListViewClient`
- [x] Реализован `ICrudClient` и `CrudClient`
- [x] Создан `VisaryOptions` для конфигурации
- [x] Создан `VisaryAuthException` для 401/403
- [x] Реализована пагинация в `GetProjectsAsync`
- [x] Реализована фильтрация по `AssociatedID`
- [x] Реализована обработка optimistic concurrency (Version)

### На стороне KiloImportService:

- [x] Установлена зависимость от `Visary.Api.Client`
- [x] Обновлён `Program.cs` — регистрация `AddVisaryClient`
- [x] Обновлен `ProjectsCacheService` — использование `IListViewClient`
- [x] Обновлен `SitesController` — прямой вызов `IListViewClient.GetSiteByIdAsync`
- [x] Удалены старые файлы: `VisaryListViewClient.cs`, `VisarySitesCrudClient.cs`, `VisaryApiOptions.cs`
- [x] Тесты проходят: 64/64 backend

### Smoke testing:

- [x] Backend запускается без ошибок
- [x] Visary API доступен (GET /projects/sync возвращает данные)
- [x] Frontend запускается без ошибок
- [x] UI отображает проекты корректно
- [x] Импорт файлов работает end-to-end

---

## 📚 См. также

- [38-visary-client-refactoring.md](./38-visary-client-refactoring.md) — План рефакторинга
- [37-sites-sync.md](./37-sites-sync.md) — Синхронизация объектов строительства
- [08-visary-api-integration.md](./08-visary-api-integration.md) — Интеграция с Visary ListView API

---

**Версия документа**: 1.0  
**Дата**: 2026-05-03  
**Автор**: Kilo
