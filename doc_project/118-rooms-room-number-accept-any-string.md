# 🏷 Импорт «Помещения» — `RoomNumber` принимается как есть (включая текст)

## 📋 Описание

В импорте «Квартирограммы» (`rooms`) поле **«Номер помещения/Квартира/Номер
квартиры»** перестало нормализоваться. Любое непустое значение из ячейки
(после `Trim()`) пишется в `Room.ExplicationNumber` и используется в
композитных ключах поиска как есть.

**Зачем:** заказчик использует не-числовые обозначения для нежилых
объектов (кладовые, паркинг, коммерция): «ПХ-15», «Кладовка-А», «А1»,
«офис-12». До правки `ExtractDigitsOnly` срезал из таких значений все
не-цифры — «ПХ-15» становилось «15», «Кладовка-А» → `""` →
`required_missing`. В результате:
- два разных нежилых с номерами «А1» и «Б1» схлопывались до «1» и
  PATCH-или друг друга;
- помещения с буквенно-только номером отбрасывались валидацией.

Отменяет цифровую нормализацию из [doc 79](./79-rooms-import-validation-and-fileupload-ux.md).

---

## ✅ Правильная реализация (после правки)

```csharp
// KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs

// ── Поля поиска Room ────────────────────────────────────────────
// Принимаем номер помещения как есть (включая текст и любые символы):
// «п1», «12А», «ПХ-15», «Кладовка-А» сохраняются без нормализации.
// См. doc 118 — заказчик использует не-числовые обозначения для нежилых.
var roomNumberRaw = ReadString(row, RoomNumberAliases);
var roomNumber = roomNumberRaw?.Trim() ?? string.Empty;
if (string.IsNullOrWhiteSpace(roomNumber))
{
    rowErrors.Add(new RowError(string.Join(" / ", RoomNumberAliases), "required_missing",
        "Не указан номер помещения."));
}
```

### ⚠️ Важно

- Единственное преобразование — `Trim()`. Никаких регэкспов, удаления
  префиксов, приведения регистра на этапе валидации.
- Регистр и whitespace нормализуются **позже**, в:
  - `RoomApplySnapshotStore.BuildKey` — `Trim()+ToLowerInvariant()` для
    snapshot-ключа (защита от того, что «А1» и «а1 » дадут разные ключи);
  - Find existing room в section — `Trim()+OrdinalIgnoreCase` сравнение
    с `Room.ExplicationNumber`/`Room.Number`.
- `RoomNumber` остаётся **обязательным** (`required_missing` при пустой
  строке) — пустую запись Visary не примет.
- `RoomNumber` участвует в композитном `uniqueNumber` для
  `Room.Number` (`{roomNumber}_{sectionTitle}_{buildingSection}` —
  см. [doc 77](./77-room-uniqueness-building-section.md)) — текст в
  любом из сегментов не ломает уникальность.
- Поле включено в `HashedMappedFields` (через `RoomNumber` ключ
  `MappedValues`), так что diff-skip продолжает работать.

---

## ❌ Старая реализация (до doc 118)

```csharp
// НЕПРАВИЛЬНО — обрубает текстовые номера, теряет данные для нежилых.
var roomNumber = ExtractDigitsOnly(roomNumberRaw);   // «ПХ-15» → «15»

private static string ExtractDigitsOnly(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
    return new string(raw.Where(char.IsDigit).ToArray());
}
```

**Симптомы, которые были у заказчика:**
1. Два разных нежилых «А1» и «Б1» из одной секции после нормализации оба
   получали `RoomNumber="1"`. В Visary создавался один Room, второй на
   повторном импорте PATCH-ил его (видны были «качели» площадей).
2. «Кладовка-А» / «офис-А» / «ПХ» — после `ExtractDigitsOnly` пустая
   строка → `required_missing` → строка вообще не доходила до Apply.
3. Чисто цифровые «1»/«п1»/«12А» работали корректно — поэтому регрессия
   была заметна только на нежилом импорте.

---

## 🔁 Эффект на повторные импорты после раскатки

При первом импорте уже импортированных файлов **после** раскатки правки:

- `MappedHash` для всех строк, где `roomNumberRaw != roomNumber`, изменится
  (раньше «п1»→«1», теперь «п1» в hash → новый хэш).
- `BuildKey` (через `Trim()+ToLowerInvariant()`) тоже изменится — старые
  snapshot-записи `import.room_apply_snapshots` для этих строк перестанут
  матчиться по ключу. Diff-skip → cache-miss → revalidation против Visary
  (см. [doc 106](./106-rooms-snapshot-revalidation.md)).
- Visary найдёт существующий Room по `Section × Kind × Number × BuildingSection`
  (см. [doc 77](./77-room-uniqueness-building-section.md)) — `OrdinalIgnoreCase`
  сравнение тривиально матчит «1» в Visary и «1» из новой версии файла, дубли
  **не создаются**. Будет один «толстый» PATCH-Apply, после которого новый
  snapshot уже использует raw-значение.

⚠️ **Если в Visary исторически уже лежит Room с `ExplicationNumber="1"`,
а в файле «п1» — теперь они НЕ сматчатся** (раньше «п1» нормализовалось в
«1» и Apply делал PATCH; теперь Apply создаст НОВЫЙ Room с
`ExplicationNumber="п1"`). Это поведение, которого хотел заказчик: «п1» —
**другая** сущность, не «1». Если у конкретного клиента ожидается совпадение,
файл нужно поправить на стороне источника.

---

## 📍 Применение в проекте

| Компонент | Файл | Что изменилось |
|-----------|------|----------------|
| Валидатор RoomsForm | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | Блок «Поля поиска Room» — `roomNumber = roomNumberRaw?.Trim()` вместо `ExtractDigitsOnly` |
| Helper | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | `ExtractDigitsOnly` удалён |
| Snapshot ключ | [RoomApplySnapshotStore](../KiloImportService.Api/Data/RoomApplySnapshotStore.cs) | Без изменений — `Trim()+ToLowerInvariant()` справляется с raw-текстом |
| Документация | [doc 79](./79-rooms-import-validation-and-fileupload-ux.md) | Раздел 1 помечен как отменённый, ссылается сюда |

---

## 🎯 Чек-лист

- [x] `ExtractDigitsOnly` удалён из маппера и из доков (поиск даёт 0)
- [x] `required_missing` сохраняется на пустой/whitespace-строке (для Visary `Number` пустой не пройдёт)
- [x] Сборка + 103 теста `RoomsForm*`/`RoomApplySnapshot*` зелёные
- [x] `BuildKey` устойчив к не-числовым `roomNumber` (нормализация Trim+lower осталась)
- [x] [doc 79](./79-rooms-import-validation-and-fileupload-ux.md) обновлён со ссылкой на эту запись
- [ ] При первой раскатке — ожидаем один «толстый» Apply (PATCH/cache-miss snapshot'а) на ранее импортированных файлах; cache наполнится новыми ключами на следующем импорте

---

## 💡 Урок

Цифровая нормализация input-полей — соблазнительно «универсальный» приём,
но он молча теряет данные, если в домене допустимы не-числовые значения.
Если ключ уникальности в downstream системе — строковый
(`Section × Kind × Number × BuildingSection` со `OrdinalIgnoreCase`), и
unique-индекс этого ключа выдерживает кириллицу/спецсимволы, нормализацию
лучше **не** вводить. Вместо «делаем чище» правильнее «принимаем как есть и
доверяем уникальному ключу». Раньше нормализация ловила «п1» от опечаток
оператора — но цена этого ловли (потеря «ПХ-15» / «А1») оказалась выше.
