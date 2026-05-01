# 🔍 Найденные проблемы с Backend и их исправления

**Дата**: 2026-05-01  
**Автор**: Kilo

---

## 📋 Проблемы

### Проблема 1: Неверная логика поиска при пустом запросе

**Файл**: `KiloImportService.Api/Domain/Projects/ProjectsCacheService.cs:121-127`

**Описание**: 
Метод `SearchLocalAsync` возвращал все проекты из локального кэша, когда строка запроса была пустой. Это противоречит ожидаемому поведению:

- UI ожидает пустой список при пустой строке поиска
- Пустой запрос должен возвращать 0 результатов, а не все проекты
- Это может привести к проблемам в UI, если Select пытается отобразить 50+ проектов без фильтрации

**До**:
```csharp
if (string.IsNullOrEmpty(query))
{
    return await _db.CachedProjects
        .OrderBy(p => p.Title)
        .Take(take)
        .ToListAsync(ct);
}
```

**После**:
```csharp
if (string.IsNullOrEmpty(query))
{
    return Array.Empty<CachedProject>();
}
```

---

### Проблема 2: Метод Contains() вместо EF.Functions.Like()

**Файл**: `KiloImportService.Api/Domain/Projects/ProjectsCacheService.cs:134-143`

**Описание**:
Использование `.Contains()`, `.ToLower()` напрямую в LINQ может привести к:

1. **Плохой производительности**: EF Core может выполнить загрузку всей таблицы в память, а потом применить фильтр
2. **Непредсказуемому поведению**: Методы `.ToLower()` не всегда транслируются в SQL корректно для всех провайдеров
3. **Проблемам с индексами**: Запросы с `.Contains()` не могут эффективно использовать индексы

**До**:
```csharp
var lowered = query.ToLowerInvariant();
return await _db.CachedProjects
    .Where(p =>
        p.Title.ToLower().Contains(lowered) ||
        (p.IdentifierKK != null && p.IdentifierKK.ToLower().Contains(lowered)) ||
        (p.IdentifierZPLM != null && p.IdentifierZPLM.ToLower().Contains(lowered)))
    .OrderBy(p => p.Title)
    .Take(take)
    .ToListAsync(ct);
```

**После**:
```csharp
var lowered = query.ToLowerInvariant();
return await _db.CachedProjects
    .Where(p =>
        EF.Functions.Like(p.Title.ToLower(), $"%{lowered}%") ||
        (p.IdentifierKK != null && EF.Functions.Like(p.IdentifierKK.ToLower(), $"%{lowered}%")) ||
        (p.IdentifierZPLM != null && EF.Functions.Like(p.IdentifierZPLM.ToLower(), $"%{lowered}%")))
    .OrderBy(p => p.Title)
    .Take(take)
    .ToListAsync(ct);
```

**Преимущества**:
- ✅ EF Functions.Like транслируется в SQL `LIKE %value%`
- ✅ Использует функциональные индексы (если есть)
- ✅ Позволяет оптимизатору PostgreSQL строить правильные планы выполнения
- ✅ Кроссплатформенная идемпотентность (всегда работает одинаково)

---

## 🧪 Проверка изменений

### 1. Запуск backend
```bash
cd "C:\Users\ancye\Downloads\vs code\Alfa\KiloImportService.Api"
dotnet build
dotnet run
```

В логах должно быть:
```
[HH:mm:ss INF] Starting KiloImportService.Api...
[HH:mm:ss INF] Now listening on: http://localhost:5000
```

### 2. Тест поиска проектов

Для пустой строки запроса:
```bash
curl "http://localhost:5000/api/projects/search?q=&limit=10"
# Ожидаемый ответ: { "items": [], "total": 0 }
```

Для поиска:
```bash
curl "http://localhost:5000/api/projects/search?q=тест&limit=10"
# Ожидаемый ответ: { "items": [...], "total": N }
```

### 3. Проверка SQL через логи

В логах backend должен появиться SQL с `LIKE`:
```
[HH:mm:ss INF] Executing DbCommand [...] SELECT ...
FROM "import"."cached_projects"
WHERE (Lower(p."Title") LIKE @__lowered_0) OR ...
```

---

## 📊 Статистика

**Изменённые файлы**:
- `KiloImportService.Api/Domain/Projects/ProjectsCacheService.cs`

**Исправленные проблемы**:
- ❌ Логика поиска при пустом запросе
- ❌ Использование `.Contains()` вместо `EF.Functions.Like()`

**Ожидаемое улучшение**:
- Запросы к кэшу проектов станут быстрее
- Снижение нагрузки на PostgreSQL
- Правильная работа UI при вводе пустой строки

---

## 🔗 См. также

- `doc_project/18-projects-cache.md` — кэш проектов Visary
- `doc_project/26-troubleshooting.md` — решение проблем backend
- `doc_project/27-checklists.md` — чек-лист для деплоя

---

**Версия**: 1.0  
**Дата**: 2026-05-01  
**Автор**: Kilo
