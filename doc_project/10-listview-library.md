# 🧰 Библиотека методов Visary ListView

## 📋 Описание

Переиспользуемое ядро для всех методов Visary ListView API
(`POST /api/visary/listview/{mnemonic}`). Любой новый справочник
(объекты, организации, покупатели, ...) добавляется **тремя шагами**
без копипаста сетевой логики, парсинга ответа и lazy-load машины.

> 💡 Контекст и причина рефакторинга: `doc_project/plan-listview-library.md`.
>
> Базовый слой API (`visaryApi.ts`) и принципы lazy-load описаны в
> `08-visary-api-integration.md` и `09-lazy-loaded-select.md`.

---

## 🏗️ Архитектура (3 слоя)

```
┌──────────────────────────────────────────────┐
│  UI: ImportForm.tsx                          │
│       │                                      │
│       ▼                                      │
│  hooks/useProjects, useSites, ...            │  ← тонкие обёртки
│       │                                      │
│       ▼                                      │
│  hooks/useListView<TItem>(service)           │  ← generic lazy-load
│       │                                      │
│       ▼                                      │
│  services/listView/entities/<name>.ts        │  ← per-entity config + mapper
│       │                                      │
│       ▼                                      │
│  services/listView/createListViewService.ts  │  ← фабрика fetch+parse
│       │                                      │
│       ▼                                      │
│  services/visaryApi.ts (visaryPost)          │  ← низкоуровневый fetch + auth
└──────────────────────────────────────────────┘
```

### Файлы

```
src/
├── services/
│   ├── visaryApi.ts                   # без изменений
│   └── listView/
│       ├── index.ts                   # публичный re-export всех частей
│       ├── types.ts                   # ListViewServiceConfig, ListViewQuery, ...
│       ├── parseListViewResponse.ts   # generic парсер Data/Items/items
│       ├── createListViewService.ts   # фабрика сервисов + buildListViewRequestBody
│       ├── entities/
│       │   ├── projects.ts            # mnemonic + columns + toProjectItem
│       │   └── sites.ts               # + buildSitesQueryByProject
│       └── __tests__/
│           ├── parseListViewResponse.test.ts
│           ├── createListViewService.test.ts
│           └── sites.test.ts
└── hooks/
    ├── useListView.ts                 # generic lazy-load хук
    ├── useProjects.ts                 # тонкая обёртка
    └── useSites.ts                    # обёртка с фильтром по projectId
```

---

## ✅ Как добавить новый ListView-эндпоинт (3 шага)

### Шаг 1. Сырой и UI-тип в `types/listView.ts`

```ts
// types/listView.ts
export interface OrganizationRaw {
  ID: number;
  Title?: string | null;
  INN?: string | null;
  // ... PascalCase, как в Visary API
}

export interface OrganizationItem {
  id: number;
  title: string;
  inn: string;
  raw?: OrganizationRaw;
}
```

### Шаг 2. Адаптер сущности в `services/listView/entities/organizations.ts`

```ts
import { createListViewService } from '../createListViewService';
import type { ListViewMapper } from '../types';
import type { OrganizationRaw, OrganizationItem } from '../../../types/listView';

export const ORGANIZATION_COLUMNS = ['ID', 'Title', 'INN'];

// ⚠️ Используем ||, не ??: пустая строка от backend → fallback тоже сработает.
export const toOrganizationItem: ListViewMapper<OrganizationRaw, OrganizationItem> = (raw) => ({
  id: raw.ID,
  title: raw.Title || `Организация #${raw.ID}`,
  inn: raw.INN || '',
  raw,
});

export const organizationsService = createListViewService<OrganizationRaw, OrganizationItem>({
  mnemonic: 'organization',
  columns: ORGANIZATION_COLUMNS,
  toItem: toOrganizationItem,
  logTag: '[organizations]',
});
```

### Шаг 3. Хук-обёртка `hooks/useOrganizations.ts`

```ts
import { organizationsService } from '../services/listView/entities/organizations';
import { useListView, type UseListViewState } from './useListView';
import type { OrganizationItem } from '../types/listView';

