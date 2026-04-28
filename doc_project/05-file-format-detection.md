# 🔍 Автоопределение формата файла

## 📋 Описание

Формат файла определяется **автоматически** по расширению, без участия пользователя. UI-выбор формата (`FileFormatPicker`) **удалён** — пользователь только загружает файл, а сервис сам резолвит парсер.

---

## 🎯 Принцип

```
Пользователь загрузил → detectFileFormat(name) → null | 'csv' | 'xls' | 'xlsb' | 'xlsx'
                                ↓
                       Если null → ошибка "Неподдерживаемый формат"
                       Если ok   → показать определённый формат + продолжить
```

---

## ✅ Правильная реализация

### Утилита определения

```ts
// src/utils/fileFormat.ts
import type { FileFormat } from '../types/import';

export const SUPPORTED_FORMATS: FileFormat[] = ['csv', 'xls', 'xlsb', 'xlsx'];

export const ACCEPT_ALL_SUPPORTED =
  '.csv,.xls,.xlsb,.xlsx,' +
  'text/csv,' +
  'application/vnd.ms-excel,' +
  'application/vnd.ms-excel.sheet.binary.macroEnabled.12,' +
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';

export const detectFileFormat = (fileName: string): FileFormat | null => {
  const lower = fileName.toLowerCase();
  const dot = lower.lastIndexOf('.');
  if (dot === -1) return null;          // 👈 файл без расширения → null
  const ext = lower.slice(dot + 1) as FileFormat;
  return SUPPORTED_FORMATS.includes(ext) ? ext : null;
};
```

### Использование в App.tsx (через useMemo)

```tsx
const [file, setFile] = useState<File | null>(null);
const detectedFormat = useMemo(() => (file ? detectFileFormat(file.name) : null), [file]);

const canSubmit = importType !== null
  && projectId !== null
  && siteId !== null
  && file !== null
  && detectedFormat !== null;   // 👈 валидируем что формат определился
```

### Валидация при выборе файла

```tsx
// FileUpload.tsx
const validateAndSelect = (f: File | null | undefined) => {
  if (!f) return;
  const fmt = detectFileFormat(f.name);
  if (!fmt) {
    setError(`Неподдерживаемый формат файла. Допустимые: CSV, XLS, XLSB, XLSX.`);
    onFileSelect(null);    // 👈 НЕ принимаем файл с неподдерживаемым форматом
    return;
  }
  setError(null);
  onFileSelect(f);
};
```

### Отображение определённого формата (Status badge)

```tsx
{file && detectedFormat && (
  <div className="file-uploaded__format">
    <Typography.Text view="primary-small" color="secondary" tag="span">
      Определён формат:
    </Typography.Text>
    <Status color="blue" view="soft">
      {detectedFormat.toUpperCase()}
    </Status>
  </div>
)}
```

### ⚠️ Важно
- `accept` атрибут на `<input type="file">` — **первый барьер** валидации (фильтр в диалоге выбора)
- `detectFileFormat()` — **второй барьер** (для drag-and-drop, где `accept` не работает на 100%)
- Backend — **третий барьер** (magic bytes проверка, см. рекомендации по безопасности)

---

## ❌ Типичные ошибки

### Ошибка 1: формат как user-input

```tsx
{/* НЕПРАВИЛЬНО — заставляем пользователя выбирать формат вручную */}
<FileFormatPicker value={fileFormat} onChange={setFileFormat} />
<FileUpload accept={fileFormat === 'csv' ? '.csv' : '.xlsx'} />
```

**Почему плохо:**
- Лишний шаг для пользователя
- Возможна ошибка: выбрал XLSX, загрузил CSV → backend упадёт на парсинге
- Если форматов 4+ → UI становится перегружен

### Ошибка 2: определение по MIME-типу

```tsx
{/* НЕНАДЁЖНО — MIME-тип может отсутствовать или быть неправильным */}
const fmt = file.type === 'text/csv' ? 'csv' : 'xlsx';
```

**Почему плохо:**
- Браузеры по-разному определяют MIME-тип (особенно для XLSB)
- `file.type` для drag-and-drop часто пустой
- Расширение — **более стабильный** источник истины

### Ошибка 3: чувствительность к регистру

```ts
{/* НЕПРАВИЛЬНО — файл `Report.XLSX` не будет распознан */}
const ext = fileName.split('.').pop();   // 'XLSX'
return SUPPORTED_FORMATS.includes(ext);  // ❌ всегда false для CAPS
```

**Правильно:** `fileName.toLowerCase()` перед извлечением расширения.

### Ошибка 4: файлы с двойными расширениями

```ts
{/* НЕПРАВИЛЬНО — split('.').pop() даст 'gz', а не 'csv' */}
const ext = 'data.csv.gz'.split('.').pop();  // 'gz'
```

**Правильно:** `lastIndexOf('.')` + `slice` — берём строго последнее расширение.

---

## 📍 Применение в проекте

| Паттерн | Файл | Функции |
|---------|------|---------|
| Утилита определения | `KiloImportService.Web/src/utils/fileFormat.ts` | `detectFileFormat`, `ACCEPT_ALL_SUPPORTED` |
| Валидация при выборе | `KiloImportService.Web/src/components/FileUpload/FileUpload.tsx` | `validateAndSelect` |
| Отображение формата | `KiloImportService.Web/src/components/FileUpload/FileUpload.tsx` | `Status` badge |
| Использование в форме | `KiloImportService.Web/src/App.tsx` | `useMemo(() => detectFileFormat(file.name))` |

---

## 🔌 Соответствие на backend

### .NET — `FileParserFactory`

```csharp
// FileParserFactory.cs
public static FileFormat ResolveFormat(string extension) => extension.ToLowerInvariant() switch
{
    ".csv"  => FileFormat.Csv,
    ".xls"  => FileFormat.Xls,
    ".xlsb" => FileFormat.Xlsb,
    ".xlsx" => FileFormat.Xlsx,
    _ => throw new NotSupportedException($"Расширение {extension} не поддерживается")
};
```

### ⚠️ Согласованность UI и backend

- Список `SUPPORTED_FORMATS` в UI **должен совпадать** с поддерживаемыми форматами в `FileParserFactory`
- При добавлении нового формата — обновить **оба** места
- В будущем можно вынести список форматов в отдельный endpoint `GET /api/import/supported-formats`

---

## 🎯 Чек-лист при добавлении нового формата

- [ ] Добавил в `FileFormat` union (`src/types/import.ts`)
- [ ] Добавил в `SUPPORTED_FORMATS` массив
- [ ] Добавил MIME-тип в `ACCEPT_ALL_SUPPORTED`
- [ ] Реализовал backend-парсер `IFileParser` с соответствующим `SupportedFormat`
- [ ] Зарегистрировал парсер в DI на backend
- [ ] Добавил unit-тест парсера
- [ ] Обновил FAQ в `import-excel-service-architecture.md`
