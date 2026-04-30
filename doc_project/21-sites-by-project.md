# 🏗️ Получение объектов строительства по проекту

## 📋 Описание

Метод для получения списка объектов строительства (construction sites), отфильтрованных по выбранному проекту. Использует специальный эндпоинт Visary ListView API `/onetomany/Project` с фильтром `AssociationFilter`.

> 🔁 См. также: `08-visary-api-integration.md`, `09-lazy-loaded-select.md`, `10-listview-library.md`.

---

## 🔌 API эндпоинт

```
POST https://isup-alfa-test.k8s.npc.ba/api/visary/listview/constructionsite/onetomany/Project
```

### Тело запроса

```json
{
  "Mnemonic": "constructionsite",
  "PageSkip": 0,
  "PageSize": 50,
  "Columns": [
    "ID", "Date", "Project", "Title", "Location",
    "ConstructionProjectNumber", "Address", "Type",
    "EstateClass", "BuildingMaterial", "FinishingMaterial",
    "TotalArea", "StartDate", "FinishDate", "MonthDuration",
    "TempOfConstruction", "ClaimedCost", "AreaCost",
    "RiskFund", "Borrower", "ComplexID", "Town",
    "Comment", "RowVersion"
  ],
  "Sorts": "[{\"selector\":\"ID\",\"desc\":false}]",
  "Hidden": false,
  "ExtraFilter": null,
  "SearchString": "",
  "AssociationFilter": {
    "AssociatedId": 123,  // ID выбранного проекта
    "Filters": null
  }
}
```

### Заголовки

```
Content-Type: application/json
Authorization: Bearer <JWT_TOKEN>
```

---

## ✅ Правильная реализация

### Шаг 1: Типы данных

```ts
// types/listView.ts
export interface ConstructionSiteRaw {
  ID: number;
  Date?: string | null;
  Project?: string | null;
  Title?: string | null;
  Location?: string | null;
  ConstructionProjectNumber?: string | null;
  Address?: string | null;
  Type?: string | null;
  EstateClass?: string | null;
  BuildingMaterial?: string | null;
  FinishingMaterial?: string | null;
  TotalArea?: number | null;
  StartDate?: string | null;
  FinishDate?: string | null;
  MonthDuration?: number | null;
  TempOfConstruction?: string | null;
  ClaimedCost?: number | null;
  AreaCost?: number | null;
  RiskFund?: number | null;
  Borrower?: string | null;
  ComplexID?: number | null;
  Town?: string | null;
  Comment?: string | null;
  RowVersion?: string | number | null;
}

export interface SiteItem {
  id: number;
  title: string;
  address: string;
  constructionProjectNumber: string;
  type: string;
  totalArea: number | null;
  raw?: ConstructionSiteRaw;
}
```

### Шаг 2: Сервис с AssociationFilter

```ts
// services/listView/entities/sites.ts
import { createListViewService } from '../createListViewService';
import type { ListViewMapper, ListViewQuery } from '../types';
import type { ConstructionSiteRaw, SiteItem } from '../../../types/listView';

export const SITE_COLUMNS = [
  'ID', 'Date', 'Project', 'Title', 'Location',
  'ConstructionProjectNumber', 'Address', 'Type',
  'EstateClass', 'BuildingMaterial', 'FinishingMaterial',
  'TotalArea', 'StartDate', 'FinishDate', 'MonthDuration',
  'TempOfConstruction', 'ClaimedCost', 'AreaCost',
  'RiskFund', 'Borrower', 'ComplexID', 'Town',
  'Comment', 'RowVersion',
];

export const toSiteItem: ListViewMapper<ConstructionSiteRaw, SiteItem> = (raw) => ({
  id: raw.ID,
  title: raw.Title || `Объект #${raw.ID}`,
  address: raw.Address || '',
  constructionProjectNumber: raw.ConstructionProjectNumber || '',
  type: raw.Type || '',
  totalArea: raw.TotalArea ?? null,
  raw,
});

export const sitesService = createListViewService<ConstructionSiteRaw, SiteItem>({
  mnemonic: 'constructionsite',
  pathSuffix: '/onetomany/Project',  // 👈 Специальный эндпоинт
  columns: SITE_COLUMNS,
  defaultSort: '[{"selector":"ID","desc":false}]',
  toItem: toSiteItem,
  logTag: '[sites]',
});

export function buildSitesQueryByProject(
  projectId: number,
  query: Omit<ListViewQuery, 'associationFilter'> = {},
): ListViewQuery {
  return {
    ...query,
    associationFilter: {
      AssociatedId: projectId,
      Filters: null,
    },
  };
}
```

### Шаг 3: Хук для UI

```ts
// hooks/useSites.ts
import { useMemo } from 'react';
import { buildSitesQueryByProject, sitesService } from '../services/listView/entities/sites';
import { useListView } from './useListView';
import type { UseListViewState } from './useListView';
import type { SiteItem } from '../types/listView';

export function useSites(projectId: number | null): UseListViewState<SiteItem> {
  const query = useMemo(
    () => (projectId !== null ? buildSitesQueryByProject(projectId) : undefined),
    [projectId],
  );

  const state = useListView(sitesService, {
    query,
    logTag: '[useSites]',
  });

  if (projectId === null) {
    return {
      ...state,
      load: () => console.info('[useSites] load() пропущен — projectId не выбран'),
    };
  }
  return state;
}
```

### Шаг 4: Использование в компоненте

```tsx
// components/ImportForm/ImportForm.tsx
import { useSites } from '../../hooks/useSites';

