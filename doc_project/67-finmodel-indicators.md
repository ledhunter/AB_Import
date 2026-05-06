# 📊 Финмодель: показатели объекта (ConstructionSiteIndicator + Value по стадии)

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-06

В шаблон «Финмодель» добавлен **третий тип параметров** — показатели (ТЭПы) объекта строительства.
Первый показатель: **«Площадь застройки»**, обновляется значение со **стадией «Экспертиза»** (`Stage = 50`).

В отличие от FK-параметров (Тип отделки, Класс жилья — см. [66](./66-finmodel-estate-class.md)),
показатель — это не поле Site, а отдельная сущность `ConstructionSiteIndicator`, привязанная к
объекту, у которой есть N значений (`ConstructionSiteIndicatorValue`) — по одному на каждую
стадию документа (ГПЗУ, ГенПлан, Экспертиза, …).

> 🔁 См. также: [50-visary-api-new-methods.md](./50-visary-api-new-methods.md)
> (clients для indicator/value), [62-vertical-keyvalue-layout.md](./62-vertical-keyvalue-layout.md),
> [63-site-finishing-material-update-crud.md](./63-site-finishing-material-update-crud.md) (паттерн
> GET → PATCH с RowVersion).

---

## ✅ Правильная реализация

### Полный flow обновления показателя

```
1. listview/constructionsiteindicator/onetomany/ConstructionSite?associationId={siteId}
   с фильтром ["Title","=","Площадь застройки"]
   → получаем ConstructionSiteIndicator.ID = 114306

2. listview/constructionsiteindicatorvalue/onetomany/ConstructionSiteIndicator?associationId=114306
   → получаем все Value-записи по этому показателю

3. Среди них находим запись со Stage == 50 (Экспертиза)
   → ID = 823481

4. GET /crud/constructionsiteindicatorvalue/823481
   → актуальный RowVersion: long (нужен для optimistic locking; в listview Version: DateTime — не подходит)

5. PATCH /crud/constructionsiteindicatorvalue/823481?forceUpdate=false
   body: { ID: 823481, RowVersion: 4755619, Value: 333 }
```

### Декларативное описание показателя в маппере

```csharp
// KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs
private const int ProjectStageExpertise = 50;

private static readonly IndicatorParameter[] Indicators =
[
    new(
        HumanName:   "Площадь застройки",
        Aliases:     ["Площадь застройки", "BuildingArea"],
        VisaryTitle: "Площадь застройки",
        Stage:       ProjectStageExpertise),
    new(
        HumanName:   "Плотность застройки",
        Aliases:     ["Плотность застройки", "BuildingDensity"],
        VisaryTitle: "Плотность застройки",
        Stage:       ProjectStageExpertise),
];

private sealed record IndicatorParameter(
    string HumanName,
    string[] Aliases,
    string VisaryTitle,
    int Stage);
```

**Добавление нового показателя** (например, «Количество квартир» на стадии Экспертиза) =
**одна строка** в массиве `Indicators`. Flow остаётся прежним.

### `ApplyIndicatorAsync` — единый метод flow

```csharp
private async Task ApplyIndicatorAsync(int siteId, IndicatorParameter param, double value, CancellationToken ct)
{
    // 1. Найти показатель по точному Title (Filter ["Title","=",X])
    var indicators = await _listViewClient.GetIndicatorsBySiteAsync(siteId, param.VisaryTitle, ct);
    var indicator = indicators.Data.FirstOrDefault(i =>
        string.Equals(i.Title, param.VisaryTitle, StringComparison.OrdinalIgnoreCase));
    if (indicator is null)
        throw new KeyNotFoundException(
            $"Показатель '{param.VisaryTitle}' не найден у объекта siteId={siteId}.");

    // 2. Значение нужной стадии
    var values = await _listViewClient.GetIndicatorValuesByIndicatorAsync(indicator.ID, ct);
    var target = values.Data.FirstOrDefault(v => v.Stage == param.Stage);
    if (target is null)
        throw new KeyNotFoundException(
            $"У показателя '{param.VisaryTitle}' нет значения со стадией {param.Stage}.");

    // 3. GET для свежего RowVersion (long)
    var current = await _visaryClient.GetIndicatorValueByIdAsync(target.ID, ct);

    // 4. PATCH
    await _visaryClient.PatchIndicatorValueAsync(target.ID, new IndicatorValuePatchRequest
    {
        ID         = target.ID,
        RowVersion = current.RowVersion,
        Value      = value,
    }, ct);
}
```

