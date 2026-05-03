# 🧰 Паттерн глобальных using alias

## 📋 Описание

В проекте при интеграции библиотеки `Visary.Api.Client` возникли конфликты имён между:
- `KiloImportService.Api.Domain.Visary.VisaryOptions` (убран)
- `Visary.Api.Dto.VisaryOptions` (новая библиотека)

Для решения этой проблемы был использован **глобальный using alias** — явное указание полного namespace с префиксом `global::`.

---

## ✅ Правильная реализация

### Пример: Использование VisaryOptions из библиотеки

**Файл**: `KiloImportService.Api.Domain.Sites.SitesSyncService`

```csharp
using Visary.Api;
using Visary.Api.CRUD;
using Visary.Api.Dto;

namespace KiloImportService.Api.Domain.Sites;

public sealed class SitesSyncService : ISitesSyncService
{
    private readonly VisaryDbContext _db;
    private readonly ICrudClient _visaryClient;
    private readonly global::Visary.Api.Dto.VisaryOptions _options;  // 👈 Глобальный alias

    public SitesSyncService(
        VisaryDbContext db,
        ICrudClient visaryClient,
        IOptions<global::Visary.Api.Dto.VisaryOptions> options,  // 👈 Глобальный alias
        ILogger<SitesSyncService> log)
    {
        _db = db;
        _visaryClient = visaryClient;
        _options = options.Value;
        _log = log;
    }
}
```

**Объяснение**:
- `global::` указывает компилятору, что искать namespace нужно от корня (глобально)
- Это предотвращает поиск внутри текущего `KiloImportService.Api.Domain.Visary`
- Компилятор однозначно находит `Visary.Api.Dto.VisaryOptions`

---

### Пример: Использование типов из библиотеки

```csharp
// Глобальный alias для DTO типов
private async Task UpsertAsync(global::Visary.Api.Dto.ConstructionSiteRaw raw, CancellationToken ct)
{
    var entity = existing ?? new ConstructionSite { Id = raw.ID };
    // ...
}

// Глобальный alias для клиента
private global::Visary.Api.ListView.IListViewClient GetListViewClient()
{
    return new global::Visary.Api.ListView.ListViewClient(...);
}
```

---

## ❌ Типичная ошибка

### ❌ Отсутствие global:: prefix

**НЕПРАВИЛЬНО** — попытка использовать имя без глобального prefix:

```csharp
// ❌ КОМПИЛЯТОР НЕ НАЙДЁТ ТИП!
using Visary.Api.Dto;

private Visary.Api.Dto.VisaryOptions _options;  // 👈 Ошибка!
// Компилятор ищет KiloImportService.Api.Domain.Visary.VisaryOptions (убран)
```

**Ошибка компиляции**:
```
error CS0234: Тип или имя пространства имен "Api" не существует в пространстве имен "KiloImportService.Api.Domain.Visary"
```

**Почему?**:
- Компилятор пытается разрешить `Visary.Api.Dto.VisaryOptions` внутри текущего `namespace KiloImportService.Api.Domain.Visary`
- Получает `KiloImportService.Api.Domain.Visary.Visary.Api.Dto.VisaryOptions` — неверная вложенность

---

## 📦 Когда использовать

### ✅ Используй `global::` когда:
- Есть конфликт имён между локальными и внешними types
- Используешь типы из библиотек, которые имеют общие имена (Options, Response, Client, Exception)
- В проекте есть `using SomeNamespace` с совпадающими именами

### ❌ Можно не использовать когда:
- Нет конфликтов имён (например, `Visary.Api.Dto.ConstructionSiteRaw` уникальное имя)
- Все using корректно разрешают типы

---

## 🎯 Чек-лист

При интеграции сторонней библиотеки в существующий проект:

- [ ] Проанализируй namespace существующих типов
- [ ] Проверь namespace библиотеки на совпадения (Options, Client, Response, Exception)
- [ ] Используй `global::` для типов с потенциально конфликтными именами
- [ ] Протестируй компиляцию без `global::` — если ошибка, добавь prefix
- [ ] Убедись, что все типы разрешаются однозначно

---

## 📚 См. также

- [C# Global usings](https://learn.microsoft.com/en-us/dotnet/csharp/imports#global-using-directives)
- `doc_project/40-visary-api-refactoring-completed.md` — Рефакторинг Visary API
- `Visary.Api.Client/VisaryOptions.cs` — Библиотечная опция
- `KiloImportService.Api/Program.cs` — Пример интеграции

---

**Версия документа**: 1.0  
**Дата**: 2026-05-03  
**Автор**: Kilo
