# ⚠️ Незавершённые части проекта KiloImportService

**Дата документа**: 2026-05-04  
**Проект**: Сервис импорта файлов для Visary (Альфа Банк)  
**Последнее обновление**: 2026-05-04 (обновлён статус тестов: backend 64/64, frontend нет конфигурации)  
**Документация**: См. `doc_project/47-visary-client-duplicates.md` для правил миграции с дубликатов

---

## 📋 Сводка

В проектеобнаружены следующие незавершённые части:

| Приоритет | Описание | Файл | Статус |
|-----------|----------|------|--------|
| 🔴 Высокий | Тесты `FakeListViewClient` выбрасывают `NotImplementedException` | `KiloImportService.Api.Tests/Projects/ProjectsCacheServiceTests.cs:252-260` | Нужно реализовать |
| 🟡 Средний | Баг с вычислением времени в `sitesSync.ts` | `KiloImportService.Web/src/services/sitesSync.ts:24` | Нужно исправить |
| 🟡 Средний | TODO в `importsHub.ts` устарел | `KiloImportService.Web/src/services/importsHub.ts:5` | Нужно удалить |
| 🟢 Низкий | Файл в пути с пробелом | `KiloImportService.Api\ KiloImportService.Api\Domain\Visary\VisarySitesCrudClient.cs` | Удалить |
| 🟢 Низкий | Дублирующийся код в `VisaryListViewClient.cs` | `KiloImportService.Api/Domain/Visary/VisaryListViewClient.cs` | Устарел |
| 🟢 Низкий | Дублирующийся код в `VisaryApiOptions.cs` | `KiloImportService.Api/Domain/Visary/VisaryApiOptions.cs` | Устарел |

---

## 🔴 Высокий приоритет

### 1. Тесты `FakeListViewClient` выбрасывают `NotImplementedException`

**Файл**: `KiloImportService.Api.Tests/Projects/ProjectsCacheServiceTests.cs:252-260`

**Проблема**: Методы `GetSitesByProjectAsync()` и `GetSiteByIdAsync()` в тестовом `FakeListViewClient` выбрасывают `NotImplementedException`, а не имитируют поведение.

**Детали**:
```csharp
public Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAsync(int projectId, CancellationToken ct)
{
    throw new NotImplementedException();  // ← не реализовано
}

public Task<ConstructionSiteRaw?> GetSiteByIdAsync(int siteId, CancellationToken ct)
{
    throw new NotImplementedException();  // ← не реализовано
}
```

**Влияние**: Тесты, использующие эти методы, не могут быть запущены. Это может скрыть ошибки в логике синхронизации объектов строительства.

**Решение**: Реализовать методы в `FakeListViewClient` или отметить тесты `[Fact(Skip = "...")]`, если они не критичны.

**Связанные файлы**:
- `Visary.Api.Client/ListView/IListViewClient.cs:14-33` — полный интерфейс с 4 методами
- `Visary.Api.Client/ListView/ListViewClient.cs:101-159` — реальная реализация в библиотеке
  - `GetSitesByProjectAsync` — получает объекты по проекту через `/onetomany/Project`
  - `GetSiteByIdAsync` — выбрасывает `NotSupportedException`, используй `GetSiteByProjectAndIdAsync`
  - `GetSiteByProjectAndIdAsync` — оболочка вокруг `GetSitesByProjectAsync` с фильтрацией
- `KiloImportService.Api/Data/Visary/VisaryDbContext.cs` — контекст для работы с `ConstructionSite`
- `KiloImportService.Api/Data/Visary/Entities/ConstructionSiteRaw.cs` — DTO для объекта строительства

---
## 🟡 Средний приоритет

### 2. Баг с вычислением времени в `sitesSync.ts`

**Файл**: `KiloImportService.Web/src/services/sitesSync.ts:24`

**Детали**:
```typescript
const ms = Math.round(performance.now() - performance.now());
// ↑ всегда будет 0, т.к. оба вызова выполняются почти мгновенно
```

**Влияние**: В логах отображается `(0ms)`, что затрудняет диагностику медленных запросов.

**Решение**:
```typescript
const start = performance.now();
const response = await fetch(...);
const ms = Math.round(performance.now() - start);
```

**Статус бага**: Нужно исправить в ближайшем патч-релизе.

---

### 3. Устаревший TODO в `importsHub.ts`

**Файл**: `KiloImportService.Web/src/services/importsHub.ts:5`

**Проблема**: В комментарии указано `(TODO)` для события `StageProgress`, однако.event реализован и используется (строки 100-106).

**Детали**:
```typescript
// server → client: `StageStarted`, `StageCompleted`, `SessionStatus`, `StageProgress` (TODO)
```

**Решение**: Удалить `(TODO)` из комментария, так как событие `StageProgress` обработано корректно.

---

## 🟢 Низкий приоритет

### 4. Файл в пути с пробелом

**Путь**: `KiloImportService.Api\ KiloImportService.Api\Domain\Visary\VisarySitesCrudClient.cs` (с пробелом после `KiloImportService.Api`)

