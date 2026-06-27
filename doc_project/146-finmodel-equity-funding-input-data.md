# 💰 Финмодель → «Вложение собственных средств» (fmcode=604) поквартально

## 📋 Описание

Расширение каскада импорта Финмодели ([doc 112](./112-finmodel-version-and-inputdata.md)):
после Plan-точек (fmcode 010/020/030/040/060) маппер дополнительно создаёт
inputdata-записи с **fmcode «604» — «Вложение собственных средств»** по каждому
непустому кварталу основного файла «Параметры к переносу в АБ.xlsx».

| Что | Откуда берётся | Что попадает в Visary |
|-----|----------------|-----------------------|
| Раздел | Лист `Inputs`, колонка `C`, строка с текстом `«Финансирование: Этап 1»` | — (только маркер начала поиска) |
| Единица измерения | Колонка `D` строки `«Вложение собственных средств»` (поле справа от названия параметра) | Множитель (`«тыс. руб.» → ×1000`, `«млн руб.» → ×10⁶`, `«млрд руб.» → ×10⁹`, иначе `×1`) |
| Квартальные значения | Колонки `H..CU` той же строки (та же шапка дат, что и у ГФ Главы 1: `Inputs` row 7) | `inputdata.Summ = value × multiplier` |
| Код параметра | Справочник Visary `fmcode`, поле `Code` = `«604»` | `inputdata.Code = { ID, Title }` (Title из ответа Visary) |

Каскад вызывается из `EnsureFmModelVersionAndInputDataAsync` ПОСЛЕ Plan-точек:
- версия Финмодели уже создана (см. doc 112) — добавляем туда новые inputdata-записи;
- по одной записи на квартал с непустым (не нулевым) значением;
- заполняем **только** поле `Summ`; `Amount`/`Cost`/`Percent` = `0` (требование заказчика —
  Visary не разрешает null в числовых полях inputdata).

⚠️ Нулевые значения (`0` в файле) **пропускаются** — заказчик не хочет лишних
inputdata на пустых кварталах. Это отличается от Plan-блока «Общий график» (doc 112 v1.5),
где явный 0 — валидная точка. Логика: для «Вложение собственных средств» 0 = «не
вкладываем», что эквивалентно отсутствию записи.

---

## ✅ Правильная реализация

### Парсер: новая секция `EquityFundingHint`

[FileLayoutHint.cs](../KiloImportService.Api/Domain/Importing/FileLayoutHint.cs):

```csharp
public sealed record EquityFundingHint(
    string MarkerColumn,         // "C"
    string StartMarker,          // "Финансирование: Этап 1"
    string KeyName,              // "Вложение собственных средств"
    string UnitColumn,           // "D"
    int QuarterHeaderRow,        // 7
    string FirstQuarterColumn,   // "H"
    string LastQuarterColumn,    // "CU"
    int ScanLimitRows = 200,
    string SheetMarker = "(equity-funding)");
```

[XlsxParser.ExtractEquityFunding](../KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs):
1. Ищем строку StartMarker в `MarkerColumn` — **точное равенство** (Trim, case-insensitive).
   Substring-поиск, как у `ChapterScheduleHint`, здесь опасен: «Финансирование» (C809)
   тоже встречается выше — а нам нужен именно «Финансирование: Этап 1» (C811).
2. От `startRow+1` до `min(startRow + ScanLimitRows, lastRow)` сканируем строки.
3. Data-row — первая, где **одновременно**:
   - `cell[MarkerColumn].Trim()` точно равен `KeyName` (case-insensitive),
   - `cell[UnitColumn]` непустой.

   В реальном файле «Вложение собственных средств» встречается дважды (C839 — подзаголовок
   без единицы, C841 — собственно строка данных с `D="тыс. руб."`). Без проверки
   `UnitColumn` парсер взял бы подзаголовок и не нашёл бы значений.
4. Эмитим **две** `ParsedRow` с `Sheet="Inputs (equity-funding)"`:
   - **Header-row** (`SourceRowNumber = QuarterHeaderRow`):
     `Cells[MarkerColumn] = "__equity_quarters__"` (sentinel), а в колонках H..CU —
     ISO-даты начала кварталов (`yyyy-MM-dd`).
   - **Data-row** (`SourceRowNumber = найденная строка`):
     `Cells[MarkerColumn] = KeyName`, `Cells[UnitColumn]` = единица,
     `Cells[H]..Cells[LastQuarterColumn]` = текстовые значения квартальных ячеек.