export const useOrganizations = (): UseListViewState<OrganizationItem> =>
  useListView(organizationsService, { logTag: '[useOrganizations]' });
```

Готово. UI получает тот же контракт `{ data, status, error, totalCount, load, refetch }`,
что у `useProjects` — Select подключается так же.

> 📝 Опционально: добавь unit-тесты на маппер в
> `src/services/listView/__tests__/organizations.test.ts` (особенно edge-cases с
> пустыми/`null`-значениями) и не забудь экспорт из `services/listView/index.ts`.

---

## 🔁 Зависимые Select (фильтр по выбранной сущности)

Когда список зависит от другого выбора (например, **объекты** строительства
показываются для **выбранного проекта**), используй `query`-параметр `useListView`:

```ts
// hooks/useSites.ts
import { useMemo } from 'react';
import { sitesService, buildSitesQueryByProject } from '../services/listView/entities/sites';
import { useListView, type UseListViewState } from './useListView';
import type { SiteItem } from '../types/listView';

export function useSites(projectId: number | null): UseListViewState<SiteItem> {
  // ⚠️ useMemo по projectId — стабилизирует ссылку на query.
  // useListView сравнивает её по identity и при смене сбрасывает кэш в idle.
  const query = useMemo(
    () => (projectId !== null ? buildSitesQueryByProject(projectId) : undefined),
    [projectId],
  );

  return useListView(sitesService, { query, logTag: '[useSites]' });
}
```

### ⚠️ Важно
- **Стабилизируй ссылку** через `useMemo` — иначе `query` будет новым на каждом
  рендере, и `useListView` будет бесконечно сбрасывать кэш в `idle`.
- При смене ключа (`projectId`) активный запрос автоматически отменяется
  через `AbortController`, и Select переходит в `idle` — следующий `load()`
  дёрнет новый запрос с новым фильтром.

---

## ❌ Типичные ошибки

### Ошибка 1: дублирование `fetch + parse` логики в каждом сервисе

```ts
// НЕПРАВИЛЬНО — копипаст из projectsService при добавлении organizationsService
export async function fetchOrganizations(...) {
  const request: ListViewRequest = { Mnemonic: 'organization', ... };
  const raw = await visaryPost<ListViewResponse<OrganizationRaw>>('/listview/organization', request);
  return parseOrganizationsResponse(raw);
}
```

**Почему плохо:**
- Каждый новый эндпоинт = новый сервис + новый парсер ответа = 60+ строк копипаста
- Если изменится формат ответа Visary — править придётся в **N местах**
- Логи и обработка `Data`/`Items`-варианта легко расходятся

**Правильно:** `createListViewService(config)` — generic-фабрика, на потребителе
только `mnemonic + columns + toItem`.

### Ошибка 2: `??` вместо `||` для fallback'ов

```ts
// БАГ — пустая строка пройдёт
title: raw.Title ?? `Объект #${raw.ID}`;   // raw.Title === '' → ''
```

**Правильно:** `title: raw.Title || 'Объект #${raw.ID}'`. Пойман unit-тестом
ещё на этапе `toProjectItem` — повторять урок не надо.

### Ошибка 3: `query` без `useMemo`

```tsx
// НЕПРАВИЛЬНО
const { data } = useListView(sitesService, {
  query: { extraFilter: `[["ProjectID","=",${projectId}]]` },   // ❌ новая ссылка каждый рендер
});
```

**Что произойдёт:** `useListView` детектирует новую ссылку → сбрасывает в `idle` →
бесконечный цикл «idle → load → success → re-render → idle → ...».

**Правильно:**
```tsx
const query = useMemo(() => ({ extraFilter: `...${projectId}...` }), [projectId]);
const { data } = useListView(sitesService, { query });
```

### Ошибка 4: вызов `load()` в `useEffect` (eager loading)

```tsx
// НЕПРАВИЛЬНО — делает хук eager, теряем смысл lazy
useEffect(() => { load(); }, [load]);
```

**Правильно:** вызывать `load()` строго в `onOpen` Select-а или по другому
явному пользовательскому действию.

### Ошибка 5: ручное чтение токена при импорте сервиса

```ts
// НЕПРАВИЛЬНО — ломает unit-тесты под Node
const TOKEN = import.meta.env.VITE_VISARY_API_TOKEN;
```

**Правильно:** `visaryApi.ts` уже делает это лениво в `getToken()`. Адаптер
сущности **не должен** трогать токен или env вообще — это забота низкого уровня.

---

## 📍 Применение в проекте

| Слой | Файл | Что предоставляет |
|------|------|-------------------|
| Generic-types | `KiloImportService.Web/src/services/listView/types.ts` | `ListViewServiceConfig`, `ListViewQuery`, `ListViewService<T>`, `ListViewMapper`, `ListViewResponseRaw<T>` |
| Generic-парсер | `KiloImportService.Web/src/services/listView/parseListViewResponse.ts` | `parseListViewResponse(raw, toItem)` |
| Generic-фабрика | `KiloImportService.Web/src/services/listView/createListViewService.ts` | `createListViewService(config)`, `buildListViewRequestBody` |
| Адаптер: проекты | `KiloImportService.Web/src/services/listView/entities/projects.ts` | `projectsService`, `toProjectItem`, `PROJECT_COLUMNS` |
| Адаптер: объекты | `KiloImportService.Web/src/services/listView/entities/sites.ts` | `sitesService`, `toSiteItem`, `buildSitesQueryByProject` |
| Generic-хук | `KiloImportService.Web/src/hooks/useListView.ts` | `useListView<T>(service, options?)` |
| Хук-обёртка | `KiloImportService.Web/src/hooks/useProjects.ts` | `useProjects()` (тонкая обёртка) |
| Хук-обёртка | `KiloImportService.Web/src/hooks/useSites.ts` | `useSites(projectId)` |
| Backward-compat | `KiloImportService.Web/src/services/projectsService.ts` | `fetchProjects`, `parseProjectsResponse`, `toProjectItem` (помечены `@deprecated`) |

---

## 🧪 Тесты

```powershell
# generic-парсер (6 кейсов)
npx tsx src/services/listView/__tests__/parseListViewResponse.test.ts

