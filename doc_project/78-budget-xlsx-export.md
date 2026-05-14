# 📤 Экспорт бюджета в XLSX по эталону «Бюджет_А4.1»

## 📋 Описание

**Статус**: 🟢 v1.3 — отдача готового XLSX для ручного импорта в Visary.
**Дата**: 2026-05-14
**Заменяет подходы**: [70-wbs-api-foundation.md](70-wbs-api-foundation.md), [71-finmodel-budget-import.md](71-finmodel-budget-import.md)
(CRUD-путь WBS отключён, см. ниже).

После Apply сессии «Финмодель» backend больше **не пишет бюджет в Visary CRUD-методами**. Вместо этого собирает XLSX-файл по эталонному шаблону `Context/Бюджет_А4.1.xlsx`, который пользователь скачивает кнопкой «Скачать бюджет для Visary» и импортирует в Visary нативным механизмом.

### 📜 Changelog

| Версия | Дата | Что | Зачем |
|---|---|---|---|
| **v1.0** | 2026-05-13 | Embedded шаблон + подмена C/D + агрегация снизу-вверх. `BuildKeepSet` (trim-zeros). | Первая версия. |
| **v1.1** | 2026-05-14 | Убран `BuildKeepSet` — выгружается ПОЛНОЕ дерево шаблона (отсутствующим = 0). Удаление строк → no-op, NamedRanges cleanup не нужен. | В выгрузке пропадали ожидаемые пользователем статьи с нулём. |
| **v1.2** | 2026-05-14 | Главы 2 и 3 СВЁРНУТЫ до строки самой главы — все подстатьи (`2.1.`, …, `3.8.`) удаляются. `CollapsedChapterCodes = ["2.", "3."]`. Вернулась логика удаления строк → вернулся NamedRanges cleanup. | Бизнес: для Visary в этих главах нужны только сводные суммы. |
| **v1.3** | 2026-05-14 | Распознавание главы по «Глава N» префиксу (`FindChapterByPrefix` через регэкс). Chapter-direct override: ИТОГО главы из файла > агрегата children. Закрытие главы после первого «Итого…» (`chapterClosed`-флаг). Двусторонний fuzzy-match Title. | Главы 2/3 имели Title в файле ≠ справочнику → не распознавались. Их статьи в файле тоже не совпадают со справочником → агрегат = 0 без override. |

### 🔄 Ожидаемый результат (Глава 1)

Пример: в финмодели «Параметры к переносу в АБ.xlsx» в Глава 1 есть только 1.1, 1.6, 1.8.

| № п/п | Наименование | Источник в файле | Сумма (тыс. руб.) |
|---|---|---|---|
| 1. | Глава 1. Стоимость земельного участка… | строка «Итого…» главы (chapter-direct override) ИЛИ агрегат 1.1..1.8 | 441 333 |
| 1.1. | Затраты на приобретение прав на ЗУ | основная строка | 438 000 |
| 1.2. — 1.5. | (договоры, аренда) | отсутствует | 0 |
| 1.6. | Затраты на изменение ВРИ | основная строка | 1 111 |
| 1.7. | Возмещение убытков… | отсутствует | 0 |
| 1.8. | Прочие затраты на улучшения и содержание ЗУ | основная строка («Прочие затраты» — reverse-prefix) | 2 222 |

### 🔄 Ожидаемый результат (Главы 2 и 3)

| № п/п | Наименование | Источник в файле | Сумма (тыс. руб.) |
|---|---|---|---|
| 2. | Глава 2. Стоимость строительства | строка «Итого…» главы | 1 610 006 |
| 3. | Глава 3. Коммерческие расходы | строка «Итого…» главы | … |

Подстатьи `2.x`, `3.x` в выгрузке **отсутствуют** (свёрнуты).

После Apply сессии «Финмодель» backend больше **не пишет бюджет в Visary CRUD-методами**.
Вместо этого собирает XLSX-файл по эталонному шаблону `Context/Бюджет_А4.1.xlsx`,
которое пользователь скачивает кнопкой «Скачать бюджет для Visary» и импортирует в
Visary нативным механизмом.