### Маппер: `ValidateEquityFunding` + `EnsureEquityFundingInputDataAsync`

[FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs):

```csharp
internal const string FmCodeEquityInvestment   = "604";
private const string FallbackTitleEquityInvestment = "Вложение собственных средств";
private const string EquityFundingSheetSuffix     = "(equity-funding)";

internal static double ResolveUnitMultiplier(string? unitText)
{
    if (string.IsNullOrWhiteSpace(unitText)) return 1d;
    var u = unitText.ToLowerInvariant();
    // ⚠️ Порядок важен: «млрд» проверяем ДО «млн», иначе «млрд руб.» матчился бы на «м».
    if (u.Contains("млрд")) return 1_000_000_000d;
    if (u.Contains("млн"))  return 1_000_000d;
    if (u.Contains("тыс"))  return 1_000d;
    return 1d;
}
```

`ValidateEquityFunding` собирает 2 mapped-row:

```jsonc
// equity_funding_quarters
{ "Kind": "equity_funding_quarters",
  "Quarters": [{ "Col": "H", "Date": "2026-01-01" }, …] }

// equity_funding_data
{ "Kind": "equity_funding_data",
  "Unit": "тыс. руб.",
  "ScaleMultiplier": 1000.0,
  "Points": [{ "Col": "H", "ValueRaw": 238000, "Value": 238000000 }, …] }
```

`Points` уже содержит **домноженные** значения (Value = ExcelNumber × ScaleMultiplier).
Каскад их использует напрямую, без повторного умножения.

`EnsureEquityFundingInputDataAsync` (вызывается из `EnsureFmModelVersionAndInputDataAsync`
ПОСЛЕ Plan-точек, см. блок «5)»):

1. Найти `equity_funding_quarters` + `equity_funding_data` mapped-rows. Нет хотя бы
   одной — info-лог + return (каскад опционален).
2. Резолв fmcode «604» через `IListViewClient.FindFmCodeByCodeAsync("604", ct)`:
   - Сетевая ошибка → `equity_funding_codes_unavailable` + skip.
   - Пустой ответ → `equity_funding_code_not_found` + skip.
3. Цикл по точкам: для каждой —
   `fmPeriod = $"{date.Year}Q{(date.Month-1)/3 + 1}"`, POST `/crud/inputdata` с
   `Summ = point.Value`, `Amount=Cost=Percent=0`, + Link к версии.
4. На любую ошибку POST/Link — `failedCount++`, в конце общий
   `equity_funding_create_failed`.

### Synthetic-лист для отчёта

`SyntheticSheetEquityFundingInputData = "Вложение собственных средств"` — отдельный
synthetic-лист (см. doc 128), чтобы пользователь видел в отчёте Apply, какие
inputdata-точки 604 были созданы / упали. Не путать с `«План — Общий график»`
и `«Outputs — Факт»`.

---

## ⚠️ Важно

1. **Только `Summ`.** `Amount`/`Cost`/`Percent` — `0` (требование заказчика). Любая
   попытка проставить осмысленные ноль-эквиваленты (например, `Amount = ValueRaw`)
   — поломает контракт.

2. **Единица справа от названия параметра, не из шапки таблицы.** В файле «Параметры»
   единица лежит в `D841` (один шаг вправо от C-Title), а не в шапке столбца.
   Поэтому `EquityFundingHint.UnitColumn = "D"` — это отдельная колонка строки данных,
   не часть header-row.

3. **Множитель определяется фразой, не списком известных строк.** Через
   `contains("млрд")` / `contains("млн")` / `contains("тыс")` — устойчиво к разным
   написаниям («тыс. руб.», «тыс.руб», «ТЫС. РУБ.»). Неизвестная единица → `×1`
   (без ошибки) — заказчик может ввести что-то незнакомое, лучше «положить как есть»,
   чем заблокировать импорт.

4. **`MLрд` ДО `мЛн`.** В `ResolveUnitMultiplier` проверки идут в порядке убывания
   ключа: иначе «млрд руб.» содержит «м» и подмена была бы перепутана.

