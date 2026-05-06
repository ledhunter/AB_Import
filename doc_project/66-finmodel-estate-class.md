# 🏘️ Финмодель: добавление параметра «Класс жилья» (EstateClass)

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-06

В шаблон «Финмодель» (лист `Inputs`, вертикальный key-value layout) добавлен второй параметр —
**«Класс жилья»**. На стороне Visary он называется **«Класс недвижимости»** (`EstateClass`).
Принцип маппинга идентичен «Типу отделки» (см. [63](./63-site-finishing-material-update-crud.md)
и [64](./64-dynamic-finishing-material-dictionary.md)) — справочник тянется живьём, ID резолвится
case-insensitive по `Title`, обновление через CRUD PATCH.

> 🔁 См. также: [62-vertical-keyvalue-layout.md](./62-vertical-keyvalue-layout.md)
> (раскладка шаблона), [65-merge-integration-with-shared-helpers.md](./65-merge-integration-with-shared-helpers.md)
> (общие хелперы клиента — переиспользуем без дублирования).

---

## ✅ Правильная реализация

### 1. Метод обновления в `CrudClient`

```csharp
// Visary.Api.Client/CRUD/CrudClient.cs
public async Task<bool> UpdateSiteEstateClassAsync(
    int siteId, int estateClassId, CancellationToken ct)
{
    // GET текущий site → актуальный RowVersion (long) для optimistic locking.
    // GetCrudByIdAsync<ConstructionSiteFull> — общий helper из VisaryHttpBase
    // (тот же, что и для UpdateSiteFinishingMaterialAsync).
    var current = await GetCrudByIdAsync<ConstructionSiteFull>(
        VisaryMnemonics.Site, siteId, ct);
    if (current is null)
        throw new KeyNotFoundException($"ConstructionSite с ID={siteId} не найден в Visary");

    // PATCH с RowVersion + EstateClass как VisaryRef ({ ID }).
    // forceUpdate=false (под true Visary падает с "Property RowVersion already exists",
    // см. doc 63).
    var body = new
    {
        ID = siteId,
        current.RowVersion,
        EstateClass = new { ID = estateClassId },
    };
    await PatchCrudAsync(
        $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Site}/{siteId}?forceUpdate=false",
        body, $"{VisaryMnemonics.Site}/{siteId}", ct);

    return true;
}
```

`ConstructionSiteFull` уже содержит `VisaryRef? EstateClass` — DTO auto-generated, ничего добавлять не нужно.
`VisaryMnemonics.EstateClass = "estateclass"` уже существовал (использовался для GET и листинга).

### 2. Справочник через общий `ListDictionaryAsync<T>`

`IListViewClient.ListEstateClassesAsync` **уже был** в семействе `List*Async`
(см. [50-visary-api-new-methods.md](./50-visary-api-new-methods.md), [64](./64-dynamic-finishing-material-dictionary.md)) —
ничего нового регистрировать не пришлось:

```csharp
public Task<ListViewResponse<EstateClassRaw>> ListEstateClassesAsync(CancellationToken ct)
    => ListDictionaryAsync<EstateClassRaw>(VisaryMnemonics.EstateClass, null, ct);
```

`EstateClassRaw` лежит в `Dto/Generated/` (auto-generated, поля `ID`, `Title`, `BaseFinishingCost`, `HasLift`, `RowVersion`, …).

### 3. Маппер: обобщили под N параметров

`FinModelImportMapper` теперь обрабатывает и «Тип отделки», и «Класс жилья» через одинаковый flow:

```csharp
private static readonly string[] FinishingTypeAliases =
    ["Тип отделки", "FinishingType", "Finishing"];

private static readonly string[] EstateClassAliases =
    ["Класс жилья", "EstateClass", "Класс недвижимости"]; // ← в Visary это «Класс недвижимости»

public async Task<ValidationResult> ValidateAsync(...)
{
    // 1. Тянем оба справочника один раз на сессию (TryLoadDictionaryAsync helper).
    var finishingByTitle = await TryLoadDictionaryAsync(
        "Тип отделки", _listViewClient.ListFinishingMaterialsAsync,
        m => m.ID, m => m.Title, fileErrors, ct);
    var estateByTitle = await TryLoadDictionaryAsync(
        "Класс недвижимости", _listViewClient.ListEstateClassesAsync,
        m => m.ID, m => m.Title, fileErrors, ct);

    // 2. Pre-flight: ищем ОБЕ целевые колонки на уровне файла.
    //    • Нет ни одной → ОДНА file-level ошибка с алиасами обоих параметров.
    //    • Нет одной → file-level ошибка про конкретно эту колонку (не row-spam).

    // 3. Per-row: ResolveValue(...) выполняет тот же Trim → case-insensitive lookup
    //    для каждого параметра. Ошибки агрегируются в rowErrors.
    //    Строка валидна только если ОБА значения резолвятся.

    // 4. На выходе MappedRow.MappedValues:
    //    { FinishingMaterialId, FinishingMaterialTitle, EstateClassId, EstateClassTitle }
}

public async Task<ApplyResult> ApplyAsync(...)
{
    // KeyValueVertical: все этапы несут одни и те же значения параметров.
    // Берём первую валидную строку, делаем два независимых PATCH'а.
    await _visaryClient.UpdateSiteFinishingMaterialAsync(siteId, finishingMaterialId, ct);
    await _visaryClient.UpdateSiteEstateClassAsync(siteId, estateClassId, ct);
}
```

