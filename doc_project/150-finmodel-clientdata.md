# 👥 Финмодель → «Данные клиента» (`clientdata`) поквартально на (Site × RoomKind)

## 📋 Описание

Расширение каскада импорта Финмодели ([doc 110](./110-finmodel-plan-and-fmmodel.md) +
[doc 112](./112-finmodel-version-and-inputdata.md)): после Plan-точек `inputdata`
и независимо от `fmmodelversion` маппер дополнительно создаёт записи **«Данные клиента»**
(`clientdata`) — поквартальный срез стоимости 1 кв.м (`Cost`) и площади 1 кв.м (`Rates`)
для одного вида помещения на объекте строительства.

| Что | Откуда берётся | Что попадает в Visary |
|-----|----------------|-----------------------|
| Cost / `{Prefix}`Cost | Plan-парсер: «Стоимость 1 кв.м» (Cost-строка) | `clientdata.Cost`, `{Prefix}Cost` |
| Rates / `{Prefix}`Rates | Plan-парсер: «Площадь 1 кв.м» / «Колич-во м/м» (Amount-строка) | `clientdata.Rates`, `{Prefix}Rates` |
| RoomKind | Маппинг FmCode → каноничный Title в `roomkind`-справочнике | `clientdata.RoomKind = { ID, Title }` |
| RoomCategory | Справочник Visary (0/1/2/3) | `clientdata.RoomCategory` |
| Site | siteId + Title (через `listview/constructionsite`) | `clientdata.Site = { ID, Title }` |
| PeriodStartDate | Первый день квартала (`{Year}Q{N}` → `yyyy-MM-01`) | `clientdata.PeriodStartDate` |
| Date | Первый день СЛЕДУЮЩЕГО квартала | `clientdata.Date` |

Каскад **независим от `fmmodelversion`**: ClientData — самостоятельная сущность Visary,
не привязывается ни к версии, ни к fmcode-справочнику. Используются те же квартальные
ячейки, что и для Plan-точек `inputdata` — это означает, что парсер расширять не нужно,
маппер переиспользует `FinModelPlanData.InputDataPoints`.

---

## ✅ Правильная реализация

### Маппинг FmCode → ClientData-полей

```csharp
private static readonly IReadOnlyDictionary<string, ClientDataKindBinding> ClientDataKindByFmCode =
    new Dictionary<string, ClientDataKindBinding>(StringComparer.OrdinalIgnoreCase)
    {
        [FmCodeApartment]      = new("Квартира",          RoomCategory: 0, Prefix: "Residential"),
        [FmCodeNonResidential] = new("Нежилое помещение", RoomCategory: 1, Prefix: "Nonresidential"),
        [FmCodeStoreroom]      = new("Кладовая",          RoomCategory: 3, Prefix: "Othernonresidential"),
        [FmCodeParking]        = new("Машиноместо",       RoomCategory: 2, Prefix: "Parking"),
    };
```

Соответствие требованиям заказчика:

| FmCode | Plan-категория | RoomKind Title | RoomCategory | Префикс полей |
|--------|----------------|----------------|--------------|---------------|
| 010 | Продажа квартиры | «Квартира» | 0 (Residential) | `Residential` |
| 020 | Продажа нежилые (ком) ПСН | «Нежилое помещение» | 1 (NonResidential) | `Nonresidential` |
| 030 | Продажа иные нежилые (кладовки) | «Кладовая» | 3 (OtherNonResidential) | `Othernonresidential` |
| 040 | Продажа м/м | «Машиноместо» | 2 (ParkingPlace) | `Parking` |

Все остальные fmCode (`060` Апартаменты, Fact-коды `011/021/...`, Equity-fmcode `604`) —
**пропускаются**: у заказчика для них нет соответствующих полей в payload ClientData.

### Visary client

[VisaryMnemonics.cs](../Visary.Api.Client/Common/VisaryMnemonics.cs):
```csharp
public const string ClientData = "clientdata";
```

