# 🏠 Импорт «Помещения»: синонимы и plural-формы вида помещения

## 📋 Описание

Маппер «Помещения» резолвит вид помещения в Title справочника Visary
`RoomKind` (живой `listview/roomkind`, не локальный seed) в двух точках:

1. **Имя листа** — `ResolveKindBySheetName` (fallback, когда колонка
   «Тип/Название/Вид» пустая, см. [doc 90](./90-rooms-skip-unknown-kind-sheets.md));
2. **Колонка `Тип/Название/Вид`** — приоритетный источник в каждой строке.

До этой правки **обе точки расходились**: для имени листа применялись
plural-trim ([doc 116](./116-resolve-kind-multiword-plural.md)) и алиас
«ПСН», а ячейка проверялась строгим `kindByTitle.TryGetValue(raw.Trim())`.
В файлах заказчика это давало `fk_not_found` на семантически валидных
формулировках:

| Источник                                                  | Ожидалось         |
|-----------------------------------------------------------|-------------------|
| `Квартира-студия`                                         | `Квартира`        |
| `Нежилое помещение для коммерческого использования`       | `Нежилое помещение` |
| `Машино-место`, `Машино-место МГН`                        | `Машиноместо`     |
| `Апартамент` (singular, при справочнике `Апартаменты`)    | `Апартаменты`     |
| `Офисы`, `Комнаты`, `Студии`, `Гаражи`, `Нежилые помещения` | соответствующий singular |

Теперь обе точки идут через общий `ResolveKindByTitle` — нормализация
едина.

---

## ✅ Правильная реализация

### 1) Алиасы (синонимы + singular→plural)

[`RoomsFormImportMapper.cs`](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs)
— `RoomKindTitleAliases`:

```csharp
private static readonly Dictionary<string, string> RoomKindTitleAliases =
    new(StringComparer.OrdinalIgnoreCase)
    {
        // Аббревиатуры
        ["ПСН"]                                                = "Нежилое помещение",

        // Уточнения вида заказчика (свёртка до базового Title)
        ["Квартира-студия"]                                    = "Квартира",
        ["Нежилое помещение для коммерческого использования"]  = "Нежилое помещение",
        ["Машино-место"]                                        = "Машиноместо",
        ["Машино-место МГН"]                                    = "Машиноместо",

        // singular → plural-Title («Апартаменты»):
        // plural-trim даёт только обратное направление.
        ["Апартамент"]                                          = "Апартаменты",
    };
```

### 2) Единая функция резолва

```csharp
internal static (int? Id, string? Title) ResolveKindByTitle(
    string? raw, IDictionary<string, int> kindByTitle)
{
    if (string.IsNullOrWhiteSpace(raw)) return (null, null);
    var name = raw.Trim();

    // 1. Прямое совпадение
    if (kindByTitle.TryGetValue(name, out var id1)) return (id1, ...);

    // 2. Алиас
    if (RoomKindTitleAliases.TryGetValue(name, out var alias)
        && kindByTitle.TryGetValue(alias, out var idA)) return (idA, ...);

    // 3. Plural-trim per word + декартово произведение (см. doc 116)
    ...
}

internal static (int? Id, string? Title) ResolveKindBySheetName(
    string sheetName, IDictionary<string, int> kindByTitle)
    => ResolveKindByTitle(sheetName, kindByTitle);   // 👈 та же логика
```

### 3) Новый plural-суффикс `ые → ое`

`SingularCandidates` теперь возвращает **два** кандидата для `ые`-формы:

```csharp
if (last2 == "ые")
{
    yield return head2 + "ая"; // Кладовые → Кладовая
    yield return head2 + "ое"; // Нежилые  → Нежилое (для «Нежилые помещения»)
}
```

Кандидат, не совпавший ни с одним Title, безопасно отбрасывается.

### 4) Колонка «Тип/Название/Вид» в `ValidateAsync`

```csharp
else
{
    var (resolvedId, resolvedTitle) = ResolveKindByTitle(roomKindTitle, kindByTitle);
    if (!resolvedId.HasValue)
    {
        rowErrors.Add(new RowError(..., "fk_not_found",
            $"Вид помещения '{roomKindTitle}' не найден в справочнике RoomKind."));
    }
    else
    {
        kindId = resolvedId.Value;
        // Подменяем raw-значение каноническим Title — это идёт в Visary и в отчёт.
        if (!string.IsNullOrEmpty(resolvedTitle)) roomKindTitle = resolvedTitle;
    }
}
```