### Обновлённый `IndicatorValuePatchRequest` + `PatchIndicatorValueAsync`

```csharp
// Visary.Api.Client/Dto/VisaryCrudRequests.cs
public sealed class IndicatorValuePatchRequest
{
    public int ID { get; set; }
    public long RowVersion { get; set; }   // ← добавлено в v2 для optimistic locking
    public double? Value { get; set; }
    public double? PlanValue { get; set; }
    public double? ForecastValue { get; set; }
}

// Visary.Api.Client/CRUD/CrudClient.cs
public Task<bool> PatchIndicatorValueAsync(
    int valueId, IndicatorValuePatchRequest request, CancellationToken ct)
{
    // forceUpdate=true → false (как у Site PATCH'ей, см. doc 63)
    ApplyEntityId(request, valueId, r => r.ID, (r, v) => r.ID = v, nameof(valueId));
    return PatchAndReportAsync(
        $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.SiteIndicatorValue}/{valueId}?forceUpdate=false",
        request, $"{VisaryMnemonics.SiteIndicatorValue}/{valueId}", valueId, ct,
        $"CrudClient.PatchIndicatorValueAsync: valueId={{Id}} success");
}
```

### Парсер чисел в Excel — `TryParseFlexibleDouble`

```csharp
// Поддерживает: "12345.67" (invariant), "12345,67" (ru-RU), "12 345,67" (с разделителем тысяч)
private static bool TryParseFlexibleDouble(string raw, out double result)
{
    var cleaned = raw.Replace(" ", "").Replace(" ", "");  // обычный + неразрывный пробел
    if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
        return true;
    return double.TryParse(cleaned.Replace(',', '.'), NumberStyles.Float,
        CultureInfo.InvariantCulture, out result);
}
```

### ⚠️ Важно

- **Stage = 50 = «Экспертиза»** — взято из `Domain.Model.Enums.ProjectStage` (находка
  в `FinModel/Альфа Банк. Управление проектами.drawio.xml` → `"50 Expertise (Экспертиза)"`).
  Если в Visary поменяют enum-значение — нужно поправить константу `ProjectStageExpertise`.
- **Один GET на свежий RowVersion для каждого indicator-параметра.** Listview-ответ
  возвращает `Version: DateTime`, а PATCH ожидает `RowVersion: long` — это два разных
  поля в Visary. Не пытаться привести Version.Ticks к RowVersion.
- **forceUpdate=false** — точно как пользователь показал в payload. Под `true` Visary
  пытается «дописать» поля в загруженный JObject и падает с `Property RowVersion already exists`
  (тот же баг, что для Site в [doc 63](./63-site-finishing-material-update-crud.md)).
- **Поиск показателя через filter `["Title","contains","X"]`**, точное равенство — уже
  в коде после ответа через `Trim()+OrdinalIgnoreCase`. Причина: реальные показатели в
  Visary могут иметь **хвостовые пробелы в Title** (`"Площадь застройки "` ← с пробелом!).
  Точный `=` фильтр такие записи не находит. UI Visary использует именно `contains` —
  по той же причине. Защита от ложных match'ей («Общая площадь застройки») —
  пост-фильтрация в коде (`string.Equals(i.Title?.Trim(), needle, OrdinalIgnoreCase)`).
- **Apply non-transactional.** FK-обновления (Тип отделки, Класс жилья) и indicator-обновления —
  4+ независимых PATCH'а. Если упало в середине, предыдущие не откатываются. Поэтому ошибки
  индикаторов попадают в `Errors` отдельным `indicator_not_found` / `indicator_update_error`,
  но FK-обновления в этом случае всё равно были применены успешно.