[VisaryEntities.cs](../Visary.Api.Client/Dto/VisaryEntities.cs) — DTO `ClientDataRaw`
(nullable-поля для ответа Visary).

[VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs):
```csharp
public sealed class ClientDataCreateRequest
{
    public double Cost { get; set; }
    public double Rates { get; set; }
    public int RoomCategory { get; set; }
    public VisaryRef RoomKind { get; set; } = null!;
    public double ODCountParking { get; set; }
    public double ODCountOtherNonRes { get; set; }
    public double ODCountNonRes { get; set; }
    public double ODCount { get; set; }
    public double ODCountRes { get; set; }
    public VisaryRef Site { get; set; } = null!;
    public string Date { get; set; } = null!;
    public double ParkingCost { get; set; }
    public double ParkingRates { get; set; }
    public double OtherNonresidentialCost { get; set; }
    public double OthernonresidentialRates { get; set; }
    public double NonresidentialCost { get; set; }
    public double NonresidentialRates { get; set; }
    public double ResidentialCost { get; set; }
    public double ResidentialRates { get; set; }
    public string PeriodStartDate { get; set; } = null!;
}
```

[CrudClient](../Visary.Api.Client/CRUD/CrudClient.cs):
- `CreateClientDataAsync` — POST `/crud/clientdata`. Идемпотентности на сервере нет;
  для импорта Финмодели pre-check не нужен (см. ниже §7 «Идемпотентность»).

### Маппер ([FinModelImportMapper](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs))

`EnsureClientDataAsync(projectId, siteId, planData, errors, synthetic, ct)` вызывается
из `EnsureFmModelAsync` в самом конце (после Plan + Equity + Fact):

1. **Фильтр поддерживаемых точек** — Plan-points с `fmCode ∈ {010/020/030/040}` И с
   ненулевым Cost ИЛИ Amount. Точки только-Summ (план продаж без unit-цен) пропускаются
   как бессмысленные для ClientData.
2. **Резолв Title объекта** — `listview/constructionsite` (через `GetSiteByProjectAndIdAsync`).
   - Ошибка / null → фолбэк `Site.Title = "Объект #{siteId}"`. Не блокирует импорт.
3. **Резолв RoomKind-словаря** — `listview/roomkind` (через `ListRoomKindsAsync`).
   - Ошибка → row-error `clientdata_roomkind_unavailable` + skip ВСЕХ ClientData
     (Plan/Equity/Fact-точки не страдают).
4. **Цикл по точкам**:
   - RoomKind не найден в словаре (по каноничному Title) → копим в `missingKindTitles`,
     в конце — row-error `clientdata_roomkind_not_found` со списком.
   - `TryConvertFmPeriodToDates` упал на FmPeriod → точка пропущена с warning-логом
     (защита от мусорных кодов; в норме невозможна, парсер генерирует строго `{Y}Q{N}`).
   - `BuildClientDataRequest` собирает payload → POST. На ошибку — `failedCount++`,
     в конце один row-error `clientdata_create_failed`.

```csharp
internal static ClientDataCreateRequest BuildClientDataRequest(
    VisaryRef siteRef, RoomKindRaw roomKind, ClientDataKindBinding binding,
    FinModelPlanInputDataPoint point, DateTime periodStart, DateTime nextPeriodStart)
{
    var req = new ClientDataCreateRequest
    {
        Cost = point.Cost,
        Rates = point.Amount,
        RoomCategory = binding.RoomCategory,
        RoomKind = new VisaryRef { ID = roomKind.ID, Title = roomKind.Title },
        Site = siteRef,
        PeriodStartDate = periodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Date = nextPeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        // Все остальные prefixed-поля и ODCount* = 0 (заполняем через switch).
    };
    switch (binding.Prefix)
    {
        case "Residential":         req.ResidentialCost = point.Cost; req.ResidentialRates = point.Amount; break;
        case "Nonresidential":      req.NonresidentialCost = point.Cost; req.NonresidentialRates = point.Amount; break;
        case "Othernonresidential": req.OtherNonresidentialCost = point.Cost; req.OthernonresidentialRates = point.Amount; break;
        case "Parking":             req.ParkingCost = point.Cost; req.ParkingRates = point.Amount; break;
    }
    return req;
}
```

