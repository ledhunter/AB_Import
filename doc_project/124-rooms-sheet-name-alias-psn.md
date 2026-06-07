# 🏷 Импорт «Помещения» — алиас имени листа «ПСН» → «Нежилое помещение»

## 📋 Описание

Заказчик присылает Квартирограмму, в которой нежилые помещения лежат на
листе с именем **`ПСН`** («помещение свободного назначения»). В живом
справочнике `RoomKind` Visary такого Title нет — есть только канонический
**«Нежилое помещение»**. Plural-trim эвристика [doc 116](./116-resolve-kind-multiword-plural.md)
аббревиатуру в Title не приводит, поэтому до правки лист молча
отфильтровывался третьим слоем защиты ([doc 90](./90-rooms-skip-unknown-kind-sheets.md))
и нежилые из файла в Visary не доезжали.

Решение — добавить **отдельный словарь алиасов** `SheetNameAliases` в
`RoomsFormImportMapper.ResolveKindBySheetName`, отрабатывающий **до**
plural-trim и **после** прямого совпадения с Title справочника. Алиас
«ПСН» → «Нежилое помещение» матчится case-insensitive после `Trim()`.

Если канонический Title (`Нежилое помещение`) отсутствует в живом
справочнике Visary — алиас молча не срабатывает, имя проваливается дальше
в plural-trim → `(null, null)`, лист пропускается как «неизвестный вид».
Это сохраняет инвариант: «маппер не знает Visary-схему, только справочник».

---

## ✅ Правильная реализация

`KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs`:

```csharp
// Алиасы коротких/отраслевых имён листов на канонические Title справочника
// RoomKind. Используются ДО plural-trim, чтобы аббревиатуры, которые
// эвристика «мн.→ед.ч.» не превратит в Title, всё равно резолвились.
private static readonly Dictionary<string, string> SheetNameAliases =
    new(StringComparer.OrdinalIgnoreCase)
    {
        // «ПСН» (помещение свободного назначения) — заказчик использует как
        // имя листа для нежилых помещений.
        ["ПСН"] = "Нежилое помещение",   // 👈 расширять только при подтверждённом запросе заказчика
    };

internal static (int? Id, string? Title) ResolveKindBySheetName(
    string sheetName, IDictionary<string, int> kindByTitle)
{
    if (string.IsNullOrWhiteSpace(sheetName)) return (null, null);
    var name = sheetName.Trim();

    // 1. Прямое совпадение с Title справочника
    if (kindByTitle.TryGetValue(name, out var id1))
        return (id1, FindMatchingTitle(name, kindByTitle));

    // 2. Алиас короткой/отраслевой формы (например, «ПСН» → «Нежилое помещение»).
    //    Если канонический Title отсутствует в живом справочнике Visary —
    //    провал в plural-trim, как для неизвестного имени.
    if (SheetNameAliases.TryGetValue(name, out var aliasTitle)
        && kindByTitle.TryGetValue(aliasTitle, out var idA))
        return (idA, FindMatchingTitle(aliasTitle, kindByTitle));

    // 3. Plural-trim per-word + декартово произведение  (см. doc 116)
    // …
}
```

### ⚠️ Важно

- **Порядок шагов**: алиас идёт **после** direct match, чтобы случайное
  совпадение алиаса с реальным Title справочника не «угнало» резолв.
  И **до** plural-trim — иначе короткая аббревиатура без singular/plural
  пары просто не пройдёт эвристику.
- **Source of truth — Title из живого справочника, а не алиас**.
  Возвращаем `FindMatchingTitle(aliasTitle, kindByTitle)`, чтобы в логе /
  RowActions фигурировал канонический Title с правильным регистром, а не
  «ПСН».
- **Алиас опционален**: при отсутствии канонического Title в справочнике —
  алиас не подменяет провал. Это сохраняет third-layer-защиту от
  опечаток в названии Kind на стенде.

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — подменять Title в kindByTitle при загрузке справочника
var kindByTitle = roomKindList.Data.ToDictionary(...);
kindByTitle["ПСН"] = kindByTitle["Нежилое помещение"];  // 👎 утечка алиаса в общий словарь
```

Почему нельзя:

- Тот же `kindByTitle` используется при резолве `roomKindTitle` из колонки
  строки (`RoomsFormImportMapper.cs:488`). Если пользователь напишет «ПСН»
  в колонке «Тип/Вид помещения» — а такой Title в Visary не существует —
  попадёт NotFound-warn'а не будет, а маппер тихо запишет помещение в
  «Нежилое». Алиас уровня имени листа ≠ алиас уровня значения колонки;
  смешивать нельзя.

```csharp
// НЕПРАВИЛЬНО — substring-fallback ради «ПСН»
if (kindByTitle.Keys.Any(k => name.Contains(k))) ...     // 👎 doc 90 — запрещено
```

Substring-сравнение «ПСН» с «Нежилое помещение» вообще не сработает (нет
общей подстроки), а если сделать наоборот (`k.Contains(name)`), то лист
«Кв» совпадёт с «Квартира», и исторические снапшоты «Кв_01.04.26» снова
поедут в импорт (см. [doc 90](./90-rooms-skip-unknown-kind-sheets.md)).

---

## 📍 Применение в проекте

| Компонент | Файл | Сущности |
|-----------|------|----------|
| Резолв имени листа в Kind | [KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | `SheetNameAliases`, `ResolveKindBySheetName` |
| Тесты | [KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperTests.cs](../KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperTests.cs) | `ResolveKindBySheetName_AliasPsn` |

---

## 🎯 Чек-лист добавления нового алиаса

- [ ] Заказчик подтвердил формулировку (короткая форма ↔ канонический Title)
- [ ] Канонический Title существует в живом справочнике `RoomKind` Visary
      на всех стендах (test / preprod / prod)
- [ ] Запись в `SheetNameAliases` добавлена с краткой расшифровкой в комментарии
- [ ] Добавлен unit-тест по образцу `ResolveKindBySheetName_AliasPsn`
      (положительный + case-insensitive + Trim + отсутствие канонического Title)
- [ ] Алиас НЕ дублирует существующий direct-match или plural-trim результат
- [ ] Алиас НЕ затрагивает резолв колонки «Тип/Вид помещения» в строке
      (см. ❌ Типичная ошибка)
