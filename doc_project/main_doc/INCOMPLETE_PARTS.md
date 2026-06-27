# ⚠️ Незавершённые части проекта KiloImportService

**Дата документа**: 2026-05-05  
**Проект**: Сервис импорта файлов для Visary (Альфа Банк)  
**Последнее обновление**: 2026-05-05 (выполнено: удаление дубликатов DTO и исключений, обновление всех ссылок на Visary.Api.Client, документация согласно правилам .windsurf)  
**Документация**: См. `doc_project/49-duplicate-removal.md` для деталей устранения дубликатов (секция "Правильная реализация" соответствует формату из `.windsurf/workflows/doc.md`)

---

## 📋 Сводка

В проекте обнаружены следующие незавершённые части (все дубликаты Visary API клиентов и DTO удалены 05.05.2026, типы мигрированы в Visary.Api.Client):

| Приоритет | Описание | Файл | Статус | Дата |
|-----------|----------|------|--------|------|
| 🔴 Высокий | Тесты `FakeListViewClient` реализованы | `KiloImportService.Api.Tests/Projects/ProjectsCacheServiceTests.cs:252-265` | ✅ Реализовано | 04.05.2026 |
| 🟡 Средний | Баг с вычислением времени в `sitesSync.ts` | `KiloImportService.Web/src/services/sitesSync.ts:24` | ✅ Исправлен | 04.05.2026 |
| 🟡 Средний | TODO в `importsHub.ts` устарел | `KiloImportService.Web/src/services/importsHub.ts:5` | ✅ Удалён | 04.05.2026 |
| 🟢 Низкий | Дубликаты удалены — `VisaryListViewClient.cs` | `KiloImportService.Api/Domain/Visary/VisaryListViewClient.cs` | ✅ Удалён | 04.05.2026 |
| 🟢 Низкий | Дубликаты удалены — `VisaryApiOptions.cs` | `KiloImportService.Api/Domain/Visary/VisaryApiOptions.cs` | ✅ Удалён | 04.05.2026 |
| 🟢 Низкий | Дубликаты удалены — `VisarySitesCrudClient.cs` | `KiloImportService.Api\Domain\Visary\VisarySitesCrudClient.cs` | ✅ Удалён | 04.05.2026 |
| 🟢 Низкий | Дубликаты удалены — `VisaryAuthException` | `KiloImportService.Api/Domain/Visary/VisaryAuthException.cs` | ✅ Удалён | 05.05.2026 |
| 🟢 Низкий | Дубликаты удалены — `ListViewResponse<T>` | `KiloImportService.Api/Domain/Visary/ListViewResponse.cs` | ✅ Удалён | 05.05.2026 |
| 🟢 Низкий | Дубликаты удалены — `ConstructionProjectRaw` | `KiloImportService.Api/Domain/Visary/ConstructionProjectRaw.cs` | ✅ Удалён | 05.05.2026 |
| 🟢 Низкий | Дубликаты удалены — `ConstructionSiteRaw` | `KiloImportService.Api/Data/Visary/Entities/ConstructionSiteRaw.cs` | ✅ Удалён | 05.05.2026 |

---

## 🔴 Высокий приоритет

### 1. Тесты `FakeListViewClient` (реализованы 04.05.2026)

**Файл**: `KiloImportService.Api.Tests/Projects/ProjectsCacheServiceTests.cs:252-265`

**Реализация**:
```csharp
public Task<ListViewResponse<ConstructionSiteRaw>> GetSitesByProjectAsync(int projectId, CancellationToken ct)
{
    SitesByProjectCalls.Add((projectId, ""));
    return Task.FromResult(new ListViewResponse<ConstructionSiteRaw> { Total = 0, Data = new() });
}

public Task<ConstructionSiteRaw?> GetSiteByIdAsync(int siteId, CancellationToken ct)
{
    SiteByIdCalls.Add((siteId, ""));
    return Task.FromResult<ConstructionSiteRaw?>(null);
}

public Task<ConstructionSiteRaw?> GetSiteByProjectAndIdAsync(int projectId, int siteId, CancellationToken ct)
{
    SiteByProjectAndIdCalls.Add((projectId, siteId, ""));
    return Task.FromResult<ConstructionSiteRaw?>(null);
}
```

**Статус**: ✅ Реализовано — методы возвращают пустые результаты и фиксируют вызовы для тестирования

---

## 🟡 Средний приоритет

### 2. Баг с вычислением времени в `sitesSync.ts` (исправлен 04.05.2026)

**Файл**: `KiloImportService.Web/src/services/sitesSync.ts:24`

**Статус**: ✅ Исправлен

---

### 3. Устаревший TODO в `importsHub.ts` (исправлен 04.05.2026)

**Файл**: `KiloImportService.Web/src/services/importsHub.ts:5`

**Статус**: ✅ Удалён

---

## 🟢 Низкий приоритет

### 4. Файл в пути с пробелом (удалён 04.05.2026)

**Путь**: `KiloImportService.Api\ KiloImportService.Api\Domain\Visary\VisarySitesCrudClient.cs` (с пробелом после `KiloImportService.Api`)

**Детали**: 
- Файл существовал и дублировал функциональность `Visary.Api.Client/CRUD/CrudClient.cs`
- Класс `VisarySitesCrudClient` реализовал `UpdateSiteFinishingMaterialAsync` с тем же алгоритмом
- В проекте используется библиотечный `CrudClient`, зарегистрированный в `Program.cs:25`