### ⚠️ Важно

- **Источник истины — живой справочник Visary** (`listview/roomkind`).
  Если канонический Title отсутствует в `kindByTitle` — алиас не
  срабатывает, plural-trim возвращает `null`, строка падает с
  `fk_not_found`. Хардкод списка видов запрещён.
- **`roomKindTitle` подменяется** на канонический Title после успешного
  резолва: дальше в `MappedValues["RoomKindTitle"]` и в отчёт уходит
  именно то, что есть в справочнике, а не исходное «Квартира-студия».
- **Substring-fallback по-прежнему запрещён** (см. [doc 90](./90-rooms-skip-unknown-kind-sheets.md)).
- **Case-insensitive** во всех точках (словарь, алиасы, plural-trim).
- **Порядок суффиксов в `SingularCandidates`** — 2-буквенные ДО
  1-буквенных, иначе «Кладовые → срез 'е' → Кладовы» создаст ложный
  шорт-цикл и «Кладовая» в перебор не попадёт (см. [doc 116](./116-resolve-kind-multiword-plural.md)).

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — для имени листа алиасы + plural-trim, для ячейки строгий equals
if (!kindByTitle.TryGetValue(roomKindTitle.Trim(), out kindId))
    rowErrors.Add(new RowError(..., "fk_not_found", ...));
```

Симптомы для пользовательских файлов:
- ячейка «Машино-место» → `fk_not_found`, при том что лист «Машино-места»
  резолвится в «Машиноместо»;
- ячейка «Апартамент» → `fk_not_found` (справочник содержит «Апартаменты»);
- ячейка «Нежилое помещение для коммерческого использования» (типовая
  формулировка из ДДУ) → `fk_not_found`.

```csharp
// НЕПРАВИЛЬНО — хардкод списка синонимов в коде валидации
if (raw == "Машино-место" || raw == "Машино-место МГН") raw = "Машиноместо";
```

Расползается по 2+ файлам, рассинхронизируется. Единая таблица
`RoomKindTitleAliases` + общий резолв — единый источник нормализации.

---

## 📍 Применение в проекте

| Что                                  | Файл                                                                                  | Что делает                                                                                  |
|--------------------------------------|---------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------|
| `RoomKindTitleAliases`               | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | Таблица алиасов: синонимы + singular→plural                                                 |
| `ResolveKindByTitle`                 | там же                                                                                | Общий резолв: direct → alias → plural-trim                                                  |
| `ResolveKindBySheetName`             | там же                                                                                | Делегирует в `ResolveKindByTitle` (имя для читаемости вызывающего кода)                     |
| `SingularCandidates` (новый суффикс) | там же                                                                                | Добавлено `ые → ое` для «Нежилые помещения»                                                 |
| Колонка «Тип/Название/Вид»           | там же, `ValidateAsync`                                                               | Использует `ResolveKindByTitle`; подменяет raw-Title каноническим после успешного резолва   |
| Tests                                | [RoomsFormImportMapperTests.cs](../KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperTests.cs) | `ResolveKindByTitle_SynonymAliases_Doc138`, `ResolveKindByTitle_PluralForms_Doc138`         |

---

## 🎯 Чек-лист при добавлении новых видов / алиасов

- [ ] Если новая формулировка — **синоним** уже существующего Title:
      добавить пару в `RoomKindTitleAliases`. Хардкод-условий в
      `ValidateAsync` не писать.
- [ ] Если новая формулировка — **plural** существующего Title:
      проверить, что `SingularCandidates` уже даёт нужный кандидат
      (большинство русских окончаний покрыты). Если нет — расширить
      `SingularCandidates`, 2-буквенные ДО 1-буквенных.
- [ ] Добавить `InlineData`/`Assert.Equal` в
      `ResolveKindByTitle_SynonymAliases_Doc138` или
      `ResolveKindByTitle_PluralForms_Doc138`.
- [ ] Проверить отсутствие регрессий: `Кв_01.04.26` / `Итог` /
      служебные листы по-прежнему `null` (substring-fallback запрещён).
- [ ] Документировать новый алиас/суффикс в этом файле.