### TryConvertFmPeriodToDates — даты от FmPeriod

```csharp
internal static bool TryConvertFmPeriodToDates(
    string fmPeriod, out DateTime periodStart, out DateTime nextPeriodStart)
{
    // Regex ^(\d{4})Q([1-4])$
    var startMonth = (quarter - 1) * 3 + 1;
    periodStart = new DateTime(year, startMonth, 1);
    nextPeriodStart = periodStart.AddMonths(3);  // переход 2026Q4 → 2027Q1 корректен
    return true;
}
```

Примеры:
- `2026Q1` → `PeriodStartDate="2026-01-01"`, `Date="2026-04-01"`
- `2026Q4` → `PeriodStartDate="2026-10-01"`, `Date="2027-01-01"` (год+1)

### Synthetic-лист для отчёта

`SyntheticSheetClientData = "Данные клиента"` — отдельный synthetic-лист
(см. [doc 128](./128-synthetic-stagedrows-and-file-grouping.md)), чтобы пользователь
в отчёте Apply видел, какие ClientData были созданы / упали.

---

## ⚠️ Важно

1. **Только 4 вида помещений.** Заказчик указал маппинг полей только для:
   - `Residential` (Квартира) — fmcode 010
   - `Nonresidential` (Нежилое помещение) — fmcode 020
   - `Othernonresidential` (Кладовая) — fmcode 030
   - `Parking` (Машиноместо) — fmcode 040

   Все остальные категории (Апартаменты `060`, Fact-коды `011/021/...`) — **пропускаются
   тихо**, без row-error. Если заказчик попросит расширить — добавить запись в
   `ClientDataKindByFmCode` + новый case в `BuildClientDataRequest`.

2. **Общие `Cost`/`Rates` И префиксированные — заполнены одним и тем же значением.**
   Например, для квартир Q1: `Cost=10000`, `Rates=100`, `ResidentialCost=10000`,
   `ResidentialRates=100`, остальные prefixed = 0. Это требование заказчика — общие
   поля для дашбордов, prefixed — для детальных срезов.

3. **`ODCount*` всегда 0.** Из файла не берутся — заказчик не указывал источник.
   Visary не допускает `null` в числовых полях (контракт inputdata-стиля), поэтому
   проставляем 0.

4. **PeriodStartDate vs Date.** По примеру HAR: `PeriodStartDate="2026-04-01"` (начало
   Q2), `Date="2026-07-01"` (НАЧАЛО Q3, не конец Q2). Это семантика «период действия
   данных открыт [PeriodStartDate, Date)». Не путать с «конец квартала Q2 = 30 июня».

5. **Точки только-Summ пропускаются.** Если на листе «Общий график» заполнен ТОЛЬКО
   ряд «Сумма от продажи» (без «Площадь»/«Стоимость 1 кв.м») — ClientData не имеет
   данных для записи (`Cost=0`, `Rates=0`). Такие точки пропускаются в фильтре
   `p.Cost != 0d || p.Amount != 0d` ПЕРЕД попаданием в цикл.

6. **Site.Title — фолбэк при недоступности listview.** В отличие от RoomKind (без
   которого ClientData невозможно собрать), Site.Title — декоративное поле для UI
   Visary. Если `listview/constructionsite` отвалился — пишем `Title = "Объект #{ID}"`,
   импорт не блокируем.