### 🎯 Почему так

| Что | Что было | Почему отказались |
|---|---|---|
| Создание глав через `POST /crud/wbs` | Работало | OK |
| Чтение существующих WBS проекта | `POST /listview/wbs/onetomany/ConstructionProject?associationId={pid}` | Visary возвращает 500: `Instance property 'ConstructionProject' is not defined for type 'Domain.Model.Entities.WBS'` |
| Иерархия дерева | Двухуровневая (Глава → Подстатья, привязка к Site через `ConstructionSiteID`) | По факту **четырёхуровневая**: `ProjectRoot → SiteRoot → Глава → Подстатья`, причём `SiteRoot` нужно создавать отдельно для каждого ОКСа |

Поднимать всё это CRUD-ом — много шагов, каждый из которых может упасть, и нет
надёжного listview, чтобы проверить идемпотентность. Visary при ручном импорте
делает всё сама за один шаг — это естественнее.

---

## ✅ Правильная реализация

### Структура эталонного файла `Context/Бюджет_А4.1.xlsx`

| Колонка | Header | Заполнение |
|---|---|---|
| `A` | `№ п/п` | КБК с хвостовой точкой: `"1."`, `"2.2.1.10."` (так же, как Visary в Code) |
| `B` | `Наименование работ и затрат…` | Title статьи |
| `C` | `Сумма заявленных капвложений, в т.ч. НДС, руб.` | **DeclaredSum** — подмена |
| `D` | `Сумма одобренных капвложений, в т.ч. НДС, руб.` | **ConfirmedSum** — подмена |
| `E` | `Расход в год` | пусто |
| `F` | — | пусто |
| `G` | `Примечания по статьям` | help-текст (копируется из эталона как есть) |

- **99 data-строк** (R2..R100), 1 header (R1). Лист один, имя — `Бюджет`.
- Полный набор глав/подстатей зафиксирован в эталоне: 3 главы, все подстатьи присутствуют **даже с нулевыми суммами**. Visary, очевидно, ждёт ровно эту форму.
- Code строится сервером (`"1.1."`, `"2.2.1.10."`); иерархия восстанавливается им же из Code (`ParentCode = "2.2.1." → parent`).

### Поток

```
┌─────────────────────┐    POST /api/imports/{id}/apply
│ User → Apply сессии │ ──────────────────────────────►  Backend:
└─────────────────────┘                                  • ApplyParametersAsync (отделка, класс, адрес, индикаторы)
                                                         • budget rows → applied++ (НЕ дёргаем Visary CRUD)
                                                         • session.Status = Applied
                                                          
┌─────────────────────┐    GET /api/imports/{id}/budget-xlsx
│ User → «Скачать»    │ ──────────────────────────────►  BudgetXlsxExporter:
└─────────────────────┘                                  • читает staged_rows (Kind="budget")
                                                         • открывает embedded шаблон
                                                         • агрегирует Глава/Раздел снизу вверх
                                                         • подменяет C/D, остальное — как есть
                                                         • возвращает application/vnd.openxml...sheet
                                                          
┌─────────────────────┐
│ User → Visary UI    │  →  ручной импорт «Бюджет_<sessionId>.xlsx»
└─────────────────────┘
```

### Ключевые места кода (BudgetXlsxExporter v1.3)

