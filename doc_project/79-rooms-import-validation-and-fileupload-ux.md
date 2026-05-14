# 🧹 Импорт «Помещения» — нормализация и валидация (RoomNumber/RoomsCount) + UX выбора файла

## 📋 Описание

Три связанные доработки импорта `rooms`:

1. **`RoomNumber`** — пользователи иногда пишут номер квартиры с префиксом
   («п1», «кв.7», «12А»). Visary хранит `ExplicationNumber` как строку, но
   ожидает только цифровую часть — иначе ДДУ/Room не находятся при повторных
   импортах и появляются дубли.
2. **`RoomsCount`** — для жилого типа «Квартира» обязательно указывать
   количество комнат. Раньше пустое значение молча уходило `null` в Visary.
3. **FileUpload UX** — иконка `showDelete` у `FileUploadItem`
   (`@alfalab/core-components`) визуально незаметна на больших файлах; нужна
   явная кнопка для удаления/замены выбранного файла **до** старта импорта.

---

## ✅ Правильная реализация

### 1. Нормализация номера помещения

```csharp
// KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs

// ── Поля поиска Room ────────────────────────────────────────────
// Из значения извлекаем только цифры: «п1» → «1», «12А» → «12».
// Если в файле остался текст вокруг числа, фиксируем в логе.
var roomNumberRaw = ReadString(row, RoomNumberAliases);
var roomNumber = ExtractDigitsOnly(roomNumberRaw);
if (string.IsNullOrWhiteSpace(roomNumber))
{
    rowErrors.Add(new RowError(string.Join(" / ", RoomNumberAliases), "required_missing",
        "Не указан номер помещения."));
}
else if (!string.Equals(roomNumberRaw, roomNumber, StringComparison.Ordinal))
{
    _log.LogDebug(
        "RoomsForm.Validate: row {Row} — номер помещения '{Raw}' нормализован в '{Numeric}' (удалены не-цифры).",
        row.SourceRowNumber, roomNumberRaw, roomNumber);
}

// ...

/// <summary>«п1» → «1»; «12А» → «12»; «кв. 7» → «7»; «—» → "".
/// Игнорирует все символы кроме цифр (включая точки/запятые).</summary>
private static string ExtractDigitsOnly(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
    return new string(raw.Where(char.IsDigit).ToArray());
}
```

### ⚠️ Важно

- `ExtractDigitsOnly` **отличается** от существующего `ExtractNumericPart`
  (последний сохраняет точки/запятые — используется для `SectionTitleNumeric`
  где «Лит 1.1» должен превратиться в «1.1», а не «11»).
- Для `RoomNumber` точка/запятая считаются «текстом» и удаляются: номер
  квартиры — всегда целое число.
- Нормализованное значение пишется в `ExplicationNumber` через
  `["RoomNumber"] = roomNumber` (строки 320–325 маппера) — там, где раньше
  было raw-значение.
- Если в файле было «п1» — оригинал остаётся в логе уровня Debug на случай
  будущей трассировки.

### 2. Required-валидация «Количество комнат» для квартиры

```csharp
var roomsCountRaw = ReadString(row, RoomsCountAliases);
int? roomsCount = TryParseNullableInt(roomsCountRaw, out var rcErr);
if (rcErr != null) rowErrors.Add(new RowError(string.Join(" / ", RoomsCountAliases), "invalid_number", rcErr));
// Если вид помещения «Квартира» — «Количество комнат» обязательно.
else if (roomsCount is null
         && !string.IsNullOrWhiteSpace(roomKindTitle)
         && string.Equals(roomKindTitle.Trim(), "Квартира", StringComparison.OrdinalIgnoreCase))
{
    rowErrors.Add(new RowError(string.Join(" / ", RoomsCountAliases), "required_missing",
        "Не указано количество комнат для квартиры."));
}
```

### ⚠️ Важно

- Проверка идёт **после** резолва `roomKindTitle` (строки 235–269 в маппере),
  потому что title может прийти из колонки «Тип/Название/Вид» **или** из
  имени листа («Квартиры» → «Квартира» через `ResolveKindBySheetName`).
- Сравнение по `Trim() + OrdinalIgnoreCase` — Visary возвращает Title с
  пробелами и в разном регистре (см. [77-room-uniqueness-building-section.md](77-room-uniqueness-building-section.md)).
- Парковки / нежилые / кладовые **не блокируются** — там `RoomsCount`
  естественно отсутствует.
- Ошибка `required_missing` уходит в `ImportError`, видна в UI на
  `SessionRowsTable` с указанием строки и колонки.

### 3. UI: явная кнопка удаления + замены файла

