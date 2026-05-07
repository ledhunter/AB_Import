# 🏠 Финмодель: параметр «Строительный адрес»

## 📋 Описание

**Статус**: ✅ Реализовано (v1)
**Дата**: 2026-05-07
**Маппер**: `FinModelImportMapper` (код типа `"finmodel"`)

В импорт «Финмодель» добавлен третий простой параметр объекта строительства —
**строительный адрес** (`Address` в Visary, строковый атрибут `ConstructionSite`).

В отличие от `FinishingMaterial` / `EstateClass` (FK → справочник), `Address` — это
**свободная строка**, поэтому реализация **проще**: нет шага `TryLoadDictionaryAsync`,
нет резолва `Title → ID`, валидация ограничивается «не пусто».

---

## 🏗️ Поток обработки

```
Лист Inputs (вертикальный key-value)
  ┌──────────────────────────┬────────────┐
  │ Строительный адрес       │ ул. ...    │   ← KeyColumn=C, ValueColumn=H+
  └──────────────────────────┴────────────┘
              │
   ValidateAsync (per-row):
              │
   ReadCellTrimmed(AddressAliases)  ← без справочника
              │
   MappedRow.Address = "ул. ..."
              │
   ApplyAsync:
              │
   ICrudClient.UpdateSiteAddressAsync(siteId, address, ct)
              │
   PATCH /api/visary/crud/constructionsite/{id}?forceUpdate=false
   body: { ID, RowVersion, Address: "ул. ..." }
```

---

## ✅ Правильная реализация

### Алиасы колонок (вертикальный layout)

```csharp
// FinModelImportMapper.cs
private static readonly string[] AddressAliases =
    ["Строительный адрес", "Address", "Адрес"];
```

### Чтение (без справочника — просто строка)

```csharp
// FinModelImportMapper.ValidateAsync
var addressValue = ReadCellTrimmed(
    row, fileAddressCol!, AddressAliases, "Строительный адрес", rowErrors);

// ↑ ReadCellTrimmed возвращает trimmed-значение или null
//   и при null уже добавила row-error "value_empty" в rowErrors.
//   Никакого ResolveDictionaryValue / TryLoadDictionaryAsync.
```

### Сохранение в MappedRow JSON

```csharp
var mappedJson = JsonSerializer.Serialize(new
{
    FinishingMaterialId    = finishingEntry!.Value.Id,
    FinishingMaterialTitle = finishingEntry.Value.Title,
    EstateClassId          = estateEntry!.Value.Id,
    EstateClassTitle       = estateEntry.Value.Title,
    Address                = addressValue,    // 👈 строка, не FK
    Indicators             = indicatorValues,
});
```

### Применение в ApplyAsync

```csharp
var address = root.TryGetProperty("Address", out var addrEl)
              && addrEl.ValueKind == JsonValueKind.String
              ? addrEl.GetString()
              : null;

await _visaryClient.UpdateSiteFinishingMaterialAsync(siteId, finishingMaterialId, ct);
await _visaryClient.UpdateSiteEstateClassAsync(siteId, estateClassId, ct);
if (!string.IsNullOrWhiteSpace(address))
    await _visaryClient.UpdateSiteAddressAsync(siteId, address, ct);
//  ↑ namespacing: пустую строку не пушим в Visary, чтобы не затирать существующий
//    Address у объекта (Validate уже отметил такую строку как value_empty → IsValid=false,
//    но защита от регрессии "пустая строка стала валидной" не помешает).
```

### CRUD-метод клиента

```csharp
// Visary.Api.Client/CRUD/CrudClient.cs
public async Task<bool> UpdateSiteAddressAsync(
    int siteId, string address, CancellationToken ct)
{
    var current = await GetCrudByIdAsync<ConstructionSiteFull>(
        VisaryMnemonics.Site, siteId, ct);
    if (current is null)
        throw new KeyNotFoundException($"ConstructionSite с ID={siteId} не найден в Visary");

    var body = new
    {
        ID = siteId,
        current.RowVersion,
        Address = address,        // 👈 простая строка, не VisaryRef { ID = ... }
    };
    await PatchCrudAsync(
        $"{BaseUrl}/api/visary/crud/{VisaryMnemonics.Site}/{siteId}?forceUpdate=false",
        body, $"{VisaryMnemonics.Site}/{siteId}", ct);
    _log.LogInformation("CrudClient.UpdateSiteAddressAsync: siteId={SiteId} success", siteId);
    return true;
}
```

### ⚠️ Важно

- **`Address` — НЕ `VisaryRef`.** В body передаётся обычная строка. Обёртка
  `new { ID = ... }` нужна только для FK-полей (FinishingMaterial, EstateClass).
- Для FK-обновления использовали бы `body = new { ID, RowVersion, Address = new { ID = ... } }` — это **типичная ошибка**, которая привела бы к 400/500 от Visary.
- Address уже **есть в `ConstructionSiteFull`** как `JsonElement?` (Visary возвращает
  его как строку или null). Обновлять можно через PATCH с anonymous body (как сделано),
  без необходимости расширять `SitePatchRequest` (можно при желании — это был бы
  альтернативный путь через `PatchSiteAsync`).

