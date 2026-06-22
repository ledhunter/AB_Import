# 🧾 Финмодель → «Итоговое заключение КА БП7» + рассрочки

## 📋 Описание

После того как импорт «Финмодель» отработал Бюджет (см. [doc 71](./71-finmodel-budget-import.md)) и
ГФ Главы 1 ([doc 91](./91-finmodel-chapter1-schedule.md)), мапер дополнительно
создаёт «Заключение» с типом «Итоговое заключение КА БП7» и заполняет в связанном
«Наборе данных для ФМ» поля рассрочек (ДДУ равномерная / ДДУ единовременная / ДКП)
по данным листов **Control** и **Outputs** основного файла.

| # | Что делает | Visary endpoint |
|---|------------|-----------------|
| 1 | Создать «Заключение» (`Stage=110`, `Status=10`) — каждый импорт = новая запись | `POST /api/visary/crud/projectaudit` |
| 2 | Найти автоматически созданный «Набор данных для ФМ» | `POST /api/visary/listview/datasetforfm` |
| 3 | Получить дикт RoomKind (`Title → ID`) | `POST /api/visary/listview/roomkind` |
| 4 | Pre-check существующих `dataforfm` — собрать `(RoomKindId → dataForFmId)` | `POST /api/visary/listview/dataforfm/onetomany/DataSetForFM` |
| 5 | Для каждого «1 - Да» RoomKind: **PATCH** существующего ИЛИ **POST** нового. На 422 — refetch + PATCH | `POST /api/visary/crud/dataforfm` или `PATCH /api/visary/crud/dataforfm/{id}?forceUpdate=true` |
| 6 | Получить актуальный `RowVersion` `datasetforfm` | `GET /api/visary/crud/datasetforfm/{id}` |
| 7 | PATCH полями `<Prefix>OwnShare/PostpShare/RoomKinds` для **каждой** найденной схемы: включена → значения, выключена → null/[] (очистка) | `PATCH /api/visary/crud/datasetforfm/{id}?forceUpdate=false` |

Источник правды — HAR заказчика `Context/har заключ рассрочки равн.txt` + ответ
GET `/crud/datasetforfm/8030` из чата заказчика (v1.2).

---

## ✅ Правильная реализация

### Парсер блока «Продажи» (лист Control)

Якорь — `B61="Продажи"`. Шапка этапов лежит в строке 23
(`D23="Этап 1"`, `E23="Этап 2"`, `F23="Этап 3"`) и относится ко всей левой
половине листа. Все «1 - Да»/«0 - Нет» для параметров читаем из колонки D
(Этап 1).

Внутри блока сканируем три якорные строки в колонке B (точное совпадение,
case-insensitive):

```text
B69 = "Отсрочка оплаты по ДДУ (равномерная)"   D69 = "1 - Да"
B80 = "Отсрочка оплаты по ДДУ (единовременная)" D80 = "0 - Нет"
B92 = "Отсрочка оплаты по ДКП"                  D92 = "0 - Нет"
```

Для якорей с `D{r}="1 - Да"` парсер далее в окне `[r+1..r+12]`:

- Останавливается на следующем якоре схемы / `"Комплексный продукт"` /
  пустой строке (не строго — пустые строки просто `continue`).
- Пропускает `"Тип помещений"`, `"Период отсрочки …"`, `"Дата для …"`.
- В `"Доля отсрочек"` → `PostpShare`, в `"Доля СУ по ипотеке …"` →
  `OwnShare`. Парсинг процентной ячейки — см. ниже.
- Остальные лейблы трактуются как строки видов помещений; их ячейка
  в колонке `Этап 1` должна содержать `"1 - Да"`.

Лейблы видов помещений в эталонном файле имеют ведущие пробелы:
`"      Квартиры/Апартаменты"`, `"      ПСН"`, `"      Кладовые"`,
`"      Машиноместа"` — `ReadCellTextTrimmed` снимает их.

```csharp
internal const string InstallmentDDUSteadyMarker   = "Отсрочка оплаты по ДДУ (равномерная)";
internal const string InstallmentDDUOnetimeMarker  = "Отсрочка оплаты по ДДУ (единовременная)";
internal const string InstallmentDKPMarker         = "Отсрочка оплаты по ДКП";

internal sealed record InstallmentsData(
    IReadOnlyList<EnabledInstallmentScheme> Schemes);

internal sealed record EnabledInstallmentScheme(
    string Marker,
    double? OwnSharePercent,
    double? PostpSharePercent,
    IReadOnlyList<string> EnabledRoomTypeLabels);
```

### Парсер «Площади реализации» (лист Outputs)

Якоря (колонка C):

```text
C163 = "Доходы поэтапно"
C165 = "Этап 1"
C167 = "Площадь реализации, кв.м."   ← anchor
C168..C176 — Квартиры / Апартаменты / ПСН / Кладовые / Машиноместа / ДОУ / СОШ / ...
E168..E176 — итоговая площадь Этапа 1
```

Прочерки (`—`/`–`/`-`) трактуются как `0`. Останавливаемся на `"Итого"`,
`"Цена реализации"`, `"Выручка"` или пустой строке.

Значение из этой таблицы становится полем `Indicator` у `dataforfm`-строки —
HAR заказчика подтверждает: для квартир `Indicator=16445`, что совпадает
с `E168=16 445`.

### Маппинг лейблов на Visary RoomKind

Лейбл `"Квартиры/Апартаменты"` из блока «Продажи» — групповой: импорт
создаёт **одну** `dataforfm` со связкой `RoomKind="Квартира"`. Если в
файле появится отдельная строка `"Апартаменты"` с «1 - Да» (нестандартный
шаблон), будет создана вторая `dataforfm`. В Outputs «Апартаменты» —
отдельная строка с самостоятельной площадью.

