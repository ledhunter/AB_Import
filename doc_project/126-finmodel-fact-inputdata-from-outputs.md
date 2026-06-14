# 📈 Финмодель → Fact-блок на листе Outputs основного файла

## 📋 Описание

После того как Plan-каскад создал `fmmodel` + новую `fmmodelversion` и наполнил её плановыми `inputdata`-точками из второго файла «Общий график» (см. [doc 112](./112-finmodel-version-and-inputdata.md), [doc 110](./110-finmodel-plan-and-fmmodel.md)), маппер опционально дочитывает **фактические** значения с листа `Outputs` **основного** файла «Параметры к переносу в АБ.xlsx» и доливает их в **ту же только что созданную версию** под отдельными Fact-кодами справочника `fmcode`.

**Спрос заказчика (2026-06-07)**: переносить факт-значения по типам помещений (Площадь × Цена × Выручка) за один отчётный квартал в Финмодель, чтобы Visary показывал План/Факт-сводку в той же версии без отдельной импорт-формы.

**Парный с**: [doc 110](./110-finmodel-plan-and-fmmodel.md) (Plan + fmmodel), [doc 112](./112-finmodel-version-and-inputdata.md) (Plan + версия + InputData), [doc 105](./105-control-value-ref.md) (ControlValueRef).

---

## 🏗️ Архитектура: 4 шага на листе Outputs

```
1.  Поиск ячейки «Факт»  ─→  координаты (factRow, factCol)
                              │
2.  Под ячейкой:              │
    (factRow+1, factCol) = год (int)
    (factRow+2, factCol) = квартал (1..4)
                              ↓
                         FMPeriod = "{year}Q{quarter}"
                              ↓
3.  «Доходы поэтапно» → «Этап 1»  (anchor-by-label, без хардкода строк)
        ├── «Площадь реализации, кв.м.»       → InputData.Amount  (× 1)
        ├── «Цена реализации, тыс. руб./кв.м» → InputData.Cost    (× 1 000)
        └── «Выручка от реализации, млн руб.» → InputData.Summ    (× 1 000 000)
                              ↓
4.  Для каждой строки в подсекции:
        C-колонка = тип помещения  →  ResolveFactFmCode  →  fmcode справочник
        factCol-колонка = значение  →  парс/прочерк-skip
        Слияние трёх подсекций в одну точку (Code × FmPeriod)
```

Маркер «Факт» **опционален**: его отсутствие — нормальный кейс (старый шаблон без Fact-колонки). Парсер возвращает `null`, маппер тихо пропускает Fact-каскад, **без row-error**.

---

## ✅ Правильная реализация

### Резолв Year/Quarter под маркером «Факт»

```csharp
// FinModelImportMapper.cs — поиск маркера: текст ИЛИ custom number format.
int factRow = -1, factCol = -1;
for (int r = 1; r <= lastUsedRow && factRow < 0; r++)
{
    for (int c = 1; c <= lastUsedColumn; c++)
    {
        var cell = sheet.Cell(r, c);
        if (cell.IsEmpty()) continue;

        // (a) Сырое значение — для текстовых «Факт».
        var raw = cell.GetString().Trim();
        if (string.Equals(raw, "Факт", StringComparison.OrdinalIgnoreCase))
        { factRow = r; factCol = c; break; }

        // (b) Custom number format — для числовых ячеек, отображаемых как «Факт».
        // Реальный шаблон «Параметры к переносу в АБ.xlsx»: H12=0 с форматом
        // [=0]"Факт";[<>0]"Прогноз" → пользователь видит «Факт», а GetString()
        // возвращает «0». Без этой ветки маркер не находится. См. v1.2.
        if (cell.DataType == XLDataType.Number || cell.DataType == XLDataType.Boolean)
        {
            var formatted = cell.GetFormattedString().Trim();
            if (string.Equals(formatted, "Факт", StringComparison.OrdinalIgnoreCase))
            { factRow = r; factCol = c; break; }
        }
    }
}
if (factRow < 0) return null;  // 👈 нет маркера = старый шаблон, тихий skip

var year    = int.Parse(sheet.Cell(factRow + 1, factCol).GetString().Trim());
var quarter = ParseQuarter(sheet.Cell(factRow + 2, factCol).GetString().Trim());
var fmPeriod = $"{year}Q{quarter}";  // "2026Q1" для H12=0 → H13=2026, H14=1
```

### 🧹 Финальный фильтр: «все три = null|0 → skip» (v1.3)