function ImportForm() {
  const [projectId, setProjectId] = useState<number | null>(null);
  const sites = useSites(projectId);

  useEffect(() => {
    if (projectId !== null) {
      sites.load();
    }
  }, [projectId]);

  return (
    <Select
      label="Объект строительства"
      disabled={projectId === null || sites.status === 'loading'}
      options={sites.data.map(site => ({
        key: String(site.id),
        content: site.title,
      }))}
      onChange={({ selected }) => {
        // Обработка выбора
      }}
    />
  );
}
```

---

## ⚙️ Конфигурация

### Frontend (.env.local)

```env
VITE_VISARY_API_URL=https://isup-alfa-test.k8s.npc.ba
VITE_VISARY_API_TOKEN=<JWT без префикса "Bearer ">
```

### Vite proxy (vite.config.ts)

```ts
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const visaryTarget = env.VITE_VISARY_API_URL || 'https://isup-alfa-test.k8s.npc.ba';

  return {
    server: {
      proxy: {
        '/api/visary': {
          target: visaryTarget,
          changeOrigin: true,
          secure: true,
          configure: logging('visary', visaryTarget),
        },
      },
    },
  };
});
```

---

## ❌ Типичные ошибки

### Ошибка 1: Использование старого формата с ExtraFilter

```ts
// НЕПРАВИЛЬНО — старый формат фильтрации
export function buildSitesQueryByProject(projectId: number): ListViewQuery {
  return {
    extraFilter: `[["ConstructionProjectID","=",${projectId}]]`,  // ❌
  };
}
```

**Правильно:** использовать `AssociationFilter` с `AssociatedId`.

### Ошибка 2: Отсутствие pathSuffix

```ts
// НЕПРАВИЛЬНО — без специального пути
export const sitesService = createListViewService({
  mnemonic: 'constructionsite',  // ❌ путь будет /listview/constructionsite
  columns: SITE_COLUMNS,
  toItem: toSiteItem,
});
```

**Правильно:** добавить `pathSuffix: '/onetomany/Project'`.

### Ошибка 3: Неполный список колонок

```ts
// НЕПРАВИЛЬНО — запрашиваем только часть полей
export const SITE_COLUMNS = ['ID', 'Title'];  // ❌
```

**Что произойдёт:** в `ConstructionSiteRaw` все остальные поля будут `undefined`, маппер вернёт пустые строки.

**Правильно:** запрашивать все необходимые колонки согласно API.

### Ошибка 4: Токен не задан

```
[VisaryAPI] ❌ VITE_VISARY_API_TOKEN не задан
```

**Решение:** создать `.env.local` с токеном (см. раздел Конфигурация).

---

## 🧪 Тестирование

### Ручное тестирование через curl

```bash
curl --location 'https://isup-alfa-test.k8s.npc.ba/api/visary/listview/constructionsite/onetomany/Project' \
--header 'Content-Type: application/json' \
--header 'Authorization: Bearer <YOUR_TOKEN>' \
--data '{
    "Mnemonic": "constructionsite",
    "PageSkip": 0,
    "PageSize": 50,
    "Columns": ["ID", "Title", "Address", "Type"],
    "Sorts": "[{\"selector\":\"ID\",\"desc\":false}]",
    "Hidden": false,
    "ExtraFilter": null,
    "SearchString": "",
    "AssociationFilter": {
        "AssociatedId": 123,
        "Filters": null
    }
}'
```

### Проверка в DevTools

1. Открой DevTools → Network
2. Выбери проект в форме импорта
3. Проверь запрос к `/api/visary/listview/constructionsite/onetomany/Project`
4. Убедись, что в теле запроса присутствует `AssociationFilter.AssociatedId`

---

## 📍 Применение в проекте

| Слой | Файл |
|------|------|
| Типы API | `KiloImportService.Web/src/types/listView.ts` |
| Типы ListView | `KiloImportService.Web/src/services/listView/types.ts` |
| Сервис | `KiloImportService.Web/src/services/listView/entities/sites.ts` |
| Хук | `KiloImportService.Web/src/hooks/useSites.ts` |
| Компонент | `KiloImportService.Web/src/components/ImportForm/ImportForm.tsx` |
| Vite proxy | `KiloImportService.Web/vite.config.ts` |

---

## 🎯 Чек-лист при добавлении нового ListView-метода

- [ ] Определены типы `*Raw` и `*Item` в `types/listView.ts`
- [ ] Создан список колонок `*_COLUMNS`
- [ ] Реализован маппер `to*Item` с fallback через `||` для строк
- [ ] Создан сервис через `createListViewService` с правильным `pathSuffix` (если нужен)
- [ ] Добавлена helper-функция `build*Query` для построения запроса с фильтрами
- [ ] Создан хук `use*` на базе `useListView`
- [ ] Токен `VITE_VISARY_API_TOKEN` задан в `.env.local`
- [ ] Vite proxy настроен для `/api/visary`
- [ ] Проверено в DevTools, что запрос уходит с правильным телом
- [ ] Логи в консоли показывают корректные данные
