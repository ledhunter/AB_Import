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
| [28-faq.md](./28-faq.md) | ❓ Частые вопросы: пустой Select, бэком не отвечает, обновление токена |
| [plan-listview-library.md](./plan-listview-library.md) | 🏗️ План рефакторинга ListView в переиспользуемую библиотеку (с чек-листом и логом прогресса) |

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
