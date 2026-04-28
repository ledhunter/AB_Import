# 🏗️ Архитектура UI-прототипа

## 📋 Описание

UI-прототип сервиса импорта (`KiloImportService.Web/`) построен на **React 19 + TypeScript + Vite** с использованием оригинальных компонентов Альфа-Банка `@alfalab/core-components`. Все данные **моковые** — backend ещё не реализован.

---

## 📂 Структура папок

```
KiloImportService.Web/src/
├── components/                          # UI-компоненты по фичам
│   ├── ImportTypePicker/
│   │   └── ImportTypePicker.tsx        # Select: тип импорта (8+ опций)
│   ├── ImportForm/
│   │   └── ImportForm.tsx              # Select (проект/объект) + CalendarInput
│   ├── FileUpload/
│   │   └── FileUpload.tsx              # Dropzone + FileUploadItem + автоопределение формата
│   └── ImportReport/                   # Подробный отчёт (контейнер)
│       ├── ImportReport.tsx            # Контейнер
│       ├── ReportProgress.tsx          # Прогресс-бар + текущий лист
│       ├── ReportSummary.tsx           # Карточки сводки (8 метрик)
│       └── ReportTable.tsx             # Таблица + фильтры + Collapse
├── types/                              # Типы (TypeScript)
│   ├── import.ts                       # ImportType, FileFormat, ImportReport, ReportRow
│   └── listView.ts                     # ProjectItem, SiteItem
├── mocks/
│   ├── data.ts                         # Моки: 6 объектов строительства, 8 строк отчёта
│   └── importTypes.ts                  # IMPORT_TYPES — реестр типов импорта (8 шт.)
├── services/                           # 🔌 Интеграция с Visary API
│   ├── visaryApi.ts                    # fetch-обёртка + VisaryApiError/VisaryAuthError
│   ├── projectsService.ts              # fetchProjects() + toProjectItem()
│   └── __tests__/
│       └── projectsService.test.ts     # Unit-тесты маппинга (5 кейсов)
├── hooks/
│   └── useProjects.ts                  # Хук: data/loading/error + AbortController
├── utils/
│   ├── fileFormat.ts                   # detectFileFormat() — резолв формата по расширению
│   └── datetime.ts                     # formatDateTime() — ISO → DD.MM.YYYY HH:mm:ss
├── App.tsx                             # Главная страница + state-machine (stage)
├── App.css                             # Все стили прототипа
└── main.tsx
```

> 📝 **Важные изменения после первой версии прототипа:**
> - ❌ `FileFormatPicker` удалён — формат определяется автоматически по расширению (см. [05-file-format-detection.md](./05-file-format-detection.md))
> - 🔄 `ImportTypePicker` — переведён с `RadioGroup` на `Select` для масштабирования (см. [06-import-types-registry.md](./06-import-types-registry.md))
> - ❌ `CalendarInput` для дат импорта удалён — `startedAt`/`completedAt` информационные, фиксируются backend'ом (см. [07-import-datetime-metadata.md](./07-import-datetime-metadata.md))
> - 🔌 **Список проектов** теперь грузится из реального Visary API (см. [08-visary-api-integration.md](./08-visary-api-integration.md)), `MOCK_PROJECTS` удалён
> - ➕ Добавлены папки `services/`, `hooks/`, `utils/`, `services/__tests__/`

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