```csharp
// KiloImportService.Api/Budget/BudgetXlsxExporter.cs
private const string TemplateResourceName =
    "KiloImportService.Api.Resources.budget-template-a41.xlsx";  // 👈 embedded
private const string SheetName = "Бюджет";
private const int ColCode         = 1; // A
private const int ColDeclaredSum  = 3; // C
private const int ColConfirmedSum = 4; // D
private const double FinmodelToVisaryFactor = 1000d; // тыс.руб → руб

// v1.2: Главы 2 и 3 свёрнуты — выводим только саму главу, без подстатей.
private static readonly string[] CollapsedChapterCodes = ["2.", "3."];

// v1.3: ExtractBudgetSums разделяет на terminal + chapterDirect.
// chapterDirect содержит «Итого» главы из файла — это override для агрегата.
var (terminalSums, chapterDirectSums) = ExtractBudgetSums(rows);
var aggregated = AggregateUpwards(terminalSums, chapterDirectSums);
//                                                ^^^^^^^^^^^^^^^^^^
//      После агрегации снизу-вверх по children — переписываем суммы глав
//      значениями chapterDirect (см. AggregateUpwards в коде).

// NamedRanges cleanup нужен, потому что v1.2 снова удаляет строки
// (подстатьи Глав 2/3); ClosedXML на пустых refs ловит ParsingException.
foreach (var nr in workbook.NamedRanges.ToList()) nr.Delete();
foreach (var nr in sheet.NamedRanges.ToList()) nr.Delete();

// Прогон ВСЕХ строк template:
//   • для строк сворачиваемых глав 2/3 (кроме самих 2., 3.) — собираем на удаление;
//   • для остальных — проставляем сумму × 1000 (отсутствующим = 0).
var rowsToDelete = new List<int>();
for (int rownum = 2; rownum <= lastRow; rownum++)
{
    var code = NormalizeCode(sheet.Cell(rownum, ColCode).GetString());
    if (string.IsNullOrEmpty(code)) continue;

    if (IsCollapsedChapterDescendant(code))
    {
        rowsToDelete.Add(rownum);
        continue;
    }

    var (decl, conf) = aggregated.TryGetValue(code, out var v) ? v : (0d, 0d);
    sheet.Cell(rownum, ColDeclaredSum).Value = decl * FinmodelToVisaryFactor;
    sheet.Cell(rownum, ColConfirmedSum).Value = conf * FinmodelToVisaryFactor;
}

// С конца, чтобы номера выше не сдвигались.
for (int i = rowsToDelete.Count - 1; i >= 0; i--)
    sheet.Row(rowsToDelete[i]).Delete();
```

```csharp
// KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs — ValidateBudget (v1.3)
foreach (var row in ordered)
{
    var title = /* col C */ ;

    // «Итого…» закрывает текущую главу — последующие повторы статей до новой «Глава X»
    // НЕ агрегируются (избегаем двойного учёта «Этап 2» / «фактические»). Дополнительно
    // фиксируем сумму ИТОГО как chapter-direct override для exporter-а.
    if (title.StartsWith("Итого", IgnoreCase))
    {
        if (currentChapter is not null && !chapterClosed)
        {
            var chapterTotal = ParseSumOrZero(row.Cells["E"]);
            if (chapterTotal > 0)
                chapterDirectTotals[currentChapter.Code] = new ChapterTotalBucket(currentChapter, chapterTotal, row.SourceRowNumber);
            chapterClosed = true;
        }
        continue;
    }

    var entry = _budgetRef.FindByTitle(title);
    // Fallback 1: «Глава N. <чужой суффикс>» — резолвим по номеру.
    entry ??= FindChapterByPrefix(title);
    // Fallback 2: короткая форма Title в файле — reverse-prefix в пределах главы.
    if (entry is null && currentChapter is not null)
        entry = FindArticleInChapterByPrefix(title, currentChapter);

    if (entry is null) continue;
    if (entry.IsChapter) { currentChapter = entry; chapterClosed = false; continue; }
    if (chapterClosed) continue;                                 // 👈 не дублируем повторы

    /* ... агрегация по (chapter, article) ... */
}

// chapterDirectTotals эмитятся отдельным набором mapped-rows c ArticleCode == ChapterCode.
```

```csharp
// KiloImportService.Api/Controllers/ImportsController.cs — endpoint
[HttpGet("{id:guid}/budget-xlsx")]
public async Task<IActionResult> ExportBudgetXlsx(Guid id, ...)
{
    var bytes = await exporter.GenerateAsync(id, ct);
    return File(bytes,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        $"Бюджет_{id}.xlsx");
}
```

