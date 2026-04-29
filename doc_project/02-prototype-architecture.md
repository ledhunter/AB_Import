# 🏗️ Архитектура UI-прототипа

## 📋 Описание

UI сервиса импорта (`KiloImportService.Web/`) построен на **React 19 + TypeScript + Vite** с использованием оригинальных компонентов Альфа-Банка `@alfalab/core-components`. Работает с двумя backend'ами:

- **`KiloImportService.Api`** (`/api/imports`, `/api/import-types`, `/hubs/imports`) — собственный сервис на .NET 10 + SignalR.
- **Visary ListView API** (`/api/visary/listview/*`) — внешний справочник проектов и объектов строительства.

Полный контур интеграции UI ↔ собственный backend описан в [14-imports-backend-integration.md](./14-imports-backend-integration.md), Visary — в [08-visary-api-integration.md](./08-visary-api-integration.md).

---

## 📂 Структура папок

```
KiloImportService.Web/src/
├── components/                          # UI-компоненты по фичам
│   ├── ImportTypePicker/
│   │   └── ImportTypePicker.tsx        # Select из useImportTypes (eager-load /api/import-types)
│   ├── ImportForm/
│   │   └── ImportForm.tsx              # Select (проект/объект) — useProjects + useSites
│   ├── FileUpload/
│   │   └── FileUpload.tsx              # Dropzone + FileUploadItem + автоопределение формата
│   └── ImportSession/                  # Активная сессия (прогресс / отчёт / кнопки)
│       ├── SessionView.tsx             # Композиция: phase → Progress | Summary+Table
│       ├── SessionProgress.tsx         # ProgressBar + live "строка X из Y (лист «...»)"
│       ├── SessionSummary.tsx          # Карточки + datetime метаданных
│       ├── SessionRowsTable.tsx        # Таблица строк с фильтрами + ошибки
│       ├── SessionStatusBadge.tsx      # Цветной Status по SessionStatusVariant
│       └── labels.ts                   # SESSION_STATUS_LABELS, STAGE_LABELS
├── types/                              # Типы (TypeScript)
│   ├── import.ts                       # ImportType (string), FileFormat (union)
│   ├── api.ts                          # Backend DTO: ApiImportSession/Report/Status/Stage
│   ├── session.ts                      # UI-модель: UiSession/UiReport/UiStageProgress
│   └── listView.ts                     # ProjectItem, SiteItem (Visary)
├── services/                           # 🔌 Сервисы интеграции
│   ├── visaryApi.ts                    # Visary fetch + VisaryApiError/VisaryAuthError
│   ├── importsService.ts               # 🆕 REST-клиент собственного backend
│   ├── importsHub.ts                   # 🆕 SignalR-обёртка (/hubs/imports)
│   ├── importMappers.ts                # 🆕 toUiSession/toUiReport/computeDuration
│   ├── projectsService.ts              # @deprecated shim
│   ├── listView/                       # 🧰 Библиотека методов ListView (см. 10-listview-library.md)
│   │   ├── index.ts
│   │   ├── types.ts
│   │   ├── parseListViewResponse.ts
│   │   ├── createListViewService.ts
│   │   ├── entities/
│   │   │   ├── projects.ts
│   │   │   └── sites.ts
│   │   └── __tests__/                  # 6+6+6 кейсов
│   └── __tests__/
│       ├── projectsService.test.ts     # 10 кейсов
│       └── importMappers.test.ts       # 🆕 21 кейс (toSessionVariant, computeDuration, toUiReport...)
├── hooks/
│   ├── useListView.ts                  # generic lazy-load (idle/loading/success/error + AbortController)
│   ├── useProjects.ts                  # обёртка useListView(projectsService)
│   ├── useSites.ts                     # обёртка с фильтром по projectId
│   ├── useImportSession.ts             # 🆕 phase-machine: idle/uploading/tracking/applying/completed/error
│   └── useImportTypes.ts               # 🆕 eager-load /api/import-types
├── utils/
│   ├── fileFormat.ts                   # detectFileFormat() — резолв формата по расширению
│   └── datetime.ts                     # formatDateTime() — ISO → DD.MM.YYYY HH:mm:ss
├── App.tsx                             # Главная: useImportSession + render по phase
├── App.css                             # Все стили
└── main.tsx
```