- **Indicator-параметры обязательны** в шаблоне (column_not_found если нет колонки), но
  при ошибке резолва на стороне Visary (показателя нет на этом сайте, нет значения нужной
  стадии) — это row-level / apply-level ошибка, а не file-level. Файл валиден, конкретное
  обновление не прошло.
- **Парсинг double — flexible.** Excel может вернуть значение в любом формате (invariant,
  ru-RU, с пробелами тысяч). `TryParseFlexibleDouble` пробует все три. Если ни один не
  подходит — `invalid_value` row-error.

---

## ❌ Типичные ошибки

### 1. Использовать `Version: DateTime` из listview как `RowVersion: long`

```csharp
// ❌ Кросс-формат — Visary вернёт 409 либо проигнорирует.
var values = await _listViewClient.GetIndicatorValuesByIndicatorAsync(indicatorId, ct);
var target = values.Data.First(v => v.Stage == 50);
var rowVersion = target.Version!.Value.Ticks;   // ← это DateTime.Ticks, НЕ Visary RowVersion
```

```csharp
// ✅ Отдельный GET через CRUD endpoint для актуального long-RowVersion
var current = await _visaryClient.GetIndicatorValueByIdAsync(target.ID, ct);
var rowVersion = current.RowVersion;
```

### 2. PATCH с `forceUpdate=true`

```csharp
// ❌ → 500 "Property RowVersion already exists"
$"{BaseUrl}/api/visary/crud/constructionsiteindicatorvalue/{valueId}?forceUpdate=true"
```

То же самое поведение, что в [doc 63](./63-site-finishing-material-update-crud.md): `false`
включает обычный optimistic update без попытки JObject.Add().

### 3. Захардкодить Stage по Title

```csharp
// ❌ Visary enum-значения могут отличаться от UI-Title. Хардкод имени стадии в коде = бомба замедленного действия.
var stageId = stageTitle switch { "Экспертиза" => 50, _ => 0 };
```

В нашем случае хардкод неизбежен (`Domain.Model.Enums.ProjectStage` — enum, не listview),
но он должен жить в одной константе `ProjectStageExpertise = 50` рядом с upd-flow, а не
рассыпаться по коду.

### 4. Поиск показателя по `contains` без пост-фильтрации

```csharp
// ❌ "Площадь" может matchать «Площадь застройки», «Общая площадь» и пр.
var indicators = await _listViewClient.GetIndicatorsBySiteAsync(siteId, "Площадь", ct);
var indicator = indicators.Data.First();   // ← может оказаться не тот
```

Filter `["Title","contains","Площадь застройки"]` на сервере **обязателен** (точный `=`
не находит записи с хвостовыми пробелами — типичный баг Visary), но **результат всегда
пост-фильтруем по точному равенству** через `Trim()+OrdinalIgnoreCase`:

```csharp
var needle = param.VisaryTitle.Trim();
var indicator = indicators.Data.FirstOrDefault(i =>
    string.Equals(i.Title?.Trim(), needle, StringComparison.OrdinalIgnoreCase));
```

### 5. Точное `=` в фильтре для показателей

```csharp
// ❌ Visary иногда хранит Title с хвостовыми пробелами ("Площадь застройки ").
//    "=" не найдёт такую запись → indicator_not_found, хотя показатель есть.
Filter = FilterByString("Title", "Площадь застройки");
```

```csharp
// ✅ contains на сервере + Trim() в коде
Filter = FilterByStringContains("Title", "Площадь застройки");
```

### 5. Индикатор-обновление в общем try/catch с FK

```csharp
// ❌ Если индикатор не найден (показатель отсутствует на сайте) — общий catch
//    кинет visary_update_error для всего, маскируя реальную причину.
try {
    await UpdateSiteFk(...);
    await UpdateIndicator(...);   // KeyNotFoundException
} catch (Exception ex) {
    errors.Add(new RowError(null, "visary_update_error", ex.Message));
}
```