UI рендерит **общий раздел «Сформированные файлы»** — компонент [SessionGeneratedFiles.tsx](../KiloImportService.Web/src/components/ImportSession/SessionGeneratedFiles.tsx). Он подключён и в активную сессию ([SessionView](../KiloImportService.Web/src/components/ImportSession/SessionView.tsx)), и в детальный просмотр истории ([HistoryDetailView](../KiloImportService.Web/src/components/ImportHistory/HistoryDetailView.tsx)). Список файлов готовый приходит из ответа `GET /api/imports/{id}` — поле `generatedFiles`:

```csharp
// KiloImportService.Api/Controllers/ImportsController.cs — BuildGeneratedFilesAsync
if (s.ImportTypeCode == "finmodel")
{
    var hasBudgetRows = await _db.StagedRows.AnyAsync(r =>
        r.ImportSessionId == s.Id
        && (r.Status == StagedRowStatus.Valid || r.Status == StagedRowStatus.Applied)
        && r.MappedValues != null
        && EF.Functions.JsonContains(r.MappedValues, "{\"Kind\":\"budget\"}"), ct);
    if (hasBudgetRows)
        files.Add(new { kind = "budget-xlsx", label = "Бюджет для импорта в Visary",
                        description = "...", downloadUrl = $"/api/imports/{s.Id}/budget-xlsx",
                        fileName = $"Бюджет_{s.Id}.xlsx" });
}
```

```tsx
// KiloImportService.Web/src/components/ImportSession/SessionGeneratedFiles.tsx
if (files.length === 0) return null;  // 👈 нет файлов → раздел не рендерится вообще
// каждый элемент массива — label/description/fileName + кнопка «Скачать»
// fetch → blob → downloadBlob(blob, file.fileName) — тот же паттерн, что у PDF-экспорта
```

**Архитектурное решение**: список файлов — это часть состояния сессии (ответ `GET /api/imports/{id}`), а не отдельный эндпоинт `/files`. Backend сам решает «доступен» файл или нет — кнопка «Скачать» не покажется, если нет данных. Это исключает 404-при-клике (плохой UX) и позволяет UI единообразно показывать или скрывать раздел.

### ⚠️ Важно