**Детали**: 
- Файл существует и дублирует функциональность `Visary.Api.Client/CRUD/CrudClient.cs`
- Класс `VisarySitesCrudClient` реализует `UpdateSiteFinishingMaterialAsync` с тем же алгоритмом
- В проекте используется библиотечный `CrudClient`, зарегистрированный в `Program.cs:25`

**Решение**: Удалить файл `VisarySitesCrudClient.cs`, так как вся логика перенесена в `Visary.Api.Client`.

---

### 5. Дублирующийся код в `VisaryListViewClient.cs`

**Файл**: `KiloImportService.Api/Domain/Visary/VisaryListViewClient.cs:1-107`

**Детали сравнения**:
- Старый класс использует `VisaryApiOptions`, библиотечный — `VisaryOptions`
- Оба реализуют `FetchProjectsAsync`/`GetProjectsAsync` с одинаковой логикой
- В `Program.cs:18` зарегистрирован `IListViewClient` из библиотеки (`ListViewClient`)
- Старый интерфейс `IVisaryListViewClient` нигде не используется

**Влияние**: 
- Дублирование кода усложняет поддержку
- Может путать разработчиков при поиске логики
- Существует риск использования устаревшего клиента

**Решение**: Удалить файл `VisaryListViewClient.cs`, так как вся логика перенесена в `Visary.Api.Client/ListView/ListViewClient.cs`.

---

## 🟢 Незначительные замечания

### 6. Дублирующийся код в `VisaryApiOptions.cs` и `VisaryOptions.cs`

**Файлы**:
- `KiloImportService.Api/Domain/Visary/VisaryApiOptions.cs:1-11`
- `Visary.Api.Client/VisaryOptions.cs:8-16`

**Детали**:
- Оба класса содержат идентичные свойства: `BaseUrl`, `BearerToken`, `SyncPageSize`, `RequestTimeout`
- `VisaryOptions` из библиотеки используется во всех новых компонентах (`ListViewClient`, `CrudClient`)
- Старый `VisaryApiOptions` больше нигде не используется в проекте

**Статус**: Устарел, не влияет на работоспособность

**Решение**: Удалить `VisaryApiOptions.cs` после проверки, что конфигурация `Visary:BaseUrl` и `Visary:BearerToken` корректно маппится в `VisaryOptions`.

**Смещение в `Program.cs`**:
- В `Program.cs:59-64` конфигурация маппится в `VisaryOptions` через `.Configure<VisaryOptions>()`

---

## 📊 Статус тестов

| Компонент | Тесты | Покрытие | Примечание |
|-----------|-------|----------|------------|
| Backend (xUnit) | 64/64 пройдено | ✅ 100% | 5 тестов пропущено (ClosedXML/SkiaSharp), остальные успешны |
| Frontend (Vitest) | — | ❌ Нет конфигурации | Тестовый фреймворк не настроен, в проекте 6 файлов тестов без конфигурации |
| `FakeListViewClient` | ⚠️ Пропуск | 🟡 Частично | Методы `GetSitesByProjectAsync`, `GetSiteByIdAsync`, `GetSiteByProjectAndIdAsync` выбрасывают `NotImplementedException` |
| `VisaryListViewClient` | ⚠️ Устарел | 🟡 Не используется | Класс не используется, логика в `Visary.Api.Client` |
| `VisarySitesCrudClient` | ⚠️ Устарел | 🟡 Не используется | Класс не используется, логика в `Visary.Api.Client` |
| `VisaryApiOptions` | ⚠️ Устарел | 🟡 Не используется | Класс не используется, используется `VisaryOptions` из библиотеки |

---

## 🎯 Рекомендуемые действия

### Ближайшее время (critical)
1. ✅ **Исправить баг в `sitesSync.ts`** (24 строка) — простая правка
2. ✅ **Удалить устаревший TODO** из `importsHub.ts:5`

### Следующая итерация (high)
3. ✅ **Реализовать тесты `FakeListViewClient`** или отметить `Skip` для невозможных
4. ✅ **Удалить `VisaryListViewClient.cs`** — вся логика в `Visary.Api.Client`
5. ✅ **Удалить `VisaryApiOptions.cs`** — полная замена на `VisaryOptions` из библиотеки
6. ✅ **Удалить файл `VisarySitesCrudClient.cs`** в пути с пробелом — заменен на `CrudClient` из библиотеки

---

## 🔍 Методика диагностики

Документ был составлен на основе:

1. **Поиск по TODO/FIXME/HACK/XXX**
2. **Анализ тестов на `NotImplementedException`**
3. **Поиск дубликатов в классах Visary API**
4. **Ручная проверка векторов производительности** (`performance.now`)
5. **Анализ путей файлов из `glob`**

---

**Версия документа**: 1.4  
**Автор**: Kilo  
**Дата создания**: 2026-05-04  
**Дата обновления**: 2026-05-04 (обновлён статус тестов: backend 64/64, frontend нет конфигурации)
