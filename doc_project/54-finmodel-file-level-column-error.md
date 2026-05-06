# 🛑 FinModel: file-level ошибка `column_not_found` вместо row-spam

## 📋 Описание

Маппер «Финмодель» (`FinModelImportMapper`) ищет колонку `Тип отделки` (или алиасы `FinishingType` / `Finishing`). Если пользователь загрузил **не тот шаблон** (например, «Параметры к переносу в АБ.xlsx» с листом «Outputs 7 этапов»), колонка не находится.

**Раньше**: на каждой из 2782 строк создавалась отдельная row-level ошибка `column_not_found` с одинаковым текстом → раздутый отчёт, нагрузка на БД, пользователь не понимает, *что именно* не так с файлом.

**Теперь**: одна **file-level** ошибка с перечислением реально найденных колонок — пользователь сразу видит, что загружен не тот шаблон.

> 🔁 См. также: `23-finmodel-import.md`, `24-finmodel-testing-and-fixes.md`, `26-troubleshooting.md`.

---

## ✅ Правильная реализация

### Pre-flight проверка перед row-loop

```csharp
public async Task<ValidationResult> ValidateAsync(
    ImportContext context,
    IReadOnlyList<ParsedRow> rows,
    VisaryDbContext visaryDb,
    CancellationToken ct)
{
    // ... проверки siteId / site ...

    // 👇 Pre-flight: ищем целевую колонку один раз на уровне всего файла.
    // Учитываем sparse-строки: агрегируем ключи всех строк через
    // case-insensitive Distinct.
    var allColumns = rows
        .SelectMany(r => r.Cells.Keys)
        .Where(k => !string.IsNullOrWhiteSpace(k))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    var fileFinishingTypeCol = allColumns.FirstOrDefault(k =>
        FinishingTypeAliases.Any(a => a.Equals(k, StringComparison.OrdinalIgnoreCase)));

    if (fileFinishingTypeCol is null)
    {
        // Без колонки делать нечего — ОДНА file-level ошибка со списком
        // обнаруженных колонок. Никакого row-spam.
        var detectedList = allColumns.Count == 0
            ? "(колонки не найдены)"
            : string.Join(", ", allColumns.Take(20).Select(c => $"'{c}'"))
              + (allColumns.Count > 20 ? $" и ещё {allColumns.Count - 20}…" : string.Empty);

        fileErrors.Add(new RowError(
            string.Join(" / ", FinishingTypeAliases),
            "column_not_found",
            $"Не найдена колонка 'Тип отделки' (допустимые алиасы: {string.Join(", ", FinishingTypeAliases)}). " +
            $"В файле обнаружены колонки: {detectedList}. " +
            "Убедитесь, что вы загружаете шаблон импорта 'Финмодель'."));

        return new ValidationResult([], fileErrors);
    }

    // Дальше row-loop — колонка гарантированно есть в файле,
    // но в конкретной строке может отсутствовать (sparse) → row-level error.
    // ...
}
```

### ⚠️ Важно

- **Distinct по `StringComparer.OrdinalIgnoreCase`** — Excel может содержать `"Тип отделки"`, `"тип отделки"`, `"ТИП ОТДЕЛКИ"` в разных строках, считаем как одну.
- **Take(20)** в детекте — защита от файлов с десятками колонок (UI не должен задыхаться от тысячи символов в одной ошибке).
- **Pre-flight ≠ замена row-level**: если колонка нашлась на уровне файла, но **в конкретной строке отсутствует** (sparse Excel), фиксируем row-level `value_empty` — это нормальный случай для мульти-строчных импортов.
- `ValidationResult([], fileErrors)` — возвращаем пустой `Rows` и единственную ошибку → отчёт показывает file-level блок, не пытается отрендерить 2782 строки.

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — каждая строка генерирует одинаковую row-level ошибку
for (int i = 0; i < rows.Count; i++)
{
    var row = rows[i];
    var finishingTypeCol = row.Cells.Keys.FirstOrDefault(k =>
        FinishingTypeAliases.Any(a => a.Equals(k, StringComparison.OrdinalIgnoreCase)));

    if (finishingTypeCol is null)
    {
        // ❌ 2782 одинаковых ошибки в БД, у пользователя — стена spam'а в отчёте.
        rowErrors.Add(new RowError("...", "column_not_found", "Не найдена колонка 'Тип отделки'."));
        mappedRows.Add(new MappedRow(row.SourceRowNumber, false, ..., rowErrors));
        continue;
    }
}
```

**Симптомы**:
- В отчёте: «Строк: 2782 · валидных: 0 · с ошибками: 2782».
- Все 2782 ошибки идентичны.
- Пользователь не понимает, **какие** колонки в файле есть → не догадывается, что загрузил неверный шаблон.
- БД: `import.import_errors` раздувается на каждый импорт.

---

## 📍 Применение в проекте

| Компонент | Файл | Что делает |
|-----------|------|-----------|
| `FinModelImportMapper.ValidateAsync` | [Domain/Mapping/FinModelImportMapper.cs:71-105](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs#L71-L105) | Pre-flight + file-level error |
| Тест file-level error | [Mapping/FinModelImportMapperTests.cs:85-119](../KiloImportService.Api.Tests/Mapping/FinModelImportMapperTests.cs#L85-L119) | `ValidateAsync_MissingColumn_ReturnsFileLevelErrorWithDetectedColumns` |

---

## 🎯 Чек-лист (применять при добавлении нового маппера)

- [ ] Целевые колонки/обязательные параметры проверяются **один раз** перед row-loop.
- [ ] Если колонки нет — `fileErrors.Add(...)` + `return ValidationResult([], fileErrors)`. **Не** генерировать row-level ошибки.
- [ ] Сообщение об ошибке содержит:
  - [ ] Имя/алиасы искомой колонки.
  - [ ] Список реально найденных колонок (с лимитом ~20).
  - [ ] Подсказку о правильном шаблоне.
- [ ] `Distinct` по `StringComparer.OrdinalIgnoreCase` (Excel допускает регистр-вариации).
- [ ] Тест `MissingColumn_ReturnsFileLevelError` проверяет: `result.Rows` пустой + единственная ошибка в `result.FileLevelErrors`.
- [ ] Логировать `LogWarning` со списком обнаруженных колонок — для дебага в backend-логах.

---

## 🧪 Связанный паттерн: row-level vs file-level

| Тип ошибки | Когда применять | Пример |
|------------|----------------|--------|
| **File-level** (`fileErrors`) | Структурная проблема всего файла: нет колонки, нет листа, не выбран site | `column_not_found`, `site_required`, `sheet_missing` |
| **Row-level** (`rowErrors`) | Локальная проблема конкретной строки: пустое значение, неверное значение, ссылка на несуществующий объект | `value_empty`, `invalid_value`, `entity_not_found` |

**Правило**: если ошибка **одинакова для всех строк** — это file-level, а не row-level.
