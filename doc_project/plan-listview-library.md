# 🏗️ План: библиотека методов Visary ListView для разных импортов

> **Статус**: ✅ выполнено (28/28 тестов, `tsc --noEmit` ✓, `eslint` ✓, `vite build` ✓, контракт `useProjects` сохранён)
> **Цель**: вынести логику работы с Visary ListView в переиспользуемое ядро, чтобы добавление нового справочника (объекты строительства, организации, покупатели, ...) сводилось к написанию **только** конфигурации эндпоинта и маппера, без копипаста сервиса/хука.

---

## ✅ Чек-лист

### Этап 1. Анализ и проектирование
- [x] Изучить текущую реализацию `projectsService` + `useProjects`
- [x] Изучить документацию `08-visary-api-integration.md` и `09-lazy-loaded-select.md`
- [x] Согласовать архитектуру: **generic core + per-entity adapters**
- [x] Согласовать имена и расположение файлов с пользователем

### Этап 2. Generic-ядро `services/listView/`
- [x] Создать `src/services/listView/types.ts` — общие типы (`ListViewServiceConfig`, `ListViewMapper`, `ListViewResult`, `ListViewQuery`, `ListViewResponseRaw<T>`, `ListViewService<T>`)
- [x] Создать `src/services/listView/parseListViewResponse.ts` — generic-парсер `Data/Items/items` + `Total/TotalCount/totalCount`
- [x] Создать `src/services/listView/createListViewService.ts` — фабрика `createListViewService<TRaw, TItem>(config)` + `buildListViewRequestBody`
- [x] Покрыть generic-ядро unit-тестами в `src/services/listView/__tests__/` (12 кейсов)

### Этап 3. Адаптеры сущностей `services/listView/entities/`
- [x] Перенести `projectsService` на новое ядро → `entities/projects.ts` (config + mapper + `PROJECT_COLUMNS`)
- [x] Шаблон для `entities/sites.ts` (mnemonic `constructionsite`, фильтр `buildSitesQueryByProject(projectId)`, добавлен `ConstructionSiteRaw` в `types/listView.ts`)
- [ ] ~~Шаблон для `entities/organizations.ts`~~ — отложено: добавим при первом реальном использовании (нужны точные имена колонок Visary)
- [x] Сохранить обратную совместимость: `services/projectsService.ts` стал `@deprecated`-shim'ом, реэкспортит `fetchProjects` / `parseProjectsResponse` / `toProjectItem` из нового места
- [x] `services/listView/index.ts` — публичный re-export

### Этап 4. Generic-хук `hooks/useListView.ts`
- [x] Реализовать `useListView<TItem>(service, options?)` со state-machine `idle/loading/success/error` + `AbortController`
- [x] Поддержать параметр запроса `query` через `options.query`; при смене ссылки — сброс кэша в `idle` + abort активного запроса (для зависимых Select-ов)
- [x] Cleanup: отмена запроса при размонтировании компонента
- [x] Переписать `hooks/useProjects.ts` как тонкую обёртку `useListView(projectsService, { logTag: '[useProjects]' })` — контракт `{ data, status, error, totalCount, load, refetch }` сохранён
- [x] Добавить `hooks/useSites.ts` как пример переиспользования (с фильтром по `projectId`)
- [x] Сохранить логи переходов статуса с `logTag` (важно для диагностики)

### Этап 5. Тесты
- [x] Generic `parseListViewResponse` — все варианты ключей + приоритеты (6 кейсов)
- [x] `buildListViewRequestBody` — корректно собирает body запроса по `config` + `query` (6 кейсов)
- [x] Маппер `toProjectItem` — существующие 10 кейсов перенацелены на shim, все зелёные
- [x] Маппер `toSiteItem` + `buildSitesQueryByProject` — 6 кейсов
- [x] Запуск: **28 тестов, 0 fail** через `npx tsx ...`

### Этап 6. Документация
- [x] Создать `10-listview-library.md` — гайд «как добавить новый ListView-эндпоинт за 3 шага»
- [x] Обновить чек-лист в `08-visary-api-integration.md` (раздел «Чек-лист для добавления нового ListView-эндпоинта»)
- [x] Обновить `02-prototype-architecture.md` — структура папок
- [x] Обновить `README.md` индекс документации (добавлены ссылки на `10-listview-library.md` и план)

### Этап 7. Интеграция в UI
- [x] `ImportForm.tsx` — продолжает использовать `useProjects()` без изменений (контракт сохранён, проверено `grep`)
- [x] `npm install` выполнен; `tsc --noEmit -p tsconfig.app.json` ✓ (0 ошибок)
- [x] `npm run lint` ✓ (0 ошибок). Поймана и исправлена ошибка `react-hooks/refs` в `useListView` — обновление `queryRef.current` перенесено из фазы рендера в `useEffect`
- [x] `npm run build` ✓ (vite + tsc -b прошли, 920 модулей)
- [ ] Smoke-тест в браузере: открыть Select «Проект», убедиться что данные грузятся (требует валидного `VITE_VISARY_API_TOKEN`)

---

## 🏗️ Целевая архитектура (реализована)

### Структура папок