7. **Идемпотентность.** Пока не реализована: каждый импорт Финмодели создаёт N×4
   новых ClientData (N кварталов × 4 RoomKind). Заказчик пока не запрашивал pre-check;
   если потребуется — добавить `GetClientDataBySiteAsync` в `ListViewClient` и
   фильтровать по `(siteId, RoomKind.ID, PeriodStartDate)`.

8. **Каскад идёт ПОСЛЕ Plan/Equity/Fact, независимо.** Если предыдущие шаги упали
   (например, `inputdata_codes_unavailable`), ClientData всё равно попытается
   отработать — но у него СВОЙ источник данных (Plan-points `Cost`/`Amount`),
   так что если Plan-парсинг прошёл, ClientData будет создан даже когда fmcode-резолв
   упал и сами inputdata не созданы.

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО — заполнять prefixed-поля для ВСЕХ типов сразу.
new ClientDataCreateRequest
{
    ResidentialCost = 10_000, NonresidentialCost = 10_000, ParkingCost = 10_000, ...
}
// 💥 В UI Visary квартира будет выглядеть как «и квартира, и нежилое, и парковка
//    одновременно по 10к за квадрат» — мусор.
// Правильно — switch по prefix, заполняем только «своё».
```

```csharp
// НЕПРАВИЛЬНО — RoomCategory брать из RoomKindRaw.RoomCategory.
RoomCategory = roomKind.RoomCategory ?? 0,
// 💥 RoomCategory у одного и того же RoomKind может различаться между стендами
//    (опечатка в админке Visary). Безопаснее — захардкоженный маппинг по fmCode.
// Правильно — RoomCategory = binding.RoomCategory (контракт маппера).
```

```csharp
// НЕПРАВИЛЬНО — pass siteId как ID без Title.
new VisaryRef { ID = siteId }  // 💥 в UI Visary сайт без имени, неудобно
// Правильно — отдельный listview/constructionsite + фолбэк "Объект #{id}".
```

```csharp
// НЕПРАВИЛЬНО — Date = PeriodStartDate.AddDays(-1) (последний день предыдущего).
nextPeriodStart = periodStart.AddMonths(3).AddDays(-1);
// 💥 Q2 2026: PeriodStartDate="2026-04-01", Date="2026-06-30" — не совпадает с HAR
//    («2026-07-01»). Заказчик хочет «начало след. квартала», не «конец текущего».
// Правильно — nextPeriodStart = periodStart.AddMonths(3).
```

```csharp
// НЕПРАВИЛЬНО — отправлять точки с Cost=0 И Amount=0 как ClientData.
foreach (var point in planData.InputDataPoints) { /* без фильтра */ }
// 💥 На каждый файл-плановик нулевые ClientData (Cost=0, Rates=0) → загрязнение UI.
// Правильно — Where(p => p.Cost != 0 || p.Amount != 0) ПЕРЕД циклом.
```

```csharp
// НЕПРАВИЛЬНО — резолвить RoomKind точечно через listview/roomkind на каждую точку.
foreach (var point in points) {
    var rk = await _listView.FindRoomKindByTitleAsync(binding.Title, ct);  // 💥 N запросов
}
// Правильно — один ListRoomKindsAsync за вызов + Dictionary<Title, RoomKindRaw>.
```

---

## 📍 Применение в проекте

| Слой | Файл | Метод/блок |
|------|------|------------|
| Мнемоника | `Visary.Api.Client/Common/VisaryMnemonics.cs` | `ClientData = "clientdata"` |
| DTO (Raw) | `Visary.Api.Client/Dto/VisaryEntities.cs` | `ClientDataRaw` |
| DTO (Create) | `Visary.Api.Client/Dto/VisaryCrudRequests.cs` | `ClientDataCreateRequest` |
| CRUD | `Visary.Api.Client/CRUD/CrudClient.cs` | `CreateClientDataAsync` |
| Маппер (entry) | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs` | `EnsureClientDataAsync` (вызов из конца `EnsureFmModelAsync`) |
| Маппер (helpers) | там же | `BuildClientDataRequest`, `TryConvertFmPeriodToDates`, `ClientDataKindByFmCode`, `ClientDataKindBinding` |
| Константа | там же | `SyntheticSheetClientData = "Данные клиента"` |
| Тесты | `KiloImportService.Api.Tests/Mapping/FinModelClientDataTests.cs` | 23 теста: helpers (Theory) + Apply (happy/sad-сценарии) |