> 📝 **История изменений:**
> - ❌ `FileFormatPicker` удалён — формат по расширению (см. [05-file-format-detection.md](./05-file-format-detection.md))
> - 🔄 `ImportTypePicker` — `RadioGroup` → `Select` (см. [06-import-types-registry.md](./06-import-types-registry.md))
> - ❌ `CalendarInput` для дат удалён — `startedAt`/`completedAt` от backend'а (см. [07-import-datetime-metadata.md](./07-import-datetime-metadata.md))
> - 🔌 **Список проектов** из Visary API (см. [08-visary-api-integration.md](./08-visary-api-integration.md)), `MOCK_PROJECTS` удалён
> - ❌ **Папка `mocks/` удалена** — `MOCK_SITES`/`MOCK_PROGRESS`/`MOCK_REPORT`/`IMPORT_TYPES` все убраны
> - ❌ **Папка `components/ImportReport/` удалена** — заменена на `components/ImportSession/` под реальный backend DTO
> - ➕ **Папка `components/ImportSession/`** — рендерит `UiReport` (см. [14-imports-backend-integration.md](./14-imports-backend-integration.md))
> - ➕ **Сервисы `importsService` + `importsHub` + `importMappers`** — клиент собственного backend (REST + SignalR)
> - ➕ **Хуки `useImportSession` + `useImportTypes`** — phase-machine импорта
> - ➕ **Типы `types/api.ts` + `types/session.ts`** — два слоя (DTO ⇄ UI), маппер между ними
> - ➕ **Тесты `__tests__/importMappers.test.ts`** — 21 кейс на маппер

---

## 🎯 Принципы архитектуры

### ✅ 1. Один компонент = одна папка

```
components/
└── ImportTypePicker/
    └── ImportTypePicker.tsx
```

**Зачем**: при росте можно добавить рядом `index.ts`, `*.test.tsx`, `*.module.css` без миграции.

### ✅ 2. Чёткое разделение типов и моков

- `types/import.ts` — все доменные типы
- `mocks/data.ts` — все моковые данные

**Зачем**: при появлении API легко заменить `mocks/data.ts` на реальные `services/api.ts` — типы остаются.

### ✅ 3. Контролируемые компоненты (Lifted State)

```tsx
// ✅ ПРАВИЛЬНО — состояние в App.tsx, дочерние компоненты только отображают
const [importType, setImportType] = useState<ImportType>('mixed');

<ImportTypePicker value={importType} onChange={setImportType} />
```

**Зачем**: `App.tsx` владеет всем состоянием формы → легко собрать payload для API в одном месте.

### ❌ Антипаттерн

```tsx
// НЕПРАВИЛЬНО — состояние внутри Picker'а, никак не использовать снаружи
const ImportTypePicker = () => {
  const [value, setValue] = useState('mixed');
  return <RadioGroup value={value} ... />;
};
```

---

## 🔄 State-machine главной страницы (App.tsx)

### Этапы (stages)

```ts
type Stage = 'form' | 'processing' | 'completed';
```

| Stage | Что отображается | Доступные действия |
|-------|------------------|---------------------|
| `form` | Форма параметров + кнопка «Запустить» | Заполнение полей |
| `processing` | Прогресс-бар (имитация SignalR) | — (ожидание) |
| `completed` | Полный отчёт + кнопка «Новый импорт» | Сброс к `form` |

### Переходы

```
[form] --[handleSubmit]--> [processing] --[interval done]--> [completed]
                                                                 |
[form] <----------[handleReset]---------------------------------/
```

### ✅ Правильная реализация переключения

```tsx
// App.tsx
const handleSubmit = () => {
  setStage('processing');
  // ... симуляция прогресса через setInterval
  // По завершении: setStage('completed');
};

return (
  <>
    {stage === 'form' && <FormCard />}
    {(stage === 'processing' || stage === 'completed') && <ImportReport ... />}
  </>
);
```