Заказчик в шаблоне «Параметры к переносу в АБ.xlsx» заполняет неиспользуемые типы помещений **явными нулями** (`0`) во всех трёх подсекциях — не прочерками. Раньше парсер на этом создавал `InputData(0, 0, 0)` для 6 пустых категорий (Апартаменты/ПСН/Кладовые/ДОУ/СОШ/Поликлиника/ФОК), что захламляло версию Финмодели.

```csharp
// FinModelImportMapper.cs:ReadOutputsFactDataFromBytes — на выходе фильтруем.
var points = pointsByCode.Values
    .Where(b => HasAnyNonZero(b.Amount) || HasAnyNonZero(b.Cost) || HasAnyNonZero(b.Summ))
    .Select(b => new FinModelFactInputDataPoint(...))
    .ToList();

private static bool HasAnyNonZero(double? v)
    => v.HasValue && Math.Abs(v.Value) > 1e-9d;  // 👈 null (прочерк) и 0 трактуются одинаково
```

**Семантика**:
- ✅ Прочерк `-/—/–/−` в ВСЕХ трёх блоках → builder не существует → skip.
- ✅ Явные нули `0` в ВСЕХ трёх → builder есть с (0,0,0) → выкидывается фильтром.
- ✅ Любая комбинация прочерков и нулей с нулевым суммарным сигналом → skip.
- ✅ ХОТЯ БЫ ОДНО ненулевое поле (например, Машиноместа: Площадь=0, Цена=1489, Выручка=0) → создаём с нулями на «пустых» полях.

### 📌 Тип помещения — в любой колонке слева от факт-колонки (v1.3)

Раньше парсер читал label из C-колонки строго (`sheet.Cell(r, 3)`). Это ломалось на шаблонах с группировкой («Подгруппа» в C + «Тип» в D) или со сдвигом блока вправо.

```csharp
// FinModelImportMapper.cs:ReadFactSubsection — сканируем все колонки слева от factCol.
int labelColEnd = Math.Max(1, factCol - 1);
for (int c = 1; c <= labelColEnd; c++)
{
    var t = sheet.Cell(r, c).GetString().Trim();
    if (t.StartsWith("Итого", ...)) { stop = true; break; }  // 👈 Итого тоже в любой колонке
    var code = ResolveFactFmCode(t.ToLowerInvariant());
    if (code is null) continue;
    label = t; fmCode = code; break;  // первый матч — обычно C-колонка
}
```

**Инвариант**: тип помещения не может стоять ПРАВЕЕ Fact-колонки (там числовое значение). Поэтому скан `1..factCol-1` достаточен и оптимален.

### ⚙️ Two-source поиск маркера: text + formatted (v1.2)

| Источник | Когда срабатывает | Стоимость |
|---|---|---|
| `cell.GetString()` | Текстовая ячейка со значением «Факт» (фикстуры/upload-формы) | Дешёвый — прямое чтение |
| `cell.GetFormattedString()` | Числовая ячейка с custom number format `[=0]"Факт";...` (шаблон «Параметры») | Дороже — Excel применяет формат |

Парсер сначала пробует `(a)` и идёт дальше при mismatch; `(b)` считаем **только** на ячейках типа Number/Boolean (текст уже проверили в `(a)`, пустые отфильтровали `cell.IsEmpty()` сразу). На листе Outputs ~1700×143 = 240k ячеек это даёт +0.5 c к парсингу — приемлемо.

### Anchor-by-label для подсекций (без хардкода строк)

```csharp
// 3 подсекции в окне «Этап 1» .. «Этап 2» (или конца листа).
int amountAnchor = FindRowByLabel(sheet, stage1Row + 1, stageEndRow, lastUsedColumn,
                                  "Площадь реализации");
int costAnchor   = FindRowByLabel(sheet, amountAnchor + 1, stageEndRow, lastUsedColumn,
                                  "Цена реализации");
int summAnchor   = FindRowByLabel(sheet, costAnchor + 1, stageEndRow, lastUsedColumn,
                                  "Выручка от реализации");
```

`FindRowByLabel` сравнивает по двум стратегиям: strict-equal (case-insensitive) ИЛИ `StartsWith(label)` для якорей, оканчивающихся на ` реализации` (т.к. в файле они идут с суффиксом единиц измерения: «Площадь реализации, кв.м.», «Цена реализации, тыс. руб./кв.м»).

### Слияние трёх подсекций в одну Fact-точку