```
src/
├── services/
│   ├── visaryApi.ts                 # без изменений (низкий уровень)
│   ├── projectsService.ts           # @deprecated shim для обратной совместимости
│   └── listView/
│       ├── index.ts                 # публичный re-export
│       ├── types.ts                 # ListViewServiceConfig, ListViewMapper, ...
│       ├── parseListViewResponse.ts # generic парсер ответа
│       ├── createListViewService.ts # фабрика сервисов + buildListViewRequestBody
│       ├── entities/
│       │   ├── projects.ts          # mnemonic + columns + toProjectItem
│       │   └── sites.ts             # + buildSitesQueryByProject(projectId)
│       └── __tests__/
│           ├── parseListViewResponse.test.ts   # 6 кейсов
│           ├── createListViewService.test.ts   # 6 кейсов
│           └── sites.test.ts                   # 6 кейсов
└── hooks/
    ├── useListView.ts               # generic lazy-load с idle/loading/success/error + AbortController
    ├── useProjects.ts               # тонкая обёртка
    └── useSites.ts                  # обёртка с фильтром по projectId
```

### Реализованный API

```ts
// services/listView/index.ts
export {
  createListViewService, buildListViewRequestBody,
  parseListViewResponse,
  // адаптеры
  projectsService, toProjectItem, PROJECT_COLUMNS,
  sitesService, toSiteItem, SITE_COLUMNS, buildSitesQueryByProject,
};
export type {
  ListViewQuery, ListViewResult, ListViewService,
  ListViewServiceConfig, ListViewMapper, ListViewResponseRaw,
};
```

```ts
// hooks/useListView.ts
export function useListView<TItem>(
  service: ListViewService<TItem>,
  options?: { query?: ListViewQuery; logTag?: string },
): UseListViewState<TItem>;  // { data, status, error, totalCount, load, refetch }
```

### Не сломанные контракты
- `useProjects()` отдаёт ровно `{ data, status, error, totalCount, load, refetch }` — `ImportForm.tsx` не правился
- `services/projectsService.ts` сохраняет экспорты `fetchProjects`, `parseProjectsResponse`, `toProjectItem` (помечены `@deprecated`) — старые тесты зелёные без правок

---

## 🎯 Приёмочные критерии

- [x] Чтобы добавить новый ListView, нужно: создать `entities/<name>.ts` (mnemonic + columns + mapper) и `hooks/use<Name>.ts` (одна строка)
- [x] Ни в одном `entities/<name>.ts` нет дублирования логики `fetch`, `parseResponse`, `AbortController`, статус-машины
- [x] Все существующие тесты `projectsService` зелёные после переноса (10/10)
- [x] Новый тест на маппер `sites` написан и зелёный (6/6)
- [x] `ImportForm.tsx` работает без изменений (контракт `useProjects` сохранён, проверено grep'ом)
- [x] `tsc --noEmit -p tsconfig.app.json` ✓ (0 ошибок)
- [x] `npm run lint` ✓ (0 ошибок)
- [x] `npm run build` ✓ (920 модулей собраны)
- [x] Документация обновлена (`08`, `02`, новый `10-listview-library.md`, `README.md`)

---

## 📝 Лог прогресса

| Дата | Этап | Что сделано |
|------|------|-------------|
| 2026-04-29 | 1 | План составлен, текущая реализация изучена |
| 2026-04-29 | 2 | Generic-ядро: `types.ts`, `parseListViewResponse.ts`, `createListViewService.ts` + 12 тестов |
| 2026-04-29 | 3 | Адаптеры `entities/projects.ts` (перенос) + `entities/sites.ts` (новый, с `buildSitesQueryByProject`); `ConstructionSiteRaw` в `types/listView.ts`; shim `projectsService.ts`; `services/listView/index.ts` |
| 2026-04-29 | 4 | `useListView<T>` + рефакторинг `useProjects` в обёртку + новый `useSites(projectId)`; добавлена логика сброса кэша при смене `query` + cleanup на unmount |
| 2026-04-29 | 5 | Прогон тестов: **28 passed / 0 failed** (parseListViewResponse 6, createListViewService 6, projectsService 10, sites 6) |
| 2026-04-29 | 6 | `10-listview-library.md` создан; `08`, `02`, `README.md` обновлены |
| 2026-04-29 | 7 | `npm install` (505 пакетов), `tsc --noEmit` ✓, `eslint` ✓, `vite build` ✓; пойман и исправлен `react-hooks/refs` в `useListView` (обновление `queryRef.current` перенесено в `useEffect`) |

---

## 🔭 Что дальше (вне scope этого плана)

- При первом реальном запросе к `constructionsite` через `sitesService` — сверить имена полей в логе ответа Visary с предположением в `ConstructionSiteRaw`/`SITE_COLUMNS`. Если ключи отличаются — поправить и добавить регрессионный тест (как было с `Data` vs `Items` для проектов).
- При появлении новых эндпоинтов (`organization`, `buyers`, `paymentschedule`, ...) — следовать чек-листу из `10-listview-library.md`, шаги 1-3.
- Если зависимых Select-ов станет больше двух — рассмотреть вынесение паттерна «query через `useMemo`» в утилиту `useListViewQuery(deps)`.
- Smoke-тест в браузере на реальном Visary API (с `VITE_VISARY_API_TOKEN` в `.env.local`) — финальная проверка, что лог-цепочка `[useProjects] → [VisaryAPI] → [Vite proxy] ← Data/Total` корректно превращается в опции Select.
- При наличии backend `KiloImportService.Server` поднять docker-compose и проверить интеграцию end-to-end.