# generic-фабрика тела запроса (6 кейсов)
npx tsx src/services/listView/__tests__/createListViewService.test.ts

# адаптер проектов (10 кейсов, сохранены из старой версии)
npx tsx src/services/__tests__/projectsService.test.ts

# адаптер объектов (6 кейсов)
npx tsx src/services/listView/__tests__/sites.test.ts
```

Сетевая часть (`fetch`) **не покрывается** unit-тестами — она зависит от
`import.meta.env` Vite и реального токена. Проверяется руками через UI с
многослойным логированием (см. `08-visary-api-integration.md`).

---

## 🎯 Чек-лист «как добавить новый ListView-эндпоинт»

- [ ] Описать сырой тип `XxxRaw` в `types/listView.ts` (PascalCase, как в API)
- [ ] Описать UI-тип `XxxItem` (camelCase, нормализованный)
- [ ] Создать `services/listView/entities/<name>.ts`:
  - [ ] Константа `XXX_COLUMNS`
  - [ ] Маппер `toXxxItem` через `||` (не `??`)
  - [ ] `xxxService = createListViewService({ mnemonic, columns, toItem, logTag })`
  - [ ] Опционально: helper `buildXxxQueryByYyy(...)` для зависимых фильтров
- [ ] Создать хук-обёртку `hooks/useXxx.ts` через `useListView(xxxService)`
- [ ] Re-export в `services/listView/index.ts` (опционально)
- [ ] Unit-тесты в `services/listView/__tests__/<name>.test.ts`
- [ ] Запуск тестов: `npx tsx src/services/listView/__tests__/<name>.test.ts` — все зелёные
- [ ] Подключить хук в UI (Select c `onOpen={p => p.open && load()}`)
- [ ] При первом запросе к реальному API — посмотреть лог
      `[VisaryAPI] ← 200 ... response: {Data: [...], Total: N}` и убедиться,
      что ключи действительно `Data/Total` (или поправить `parseListViewResponse`)