```tsx
// KiloImportService.Web/src/components/FileUpload/FileUpload.tsx
const handleRemove = () => {
  setError(null);
  if (inputRef.current) inputRef.current.value = '';   // 👈 сбрасываем native input
  onFileSelect(null);
};

// один <input> вынесен наружу обеих веток — ref всегда привязан
<input ref={inputRef} type="file" accept={ACCEPT_ALL_SUPPORTED}
       onChange={handleChange} style={{ display: 'none' }} />

// под FileUploadItem — две явные кнопки
<div className="file-uploaded__actions">
  <Button view="secondary" size={40} onClick={handleRemove}>Удалить файл</Button>
  <Button view="tertiary" size={40} onClick={handleClick}>Выбрать другой</Button>
</div>
```

```css
/* App.css */
.file-uploaded__actions {
  display: flex;
  gap: 12px;
  margin-top: 8px;
  flex-wrap: wrap;
}
```

### ⚠️ Важно

- **Один `<input type=file>` на компонент** — вынесен из обеих веток
  (`!file ? ... : ...`). Иначе React при переключении ветки размонтирует один
  input и монтирует другой, `inputRef.current` отдельным циклом, что ломает
  программное `.click()` сразу после `setFile(null)`.
- `inputRef.current.value = ''` обязателен в `handleRemove`, иначе при выборе
  файла **с тем же именем** повторно `onChange` не сработает (browser считает,
  что значение не изменилось).
- `showDelete + onDelete` у `FileUploadItem` **оставлены** — это даёт
  компактный «крестик» внутри плашки, дублируя явные кнопки ниже.

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО — оставить номер «как есть»
var roomNumber = ReadString(row, RoomNumberAliases);
// → Visary "п1" не равно "1", дубли при повторном импорте
```

```csharp
// НЕПРАВИЛЬНО — переиспользовать ExtractNumericPart для RoomNumber
var roomNumber = ExtractNumericPart(roomNumberRaw);
// → «1.1» останется «1.1», а нам нужны только цифры
```

```csharp
// НЕПРАВИЛЬНО — проверка RoomsCount до резолва roomKindTitle
if (roomsCount is null && fileType == "Квартира") { ... }
// → roomKindTitle ещё пуст, если он берётся из имени листа (см. 68-rooms-import.md)
```

```tsx
// НЕПРАВИЛЬНО — два <input ref={inputRef}> в обеих ветках
{!file ? <input ref={inputRef} ... /> : <input ref={inputRef} ... />}
// → React переключает DOM-узел; программный click может срабатывать на старом ref
```

```tsx
// НЕПРАВИЛЬНО — забыть сбросить input.value
const handleRemove = () => onFileSelect(null);
// → повторно выбрать тот же файл из системного диалога невозможно (onChange не сработает)
```

---

## 📍 Применение в проекте

| Что | Файл | Ключ |
|-----|------|------|
| Нормализация RoomNumber | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | `ExtractDigitsOnly`, блок «Поля поиска Room» |
| Required RoomsCount для квартиры | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | блок «Прочие поля» после `TryParseNullableInt(roomsCountRaw, …)` |
| UI кнопки | [FileUpload.tsx](../KiloImportService.Web/src/components/FileUpload/FileUpload.tsx) | `handleRemove`, `file-uploaded__actions` |
| Стили кнопок | [App.css](../KiloImportService.Web/src/App.css) | `.file-uploaded__actions` |

---

## 🔗 Связанная документация

- [68-rooms-import.md](68-rooms-import.md) — общий контракт импорта помещений
- [72-multi-sheet-import.md](72-multi-sheet-import.md) — резолв kind по имени листа
- [76-share-agreement-dedup.md](76-share-agreement-dedup.md) — нормализация номера влияет на дедуп ДДУ
- [77-room-uniqueness-building-section.md](77-room-uniqueness-building-section.md) — `Trim() + OrdinalIgnoreCase` для сравнения

---

## 🎯 Чек-лист

- [ ] При импорте «п1» в файле — в Visary уходит `ExplicationNumber=1`, в Debug-логе видно «нормализован»
- [ ] Пустая колонка «Количество комнат» + Kind=«Квартира» → ошибка `required_missing` в `SessionRowsTable`
- [ ] Пустая колонка «Количество комнат» + Kind=«Машиноместо/Кладовая» → импорт проходит без ошибки
- [ ] На странице импорта после выбора файла появляются кнопки «Удалить файл» и «Выбрать другой»
- [ ] Кнопка «Удалить файл» очищает state, кнопка «Выбрать другой» открывает системный диалог
- [ ] Повторный выбор файла **с тем же именем** срабатывает (input.value сброшен)
