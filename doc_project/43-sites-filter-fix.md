# 🔧 Исправление фильтрации объектов строительства по проекту

## 📋 Описание проблемы

При выборе проекта в форме импорта, выпадающий список "Объект строительства" показывал **все объекты из системы**, а не только те, что относятся к выбранному проекту.

### Причина

Код передавал `AssociationFilter` в **теле запроса**, но Visary API для эндпоинта `/onetomany/Project` требует:
1. **Query parameter** `associationId` в URL
2. **Отсутствие** поля `AssociationFilter` в теле запроса

Также использовались устаревшие названия полей:
- `SearchString` вместо `SearchPhrase`
- Отсутствовало поле `Summaries`

---

## ✅ Решение

### 1. Обновлена структура тела запроса

**Файл:** `KiloImportService.Web/src/services/listView/createListViewService.ts`

```ts
interface ListViewRequestBody {
  Mnemonic: string;
  PageSkip: number;
  PageSize: number;
  Columns: string[];
  Sorts: string;
  Hidden: boolean;
  ExtraFilter?: string | null;
  SearchPhrase: string | null;  // ✅ Было: SearchString
  Summaries: unknown[];          // ✅ Добавлено
  AssociationFilter?: {          // ⚠️ Только для не-onetomany эндпоинтов
    AssociatedId: number;
    Filters: unknown | null;
  } | null;
}
```

### 2. Условное добавление AssociationFilter

```ts
export function buildListViewRequestBody<TRaw, TItem>(
  config: ListViewServiceConfig<TRaw, TItem>,
  query: ListViewQuery = {},
): ListViewRequestBody {
  const body: ListViewRequestBody = {
    Mnemonic: config.mnemonic,
    PageSkip: query.pageSkip ?? 0,
    PageSize: query.pageSize ?? config.defaultPageSize ?? DEFAULT_PAGE_SIZE,
    Columns: config.columns,
    Sorts: query.sorts ?? config.defaultSort ?? DEFAULT_SORT,
    Hidden: false,
    ExtraFilter: query.extraFilter ?? null,
    SearchPhrase: query.searchString || null,  // ✅ Было: SearchString
    Summaries: [],                              // ✅ Добавлено
  };
  
  // AssociationFilter НЕ передаётся в теле для /onetomany эндпоинтов.
  // Вместо этого используется query parameter associationId (см. createListViewService).
  if (query.associationFilter && !config.pathSuffix?.includes('/onetomany/')) {
    body.AssociationFilter = query.associationFilter;
  }
  
  return body;
}
```

### 3. Query parameter для /onetomany

Код уже правильно передавал `associationId` как query parameter (строки 77-79):

```ts
// Для эндпоинта /onetomany/Project нужен query parameter associationId
const queryParams = query.associationFilter?.AssociatedId
  ? { associationId: query.associationFilter.AssociatedId }
  : undefined;
```

---

## 🧪 Обновлены тесты

### Файл: `createListViewService.test.ts`

1. Заменены проверки `SearchString` → `SearchPhrase`
2. Добавлены проверки `Summaries`
3. Убраны проверки `AssociatedID` (устаревшее поле)
4. Добавлены новые тесты:
   - `AssociationFilter НЕ попадает в тело для /onetomany эндпоинтов`
   - `AssociationFilter попадает в тело для обычных эндпоинтов`

### Файл: `sites.test.ts`

1. Обновлены проверки `toSiteItem` для актуальных полей:
   - `address`, `constructionProjectNumber`, `type`, `totalArea`
   - Убраны: `constructionPermissionNumber`, `stageNumber`
2. Обновлены тесты `buildSitesQueryByProject`:
   - Проверяется `associationFilter` вместо `associatedId` и `extraFilter`

**Результат:** Все 14 тестов проходят ✅

---

## 📊 Пример правильного запроса

### URL
```
POST https://isup-alfa-test.k8s.npc.ba/api/visary/listview/constructionsite/onetomany/Project?associationId=4584
```

### Тело запроса
```json
{
  "Mnemonic": "constructionsite",
  "PageSkip": 0,
  "PageSize": 50,
  "Columns": ["ID", "Title", "Address", "Type", ...],
  "Sorts": "[{\"selector\":\"ID\",\"desc\":false}]",
  "Hidden": false,
  "ExtraFilter": null,
  "SearchPhrase": null,
  "Summaries": []
}
```

**Обратите внимание:**
- ✅ `associationId=4584` в URL
- ✅ `SearchPhrase` вместо `SearchString`
- ✅ `Summaries: []`
- ✅ **НЕТ** поля `AssociationFilter` в теле

---

## 🎯 Результат

Теперь при выборе проекта в форме импорта, выпадающий список "Объект строительства" показывает **только объекты выбранного проекта**.

### Как проверить

1. Откройте форму импорта: http://localhost:5173
2. Выберите проект (например, "ДДУЗСК")
3. Откройте выпадающий список "Объект строительства"
4. Проверьте в DevTools → Network:
   - URL содержит `?associationId=<project_id>`
   - Тело запроса **не содержит** `AssociationFilter`
   - Тело содержит `SearchPhrase` и `Summaries`
5. Убедитесь, что показываются только объекты выбранного проекта

---

## 📝 Связанные документы

- `21-sites-by-project.md` — Документация по получению объектов по проекту
- `10-listview-library.md` — Библиотека методов Visary ListView
- `08-visary-api-integration.md` — Интеграция с Visary API

---

## ✅ Чек-лист изменений

- [x] Обновлена структура `ListViewRequestBody` (SearchPhrase, Summaries)
- [x] Добавлена условная логика для AssociationFilter
- [x] Обновлены тесты `createListViewService.test.ts` (8 тестов)
- [x] Обновлены тесты `sites.test.ts` (6 тестов)
- [x] Все 14 тестов проходят
- [x] Создана документация `43-sites-filter-fix.md`

---

**Дата:** 2026-05-04  
**Автор:** Cascade AI  
**Версия:** 1.0