| Лейбл Control | RoomKind.Title (Visary) | Лейбл Outputs (для Indicator) |
|---|---|---|
| `Квартиры/Апартаменты` | `Квартира` | `Квартиры` |
| `Квартиры` | `Квартира` | `Квартиры` |
| `Апартаменты` | `Апартаменты` | `Апартаменты` |
| `ПСН` | `Нежилое помещение` | `ПСН` |
| `Кладовые` | `Кладовая` | `Кладовые` |
| `Машиноместа` | `Машиноместо` | `Машиноместа` |

ДОУ/СОШ/Поликлиника/ФОК в блоке «Продажи» не встречаются — для них
рассрочки не предусмотрены. Если появятся — словарь
`ControlRoomTypeToKindTitles` пополняется одной строкой.

### Visary API — payload'ы

#### POST /crud/projectaudit

```json
{
  "Date": "2026-06-17T08:41:39Z",
  "Status": 10,
  "Stage": 110,
  "ProjectID": 4653,
  "Project": { "ID": 4653 },
  "ConstructionSite": { "ID": 8030 }
}
```

`Stage=110` = «Итоговое заключение КА БП7» (единственный поддерживаемый тип).
`ConstructionSite` передаём явно — иначе сервер по своей логике подцепит
Site проекта по-умолчанию (HAR показывает именно такое поведение), а нам
надо быть уверенным, что Заключение лежит на нужном объекте.

#### POST /crud/dataforfm

```json
{
  "DataSetForFMID": 8030,
  "DataSetForFM": { "ID": 8030 },
  "Title": "Данные по Квартирам",
  "RoomKind": { "Title": "Квартира", "ID": 3 },
  "Indicator": 17445
}
```

Title не валидируется сервером, но для UI используем дательный падеж
(«Квартирам»/«Машиноместам»/…). Если RoomKind не из словаря —
fallback `"Данные по {RoomKind.Title}"`. `Indicator` — площадь
реализации в кв.м. из листа Outputs (для машиномест — шт.).

#### PATCH /crud/dataforfm/{id}?forceUpdate=true (v1.2)

```json
{
  "ID": 94,
  "Title": "Данные по Квартирам",
  "Indicator": 17445
}
```

Используется когда pre-check нашёл существующую строку или после 422
на POST: обновляем `Indicator` новой площадью. `forceUpdate=true` —
без проверки `RowVersion` (паттерн `PatchRoomAsync` / `PatchShareAgreementAsync`).

#### PATCH /crud/datasetforfm/{id}?forceUpdate=false

```json
{
  "DDUSteadyOwnShare": 30,
  "DDUSteadyPostpShare": 50,
  "DDUSteadyRoomKinds": [
    { "Object": { "Title": "Квартира", "ID": 3 } },
    { "Object": { "Title": "Машиноместо", "ID": 4 } }
  ],
  "ID": 8030,
  "RowVersion": 8763241
}
```

**Имена полей** (HAR-подтверждены v1.2):

| Схема | OwnShare | PostpShare | RoomKinds |
|---|---|---|---|
| Равномерная | `DDUSteadyOwnShare` | `DDUSteadyPostpShare` | **`DDUSteadyRoomKinds`** (без `Postp`) |
| Единовременная | `DDUOneTimeOwnShare` | `DDUOneTimePostpShare` | **`DDUOneTimePostpRoomKinds`** (с `Postp`) |
| ДКП | `DKPOwnShare` | `DKPPostpShare` | **`DKPPostpRoomKinds`** (с `Postp`) |

⚠️ Регистр: «**OneTime**» — заглавная T (CamelCase). У равномерной
RoomKinds-поле БЕЗ постфикса `Postp`; у двух других — С `Postp`.

**Очистка выключенных схем** (v1.2): если в Excel `D{anchor}="0 - Нет"`,
PATCH отправляется с `null` для shares и `[]` для RoomKinds — старые
значения в Visary очищаются. Если маркер вовсе отсутствует в шаблоне —
PATCH не отправляется (схема не в результате парсера).

Важно:

- **RoomKinds — массив `{Object: {ID, Title}}`** (M:N-обёртка Visary,
  а не голый массив `VisaryRef`).

- **`RowVersion` обязателен.** Перед каждым PATCH (даже вторым в рамках
  одного импорта — для второй схемы) перечитываем `RowVersion` через
  `GET /crud/datasetforfm/{id}`.

- **`DKPPostpQuarterCount`** — поле есть в сущности, но в Excel
  соответствующего параметра нет, импорт его не PATCH-ит.

### Интеграция в `ApplyAsync`

Шаг вызывается **в конце** `ApplyAsync`, после Budget+ГФ, до `return`:

```csharp
if (context.VisaryProjectId is { } projectIdForAudit)
{
    await EnsureProjectAuditAndInstallmentsAsync(
        projectIdForAudit, siteId,
        context.PrimaryFileRelativePath,
        paramsApplied, budgetUploadOk,
        errors, synthetic, ct);
}
```

Шаг ортогонален mapped-строкам: даже если parameters/budget/GF упали —
Заключение создаётся (если в файле есть включённые схемы). Любая ошибка
внутри — одна row-error + skip всего шага, остальные шаги Финмодели
не затрагиваются. Файлово сгруппированный отчёт показывает синтетический
лист **«Заключение и рассрочки»**.

---

## ⚠️ Важно

1. **`Stage=110`/`Status=10` — это контракт.** «Тип заключения» в Visary —
   не справочная сущность, а целочисленный код в поле `Stage` у
   `projectaudit`. Источник — HAR.

