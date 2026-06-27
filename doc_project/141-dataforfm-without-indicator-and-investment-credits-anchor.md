# 🧾 Финмодель → dataforfm без Indicator + якорь «Инвестиционные кредиты»

## 📋 Описание

Доработка doc 139 по двум пунктам заказчика:

1. **`dataforfm` создаётся БЕЗ поля `Indicator`** — Visary рассчитает его сам по
   видам помещений. Лист **Outputs → «Площадь реализации»** для этого шага
   больше не открывается. Связка `dataforfm ↔ RoomKind` остаётся: каждому
   включённому в блоке «Продажи» виду помещения соответствует одна `dataforfm`
   с `RoomKind` + `Title`.

2. **Парсер процентных ставок якорится на подразделе «Инвестиционные кредиты»**
   (внутри раздела «Финансирование» листа Control). Первичный якорь —
   `«Инвестиционные кредиты»`; fallback — `«Финансирование»` на случай файлов,
   где подраздел отсутствует.

| # | Что изменилось | Где |
|---|----------------|-----|
| 1 | `DataForFmCreateRequest.Indicator` удалён | `Visary.Api.Client/Dto/VisaryCrudRequests.cs` |
| 2 | `DataForFmPatchRequest` + `PatchDataForFmAsync` удалены (PATCH был нужен только для апдейта Indicator) | `Visary.Api.Client/...` |
| 3 | Лист Outputs «Площадь реализации» больше не парсится; парсер `ReadSalesAreasData`/`SalesAreasData` удалён | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.Installments.cs` |
| 4 | Если `dataforfm` уже есть в Visary — POST не делается (skip с info-emit), 422 на CREATE тоже трактуется как «уже есть, пропуск» | там же, `EnsureProjectAuditAndInstallmentsAsync` |
| 5 | Парсер ставок: первичный якорь — `«Инвестиционные кредиты»`, fallback — `«Финансирование»` | `ReadFinancingRates` |
| 6 | Тестовая фикстура добавляет `«Инвестиционные кредиты»` перед родительскими строками LM10..LM40 | `KiloImportService.Api.Tests/Mapping/FinModelInstallmentsTests.cs` |

---

## ✅ Правильная реализация

### POST `/crud/dataforfm` — без Indicator

```json
{
  "DataSetForFMID": 8030,
  "DataSetForFM": { "ID": 8030 },
  "Title": "Данные по Квартирам",
  "RoomKind": { "Title": "Квартира", "ID": 3 }
}
```

`Title` — по словарю дательного падежа («Данные по Квартирам»/«Машиноместам»/…),
fallback `«Данные по {RoomKind.Title}»`. `Indicator` Visary считает сам по
правилам сущности — импортер его НЕ выставляет.

### Оркестратор: pre-check → POST или skip

```csharp
// 6) Pre-check существующих dataforfm: RoomKindId → existingDataForFmId.
var existingDataForFmIdByKindId = new Dictionary<int, int>();
try
{
    var resp = await _listViewClient.GetDataForFmByDataSetAsync(dataSetId, ct);
    foreach (var d in resp.Data ?? new List<DataForFmRaw>())
        if (d.RoomKind?.ID is { } kindId && kindId > 0 && d.ID > 0)
            existingDataForFmIdByKindId[kindId] = d.ID;
}
catch { /* листинг упал — продолжаем без pre-check */ }

// 8) Для каждого RoomKind: skip если есть, POST если нет.
foreach (var (kindId, kindTitle) in roomKindsToCreate)
{
    if (existingDataForFmIdByKindId.TryGetValue(kindId, out var existingId))
    {
        synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
            [$"Данные для ФМ [{kindTitle}]: уже существует (id={existingId}) — пропуск"]);
        continue;
    }
    try
    {
        var created = await _visaryClient.CreateDataForFmAsync(new DataForFmCreateRequest
        {
            DataSetForFMID = dataSetId,
            DataSetForFM = new VisaryRef { ID = dataSetId },
            Title = BuildDataForFmTitle(kindTitle),
            RoomKind = new VisaryRef { ID = kindId, Title = kindTitle },
        }, ct);
        existingDataForFmIdByKindId[kindId] = created.ID;
    }
    catch (Exception ex) when (IsDuplicateDataForFmConflict(ex))
    {
        // 422 на UX_DataForFM_DataSetForFMID_RoomKindID — pre-check
        // не нашёл (variant-поле / транспорт-fail), но запись есть.
        // Indicator не апдейтим, поэтому просто пропускаем.
    }
}
```

### Парсер ставок: первичный якорь «Инвестиционные кредиты»

```csharp
private const string InvestmentCreditsBlockMarker = "Инвестиционные кредиты";
private const string FinancingBlockMarker         = "Финансирование";