```csharp
// ✅ Свой try/catch вокруг каждого indicator-параметра
foreach (var (param, value) in indicators)
{
    try { await ApplyIndicatorAsync(siteId, param, value, ct); }
    catch (KeyNotFoundException ex) { errors.Add(new RowError(null, "indicator_not_found", ex.Message)); }
    catch (Exception ex) { errors.Add(new RowError(null, "indicator_update_error", ex.Message)); }
}
```

---

## 📍 Применение в проекте

| Компонент | Файл | Что добавлено / изменено |
|-----------|------|--------------------------|
| `IndicatorValuePatchRequest` | [Visary.Api.Client/Dto/VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs) | Поля `ID`, `RowVersion` |
| `PatchIndicatorValueAsync` | [Visary.Api.Client/CRUD/CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | `forceUpdate=true` → `false`; добавлен `ApplyEntityId`/`PatchAndReportAsync` (как у Site) |
| `IndicatorParameter` (record) | [Domain/Mapping/FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | Декларативное описание показателя для импорта |
| `Indicators[]` | там же | Список indicator-параметров (расширяется одной строкой) |
| `ProjectStageExpertise = 50` | там же | Константа стадии «Экспертиза» |
| `ApplyIndicatorAsync` | там же | Полный flow: indicator → value по Stage → GET для RowVersion → PATCH |
| `TryParseFlexibleDouble` | там же | Парсер числа из Excel (invariant + ru-RU + пробелы тысяч) |
| `ResolveDoubleValue` / `ReadCellTrimmed` | там же | Generic helpers для row-level валидации |
| Тесты | [FinModelImportMapperTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelImportMapperTests.cs) | +5 тестов: парсинг разных форматов double, алиасы, отсутствующая колонка, end-to-end Apply, indicator_not_found |

### Что **переиспользовано без дублирования**

- `IListViewClient.GetIndicatorsBySiteAsync(siteId, titleFilter, ct)` — был для другого UI.
- `IListViewClient.GetIndicatorValuesByIndicatorAsync(indicatorId, ct)` — был для другого UI.
- `ICrudClient.GetIndicatorValueByIdAsync(id, ct)` — был.
- `ConstructionSiteIndicatorValueFull.RowVersion: long` — auto-generated DTO.
- Generic helpers `FindColumn`, `BuildColumnNotFoundError`, `ReadCellTrimmed` (вынесены из FK-flow).

---

## 🎯 Чек-лист (при добавлении нового показателя в Финмодель)

- [ ] Знать **точный Title** показателя в Visary (открыть UI → Показатели → найти запись).
- [ ] Знать **Stage** (число) из `Domain.Model.Enums.ProjectStage`. Если стадия новая —
      добавить константу рядом с `ProjectStageExpertise` (один источник).
- [ ] Добавить **одну строку** в `Indicators[]`:
      `new("Имя в логах", ["Алиас в Excel", "EnAlias"], "Title в Visary", StageNumber)`.
- [ ] Убедиться, что в шаблоне Excel колонка есть и имя совпадает с одним из алиасов.
- [ ] Тест: успех (parses + PATCH'ит правильное значение), отсутствие колонки (file-level),
      отсутствие показателя/стадии у конкретного сайта (apply-level error, не падение).

---

## 🧪 Связанный паттерн: три типа параметров одного импорта

| Тип параметра | Что обновляет | Пример | Find-flow |
|---|---|---|---|
| **Dictionary FK** | поле Site (FK) | Тип отделки, Класс жилья | `List*Async` справочник → Title→ID lookup |
| **Indicator value** | `ConstructionSiteIndicatorValue.Value` | Площадь застройки | siteId → indicator by Title → value by Stage |
| **Direct field** | поле Site (не FK) | Описание, дата (когда понадобится) | прямой PATCH |

Каждый тип — свой flow в маппере, но шаги Validate (поиск колонки + парсинг ячейки) общие
через `ResolveDictionaryValue` / `ResolveDoubleValue`. Дальнейшие типы добавляются по тому же
паттерну: декларативный массив + единый `Apply*Async` метод.

---

**Версия**: 1.0
**Дата**: 2026-05-06
