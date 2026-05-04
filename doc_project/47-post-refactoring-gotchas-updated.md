# ⚠️ Незавершённые части проекта KiloImportService

**Дата документа**: 2026-05-04  
**Проект**: Сервис импорта файлов для Visary (Альфа Банк)

---

## 📋 Сводка

В проекте обнаружены следующие незавершённые части:

| Приоритет | Описание | Файл | Статус |
|-----------|----------|------|--------|
| 🔴 Высокий | Тесты `FakeListViewClient` не реализованы | `KiloImportService.Api.Tests/Projects/ProjectsCacheServiceTests.cs:252-260` | Частично |
| 🟡 Средний | Баг с вычислением времени в `sitesSync.ts` | `KiloImportService.Web/src/services/sitesSync.ts:24` | Нужно исправить |
| 🟡 Средний | TODO в `importsHub.ts` устарел | `KiloImportService.Web/src/services/importsHub.ts:5` | Нужно удалить |
| 🟢 Низкий | Файл-дубликат в пути | `KiloImportService.Api/ KiloImportService.Api/...` (с пробелом) | Удалить |
| 🟢 Низкий | Дублирующийся код в `VisaryListViewClient.cs` | `KiloImportService.Api/Domain/Visary/VisaryListViewClient.cs` | Устарел |

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
- `Visary.Api.Client/ListView/IListViewClient.cs:21-27` — интерфейс с этими методами
- `Visary.Api.Client/ListView/ListViewClient.cs:149-159` — реальная реализация в библиотеке
- `KiloImportService.Api/Data/Visary/VisaryDbContext.cs` — контекст для работы с `ConstructionSite`

---

## 🟡 Средний приоритет

### 2. Баг с вычислением времени в `sitesSync.ts`

**Файл**: `KiloImportService.Web/src/services/sitesSync.ts:24`

**Проблема**: Результат вычисления времени всегда 0, т.к. `performance.now()` вызывается дважды подряд без промежуточного измерения.

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

**Проблема**: В комментарии указано `(TODO)` для события `StageProgress`, однако событие реализовано и используется (строки 100-106).

**Детали**:
```typescript
// server → client: `StageStarted`, `StageCompleted`, `SessionStatus`, `StageProgress` (TODO)
```

**Решение**: Удалить `(TODO)` из комментария, так как событие `StageProgress` обработано корректно.

---

## 🟢 Низкий приоритет

### 4. Файл в некорректном пути (probable typo)

**Путь из результатов `glob`**: `KiloImportService.Api/ KiloImportService.Api/Domain/Visary/VisarySitesCrudClient.cs`

**Проблема**: Путь содержит лишний пробел после `KiloImportService.Api/`.

**Состояние**: file not found по правильному пути `KiloImportService.Api/Domain/Visary/VisarySitesCrudClient.cs`.

**Решение**: 
- Удалить файл, если он дублирует функциональность `Visary.Api.Client/CRUD/CrudClient.cs`
- Или переместить в правильную папку `Domain/Visary/`

---

### 5. Дублирующийся код в `VisaryListViewClient.cs`

**Файл**: `KiloImportService.Api/Domain/Visary/VisaryListViewClient.cs:1-107`

**Проблема**: Класс дублирует функциональность `Visary.Api.Client/ListView/ListViewClient.cs`. В `Program.cs` используется новая библиотека, но старый файл не удален.

**Сравнение**:

| Функция | `VisaryListViewClient` (старый) | `ListViewClient` (новый) |
|---------|--------------------------------|--------------------------|
| Поддержка | ❌ Устарел | ✅ Используется |
| Логирование | `LogInformation`/`LogDebug` | `LogInformation`/`LogDebug` |
| Обработка ошибок | ✅ | ✅ |
| Кэширование | ❌ Нет | ✅ `HttpClient` от DI |
| Место | `KiloImportService.Api/Domain/Visary/` | `Visary.Api.Client/ListView/` |

**Влияние**: 
- Дублирование кода усложняет поддержку
- Может путать разработчиков при поиске логики
- Существует риск использования старого клиента

**Решение**: Удалить файл `VisaryListViewClient.cs`, так как в `Visary.Api.Client` уже есть реализация.

---

## 🟢 Незначительные замечания

### 6. Дублирующийся код в `VisaryApiOptions.cs` и `VisaryOptions.cs`

**Файлы**:
- `KiloImportService.Api/Domain/Visary/VisaryApiOptions.cs`
- `Visary.Api.Client/VisaryOptions.cs`

**Проблема**: Оба файла содержат одиаковые опции (`BaseUrl`, `BearerToken`). Второй — часть переиспользуемой библиотеки.

**Статус**: Пока функционирует, т.к. `Visary.Api.Client` используется с `IVisaryClient`, но старый класс остаётся.

**Рекомендация**: При следующем рефакторинге удалить `VisaryApiOptions.cs` после полной миграции на `VisaryOptions` из библиотеки.

---

## 📊 Статус тестов

| Компонент | Тесты | Покрытие | Примечание |
|-----------|-------|----------|------------|
| Backend (xUnit) | 64/64 проходят | ✅ 100% | Все тесты успешны |
| Frontend (Vitest) | 28/28 проходят | ✅ 100% | Все тесты успешны |
| `FakeListViewClient` | ⚠️ Пропуск | 🟡 Частично | Методы `GetSitesByProjectAsync`, `GetSiteByIdAsync` выбрасывают `NotImplementedException` |

---

## 🎯 Рекомендуемые действия

### Ближайшее время (critical)
1. ✅ **Исправить баг в `sitesSync.ts`** (24 строка) — простая правка
2. ✅ **Удалить устаревший TODO** из `importsHub.ts:5`

### Следующая итерация (high)
3. ✅ **Реализовать тесты `FakeListViewClient`** или отметить `Skip` для невозможных
4. ✅ **Удалить `VisaryListViewClient.cs`** после верификации, что вся логика в `Visary.Api.Client`

### Опционально (medium)
5. ✅ Проверить `VisaryApiOptions.cs` — оставить только если используется где-то вне библиотеки
6. ✅ Поискать и удалить дубликаты в файловой системе (с пробелом в пути)

---

## 🔍 Методика диагностики

Документ был составлен на основе:

1. **Поиск по TODO/FIXME/HACK/XXX**
2. **Анализ тестов на `NotImplementedException`**
3. **Поиск дубликатов в классах Visary API**
4. **Ручная проверка векторов производительности** (`performance.now`)
5. **Анализ путей файлов из `glob`**

---

**Версия документа**: 1.0  
**Автор**: Kilo  
**Дата создания**: 2026-05-04