// ...

var anchorRow = FindAnyColumnRowContains(sheet,
    search: InvestmentCreditsBlockMarker,
    startRow: 1, endRow: 500, firstCol: 1, lastCol: 6);
if (anchorRow < 0)
{
    // Fallback на родительский раздел, если в файле нет подраздела.
    anchorRow = FindAnyColumnRowContains(sheet,
        search: FinancingBlockMarker,
        startRow: 1, endRow: 500, firstCol: 1, lastCol: 6);
}
if (anchorRow < 0) return Array.Empty<EnabledFinancingRate>();
```

Под этим якорем парсер ищет родительские строки LM10..LM40 (см. doc 139 v1.4.1) —
«Базовая %% ставка», «Капитализация / отсрочка уплаты %%», «Базовая процентная
ставка по капитализированным %%», «Комиссия за отсрочку %%».

---

## 📍 Источник правды (скриншот заказчика)

Раздел листа Control:

```
┌── Инвестиционные кредиты ────────────────────── Кредитная линия Этап 1 ──┐
│   Базовая %% ставка                                                       │
│      Фиксированная ставка (сценарий 1)              2 - Премия к КС РФ    │
│      Премия к КС РФ (фикс) (сценарий 2)             5,0%                  │
│   Спец. процентная ставка                           5,0%                  │
│   Коэф покрытия эскроу/долг ...                     1,3                   │
│   Капитализация / отсрочка уплаты %%                3 - Отсрочка ...      │
│      Ручной ввод периода отсрочки, кварталы                               │
│      Доля капитализации/отсрочки процентов          100,0%                │
│   Выбор ставки для капитализации процентов          1 - Средневзвешенная  │
│   Базовая процентная ставка по капитализированным %%  2 - Премия к КС РФ │
│      Фиксированная ставка (сценарии 1-2)                                  │
│      Премия к КС РФ (сценарии 1-2)                                        │
│   Комиссия за отсрочку %% (сценарии 3)                                    │
│   Опцион                                            0 - Нет               │
└──────────────────────────────────────────────────────────────────────────┘
```

Парсер находит подзаголовок **«Инвестиционные кредиты»** и далее сканирует
строки в этом подразделе на родителей LM10/20/30/40, обрабатывая sub-строки
сценариев как в doc 139 v1.4.1.

---

## ⚠️ Важно

1. **Indicator больше не вычисляется**. Если заказчик попросит вернуть — в
   `DataForFmCreateRequest` добавляется поле `int Indicator`, в оркестраторе
   возвращается чтение `Outputs → Площадь реализации` (см. git history doc 139 v1.0–v1.2).

2. **Pre-check используется для skip, а не для PATCH**. Раньше pre-check был
   нужен, чтобы PATCH-нуть Indicator существующей строки. Сейчас pre-check
   только защищает от 422 при POST дубликата — если запись есть, мы её не
   трогаем.

3. **422 не считается ошибкой**. Если pre-check listview упал, но POST упёрся
   в `UX_DataForFM_DataSetForFMID_RoomKindID` — это значит «уже есть, OK»,
   эмитим info, не валим импорт.

4. **Якорь подраздела важнее якоря раздела**. Если в файле есть оба
   («Финансирование» и «Инвестиционные кредиты»), используем второй. Это
   защищает от случая, когда в «Финансирование» есть другая таблица (например,
   «Проектное финансирование»), а нужные ставки лежат именно в
   «Инвестиционные кредиты».

5. **Шапка этапов всё ещё ищется глобально** (`FindStageHeaderRow`, поиск
   точного `«Этап 1»` в колонках 3..7 первых ~60 строк). Столбец
   `«Кредитная линия Этап 1»` на скриншоте — лейбл колонки внутри подраздела;
   реально значения по-прежнему попадают в глобальный столбец Этап 1.

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО — заполнять Indicator из Outputs.
var area = areas.AreaByRoomTypeLabel.TryGetValue(label, out var v) ? v : 0d;
await _visaryClient.CreateDataForFmAsync(new DataForFmCreateRequest
{
    Indicator = (int)Math.Round(area), // 💥 заказчик: Indicator не заполнять
    ...
});
```