5. **Нулевые значения пропускаются.** В отличие от Plan-блока «Общий график»
   (doc 112 v1.5), где явный 0 — валидная точка плана продаж, для «Вложение
   собственных средств» 0 = «нет вложения», и заказчик не хочет лишней
   inputdata-записи. Реализация: `if (Math.Abs(value) < 1e-9) continue;` в
   `ValidateEquityFunding` ПЕРЕД добавлением в `Points`.

6. **Каскад идёт ПОСЛЕ Plan, в той же версии.** Если Plan-блок упал (например,
   `inputdata_codes_unavailable`), версия всё равно создана, и Equity-каскад
   попытается долить свои точки. Ошибки независимы — Plan/Equity не валят друг
   друга. Сами 604-точки идут в ту же `versionId`, что и 010/020/…

7. **Точное равенство `StartMarker`/`KeyName`, без substring.** Файл содержит
   близкие строки («Финансирование», «Вложение собственных средств на уплату процентов»),
   которые подменили бы целевую при поиске через `Contains`. Используем
   `string.Equals(text, marker, OrdinalIgnoreCase)` с `Trim`.

8. **`KeyName` встречается дважды — берём ту, у которой есть единица.** В файле
   `C839` — подзаголовок параметра без `D`-значения; `C841` — собственно строка
   данных с `D="тыс. руб."`. Условие отбора: непустая ячейка в `UnitColumn`.

