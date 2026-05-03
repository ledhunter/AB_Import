# 🗃️ Синхронизация объектов строительства с Visary

## 📋 Описание

Документ описывает реализацию сервиса синхронизации объектов строительства (ConstructionSite) из Visary API в локальную базу данных импорта. Реализованы два компонента:

1. **SitesSyncService** — загрузка данных объекта из Visary и сохранение в `VisaryDbContext` (для кэширования и ускорения запросов)
2. **VisarySitesCrudClient** — обновление типа отделки (FinishingMaterialId) через Visary CRUD API для импорта "Финмодель"

---

## 🏗️ Архитектура

### Компонент 1: SitesSyncService

**Назначение**: Кэширование объектов строительства из Visary в локальную БД импорта.

**Мотивация**: Visary ListView API медленный для frequent queries. Кэширование ускоряет отображение списков объектов в UI.

**Флоу синхронизации**:
```
User selects object → ImportForm.tsx → syncSite(siteId)
    → SitesController.Sync(siteId)
    → SitesSyncService.SyncAsync(siteId)
    → Visary API GET ConstructionSite by ID
    → Upsert in VisaryDbContext
```

**Использование**:
- Вызывается при выборе объекта в UI (ImportForm.tsx)
- Обновляет кэш объектов перед запуском импорта

---

### Компонент 2: VisarySitesCrudClient

**Назначение**: Обновление типа отделки объекта строительства через Visary CRUD API.

**Мотивация**: Для импорта "Финмодель" нужна возможность обновлять поле `FinishingMaterialId` у объектов строительства.

**Workflow обновления**:
```
FinModelImportMapper.ApplyAsync
    → VisarySitesCrudClient.UpdateSiteFinishingMaterialAsync(siteId, materialId)
    → Visary API GET ConstructionSite (получить Version)
    → Обновить FinishingMaterialId
    → Visary API PUT ConstructionSite (с Version)
```

---

## ✅ Реализация

### 1. SitesSyncService

#### Контракт

```csharp
public interface ISitesSyncService
{
    Task<bool> SyncAsync(int siteId, CancellationToken ct);
}
```

#### Регистрация в DI

```csharp
// Program.cs
builder.Services.AddScoped<ISitesSyncService, SitesSyncService>();
```

#### Использование

```csharp
// SitesController.cs
[HttpPost("sync/{id:int}")]
public async Task<IActionResult> Sync(int id, CancellationToken ct)
{
    var result = await _service.SyncAsync(id, ct);
    return Ok(new { success = result, siteId = id });
}
```

#### Вызов из UI

```typescript
// ImportForm.tsx
onChange={async ({ selected }) => {
    const newSiteId = selected ? Number(selected.key) : null;
    if (newSiteId) {
        await syncSite(newSiteId); // вызывает /api/sites/sync/{id}
    }
    onSiteChange(newSiteId);
}}
```

---

### 2. VisarySitesCrudClient

#### Контракт

```csharp
public interface IVisarySitesCrudClient
{
    Task<bool> UpdateSiteFinishingMaterialAsync(int siteId, int finishingMaterialId, CancellationToken ct);
}
```

#### Регистрация в DI

```csharp
// Program.cs
builder.Services.AddScoped<IVisarySitesCrudClient, VisarySitesCrudClient>();
```

#### Использование в FinModelImportMapper

```csharp
public sealed class FinModelImportMapper : IImportMapper
{
    private readonly IVisarySitesCrudClient _visaryCrudClient;

    public FinModelImportMapper(
        ILogger<FinModelImportMapper> log,
        IVisarySitesCrudClient visaryCrudClient)
    {
        _log = log;
        _visaryCrudClient = visaryCrudClient;
    }

    public async Task<ApplyResult> ApplyAsync(...)
    {
        var finishingMaterialId = ...;
        var success = await _visaryCrudClient.UpdateSiteFinishingMaterialAsync(
            context.VisarySiteId.Value, finishingMaterialId, ct);
        
        return new ApplyResult(success ? 1 : 0, errors);
    }
}
```

---

## 🗂️ Изменения в сущности ConstructionSite

### Файлы

