# 🎨 API @alfalab/core-components

## 📋 Описание

Гид по корректному использованию компонентов библиотеки [`@alfalab/core-components`](https://github.com/alfa-laboratory/core-components) v50+. Документ фиксирует **реальные API компонентов** (на основе `.d.ts` файлов из `node_modules`), чтобы избежать повторения ошибок типизации.

---

## 📦 Корректные пути импорта

```tsx
// ✅ ПРАВИЛЬНО — короткие пути через зонт-пакет
import { Button } from '@alfalab/core-components/button';
import { Select } from '@alfalab/core-components/select';
import { RadioGroup } from '@alfalab/core-components/radio-group';
import { Radio } from '@alfalab/core-components/radio';
import { Tabs, Tab } from '@alfalab/core-components/tabs';
import { Typography } from '@alfalab/core-components/typography';
import { CalendarInput } from '@alfalab/core-components/calendar-input';
import { Dropzone } from '@alfalab/core-components/dropzone';
import { FileUploadItem } from '@alfalab/core-components/file-upload-item';
import { ProgressBar } from '@alfalab/core-components/progress-bar';
import { Status } from '@alfalab/core-components/status';
import { Collapse } from '@alfalab/core-components/collapse';
import { Divider } from '@alfalab/core-components/divider';
import { Alert } from '@alfalab/core-components/alert';
```

```tsx
// ⚠️ ТАКЖЕ ВОЗМОЖНО — отдельные npm-пакеты (равноценно)
import { Button } from '@alfalab/core-components-button';
```

```tsx
// ❌ НЕ РАБОТАЕТ — нет barrel-export из корня
import { Button } from '@alfalab/core-components';
```

---

## 🎛️ Dropzone

### ✅ Правильная реализация

```tsx
import { Dropzone } from '@alfalab/core-components/dropzone';

<Dropzone
  onDrop={(files: File[]) => handleFiles(files)}
  block          /* 👈 растянуть на всю ширину */
  text="Перетащите файл"
>
  <CustomContent />
</Dropzone>
```

### ❌ Типичная ошибка

```tsx
{/* НЕПРАВИЛЬНО — нет пропа `accept`, нет multiple */}
<Dropzone accept=".xlsx" multiple={false} onDrop={...} />
```

### 📌 Реальные пропы Dropzone

| Проп | Тип | Описание |
|------|-----|----------|
| `onDrop` | `(files: File[]) => void` | Обработчик drop |
| `onDragOver`, `onDragLeave`, `onDragEnter` | event handlers | События drag |
| `text` | `string` | Подпись для заглушки |
| `disabled` | `boolean` | Заблокированное состояние |
| `error` | `boolean` | Состояние ошибки |
| `block` | `boolean` | Растянуть на всю ширину |
| `Overlay` | `ComponentType` | Кастомный оверлей |
| `children` | `ReactNode` | Дочерние элементы |

> ⚠️ Фильтрацию по расширению (`accept`) нужно делать через скрытый `<input type="file" accept=".xlsx" />` рядом с Dropzone, либо валидацией в `onDrop`.

---

## 📎 FileUploadItem

### ✅ Правильная реализация

```tsx
<FileUploadItem
  title={file.name}        /* 👈 НЕ name, а title */
  size={file.size}
  uploadStatus="UPLOADED"  /* 👈 строго из union FileUploadItemStatus */
  showDelete
  onDelete={() => clearFile()}
/>
```

### ❌ Типичная ошибка

```tsx
{/* НЕПРАВИЛЬНО */}
<FileUploadItem
  name={file.name}              /* ❌ не существует, нужен `title` */
  uploadStatus="SUCCESS"        /* ❌ это валидное значение, но неоднозначное */
  uploadStatus="UPLOAD_SUCCESS" /* ❌ такого значения НЕТ */
/>
```

### 📌 Допустимые значения uploadStatus

```ts
type FileUploadItemStatus =
  | 'INITIAL'
  | 'SUCCESS'   // успех при загрузке файла на сервер
  | 'ERROR'
  | 'UPLOADING'
  | 'UPLOADED'  // файл загружен (используем для показа загруженного файла)
  | 'LOADING'
  | 'DELETED';
```

---

## 🔘 Select

### ✅ Правильная реализация

```tsx
const options = [
  { key: '1', content: 'Проект 1' },  // 👈 key + content (НЕ value + label)
  { key: '2', content: 'Проект 2' },
];

<Select
  label="Проект"
  placeholder="Выберите"
  options={options}
  selected={selectedKey}                              /* string | OptionShape | null */
  onChange={({ selected }) => {                       /* 👈 деструктуризация payload */
    setSelectedKey(selected ? selected.key : null);
  }}
  block
/>
```

### ❌ Типичная ошибка

```tsx
{/* НЕПРАВИЛЬНО */}
<Select
  options={[{ value: '1', label: 'X' }]}                     /* ❌ не value/label, а key/content */
  onChange={(e) => setVal(e.target.value)}                   /* ❌ payload, а не event */
  selected={projectId ? String(projectId) : undefined}       /* ❌ undefined → uncontrolled mode */
/>
```

### ⚠️ Грабли: `selected={undefined}` ломает контролируемость

`@alfalab/core-components/select` основан на **Downshift**. Передача `undefined` в `selected` означает «uncontrolled mode» (компонент сам управляет выбором). Если потом передать строку — Downshift выкинет ошибку:

```
downshift: A component has changed the uncontrolled prop "selectedItems"
to be controlled. This prop should not switch from controlled to uncontrolled.
```

```tsx
{/* ❌ ОШИБКА: undefined переключает в uncontrolled mode */}
selected={projectId ? String(projectId) : undefined}

{/* ✅ ПРАВИЛЬНО: null оставляет controlled mode */}
selected={projectId !== null ? String(projectId) : null}

{/* ✅ Тоже правильно (если value: T | null) */}
selected={value ?? null}
```

**Правило**: для controlled Select **никогда не передавайте `undefined`** в `selected` — только строку, объект `OptionShape`, или `null`.

### 📌 OptionShape

```ts
type OptionShape = {
  key: string;            // уникальный идентификатор
  content?: ReactNode;    // содержимое опции
  value?: any;            // значение (опционально, доступно через payload.selected.value)
  disabled?: boolean;
};
```

### 📌 onChange payload

```ts
type BaseSelectChangePayload = {
  selected: OptionShape | null;
  selectedMultiple: OptionShape[];
  initiator: OptionShape | null;
  // ... другие поля
};
```

---

## 📻 RadioGroup + Radio

### ✅ Правильная реализация

```tsx
import { RadioGroup } from '@alfalab/core-components/radio-group';
import { Radio } from '@alfalab/core-components/radio';

<RadioGroup
  label=""                              /* можно пустую — заголовок снаружи */
  name="importType"
  direction="horizontal"                /* 'horizontal' | 'vertical' */
  value={value}
  onChange={(_, payload) => {
    setValue(payload?.value as MyType); /* 👈 второй параметр содержит { value } */
  }}
>
  <Radio label="Помещения" value="rooms" />
  <Radio label="ДДУ" value="shareAgreements" />
</RadioGroup>
```

### ⚠️ Важно
- `onChange` принимает **два параметра**: `(event, payload)`, где `payload = { name, value }`
- `Radio` импортируется из **отдельного** пути `@alfalab/core-components/radio`, а не из `radio-group`

---

## 📑 Tabs + Tab

### ✅ Правильная реализация

```tsx
import { Tabs, Tab } from '@alfalab/core-components/tabs';

<Tabs
  selectedId={value}
  onChange={(_, { selectedId }) => setValue(selectedId)}  /* 👈 деструктуризация */
>
  <Tab id="csv" title="CSV" />
  <Tab id="xls" title="XLS" />
  <Tab id="xlsx" title="XLSX" />
</Tabs>
```

### ⚠️ Важно
- Используется `selectedId` / `id`, **а не** `activeKey` / `key`
- `onChange(event, { selectedId })` — деструктурируем payload

---

## 📅 CalendarInput

### ✅ Правильная реализация

```tsx
<CalendarInput
  label="Дата начала"
  value={dateStr}                          /* string в формате DD.MM.YYYY */
  onChange={(_, { value }) => setDate(value)}  /* 👈 string */
  block
/>
```

### ⚠️ Важно
- `value` — **строка** в формате `DD.MM.YYYY`, не Date-объект
- `onChange(event, { value, date })` — `value: string`, `date: Date`

---

## 📊 ProgressBar

### ✅ Правильная реализация

```tsx
<ProgressBar
  value={45}                /* 0-100 */
  view="accent"             /* см. список ниже */
  size={8}                  /* 4 | 8 */
/>
```

### 📌 Допустимые значения view

```ts
type ProgressBarView =
  | 'positive'   // зелёный
  | 'negative'   // красный
  | 'attention'  // оранжевый
  | 'link'       // синий
  | 'tertiary'
  | 'secondary'
  | 'primary'
  | 'accent';    // фирменный красный Альфа
```

---

## 🏷️ Status

### ✅ Правильная реализация

```tsx
<Status color="green" view="soft">Успех</Status>
<Status color="red" view="soft">Ошибка</Status>
<Status color="orange" view="soft">Предупреждение</Status>
```

### 📌 Допустимые значения

```ts
type StatusColor = 'green' | 'orange' | 'red' | 'blue' | 'grey' | 'teal' | 'purple';
type StatusView = 'contrast' | 'soft' | 'muted' | 'muted-alt';
type StatusSize = 20 | 24 | 32 | 40;
type StatusShape = 'rounded' | 'rectangular';
```

---

## ✏️ Typography

### ✅ Правильная реализация

```tsx
import { Typography } from '@alfalab/core-components/typography';

<Typography.Title view="medium" tag="h1" weight="bold">Заголовок</Typography.Title>
<Typography.Title view="small" tag="h2">Подзаголовок</Typography.Title>
<Typography.Text view="primary-medium" weight="bold">Жирный текст</Typography.Text>
<Typography.Text view="primary-small" color="secondary">Мелкий вторичный текст</Typography.Text>
```

### 📌 Виды

| Компонент | view | Назначение |
|-----------|------|------------|
| `Title` | `medium`, `small`, `xsmall`, `xlarge` | Заголовки |
| `Text` | `primary-large`, `primary-medium`, `primary-small`, `secondary-large`, `secondary-medium`, `secondary-small` | Текст |

### ⚠️ Важно
- `Title` **поддерживает** проп `style` ✅
- `Text` **поддерживает** проп `style` ✅
- `tag` — обязательный для семантики (`h1`, `h2`, `div`, `span`, `p`)

---

## ➖ Divider

### ❌ Типичная ошибка

```tsx
{/* НЕПРАВИЛЬНО — Divider НЕ принимает style */}
<Divider style={{ margin: '24px 0' }} />
```

### ✅ Правильная реализация

```tsx
{/* Оборачиваем в div с нужными стилями */}
<div className="section-gap"><Divider /></div>

{/* CSS */}
.section-gap { margin: 24px 0; }
```

### 📌 Реальные пропы Divider

| Проп | Тип |
|------|-----|
| `className` | `string` |
| `dataTestId` | `string` |

> Только `className` — стилизуем через CSS классы, не inline.

---

## 🚨 Alert

### ❌ Типичная ошибка

```tsx
{/* НЕПРАВИЛЬНО — Alert НЕ принимает style */}
<Alert view="negative" style={{ display: 'none' }}>Текст</Alert>
```

### ✅ Правильная реализация

```tsx
{shouldShow && (
  <Alert view="negative">Текст ошибки</Alert>
)}

{/* Если нужны отступы — оборачиваем */}
<div className="alert-wrap"><Alert view="negative">...</Alert></div>
```

---

## 🎯 Чек-лист при добавлении нового Alfa-компонента

- [ ] Проверил путь импорта: `@alfalab/core-components/<kebab-case-name>`
- [ ] Прочитал `node_modules/@alfalab/core-components-<name>/esm/Component.d.ts`
- [ ] **Не использовал** проп `style` (большинство компонентов его не принимают)
- [ ] Проверил, что `onChange` имеет сигнатуру `(event, payload)` с деструктуризацией
- [ ] Использовал `key`/`content` вместо `value`/`label` для опций
- [ ] Использовал значения из union-типов (см. `.d.ts`), а не угаданные строки

---

## 📍 Применение в проекте

| Компонент | Файл | Использование |
|-----------|------|---------------|
| `Select` | `KiloImportService.Web/src/components/ImportTypePicker/ImportTypePicker.tsx` | Выбор типа импорта (8+ опций) |
| `Select`, `CalendarInput` | `KiloImportService.Web/src/components/ImportForm/ImportForm.tsx` | Выбор проекта/объекта + даты |
| `Dropzone`, `FileUploadItem`, `Status` | `KiloImportService.Web/src/components/FileUpload/FileUpload.tsx` | Загрузка файла + автоопределение формата |
| `ProgressBar` | `KiloImportService.Web/src/components/ImportReport/ReportProgress.tsx` | Прогресс импорта |
| `Status`, `Collapse` | `KiloImportService.Web/src/components/ImportReport/ReportTable.tsx` | Таблица отчёта |
| `Divider` | `KiloImportService.Web/src/components/ImportReport/ImportReport.tsx` | Разделители |
| `Button`, `Typography` | `KiloImportService.Web/src/App.tsx` | Главная страница |

> 📝 `RadioGroup`/`Radio` и `Tabs`/`Tab` **больше не используются** в проекте. Документация по их API сохранена для будущих сценариев (форма с 2-4 равнозначными опциями, табы между разделами).
