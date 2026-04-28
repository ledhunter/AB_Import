# 📋 Реестр типов импорта

## 📋 Описание

Типы импорта могут быть **многочисленными** (от 3 до 20+ сценариев). Поэтому используется **Select** (выпадающий список) вместо `RadioGroup`. Список типов хранится в **реестре** (`IMPORT_TYPES`), который в будущем подгружается с backend через API.

---

## 🎯 Принцип

```
[Реестр IMPORT_TYPES] → опции Select → пользователь выбирает → отправка importType.id на backend
```

### Почему Select вместо RadioGroup

| Критерий | RadioGroup | Select |
|----------|-----------|--------|
| Кол-во вариантов 2-4 | ✅ Подходит | ⚠️ Избыточно |
| Кол-во вариантов 5-10 | ❌ Занимает много места | ✅ Подходит |
| Кол-во вариантов 10+ | ❌ Невозможно | ✅ Подходит |
| Описания вариантов | ❌ Тесно | ✅ Можно показать `description` в опции |
| Поиск/фильтрация | ❌ Невозможно | ✅ Встроено в Alfa Select |

> **Решение**: с самого начала используем `Select` — масштабируется без редизайна.

---

## ✅ Правильная реализация

### 1. Тип данных (открытый union)

```ts
// src/types/import.ts

// Открытый union: список типов приходит из IMPORT_TYPES (могут добавляться)
export type ImportType = string;

export interface ImportTypeOption {
  id: string;                  // 👈 уникальный id, передаётся на backend
  label: string;               // 👈 название для UI
  description?: string;        // 👈 опциональное описание (tooltip / подсказка)
}
```

### ⚠️ Почему `string`, а не строгий union?

- При **строгом union** (`'rooms' | 'shareAgreements' | ...`) — каждое добавление нового типа требует изменения типа в коде
- При **`string`** — список расширяется только в `IMPORT_TYPES`, новый тип сразу доступен
- При **загрузке с backend** через `GET /api/import/types` строгий union вообще невозможен

### 2. Реестр типов

```ts
// src/mocks/importTypes.ts
import type { ImportTypeOption } from '../types/import';

export const IMPORT_TYPES: ImportTypeOption[] = [
  { id: 'rooms', label: 'Помещения', description: 'Импорт реестра помещений' },
  { id: 'shareAgreements', label: 'ДДУ', description: 'Импорт договоров долевого участия' },
  { id: 'mixed', label: 'Помещения + ДДУ (полный цикл)', description: '...' },
  { id: 'paymentSchedule', label: 'График платежей по ДДУ' },
  { id: 'escrowAccounts', label: 'Счета эскроу' },
  { id: 'constructionSites', label: 'Объекты строительства' },
  { id: 'organizations', label: 'Организации (застройщики)' },
  { id: 'buyers', label: 'Покупатели (физ.лица)' },
  // ... добавляем новые типы здесь
];
```

### 3. Компонент ImportTypePicker (на Select)

```tsx
// src/components/ImportTypePicker/ImportTypePicker.tsx
import { useMemo } from 'react';
import { Select } from '@alfalab/core-components/select';
import type { ImportType } from '../../types/import';
import { IMPORT_TYPES } from '../../mocks/importTypes';

interface Props {
  value: ImportType | null;
  onChange: (value: ImportType | null) => void;
}

export const ImportTypePicker = ({ value, onChange }: Props) => {
  const options = useMemo(
    () => IMPORT_TYPES.map((t) => ({ key: t.id, content: t.label })),
    [],
  );

  return (
    <div className="field">
      <Select
        label="Тип импорта"
        placeholder="Выберите тип импорта"
        options={options}
        selected={value ?? undefined}
        onChange={({ selected }) => onChange(selected ? (selected.key as ImportType) : null)}
        block
      />
    </div>
  );
};
```

### ⚠️ Важно

- `value` начально `null` (не `'mixed'`) — заставляет пользователя осознанно выбрать
- `selected={value ?? undefined}` — Select требует `undefined`, а не `null`
- `useMemo` для `options` — список не пересчитывается на каждый render

---

## ❌ Типичные ошибки

### Ошибка 1: RadioGroup для большого списка

