# 📚 Документация проекта KiloImportService

Документация по реализации сервиса импорта файлов (CSV/XLS/XLSB/XLSX) в систему Visary.

## 📂 Структура

| Файл | Содержание |
|------|------------|
| [01-alfa-core-components-api.md](./01-alfa-core-components-api.md) | 🎨 API оригинальных компонентов `@alfalab/core-components` — корректные пропы и типичные ошибки |
| [02-prototype-architecture.md](./02-prototype-architecture.md) | 🏗️ Архитектура UI-прототипа: структура папок, типизация, mock-данные |
| [03-import-flow.md](./03-import-flow.md) | 🔄 Поток импорта в UI: stages, состояния, симуляция SignalR |
| [04-css-patterns.md](./04-css-patterns.md) | 🎨 CSS-паттерны прототипа (таблица отчёта, фильтр-теги, карточки сводки) |
| [05-file-format-detection.md](./05-file-format-detection.md) | 🔍 Автоопределение формата файла по расширению |
| [06-import-types-registry.md](./06-import-types-registry.md) | 📋 Реестр типов импорта (открытый список через Select) |
| [07-import-datetime-metadata.md](./07-import-datetime-metadata.md) | 🕐 `startedAt`/`completedAt` как информационные поля (не вводятся пользователем) |
| [08-visary-api-integration.md](./08-visary-api-integration.md) | 🔌 Интеграция с Visary ListView API (proxy, Bearer, маппинг, тесты, многослойное логирование) |
| [09-lazy-loaded-select.md](./09-lazy-loaded-select.md) | 🎯 Lazy-load паттерн для Select (`idle/loading/success/error`, `onOpen`, `AbortController`) |
| [10-listview-library.md](./10-listview-library.md) | 🧰 Библиотека методов Visary ListView (generic-ядро + per-entity адаптеры, как добавить новый эндпоинт за 3 шага) |
| [11-react-refs-discipline.md](./11-react-refs-discipline.md) | 🪝 Дисциплина `useRef` в React 19: правила записи `ref.current`, паттерн «latest value» через `useEffect`, типичные ошибки |
| [12-ef-core-migrations.md](./12-ef-core-migrations.md) | 🗄️ EF Core миграции для service-db: guard `EF.IsDesignTime`, `MigrationsHistoryTable`, partial unique index, команды dotnet-ef |
| [13-vite-proxy-backend.md](./13-vite-proxy-backend.md) | 🔌 Vite proxy для собственного backend: префиксы `/api/imports`, `/hubs` с `ws: true`, факторинг через `backendProxy()` helper |
| [14-imports-backend-integration.md](./14-imports-backend-integration.md) | 🔄 Полный контур UI ↔ backend: importsService + importsHub + useImportSession + DTO ↔ UI mapper |
| [15-signalr-progress.md](./15-signalr-progress.md) | 📡 SignalR прогресс импорта: `StageProgress` с троттлингом ≈50/файл, `JoinSession`, autoReconnect, защита от старых событий |
| [16-import-cancellation.md](./16-import-cancellation.md) | 🛑 Отмена сессии: Singleton-реестр `CancellationTokenSource`, `MarkCancelledAsync` с независимым ct, парсеры с `catch (OCE) { throw }` |
| [17-backend-tests-xunit.md](./17-backend-tests-xunit.md) | 🧪 Backend xUnit-тесты: in-memory EF, SkippableFact для ClosedXML/SkiaSharp, паттерн уникальной БД на тест |
| [18-projects-cache.md](./18-projects-cache.md) | 🗂️ Кэш проектов Visary + поиск-as-you-type, **probe-then-sync** стратегия (sync только если кэш пуст), graceful fallback при Visary outage |
| [19-net10-deployment-gotchas.md](./19-net10-deployment-gotchas.md) | 🐛 5 граблей при первом деплое .NET 10 + Alpine: addgroup, Swashbuckle 10.1.7, JsonDocument в InMemory, TLS revocation, varchar(64) → 255 |
| [20-select-with-search.md](./20-select-with-search.md) | 🔍 Alfa Select + showSearch + динамические опции: пиннинг выбранной опции, очистка search на onChange |
| [21-sites-by-project.md](./21-sites-by-project.md) | 🏗️ Получение объектов строительства по проекту: специальный эндпоинт `/onetomany/Project`, AssociationFilter, полный список колонок |
| [22-update-finishing-material.md](./22-update-finishing-material.md) | 🏗️ Обновление типа отделки объекта строительства через Visary CRUD API |
| [23-finmodel-import.md](./23-finmodel-import.md) | 📊 Импорт "Финмодель": обновление параметров объекта строительства (тип отделки) из Excel |
| [24-finmodel-testing-and-fixes.md](./24-finmodel-testing-and-fixes.md) | 🧪 Тестирование и исправление ошибок: in-memory БД, Docker, типичные проблемы при добавлении мапперов |
| [25-ui-project-options-display.md](./25-ui-project-options-display.md) | 📋 Отображение проектов в Select без лишних символов: убран `code`, одни и те же файлы можно загружать несколько раз |
| [26-troubleshooting.md](./26-troubleshooting.md) | 🛠️ Решение проблем: запуск backend, проверка БД, отладка импорта "Финмодель" |
| [27-checklists.md](./27-checklists.md) | ✅ Чек-листы: запуск цикла, добавление маппера, деплой |
| [50-visary-api-new-methods.md](./50-visary-api-new-methods.md) | 🔌 Новые методы Visary API Client: 11 ListView + 12 CRUD методов для Room, Deal, ТЭП, ЗУ, ДДУ, Секция, Организация, PercentBet, Project |
| [51-sites-sync-bugs-and-token-update.md](./51-sites-sync-bugs-and-token-update.md) | 🐛 Два бага синхронизации объектов: `new HttpClient()` в Docker + отсутствие `/api/sites` в Vite proxy; обновление Bearer token |
| [53-visary-api-schema-audit.md](./53-visary-api-schema-audit.md) | 🔍 Snapshot 19 сущностей Visary API в Postgres (`visary_api.fields`), сравнение с DTO, 442 поля; скрипт `audit-visary-api.ps1` |
| [54-visary-token-hot-reload.md](./54-visary-token-hot-reload.md) | 🔐 Bearer-токен в `appsettings.Local.json` (gitignore) + `IOptionsMonitor<VisaryOptions>` ⇒ замена токена без рестарта |
| [55-visary-proxy-controllers.md](./55-visary-proxy-controllers.md) | 🔌 `/api/visary/*` контроллеры: registry-pattern для 8 справочников, явные actions для 11 основных сущностей; добавление нового справочника = 1 строка |
| [56-visary-dto-deserialization-pitfalls.md](./56-visary-dto-deserialization-pitfalls.md) | ⚠️ Ловушки десериализации: `Status`/`RoomCategory`/`MainSource` приходят разными типами → `JsonElement?`; `*Raw` vs `*Full` для listview vs crud |
| [57-visary-api-testing.md](./57-visary-api-testing.md) | 🧪 Три уровня тестов Visary API: 39 контракт-тестов клиентов, 18 тестов контроллеров, 38 live smoke-тестов с автоматическим skip при истёкшем токене |
| [52-project-dropdown-empty-query.md](./52-project-dropdown-empty-query.md) | 🎯 Dropdown «Проект»: пустой запрос возвращает первую страницу кэша (probe-then-sync не блокируется при недоступном Visary) |
| [60-select-desktop-vs-responsive.md](./60-select-desktop-vs-responsive.md) | 🎨 `SelectDesktop` вместо `SelectResponsive`: всегда классический dropdown, не Bottom Sheet на узких экранах |
| [61-finmodel-file-level-column-error.md](./61-finmodel-file-level-column-error.md) | 🛑 FinModel: file-level ошибка `column_not_found` со списком обнаруженных колонок вместо row-spam на 2782 строки |
| [62-vertical-keyvalue-layout.md](./62-vertical-keyvalue-layout.md) | 🎨 Вертикальный key-value layout (`Inputs C/H+`) + управляющий лист `Control` для числа этапов — `FileLayoutHint.KeyValueVertical` |
| [63-site-finishing-material-update-crud.md](./63-site-finishing-material-update-crud.md) | 🔌 Обновление типа отделки через PATCH `/crud/constructionsite/{id}?forceUpdate=false` (вместо PUT/listview → 405 / 500) |
| [64-dynamic-finishing-material-dictionary.md](./64-dynamic-finishing-material-dictionary.md) | 🔌 Справочник «Тип отделки» из Visary (`listview/finishingmaterial`) вместо хардкода Title→ID — переиспользуемый метод в `IListViewClient` |
| [65-merge-integration-with-shared-helpers.md](./65-merge-integration-with-shared-helpers.md) | 🔧 Merge feature-ветки с main: убираем дубликаты `private GetCrudAsync` / DTO в пользу `GetCrudByIdAsync<T>` + `Dto/Generated/`, единый namespace `VisaryMnemonics`, обновление доков |
| [66-finmodel-estate-class.md](./66-finmodel-estate-class.md) | 🏘️ Финмодель: добавлен параметр «Класс жилья» (Visary `EstateClass`) — `UpdateSiteEstateClassAsync` + динамический справочник через `ListEstateClassesAsync`, обобщённые helpers `TryLoadDictionaryAsync` / `ResolveValue` |

## 🎯 Контекст проекта

**Проект**: Сервис импорта файлов в Visary (Альфа Банк - Управление проектами)

**Технологический стек**:
- **Backend**: .NET 10 Web API + PostgreSQL + SignalR
- **Frontend**: React 18-19 + TypeScript + `@alfalab/core-components`
- **Контейнеризация**: Docker / Docker Compose

**Основной документ архитектуры**: `../import-excel-service-architecture.md`

**UI-прототип**: `../KiloImportService.Web/`

## ⚠️ Важно при работе

1. **Всегда** используй компоненты из `@alfalab/core-components/<имя>` — это оригинальные компоненты Альфа-Банка
2. **Не используй** Tailwind / Material UI / Ant Design — только Alfa-компоненты
3. **Перед** добавлением пропа компоненту — проверь его типы в `node_modules/@alfalab/core-components-<name>/esm/Component.d.ts`
4. **При** ошибках типов — см. документ `01-alfa-core-components-api.md`