```csharp
// FinModelImportMapper.cs:ReadFactSubsection — три прохода по факт-колонке,
// один и тот же тип помещения накапливает Amount/Cost/Summ в один builder.
private sealed class FactPointBuilder
{
    public string FmCode { get; }
    public double? Amount { get; set; }
    public double? Cost   { get; set; }   // 👈 сохраняется уже × 1 000 (тыс → руб)
    public double? Summ   { get; set; }   // 👈 сохраняется уже × 1 000 000 (млн → руб)
}

switch (kind)
{
    case FactKind.Amount: builder.Amount = val;                  break;
    case FactKind.Cost:   builder.Cost   = val * 1_000d;         break;
    case FactKind.Summ:   builder.Summ   = val * 1_000_000d;     break;
}
```

### Универсальные dash-маркеры → значение отсутствует (НЕ row-error)

```csharp
// FinModelImportMapper.cs — совпадает с rooms-импортом (doc 125).
private static readonly HashSet<string> FactDashMarkers =
    new(StringComparer.Ordinal) { "-", "—", "–", "−" };

private static (bool HasValue, double Value) TryReadFactCellNumber(
    IXLWorksheet sheet, int row, int col)
{
    var cell = sheet.Cell(row, col);
    if (cell.IsEmpty()) return (false, 0d);
    if (cell.TryGetValue<double>(out var d)) return (true, d);

    var text = cell.GetString().Trim();
    if (FactDashMarkers.Contains(text)) return (false, 0d);  // 👈 тихий skip
    // ...
}
```

### Идемпотентность Fact-каскада

Plan-каскад (см. [doc 112](./112-finmodel-version-and-inputdata.md)) каждый импорт создаёт **новую версию** через `BuildNextVersionTitle`. Fact-точки доливаются именно в эту версию — внутри которой их заведомо нет. **Pre-check `GetInputDataByVersionAsync` не нужен**.

Если повторный импорт того же файла приведёт к появлению версии «Версия - Перенос из Эксель 2» — в ней Fact-точки будут созданы заново. Это **сознательное** поведение: каждая версия = снимок состояния модели на момент импорта.

### Возврат `versionId` из Plan-каскада

```csharp
// FinModelImportMapper.cs:EnsureFmModelVersionAndInputDataAsync
private async Task<int?> EnsureFmModelVersionAndInputDataAsync(...)  // 👈 был Task, стал Task<int?>
{
    // ... POST /crud/fmmodelversion ...
    return versionId;
}

// Вызов:
var versionId = await EnsureFmModelVersionAndInputDataAsync(fmModelId, planData, errors, ct);
if (versionId is { } vId && !string.IsNullOrWhiteSpace(primaryFilePath))
{
    await EnsureFmModelVersionFactInputDataAsync(vId, primaryFilePath!, errors, ct);
}
```

### Запись 0 в payload при `null`-полях точки

```csharp
// Контракт inputdata не принимает null-числа. Если в файле строка типа помещения
// присутствует в одной из подсекций (например, Amount), а в других нет (Cost/Summ
// остались null) — на payload-уровне нормализуем в 0.
new InputDataCreateRequest
{
    FMModelVersionID = versionId,
    FMModelVersion = new VisaryRef { ID = versionId },
    FMPeriod = factData.FmPeriod,
    Code = new VisaryRef { ID = codeRef.Id, Title = codeRef.Title },
    Summ   = point.Summ ?? 0d,
    Amount = point.Amount ?? 0d,
    Cost   = point.Cost ?? 0d,
    Percent = 0d,
}
```

### ⚠️ Важно
- **Маркер `Факт` опционален** — отсутствие = `null` от парсера, **никакой row-error**. Старые шаблоны без Fact-колонки продолжают работать.
- **Маркер может быть числом с custom format** (`[=0]"Факт";[<>0]"Прогноз"`) — `cell.GetString()` его НЕ увидит, нужен `cell.GetFormattedString()`. См. v1.2.
- **Стопаемся на `«Этап 2»`** при сборе окна «Этап 1» — заказчик подтвердил: на v1 только Этап 1, остальные пропускаются.
- **`Cost × 1 000`, `Summ × 1 000 000`** — единицы файла (тыс. руб., млн. руб.) приводятся к рублям **на этапе парсинга**. Маппер payload-уровня видит уже сырое значение.
- **Границы окна подсекции `[startRow, endRowExclusive)`** — для Summ передавать `stageEndRow + 1`, иначе **последняя строка с данными теряется** (`lastUsedRow` inclusive). На этом баге уже отловлен kg.Summ=null в фикстуре.
- **`Trim()+ToLowerInvariant()`** для всех label-проверок (тип помещения, маркеры подсекций) — заказчик копипастит из Word с произвольным регистром.