2. **`datasetforfm` создаётся сервером автоматически** при POST `projectaudit`
   (по паре `Site + Project`). Импорт его **не создаёт явно** — только
   находит и PATCH-ит. Если listview вернул пусто (сервер не подтянул),
   фиксируем row-error `datasetforfm_not_found`.

3. **Pre-check Заключения по `(Site, Stage)`.** Один Site может иметь
   несколько `projectaudit` разных типов (Stage), но «Итоговое заключение
   КА БП7» — одно. Повторный импорт того же файла переиспользует найденный
   `projectaudit`, PATCH'и рассрочек применяются к тому же `datasetforfm`.

4. **`Indicator` = площадь реализации в кв.м.** Округление —
   `MidpointRounding.AwayFromZero`. Для «Машиноместа» единица измерения
   `шт.` — Visary интерпретирует Indicator по `RoomKind` в любом случае.

5. **Поле `OwnShare/PostpShare` хранится в Visary в **процентах** (30 = 30%),
   не в долях.** HAR подтверждает: `DDUSteadyOwnShare:30`. Парсер
   `TryReadPercentCell` корректно работает с тремя вариантами:
   - число в [0..1] (доля) → ×100;
   - число c процентным форматом → ×100;
   - голое число > 1 → как есть (уже в процентах).

6. **DDUO/DDUS/DKPP — пользовательские прозвища.** Заказчик ссылается
   на префиксы как «DDUO» (равномерная), «DDUS» (единовременная), «DKPP»
   (ДКП). По HAR реальный префикс «равномерной» — **`DDUSteady`**, а не
   `DDUO`. Если на стенде заказчика поля окажутся переименованы — смена
   префикса = одна строка в `InstallmentSchemes` в
   [FinModelImportMapper.Installments.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.Installments.cs).

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО — создавать datasetforfm вручную перед PATCH.
await _visaryClient.CreateDataSetForFmAsync(...); // 💥 нет такого метода, и не нужно
// Правильно — после POST projectaudit сервер сам создаёт DataSet;
// мы только находим его через listview/datasetforfm по (Site, Project).
```

```csharp
// НЕПРАВИЛЬНО — слать массив VisaryRef «голым».
DDUSteadyRoomKinds = roomKinds // 💥 Visary 400: ожидает {Object:{...}}
// Правильно — обернуть каждый RoomKind в {Object:{...}}:
DDUSteadyRoomKinds = roomKinds.Select(rk => new { Object = new { rk.ID, rk.Title } })
```

```csharp
// НЕПРАВИЛЬНО — переиспользовать RowVersion между PATCH'ами.
var fresh = await _crud.GetDataSetForFmByIdAsync(id, ct);
foreach (var scheme in enabledSchemes)
{
    await _crud.PatchDataSetForFmInstallmentsAsync(new { ..., fresh.RowVersion });
    // 💥 второй PATCH упадёт 409 — RowVersion устарел после первого PATCH'а
}
// Правильно — перечитывать RowVersion перед каждым PATCH'ом:
foreach (var scheme in enabledSchemes)
{
    var fresh = await _crud.GetDataSetForFmByIdAsync(id, ct);
    await _crud.PatchDataSetForFmInstallmentsAsync(new { ..., fresh.RowVersion });
}
```

```csharp
// НЕПРАВИЛЬНО — игнорировать «Этап 1»-колонку.
var v = sheet.Cell(69, 4).GetFormattedString(); // 💥 хардкод на D
// Правильно — найти колонку «Этап 1» в шапке (строка 23) и читать из неё:
var stageRow = FindStageHeaderRow(sheet, out var stageColumn);
var v = ReadCellTextTrimmed(sheet, anchor, stageColumn);
```

```csharp
// НЕПРАВИЛЬНО — считать «—» нестроковой ячейкой пустотой.
if (cell.IsEmpty()) return null; // 💥 «—» — это число 0 с custom format
// Правильно (см. doc 126 v1.2) — использовать GetFormattedString + явный
// маркер «—»/«–»/«-» → 0.
```

```csharp
// НЕПРАВИЛЬНО (до v1.2) — POST dataforfm и skip на 422.
if (existingByKind.Contains(kindId)) continue;
await _crud.CreateDataForFmAsync(...); // 💥 422 → Indicator никогда не обновится
// Правильно (v1.2) — Dictionary kindId → existingId, PATCH если есть, POST если нет:
if (existingByKind.TryGetValue(kindId, out var id))
    await _crud.PatchDataForFmAsync(id, new DataForFmPatchRequest { Indicator = newIndicator });
else
    await _crud.CreateDataForFmAsync(...);