---

## 🎯 Чек-лист

- [ ] Лист «Общий график» содержит 4 таблицы (Квартиры/Нежилые/Кладовые/Машиноместа)
      с 4 кварталами → 16 POST `/crud/clientdata`.
- [ ] Квартиры: `RoomCategory=0`, `RoomKind.Title="Квартира"`, заполнены
      `Cost`/`Rates`/`ResidentialCost`/`ResidentialRates`; остальные prefixed = 0.
- [ ] Нежилые: `RoomCategory=1`, prefix `Nonresidential`.
- [ ] Кладовые: `RoomCategory=3`, prefix `Othernonresidential` (внимание: `Other` с
      заглавной, остальная часть с маленькой — точное написание HAR-полей).
- [ ] Машиноместа: `RoomCategory=2`, prefix `Parking`.
- [ ] `Site.Title` берётся из `listview/constructionsite`; недоступен → фолбэк `"Объект #{ID}"`.
- [ ] `PeriodStartDate` = `yyyy-MM-01` первого месяца квартала.
- [ ] `Date` = `yyyy-MM-01` следующего квартала (включая переход через год: Q4 → Q1+1).
- [ ] Точки только-Summ (Cost=0 И Amount=0) **пропускаются**.
- [ ] Апартаменты (fmcode=060), Equity (604), Fact-коды — **пропускаются тихо**.
- [ ] `listview/roomkind` 5xx → row-error `clientdata_roomkind_unavailable`,
      Plan/Equity/Fact не страдают.
- [ ] RoomKind отсутствует в словаре → row-error `clientdata_roomkind_not_found` со
      списком ненайденных Title.
- [ ] POST `/crud/clientdata` падает на одной точке → `clientdata_create_failed`
      в конце со счётчиком, остальные точки идут.
- [ ] Без secondary файла — каскад пропущен (нет Plan-точек).
- [ ] Все тесты `FinModel*` зелёные.

---

## 📅 История изменений

- **v1.0 (2026-06-26)** — первая версия. Маппинг 4 видов помещений (Квартира/Нежилое
  помещение/Кладовая/Машиноместо) → `clientdata` с одинаковыми значениями в общих
  `Cost`/`Rates` и prefixed-полях. `ODCount*` всегда 0. `PeriodStartDate` = первый
  день квартала, `Date` = первый день следующего. 23 теста (helpers + happy/sad).

## 🔗 Связанная документация

- [doc 110 — finmodel-plan-and-fmmodel](./110-finmodel-plan-and-fmmodel.md) —
  парсер «Общий график», источник Cost/Amount для ClientData.
- [doc 112 — finmodel-version-and-inputdata](./112-finmodel-version-and-inputdata.md) —
  Plan-точки `inputdata`; ClientData использует те же материализованные точки.
- [doc 146 — finmodel-equity-funding-input-data](./146-finmodel-equity-funding-input-data.md) —
  каскад «Вложение собственных средств»; ClientData идёт ПОСЛЕ него в `EnsureFmModelAsync`.
- [doc 128 — synthetic-stagedrows-and-file-grouping](./128-synthetic-stagedrows-and-file-grouping.md)
  — synthetic-листы; новый лист «Данные клиента».
- [Reference Visary RoomCategory справочник](../memory/reference_visary_room_category.md) —
  0=Residential, 1=NonResidential, 2=ParkingPlace, 3=OtherNonResidential.
