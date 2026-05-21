# 🧹 Импорт «Помещения» — три починки после первой production-выгрузки

## 📋 Описание

После прогона импорта на реальном файле «Репино-Парк» обнаружились три независимых дефекта:

1. **Пагинация раздувалась**: на странице 1 вместо 50 строк показывалось «1–693 из 1735».
2. **`apply_failed: Visary вернул 500`** на 11 строках — все с одной комбинацией: `№ ДДУ = #N/A` (Excel-ошибка в формуле).
3. **`TotalArea` для нежилых не записывался** — в Visary улетал только `"ProjectArea":0`, машиноместо/кладовая оставались без площади.

---

## 1️⃣ Пагинация — ровно `take` строк на странице

### ✅ Правильная реализация

```ts
// importMappers.ts — toUiReport
const rows: UiReportRow[] = (api.rows ?? []).map(r => ({
  rowNumber: r.sourceRowNumber, sheet: r.sheet, status: r.status,
  errors: errorsByRow.get(rowKey(r.sheet, r.sourceRowNumber)) ?? [],
  actions: r.actions ?? [],
}));
// 👇 НИЧЕГО больше в rows не пушим — даже «осиротевшие» ошибки.
//    Ошибка отрисуется на той странице, где её строка попадает в выборку.
rows.sort(...);
```

### ❌ Типичная ошибка (откатили)

```ts
// Этот блок раздувал страницу: backend отдавал errors всей сессии (без skip/take),
// и UI пушил их в rows как отдельные строки → 50 page + 643 orphan = 693.
const seenKeys = new Set(rows.map(r => rowKey(r.sheet, r.rowNumber)));
for (const [key, errors] of errorsByRow.entries()) {
  if (!seenKeys.has(key) && errors.length > 0) {
    rows.push({ rowNumber: errors[0].rowNumber, ... });
  }
}
```

### ⚠️ Важно

- Backend `GetReport` пагинирует `rows` (`Skip(skip).Take(take)`), а `errors` отдаёт целиком (нужны для подсчёта). UI должен сам игнорировать те `errors`, чьи `(sheet, rowNumber)` не попали в страницу.
- При **сворачивании листа** backend пересчитывает `total` (excludeSheets применяется к выборке), пагинация по 50 видимых строк работает естественно.
- Range считается так: `rangeTo = min(total, skip + rows.length)`. После фикса `rows.length ≤ take`, поэтому страница всегда стабильна.

---

## 2️⃣ Excel-ошибки `#N/A` в источнике — обнуляем в Validate

### Симптом

```
RoomsForm.Apply: найден орфанный/несоответствующий ДДУ id=809 number='#N/A' (Room=22652) — привязываем к roomId=22653
RoomsForm.Apply: найден орфанный/несоответствующий ДДУ id=809 number='#N/A' (Room=22653) — привязываем к roomId=22654
RoomsForm.Apply: найден орфанный/несоответствующий ДДУ id=809 number='#N/A' (Room=22654) — привязываем к roomId=22655
... (сотни раз)
apply_failed: row 528: Visary вернул 500 Internal Server Error
```

Колонка `№ ДДУ` в Excel-файле содержала формулу типа `=VLOOKUP(...)`, которая падала с `#N/A`. Маппер читал это как строку, в Apply создавал ShareAgreement с `Number="#N/A"`, при следующих строках находил тот же глобальный ДДУ → orphan-reanimate → Visary бракует.

### ✅ Правильная реализация

```csharp
// RoomsFormImportMapper.cs
private static readonly HashSet<string> ExcelErrorMarkers = new(StringComparer.OrdinalIgnoreCase)
{
    "#N/A", "#REF!", "#VALUE!", "#NAME?", "#NUM!", "#DIV/0!", "#NULL!", "#GETTING_DATA",
};

// В ValidateAsync, после ReadString для каждого ключевого поля:
if (ExcelErrorMarkers.Contains(shareAgreement.Trim()))  shareAgreement  = string.Empty;
if (ExcelErrorMarkers.Contains(projectNum.Trim()))      projectNum      = string.Empty;
// и т. д. для permission/stage/sectionTitle/floor/buildingSection/developerPin
```

### ❌ Типичная ошибка

```csharp
// Принять «#N/A» как валидное значение и попытаться отправить в Visary.
// Visary создаёт первую запись, последующие attaches к тому же объекту → race → 500.
if (!string.IsNullOrWhiteSpace(saNumber)) { /* CreateShareAgreementAsync с Number="#N/A" */ }
```

### ⚠️ Важно

- Фильтр в **Validate**, не в Apply: иначе попадает в snapshot, и diff-skip принимает «#N/A» за валидную базу.
- Excel-ошибки на `НПС/Этап` ⇒ строка автоматически попадает в site_not_found_in_project (а не в exception); это уже желаемое поведение.
- Помещения без ДДУ (`saNumber = ""`) Apply корректно создаёт/обновляет — просто не вызывает `CreateShareAgreementAsync`.

---