// + catch 422 → refetch + PATCH (на случай, если pre-check упал).
```

```csharp
// НЕПРАВИЛЬНО (до v1.2) — пропускать выключенные схемы.
foreach (var scheme in InstallmentSchemes) {
    var enabled = parsed.Schemes.FirstOrDefault(s => s.Marker == scheme.Marker);
    if (enabled is null || enabled.EnabledRoomTypeLabels.Count == 0) continue;
    // 💥 старые значения из HAR/прошлого импорта остаются в Visary
    await _crud.PatchDataSetForFmInstallmentsAsync(scheme, enabled.Values);
}
// Правильно — PATCH каждую найденную схему, выключенная → null/[]:
foreach (var scheme in InstallmentSchemes) {
    var data = parsed.Schemes.FirstOrDefault(s => s.Marker == scheme.Marker);
    if (data is null) continue; // маркер отсутствует в шаблоне → не трогаем
    await _crud.PatchDataSetForFmInstallmentsAsync(scheme,
        // OwnShare/PostpShare/RoomKinds = null/[] если data.IsEnabled=false
        data.OwnSharePercent, data.PostpSharePercent, data.IsEnabled ? kinds : []);
}
```

```csharp
// НЕПРАВИЛЬНО — pre-check projectaudit по (Site, Stage).
var existing = await _listView.FindProjectAuditsBySiteAsync(siteId, 110, ct);
if (existing.Data?.FirstOrDefault() is { } found) return found.ID; // 💥 угоняет чужое
// Правильно (v1.1) — каждый импорт создаёт НОВОЕ Заключение. Pre-check удалён.
```

```csharp
// НЕПРАВИЛЬНО — строгий int? для variant-поля.
public sealed class DataForFmRaw { public int? Indicator { get; set; } }
// 💥 листинг падает на десериализации, pre-check провален → POST дубликат → 422
// Правильно (v1.1) — JsonElement? (см. doc 56):
public sealed class DataForFmRaw { public JsonElement? Indicator { get; set; } }
```

---

## 📍 Применение в проекте

| Слой | Файл | Метод/символ |
|------|------|--------------|
| Мнемоники | `Visary.Api.Client/Common/VisaryMnemonics.cs` | `ProjectAudit`, `DataSetForFm`, `DataForFm`, **`PercentBetType`** (v1.4), **`DealPercentBet`** (v1.4) |
| DTO (Raw) | `Visary.Api.Client/Dto/VisaryEntities.cs` | `ProjectAuditRaw`, `DataSetForFmRaw`, `DataForFmRaw` (Indicator=`JsonElement?`, v1.1), **`PercentBetTypeRaw`**, **`DealPercentBetRaw`** (v1.4) |
| Create/Patch requests | `Visary.Api.Client/Dto/VisaryCrudRequests.cs` | `ProjectAuditCreateRequest`, `DataForFmCreateRequest`, **`DataForFmPatchRequest`** (v1.2), `DataSetForFmInstallmentsPatchRequest`, **`DealPercentBetCreateRequest`** (v1.4) |
| CRUD | `Visary.Api.Client/CRUD/CrudClient.cs` | `CreateProjectAuditAsync`, `CreateDataForFmAsync`, **`PatchDataForFmAsync`** (v1.2), `GetDataSetForFmByIdAsync`, `PatchDataSetForFmInstallmentsAsync`, **`CreateDealPercentBetAsync`** (v1.4) |
| ListView | `Visary.Api.Client/ListView/ListViewClient.cs` | `FindProjectAuditsBySiteAsync`, `FindDataSetForFmAsync`, `GetDataForFmByDataSetAsync`, **`FindPercentBetTypeByCodeAsync`** (v1.4) |
| Парсер | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.Installments.cs` | `ReadInstallmentsData`, `ReadSalesAreasData`, `InstallmentsData`, `SalesAreasData`, `EnabledInstallmentScheme` (с **`IsEnabled`**, v1.2), **`ReadFinancingData`**, **`FinancingData`**, **`EnabledFinancingRate`**, **`TryMatchFinancingRateCode`** (v1.4) |
| Мапер (orchestrator) | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.Installments.cs` | `EnsureProjectAuditAndInstallmentsAsync` (вызов из конца `ApplyAsync`), helper `RefetchDataForFmIdAsync` (v1.2), `IsDuplicateDataForFmConflict`, **`EnsureDealPercentBetsAsync`** (v1.4 — шаг между RoomKind-резолвом и POST projectaudit) |
| Константы | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.Installments.cs` | `ProjectAuditStageFinalBp7=110`, `ProjectAuditStatusInitial=10`, `InstallmentSchemes[]` (Marker → FieldPrefix mapping, v1.2 HAR-точное) |
| Тесты (парсер) | `KiloImportService.Api.Tests/Mapping/FinModelInstallmentsTests.cs` | 32 теста: эталонная раскладка / все 3 схемы / отсутствие маркеров / percent-cell / IsEnabled-флаг (v1.2) / DateToFmPeriod + ReadCommissioning (v1.3) / **ReadFinancing + TryMatchFinancingRateCode + КД-резолв** (v1.4) |

---

## 🎯 Чек-лист

- [ ] Файл «Параметры…» содержит лист Control и B61=«Продажи» — иначе
      `installments_parse_error`.
- [ ] Если ни одна схема не включена («1 - Да») — Заключение не создаётся,
      ставится info `installments_skipped_no_schemes`.
- [ ] **Каждый импорт создаёт новое `projectaudit`** (pre-check удалён в v1.1).
- [ ] `listview/datasetforfm` возвращает 1 запись по (Site, Project) →
      её ID используется для `dataforfm` и PATCH.
- [ ] `listview/roomkind` упал → row-error `installments_roomkind_unavailable`,
      Заключение не создаётся.
- [ ] Лейбл вида помещения не сматчился → row-error
      `installments_roomkind_not_resolved` со списком, остальные виды идут.
- [ ] Для каждого RoomKind с «1 - Да»:
      • если `dataforfm` уже есть в Visary → **PATCH** новой площадью (Indicator);
      • если нет → **POST**;
      • 422 на POST → refetch + PATCH (v1.2).
- [ ] PATCH datasetforfm — один на **каждую найденную** схему:
      • включённая → значения из Excel;
      • выключенная → null + пустой массив RoomKinds (очистка старых данных);
      • отсутствующий маркер → не трогаем (схема не в результате парсера).
- [ ] `RowVersion` `datasetforfm` перечитывается перед каждым PATCH.
- [ ] Имена полей рассрочек (v1.2): **`DDUSteady*`** /
      **`DDUOneTime*` + `DDUOneTimePostpRoomKinds`** /
      **`DKP*` + `DKPPostpRoomKinds`** — см. таблицу в v1.2.
- [ ] Все 11 тестов `FinModelInstallmentsTests` — зелёные.
- [ ] Полный suite 415 / 415 — зелёный.

---

## 📅 История изменений

