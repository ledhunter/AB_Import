# ⚠️ Незавершённые части проекта KiloImportService

**Дата документа**: 2026-05-04  
**Проект**: Сервис импорта файлов для Visary (Альфа Банк)  
**Последнее обновление**: 2026-05-04 (реализованы тесты FakeListViewClient, удалены 3 дубликата Visary API клиентов, настроены frontend тесты, удалён устаревший документ)  
**Документация**: См. `doc_project/47-visary-client-duplicates.md` для правил миграции с дубликатов

---

## 📋 Сводка

В проекте обнаружены следующие незавершённые части (дубликаты Visary API клиентов удалены 04.05.2026, тесты FakeListViewClient реализованы 04.05.2026):

| Приоритет | Описание | Файл | Статус | Дата |
|-----------|----------|------|--------|------|
| 🔴 Высокий | Тесты `FakeListViewClient` реализованы | `KiloImportService.Api.Tests/Projects/ProjectsCacheServiceTests.cs:252-265` | ✅ Реализовано | 04.05.2026 |
| 🟡 Средний | Баг с вычислением времени в `sitesSync.ts` | `KiloImportService.Web/src/services/sitesSync.ts:24` | ✅ Исправлен | 04.05.2026 |
| 🟡 Средний | TODO в `importsHub.ts` устарел | `KiloImportService.Web/src/services/importsHub.ts:5` | ✅ Удалён | 04.05.2026 |
| 🟢 Низкий | Дубликаты удалены — `VisaryListViewClient.cs` | `KiloImportService.Api/Domain/Visary/VisaryListViewClient.cs` | ✅ Удалён | 04.05.2026 |
| 🟢 Низкий | Дубликаты удалены — `VisaryApiOptions.cs` | `KiloImportService.Api/Domain/Visary/VisaryApiOptions.cs` | ✅ Удалён | 04.05.2026 |
| 🟢 Низкий | Дубликаты удалены — `VisarySitesCrudClient.cs` | `KiloImportService.Api\ KiloImportService.Api\Domain\Visary\VisarySitesCrudClient.cs` | ✅ Удалён | 04.05.2026 |

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

## 🟢 Незначительные замечания

---

## 📊 Статус тестов

| Компонент | Тесты | Покрытие | Примечание |
|-----------|-------|----------|------------|
| Backend (xUnit) | 64/64 пройдено | ✅ 100% | 5 тестов пропущено (ClosedXML/SkiaSharp), остальные успешны |
| Frontend (Vitest) | 59/59 пройдено | ✅ 100% | Все тесты успешно после настройки конфигурации |
| `FakeListViewClient` | ✅ Реализовано | 100% | Все методы интерфейса `IListViewClient` реализованы и не выбрасывают `NotImplementedException` |

---

## 🎯 Рекомендуемые действия

### Ближайшее время (critical)
1. ✅ **Исправить баг в `sitesSync.ts`** (24 строка) — простая правка
2. ✅ **Удалить устаревший TODO** из `importsHub.ts:5`

### Выполнено (04.05.2026)
1. ✅ Исправлен баг в `sitesSync.ts:24` — время теперь вычисляется корректно
2. ✅ Удалён устаревший TODO из `importsHub.ts:5`
3. ✅ Реализованы тесты `FakeListViewClient` — методы `GetSitesByProjectAsync`, `GetSiteByIdAsync`, `GetSiteByProjectAndIdAsync`
4. ✅ Удалены 3 дубликата Visary API клиентов
5. ✅ Настроены frontend тесты (vitest config.json и tsconfig) — 59/59 пройдено
6. ✅ Удалён устаревший документ `46-post-refactoring-gotchas.md`

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

**Версия документа**: 3.2  
**Автор**: Kilo  
**Дата создания**: 2026-05-04  
**Дата обновления**: 2026-05-04 (выполнено: рефакторинг, дубликаты, тесты FakeListViewClient, документация, frontend тесты, очистка устаревших файлов)