| Файл | Изменения |
|------|----------|
| `Data/Visary/Entities/ConstructionSite.cs` | Добавлено свойство `FinishingMaterialId` |
| `Data/Visary/VisaryDbContext.cs` | Добавлен маппинг колонки `FinishingMaterialId` |
| `Domain/Sites/SitesSyncService.cs` | Обновлен `ConstructionSiteRaw` и `UpsertAsync` для `FinishingMaterialId` |
| `Domain/Visary/VisarySitesCrudClient.cs` | **НОВЫЙ** — CRUD клиент для Visary |

---

## 🔌 Visary API контракты

### GET ConstructionSite (по ID через associatedFilter)

**GET** `/api/visary/listview/constructionsite`

**Body**:
```json
{
  "Mnemonic": "constructionsite",
  "PageSkip": 0,
  "PageSize": 1,
  "Columns": ["ID", "Title", "FinishingMaterialId", "Version", ...],
  "Hidden": false,
  "AssociatedID": 123
}
```

**Response**:
```json
{
  "Data": [
    {
      "ID": 123,
      "Title": "Объект 1",
      "FinishingMaterialId": 1,
      "Version": "2026-04-01T12:00:00"
    }
  ],
  "Total": 1
}
```

---

### PUT ConstructionSite (обновление)

**PUT** `/api/visary/listview/constructionsite`

**Body**:
```json
{
  "Mnemonic": "constructionsite",
  "Data": [
    {
      "ID": 123,
      "FinishingMaterialId": 3,
      "Version": "2026-04-01T12:00:00"
    }
  ]
}
```

**Важно**: Поле `Version` обязательно для optimistic concurrency control！

---

## 📦 Количественные метрики

| Компонент | Файлы | Строк кода | Ошибок компиляции |
|-----------|-------|-----------|------------------|
| SitesSyncService | 1 файл | ~170 | 0 |
| VisarySitesCrudClient | 1 файл | ~210 | 0 |
| **Всего** | 2 файла | ~380 | 0 |

---

## ⚠️ Особенности и ограничения

### 1. Optimistic Concurrency

Visary API использует optimistic concurrency через поле `Version`. При обновлении нужно передавать текущую версию, иначе API вернёт 409 Conflict.

**Решение**: Сначала GET с `Version`, потом PUT с тем же `Version`.

---

### 2. Кэширование против Visary Data Source

**Вопрос**: Зачем кэшировать объекты, если они и так в Visary?

**Ответ**:
- Visary ListView API медленный (сеть, аутентификация, обработка запроса)
- Кэш в本地 БД ускоряет загрузку списков в UI
- Дублируем только ID и основные поля (Title, Address, Project)

---

### 3. Обновление FinishingMaterialId

**Вопрос**: Почему обновление через Visary API, а не через EF Core?

**Ответ**:
- Visary DB управляется внешней системой
- EF Core не должен писать в чужую БД
- Visary API — официальный интерфейс для изменений

---

## 🎯 Чек-лист

### При добавлении нового маппера, использующего Visary API:

- [ ] Добавлен интерфейс в `Domain/Visary/IVisaryXxxClient.cs`
- [ ] Реализация в `Domain/Visary/XxxClient.cs`
- [ ] Регистрация в DI (`Program.cs`)
- [ ] Обработка `VisaryAuthException` и `HttpRequestException`
- [ ] Логирование через `_log`
- [ ] Тесты (в идеале — in-memory + mock HTTP client)

---

## 📊 Логи

### SitesSyncService

```
Visary → POST listview/constructionsite siteId=123 associatedFilter
Visary ← 200 listview/constructionsite siteId=123: 1 row
SitesSyncService.UpsertAsync: siteId=123 operation=Inserted
```

### VisarySitesCrudClient

```
Visary → GET constructionsite by ID=123
Visary ← 200 constructionsite siteId=123: 1 row
Visary → PUT constructionsite ID=123
Visary ← 200 PUT constructionsite ID=123
```

---

## 📝 История

| Дата | Версия | Изменения |
|------|--------|----------|
| 2026-05-03 | 1.0 | Инициальная реализация SitesSyncService и VisarySitesCrudClient |

---

**Версия документа**: 1.0  
**Дата**: 2026-05-03  
**Автор**: Kilo