### ⚠️ Важно

- **Названия в шаблоне ≠ названия в Visary.** В Excel-шаблоне колонка называется
  «Класс жилья», а сущность Visary — `EstateClass` («Класс недвижимости»). Алиасы маппера
  поддерживают и то, и другое — пользователь не должен это знать.
- **Оба справочника обязательны.** Если хотя бы один не загрузился — `dictionary_unavailable`,
  импорт останавливается. Никаких хардкод-фолбэков (см. [64](./64-dynamic-finishing-material-dictionary.md)).
- **Обе колонки обязательны в файле.** Если одной нет — file-level `column_not_found`
  именно про неё. Если нет ни одной — одна общая ошибка («не тот шаблон»), не две.
- **Apply делает два независимых PATCH'а.** Если первый успешен, а второй упал — site
  останется в смешанном состоянии (FinishingMaterial обновлён, EstateClass нет).
  Транзакционности на стороне Visary CRUD нет; принимаем риск, ловим в логах.
- **Lookup case-insensitive** для обоих параметров (`StringComparer.OrdinalIgnoreCase`):
  «Премиум», «премиум», «ПРЕМИУМ» из Excel дают одинаковый ID.
- **Строка валидна, только если оба значения разрешились.** Если резолв одного
  не удался — `IsValid = false`, в `Errors` обе ошибки (если обе не разрешились).
- **`MappedValues` всегда содержит обе пары полей** для валидной строки. ApplyAsync
  читает их через `GetProperty(...)` без проверок — для невалидных строк `ApplyAsync`
  не вызывается.

---

## ❌ Типичные ошибки

### 1. Использовать `Title` из шаблона как имя поля Visary

```csharp
// ❌ В Visary НЕТ поля «Класс жилья» — там EstateClass
var body = new { ID = siteId, RowVersion = ..., HouseClass = new { ID = ... } };
// → Visary молча игнорирует unknown property, поле не обновляется.
```

Имена полей Visary живут в `ConstructionSiteFull` (auto-generated DTO) — открой и посмотри,
не «по аналогии».

### 2. Хардкод соответствия Title → ID

```csharp
// ❌ Title→ID может меняться между средами (test/prod), новые классы в справочнике игнорируются.
private static int? GetEstateClassId(string title) => title switch
{
    "Премиум" => 12,
    "Стандарт" => 7,
    _ => null,
};
```

См. [64](./64-dynamic-finishing-material-dictionary.md) — тянем из Visary живьём.

### 3. Один общий `RowVersion` на оба обновления

```csharp
// ❌ После первого PATCH'а RowVersion на сервере увеличится — второй PATCH с тем же
//    RowVersion получит 409 Conflict.
var current = await GetCrudByIdAsync<ConstructionSiteFull>(...);
var rowVersion = current.RowVersion;

await PatchAsync(new { ID, RowVersion = rowVersion, FinishingMaterial = ... });
await PatchAsync(new { ID, RowVersion = rowVersion, EstateClass = ... }); // 409!
```

Поэтому каждый `UpdateSiteXAsync` сам делает свой GET — `RowVersion` всегда свежий.
Альтернатива — один PATCH с обоими полями сразу, но тогда теряется атомарность ошибок
(непонятно, какое именно поле не прошло валидацию на сервере).

### 4. Дублировать `TryLoadDictionaryAsync` для каждого нового параметра

```csharp
// ❌ Копипаст: для каждого справочника свой try/catch с одинаковой логикой.
try { var fm = await ListFinishingMaterialsAsync(ct); ... } catch { ... }
try { var ec = await ListEstateClassesAsync(ct); ... } catch { ... }
```

