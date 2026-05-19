# 🎯 Импорт «Помещения»: пропуск листов с именем, не соответствующим RoomKind

## 📋 Описание

`RoomsFormImportMapper.ValidateAsync` обходит все листы, прошедшие
strict-skip `HeaderAnchors` ([83](83-rooms-shifted-header-row.md)) и фильтр
скрытых ([88](88-xlsx-skip-hidden-sheets.md)). Среди оставшихся в реальных
пользовательских файлах попадаются «исторические снапшоты» с тем же набором
колонок, что и реестр: `Кв_01.04.26`, `Кв_01.03.26 (2)`, `Кв_01.02.26 (2)`, …
Это не реестр помещений — это копия предыдущего состояния файла, оставленная
автором «на память».

До этой правки такие листы валидировались и попадали в БД как помещения. На
файле `UC9NVP_Ежевика_01.04.2026.xlsx` это давало:
- 187 строк из «Квартира» (актуальный реестр) — нужно
- 63 строки из «Кв_01.04.26» — НЕ нужно
- 60 строк из «Кв_01.03.26 (2)» — НЕ нужно

Решение: обрабатывать **только** листы, чьё имя резолвится в `RoomKind`
из живого справочника Visary (`Квартира`, `Машиноместо`, `Кладовая`,
`Апартаменты`, `Студия`, `Гараж`, `Нежилое помещение`, `Коммерческое помещение`,
`Офис`, `Комната`), включая русские plural-формы («Квартиры», «Машиноместа»).

---

## ✅ Правильная реализация

```csharp
// RoomsFormImportMapper.ValidateAsync
var sheetKindCache = new Dictionary<string, (int? Id, string? Title)>(...);
var skippedSheetsByKind = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var sheetName in dataRows.Select(r => r.Sheet).Distinct(...))
{
    var (sId, sTitle) = ResolveKindBySheetName(sheetName, kindByTitle);
    sheetKindCache[sheetName] = (sId, sTitle);
    if (sId.HasValue) { /* лог + дальнейшая обработка */ }
    else
    {
        skippedSheetsByKind.Add(sheetName);          // 👈 имя не из справочника → skip
        _log.LogInformation("лист '{Sheet}' пропущен — имя не соответствует RoomKind...", sheetName);
    }
}

if (skippedSheetsByKind.Count > 0)
{
    dataRows = dataRows.Where(r => !skippedSheetsByKind.Contains(r.Sheet)).ToList();
    if (dataRows.Count == 0)
    {
        fileErrors.Add(new RowError(null, "no_data",
            "В файле нет ни одного листа с именем, соответствующим виду помещений..."));
        return new ValidationResult([], fileErrors);
    }
}
```

`ResolveKindBySheetName` уже умеет:
1. Точное совпадение `kindByTitle[sheetName]` (case-insensitive).
2. Plural-trimming для русских окончаний: «Квартиры» → «Квартир»/«Квартира»,
   «Машиноместа» → «Машиноместо», «Кладовые» → «Кладовая» и т.п.
3. Возвращает `(null, null)` если ничего не подошло — это и есть сигнал «skip».

### ⚠️ Важно

- **Substring-fallback НЕ используется** сознательно. Иначе «Кв_01.04.26» по
  префиксу «Кв» совпадёт с «Квартира», и сноха-фикс не сработает.
- Если ВСЕ листы фильтруются — отдаём file-level error `no_data` со списком
  пропущенных листов. Это лучше, чем «успешный импорт нулевой».
- Этот фильтр применяется **после** strict-skip `HeaderAnchors` и **после**
  фильтра скрытых листов: три независимых слоя в правильном порядке.
- Раз ResolveKindBySheetName использует справочник Visary напрямую, а не
  хардкод — добавление нового `RoomKind` на бэкенде автоматически расширяет
  список «принимаемых» имён листов. Хардкод не нужен.

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО: предупреждаем в логе, но всё равно валидируем
if (!sId.HasValue)
{
    _log.LogWarning("лист '{Sheet}' имеет неизвестный вид", sheetName);
    // → строки листа попадают в mappedRows, чаще всего с required_missing,
    //   захламляют отчёт и могут случайно пройти валидацию через колонку
    //   «Тип/Название/Вид»  →  в БД оседают помещения из исторических снапшотов.
}
```

```csharp
// НЕПРАВИЛЬНО: жёсткий хардкод список листов
private static readonly HashSet<string> AllowedSheets =
    new(StringComparer.OrdinalIgnoreCase) { "Квартира", "Квартиры", "Машиноместо", ... };
// При добавлении нового RoomKind в Visary этот список не обновится; ручная
// синхронизация с справочником = технический долг.
```

---

## 📍 Применение в проекте

| Компонент | Файл | Что делает |
|-----------|------|------------|
| `RoomsFormImportMapper.ValidateAsync` | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | Строит `skippedSheetsByKind` по пустому результату `ResolveKindBySheetName`, фильтрует `dataRows` до основного цикла |
| `ResolveKindBySheetName` | там же | Уже существующая функция: точное + plural-trim матч в `kindByTitle` (живой Visary справочник) |
| Парсер `XlsxParser` | [XlsxParser.cs](../KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs) | Слой 1: `HeaderAnchors` strict-skip ([83](83-rooms-shifted-header-row.md)); Слой 2: скрытые листы ([88](88-xlsx-skip-hidden-sheets.md)). Слой 3 (этот док) — на стороне маппера |

---

## 🎯 Чек-лист

- [x] `skippedSheetsByKind` собирается параллельно с `sheetKindCache`
- [x] `dataRows` фильтруется ДО основного валидационного цикла
- [x] file-level `no_data` если все листы отфильтрованы
- [x] Логи: per-sheet INFO о пропуске + суммарное «отфильтровано N строк из M листов»
- [x] Не дублирует фильтрацию `XlsxParser` (HeaderAnchors + visibility) — три независимых слоя
- [x] Использует справочник Visary, не хардкод