- **Строки template НЕ генерируются — только подменяются значения.** Visary смотрит на перенос строк и порядок; если сгенерировать «голый» XLSX через ClosedXML, импорт сломается.
- **Колонка G копируется из эталона** (длинные help-тексты по статьям). Не перетираем.
- **Агрегация Глава/Раздел делается в коде.** В эталоне суммы Главы = Σ подстатей. Visary не считает их сама — нужно проставить.
- **Title статьи в шаблоне = Title из `BudgetReferenceProvider`.** Сверка проведена скриптом, расхождений 0 (Code и Title 1-в-1).
- **Контекста ОКСа в файле нет.** Visary при ручном импорте сама спрашивает, в какой проект/ОКС загружать. Имя файла `Бюджет_<sessionId>.xlsx` — только для удобства идентификации.
- **× 1000 при записи.** Финмодель хранит суммы в **тысячах** рублей, Visary ждёт рубли. Множитель `FinmodelToVisaryFactor = 1000` применяется только при записи в XLSX; в БД (`staged_rows`) суммы хранятся как в файле финмодели — без преобразования.
- **Полное дерево статей эталона сохраняется (v1.1), но Главы 2 и 3 сворачиваются до ИТОГО (v1.2).** В v1.1 убрали trim-zeros: даже отсутствующие в финмодели подстатьи попадают в выгрузку с суммой 0. В v1.2 (по ТЗ 2026-05-14) для Глав 2 и 3 это правило **не применяется** — оставляем только сами строки `2.` и `3.` с агрегированным ИТОГО, а все их потомки (`2.1.`, `2.1.1.`, …, `3.8.`) удаляются из выгрузки. Глава 1 — полное дерево (1., 1.1., …, 1.8.) как и раньше. Список свёрнутых глав — `CollapsedChapterCodes` в [BudgetXlsxExporter.cs](../KiloImportService.Api/Budget/BudgetXlsxExporter.cs).
- **Главу распознаём по «Глава N» префиксу (v1.3).** В файле финмодели описательная часть заголовка часто отличается от справочника: файл «Глава 2. Стоимость СМР» vs справочник «Глава 2. Стоимость строительства». Идентификатор главы — её НОМЕР (Code `2.`), а не Title. `FindByTitle` (полное совпадение / fuzzy) для главы тут фейлится → fallback `FindChapterByPrefix` извлекает номер регулярным выражением `^\s*Глава\s+(\d+)\b` и резолвит через `FindByCode("N.")`. Без этого `currentChapter` не переключается на Главу 2/3 → ИТОГО главы не захватывается → override не работает.
- **«Итого» главы — авторитативный источник суммы (v1.3).** В Главах 2/3 заголовки статей в файле не совпадают со справочником («Стоимость СМР», «Инфляционное удорожание»…), поэтому aggregated-by-children == 0. При парсинге бюджета мапер при встрече «Итого…» под `currentChapter` берёт сумму из колонки E и эмитит mapped-row с `ArticleCode == ChapterCode` (sentinel «это ИТОГО главы»). В exporter-е такие строки попадают в `chapterDirect` и применяются как **override** после `AggregateUpwards`. Если в файле «Итого» главы отсутствует или нулевое — остаётся агрегат по children (старое поведение). Для Главы 1 значения совпадают (children sum == Итого), для Глав 2/3 — overrid'ом получаем правильные суммы.
- **Главу закрывает «Итого»; повторы после неё игнорируются.** В файле финмодели после «Итого» главы обычно идут «Этап 2» или фактические значения — те же названия статей с другими цифрами. Раньше мапер `bucket.Sum += sum` всё это аккумулировал → 1.8 «Прочие затраты на улучшения и содержание ЗУ» получало 152 222 вместо 2 222 (regression от 2026-05-14). Теперь: первое «Итого» в пределах `currentChapter` ставит `chapterClosed = true`, последующие article-строки до новой «Глава X» — пропускаются. Сбрасывается `chapterClosed = false` при смене главы. См. `ValidateBudget` в [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs).
- **Fuzzy-match Title — двусторонний.** Title в файле и в справочнике расходятся в обе стороны:
  - **Файл длиннее справочника** — глобальный `FindByTitle` ([BudgetReferenceProvider.cs](../KiloImportService.Api/Domain/Mapping/Budget/BudgetReferenceProvider.cs)): «Затраты на изменение ВРИ, комплексное развитие застроенной территории (соинвестирование по прочим обязательствам)» → «Затраты на изменение ВРИ» (1.6). Самый длинный prefix-match среди не-глав с границей слова.
  - **Файл короче справочника** — chapter-scoped `FindArticleInChapterByPrefix` в [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs): «Прочие затраты» → «Прочие затраты на улучшения и содержание ЗУ» (1.8). Reverse-prefix среди потомков `currentChapter` — глобально нельзя, потому что в каждой главе своя «Прочие …», но в пределах одной главы обычно однозначно. При нескольких кандидатах одной длины — `null` (не угадываем при двусмысленности).
  - Главы (`IsChapter`) ни тем, ни другим способом не fuzzy-матчатся.
- **Удаление строк больше не выполняется (v1.1).** В v1.0 строки вне `BuildKeepSet` удалялись через `Row.Delete()`, что требовало предварительной чистки NamedRanges (ClosedXML падал `ParsingException: Unexpected token EofSymbolId` на пустых refs). Теперь шаблон копируется без удалений — пустые статьи остаются с суммой 0, поэтому ни `Row.Delete()`, ни чистка NamedRanges не нужны.

---

## ❌ Типичная ошибка

### 1. Сгенерировать XLSX «с нуля»

```csharp
// НЕПРАВИЛЬНО — не воспроизведёт переносы строк, ширины, шрифты, формулы.
// Visary при импорте поломается на валидации структуры.
using var wb = new XLWorkbook();
var sh = wb.Worksheets.Add("Бюджет");
sh.Cell(1, 1).Value = "№ п/п";  // и т.д. — теряем все стили эталона
```