- **v1.4.2 (2026-06-18)** — Два точечных фикса по результатам второй реальной
  заливки:

  **1. Глобальный поиск «Номер КД» (КД=«0» не пропускался)**

  Первая версия `TryReadKdNumber` искала заголовок «Номер КД» только в
  окне 15 строк под якорем раздела «Результаты». В реальном файле заказчика
  раздел и заголовок были разнесены дальше — парсер не находил КД и
  пропускал создание ставок с info `«Номер КД» не указан в разделе
  «Результаты»` (даже если в ячейке стояло осмысленное значение, например
  `«0»`).

  Новый двухступенчатый поиск:
  1. **Узкий**: под «Результаты» в окне ~50 строк — как раньше, но шире.
  2. **Глобальный fallback**: если узкий поиск пустой — сканируем ВЕСЬ
     лист (`LastRowUsed()` × `LastColumnUsed()`). Любая ячейка, чей текст
     содержит «Номер КД», засчитывается как заголовок; значение берём
     из ячейки СНИЗУ (приоритет) или СПРАВА (fallback на горизонтальную
     раскладку из doc 105).

  Также теперь корректно распознаётся численное значение `0` как валидный
  № КД (`GetFormattedString()` возвращает `«0»`, `IsNullOrWhiteSpace` →
  false; раньше всё работало, но добавлены явные тесты).

  **2. Уникальный `LmID` на каждую ставку (UNIQUE constraint Visary)**

  Visary держит `UX_DealPercentBet_LmID` — UNIQUE-индекс на колонку
  `LmID` сущности `dealpercentbet`. Первая версия v1.4 генерила один
  timestamp `«dd-MM-yyyy-HH-mm-ss»` на всю пачку (под одного `deal`-а),
  поэтому первая ставка создавалась успешно, а вторая+ падала 422:
  ```
  Visary 422: Такая запись существует
  Npgsql 23505: duplicate key value violates unique constraint
  "UX_DealPercentBet_LmID"
  ```

  Фикс — суффикс per-rate: `«dd-MM-yyyy-HH-mm-ss-fff-{Code}-{idx}»`.
  Расширения:
  - `-fff` — миллисекунды (защита от двух импортов в ту же секунду).
  - `-{Code}` — код ставки (`LM10`/`LM20`/…), упрощает отладку.
  - `-{idx}` — счётчик внутри пачки (защита от двух sub-строк под одним
    родителем — LM10 sub1 и LM10 sub2 получают разные суффиксы 1 и 2).

  Формат заказчика расширен — UNIQUE-индекс важнее точного длинного
  варианта. Если на стенде в столбце `LmID` слишком короткое ограничение
  типа varchar(20) — укоротить до `«dd-MM-yyyy-HH-mm-ss-{idx}»`.

- **v1.4.1 (2026-06-18)** — Алгоритм парсера ставок исправлен после первой
  реальной заливки: ставки не создавались, потому что родительская ячейка
  «Базовая %% ставка» в Excel по умолчанию пуста / содержит текст-флаг
  (например, «1 - Фиксированная»), а не числовой процент.

  **Новый алгоритм** (`ReadFinancingRates` parent + sub-rows):
  1. В блоке «Финансирование» находим все 4 родительские строки
     (LM10/LM20/LM30/LM40) через тот же `TryMatchFinancingRateCode`.
  2. Для каждого родителя читаем ячейку на пересечении со «Этапом 1»:
     - `«0 - Нет»` → пропускаем ставку целиком, идём к следующему родителю.
     - Любое другое значение (включая пустое или текст-флаг типа
       `«1 - Фиксированная»`/`«1 - Отсрочка до РНС»`) → переходим к sub-строкам.
  3. Сканируем строки между этим родителем и следующим — это «сценарии»
     (например, «Фиксированная ставка (сценарий 1)», «Премия к КС РФ
     (сценарий 2)»).
  4. Для каждой sub-строки, у которой значение в колонке Этапа 1 непустое
     и не «0 - Нет», создаём **отдельную** запись `dealpercentbet`. Rate:
     - число / процент → как есть (`TryReadPercentCell`);
     - текст-флаг `«N - X»` → ведущая цифра N (`TryParseLeadingNumber`),
       например `«1 - Фиксированная»` → `Rate=1`;
     - распарсить не удалось → `Rate=0`.
  5. Если у родителя все sub-строки пустые — ставку пропускаем (как и просит
     заказчик: «Если в полях ничего нет, тогда пропускаем ставку»).

  **PercentBetType** по-прежнему резолвится по коду родителя (LM10..40), то
  есть несколько сценариев под одной родительской ставкой получают один и тот
  же тип в Visary. Если на стенде окажется, что разные сценарии должны мапиться
  на разные `percentbettype.Code` — расширим логику lookup'ом по Title sub-строки.

  **Эталонная раскладка** (по скриншоту заказчика):
  | Родитель | Этап 1 | Sub-строка | Этап 1 | → Ставка |
  |---|---|---|---|---|
  | Базовая %% ставка (LM10) | (пусто) | Фиксированная ставка (сценарий 1) | «1 - Фиксированная» | Rate=1 |
  | | | Премия к КС РФ (фикс) (сценарий 2) | (пусто) | skip |
  | Капитализация / отсрочка уплаты %% (LM20) | «1 - Отсрочка до РНС» | Ручной ввод периода отсрочки | (пусто) | skip |
  | | | Доля капитализации/отсрочки процентов | «100%» | Rate=100 |
  | Базовая по капитализированным %% (LM30) | «1 - Фиксированная» | Фиксированная ставка (сценарии 1-2) | (пусто) | skip |
  | | | Премия к КС РФ (сценарии 1-2) | (пусто) | skip всего LM30 |
  | Комиссия за отсрочку %% (LM40) | «0 - Нет» | — | — | skip |

  **Изменения в коде**:
  - `EnabledFinancingRate` получил поле `SubRowLabel` (для диагностики /
    synthetic-отчёта: «Ставка [LM10]: создана … сценарий «Фиксированная ставка (сценарий 1)»»).
  - Добавлен helper `TryParseLeadingNumber` (вытаскивает ведущее число строки).
  - Добавлен helper `TryReadSubrowLabel` (читает лейбл в колонках 1..4).
  - Удалён старый fallback «1 - Да + значение в соседних колонках» — теперь
    значения всегда лежат в той же колонке у sub-строки.

  **Тесты** (`FinModelInstallmentsTests` обновлён под новую раскладку):
  - `ReadFinancing_Reference_KdAndTwoEnabledRates` — эталон → 2 ставки.
  - `ReadFinancing_ParentNotNo_MultipleSubrowsWithValues_AllReturned` — у LM10
    обе sub-строки с значениями → 2 ставки на одного родителя.
  - `ReadFinancing_AllParentsNo_ReturnsEmpty` — все «0 - Нет», даже если
    sub-строки заполнены.
  - `ReadFinancing_ParentIsNo_SubrowsWithValuesIgnored` — «0 - Нет» у родителя
    бьёт значения sub-строк.
  - `ReadFinancing_ParentNotNo_AllSubrowsEmpty_SkipsParent` — LM30 в эталоне.
  - `ReadFinancing_SubrowPercentValue_RateParsedAsPercent`,
    `ReadFinancing_SubrowTextWithLeadingDigit_RateIsLeadingDigit` — формат Rate.

  **Не делал в этой итерации** (если заказчик попросит — добавим):
  - Lookup `PercentBetType` по sub-row Title (сейчас всё в LM10..40).
  - Идемпотентность (повторный импорт всё ещё дублирует ставки).
  - Etap 2/3 — данных нет в Excel пока.