Helper `TryLoadDictionaryAsync<T>(humanName, loader, idSelector, titleSelector, ...)` —
один параметризованный метод, добавление третьего справочника = одна строка вызова.

---

## 📍 Применение в проекте

| Компонент | Файл | Что добавлено / изменено |
|-----------|------|--------------------------|
| `UpdateSiteEstateClassAsync` | [Visary.Api.Client/CRUD/CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | Новый метод по аналогии с `UpdateSiteFinishingMaterialAsync` |
| Контракт клиента | [там же, ICrudClient](../Visary.Api.Client/CRUD/CrudClient.cs) | `Task<bool> UpdateSiteEstateClassAsync(int siteId, int estateClassId, CancellationToken)` |
| `EstateClassAliases` | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `["Класс жилья", "EstateClass", "Класс недвижимости"]` |
| `TryLoadDictionaryAsync<T>` | там же (private) | Helper загрузки справочника, переиспользуется для FinishingMaterial и EstateClass |
| `ResolveValue` | там же (private) | Параметризованный per-row lookup значения по словарю |
| `BuildColumnNotFoundError` | там же (private) | Параметризованный билд file-level ошибки про отсутствующую колонку |
| `ApplyAsync` | там же | Два последовательных вызова: `UpdateSiteFinishingMaterialAsync` + `UpdateSiteEstateClassAsync` |
| Тесты | [FinModelImportMapperTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelImportMapperTests.cs) | 26 тестов: маппинг по обоим параметрам, алиасы, пустые/невалидные значения, отсутствующие колонки, недоступный справочник, end-to-end Apply |
| `appsettings.Local.json` | `KiloImportService.Api/` (gitignored) | Свежий Bearer-токен Visary (см. [54](./54-visary-token-hot-reload.md)) |

### Что **не** трогали (потому что уже было)

- `EstateClassRaw` — auto-generated в `Dto/Generated/` (использовался GET-методами).
- `IListViewClient.ListEstateClassesAsync` — был добавлен в [50](./50-visary-api-new-methods.md).
- `VisaryMnemonics.EstateClass` — константа `"estateclass"` была.
- `ConstructionSiteFull.EstateClass: VisaryRef?` — auto-generated DTO уже содержит поле.

---

## 🎯 Чек-лист (при добавлении нового параметра в Финмодель)

- [ ] DTO справочника есть в `Visary.Api.Client/Dto/Generated/` (auto-generated). Если нет — `scripts/generate-visary-dtos.ps1`.
- [ ] Метод `ListXAsync` есть в `IListViewClient`. Если нет — добавить one-liner через `ListDictionaryAsync<T>(VisaryMnemonics.X, null, ct)`.
- [ ] `ConstructionSiteFull` (или другой `*Full` DTO) содержит нужное поле как `VisaryRef?` / правильный тип.
- [ ] `UpdateSiteXAsync` в `CrudClient` по шаблону: GET → PATCH с RowVersion + `X = new { ID }`. forceUpdate=**false**.
- [ ] В мапперe: алиасы (`["Русское имя из шаблона", "EnglishName", "Visary-имя"]`), вызов `TryLoadDictionaryAsync` + `FindColumn` + `ResolveValue`.
- [ ] Pre-flight — все целевые колонки проверяются ОДИН раз на файл. Нет ни одной → одна общая ошибка. Нет одной → именно про неё.
- [ ] `ApplyAsync` — отдельный PATCH на каждое поле. Каждый делает свой GET (для свежего RowVersion).
- [ ] Тест: успешный маппинг, алиасы (case-insensitive), пустое значение, неизвестное значение, отсутствующая колонка, недоступный справочник, end-to-end Apply (`Verify(...)`).

---

## 🧪 Связанный паттерн: рост шаблона = рост N параметров

Шаблон «Финмодель» — открытый key-value: завтра добавится «Тип фундамента»,
«Этажность», «Подземная парковка». Каждый — это:

1. Запись в `*Aliases`.
2. Один вызов `TryLoadDictionaryAsync` (если справочник).
3. Один вызов `ResolveValue` в row-loop.
4. Одно поле в `MappedValues` JSON.
5. Один `UpdateSiteXAsync` в `ApplyAsync`.

Дублирование на уровне «5 строк на параметр» — приемлемо. Когда параметров станет
8-10 — стоит вынести в декларативный список `ParameterMapping[]` и итерировать.
Сейчас (2 параметра) — преждевременная абстракция.

---

**Версия**: 1.0
**Дата**: 2026-05-06
