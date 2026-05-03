# 🧰 Недостающие DTO типы для Visary API

## 📋 Описание

В процессе рефакторинга были обнаружены отсутствующие DTO типы, которые требовались для компиляции существующего кода. Эти типы были восстановлены и интегрированы в библиотеку `Visary.Api.Client`.

---

## ✅ Правильная реализация

### 1. ListViewResponse<T> — Generic response для ListView API

**Файл**: `Visary.Api.Client/Dto/ListViewResponse.cs`

```csharp
using System.Collections.Generic;

namespace Visary.Api.Dto;

public sealed class ListViewResponse<T>
{
    public List<T> Data { get; set; } = new();
    public int Total { get; set; }
}
```

**Особенности**:
- ✅ Generic тип `T` для разных DTO (ConstructionProjectRaw, ConstructionSiteRaw)
- ✅ Свойство `Data` (а не `Rows`) — консистентность с Visary API
- ✅ Свойство `Total` для общего количества записей

---

### 2. ConstructionProjectRaw — DTO для проекта

**Файл**: `Visary.Api.Client/Dto/VisaryDtos.cs`

```csharp
public sealed class ConstructionProjectRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public string? IdentifierKK { get; set; }
    public string? IdentifierZPLM { get; set; }
    public bool? Hidden { get; set; }
}
```

**Использование**:
- `ProjectsCacheService.SearchAsync()` — получение проектов из Visary
- `ProjectsCacheService.SyncAllAsync()` — полная синхронизация

---

### 3. ConstructionSiteRaw — DTO для объекта строительства

**Файл**: `Visary.Api.Client/Dto/VisaryDtos.cs`

```csharp
public sealed class ConstructionSiteRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public int? ConstructionProjectId { get; set; }
    public string? ConstructionPermissionNumber { get; set; }
    public string? ConstructionProjectNumber { get; set; }
    public string? StageNumber { get; set; }
    public int? RegionId { get; set; }
    public int? TownId { get; set; }
    public string? Address { get; set; }
    public bool? Hidden { get; set; }
    public DateTime? Version { get; set; }
    public int? FinishingMaterialId { get; set; }
}
```

**Использование**:
- `SitesSyncService.SyncAsync()` — синхронизация объекта
- `CrudClient.SiteUpdateData` — обновление через CRUD API

---

### 4. VisaryApiOptions → VisaryOptions

**Файл**: `Visary.Api.Client/VisaryOptions.cs`

```csharp
using System;

namespace Visary.Api.Dto;

public sealed class VisaryOptions
{
    public const string SectionName = "Visary";

    public string BaseUrl { get; set; } = string.Empty;
    public string BearerToken { get; set; } = string.Empty;
    public int SyncPageSize { get; set; } = 200;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
```

**Особенности**:
- ✅ Константа `SectionName` для привязки конфигурации
- ✅ `SyncPageSize` — размер страницы для синхронизации
- ✅ `RequestTimeout` — таймаут одного HTTP-запроса

---

## ❌ Типичная ошибка

### ❌ Отсутствие Generic response

**НЕПРАВИЛЬНО** — использование конкретного типа в интерфейсе:

```csharp
// ❌ Блокирует расширение на другие типы
public interface IVisaryListViewClient
{
    Task<ListViewResponse<ProjectRaw>> FetchProjectsAsync(...);
    // Нельзя добавить GetSitesAsync -> ListViewResponse<SiteRaw>
}
```

**ПРАВИЛЬНО** — generic тип:

```csharp
// ✅ Поддерживает любые DTO
public interface IListViewClient
{
    Task<ListViewResponse<ConstructionProjectRaw>> GetProjectsAsync(...);
    Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAsync(...);
    Task<ConstructionSiteRaw?> GetSiteByIdAsync(...);
}
```

---

### ❌ Неполный ConstructionSiteRaw

**НЕПРАВИЛЬНО** — отсутствие полей, необходимых дляUpsert:

```csharp
// ❌ Отсутствуют поля для сохранения в VisaryDbContext
public sealed class ConstructionSiteRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    // Пропущены: ConstructionProjectId, StageNumber, Address, Hidden, Version, FinishingMaterialId
}
```

**ПРАВИЛЬНО** — полный набор полей:

```csharp
// ✅ Все поля из Visary API
public sealed class ConstructionSiteRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public int? ConstructionProjectId { get; set; }
    public string? ConstructionPermissionNumber { get; set; }
    public string? ConstructionProjectNumber { get; set; }
    public string? StageNumber { get; set; }
    public int? RegionId { get; set; }
    public int? TownId { get; set; }
    public string? Address { get; set; }
    public bool? Hidden { get; set; }
    public DateTime? Version { get; set; }
    public int? FinishingMaterialId { get; set; }
}
```

---

## 📦 Интеграция

### Добавление в Visary.Api.Client

**Структура проекта**:
```
Visary.Api.Client/
├── Visary.Api.Client.csproj
├── Dto/
│   ├── ListViewResponse.cs         # Generic response
│   └── VisaryDtos.cs               # DTO для проектов и объектов
└── ...
```

### Registration в DI

**Program.cs**:
```csharp
builder.Services
    .AddVisaryClient(opt =>
    {
        opt.BaseUrl = builder.Configuration["Visary:BaseUrl"] ?? string.Empty;
        opt.BearerToken = builder.Configuration["Visary:BearerToken"] ?? string.Empty;
        opt.RequestTimeout = TimeSpan.FromSeconds(30);
    });
```

---

## 🎯 Чек-лист

При добавлении новых DTO типов для Visary API:

- [ ] Создан `Dto/` папка в библиотеке `Visary.Api.Client`
- [ ] Generic `ListViewResponse<T>` используется во всех методах
- [ ] `ConstructionProjectRaw` содержит: `ID`, `Title`, `IdentifierKK`, `IdentifierZPLM`, `Hidden`
- [ ] `ConstructionSiteRaw` содержит: все поля из `ConstructionSite` в `VisaryDbContext`
- [ ] `VisaryOptions` содержит: `BaseUrl`, `BearerToken`, `SyncPageSize`, `RequestTimeout`
- [ ] Типы используют `nullable enabled` для optional полей
- [ ] Тесты компилируются без ошибок

---

## 📚 См. также

- `doc_project/40-visary-api-refactoring-completed.md` — Рефакторинг в библиотеку
- `Visary.Api.Client/Dto/VisaryDtos.cs` — Реализация DTO
- `Data/Visary/VisaryDbContext.cs` — Маппинг DTO в EF Core сущности

---

**Версия документа**: 1.0  
**Дата**: 2026-05-03  
**Автор**: Kilo