---

## ❌ Типичная ошибка

### 0. Игнорировать custom number format (баг v1.0 → v1.2)

```csharp
// НЕПРАВИЛЬНО — пропускает реальный шаблон «Параметры к переносу в АБ.xlsx»,
// где H12 хранит число 0 с форматом [=0]"Факт";[<>0]"Прогноз".
var text = sheet.Cell(r, c).GetString().Trim();
if (string.Equals(text, "Факт", ...))  // 👈 «0», не «Факт»
```

**Симптом**: парсер возвращает `null` → пользователь видит «Для файла не были найдены фактические значения», хотя в Excel «Факт» отчётливо отображается. Не воспроизводится на тестовых фикстурах (там «Факт» — это текстовая ячейка).

**Правильно**: проверять и `GetString()`, и `GetFormattedString()` (см. v1.2 в Architecture-разделе).

### 1. Жёсткая привязка к номерам строк/колонок

```csharp
// НЕПРАВИЛЬНО — таблица динамическая, заказчик добавляет/удаляет строки.
var year = sheet.Cell(122, 6).GetString();  // 👈 хардкод
var apartmentAmount = sheet.Cell(167, 6).GetValue<double>();
```

**Почему плохо**: на разных версиях шаблона «Доходы поэтапно» уезжает на 5–20 строк (заказчик добавляет «Сводные данные», справочные таблицы). Хардкод сразу даёт `null` или подцепляет чужую ячейку.

### 2. Не различать прочерк и нечисловой мусор

```csharp
// НЕПРАВИЛЬНО — прочерк = пользовательский маркер «значение отсутствует»,
// а не ошибка ввода. row-error на «-» сломает каждую вторую строку файла.
if (!double.TryParse(text, out var v))
    rowErrors.Add(new RowError("cell", "invalid_number", $"'{text}' не число"));
```

**Правильно**: dash-маркеры `-/—/–/−` → `(false, 0)` без ошибки, как в rooms-импорте (см. [doc 125](./125-rooms-sa-soft-validation-and-journal-wording.md)).

### 3. Возвращать row-error при отсутствии маркера «Факт»

```csharp
// НЕПРАВИЛЬНО — старый шаблон без Fact-колонки → импорт упадёт.
if (factRow < 0)
    errors.Add(new RowError(null, "fact_marker_missing",
        "На листе Outputs не найдена ячейка «Факт»."));  // 👈 ломает обратную совместимость
```

**Правильно**: `return null` из парсера, маппер видит null → `LogDebug` без row-error. Fact-блок не обязателен.

### 4. Plan и Fact в РАЗНЫЕ версии

```csharp
// НЕПРАВИЛЬНО — заказчик хочет ОДНУ версию с Plan- и Fact-точками рядом.
var planVersionId = await CreateFmModelVersionAsync(...);
var factVersionId = await CreateFmModelVersionAsync(...);  // 👈 «Версия - Перенос из Эксель 2»
```

**Правильно**: Fact доливается в `planVersionId`. Соответствующие Fact-коды справочника `fmcode` (011/021/031/041/211/221/231/051) держат разные `inputdata`-строки одного `FMModelVersionID`, и в UI Visary показывает План/Факт в одной версии.

### 5. Парные коды по позиции, не по `Code`-полю

```csharp
// НЕПРАВИЛЬНО — ничто не гарантирует, что справочник вернёт «011» перед «010».
var found = fmCodes[(int)(factPosition % 9)];  // 👈 случайные сопоставления
```

**Правильно**: точечный запрос `FindFmCodeByCodeAsync("011", ct)` по полю `Code` (см. [doc 112 v1.6](./112-finmodel-version-and-inputdata.md)). Каждая категория = 1 запрос.

---

## 📍 Применение в проекте