- **v1.4 (2026-06-18)** — **Процентные ставки сделки** перед созданием Заключения.

  **Заказчик**: «Перед тем, как создавать заключение. На листе Control найти
  раздел "Результаты", далее колонку "Номер КД", получить номер в ячейке ниже.
  Найти Сделку по Номеру КД через `listview/deal`. Если в чужом проекте /
  найдено несколько — написать в отчёте, ставки не создавать, перейти к
  созданию заключения. Если всё хорошо — запомнить ID сделки. Из раздела
  «Финансирование» получить 4 ставки LM10/LM20/LM30/LM40 на пересечении со
  «Этапом 1». Если «0 - Нет» — пропустить. Иначе создать `dealpercentbet`».

  **Источник**: основной файл «Параметры», лист **Control**, разделы
  «Результаты» (КД) и «Финансирование» (ставки).

  **Парсинг «Номер КД»** (`TryReadKdNumber`):
  1. Найти раздел «Результаты» (любая колонка 1..6, строки 1..500).
  2. Под ним в окне ~15 строк найти ячейку «Номер КД» (column header).
  3. Значение — в ячейке СНИЗУ. Fallback на горизонтальную раскладку
     (label-value в одной строке): ячейка справа.

  **Парсинг ставок** (`ReadFinancingRates`):
  1. Найти раздел «Финансирование».
  2. В окне (anchor+1)..(anchor+60), в колонках 1..3 искать строки-лейблы
     ставок. Распознавание — нормализованным contains-матчем
     (`TryMatchFinancingRateCode`):
     - `LM30` → contains «капитализированным» (проверяем ПЕРВЫМ, иначе LM10 поглотит)
     - `LM20` → contains «капитализация» ИЛИ «отсрочка уплаты»
     - `LM40` → contains «комис» И «отсрочк» (корень «комис» ловит и «Комисия», и «Комиссия»)
     - `LM10` → contains «базовая» И «ставка» (последним)
  3. На пересечении с Этапом 1:
     - «0 - Нет» → ставка не попадает в результат
     - Число → используем как Rate
     - «1 - Да» (флаг) → значение лежит «между этими колонками», т.е. в
       одной из 1..3 ячеек правее (заказчик: «получить значения между
       этими колонками»)

  **Quota deal**:
  - `GetDealsAsync(docNumberFilter=КД)` — глобальный listview/deal.
  - Точное совпадение `DocNumber` локально (защита от contains-семантики Visary).
  - 0 результатов → row-error `rates_deal_not_found`, skip ставок, continue Заключение.
  - >1 → row-error `rates_multiple_deals`, skip, continue.
  - ConstructionProject.ID ≠ нашему projectId → row-error
    `rates_deal_in_other_project`, skip, continue.

  **Создание `dealpercentbet`**:
  ```json
  {
    "DealID": 91,
    "Deal": { "ID": 91 },
    "PercentKind": 10,
    "LmID": "18-09-2025-15-50-51",
    "Rate": 100,
    "PercentBetType": { "Title": "Фиксированная (базовая)", "ID": 7 }
  }
  ```
  - `PercentKind` = 10/20/30/40 для LM10/LM20/LM30/LM40 соответственно.
  - `LmID` = текущее время импорта `dd-MM-yyyy-HH-mm-ss` (одно на пачку).
  - `PercentBetType` находится через `FindPercentBetTypeByCodeAsync(rate.Code)`.

  **Размещение в pipeline**: `EnsureDealPercentBetsAsync` вызывается из
  `EnsureProjectAuditAndInstallmentsAsync` между шагом 3 (RoomKind dict)
  и шагом 4 (POST projectaudit). Любая ошибка → row-error + skip ставок;
  Заключение создаётся как обычно.

  **Новые сущности**:
  - Мнемоники: `percentbettype`, `dealpercentbet` (`VisaryMnemonics.cs`).
  - DTO: `PercentBetTypeRaw`, `DealPercentBetRaw`, `DealPercentBetCreateRequest`.
  - ListView: `FindPercentBetTypeByCodeAsync(code)` — Filter `["Code","=",code]`,
    Sorts `[{"selector":"ID","desc":true}]` (HAR заказчика).
  - CRUD: `CreateDealPercentBetAsync` — POST `/crud/dealpercentbet`.

  **Тесты** (+9 в `FinModelInstallmentsTests`, итого 32):
  - `ReadFinancing_Reference_*` — happy path.
  - `ReadFinancing_AllFourRatesEnabled_AllReturned` — все 4 включены.
  - `ReadFinancing_AllRatesDisabled_ReturnsEmpty` — все «0 - Нет».
  - `ReadFinancing_NoFinancingSection_ReturnsEmptyRates` — только КД.
  - `ReadFinancing_NoResultsSection_KdIsNull_RatesStillRead` — только ставки.
  - `ReadFinancing_NoControlSheet_ReturnsEmpty` — нет листа Control.
  - `TryMatchFinancingRateCode_VariousLabels_MatchesExpected` (Theory) — все
    варианты лейблов LM10..LM40 + негативные.
  - `ReadFinancing_KdLabelAlsoInHorizontalLayout_StillFound` — fallback на
    горизонтальную раскладку из doc 105.
  - `ReadFinancing_RateWithYesFlagAndAdjacentValue_PicksAdjacent` —
    «1 - Да»-флаг в Этапе 1 + значение справа.

  **Коды ошибок отчёта** (новые в `rates_*` namespace):
  | Код | Severity | Триггер |
  |---|---|---|
  | `rates_parse_error` | error | Исключение парсера Финансирование/Результаты |
  | `rates_deal_lookup_failed` | error | listview/deal упал |
  | `rates_deal_not_found` | warning | По КД ничего не найдено |
  | `rates_multiple_deals` | warning | По КД найдено >1 сделок |
  | `rates_deal_in_other_project` | warning | Сделка в чужом проекте |
  | `rates_bettype_lookup_failed` | error | listview/percentbettype упал |
  | `rates_bettype_not_found` | warning | В справочнике нет кода ставки |
  | `rates_create_failed` | error | POST dealpercentbet упал |

  **Важно**:
  - Pre-check существующих `dealpercentbet` НЕ делаем — повторный импорт
    создаёт дубликаты ставок. Это сознательное решение (заказчик: «каждый
    импорт = новые ставки», по аналогии с projectaudit; см. doc 139 v1.1).
    Если потребуется идемпотентность — добавить listview-фильтр по
    `(Deal, PercentKind, LmID)` и skip уже существующих.
  - Шаг ставок НЕ блокирует Заключение. Любая проблема пишется как row-error
    и тихо пропускается, projectaudit создаётся.
  - В Excel пока нет данных для ставок Этапов 2/3 — реализована только
    LM*-четвёрка Этапа 1.