```tsx
{/* НЕПРАВИЛЬНО при 5+ типах */}
<RadioGroup direction="horizontal">
  <Radio label="Помещения" value="rooms" />
  <Radio label="ДДУ" value="shareAgreements" />
  <Radio label="Mixed" value="mixed" />
  <Radio label="Платежи" value="paymentSchedule" />
  <Radio label="Эскроу" value="escrowAccounts" />
  {/* и так далее... */}
</RadioGroup>
```

**Почему плохо:**
- На `direction="horizontal"` — переполнение по ширине
- На `direction="vertical"` — занимает 5+ строк формы
- Невозможен поиск/фильтрация

### Ошибка 2: жёсткий union для расширяемого списка

```ts
{/* НЕПРАВИЛЬНО — каждый новый тип требует изменения типа */}
export type ImportType = 'rooms' | 'shareAgreements' | 'mixed' | 'paymentSchedule' | ...;
```

**Почему плохо:**
- Невозможно загрузить список с backend без `as ImportType`
- При добавлении нового типа — N мест в коде требуют обновления
- При удалении типа — TS не покажет, где он использовался (нет fail-fast)

**Правильно:** `ImportType = string` + проверки на runtime через `IMPORT_TYPES.some(t => t.id === value)`.

### Ошибка 3: дублирование названий в UI

```tsx
{/* НЕПРАВИЛЬНО — название "Помещения + ДДУ" продублировано в форме и моке */}
<Typography.Title>Помещения + ДДУ — выбран</Typography.Title>
{importType === 'mixed' && <p>Загрузите файл с помещениями и ДДУ</p>}
```

**Правильно:**
```tsx
const selectedType = IMPORT_TYPES.find(t => t.id === importType);
<Typography.Title>{selectedType?.label} — выбран</Typography.Title>
{selectedType?.description && <p>{selectedType.description}</p>}
```

---

## 🔌 Переход к загрузке с backend

### Этап 1 (текущий): моки

```ts
import { IMPORT_TYPES } from '../mocks/importTypes';
const options = IMPORT_TYPES.map(t => ({ key: t.id, content: t.label }));
```

### Этап 2: загрузка через React Query

```ts
// src/services/importTypesService.ts
export const fetchImportTypes = async (): Promise<ImportTypeOption[]> => {
  const { data } = await axios.get('/api/import/types');
  return data;
};

// src/components/ImportTypePicker/ImportTypePicker.tsx
const { data: types = [], isLoading } = useQuery({
  queryKey: ['importTypes'],
  queryFn: fetchImportTypes,
});

const options = useMemo(
  () => types.map(t => ({ key: t.id, content: t.label })),
  [types],
);

return (
  <Select
    label="Тип импорта"
    placeholder={isLoading ? 'Загрузка типов…' : 'Выберите тип импорта'}
    disabled={isLoading}
    options={options}
    {...}
  />
);
```

### Соответствующий backend endpoint

```csharp
// ImportTypesController.cs
[HttpGet("/api/import/types")]
public ActionResult<IEnumerable<ImportTypeOption>> GetTypes()
{
    return Ok(new[] {
        new ImportTypeOption { Id = "rooms", Label = "Помещения" },
        new ImportTypeOption { Id = "shareAgreements", Label = "ДДУ" },
        // ...
    });
}
```

---

## 📍 Применение в проекте

| Паттерн | Файл | Содержание |
|---------|------|------------|
| Тип `ImportType` (string) | `KiloImportService.Web/src/types/import.ts` | Открытый union |
| Реестр типов | `KiloImportService.Web/src/mocks/importTypes.ts` | `IMPORT_TYPES` массив |
| Компонент Select | `KiloImportService.Web/src/components/ImportTypePicker/ImportTypePicker.tsx` | RadioGroup → Select |
| Использование | `KiloImportService.Web/src/App.tsx` | `importType: ImportType \| null` |

---

## 🎯 Чек-лист при добавлении нового типа импорта

- [ ] Добавил запись в `IMPORT_TYPES` (id + label + description)
- [ ] **Не** изменил тип `ImportType` (он `string` намеренно)
- [ ] Backend-обработчик `IImportProcessor` поддерживает новый `id`
- [ ] Реализована backend-логика для нового типа
- [ ] Обновлён DTO `ImportRequest.ImportType` на backend (если используется enum)
- [ ] Добавлен endpoint `/api/import/types` (или обновлён, если уже есть)
- [ ] При наличии `description` — текст помещён в `IMPORT_TYPES`, не в JSX
