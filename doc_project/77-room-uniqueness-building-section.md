# 🚪 Уникальность Room: `BuildingSection` в ключе матча

## 📋 Описание

В импорте Помещений (rooms-form) идентификация существующего Room в Visary
происходит по составному ключу:

> **Section × Kind × Number × BuildingSection**

Колонка «Подъезд/Секция» из Excel (`BuildingSection` в маппере) — обязательный
компонент ключа. Две строки с одинаковыми `Section`/`Kind`/`Number`, но
разными `BuildingSection` — это **разные** помещения, импорт должен
создавать каждое.

---

## 🐛 Что было сломано

Раньше match шёл только по `Section × Kind × Number`:

```csharp
var match = roomsInSection.Data.FirstOrDefault(r =>
    (kindId is null || r.Kind?.ID == kindId.Value)
    && (string.Equals(r.ExplicationNumber, roomNumber, ...)
     || string.Equals(r.Number,            roomNumber, ...)));
```

При импорте строк, где для одной секции «1.1» был один номер «3» в подъезде
«1» и тот же номер «3» в подъезде «2» — обе строки матчили **одну и ту же**
запись в Visary, и второй PATCH перезаписывал данные первого. Помещения
второго подъезда не создавались.

---

## ✅ Правильная реализация

```csharp
// RoomsFormImportMapper.ApplyAsync — блок 3 (Room match)
var roomNumber      = GetStringOrNull(v, "RoomNumber") ?? string.Empty;
var kindId          = GetIntOrNull(v, "RoomKindId");
var buildingSection = GetStringOrNull(v, "BuildingSection") ?? string.Empty;

var match = roomsInSection.Data.FirstOrDefault(r =>
    (kindId is null || r.Kind?.ID == kindId.Value)
    && (string.Equals(r.ExplicationNumber, roomNumber, StringComparison.OrdinalIgnoreCase)
     || string.Equals(r.Number,            roomNumber, StringComparison.OrdinalIgnoreCase))
    && string.Equals(
            (r.BuildingSection ?? string.Empty).Trim(),
            buildingSection.Trim(),
            StringComparison.OrdinalIgnoreCase));
```

### ⚠️ Важно

- **Нормализация перед сравнением** — `(r.BuildingSection ?? "").Trim()`.
  В файлах часто бывают хвостовые пробелы; в Visary поле может быть `null`,
  пустой строкой или строкой с пробелами — всё это считаем «отсутствует».
- **`OrdinalIgnoreCase`** — `BuildingSection` обычно числовое («1», «2»), но
  встречаются «А», «Б» — раскладку клавиатуры/регистр пользователь не
  гарантирует.
- **Один и тот же `buildingSection` уже идёт в `UniqueNumber`** при создании
  Room (`uniqueNumber = $"{roomNumber}_{sectionTitle}_{buildingSection}"`).
  Согласованность сохранена: два разных подъезда → два разных `UniqueNumber`
  → две разные записи в Visary.

---

## ❌ Типичные ошибки

```csharp
// ❌ НЕПРАВИЛЬНО: матчить без BuildingSection
var match = roomsInSection.Data.FirstOrDefault(r =>
    r.Kind?.ID == kindId && r.Number == roomNumber);
// → строки разных подъездов с одинаковым номером PATCH-ят друг друга
//    и помещения «теряются».
```

```csharp
// ❌ НЕПРАВИЛЬНО: сравнение без нормализации
&& r.BuildingSection == buildingSection
// → "1" vs " 1" vs null будут считаться разными, импорт создаст дубликаты
//    для существующих помещений (обратная проблема).
```

```csharp
// ❌ НЕПРАВИЛЬНО: добавить BuildingSection как доп. условие через ||
&& (r.BuildingSection == buildingSection || r.Number == roomNumber)
// Слабее старого поведения; не разделяет помещения разных подъездов.
//    Использовать AND.
```

---

## 📍 Применение в проекте

| Компонент | Файл | Что меняется |
|-----------|------|--------------|
| Match Room | `KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs` — блок 3 в `ApplyAsync` | условие `&& BuildingSection.Trim() ==` |
| Объявление `buildingSection` | там же — переехало выше блока 3 (одно объявление вместо двух) | используется и в match, и в `uniqueNumber` |
| Поле в DTO | `Visary.Api.Client/Dto/VisaryEntities.cs:98` — `RoomRaw.BuildingSection` | уже было |
| Колонки listview | `Visary.Api.Client/ListView/ListViewClient.cs` — `RoomColumns` уже содержит `BuildingSection` | уже было |

---

## 🎯 Чек-лист «как протестировать»

- [ ] Создать Excel с двумя строками одного типа помещения и одного номера, но
      разными подъездами (`«1»` и `«2»`).
- [ ] Импорт.
- [ ] В Visary — **две** записи Room в секции, одна с
      `BuildingSection = "1"`, вторая с `"2"`.
- [ ] Повторный импорт того же файла — никаких изменений (идемпотентность):
      каждая строка PATCH-нет «свою» комнату, не создаст дубликаты.
- [ ] Удалить одну из комнат в Visary, повторить импорт — должна
      воссоздаться, не задеть оставшуюся.
