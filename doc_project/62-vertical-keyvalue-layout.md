# 🎨 Вертикальный key-value layout с управляющим листом этапов

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-06
**Контекст**: шаблон «Финмодель» («Параметры к переносу в АБ.xlsx»)

Шаблон — **не таблица**. Параметры расположены столбиком на листе `Inputs`:
- колонка **C** — название параметра (`Тип отделки`, `Площадь`, …);
- колонки **H, I, J, …** — значения **по этапам** (этап 1, этап 2, …).

Сколько колонок-этапов читать — задаёт лист `Control`: в столбце **F** строка
`Выбрать количество этапов`, значение в **G** (1 = читаем только H, 2 = H+I, …).

Текущий `XlsxParser` (табличный) не подходил — он брал первый лист и первую
строку как заголовки, отсюда `column_not_found` и потом **116 строк-«фантомов»** с
пустыми значениями, когда мы наивно сканировали все колонки правее H.

Решение — раскладка `FileLayoutHint.KeyValueVertical` + ссылка `StageCountReference`,
которые маппер декларативно объявляет в своём `LayoutHint`.

> 🔁 См. также: `61-finmodel-file-level-column-error.md`, `23-finmodel-import.md`.

---

## ✅ Правильная реализация

### Контракт layout

```csharp
// Domain/Importing/FileLayoutHint.cs
public abstract record FileLayoutHint
{
    public static FileLayoutHint Default { get; } = new Tabular();
}

public sealed record Tabular : FileLayoutHint;

public sealed record KeyValueVertical(
    string SheetName,                  // лист (case-insensitive)
    string KeyColumn,                  // колонка названий параметров: "C"
    string ValueStartColumn,           // первая колонка-этап:        "H"
    StageCountReference? StageCount = null
) : FileLayoutHint;

public sealed record StageCountReference(
    string SheetName,                  // "Control"
    string KeyColumn,                  // "F" — там название параметра
    string ValueColumn,                // "G" — там число
    string ParameterName               // "Выбрать количество этапов"
);
```

### Маппер объявляет, как читать его шаблон

```csharp
// Domain/Mapping/FinModelImportMapper.cs
public FileLayoutHint LayoutHint { get; } = new KeyValueVertical(
    SheetName: "Inputs",
    KeyColumn: "C",
    ValueStartColumn: "H",
    StageCount: new StageCountReference(
        SheetName: "Control",
        KeyColumn: "F",
        ValueColumn: "G",
        ParameterName: "Выбрать количество этапов"));
```

### Парсер: `KeyValueVertical` ветка XlsxParser

```csharp
private static ParseResult ParseKeyValueVertical(XLWorkbook wb, KeyValueVertical layout, ...)
{
    // 1. Лист Inputs (case-insensitive); если нет — file-level error со списком листов.
    var sheet = wb.Worksheets.FirstOrDefault(w =>
        string.Equals(w.Name, layout.SheetName, StringComparison.OrdinalIgnoreCase));

    // 2. Сканируем колонку C: собираем (row, key_text) для непустых ячеек.
    //    Эти key_text станут ключами в Cells каждого ParsedRow.
    var keyByRow = new List<(int Row, string Key)>();
    for (int r = 1; r <= lastRow; r++) { /* ... */ }

    // 3. Если задан StageCount — открываем Control, ищем "Выбрать количество этапов"
    //    в колонке F, берём int из G той же строки. → maxStages = N.
    int? maxStages = layout.StageCount is { } sc ? ReadStageCount(wb, sc) : null;

    // 4. Идём по колонкам-этапам H..H+N-1 (или до lastCol без N).
    int stopCol = maxStages.HasValue
        ? Math.Min(lastCol, valueStartCol + maxStages.Value - 1)
        : lastCol;

    for (int c = valueStartCol; c <= stopCol; c++)
    {
        // Один ParsedRow на этап с {название_параметра → значение_в_этой_колонке}.
        // SourceRowNumber = индекс колонки (8 для H), Sheet = "Inputs (H)".
        // Если maxStages задан — выпускаем row даже на пустой этап (для value_empty).
        // Без maxStages — пропускаем пустые (legacy fallback).
    }
}
```

### ⚠️ Важно

