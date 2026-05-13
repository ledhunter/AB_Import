# 📤 Экспорт бюджета в XLSX по эталону «Бюджет_А4.1»

## 📋 Описание

**Статус**: 🟢 v1.0 — отдача готового XLSX для ручного импорта в Visary.
**Дата**: 2026-05-13
**Заменяет подходы**: [70-wbs-api-foundation.md](70-wbs-api-foundation.md), [71-finmodel-budget-import.md](71-finmodel-budget-import.md)
(CRUD-путь WBS отключён, см. ниже).

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

### Ключевые места кода

```csharp
// KiloImportService.Api/Budget/BudgetXlsxExporter.cs
private const string TemplateResourceName =
    "KiloImportService.Api.Resources.budget-template-a41.xlsx";  // 👈 embedded
private const string SheetName = "Бюджет";
private const int ColCode         = 1; // A
private const int ColDeclaredSum  = 3; // C
private const int ColConfirmedSum = 4; // D

// Прогон строк template — НЕ генерируем заново, копируем эталон.
var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
for (int rownum = 2; rownum <= lastRow; rownum++)
{
    var code = NormalizeCode(sheet.Cell(rownum, ColCode).GetString());
    var (decl, conf) = aggregated.TryGetValue(code, out var v) ? v : (0d, 0d);
    sheet.Cell(rownum, ColDeclaredSum).Value = decl;
    sheet.Cell(rownum, ColConfirmedSum).Value = conf;
}
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
- **Trim-zeros по краям с сохранением промежуточных.** Полное дерево из эталона не выгружается — пустые ветки удаляются. Но между двумя ненулевыми подстатьями нулевые промежуточные **сохраняются** (1.1 + 1.4 без 1.2/1.3 ломает импорт Visary; 1.5..1.8 после 1.4 — отрезаем). Если у Главы все подстатьи нулевые — Глава удаляется целиком. См. `BuildKeepSet` в [BudgetXlsxExporter.cs](../KiloImportService.Api/Budget/BudgetXlsxExporter.cs).
- **Fuzzy-match Title.** Title из финмодели часто длиннее справочного («Затраты на изменение ВРИ, комплексное развитие застроенной территории (соинвестирование по прочим обязательствам)» → справочная «Затраты на изменение ВРИ», Code `1.6.`). [FindByTitle](../KiloImportService.Api/Domain/Mapping/Budget/BudgetReferenceProvider.cs#L77) сначала пробует точное совпадение, потом — самый длинный prefix-match среди не-глав с границей слова (за prefix-ом — пробел/запятая/точка/скобка). Главы prefix-фолбэком не матчатся.
- **Defined names очищаются перед удалением строк.** ClosedXML при `Row.Delete()` пересчитывает refs у named ranges и падает с `ParsingException: Unexpected token EofSymbolId` на пустых ссылках. Visary defined names не использует — безопасно удаляем все NamedRanges перед операциями над строками.

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
// НЕПРАВИЛЬНО — Visary ждёт полное дерево, даже с нулевыми статьями.
foreach (var row in budgetRows.Where(r => r.HasSum))
    WriteRow(row);
```

```csharp
// ПРАВИЛЬНО — проходим ВСЕ строки эталона; если в mapped нет — ставим 0.
for (int rownum = 2; rownum <= lastRow; rownum++) {
    var (decl, conf) = aggregated.TryGetValue(code, out var v) ? v : (0d, 0d);
    /* ... */
}
```

### 3. Оставить суммы Глав/Разделов пустыми

```csharp
// НЕПРАВИЛЬНО — Visary не агрегирует автоматически, получит "0" в шапке Главы.
sums["1.1."] = (789_789, 789_789);
// Глава "1." не заполнена → импорт пройдёт, но в Visary UI Глава будет 0.
```

```csharp
// ПРАВИЛЬНО — агрегируем снизу вверх по ParentCode.
foreach (var entry in entries.OrderByDescending(e => e.Depth)) {
    if (entry.ParentCode is null) continue;
    if (!acc.TryGetValue(entry.Code, out var self)) continue;
    acc.TryGetValue(entry.ParentCode, out var parent);
    acc[entry.ParentCode] = (parent.Item1 + self.Item1, parent.Item2 + self.Item2);
}
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