## 3️⃣ `TotalArea` для нежилых — отдельный алиас «Общая площадь, кв.м.»

### Симптом (из логов Visary CRUD)

```
POST /api/visary/crud/room body={
  "SiteID":7943, "Kind":{"ID":4 /* машиноместо */}, "ProjectArea":0, ...
  // 👈 TotalArea отсутствует — поле в Visary остаётся пустым
}
```

В файле колонка для нежилых называется **«Общая площадь, кв.м.»** (а не «Площадь (для квартир ...)»). Старый код имел только `ProjectAreaAliases` → значение не вычитывалось → `areaFromFile = null` → `TotalArea = null`.

### ✅ Правильная реализация

```csharp
private static readonly string[] TotalAreaAliases = [
    "Общая площадь, кв.м.", "Общая площадь, кв.м", "Общая площадь",
    // v1.1: реальный заголовок в Репино-Парк, лист «Машиноместо» — БЕЗ слова «Общая».
    // Точное сравнение в ReadString не матчило с алиасом «Площадь» из ProjectAreaAliases.
    "Площадь, кв.м.", "Площадь, кв.м", "Площадь кв.м.", "Площадь кв.м",
    "TotalArea"];

// В ValidateAsync:
double? totalArea = TryParseNullableDouble(ReadString(row, TotalAreaAliases), out var taErr);
// ... в mapped: ["TotalArea"] = totalArea

// В ApplyAsync (раскладка по жилому/нежилому):
var projectAreaFile = GetDoubleOrNull(v, "ProjectArea");
var totalAreaFile   = GetDoubleOrNull(v, "TotalArea");
var isNonResidential = roomCategory.HasValue && roomCategory.Value != ResidentialRoomCategory;
double? projectAreaForCrud = isNonResidential ? 0d : projectAreaFile;
double? totalAreaForCrud   = isNonResidential
    ? (totalAreaFile ?? projectAreaFile)   // 👈 fallback на «Площадь», если «Общая площадь» пуста
    : null;
```

### ❌ Типичная ошибка

```csharp
// Использовать одну колонку для обоих сценариев — теряем данные для нежилых.
var areaFromFile = GetDoubleOrNull(v, "ProjectArea");
double? totalAreaForCrud = isNonResidential ? areaFromFile : null;
// areaFromFile=null для нежилого → TotalArea=null → Visary без площади
```

### ⚠️ Важно

- Жилые (`RoomCategory=0 Residential`) — без изменений: `ProjectArea ← «Площадь (для квартир…)»`, `TotalArea` не отправляем.
- Fallback `totalAreaFile ?? projectAreaFile` нужен на случай, когда нежилой шаблон использует общую колонку «Площадь» (наблюдалось в части тестовых файлов).
- В Visary `Kind=4` (Машиноместо) = `RoomCategory=2 ParkingPlace`, `Kind=5` (Кладовая) = `RoomCategory=3 OtherNonResidential`, `Kind=9` (Нежилое помещение) = `RoomCategory=1 NonResidential` — все попадают в нежилую ветку.

---

## 📍 Применение в проекте

| Слой | Файл | Что изменилось |
|------|------|----------------|
| UI | [importMappers.ts:158](../KiloImportService.Web/src/services/importMappers.ts#L158) | Убран orphan-rows push в `toUiReport` |
| Mapper | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | `TotalAreaAliases`, `ExcelErrorMarkers`, фильтр в Validate, раскладка площадей в Apply |
| Tests | [RoomsFormImportMapperApplyTests.cs](../KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperApplyTests.cs) | +3 теста: TotalArea, TotalArea-fallback, `#N/A`→пусто |

---

## 🎯 Чек-лист при дальнейших правках rooms-импорта

- [ ] Любая новая «потенциально-формульная» колонка в файле — добавить фильтр `ExcelErrorMarkers` в Validate
- [ ] Не возвращать backend в режим «errors без пагинации, UI дополняет rows» — это снова раздует страницу
- [ ] Любая раскладка по `RoomCategory` — учитывать все 4 значения (0=Residential, 1/2/3=non)
- [ ] При появлении новых псевдо-площадей в файле — расширять либо `ProjectAreaAliases`, либо `TotalAreaAliases`, не оба

---

## Связанные документы

- [101-rooms-multi-site-by-project.md](101-rooms-multi-site-by-project.md) — per-row site resolve (фикс v1.0 этого же импорта)
- [56-visary-dto-deserialization-pitfalls.md](56-visary-dto-deserialization-pitfalls.md) — JsonElement?/FlexibleStringJsonConverter для variant-полей Visary
- [98-action-filters-and-single-sheet-collapse.md](98-action-filters-and-single-sheet-collapse.md) — action-фильтры, упоминание `rowsPagination/sheetTotals/actionTotals`
- [87-report-pagination.md](87-report-pagination.md) — оригинальная сборка пагинации (REPORT_PAGE_SIZE=100→50)
- [95-history-project-filter-and-collapsible-sheets.md](95-history-project-filter-and-collapsible-sheets.md) — excludeSheets, sheetTotals