```csharp
// ПРАВИЛЬНО — копируем эталон и подменяем только значения.
await using var template = OpenTemplateStream();   // GetManifestResourceStream
using var memory = new MemoryStream();
await template.CopyToAsync(memory, ct);
using var wb = new XLWorkbook(memory);
```

### 2. Выгрузить только заполненные подстатьи

```csharp
// НЕПРАВИЛЬНО — Visary ждёт полное дерево, даже с нулевыми статьями;
// пользователь ожидает увидеть все статьи шаблона.
foreach (var row in budgetRows.Where(r => r.HasSum))
    WriteRow(row);
```

```csharp
// НЕПРАВИЛЬНО (v1.0) — trim-zeros на краях главы и удаление полностью нулевых глав.
// Это приводило к тому, что в выгрузке отсутствовали ожидаемые статьи. Удалено в v1.1.
var keep = BuildKeepSet(aggregated);
// ...
if (!keep.Contains(code)) { rowsToDelete.Add(rownum); continue; }
```

```csharp
// ПРАВИЛЬНО (v1.1) — проходим ВСЕ строки эталона; если в mapped нет — ставим 0.
// Никаких удалений строк, никакого BuildKeepSet.
for (int rownum = 2; rownum <= lastRow; rownum++) {
    var code = NormalizeCode(sheet.Cell(rownum, ColCode).GetString().Trim());
    var (decl, conf) = aggregated.TryGetValue(code, out var v) ? v : (0d, 0d);
    sheet.Cell(rownum, ColDeclaredSum).Value = decl * FinmodelToVisaryFactor;
    sheet.Cell(rownum, ColConfirmedSum).Value = conf * FinmodelToVisaryFactor;
}
```

### 3. Оставить суммы Глав/Разделов пустыми

```csharp
// НЕПРАВИЛЬНО — Visary не агрегирует автоматически, получит "0" в шапке Главы.
sums["1.1."] = (789_789, 789_789);
// Глава "1." не заполнена → импорт пройдёт, но в Visary UI Глава будет 0.
```

```csharp
// ПРАВИЛЬНО — агрегируем снизу вверх по ParentCode + override из «Итого» главы (v1.3).
foreach (var entry in entries.OrderByDescending(e => e.Depth)) {
    if (entry.ParentCode is null) continue;
    if (!acc.TryGetValue(entry.Code, out var self)) continue;
    acc.TryGetValue(entry.ParentCode, out var parent);
    acc[entry.ParentCode] = (parent.Item1 + self.Item1, parent.Item2 + self.Item2);
}
// chapterDirect перетирает агрегат — для Глав, у которых статьи в файле не совпадают
// со справочником (Глава 2: «Стоимость СМР», … → 0 от children). ИТОГО из файла win.
foreach (var (code, v) in chapterDirect) acc[code] = v;
```

### 4. Распознавать главу только по полному Title

```csharp
// НЕПРАВИЛЬНО — в файле финмодели описательная часть отличается:
// «Глава 2. Стоимость СМР» (файл) vs «Глава 2. Стоимость строительства» (справочник).
// FindByTitle вернёт null → currentChapter не переключится → ИТОГО Главы 2 не захватится.
var chapter = _budgetRef.FindByTitle("Глава 2. Стоимость СМР");  // null
```

```csharp
// ПРАВИЛЬНО (v1.3) — fallback по номеру главы через регэкс.
// Идентификатор главы — её Code ("2."), а не Title.
var chapter = _budgetRef.FindByTitle(title) ?? FindChapterByPrefix(title);
// FindChapterByPrefix: regex ^\s*Глава\s+(\d+)\b → FindByCode("N.")
```

### 5. Аккумулировать одну и ту же подстатью между «Итого» / «Этап»

