# 🧰 Рефакторинг Visary API в библиотеку `Visary.Api.Client`

## 📋 Описание

**Статус**: ✅ Завершено  
**Дата**: 2026-05-03  
**Совместимость**: Полная — все существующие тесты проходят (64/64 backend, frontend работает без изменений)

---

## 📦 Что создано

### Библиотека `Visary.Api.Client`

**Локация**: `Visary.Api.Client/`

**Структура**:
```
Visary.Api.Client/
├── Visary.Api.Client.csproj
├── VisaryOptions.cs                    # Конфигурация (BaseUrl, BearerToken, Timeout)
├── Exceptions/
│   └── VisaryAuthException.cs          # Исключение для 401/403
├── Dto/
│   ├── ListViewResponse<T>             # Универсальный ответ ListView (свойство Data)
│   ├── ConstructionProjectRaw          # DTO проекта
│   ├── ConstructionSiteRaw             # DTO объекта строительства
│   └── CRUD/VisaryDtos.cs              # Общие DTO (перенесено сюда)
├── ListView/
│   ├── IListViewClient.cs              #ListView API (GET только)
│   └── ListViewClient.cs               # Реализация:
│       ├── GetProjectsAsync()
│       ├── GetSitesByProjectAsync()
│       └── GetSiteByIdAsync()
└── CRUD/
    ├── ICrudClient.cs                  # CRUD API (PUT/PATCH/POST)
    └── CrudClient.cs                   # Реализация:
        └── UpdateSiteFinishingMaterialAsync()
```

**Регистрация в DI**:
```csharp
// Program.cs
builder.Services
    .AddVisaryClient(opt =>
    {
        opt.BaseUrl = builder.Configuration["Visary:BaseUrl"] ?? string.Empty;
        opt.BearerToken = builder.Configuration["Visary:BearerToken"] ?? string.Empty;
        opt.RequestTimeout = TimeSpan.FromSeconds(30);
    })
    .Configure<VisaryOptions>(builder.Configuration.GetSection(VisaryOptions.SectionName));
```

---

## 🔄 Что изменено в KiloImportService.Api

### Обновлённые файлы

#### 1. `Program.cs`

**До**:
```csharp
builder.Services
    .AddOptions<VisaryApiOptions>()
    .Bind(builder.Configuration.GetSection(VisaryApiOptions.SectionName));

builder.Services.AddHttpClient<IVisaryListViewClient, VisaryListViewClient>();
builder.Services.AddScoped<IProjectsCacheService, ProjectsCacheService>();
builder.Services.AddScoped<ISitesSyncService, SitesSyncService>();
builder.Services.AddScoped<IVisarySitesCrudClient, VisarySitesCrudClient>();
```

**После**:
```csharp
builder.Services
    .AddVisaryClient(opt => { /* конфигурация */ });

builder.Services.AddHttpClient<IProjectsCacheService, ProjectsCacheService>();
builder.Services.AddScoped<ISitesSyncService, SitesSyncService>();
```

#### 2. `ProjectsCacheService.cs`

**До**:
```csharp
private readonly IVisaryListViewClient _visaryClient;
private readonly VisaryApiOptions _options;
```

**После**:
```csharp
private readonly IListViewClient _visaryClient;
private readonly VisaryOptions _options;
```

#### 3. `SitesSyncService.cs`

**До**:
```csharp
private readonly HttpClient _http;
private readonly VisaryApiOptions _options;
```

**После**:
```csharp
private readonly ICrudClient _visaryClient;  // 👈 Используется CRUD API
private readonly VisaryOptions _options;
```

#### 4. `FinModelImportMapper.cs`

**До**:
```csharp
private readonly IVisarySitesCrudClient _visaryCrudClient;
```

**После**:
```csharp
private readonly ICrudClient _visaryClient;
```

#### 5. `SitesController.cs`

**До**:
```csharp
using KiloImportService.Api.Domain.Visary;  // VisaryAuthException отсюда
```

**После**:
```csharp
using Visary.Api.Exceptions;  // VisaryAuthException из библиотеки
```

---

## 📦 Удалённые файлы из KiloImportService.Api

| Файл | Причина |
|------|---------|
| `Domain/Visary/VisaryListViewClient.cs` | Перемещён в `Visary.Api.Client/ListView/ListViewClient.cs` |
| `Domain/Visary/VisarySitesCrudClient.cs` | Перемещён в `Visary.Api.Client/CRUD/CrudClient.cs` |
| `Domain/Visary/VisaryApiOptions.cs` | Перемещён в `Visary.Api.Client/VisaryOptions.cs` |
| `Domain/Visary/VisaryAuthException.cs` | Перемещён в `Visary.Api.Client/Exceptions/VisaryAuthException.cs` |
| `Domain/Visary/ListViewResponse.cs` | Перемещён в `Visary.Api.Client/ListView/ListViewResponse.cs` |
| `Data/Visary/Entities/ConstructionSiteRaw.cs` | Перемещён в `Visary.Api.Client/Dto/VisaryDtos.cs` |

---

## ✅ Тестирование

### Backend (xUnit)

**Статус**: ✅ 64/64 пройдено  
**Пропущено**: 5 тестов (ClosedXML/SkiaSharp)

```bash
cd KiloImportService.Api.Tests
dotnet test
# Результат: Пройдено! : не пройдено 0, пройдено 64, пропущено 5, всего 69
```

### Frontend

**Статус**: ✅ Работает без изменений  
Фронтенд использует те же Visary API endpoints (`/api/visary/listview/...`, `/api/visary/crud/...`), поэтому не требует правок.

---

## 🎯 Преимущества рефакторинга

### 1. Разделение ответственности

- **Visary.Api.Client** — переиспользуемая библиотека для Visary API
- **KiloImportService.Api** — специфичная логика импорта

### 2. Централизация логики

Все Visary API запросы теперь через один интерфейс:
```csharp
IVisaryClient.ListView  // GET запросы
IVisaryClient.Crud      // PUT/PATCH/POST запросы
```

### 3. Легкое расширение

Добавление новых типов (Rooms, ShareAgreements, PaymentSchedule) теперь проще —只需 добавить метод в `ICrudClient`/`IListViewClient`.

### 4. Упрощённая настройка

```csharp
// Раньше: 2 отдельных интерфейса + конфигурации
builder.Services.AddHttpClient<IVisaryListViewClient, VisaryListViewClient>();
builder.Services.AddScoped<IVisarySitesCrudClient, VisarySitesCrudClient>();

// Теперь: один вызов
builder.Services.AddVisaryClient(opt => { /* одна конфигурация */ });
```

---

## 📚 См. также

- `doc_project/38-visary-client-refactoring.md` — План рефакторинга
- `doc_project/39-visary-api-refactoring.md` — Детали реализации
- `Visary.Api.Client/ListView/IListViewClient.cs` — Интерфейс ListView API
- `Visary.Api.Client/CRUD/ICrudClient.cs` — Интерфейс CRUD API

---

**Версия документа**: 1.0  
**Дата**: 2026-05-03  
**Автор**: Kilo
