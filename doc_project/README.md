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
| [54-visary-token-hot-reload.md](./54-visary-token-hot-reload.md) | 🔐 Bearer-токен Visary — единый источник в корневом `.env` (gitignored): docker-compose, Vite (`envDir: '..'`), backend (`DotEnvLoader`), live-тесты. Hot-reload отдан ради SSOT |
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
| [67-finmodel-indicators.md](./67-finmodel-indicators.md) | 📊 Финмодель: добавлен 3-й тип параметров — показатели (`ConstructionSiteIndicator` + Value по стадии). Подключены: «Площадь застройки», «Плотность застройки» (Stage=50 «Экспертиза»). Декларативный `Indicators[]` (расширяется одной строкой), обобщённый `ApplyIndicatorAsync`, `FilterByStringContains` + `Trim()` для Title с хвостовыми пробелами, `TryParseFlexibleDouble` |
| [68-rooms-import.md](./68-rooms-import.md) | 🏠 Импорт «Помещения» (`rooms`) по «Пример импорта.xlsx» / «Единая форма 3»: per-row сверка выбранного ОКСа (НПС+Этап+опц. РНС), группировка по листу = тип, fallback Kind по имени листа («Квартиры»→«Квартира»), `Section.Type=МЖД` дефолт, `RoomKind` из живого Visary API |
| [69-finmodel-address.md](./69-finmodel-address.md) | 🏠 Финмодель: добавлен параметр «Строительный адрес» (`Address`) — простой строковый атрибут Site через `UpdateSiteAddressAsync`, без справочника (отличие от FinishingMaterial / EstateClass — `Address = "string"`, не `VisaryRef`) |
| [70-wbs-api-foundation.md](./70-wbs-api-foundation.md) | 🏛️ WBS (ИСР) API клиент v0.1: `Wbs` мнемоника, `WbsRaw` / `WbsCreateRequest`, `CreateWbsAsync` + `GetWbsByProjectAsync`, smoke-тесты на проекте 4584. Code присваивается сервером (Глава 1 → 1.1, 1.2…). Маппер бюджета — следующий шаг |
| [71-finmodel-budget-import.md](./71-finmodel-budget-import.md) | 💰 Импорт «Финмодель → Себестоимость» (WBS v0.2): `BudgetSectionHint` для парсера, `BudgetReferenceProvider` (Title→Code, ~100 статей в коде), идемпотентный `ApplyBudgetAsync` (find/create/patch) + `PatchWbsAsync` `forceUpdate=true`. Повторный импорт не плодит дубликатов; суммы PATCH-аются |
| [72-multi-sheet-import.md](./72-multi-sheet-import.md) | 🗂️ Сквозной обзор многолистового XLSX-импорта — 5 мест кода (парсер, БД, пайплайн, маппер, UI), которые должны быть согласованы; миграция `(SessionId, Sheet, SourceRowNumber)`, per-sheet прогресс, race condition `setSession(prev =>)` |
| [73-import-history-page.md](./73-import-history-page.md) | 🗂️ Страница «История импортов»: `GET /api/imports` (skip/take/status/importTypeCode) + read-only детальный просмотр (переиспользует `SessionSummary` + `SessionRowsTable`), отдельный `useImportSessionDetail` без SignalR. Сброс `skip=0` при смене фильтров |
| [74-import-pdf-export.md](./74-import-pdf-export.md) | 📄 Экспорт сессий в PDF: чекбоксы в `HistorySessionsTable` (Set, не Array), `POST /api/imports/export-pdf` → `PDFsharp` + `PDFsharp-MigraDoc` (MIT). FontResolver регистрируется один раз под lock; в Dockerfile `apk add ttf-dejavu fontconfig` для кириллицы. Frontend: `response.blob()` + `<a download>` + `URL.revokeObjectURL` |
| [75-projectmanagement-developer-link.md](./75-projectmanagement-developer-link.md) | 🏗️ Привязка Организации-Застройщика к объекту через `projectmanagement`: 5-шаговый flow (1) PIN→org, (2) PM на сайте, (3) поиск в проекте через `/onetomany/Project` + `["Organization","contains","ID:{id}"]`, (4) max(ID) при нескольких или CREATE, (5) link. Новые методы `GetProjectManagementsBy{Site,Project}Async`/`CreateProjectManagementAsync`/`LinkProjectManagementToSiteAsync` в `Visary.Api.Client`. `FilterByRefIdContains` helper — для VisaryRef-полей |
| [76-share-agreement-dedup.md](./76-share-agreement-dedup.md) | 🔁 Дедупликация ДДУ: бизнес-ключ = (Number, RoomKindRef, ConditionalNumber, StageNumber, ProjectNumber). `FindShareAgreementsAsync` (глобальный listview), `ShareAgreementPatchRequest` расширен полями Room/RoomKindRef/ConditionalNumber/StageNumber/ProjectNumber для «реанимации» orphan-ДДУ. В `RoomsFormImportMapper` — Find → Reuse-or-Create с max(ID) + локальный пост-фильтр |
| [77-room-uniqueness-building-section.md](./77-room-uniqueness-building-section.md) | 🚪 Ключ уникальности Room = Section × Kind × Number × **BuildingSection** («Подъезд/Секция» из файла). Без этого два подъезда с одним номером PATCH-или друг друга. Match с `Trim() + OrdinalIgnoreCase` для нормализации null/пробелов |
| [78-budget-xlsx-export.md](./78-budget-xlsx-export.md) | 📤 **Заменяет [70](70-wbs-api-foundation.md)/[71](71-finmodel-budget-import.md):** CRUD-путь WBS отключён (Visary 500 на `listview/wbs/onetomany/ConstructionProject`). Бюджет «Финмодели» выгружается XLSX по эталону `Бюджет_А4.1`: embedded resource → ClosedXML копия → подмена сумм в C/D, агрегация Глава/Раздел снизу вверх. **v1.3 (2026-05-14)**: полное дерево для Главы 1 (отсутствующие = 0); Главы 2/3 свёрнуты до строки самой главы (`CollapsedChapterCodes`); распознавание главы по «Глава N» префиксу; chapter-direct override итоговой суммы из строки «Итого…» файла (для Глав 2/3, где статьи в файле ≠ справочнику); закрытие главы после первого «Итого…» (избегаем двойного учёта повторов под «Этап 2» / «фактические»); двусторонний fuzzy-match Title (forward + reverse-prefix-in-chapter). `GET /api/imports/{id}/budget-xlsx` |
| [79-rooms-import-validation-and-fileupload-ux.md](./79-rooms-import-validation-and-fileupload-ux.md) | 🧹 Импорт «Помещения»: нормализация `RoomNumber` через `ExtractDigitsOnly` («п1»→«1», «12А»→«12»; отличается от `ExtractNumericPart` тем, что точки/запятые тоже отбрасываются); `required_missing` для «Количество комнат», если Kind=«Квартира» (проверка после резолва title из колонки или имени листа); UI — явные кнопки «Удалить файл» / «Выбрать другой» под `FileUploadItem`, один `<input>` вынесен наружу + сброс `value` для повторного выбора одноимённого файла |
| [81-xlsx-external-links-strip.md](./81-xlsx-external-links-strip.md) | 🔗 Парсинг XLSX с external workbook links (формулы/defined names с URL вида `'https://…/[file.xls]Sheet'!…`): catch «Unable to determine token» → zip-уровень cleanup (`xl/externalLinks/*`, `<externalReferences>`, external `definedName`, `externalLink` rels) → retry. Кэшированные `<v>` остаются — данные читаются. |
| [80-multi-sheet-report-end-to-end.md](./80-multi-sheet-report-end-to-end.md) | 🗂️ Дополнение к [72](72-multi-sheet-import.md): сквозной проброс `Sheet` от БД до UI отчёта. `ImportsController.GetReport` возвращает `Sheet` для строк и ошибок и сортирует по `(Sheet, SourceRowNumber)`; `ApiImportRow/Error.sheet` + `UiReportRow/Error.sheet`; в `toUiReport` композитный ключ `rowKey(sheet, rowNumber)` + сортировка `localeCompare(_, 'ru')`. В `SessionRowsTable` каждый лист рендерится в своём `<tbody>` с заголовком «Лист: …», ключ строки `${sheet}::${rowNumber}` (без React-warning о дубликатах) |

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