- **v1.3 (2026-06-18)** — `fmmodel.CommisioningPeriod` — квартал ввода
  в эксплуатацию.

  **Источник**: основной файл «Параметры», лист **Control**, раздел
  «Конфигурация этапов», строка `Этап 1.`, колонка «Ввод в эксплуатацию
  (получение РнВ)». Дата преобразуется в квартал `{Year}Q{N}`.

  **Правило `DateToFmPeriod`** (стандартное определение, уточнено заказчиком
  2026-06-18):
  - Q1: январь–март (месяцы 1..3)
  - Q2: апрель–июнь (месяцы 4..6)
  - Q3: июль–сентябрь (месяцы 7..9)
  - Q4: октябрь–декабрь (месяцы 10..12)

  Реализация — одна формула, последний день квартала остаётся в этом же
  квартале (`31.03.2029 → 2029Q1`, `31.12.2029 → 2029Q4`):
  ```csharp
  var quarter = (date.Month - 1) / 3 + 1;
  return $"{date.Year}Q{quarter}";
  ```

  **Поле**: `FmModelRaw.CommisioningPeriod` (`string?`) + одноимённое поле
  в `FmModelCreateRequest`. ⚠️ Имя сохранено с одной «s» — как написал
  заказчик (`CommisioningPeriod`, не `CommissioningPeriod`). Если стенд
  использует грамматически корректную форму — поменять = одна строка в DTO.

  **Парсер**: `FinModelImportMapper.ReadCommissioningData(stream)` — открывает
  лист `Control`, ищет «Конфигурация этапов» (anchor), под ним шапку с текстом
  «Ввод в эксплуатацию» (запоминает колонку), ниже — строку `Этап 1.` или
  `Этап 1` (без точки). Дата читается через `cell.TryGetValue<DateTime>` +
  текстовый fallback `ru-RU` / Invariant. Любой из якорей не найден → возвращает
  null, POST `fmmodel` идёт без `CommisioningPeriod` (опциональное поле).

  **Интеграция**: `EnsureFmModelAsync` парсит до `CreateFmModelAsync` и кладёт
  в payload. Любая ошибка парсинга — WARN в лог, не блокирует Финмодель.

  **Тесты** (12 новых, всего 23 в `FinModelInstallmentsTests`):
  - `DateToFmPeriod_ReturnsExpectedQuarter` — 8 Theory-кейсов (концы кварталов,
    середины, первый день квартала).
  - `ReadCommissioning_Reference_ReturnsStage1ProductionQuarter` — happy path
    с эталонной раскладкой Control.
  - `ReadCommissioning_NoStage1Row_ReturnsNull` /
    `…NoCommissioningHeader_ReturnsNull` /
    `…EmptyDateCell_ReturnsNull` — три edge case.