| Слой | Файл | Что добавлено |
|------|------|---------------|
| **ImportContext** | [IImportMapper.cs](../KiloImportService.Api/Domain/Mapping/IImportMapper.cs) | `PrimaryFileRelativePath` (опц.) — путь основного файла в `IFileStorage` |
| **Pipeline** | [ImportPipeline.cs](../KiloImportService.Api/Domain/Pipeline/ImportPipeline.cs) | Прокидывание `session.FileSnapshot?.RelativePath` в обоих местах (Validate/Apply) |
| **Парсер** | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `ReadOutputsFactData` + `ReadOutputsFactDataFromBytes` + `ReadFactSubsection` + `FindRowByLabel` + `TryReadFactCellNumber` |
| **Структуры** | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `FinModelFactData`, `FinModelFactInputDataPoint`, `FactPointBuilder`, `FactKind`, `FinModelFactParseException` |
| **Резолв кода** | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `ResolveFactFmCode` (тип помещения → Code) + расширенный `ResolveFallbackTitle` |
| **Константы** | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `FmCode*Fact` (8 шт.) + `FallbackTitle*Fact` (8 шт.) |
| **Маппер** | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `EnsureFmModelVersionFactInputDataAsync` + изменённая сигнатура `EnsureFmModelVersionAndInputDataAsync` (`Task<int?>` вместо `Task`) |
| **Интеграция** | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | Вызов Fact-каскада в `EnsureFmModelAsync` после Plan + проброс `primaryFilePath` в `ApplyAsync` |
| **Тесты** | [FinModelFactInputDataTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelFactInputDataTests.cs) | 7 тестов: maker-missing, reference-layout, malformed-year, primary-with-fact, primary-without-fact, primary-null, dash-in-amount |

### Таблица Fact-кодов

| Тип помещения (C-колонка) | Plan-код | Fact-код | Title из справочника |
|---|---|---|---|
| Квартиры | `010` | `011` | «011 Продажа квартиры (факт)» |
| Апартаменты | `060` | `061` | «061 Продажа апартаменты (факт)» |
| ПСН / нежилое | `020` | `021` | «021 Продажа нежилые (ком) ПСН (факт)» |
| Кладовые | `030` | `031` | «031 Продажа иные нежилые (кладовки) (факт)» |
| Машиноместа | `040` | `041` | «041 Продажа м/м (факт)» |
| ДОУ | — | `221` | «221 ДОУ (факт)» |
| СОШ | — | `211` | «211 СОШ (факт)» |
| Поликлиника | — | `231` | «231 Поликлиника (факт)» |
| ФОК | — | `051` | «051 ФОК (факт)» |

⚠️ **«Апартаменты»** добавлены 2026-06-08 (заказчик подтвердил Plan=`060`, Fact=`061`). Резолв в `ResolveFmCode` (Plan) и `ResolveFactFmCode` (Fact) идёт по префиксу `"апарт"` — стоит **после** `"кварт"`, чтобы «Квартиры» не перехватывали более общий маркер. Префиксы не пересекаются.

⚠️ Title-ы резолвятся через `FindFmCodeByCodeAsync` по `Code`, а не по Title (см. [doc 112 v1.6](./112-finmodel-version-and-inputdata.md)). Fallback-ы выше — на случай, когда Visary вернул пустой `Title` в DTO.

### Row-errors (для отчёта)

| ErrorCode | Когда | Уровень |
|---|---|---|
| `fact_block_parse_error` | Маркер «Факт» найден, но год/квартал не парсятся | file-level |
| `inputdata_fact_codes_unavailable` | Сетевая ошибка `listview/fmcode` для одного из Fact-кодов | file-level |
| `inputdata_fact_code_not_found` | Один или несколько Fact-кодов отсутствуют в справочнике | file-level (после цикла) |
| `inputdata_fact_create_failed` | Часть Fact-точек не создалась/не привязалась | file-level (после цикла) |
| **нет** | Маркер «Факт» отсутствует, или primaryFilePath=null, или Outputs нет на листе | `LogDebug` |

---

## 🎯 Чек-лист при правках Fact-каскада

- [ ] Маркер «Факт» опционален — никаких row-errors при отсутствии
- [ ] Anchor-by-label, не хардкод строк (тест: добавить в фикстуру 10 пустых строк перед «Доходы поэтапно»)
- [ ] Cost умножать на 1 000, Summ — на 1 000 000 **в парсере**, не в payload
- [ ] Dash-маркеры `-/—/–/−` → значение отсутствует (НЕ row-error)
- [ ] Fact-точка пишется в **ту же** версию, что Plan
- [ ] Граница окна подсекции для Summ = `stageEndRow + 1` (inclusive)
- [ ] Новые типы помещений → добавлять и в `ResolveFactFmCode`, и в `FmCode*Fact`-константу, и в `FallbackTitle*Fact`
- [ ] Сеть-ошибки fmcode → одна row-error + skip всего Fact-каскада (Plan уже сохранён)
- [ ] Тесты: marker-missing, reference-layout, dash-skip, primary-path-null, primary-without-fact, **custom-number-format**, real-parameters-file (regression)
- [ ] При расширении количества типов в `BuildOutputsWithFactColumnXlsx` — обновить ассерт в `ReadOutputsFactData_ReferenceLayout_ResolvesPeriodAndAllRoomTypes`
