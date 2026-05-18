# 🔢 Импорт «Помещения»: нормализация «Колич. комнат»

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-18
**Дополняет**: [68-rooms-import.md](68-rooms-import.md), [83-rooms-shifted-header-row.md](83-rooms-shifted-header-row.md)

В пользовательских реестрах квартир (например, «Ежевика короткая 1.xlsx»,
«Волга короткая 1.xlsx») значение «Колич. комнат» приходит в свободной
форме: `1 к.`, `1 к`, `п1`, `1п`, `10 к`, `3-к`, `3 ком.`, `студия`. Раньше
маппер ждал чистое целое и на любые такие значения выдавал `invalid_number`
— заказчик получал ошибку «`1 к.` не является валидным целым числом» для
каждой нормальной однушки.

Теперь маппер вытаскивает **первую непрерывную группу цифр** и принимает
её за число комнат. Алиасы шапки тоже расширены.

---

## ✅ Правильная реализация

### 1. Расширенные алиасы

`RoomsFormImportMapper.RoomsCountAliases`:

```csharp
private static readonly string[] RoomsCountAliases = [
    "Колич. комнат", "Колич комнат",    // 👈 с точкой и без — в реальных файлах встречаются обе
    "Количество комнат", "Кол-во комнат", "Кол. комнат",
    "Количество",
    "RoomsNumber"];
```

В файле «Ежевика короткая 1.xlsx» заголовок — `Колич комнат` (без точки).
Без второго алиаса столбец не находился и `roomsCount=null` приводил к
`required_missing` на каждой строке-квартире.

### 2. Извлечение первой группы цифр

```csharp
internal static int? ExtractFirstRunOfDigits(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    var sb = new System.Text.StringBuilder();
    foreach (var ch in raw)
    {
        if (char.IsDigit(ch)) sb.Append(ch);
        else if (sb.Length > 0) break;   // 👈 БЕРЁМ ПЕРВЫЙ run, не клеим разрозненные цифры
    }
    if (sb.Length == 0) return null;
    return int.TryParse(sb.ToString(),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
}
```

Поведение на примерах:

| Вход     | Выход | Пояснение                                 |
|----------|------:|-------------------------------------------|
| `1 к.`   | `1`   | суффикс с пробелом и точкой               |
| `1 к`    | `1`   | без точки                                 |
| `1к`     | `1`   | без пробела                               |
| `2 к.`   | `2`   |                                           |
| `3 ком.` | `3`   |                                           |
| `3-к`    | `3`   | тире как разделитель                      |
| `10 к`   | `10`  | многозначное число читается целиком       |
| `п1`     | `1`   | префиксная буква                          |
| `1п`     | `1`   | суффиксная буква                          |
| `студия` | `null`| цифр нет — для квартиры это `required_missing` |
| `—`      | `null`|                                           |
| `1 к. 2` | `1`   | ⚠️ берём ПЕРВЫЙ run, не «12»; «2» — заметка, не комната |

### 3. Применение в маппере

```csharp
var roomsCountRaw = ReadString(row, RoomsCountAliases);
int? roomsCount = ExtractFirstRunOfDigits(roomsCountRaw);
if (roomsCount.HasValue
    && !string.Equals(roomsCountRaw, roomsCount.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
{
    _log.LogDebug(
        "RoomsForm.Validate: row {Row} — «Колич. комнат» '{Raw}' нормализовано в {N}.",
        row.SourceRowNumber, roomsCountRaw, roomsCount.Value);
}
// Если raw непустой, но числа нет — оставляем null. required_missing
// ниже сработает только для Kind=Квартира.
if (roomsCount is null
    && !string.IsNullOrWhiteSpace(roomKindTitle)
    && string.Equals(roomKindTitle.Trim(), "Квартира", StringComparison.OrdinalIgnoreCase))
{
    rowErrors.Add(new RowError(string.Join(" / ", RoomsCountAliases), "required_missing",
        "Не указано количество комнат для квартиры."));
}
```

### ⚠️ Важно

- Берём **ПЕРВЫЙ** непрерывный run цифр, не клеим. Иначе «1 к. 2» дал бы
  фейковую двенашку.
- Если **raw непустой, но числа нет** (`студия`, `—`) — это **не**
  `invalid_number`. Маппер молча оставляет `roomsCount=null`. Жалоба
  будет только если `Kind=Квартира` — тогда `required_missing`.
- `InternalsVisibleTo` в `KiloImportService.Api.csproj` позволяет тестам
  напрямую вызывать `ExtractFirstRunOfDigits`.

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО — int.TryParse не распознаёт «1 к.» / «п1» и плодит
// invalid_number на нормальных квартирах:
int? roomsCount = int.TryParse(raw, out var n) ? n : null;
if (roomsCount is null) rowErrors.Add(new RowError(..., "invalid_number", ...));
```

```csharp
// НЕПРАВИЛЬНО — char.IsDigit без break склеит «1 к. 2» в «12»:
var digits = new string(raw.Where(char.IsDigit).ToArray());
return int.Parse(digits);  // ← 12 вместо 1
```

```csharp
// НЕПРАВИЛЬНО — алиас «Количество комнат» без «Колич комнат» (без точки):
// файл от заказчика с шапкой «Колич комнат» не находит столбец, и
// roomsCount=null приводит к required_missing для всех квартир.
private static readonly string[] RoomsCountAliases = ["Количество комнат"];
```

---

## 📍 Применение в проекте

| Компонент                                | Файл                                                     | Что изменилось |
|------------------------------------------|----------------------------------------------------------|----------------|
| `RoomsCountAliases`                      | `Domain/Mapping/RoomsFormImportMapper.cs`                | Добавлены 4 варианта без точки/с дефисом |
| `ExtractFirstRunOfDigits`                | `Domain/Mapping/RoomsFormImportMapper.cs`                | Новый internal helper |
| `Validate`-секция Колич. комнат          | `Domain/Mapping/RoomsFormImportMapper.cs`                | `TryParseNullableInt` → `ExtractFirstRunOfDigits` + лог нормализации |
| `InternalsVisibleTo: …Tests`             | `KiloImportService.Api.csproj`                           | Открыт internal-API для тестов |
| Тесты                                    | `KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperTests.cs` | 22 `InlineData` (включая `1 к.`, `п1`, `1п`, `студия`, `1 к. 2`) |

---

## 🎯 Чек-лист

- [x] Заголовок `Колич комнат` (без точки) добавлен в алиасы
- [x] `ExtractFirstRunOfDigits` берёт **первый** run, не клеит цифры
- [x] `студия`/`—`/пусто → `null` без `invalid_number`
- [x] Логи нормализации (`LogDebug`) показывают raw→normalized для аудита
- [x] Unit-тесты на 22 кейса, все зелёные
- [x] `required_missing` бьёт только для Kind=Квартира (для нежилых
      «Колич. комнат» опциональна)
- [ ] При появлении нового файла с экзотической формой записи —
      добавить тестовый кейс, прежде чем менять `ExtractFirstRunOfDigits`