- **Имена листов и колонок ищутся case-insensitive** (`OrdinalIgnoreCase`). Excel-шаблоны живут в трёх регистрах одновременно.
- **`StageCount` обязателен для шаблонов с «запасными» колонками.** Без него парсер схватит мусор справа от H и наплодит row-spam (как было в первой итерации — 116 ошибок).
- **Парсер выпускает по одному `ParsedRow` на каждую колонку-этап**, со словарём `{параметр → значение}`. Маппер видит каждую стадию как обычную строку и валидирует её **независимо**.
- **С `StageCount` парсер эмитит ровно N строк, даже если этап пустой.** Это нужно, чтобы маппер показал `value_empty` именно для конкретного этапа, а не молча пропустил его.
- **`SourceRowNumber` = индекс колонки-значения** (H=8, I=9, …). В отчёте пользователь видит, какая именно колонка дала строку.
- **`Sheet = "Inputs (H)"`** — добавляем буквенный идентификатор колонки, чтобы трассировать конкретный этап в UI.
- **Парсер НЕ поддерживает `KeyValueVertical` для CSV/XLS/XLSB** — возвращает file-level `parse_failure` с понятным сообщением. Шаблон — XLSX-only.

---

## ❌ Типичная ошибка

### 1. Не задавать `StageCount` → лишние «фантомные» строки

```csharp
// ❌ Без ограничения по этапам
public FileLayoutHint LayoutHint { get; } = new KeyValueVertical(
    "Inputs", "C", "H");  // нет StageCount!
```

**Симптом**: «Всего строк: 116, С ошибками: 116» — все одинаковые `value_empty`.
Шаблон оставляет колонки I, J, …, BL пустыми «про запас» — парсер сканирует
их все и каждой эмитит ParsedRow.

### 2. Жёстко захардкодить номер строки параметра

```csharp
// ❌ "Тип отделки" гарантированно в C28
var value = sheet.Cell(28, 8).GetString();  // H28
```

**Почему ломается**: в разных версиях шаблона строка может быть другой.
**Правильно**: ищем по тексту в колонке-ключе (`KeyColumn`), а не по номеру строки.

### 3. Захардкодить регистр имени листа

```csharp
// ❌ Строгое сравнение
if (sheet.Name != "Inputs") return error;
```

В Excel-шаблонах живут «inputs», «Inputs», «INPUTS» одновременно — теряем 1/3 файлов.

---

## 📍 Применение в проекте

| Компонент | Файл | Назначение |
|-----------|------|-----------|
| Контракт | [Domain/Importing/FileLayoutHint.cs](../KiloImportService.Api/Domain/Importing/FileLayoutHint.cs) | `Tabular` / `KeyValueVertical` / `StageCountReference` |
| Парсер | [Domain/Importing/Parsers/XlsxParser.cs](../KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs) | Метод `ParseKeyValueVertical` + helper `ReadStageCount` |
| Контракт парсера | [Domain/Importing/IFileParser.cs](../KiloImportService.Api/Domain/Importing/IFileParser.cs) | `ParseAsync(stream, FileLayoutHint?, ct)` |
| Контракт маппера | [Domain/Mapping/IImportMapper.cs](../KiloImportService.Api/Domain/Mapping/IImportMapper.cs) | `FileLayoutHint LayoutHint => FileLayoutHint.Default` |
| Финмодель | [Domain/Mapping/FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `LayoutHint = new KeyValueVertical("Inputs", "C", "H", new StageCountReference("Control","F","G","Выбрать количество этапов"))` |
| Пайплайн | [Domain/Pipeline/ImportPipeline.cs](../KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs) | `parser.ParseAsync(stream, mapper.LayoutHint, ct)` |
| Тесты | [KiloImportService.Api.Tests/Importing/XlsxParserTests.cs](../KiloImportService.Api.Tests/Importing/XlsxParserTests.cs) | 8 SkippableFact'ов под KV layout (StageCount=1/3, missing Control, empty stage, …) |

---

## 🎯 Чек-лист (при добавлении нового key-value импорта)

- [ ] У маппера переопределён `LayoutHint` с `KeyValueVertical(SheetName, KeyColumn, ValueStartColumn)`.
- [ ] Если шаблон оставляет «запасные» колонки справа от ValueStartColumn — обязательно задан `StageCount`.
- [ ] Имена листа и колонок берутся **из реального шаблона** (открыть Excel и проверить буквы), а не «по аналогии».
- [ ] Параметр в `StageCountReference.ParameterName` написан **как в файле**, символ в символ (поиск case-insensitive, но опечатки не прощаются).
- [ ] Маппер не лезет в Excel напрямую — работает только с `row.Cells[name]`. Логика layout инкапсулирована в парсере.
- [ ] Тест `KeyValueVertical_*` с StageCount=1/3, missing Control, empty stage column.

---

## 🧪 Связанный паттерн: декларативные подсказки парсеру

`FileLayoutHint` — пример **открытого контракта**: маппер декларирует *что* за раскладка ему нужна, парсер инкапсулирует *как* эту раскладку прочитать. Завтра появится `MultiSheetTabular` (один импорт = N листов с разными колонками) — добавим record-наследник, новую ветку switch в парсере, маппер не меняется.

---

**Версия**: 1.0
**Дата**: 2026-05-06
