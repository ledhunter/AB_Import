# 🧰 Удаление дубликатов Visary API DTO и исключений

## 📋 Описание

**Статус**: ✅ Завершено  
**Дата**: 2026-05-05  
**Совместимость**: Полная — все существующие тесты проходят (64/64 backend, сборка успешна)

В проекте существовали дубликаты типов, уже определённых в библиотеке `Visary.Api.Client`. Это нарушало принцип DRY и создавало риск рассогласованности при обновлениях.

---

## 📦 Удалённые файлы

| Файл (старый) | Причина | Замена |
|--------------|---------|--------|
| `Domain/Visary/VisaryAuthException.cs` | Дубликат исключения аутентификации | `Visary.Api.Exceptions.VisaryAuthException` |
| `Domain/Visary/ListViewResponse.cs` | Дубликат DTO ответа (свойства `Rows`/`TotalRows`) | `Visary.Api.Dto.ListViewResponse<T>` (свойства `Data`/`Total`) |
| `Domain/Visary/ConstructionProjectRaw.cs` | Дубликат DTO проекта | `Visary.Api.Dto.ConstructionProjectRaw` |
| `Data/Visary/Entities/ConstructionSiteRaw.cs` | Дубликат DTO объекта строительства | `Visary.Api.Dto.ConstructionSiteRaw` |

---

## ✅ Правильная реализация

### 1. Использование using alias для разрешения конфликтов

```csharp
using VisaryAuthException = Visary.Api.Exceptions.VisaryAuthException;
using ConstructionProjectRaw = Visary.Api.Dto.ConstructionProjectRaw;
using ConstructionSiteRaw = Visary.Api.Dto.ConstructionSiteRaw;
```

**Преимущества**:
- ✅ Явное указание источника типов
- ✅ Возможность локального переопределения (если потребуется)
- ✅ Согласованность с паттерном `doc_project/42-global-using-alias.md`

### 2. Упрощённая регистрация в `Program.cs`

#### ✅ Правильная реализация

**Файл**: `Program.cs`

```csharp
using Visary.Api;
using Visary.Api.CRUD;
using Visary.Api.Dto;  // 👈 Добавлен импорт для VisaryOptions

// ...

builder.Services
    .AddVisaryClient(opt =>
    {
        opt.BaseUrl = builder.Configuration["Visary:BaseUrl"] ?? string.Empty;
        opt.BearerToken = builder.Configuration["Visary:BearerToken"] ?? string.Empty;
        opt.RequestTimeout = TimeSpan.FromSeconds(30);
    })
    .Configure<VisaryOptions>(builder.Configuration.GetSection(VisaryOptions.SectionName));
```

**Объяснение**:
- `AddVisaryClient()` автоматически регистрирует `IListViewClient` и `ICrudClient`
- `Configure<VisaryOptions>()` связывает конфигурацию из `appsettings.json`
- Все Visary API клиенты получают настройки через `IOptions<VisaryOptions>`

#### ❌ Типичная ошибка

**НЕПРАВИЛЬНО** — отсутствие импорта `Visary.Api.Dto`:

```csharp
using Visary.Api;
using Visary.Api.CRUD;
using Visary.Api.Exceptions;
// using Visary.Api.Dto;  // ❌ Отсутствует — VisaryOptions не найден!
```

**Ошибка компиляции**:
```
error CS0246: Не удалось найти тип или имя пространства имен "VisaryOptions"
```

**Почему?**:
- `VisaryOptions` находится в `Visary.Api.Dto` namespace
- Без `using Visary.Api.Dto` компилятор не может разрешить имя `VisaryOptions`

### 3. Удаление вложенных классов

#### ✅ Правильная реализация

**Файл**: `SitesSyncService.cs`

```csharp
using ConstructionSiteRaw = Visary.Api.Dto.ConstructionSiteRaw;  // 👈 Using alias

public sealed class SitesSyncService : ISitesSyncService
{
    private async Task UpsertAsync(ConstructionSiteRaw raw, CancellationToken ct)
    {
        var existing = await _db.ConstructionSites
            .FirstOrDefaultAsync(s => s.Id == raw.ID, ct);

        var entity = existing ?? new ConstructionSite { Id = raw.ID };
        entity.Title = string.IsNullOrEmpty(raw.Title) ? $"Site #{raw.ID}" : raw.Title!;
        entity.ConstructionProjectId = raw.ConstructionProjectId;
        entity.FinishingMaterialId = raw.FinishingMaterialId;
        // ... обновление остальных полей

        if (existing == null)
            _db.ConstructionSites.Add(entity);

        await _db.SaveChangesAsync(ct);
    }
}
```