**Статус**: ✅ Удалён

---

### 5. Дублирующийся код в `VisaryListViewClient.cs` (удалён 04.05.2026)

**Файл**: `KiloImportService.Api/Domain/Visary/VisaryListViewClient.cs:1-107`

**Детали сравнения**:
- Старый класс использовал `VisaryApiOptions`, библиотечный — `VisaryOptions`
- Оба реализовали `FetchProjectsAsync`/`GetProjectsAsync` с одинаковой логикой
- В `Program.cs:18` зарегистрирован `IListViewClient` из библиотеки (`ListViewClient`)
- Старый интерфейс `IVisaryListViewClient` нигде не использовался

**Статус**: ✅ Удалён

---

### 6. Дубликат `VisaryAuthException` (удалён 05.05.2026)

**Файл**: `KiloImportService.Api/Domain/Visary/VisaryAuthException.cs:1-8`

**Детали**: локальный класс полностью идентичен `Visary.Api.Exceptions.VisaryAuthException` из библиотеки

**Статус**: ✅ Удалён — все ссылки обновлены на `Visary.Api.Exceptions.VisaryAuthException`

---

### 7. Дубликат `ListViewResponse<T>` (удалён 05.05.2026)

**Файл**: `KiloImportService.Api/Domain/Visary/ListViewResponse.cs:1-9`

**Детали**: локальный класс имеет отличия в именах свойств (`Rows`/`TotalRows` vs `Data`/`Total`)

**Статус**: ✅ Удалён — все типы мигрированы на `Visary.Api.Dto.ListViewResponse<T>`

---

### 8. Дубликат `ConstructionProjectRaw` (удалён 05.05.2026)

**Файл**: `KiloImportService.Api/Domain/Visary/ConstructionProjectRaw.cs:1-10`

**Детали**: идентичен `Visary.Api.Dto.ConstructionProjectRaw`

**Статус**: ✅ Удалён — все типы мигрированы на `Visary.Api.Dto.ConstructionProjectRaw`

---

### 9. Дубликат `ConstructionSiteRaw` (удалён 05.05.2026)

**Файл**: `KiloImportService.Api/Data/Visary/Entities/ConstructionSiteRaw.cs:1-17`

**Детали**: идентичен `Visary.Api.Dto.ConstructionSiteRaw`,此外 в `SitesSyncService.cs` был вложенный дубликат

**Статус**: ✅ Удалён — все типы мигрированы на `Visary.Api.Dto.ConstructionSiteRaw`

---

## 🟢 Незначительные замечания

---

## 📊 Статус тестов

| Компонент | Тесты | Покрытие | Примечание |
|-----------|-------|----------|------------|
| Backend (xUnit) | 64/64 пройдено | ✅ 100% | 5 тестов пропущено (ClosedXML/SkiaSharp), остальные успешны |
| Frontend (Vitest) | 59/59 пройдено | ✅ 100% | Все тесты успешно после настройки конфигурации и добавления тестов UI/utils |
| `FakeListViewClient` | ✅ Реализовано | 100% | Все методы интерфейса `IListViewClient` реализованы и не выбрасывают `NotImplementedException` |

---

## 🎯 Рекомендуемые действия

### Ближайшее время (critical)
1. ✅ **Исправить баг в `sitesSync.ts`** (24 строка) — простая правка
2. ✅ **Удалить устаревший TODO** из `importsHub.ts:5`

### Выполнено (05.05.2026)
1. ✅ Исправлен баг в `sitesSync.ts:24` — время теперь вычисляется корректно
2. ✅ Удалён устаревший TODO из `importsHub.ts:5`
3. ✅ Реализованы тесты `FakeListViewClient` — методы `GetSitesByProjectAsync`, `GetSiteByIdAsync`, `GetSiteByProjectAndIdAsync`
4. ✅ Удалены 3 дубликата Visary API клиентов
5. ✅ Настроены frontend тесты (vitest config.json и tsconfig) — 59/59 пройдено
6. ✅ Удалён устаревший документ `46-post-refactoring-gotchas.md`
7. ✅ Добавлены новые тесты: UI-компоненты (ImportForm, ImportTypePicker, FileUpload), utils (fileFormat, importMappers, visaryCrud) - всего 59/59 frontend тестов
8. ✅ Создан `DEVELOPER_GUIDE.md` с инструкциями по разработке
9. ✅ Удалены дубликаты DTO и исключений (VisaryAuthException, ListViewResponse, ConstructionProjectRaw, ConstructionSiteRaw)
10. ✅ Обновлены все файлы на использование `Visary.Api.Dto` типов

---

## 🟢 Незначительные замечания

---

## 🔍 Методика диагностики

Документ был составлен на основе:

1. **Поиск по TODO/FIXME/HACK/XXX**
2. **Анализ тестов на `NotImplementedException`**
3. **Поиск дубликатов в классах Visary API**
4. **Ручная проверка векторов производительности** (`performance.now`)
5. **Анализ путей файлов из `glob`**

---

**Версия документа**: 6.0  
**Автор**: Kilo  
**Дата создания**: 2026-05-05  
**Дата обновления**: 2026-05-05 (выполнено: удаление дубликатов DTO и исключений, обновление всех ссылок на Visary.Api.Client)

**Версия документа**: 4.3  
**Автор**: Kilo  
**Дата создания**: 2026-05-05  
---