9. **Шапка кварталов — общая с ГФ.** `QuarterHeaderRow=7`, `FirstQuarterColumn=H`,
   `LastQuarterColumn=CU` совпадают с `ChapterScheduleHint`. Но **парсер копирует
   даты ещё раз** в свой header-row (через `EquityFundingQuartersSentinel`), чтобы
   маппер мог обрабатывать equity-блок независимо от того, есть ли в файле блок ГФ
   Главы 1. Сама шапка эмитится через `cell.TryGetValue<DateTime>` → ISO-строка.

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО — substring-поиск StartMarker.
if (text.Contains("Финансирование", StringComparison.OrdinalIgnoreCase)) ...
// 💥 Раздел «Финансирование» (C809) тоже содержит «Финансирование» — найдём не ту строку.
// Правильно — точное равенство с "Финансирование: Этап 1".
```

```csharp
// НЕПРАВИЛЬНО — взять первую строку с KeyName в MarkerColumn.
if (cell[markerColumn] == "Вложение собственных средств") { dataRow = r; break; }
// 💥 В файле есть C839 (подзаголовок без единицы и значений) и C841 (данные).
//    Возьмёшь C839 — единицу не найдёшь, кварталы пустые.
// Правильно — дополнительное условие cell[unitColumn] не пустой.
```

```csharp
// НЕПРАВИЛЬНО — проверять "млн" ДО "млрд".
if (u.Contains("млн")) return 1_000_000d;
if (u.Contains("млрд")) return 1_000_000_000d;
// 💥 "млрд руб." содержит "млн" — вернули бы 1e6 вместо 1e9.
// Правильно — обратный порядок.
```

```csharp
// НЕПРАВИЛЬНО — отправлять явный 0 в Visary.
foreach (var point in points)
{
    await CreateInputDataAsync(new InputDataCreateRequest { Summ = point.Value, … });
}
// 💥 На листе у заказчика реально только 1-2 непустых квартала из 92 — остальные 0.
//    Без фильтра ноль-значений мы создадим 90+ пустых inputdata-точек.
// Правильно — фильтровать в ValidateEquityFunding ПЕРЕД попаданием в Points.
```

```csharp
// НЕПРАВИЛЬНО — забыть, что Plan и Equity лежат в одной версии.
var equityVersion = await _visaryClient.CreateFmModelVersionAsync(...);  // 💥 вторая версия
// Правильно — equity-каскад использует ту же versionId, что вернул Plan-каскад.
```

```csharp
// НЕПРАВИЛЬНО — заполнять Amount/Cost числом часов или площадью.
new InputDataCreateRequest { Summ = value, Amount = 1, Cost = 1 }
// 💥 Заказчик увидит «Вложение собственных средств: Площадь=1 кв.м» в UI — мусор.
// Правильно — Amount=Cost=Percent=0 (контракт inputdata не допускает null).
```

---

## 📍 Применение в проекте

| Слой | Файл | Метод/блок |
|------|------|------------|
| Hint | `Domain/Importing/FileLayoutHint.cs` | `EquityFundingHint`, `KeyValueVertical.EquityFunding` |
| Парсер | `Domain/Importing/Parsers/XlsxParser.cs` | `ExtractEquityFunding`, `EquityFundingQuartersSentinel` |
| LayoutHint | `Domain/Mapping/FinModelImportMapper.cs` | `LayoutHint` (новый `EquityFunding:` блок) |
| Константы | `Domain/Mapping/FinModelImportMapper.cs` | `FmCodeEquityInvestment="604"`, `FallbackTitleEquityInvestment`, `EquityFundingSheetSuffix`, `SyntheticSheetEquityFundingInputData` |
| Validate | `Domain/Mapping/FinModelImportMapper.cs` | `ValidateEquityFunding`, `ResolveUnitMultiplier`, `IsEquityFundingRow` |
| Apply | `Domain/Mapping/FinModelImportMapper.cs` | `EnsureEquityFundingInputDataAsync` (вызывается из `EnsureFmModelVersionAndInputDataAsync` после Plan-цикла) |
| Тесты | `KiloImportService.Api.Tests/Mapping/FinModelEquityFundingTests.cs` | парсер + Validate + Apply (12 happy/sad-сценариев + 13 `ResolveUnitMultiplier` Theory-кейсов) |

---

## 🎯 Чек-лист

- [ ] `Inputs!C811` содержит `«Финансирование: Этап 1»`, ниже на 2-3 строки —
      `«Вложение собственных средств»` с единицей в `D` → парсер находит обе строки и
      эмитит 2 ParsedRow `Inputs (equity-funding)`.
- [ ] Шапка кварталов берётся из `Inputs!H7..CU7` (тот же ряд, что у ГФ).
- [ ] Единица `«тыс. руб.»` → множитель 1000; `«руб.»` → 1; `«млн руб.»` → 1e6;
      `«млрд руб.»` → 1e9; неизвестная → 1.
- [ ] На каждый квартал с **ненулевым** значением создаётся inputdata `Code.ID=604_*`,
      `Summ = value × multiplier`, `Amount=Cost=Percent=0`.
- [ ] inputdata линкуется к той же `versionId`, что и Plan-точки (одна версия).
- [ ] Нулевые квартальные значения **пропускаются** (отличие от Plan, см. doc 112 v1.5).
- [ ] При отсутствии раздела «Финансирование: Этап 1» / строки «Вложение собственных
      средств» в файле — equity-каскад тихо пропущен (info-лог), errors не добавляются.
- [ ] При ошибке резолва fmcode 604 (`listview/fmcode` 5xx/timeout) → row-error
      `equity_funding_codes_unavailable`, Plan-точки не страдают.
- [ ] При отсутствии записи 604 в справочнике (`Total=0`) → row-error
      `equity_funding_code_not_found`.
- [ ] При POST/Link ошибке на отдельной точке → `equity_funding_create_failed` с
      числом упавших.

---

## 📅 История изменений

- **v1.0 (2026-06-23)** — первая версия. fmcode «604», 5 кодов ошибок
  (`equity_funding_codes_unavailable`/`_code_not_found`/`_create_failed`),
  множитель из текста единицы (тыс./млн./млрд.), нулевые значения пропускаются.

## 🔗 Связанная документация

- [doc 112 — finmodel-version-and-inputdata](./112-finmodel-version-and-inputdata.md) —
  Plan-точки fmcode 010/020/030/040/060; каскад «Вложение собственных средств»
  стартует после Plan-цикла в той же версии.
- [doc 91 — finmodel-chapter1-schedule](./91-finmodel-chapter1-schedule.md) — ГФ Главы 1,
  откуда мы переиспользуем шапку кварталов (`Inputs!H7..CU7`).
- [doc 128 — synthetic-stagedrows-and-file-grouping](./128-synthetic-stagedrows-and-file-grouping.md)
  — synthetic-листы для отчёта; новый лист «Вложение собственных средств».
- [doc 105 — control-value-ref](./105-control-value-ref.md) — паттерн «hint парсеру для
  out-of-band ячеек на том же листе», к которому относится `EquityFundingHint`.