```csharp
// НЕПРАВИЛЬНО — PATCH существующего dataforfm для апдейта Indicator.
await _visaryClient.PatchDataForFmAsync(existingId, new DataForFmPatchRequest
{
    Indicator = newIndicator, // 💥 поля нет в DTO, метод удалён
});
// Правильно — pre-check нашёл → skip + info-emit:
synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
    [$"Данные для ФМ [{kindTitle}]: уже существует (id={existingId}) — пропуск"]);
```

```csharp
// НЕПРАВИЛЬНО — якорить ставки только на «Финансирование».
var financingRow = FindAnyColumnRowContains(sheet, search: "Финансирование", ...);
// 💥 в реальном файле «Финансирование» — заголовок главы, а ставки лежат
//    в подразделе «Инвестиционные кредиты» где-то ниже.
// Правильно — приоритет на «Инвестиционные кредиты», fallback на
// «Финансирование» (см. ReadFinancingRates).
```

---

## 📍 Применение в проекте

| Слой | Файл | Метод/символ |
|------|------|--------------|
| DTO | `Visary.Api.Client/Dto/VisaryCrudRequests.cs` | `DataForFmCreateRequest` (без `Indicator`); `DataForFmPatchRequest` — **удалён** |
| CRUD | `Visary.Api.Client/CRUD/CrudClient.cs` | `CreateDataForFmAsync` (без Indicator-лога); `PatchDataForFmAsync` — **удалён** |
| Мапер (orchestrator) | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.Installments.cs` | `EnsureProjectAuditAndInstallmentsAsync` — больше не открывает Outputs, для существующих `dataforfm` делает skip |
| Парсер ставок | там же | `ReadFinancingRates` — двух-уровневый якорь («Инвестиционные кредиты» → «Финансирование») |
| Константы | там же | `InvestmentCreditsBlockMarker = "Инвестиционные кредиты"` |
| Удалённые helpers | там же | `ReadSalesAreasData`, `SalesAreasData`, `ReadSalesAreasFromSheet`, `OutputsRoomTypeToKindTitle`, `RefetchDataForFmIdAsync`, `FindRowByCellContains`, `TryReadNumberCell` |
| Тесты | `KiloImportService.Api.Tests/Mapping/FinModelInstallmentsTests.cs` | Удалены `ReadSalesAreas_*` (2 шт.) и `BuildReferenceOutputsSheet`; в `BuildReferenceFinancingControlSheet` добавлена строка `«Инвестиционные кредиты»` под `«Финансирование»` |

---

## 🎯 Чек-лист

- [ ] POST `/crud/dataforfm` НЕ содержит `Indicator` в теле.
- [ ] Существующая в Visary строка `dataforfm` под (DataSet, RoomKind) — skip
      с info-emit «уже существует, пропуск».
- [ ] 422 на CREATE → skip (info), не error.
- [ ] Парсер ставок предпочитает якорь `«Инвестиционные кредиты»`, fallback
      на `«Финансирование»`.
- [ ] Все 49 тестов `FinModelInstallmentsTests` — зелёные.
- [ ] Полный suite (без VisaryLive) — 453/453.
- [ ] Лист Outputs больше не открывается импортером Финмодели для шага
      Заключения (Outputs `Fact`-блок Plan-каскада по doc 126 — не затронут).

---

## 📅 История изменений

- **v1.0 (2026-06-19)** — первая реализация. `Indicator` убран из всего
  потока создания `dataforfm`; `dataforfm` для существующих RoomKind не
  обновляется (skip с info). Парсер ставок добавил первичный якорь
  «Инвестиционные кредиты» с fallback на «Финансирование». Удалены неиспользуемые
  ныне `ReadSalesAreas*`/`DataForFmPatchRequest`/`PatchDataForFmAsync`/
  `RefetchDataForFmIdAsync`/`FindRowByCellContains`/`TryReadNumberCell`/
  `OutputsRoomTypeToKindTitle`. Тесты обновлены под новые якоря, ReadSalesAreas-тесты
  удалены.

## 🔗 Связанная документация

- [doc 139 — Заключение БП7 + рассрочки + ставки](./139-finmodel-installments-and-conclusion.md) —
  базовая doc, которую дорабатывает 141.
- [doc 126 — fact-inputdata-from-outputs](./126-finmodel-fact-inputdata-from-outputs.md) —
  Plan-каскад Outputs, не затронут (другой шаг Финмодели).
- [doc 105 — control-value-ref](./105-control-value-ref.md) — KV-парсер хинт
  «значение с управляющего листа», используется для «Номер КД».
