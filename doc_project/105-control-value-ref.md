# 🪞 ControlValueRef — скалярные значения с управляющего листа

## 📋 Описание

`ControlValueRef` — парсер-хинт для `KeyValueVertical`-раскладки, позволяющий вытащить
**одиночное** значение с **отдельного** листа (обычно «Control») и подставить его в
`Cells[OutputKey]` каждого эмитируемого `ParsedRow` основного KV-листа. Значение
одинаково для всех этапов (как `SingleValueOverride`), но лежит вне таблицы параметров.

Введён в [doc 104 v1.3](./104-finmodel-deal-precheck.md) для перевода «Номер договора»
с листа `Inputs` на лист `Control` (поле «Номер КД»). Полезен любым следующим импортам,
где служебные/мета-значения лежат на отдельном управляющем листе.

---

## 🎯 Зачем нужно

Бывает, что поле должно ехать в Visary, но в шаблоне оно лежит **не там**, где обычные
параметры:

- значение служебное/мета (одно на весь файл), а не «параметр по этапам»;
- бизнес положил его на лист «Control» рядом с «количеством этапов», а не в `Inputs`;
- шаблоны без этого поля должны продолжать работать (опциональность важнее обязательности).

Прямой путь — добавить колонку в основной KV-лист — не подходит: ломает существующие
шаблоны и мешает делать поле опциональным. `ControlValueRef` решает это, инжектируя
значение в Cells на этапе парсинга, так что весь маппер-код продолжает читать «как из
обычной колонки» через `FindColumn` + `ReadCellTrimmed`.

---

## ✅ Правильная реализация

### 1. Определение в `FileLayoutHint`

```csharp
// KiloImportService.Api/Domain/Importing/FileLayoutHint.cs
public sealed record KeyValueVertical(
    string SheetName,
    string KeyColumn,
    string ValueStartColumn,
    StageCountReference? StageCount = null,
    BudgetSectionHint? Budget = null,
    ChapterScheduleHint? ChapterSchedule = null,
    IReadOnlyList<SingleValueOverride>? SingleValues = null,
    IReadOnlyList<ControlValueRef>? ControlValues = null) : FileLayoutHint;  // 👈

public sealed record ControlValueRef(
    string SheetName,      // напр. "Control"
    string KeyColumn,      // напр. "F" — где лежит текст-ключ
    string ValueColumn,    // напр. "G" — где лежит значение
    string ParameterName,  // напр. "Номер КД" — что искать в KeyColumn (Trim, case-insensitive)
    string OutputKey);     // напр. "Номер договора" — под каким ключом класть в Cells
```

### 2. Объявление в маппере

```csharp
// KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs
public FileLayoutHint LayoutHint { get; } = new KeyValueVertical(
    SheetName: "Inputs",
    KeyColumn: "C",
    ValueStartColumn: "H",
    StageCount: new StageCountReference("Control", "F", "G", "Выбрать количество этапов"),
    // ...
    ControlValues: new[]
    {
        new ControlValueRef(
            SheetName:     "Control",
            KeyColumn:     "F",
            ValueColumn:   "G",
            ParameterName: "Номер КД",
            OutputKey:     "Номер договора"),
    });
```

### 3. Что делает парсер

После основного KV-цикла (но до возврата `ParseResult`) парсер для каждого
`ControlValueRef`:

1. Находит видимый лист с именем `SheetName` (case-insensitive). Скрытый/отсутствующий → skip.
2. Сканирует строки `RangeUsed()`, ищет ту, где `Cells[r, KeyColumn].GetString().Trim()`
   == `ParameterName` (case-insensitive). Не нашёл → skip.
3. Берёт значение `Cells[r, ValueColumn].GetString()` и записывает в
   `overrideValues.Add((OutputKey, value))`.
4. Основной цикл подставляет `overrideValues` в `Cells[OutputKey]` КАЖДОГО ParsedRow —
   значение одинаково для всех этапов.

Маппер видит `Cells["Номер договора"]` как обычную колонку:

```csharp
var fileDocNumberCol = FindColumn(allColumns, DocNumberAliases);
// FindColumn находит ключ "Номер договора" в allColumns без правок в Validate-коде.
```

### ⚠️ Важно

- **Опциональность встроенная.** Лист/строки нет → подстановка молча skip-ается.
  `FindColumn` вернёт `null`, маппер тихо пропустит блок чтения. Это позволяет
  старым шаблонам без поля продолжать работать.
- **Значение одно на весь файл.** Все ParsedRow получают идентичный
  `Cells[OutputKey]`. Если нужны разные значения на этапах — колонка должна быть
  в основном KV-листе.
- **Trim + case-insensitive** на `ParameterName` (как у `StageCountReference`)
  спасает от хвостовых пробелов / разнобоя регистра в файлах заказчика.