```csharp
// НЕПРАВИЛЬНО — в файле после «Итого» главы 1 идут «Этап 2» / фактические значения
// с теми же названиями статей. Суммирование даст двойной учёт:
// 1.8 «Прочие затраты» = 2 222 + 150 000 = 152 222 вместо ожидаемых 2 222.
if (aggregated.TryGetValue(key, out var bucket)) bucket.Sum += sum;
```

```csharp
// ПРАВИЛЬНО (v1.3) — «Итого…» под currentChapter закрывает главу для дальнейшей сборки.
// chapterClosed сбрасывается на следующей «Глава X»; до этого article-строки пропускаем.
if (title.StartsWith("Итого", IgnoreCase)) { chapterClosed = true; continue; }
// ...
if (chapterClosed) continue;  // 👈 не дублируем повторы
```

---

## 📍 Применение в проекте

| Компонент | Файл | Ключ |
|---|---|---|
| Эталонный XLSX (embedded) | [Resources/budget-template-a41.xlsx](../KiloImportService.Api/Resources/budget-template-a41.xlsx) | `<EmbeddedResource>` в [KiloImportService.Api.csproj](../KiloImportService.Api/KiloImportService.Api.csproj) |
| Справочник статей (оглавление эталона) | [BudgetReferenceProvider.cs](../KiloImportService.Api/Domain/Mapping/Budget/BudgetReferenceProvider.cs) | 99 записей, сверка 1-в-1 с XLSX |
| Парсер секции «Себестоимость» | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `BudgetSectionHint` + `ValidateBudget` |
| Экспортёр | [BudgetXlsxExporter.cs](../KiloImportService.Api/Budget/BudgetXlsxExporter.cs) | embedded → ClosedXML → подмена C/D |
| Endpoint | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) | `GET /api/imports/{id}/budget-xlsx` |
| DI | [Program.cs](../KiloImportService.Api/Program.cs) | `AddScoped<BudgetXlsxExporter>()` |
| `generatedFiles` в API-ответе | [ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) | `BuildGeneratedFilesAsync` (проверка наличия бюджетных staged rows через `EF.Functions.JsonContains`) |
| UI-типы | [api.ts](../KiloImportService.Web/src/types/api.ts), [session.ts](../KiloImportService.Web/src/types/session.ts) | `ApiGeneratedFile`, `UiGeneratedFile`, `UiSession.generatedFiles` |
| UI-маппер | [importMappers.ts](../KiloImportService.Web/src/services/importMappers.ts) | `toUiSession` пробрасывает `generatedFiles` 1-в-1 |
| UI-компонент | [SessionGeneratedFiles.tsx](../KiloImportService.Web/src/components/ImportSession/SessionGeneratedFiles.tsx) | Общий раздел «Сформированные файлы», `fetch → blob → downloadBlob` |
| Использование | [SessionView.tsx](../KiloImportService.Web/src/components/ImportSession/SessionView.tsx), [HistoryDetailView.tsx](../KiloImportService.Web/src/components/ImportHistory/HistoryDetailView.tsx) | `{session.generatedFiles.length > 0 && <SessionGeneratedFiles … />}` |

---

## 🎯 Чек-лист

- [ ] При обновлении эталонного шаблона — заменить файл в `KiloImportService.Api/Resources/` и проверить, что `BudgetReferenceProvider.RawData` всё ещё 1-в-1 совпадает (script: `python diff_provider_vs_xlsx.py`).
- [ ] Лист в шаблоне должен называться `"Бюджет"` (если переименуют — поправить `SheetName` в [BudgetXlsxExporter.cs](../KiloImportService.Api/Budget/BudgetXlsxExporter.cs)).
- [ ] Колонки A/C/D — фиксированный номер. Если в новом эталоне их сдвинут — поменять `ColCode` / `ColDeclaredSum` / `ColConfirmedSum`.
- [ ] При добавлении новых статей в эталон — обновить `BudgetReferenceProvider.RawData` (порядок и Code обязаны совпадать с XLSX).
- [ ] Visary должен принимать сгенерированный файл — sanity-test: импортировать в Visary UI на тестовом ОКСе перед мержем больших изменений в экспортёре.
