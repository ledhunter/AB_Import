# 🧰 План рефакторингаVisary API клиентов

## 📋 Описание

Цель: Вынести Visary API клиенты (SitesSyncService, VisarySitesCrudClient) в переиспользуемую библиотеку для расширения функционала (Rooms, ShareAgreements и т.д.).

---

## 🏗️ Текущее состояние

### Файлы

```
KiloImportService.Api/
├── Domain/
│   ├── Visary/
│   │   ├── VisaryListViewClient.cs         # fetch projects ( ListView API )
│   │   ├── VisaryApiOptions.cs             # конфигурация
│   │   ├── VisaryDtos.cs                   # DTO
│   │   ├── VisaryAuthException.cs          # исключение
│   │   └── VisarySitesCrudClient.cs        # CRUD API
│   ├── Sites/
│   │   ├── SitesSyncService.cs             # кэширование объектов
│   │   └── ISitesSyncService.cs
```

### Проблемы

1. **VisaryListViewClient.cs** и **VisarySitesCrudClient.cs** — оба используют одинаковые `HttpClient`, `VisaryApiOptions`, обработку исключений
2. **SitesSyncService.cs** — специфичен только для ConstructionSite, но логика HTTP запроса повторяется
3. Нет централизованного места для Visary API методов

---

## 🎯 Требования к рефакторингу

### 1. Долгосрочные цели

- ✅ Вынести Visary API клиенты в отдельную библиотеку (например, `Visary.Api.Client`)
- ✅ Сделать интерфейс `IVisaryClient` с методами для проектов, объектов, помещений
- ✅ Общая обработка исключений (`VisaryAuthException`, `VisaryApiException`)
- ✅ Повторное использование в других проектах (не только KiloImportService)

### 2. Внутренняя структура

```
Visary.Api.Client/
├── IVisaryClient.cs                        # главный интерфейс
├── VisaryClient.cs                         # реализация
├── VisaryOptions.cs                        # конфигурация
├── Exceptions/
│   ├── VisaryAuthException.cs
│   ├── VisaryApiException.cs
│   └── VisaryNotFoundException.cs
├──ListView/                                # ListView API (GET только)
│   ├── IListViewClient.cs
│   ├── ListViewClient.cs
│   └── Dto/
│       ├── ProjectDto.cs
│       ├── SiteDto.cs
│       └── ResponseDto.cs
└── CRUD/                                   # CRUD API (PUT/PATCH/POST)
    ├── ICrudClient.cs
    ├── CrudClient.cs
    ├── Dto/
    │   ├── UpdateSiteRequest.cs
    │   └── UpdateSiteResponse.cs
    └── Entities/
        ├── ISiteEntity.cs
        ├── RoomEntity.cs
        └── ProjectEntity.cs
```

---

## 📐 Структура нового клиента

### 1. Главный интерфейс

```csharp
public interface IVisaryClient : IDisposable
{
    IListViewClient ListView { get; }
    ICrudClient Crud { get; }
    
    VisaryOptions Options { get; }
    
    Task EnsureConnectedAsync(CancellationToken ct = default);
}
```

### 2. ListView API (только чтение)

```csharp
public interface IListViewClient
{
    Task<ListViewResponse<ProjectDto>> GetProjectsAsync(
        string? search = null,
        int pageSize = 200,
        CancellationToken ct = default);

    Task<ListViewResponse<SiteDto>> GetSitesByProjectAsync(
        int projectId,
        CancellationToken ct = default);

    Task<ListViewResponse<RoomDto>> GetRoomsBySiteAsync(
        int siteId,
        CancellationToken ct = default);

    Task<SiteDto?> GetSiteByIdAsync(
        int siteId,
        CancellationToken ct = default);
}
```

### 3. CRUD API (изменения)

```csharp
public interface ICrudClient
{
    Task<bool> UpdateSiteFinishingMaterialAsync(
        int siteId,
        int finishingMaterialId,
        CancellationToken ct = default);

    Task<bool> UpdateRoomPropertiesAsync(
        int roomId,
        IDictionary<string, object> properties,
        CancellationToken ct = default);
}
```

---

## 🔄 Миграция

### Этап 1: Создание Visary.Api.Client

1. Создать новый проект `Visary.Api.Client`
2. Перенести `VisaryListViewClient`, `VisaryDtos`, `VisaryApiOptions`
3. Перенести `VisarySitesCrudClient`
4. Создать общую `IVisaryClient` и `VisaryClient`
5. Реализовать `IListViewClient` и `ICrudClient`

### Этап 2: Интеграция в KiloImportService

```csharp
// Program.cs
builder.Services.AddScoped<IVisaryClient, VisaryClient>();
builder.Services.AddScoped<IListViewClient, ListViewClient>();
builder.Services.AddScoped<ICrudClient, CrudClient>();
```

### Этап 3: Обновление зависимых кода

| Файл | Старый код | Новый код |
|------|-----------|----------|
| `ProjectsCacheService` | `IVisaryListViewClient` | `IVisaryClient.ListView` |
| `VisarySitesCrudClient` | `IVisarySitesCrudClient` | `IVisaryClient.Crud` |
| `SitesSyncService` | HTTP client + options | `IVisaryClient.ListView.GetSiteByIdAsync` |

### Этап 4: Удаление старого кода

- Удалить `VisaryListViewClient.cs` из KiloImportService.Api
- Удалить `VisarySitesCrudClient.cs`
- Удалить `SitesSyncService.cs` (мigrating в Visary.Api.Client)
- Удалить `IVisarySitesCrudClient`

---

## 📊 Оценка работы

| Этап | Описание | Срок |
|------|----------|------|
| 1 | Создание Visary.Api.Client | 1 день |
| 2 | Реализация IVisaryClient | 2 дня |
| 3 | Миграция KiloImportService | 1 день |
| 4 | Тесты и удаление старого кода | 1 день |
| **Всего** | | **5 дней** |

---

## ⚠️ Риски

1. **Обратная совместимость**: Старый код может зависеть от методов, которые изменятся
   - Решение: Оставить старые интерфейсы как Wrapper в KiloImportService
2. **Тесты**: Нужны моки HTTPclient
   - Решение: Использовать `HttpMessageHandler` mock
3. **Документация**: Нужно обновить doc_project
   - Решение: Записать migration guide

---

## 🎯 Приоритет

**Высокий** — улучшение архитектуры и подготовка к добавлению новых типов импорта (Rooms, ShareAgreements, PaymentSchedule и т.д.)

---

**Версия**: 1.0  
**Дата**: 2026-05-03  
**Автор**: Kilo