- **v1.2 (2026-06-18)** — точные имена полей `DataSetForFM` уточнены по ответу
  GET `/crud/datasetforfm/8030` от заказчика (см. чат):

  | Схема | OwnShare | PostpShare | RoomKinds |
  |---|---|---|---|
  | Равномерная | `DDUSteadyOwnShare` | `DDUSteadyPostpShare` | `DDUSteadyRoomKinds` (без `Postp`) |
  | Единовременная | `DDUOneTimeOwnShare` | `DDUOneTimePostpShare` | `DDUOneTimePostpRoomKinds` (с `Postp`!) |
  | ДКП | `DKPOwnShare` | `DKPPostpShare` | `DKPPostpRoomKinds` (с `Postp`!) |

  Гайды по нейминговым нюансам:
  - **CamelCase «OneTime»**, не «Onetime» — заглавная T.
  - Для **равномерной** RoomKinds-поле БЕЗ `Postp` (`DDUSteadyRoomKinds`),
    для остальных двух — С `Postp` (`<Prefix>PostpRoomKinds`).
  - У ДКП есть дополнительное поле `DKPPostpQuarterCount` (квартальный счётчик
    отсрочки) — в Excel его нет, импорт не PATCH-ит.

  Также убран одноразовый лог raw-body GET `datasetforfm` — он сделал своё дело.

  Помимо имён, в v1.2 добавлены **четыре** поведенческих фикса:

  1. **`PatchDataForFmAsync(id, Indicator)`** — обновление Indicator у существующих
     `dataforfm`-строк (раньше POST упирался в 422 → `dataforfm` оставались
     со старыми значениями из HAR).

  2. **Pre-check теперь возвращает `Dictionary<roomKindId, dataForFmId>`** —
     каждому RoomKind ставится в соответствие конкретный ID существующей строки.
     Если есть → PATCH (обновляем Indicator), нет → POST.

  3. **422 → refetch + PATCH.** Если pre-check упал (variant-поле или транспорт-fail),
     POST даст 422 — ловим, делаем повторный listview, находим ID существующей
     строки и PATCH-им её новой площадью.

  4. **Очистка выключенных схем.** Парсер расширен: `EnabledInstallmentScheme.IsEnabled`.
     Маркер найден + `D{anchor}="0 - Нет"` → схема в результате с
     `IsEnabled=false, OwnShare=null, PostpShare=null, RoomKinds=[]`. Оркестратор
     PATCH-ит каждую найденную схему, для выключенной шлёт пустоту → очищает
     поля в Visary (раньше старые данные из предыдущего импорта/HAR оставались).

- **v1.1 (2026-06-18)** — три точечных фикса по результатам первой реальной заливки
  (см. логи backend от 04:44):

  1. **Pre-check `projectaudit` удалён** — каждый импорт создаёт НОВОЕ Заключение.
     В первой версии pre-check по `(Site, Stage=110)` находил чужое 7135-е
     Заключение, созданное вручную (из HAR заказчика), и реюзал его. Заказчик
     ожидает: «один импорт = одно новое Заключение».

  2. **`DataForFmRaw.Indicator` переведён с `int?` на `JsonElement?`.**
     При CREATE сервер принимает `Indicator` как целое битмаска (HAR: 16445/65),
     но в listview возвращает variant-формат, на котором падает строгий
     `Int32`-десериализатор:
     ```
     The JSON value could not be converted to System.Nullable[Int32].
     Path: $.Data[0].Indicator
     ```
     Импортеру в pre-check нужны только `(DataSetForFMID, RoomKind.ID)` —
     `JsonElement?` обнуляет проблему. Паттерн см. doc 56.

  3. **422 при создании `dataforfm` трактуется как «уже существует, skip».**
     Сервер защищён `UX_DataForFM_DataSetForFMID_RoomKindID` (uniq.
     `(DataSetForFMID, RoomKindID)`); при провале pre-check выше — CREATE
     может попасть в конфликт. Распознаём по тексту exception (`422` +
     `UX_DataForFM_DataSetForFMID_RoomKindID` или `Тип помещения`),
     эмитим `synthetic` с пометкой «уже существует в Visary — пропуск».

- **v1.0 (2026-06-18)** — первая реализация. Реализованы:
  - 3 DTO в Visary.Api.Client (Raw, Create, PatchInstallments).
  - 4 метода в `CrudClient` + 3 метода в `ListViewClient`.
  - `partial class FinModelImportMapper` (новый файл `.Installments.cs`):
    парсер «Продажи» (Control), парсер «Площадь реализации» (Outputs),
    orchestrator `EnsureProjectAuditAndInstallmentsAsync` (вызов в конце
    `ApplyAsync`).
  - 11 unit-тестов (парсер + проценты).
  - HAR-подтверждённые имена полей `DDUSteady*` для равномерной;
    `DDUOnetime*` / `DKP*` — educated guess для остальных двух схем
    (требуется HAR-подтверждение при первой реальной загрузке).

## 🔗 Связанная документация

- [doc 91 — chapter1-schedule](./91-finmodel-chapter1-schedule.md) — ГФ Главы 1,
  идёт перед Заключением.
- [doc 110 — finmodel-plan-and-fmmodel](./110-finmodel-plan-and-fmmodel.md) —
  fmmodel: базовый каскад Финмодели.
- [doc 112 — finmodel-version-and-inputdata](./112-finmodel-version-and-inputdata.md) —
  паттерн «pre-check + create + link», заимствован здесь.
- [doc 126 — finmodel-fact-inputdata-from-outputs](./126-finmodel-fact-inputdata-from-outputs.md) —
  парсер Outputs (Fact-блок), близкий по структуре к «Площади реализации».