---

## ❌ Типичные ошибки

### Ошибка 1: обернуть строку в VisaryRef

```csharp
// НЕПРАВИЛЬНО — Address это строка, а не FK
var body = new
{
    ID = siteId,
    current.RowVersion,
    Address = new { ID = "ул. Ленина" }, // ← 400 Bad Request от Visary
};
```

### Ошибка 2: пытаться загрузить «справочник адресов»

`Address` свободный текст — никаких `listview/address`. Не надо городить
`TryLoadDictionaryAsync` или `ResolveDictionaryValue`. Достаточно `ReadCellTrimmed`.

### Ошибка 3: пушить пустую строку в Visary

```csharp
// НЕПРАВИЛЬНО — затирает поле у объекта
if (address is not null)
    await _visaryClient.UpdateSiteAddressAsync(siteId, address, ct);
```

```csharp
// ПРАВИЛЬНО — фильтр по `IsNullOrWhiteSpace`, не по null
if (!string.IsNullOrWhiteSpace(address))
    await _visaryClient.UpdateSiteAddressAsync(siteId, address, ct);
```

---

## 📍 Применение в проекте

| Артефакт | Файл | Ключевые места |
|----------|------|----------------|
| Алиасы + чтение | [FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | `AddressAliases`, `ValidateAsync`, `ApplyAsync` |
| CRUD клиент | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | `UpdateSiteAddressAsync` |
| Интерфейс | [CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | `ICrudClient.UpdateSiteAddressAsync` |
| Тесты | [FinModelImportMapperTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelImportMapperTests.cs) | 7 новых тестов (алиасы, маппинг, пустое значение, отсутствие колонки, Apply) |

---

## 🧪 Тесты

| Тест | Что проверяет |
|------|---------------|
| `ValidateAsync_Address_StoredAsString` | значение из колонки «Строительный адрес» попадает в `MappedRow.Address` как строка |
| `ValidateAsync_AddressColumnAliases_WorkCaseInsensitive` | алиасы `Address` / `Адрес` / case-insensitive `строительный адрес` |
| `ValidateAsync_MissingAddressColumn_ReturnsFileLevelError` | если колонки нет → `column_not_found` на файле, строки не маппятся |
| `ValidateAsync_EmptyAddressValue_ReturnsRowError` | пустая ячейка → row-error `value_empty`, `IsValid=false` |
| `ApplyAsync_Address_CallsUpdateSiteAddressAsync` | `Apply` вызывает `UpdateSiteAddressAsync(siteId, "г. Уфа, ...")` ровно один раз |
| `ApplyAsync_ValidRow_CallsAllUpdates` (расширен) | теперь проверяет, что Address-update вызывается рядом с FinishingMaterial и EstateClass |

Все 48 тестов FinModel-маппера проходят (`dotnet test --filter FinModelImportMapperTests`).

---

## 🎯 Чек-лист добавления нового **строкового** параметра в Финмодель

(шаблон Address — для FK-параметров см. doc 66 EstateClass; для показателей — doc 67)

- [ ] Добавить `XxxAliases` в `FinModelImportMapper.cs`
- [ ] В summary-комментарии класса пополнить список «Поддерживаемые параметры»
- [ ] В `ValidateAsync`:
  - [ ] `FindColumn(allColumns, XxxAliases)` → переменная `fileXxxCol`
  - [ ] добавить в `anyFound` проверку
  - [ ] добавить в общий `allAliases` для «не нашлось ничего»
  - [ ] добавить отдельную file-level ошибку, если колонка отсутствует
  - [ ] `ReadCellTrimmed(row, fileXxxCol!, XxxAliases, "...", rowErrors)`
  - [ ] Включить значение в JSON `MappedRow`
- [ ] В `ApplyAsync`:
  - [ ] прочитать из JSON через `TryGetProperty`
  - [ ] вызвать `UpdateSiteXxxAsync(siteId, value, ct)` под `if (!string.IsNullOrWhiteSpace(...))`
  - [ ] добавить в `LogInformation`
- [ ] В `ICrudClient` + `CrudClient`:
  - [ ] метод `UpdateSiteXxxAsync(int siteId, T value, CancellationToken ct)`
  - [ ] GET site (RowVersion) → PATCH `/crud/site/{id}?forceUpdate=false`
- [ ] В тестах:
  - [ ] обновить `Row()`-helper (новый параметр с дефолтом)
  - [ ] обновить existing 4 alias-тесты (Finishing/Estate/BuildingArea/BuildingDensity) — добавить `["Строительный адрес"]` (или эквивалент) в их dictionaries
  - [ ] добавить mock для `UpdateSiteXxxAsync` в конструктор
  - [ ] +5 новых тестов: маппинг, алиасы, отсутствие колонки, пустое значение, Apply
- [ ] Зарегистрировать документ в `doc_project/README.md`