- **Несколько `ControlValueRef` за раз** допустимы — массив в `ControlValues`.

---

## ❌ Типичные ошибки

```csharp
// ❌ НЕПРАВИЛЬНО: дублировать ControlValueRef и SingleValueOverride на одном ключе
SingleValues:  [ new SingleValueOverride(KeyText: "X", ValueColumn: "E") ],
ControlValues: [ new ControlValueRef("Control", "F", "G", "X", "X") ],
// Парсер положит оба в overrideValues; победит последний по порядку обхода —
// поведение неочевидное. Использовать ровно ОДИН механизм для ключа.

// ❌ НЕПРАВИЛЬНО: класть значение под тем же ключом, который уже есть в KV-листе
new ControlValueRef("Control", "F", "G", "Номер КД", "Тип отделки")
// override перебьёт значение этапной колонки → пользователь увидит «Тип отделки=ДГ-7».
// OutputKey должен соответствовать алиасу, под которым его потом ищет маппер,
// и не совпадать с реальной KV-колонкой.

// ❌ НЕПРАВИЛЬНО: возвращать file-level ошибку, если строки нет
if (matchingRow == 0)
    errors.Add(new ParseError(null, $"Не найден '{ParameterName}'"));
// Это убивает опциональную семантику. Если поле обязательное — добавляйте
// валидацию в маппере (file-level error при `fileXCol is null`), а не в парсере.

// ❌ НЕПРАВИЛЬНО: использовать ControlValueRef для значений «по этапам»
ControlValues: [ new ControlValueRef("Control", "F", "G", "Этап 1 — что-то", "X") ]
// Парсер положит ОДНО значение во все ParsedRow. Этапная природа теряется.
// Для значений по этапам — обычная колонка в KV-листе.

// ❌ НЕПРАВИЛЬНО: читать XLWorkbook напрямую в маппере вместо хинта
// (сильная связанность маппера с ClosedXML, теряем юнит-тестируемость).
// Любая «нестандартная» ячейка должна идти через хинт парсера.
```

---

## 📊 Сравнение с соседними хинтами

| Хинт                       | Где лежит значение                  | Семантика отсутствия | Use case                  |
|----------------------------|-------------------------------------|----------------------|---------------------------|
| Колонка в `KeyValueVertical` | Тот же лист, КОЛОНКА этапа         | `value_empty`/`null` | Параметр по этапам        |
| `SingleValueOverride` ([doc 100](./100-finmodel-companygroup-link.md)) | Тот же лист, фиксированная КОЛОНКА в строке ключа | Молча skip          | «Группа компаний» в E14   |
| `ControlValueRef` (этот doc) | **Другой** лист, (key, value) пара | Молча skip           | «Номер КД» на Control     |
| `StageCountReference` ([doc 62](./62-vertical-keyvalue-layout.md)) | Другой лист, (key, int) пара | File-level error     | «Количество этапов»       |

---

## 📍 Применение в проекте

| Компонент                            | Файл                                                       | Идентификатор                |
|--------------------------------------|------------------------------------------------------------|------------------------------|
| Record `ControlValueRef`             | `KiloImportService.Api/Domain/Importing/FileLayoutHint.cs` | `ControlValueRef`            |
| Поле `KeyValueVertical.ControlValues`| то же                                                      | `KeyValueVertical.ControlValues` |
| Реализация в парсере                 | `KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs` | блок «ControlValueRefs» после `SingleValueOverrides` в `ParseKeyValueVertical` |
| Объявление в Финмодели               | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs` | поле `LayoutHint.ControlValues` |
| Тест-фиксация поведения              | `KiloImportService.Api.Tests/Importing/XlsxParserTests.cs` | `KeyValueVertical_ControlValueRef_InjectsValueIntoEveryStageRow`, `KeyValueVertical_ControlValueRef_Missing_SilentlySkipped` |

---

## 🎯 Чек-лист использования `ControlValueRef` в новом импорте

- [ ] Значение действительно одиночное (один на файл), не «по этапам».
- [ ] Шаблоны без поля должны работать → опциональность встроена; не плодить file-level ошибки в парсере.
- [ ] `OutputKey` уникален и не совпадает с реальной KV-колонкой основного листа.
- [ ] Алиасы в маппере (`*Aliases[]`) включают `OutputKey` — иначе `FindColumn` не найдёт.
- [ ] Парсер-тест на «подстановка работает на всех этапах» + «лист отсутствует → молча skip».
- [ ] При обязательности поля валидация делается в маппере (`fileXCol is null` → file-level error), не в парсере.
- [ ] Несколько `ControlValueRef` объединяются в один массив `ControlValues`, не размножаем поля раскладки.
