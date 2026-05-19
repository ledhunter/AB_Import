# 🙈 XLSX-парсер: пропуск скрытых листов (Hidden / VeryHidden)

## 📋 Описание

`XlsxParser` (модуль `KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs`)
читает книги Excel в обеих раскладках — `Tabular` (многолистовой) и `KeyValueVertical`
(Финмодель). Пользователь Excel-а может пометить «черновой» / «технический» лист
как **Hidden** (видим в меню «Показать») или **VeryHidden** (видим только через VBA).
Тащить из таких листов строки в импорт **нельзя**: автор файла уже спрятал их от
себя — мы не должны их видеть в качестве источника данных.

> Регрессионный кейс: `Ежевика короткая 1.xlsx` (6 листов, все видимые) — в этом
> файле скрытых нет, фильтрация лишних листов работает через `HeaderAnchors`
> (см. [83](83-rooms-shifted-header-row.md)). Но требование сформулировано
> ортогонально и должно работать **до** анкоров: пользователь может прислать
> «такой же» файл, где «Черновик» помечен Hidden — он не должен попадать в отчёт.
>
> 🧩 **Три слоя фильтрации листов** (порядок исполнения):
> 1. `XlsxParser`: visibility (этот документ) — отсеивает Hidden/VeryHidden
> 2. `XlsxParser`: [HeaderAnchors strict-skip](83-rooms-shifted-header-row.md) — отсеивает листы без нужных колонок («Общий график», «Итог», «План»)
> 3. `RoomsFormImportMapper`: [имя листа из справочника RoomKind](90-rooms-skip-unknown-kind-sheets.md) — отсеивает исторические снапшоты («Кв_01.04.26» и т.п.)

---

## ✅ Правильная реализация

### Tabular: фильтрация на старте

```csharp
// XlsxParser.ParseTabular
var sheets = workbook.Worksheets
    .Where(w => w.Visibility == XLWorksheetVisibility.Visible)  // 👈 ДО RangeUsed/анкоров
    .ToList();
if (sheets.Count == 0)
{
    errors.Add(new ParseError(null, "Файл не содержит ни одного видимого листа."));
    return new ParseResult(allHeaders, rows, errors);
}
```

### KeyValueVertical: поиск только среди видимых

```csharp
// XlsxParser.ParseKeyValueVertical
var sheet = workbook.Worksheets.FirstOrDefault(w =>
    w.Visibility == XLWorksheetVisibility.Visible
    && string.Equals(w.Name, layout.SheetName, StringComparison.OrdinalIgnoreCase));
if (sheet is null)
{
    // В списке доступных — тоже ТОЛЬКО видимые, иначе подсказка введёт в заблуждение
    // («Inputs есть в файле — почему не найден?»).
    var available = string.Join(", ", workbook.Worksheets
        .Where(w => w.Visibility == XLWorksheetVisibility.Visible)
        .Select(w => $"'{w.Name}'"));
    errors.Add(new ParseError(null,
        $"Лист '{layout.SheetName}' не найден. Доступные листы: {available}."));
    return new ParseResult(headers, rows, errors);
}
```

### Control-лист (StageCount): аналогично

```csharp
// XlsxParser.ReadStageCount
var ctrl = workbook.Worksheets.FirstOrDefault(w =>
    w.Visibility == XLWorksheetVisibility.Visible
    && string.Equals(w.Name, sc.SheetName, StringComparison.OrdinalIgnoreCase));
```

### ⚠️ Важно

- Решение принимаем **до** `RangeUsed()` и **до** проверки `HeaderAnchors` — скрытый
  лист в логике парсера эквивалентен отсутствующему. Никаких `ParsedRow`, никакого
  `ParseError` per-sheet.
- Если **все** листы файла скрыты — на практике сам Excel/ClosedXML такой XLSX не
  открывают (требуется ≥1 видимого листа). Внешний catch в `ParseAsync` выловит
  исключение и вернёт file-level error «Не удалось прочитать XLSX: …». Защитный код
  «нет ни одного видимого листа» в `ParseTabular` остаётся как safety net на
  гипотетический случай, если ClosedXML расслабит ограничение.
- Для `KeyValueVertical` / `ReadStageCount` скрытый = «не найден». Это сознательное
  поведение: иначе пользователь спрячет «Control», получит молчаливое чтение и не
  поймёт, почему импорт ведёт себя странно.
- `XLWorksheetVisibility` имеет 3 значения: `Visible`, `Hidden`, `VeryHidden`.
  Фильтр `== Visible` отсекает оба «скрытых» варианта одной строкой.

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО: workbook.Worksheets возвращает И скрытые тоже.
// До этой правки парсер тащил из «Черновик»-листа строки наравне с «Квартиры».
var sheets = workbook.Worksheets.ToList();
```

```csharp
// НЕПРАВИЛЬНО: фильтр только в Tabular, забыт в KeyValueVertical.
// → Пользователь скрывает «Inputs» в шаблоне Финмодели → парсер всё равно
//   читает оттуда тип отделки, маппер делает PATCH, никто не понимает почему.
var sheet = workbook.Worksheets.FirstOrDefault(w =>
    string.Equals(w.Name, layout.SheetName, StringComparison.OrdinalIgnoreCase));
```

```csharp
// НЕПРАВИЛЬНО: в подсказке «доступные листы» показываем скрытые тоже.
// → Пользователь видит «'Inputs' не найден. Доступные: 'Inputs', 'Outputs'»
//   и теряет несколько часов в недоумении.
var available = string.Join(", ", workbook.Worksheets.Select(w => $"'{w.Name}'"));
```

---

## 📍 Применение в проекте

| Компонент | Файл | Что делает |
|-----------|------|------------|
| `XlsxParser.ParseTabular` | `KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs` | Обход видимых листов, union заголовков, эмит `ParsedRow` |
| `XlsxParser.ParseKeyValueVertical` | там же | Поиск таргет-листа Финмодели среди видимых |
| `XlsxParser.ReadStageCount` | там же | Поиск листа `Control` (число этапов) среди видимых |
| `XlsxParserTests.Tabular_SkipsHiddenSheets` | `KiloImportService.Api.Tests/Importing/XlsxParserTests.cs` | Регрессия: Hidden-лист не порождает строк |
| `XlsxParserTests.Tabular_SkipsVeryHiddenSheets` | там же | VeryHidden — тоже игнорируется |
| `XlsxParserTests.KeyValueVertical_SkipsHiddenInputsSheet` | там же | Inputs скрыт → «лист не найден», в подсказке только видимые |
| `XlsxParserTests.KeyValueVertical_StageCount_SkipsHiddenControlSheet` | там же | Control скрыт → file-level error |

---

## 🎯 Чек-лист

- [x] `ParseTabular` фильтрует `Visibility != Visible` ДО `RangeUsed`/анкоров
- [x] `ParseKeyValueVertical` ищет лист только среди видимых
- [x] `ReadStageCount` (управляющий лист `Control`) — то же поведение
- [x] В списке «доступные листы» — только видимые
- [x] Защитный code-path «нет видимого листа» в `Tabular` (на случай если ClosedXML расслабит ограничение Excel)
- [x] Unit-тесты Hidden / VeryHidden / KV-hidden / Control-hidden
- [x] Поведение `HeaderAnchors` строгого режима ([83](83-rooms-shifted-header-row.md)) не сломалось — фильтр видимости срабатывает раньше анкоров