**Объяснение**:
- `ConstructionSiteRaw` теперь из `Visary.Api.Dto` — единый источник правды
- Нет необходимости в дублировании структуры класса
- Изменения в DTO библиотеки автоматически применимы ко всем местам

#### ❌ Типичная ошибка

**НЕПРАВИЛЬНО** — вложенный дубликат класса:

```csharp
public sealed class SitesSyncService : ISitesSyncService
{
    // ...
    
    public sealed class ConstructionSiteRaw  // ❌ Вложенный дубликат!
    {
        public int ID { get; set; }
        public string? Title { get; set; }
        public int? ConstructionProjectId { get; set; }
        public string? ConstructionPermissionNumber { get; set; }
        // ... 16 свойств — дублируют Visary.Api.Dto.ConstructionSiteRaw!
    }

    private async Task UpsertAsync(ConstructionSiteRaw raw, CancellationToken ct)
    {
        // Используем вложенный класс вместо библиотечного
    }
}
```

**Проблемы**:
- ❌ Дублирование кода — 16 свойств в двух местах
- ❌ Риск рассогласованости — при изменении библиотечного DTO локальный не обновится
- ❌ Затруднён рефакторинг — нужно знать про оба класса

---

## 📍 Применение в проекте

---

## 📍 Применение в проекте

| Компонент | Файл | Классы/Переменные |
|-----------|------|-------------------|
| СITES CONTROLLER | `Controllers/SitesController.cs` | `VisaryAuthException` (alias) |
| PROJECTS CACHE SERVICE | `Domain/Projects/ProjectsCacheService.cs` | `ConstructionProjectRaw` (alias), _visaryClient: `IListViewClient` |
| SITES SYNC SERVICE | `Domain/Sites/SitesSyncService.cs` | `ConstructionSiteRaw` (alias), _visaryClient: `ICrudClient` |
| FIN MODEL MAPPER | `Domain/Mapping/FinModelImportMapper.cs` | `ConstructionSiteRaw` (alias), _visaryClient: `ICrudClient` |
| VISARY OPTIONS | `Visary.Api.Client/VisaryOptions.cs` | Конфигурация (BaseUrl, BearerToken) |
| LIST VIEW CLIENT | `Visary.Api.Client/ListView/ListViewClient.cs` | `IListViewClient`, `GetProjectsAsync`, `GetSitesByProjectAsync` |
| CRUD CLIENT | `Visary.Api.Client/CRUD/CrudClient.cs` | `ICrudClient`, `UpdateSiteFinishingMaterialAsync` |

---

## 🧪 Тестирование

---

## 🧪 Тестирование

### Backend (xUnit)

**Статус**: ✅ 64/64 пройдено  
**Пропущено**: 5 тестов (ClosedXML/SkiaSharp)

```bash
cd KiloImportService.Api.Tests
dotnet test
# Результат: Пройдено! : не пройдено 0, пройдено 64, пропущено 5, всего 69
```

### Сборка

**Статус**: ✅ без ошибок  
**Предупреждения**: 3 (не критичные: Nullable reference types)

```bash
cd KiloImportService.Api
dotnet build
# Результат: Сборка успешно завершена
```

---

## 💡 Мотивация

1. **Устранение технического долга**: 4 дубликата → 0
2. **Гарантия согласованности**: все типы живут в одной библиотеке
3. **Упрощение деплоя**: меньше кода → меньше багов
4. **Подготовка к масштабированию**: новые типы импорта (Rooms, ShareAgreements, PaymentSchedule) будут использовать единые DTO

---

## 🎯 Чек-лист

- [ ] Проверить, что все дубликаты удалены
- [ ] Убедиться, что все using aliases объявлены корректно
- [ ] Протестировать сборку: `dotnet build`
- [ ] Запустить тесты: `dotnet test`
- [ ] Проверить, что все типы разрешаются без конфликтов

---

## 📚 См. также

- `doc_project/42-global-using-alias.md` — паттерн глобальных using alias
- `doc_project/40-visary-api-refactoring-completed.md` — завершение рефакторинга Visary API
- `doc_project/38-visary-client-refactoring.md` — план рефакторинга
- `INCOMPLETE_PARTS.md` — исторический контекст незавершённых частей

---

**Версия**: 1.0  
**Автор**: Kilo  
**Дата**: 2026-05-05