---

## 📦 Типизация

### Главные домейнные типы

```ts
// types/import.ts
export type ImportType = 'rooms' | 'shareAgreements' | 'mixed';
export type FileFormat = 'csv' | 'xls' | 'xlsb' | 'xlsx';
export type ImportStatus = 'idle' | 'queued' | 'parsing' | 'processing'
                         | 'completed' | 'completedWithWarnings' | 'failed' | 'cancelled';
export type EntityAction = 'Created' | 'Updated' | 'Skipped' | 'Error';
export type RowStatus = 'success' | 'warning' | 'error';
```

### ⚠️ Важно
- **Все** значения union-типов — в `types/import.ts`. Никаких "магических строк" в компонентах.
- При сравнении строк в JSX — TypeScript автоматически проверит соответствие union'у.

### Моки полностью соответствуют типам

```ts
// mocks/data.ts
export const MOCK_REPORT: ImportReport = { ... };  // 👈 типизировано
```

**Зачем**: при изменении типа TypeScript сразу подсветит, что нужно обновить мок.

---

## 🎨 Подключение стилей

```tsx
// App.tsx
import './App.css';     // 👈 ОДИН CSS-файл с глобальными стилями + классы компонентов
```

### ⚠️ Не используется в прототипе
- ❌ Tailwind CSS
- ❌ CSS Modules (вынесем в фазе scale-up)
- ❌ styled-components

### ✅ Используется
- ✅ Глобальные CSS-классы из `App.css`
- ✅ Inline-стили **только** для одноразовых отступов через `style={{ marginTop: 4 }}` на компонентах Typography (Typography поддерживает `style`)

### ❌ Не использовать style на:
- `<Divider style={...} />` — НЕ принимает style
- `<Alert style={...} />` — НЕ принимает style
- `<Status style={...} />` — проверь типы перед использованием

---

## 🔌 Готовность к интеграции с backend

Прототип спроектирован так, что переход на реальный API минимален:

### Замена 1: моки → API-вызовы

```ts
// БЫЛО (mocks/data.ts)
export const MOCK_PROJECTS: ProjectItem[] = [ ... ];

// СТАНЕТ (services/listViewService.ts)
export const fetchProjects = async (): Promise<ProjectItem[]> => {
  const { data } = await axios.post('/api/listview/constructionproject', { ... });
  return data.items;
};
```

### Замена 2: симуляция setInterval → SignalR

```ts
// БЫЛО (App.tsx)
const interval = setInterval(() => { ... setProgress(...) ... }, 200);

// СТАНЕТ (hooks/useImportProgress.ts)
useEffect(() => {
  const conn = new HubConnectionBuilder().withUrl('/hubs/import-progress').build();
  conn.on('ProgressUpdated', setProgress);
  conn.on('ImportCompleted', ({ summary }) => setReport(summary));
  conn.start().then(() => conn.invoke('SubscribeToImport', importId));
  return () => conn.stop();
}, [importId]);
```

### Замена 3: setStage → useReducer (если усложнится)

При добавлении ошибок API, retry, отмены — лучше перейти на `useReducer` с явным `Action` union.

---

## 🎯 Чек-лист добавления нового UI-компонента

- [ ] Создал папку `components/<ComponentName>/`
- [ ] Файл `<ComponentName>.tsx` с props-интерфейсом наверху
- [ ] Props **контролируемые** (`value` + `onChange`)
- [ ] Импорт Alfa-компонентов через `@alfalab/core-components/<kebab-name>`
- [ ] CSS-классы добавлены в `App.css` (одна точка для глобальных стилей)
- [ ] Запустил `npx tsc --noEmit -p tsconfig.app.json` — нет ошибок
- [ ] Не добавил inline-стили на компоненты, не поддерживающие `style` (см. `01-alfa-core-components-api.md`)

---

## 📍 Команды

```powershell
# Dev-сервер
npm run dev

# Type-check (без сборки)
npx tsc --noEmit -p tsconfig.app.json

# Production-сборка
npm run build

# Превью production-сборки
npm run preview
```
