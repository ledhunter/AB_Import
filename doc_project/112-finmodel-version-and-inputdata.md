# 🗂️ Финмодель → версия (`fmmodelversion`) + входные данные (`inputdata`)

## 📋 Описание

Дополнение к [doc 110](./110-finmodel-plan-and-fmmodel.md). После создания `fmmodel`
импорт «Финмодель» автоматически достраивает 3 шага по HAR заказчика:

| # | Что делает | Visary endpoint |
|---|------------|-----------------|
| 1 | Создать **версию** Финмодели с фиксированным Title «Версия - Перенос из Эксель» | `POST /api/visary/crud/fmmodelversion` |
| 2 | Создать **«Входные данные»** по каждой паре (Квартал × вид помещения) из листа «План» | `POST /api/visary/crud/inputdata` |
| 3 | **Привязать** созданную `inputdata` к версии | `POST /api/visary/listview/inputdata/onetomany/FMModelVersion?associationId={versionId}` |

Шаги вызываются **только** при наличии второго (опционального) файла «План» —
без него поведение из doc 110 без изменений (skip-warning `fmmodel_skipped_no_plan_file`).

---

## ✅ Правильная реализация

### Лист «План» — расширенный парсер

Раньше парсер брал только краевые периоды (`PeriodStart`/`PeriodEnd`). Теперь —
полный набор колонок + 3 категории по виду помещения. Раскладка эталонного шаблона
(`UBCFBE_01.04.2026_Журавли-1`):

```
        A                       B        C        D        E        F   ...
r3      Год                              2024                          (forward-fill)
r5      Квартал                  Сумма   1 кв     2 кв     3 кв     4 кв
r6      Площадь, кв.м                    2459.85  882.77   300      350     ← Amount квартир
r7      Стоимость 1 кв.м                 99065.03 100315   94000    94750   ← Cost квартир
r8      Сумма от продажи квартир         243685102 88555180 28200000 33162500 ← Summ квартир
r9      Площадь нежилых …                                                   ← Amount нежилых
r10     Стоимость 1 кв.м                                                    ← Cost нежилых
r11     Сумма от продажи нежил. помещений                                   ← Summ нежилых
r12     Колич-во м/м                                                        ← Amount м/м
r13     Стоимость 1 м/м                                                     ← Cost м/м
r14     Сумма от продажи м/м                                                ← Summ м/м
r15     ВСЕГО ВЫРУЧКА            ← игнорируется парсером
```

Парсер ([FinModelImportMapper.ReadPlanData](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs)):

1. Находит строки `Год` и `Квартал` в первых 15 строках столбца A (как в doc 110).
2. Сканирует колонки слева направо с C=3 и составляет список
   `FinModelPlanColumn(ColumnIndex, FmPeriod="{Year}Q{N}")` — forward-fill года.
3. Сканирует строки от `quarterRow+1` до `+60` в поисках Summ-якорей по правилу
   `contains("сумма") && contains("продаж") && contains(kind-marker)`:
   - `kind-marker="кварт"`  → `"Продажа квартиры (план)"`
   - `kind-marker="нежил"`  → `"Продажа нежилого помещения (план)"`
   - `kind-marker="м/м"` или `"машином"` → `"Продажа машиноместа (план)"`

   Для каждой найденной Summ-строки `AmountRow = SummRow - 2`, `CostRow = SummRow - 1`.
4. Материализует **все** точки `(Категория × Период) → (Summ, Amount, Cost)` —
   нулевые значения тоже эмитятся (план = 0 — валидно по требованию заказчика).

```csharp
internal sealed record FinModelPlanData(
    string PeriodStart,
    string PeriodEnd,
    IReadOnlyList<FinModelPlanColumn> Columns,
    IReadOnlyList<FinModelPlanCategory> Categories,
    IReadOnlyList<FinModelPlanInputDataPoint> InputDataPoints);
```

### Visary client

[VisaryMnemonics.cs](../Visary.Api.Client/Common/VisaryMnemonics.cs):
```csharp
public const string FmModelVersion = "fmmodelversion";
public const string InputData      = "inputdata";
public const string InputDataCode  = "inputdatacode";  // справочник в секции «Справочники»
```

[VisaryEntities.cs](../Visary.Api.Client/Dto/VisaryEntities.cs) — DTO `FmModelVersionRaw`,
`InputDataCodeRaw`, `InputDataRaw`.

[VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs):
```csharp
public sealed class FmModelVersionCreateRequest {
    public int FMModelID { get; set; }
    public VisaryRef FMModel { get; set; } = null!;
    public string Title { get; set; } = null!;  // "Версия - Перенос из Эксель"
}

public sealed class InputDataCreateRequest {
    public int FMModelVersionID { get; set; }
    public VisaryRef FMModelVersion { get; set; } = null!;
    public string FMPeriod { get; set; } = null!;          // "{Year}Q{N}"
    public VisaryRef Code { get; set; } = null!;           // {ID, Title} из inputdatacode
    public double Summ { get; set; }
    public double Amount { get; set; }
    public double Cost { get; set; }
    public double Percent { get; set; }                    // всегда 0 (контракт заказчика)
}
```

[CrudClient](../Visary.Api.Client/CRUD/CrudClient.cs):
- `CreateFmModelVersionAsync` — POST `/crud/fmmodelversion`.
- `CreateInputDataAsync` — POST `/crud/inputdata`.
- `LinkInputDataToVersionAsync(versionId, inputDataId)` — POST `listview/inputdata/onetomany/FMModelVersion?associationId={versionId}`
  с body `{Mnemonic:"inputdata", Filter:"[\"ID\",\"=\",{id}]", PageSkip:0, PageSize:1, Columns:[…14 полей…]}`.
  Колонки совпадают с HAR заказчика, минимальный набор сервер отбивал 400.

[ListViewClient](../Visary.Api.Client/ListView/ListViewClient.cs):
- `GetFmModelVersionsByModelAsync(fmModelId)` — POST `listview/fmmodelversion/onetomany/FMModel?associationId={fmModelId}`.
  Pre-check для идемпотентности.
- `GetInputDataByVersionAsync(versionId)` — POST `listview/inputdata/onetomany/FMModelVersion?associationId={versionId}`.
  Pre-check для дедупа (FMPeriod × Code.ID).
- `ListInputDataCodesAsync(titleFilter?)` — стандартный `listview/inputdatacode`
  через `ListDictionaryAsync<T>` (как для `companygroup`/`finishingmaterial`/…).

### Маппер ([FinModelImportMapper](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs))

Внутри `EnsureFmModelAsync` после успешного create/find `fmmodel` вызывается
`EnsureFmModelVersionAndInputDataAsync`:

1. **Резолв справочника** `inputdatacode` (Title → ID) — один listview за вызов.
   - Ошибка/недоступен → row-error `inputdata_codes_unavailable` + return.
2. **Pre-check версии** `GetFmModelVersionsByModelAsync(fmModelId)` → ищем по
   Title=«Версия - Перенос из Эксель», нашли → reuse; нет → `CreateFmModelVersionAsync`.
   - Ошибка → row-error `fmmodel_version_failed` + return.
3. **Pre-check входных данных** `GetInputDataByVersionAsync(versionId)` →
   `HashSet<(FMPeriod, Code.ID)>` для дедупа.
   - Ошибка → warning в лог, пустой HashSet (повторный POST даст дубликат,
     но «никто не загружен» хуже).
4. **Цикл по точкам** `InputDataPoints`:
   - Title нет в справочнике → копим в `missingCodeTitles`, в конце — row-error
     `inputdata_code_not_found` со списком всех ненайденных title.
   - Дубль `(period, codeId)` → skip без сообщения.
   - `CreateInputDataAsync` → `LinkInputDataToVersionAsync(versionId, newId)`.
   - На каждом из 2 шагов: ошибка → `failedCount++`, в конце — row-error
     `inputdata_create_failed`.

```csharp
// Готовая точка для POST /crud/inputdata
internal sealed record FinModelPlanInputDataPoint(
    string FmPeriod, string CodeTitle, double Summ, double Amount, double Cost);
```

---

## ⚠️ Важно

1. **`Percent` всегда `0`.** Контракт заказчика. Поле обязательное в payload-е.

2. **Нулевые периоды отправляются как есть.** «План = 0» — валидное значение
   (заказчик подтвердил: «не пропускать, записывать нулевые периоды»). Защита
   только от **дублей** (pre-check по (period, codeId)), не от нулей.

3. **`Code` резолвится через `listview/inputdatacode`, без хардкода ID.**
   Заказчик прислал ID:20 только для квартир — но ID-стек может различаться между
   стендами. Резолв за сессию (один listview) + словарь `Title → ID` case-insensitive.

4. **Title-якоря к Summ-строкам зашиты как контракт между файлом и кодом:**
   - «Продажа квартиры (план)»
   - «Продажа нежилого помещения (план)»
   - «Продажа машиноместа (план)»

   Если заказчик переименовал справочник на стенде — все 3 категории попадут
   в `missingCodeTitles` → row-error `inputdata_code_not_found` (видно какие Title
   ожидались). Сменить — в константах [FinModelImportMapper](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs):
   `InputDataCodeApartment` / `InputDataCodeNonResidential` / `InputDataCodeParking`.

5. **Материализация в парсере, не в маппере.** `XLWorkbook` закрывается в using
   парсера — после возврата чтение `sheet.Cell(…)` невозможно. Поэтому парсер
   собирает все точки **сразу**, до выхода из using-области, и возвращает
   материализованный список (см. doc 110 §6 «жизненный цикл ClosedXML», тот же
   паттерн что в `XlsxParser`).

6. **Идемпотентность каскадом** (с v1.3/v1.4):
   - `fmmodel` найдена по **(`Title`, `ABConstructionSiteID`, `PeriodStart`,
     `PeriodEnd`)** → skip POST. Изменился хоть один краевой период — это
     **другая** модель, создаём новую. См. v1.4.
   - `fmmodelversion` **всегда новая** с sequenced Title (v1.3). История переносов
     сохраняется в Visary, дубликаты не возможны.
   - `inputdata` создаются в свежей версии — pre-check внутри версии не нужен.

   Заказчик может удалить модель/версию в Visary вручную — следующий импорт
   создаст недостающее, ничего не сломав.

7. **`fmmodel` уже существует (skip).** В отличие от doc 110, где skip
   гарантировал отсутствие POST-ов, теперь даже при `fmmodel_skipped_already_exists`
   мы **продолжаем** в сторону версии и inputdata — каскад идемпотентен. Чтобы
   создать инпуты в уже существующую финмодель, ничего ручного делать не надо.

   ⚠️ Skip срабатывает **только** при совпадении периодов (v1.4). Если в Visary
   уже есть модель «Перенос из Эксель» на тот же сайт, но с другими `PeriodStart`/
   `PeriodEnd` — это другая модель, импорт создаст новую `fmmodel` и привяжет
   `inputdata` к ней. См. v1.4 / тест `ApplyAsync_PlanFile_ExistingFmModelWithDifferentPeriod_CreatesNew`.

8. **Link-запрос — это не «привязка», а «register-by-filter».** HAR заказчика
   присылает body с `Filter ["ID","=",newId]`. Семантически: «эта inputdata теперь
   фигурирует в наборе onetomany-связки FMModelVersion=versionId». Body — точный
   набор полей из HAR (14 колонок), минимальный — 400 от сервера.

9. **`Amount`/`Cost` для м/м.** Файл шлёт «Колич-во м/м» (шт.) и «Стоимость 1 м/м» (₽).
   Те же поля `Amount`/`Cost` без преобразования — Visary интерпретирует их по
   справочнику Code. Семантика «штука vs квадратный метр» — на стороне Visary.

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО — вернуть Sheet наружу из парсера.
internal sealed record FinModelPlanData(
    string PeriodStart, string PeriodEnd,
    IXLWorksheet Sheet);  // 💥 после using-блока в ReadPlanDataFromBytes — UAF

// Правильно — материализовать точки внутри парсера до выхода из using.
internal sealed record FinModelPlanData(
    ..., IReadOnlyList<FinModelPlanInputDataPoint> InputDataPoints);
```

```csharp
// НЕПРАВИЛЬНО — пропускать нулевые периоды.
foreach (var point in planData.InputDataPoints)
{
    if (point.Summ == 0 && point.Amount == 0) continue;  // 💥 заказчик: «записывать»
    await CreateInputDataAsync(...);
}
```

```csharp
// НЕПРАВИЛЬНО — хардкодить ID:20 для всех квартир.
Code = new VisaryRef { ID = 20, Title = "Продажа квартиры (план)" }  // 💥 не работает на других стендах
// Правильно — резолвить через ListInputDataCodesAsync за сессию.
```

```csharp
// НЕПРАВИЛЬНО — пропускать pre-check inputdata-by-version.
await CreateInputDataAsync(...);  // 💥 повторный импорт = N дублей у одной версии
// Правильно — pre-check + HashSet<(period, codeId)> для дедупа.
```

```csharp
// НЕПРАВИЛЬНО — при ошибке link продолжать как ни в чём ни бывало.
try { await LinkInputDataToVersionAsync(versionId, id, ct); }
catch { /* 💥 inputdata создана, но не привязана — orphan */ }
// Правильно — failedCount++ + single row-error в конце.
```

```csharp
// НЕПРАВИЛЬНО (до v1.4) — pre-check fmmodel без PeriodStart/PeriodEnd.
var existing = await _listViewClient.FindFmModelsAsync(projectId, siteId, ct);
// 💥 На сайте уже есть чужая 2024-only fmmodel → реюзается, и inputdata 2023-2027
//    из нового файла ложатся как версия чужой модели. Заказчик не разберётся.

// Правильно — передаём краевые периоды парсера в фильтр.
var existing = await _listViewClient.FindFmModelsAsync(
    projectId, siteId, planData.PeriodStart, planData.PeriodEnd, ct);
```

---

## 📍 Применение в проекте

| Слой | Файл | Метод/блок |
|------|------|------------|
| Мнемоники | `Visary.Api.Client/Common/VisaryMnemonics.cs` | `FmModelVersion`, `InputData`, `InputDataCode` |
| DTO (Raw) | `Visary.Api.Client/Dto/VisaryEntities.cs` | `FmModelVersionRaw`, `InputDataRaw`, `InputDataCodeRaw` |
| DTO (Create) | `Visary.Api.Client/Dto/VisaryCrudRequests.cs` | `FmModelVersionCreateRequest`, `InputDataCreateRequest` |
| CRUD | `Visary.Api.Client/CRUD/CrudClient.cs` | `CreateFmModelVersionAsync`, `CreateInputDataAsync`, `LinkInputDataToVersionAsync` |
| ListView | `Visary.Api.Client/ListView/ListViewClient.cs` | `GetFmModelVersionsByModelAsync`, `GetInputDataByVersionAsync`, `ListInputDataCodesAsync` |
| Парсер | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs` | `ReadPlanData`, `FinModelPlanData`, `FinModelPlanInputDataPoint`, `FinModelPlanCategory` |
| Маппер | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs` | `EnsureFmModelVersionAndInputDataAsync` (вызов из конца `EnsureFmModelAsync`) |
| Константы | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs` | `FmModelVersionTitlePrefix`, `BuildNextVersionTitle`, `InputDataCodeApartment`/`…NonResidential`/`…Storeroom`/`…Parking` |
| Тесты (парсер) | `KiloImportService.Api.Tests/Mapping/FinModelInputDataTests.cs` | `ReadPlanData_Reference_FindsThreeCategories_AndAllPeriods` |
| Тесты (Apply) | `KiloImportService.Api.Tests/Mapping/FinModelInputDataTests.cs` | happy / version-reuse / inputdata-dedup / codes-unavailable / partial-codes (5 тестов) |

---

## 🎯 Чек-лист

- [ ] Без второго файла → старое поведение `fmmodel_skipped_no_plan_file` (новые шаги не вызываются)
- [ ] `fmmodel` создан → POST `/crud/fmmodelversion` с Title «Версия - Перенос из Эксель»
- [ ] Версия уже есть с тем же Title → reuse, POST не отправляется
- [ ] `listview/inputdatacode` возвращает 3 нужных Title → они подставляются в payload как `{ID, Title}`
- [ ] `listview/inputdatacode` упал → row-error `inputdata_codes_unavailable`, ни версии, ни inputdata не создаём
- [ ] Точка `(period, codeId)` уже есть в версии → skip без error/warning
- [ ] Title из файла нет в справочнике → row-error `inputdata_code_not_found` со списком, остальные категории идут
- [ ] Каждая созданная inputdata линкуется POST `listview/inputdata/onetomany/FMModelVersion`
- [ ] `Percent=0` всегда; нулевые `Summ/Amount/Cost` тоже отправляются
- [ ] Все тесты `FinModel*` (110+ старых + 6 новых) — зелёные

---

## 📅 История изменений

- **v1.4 (2026-05-26)** — `fmmodel` pre-check теперь фильтрует по
  **(`Title`, `ABConstructionSiteID`, `PeriodStart`, `PeriodEnd`)**.

  **Инцидент** — файл «Репино-Парк» (план с 1кв.2023 по 4кв.2027). На объекте
  уже была чужая `fmmodel` с периодом `2024Q1..2024Q4`. Pre-check матчил её по
  `(Title, ABConstructionSiteID)`, реюзал ID → `inputdata` 2023-2027 ложились
  как **новая версия чужой 2024-only-модели**. Заказчик увидел в Visary одну
  модель «Перенос из Эксель» с двумя несовместимыми версиями.

  **Контракт фильтра `FindFmModelsAsync`** в [ListViewClient.cs:1018-1080](../Visary.Api.Client/ListView/ListViewClient.cs#L1018-L1080):
  ```json
  Filter = [
    ["Title", "contains", "Модель из эксель файла"],
    "and", ["ABConstructionSiteID", "=", siteId],
    "and", ["PeriodStart", "=", "2023Q1"],
    "and", ["PeriodEnd",   "=", "2027Q4"]
  ]
  ```

  Сигнатура расширена:
  ```csharp
  Task<ListViewResponse<FmModelRaw>> FindFmModelsAsync(
      int abProjectId, int abConstructionSiteId,
      string? periodStart, string? periodEnd, CancellationToken ct = default);
  ```

  `periodStart`/`periodEnd` опциональные (null → условие не добавляется в фильтр).
  В `EnsureFmModelAsync` всегда передаём `planData.PeriodStart`/`PeriodEnd` —
  они уже посчитаны парсером «Общий график».

  **Семантика**: один сайт может содержать **несколько** Финмоделей с разными
  краевыми периодами. Это законно — заказчик заводит модели на разные диапазоны
  лет (2023-2027 и 2024-2027 — РАЗНЫЕ модели). Pre-check возвращает 1 запись
  только когда период **в точности** совпадает.

  **Регрессионный тест** `ApplyAsync_PlanFile_ExistingFmModelWithDifferentPeriod_CreatesNew`
  в [FinModelFmModelTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelFmModelTests.cs):
  на сайте есть 2024Q1..2024Q2-модель, файл 2023Q1..2024Q2 → pre-check
  возвращает пусто, `CreateFmModelAsync` дёрнут с новыми краевыми.

  Обновлены мок-сетапы: `FinModelFmModelTests.cs:51`, `FinModelInputDataTests.cs:61`,
  `ProjectsCacheServiceTests.cs:339` — добавлены два `It.IsAny<string?>()`
  в `.Setup(c => c.FindFmModelsAsync(…))`.

- **v1.3 (2026-05-26)** — поддержка второй раскладки листа «Общий график»
  (Репино-Парк) **+** каскадно создаваемая **новая версия** на каждый импорт.

  **Layout-1 (Репино-Парк)** — таблицы оформлены шапкой:
  ```
  r2  A=НПС                B=504
  r3  A=Этап               B=1
  r4  A=Тип помещения      B=Квартиры        ← категория из шапки!
  r5  A=Год                C=2023  G=2024  K=2025  …
  r6  (помесячная подшапка — дублирующие года в C/D…)
  r7  A=Квартал   B=Сумма  C=1 кв  D=2 кв  …
  r8  A=План
  r9  A=Площадь, кв.м       ← Amount-строка с ОБЩИМ текстом (без маркера категории)
  r10 A=Стоимость 1 кв.м    ← Cost
  r11 A=Общая сумма          ← Summ
  ```
  Парсер `ReadGeneralScheduleDataFromBytes` теперь:
  - Допускает **1 промежуточную строку** между «Год» и «Квартал» (помесячная подшапка).
  - Перед обработкой таблицы сканирует **≤5 строк ВЫШЕ** «Год» в поисках A=«Тип
    помещения». Найдено — категория резолвится из B/C/D/E-колонки той же строки;
    Amount-строкой становится **первая** содержательная (skip «План»/stop «Факт»/«накопл»).
  - Layout-2 (Журавли, тестовый фикстур) — без изменений: шапки нет, категория
    зашита в A-текст Amount-строки.

  **`fmmodelversion` — всегда новая.** Раньше pre-check по Title переиспользовал
  существующую версию; теперь — каждый импорт создаёт версию с уникальным Title
  (`Версия - Перенос из Эксель`, `Версия - Перенос из Эксель 2`, … N+1). Логика
  в `BuildNextVersionTitle(existing)`:
  - 0 совпадающих → ровно префикс.
  - 1 совпадающее с ровно префиксом → префикс + ` 2`.
  - Среди существующих ищется max sequence N (где базовый Title = #1, «… 2» = #2…),
    новый Title — N+1.

  Pre-check `GetInputDataByVersionAsync` пропущен — новая версия заведомо пустая,
  дедуп `(period, codeId)` теряет смысл (дубликаты могут быть только в той же
  версии). Заказчик хочет историю импортов: «можно создать вторую версию с новыми
  данными» — Visary UI показывает все версии в дереве Финмодели.

  Обновлены тесты:
  - `ApplyAsync_ExistingVersion_Reused_NoCreateVersion` → переименован в
    `ApplyAsync_ExistingVersion_CreatesSecondVersion_WithSequencedTitle` (проверяет
    Title=`Версия - Перенос из Эксель 2`).
  - `ApplyAsync_DuplicateInputData_SkippedByPrecheck` → заменён на
    `ApplyAsync_RepeatedImport_AlwaysCreatesNewVersion_NoDedupAtPointLevel`
    (с двумя существующими версиями новая = `… 3`, все 12 точек создаются заново).
  - `ReadGeneralScheduleData_Layout1_ResolvesCategoryFromHeader_AndSkipsMonthsRow` —
    4 категории по эталонной раскладке Репино-Парк.
  - 4 unit-теста `BuildNextVersionTitle` (пустой / базовый / 1+2+5 / только посторонние).

- **v1.2 (2026-05-26)** — источник данных для inputdata изменился с листа «План»
  на лист **«Общий график»**. На нём заказчик размещает **несколько таблиц** (одна
  таблица на вид помещения), каждая структуры:
  - r=«Год», r+1=«Квартал»/«Сумма» — заголовок;
  - r+2=«План» (маркер);
  - r+3=Площадь/Колич-во (Amount) — A-текст содержит имя категории (квартиры/нежилые/кладовки/м/м);
  - r+4=Стоимость 1 ед. (Cost) — A-текст может быть `#GETTING_DATA` после сбоя формулы;
  - r+5=Доход (Summ);
  - r+6=«Доход накопл. Итогом» — **skip**;
  - r+7=«Факт» маркер, r+8..r+11 — фактические данные (skip).

  Парсер берёт **только первые 3 строки данных** (План), факт-блок игнорируется.
  Резолв категории — по тексту в Amount-строке (Площадь, а не Summ как раньше).
  Удалены `ReadPlanData`/`ReadPlanPeriods`, добавлен `ReadGeneralScheduleData`.

  **Также фикс ParseQuarter**: «10»/«11»/«12» (месяцы из помесячной таблицы,
  соседствующей на том же листе) теперь возвращают `null`, а не `1`. Без этого
  парсер засасывал помесячные данные как Q1.

- **v1.1 (2026-05-26)** — справочник кодов оказался `fmcode` (а не `inputdatacode` —
  404 на стенде). Контракт `listview/fmcode` ⚠️ отличается от обычного
  `ListDictionaryAsync`: `Filter`/`Sorts` — JSON-string, 14 колонок, обязательная
  сортировка `[{"selector":"Code","desc":false}]` (`SortsNullSentinel="null"` — 400).
  Резолв изменён на per-title точечные запросы (3–4 на категорию) вместо одного
  большого. Title-якоря по эталону Visary:
  - Квартиры: `"Продажа квартиры (план)"` (без изменений)
  - **Нежилые (ПСН): `"Продажа нежилые ( ком) ПСН (план)"`** ⚠️ внимание на двойной
    пробел после `(` — точное написание, без него Title не находится
  - **+ новая категория «иные нежилые (кладовки)»: `"Продажа иные нежилые (кладовки) (план)"`**
  - Машиноместа: `"Продажа м/м (план)"` (короткое написание)

  Парсер «План» теперь распознаёт кладовки **до** общих нежилых (приоритет
  `contains("кладов") || contains("иные нежил")` перед `contains("нежил")`).
  Удалены: мнемоника `InputDataCode`, DTO `InputDataCodeRaw`, метод `ListInputDataCodesAsync`.

## 🔗 Связанная документация

- [doc 110 — finmodel-plan-and-fmmodel](./110-finmodel-plan-and-fmmodel.md) — базовое создание `fmmodel` + парсер «План» (краевые периоды).
- [doc 56 — visary-dto-deserialization-pitfalls](./56-visary-dto-deserialization-pitfalls.md) — `JsonElement?` для variant-полей.
- [doc 100 — finmodel-companygroup-link](./100-finmodel-companygroup-link.md) — пример резолва справочника через listview.
- [doc 109 — finmodel-prechecks-wbs-and-gf](./109-finmodel-prechecks-wbs-and-gf.md) — паттерн идемпотентности через pre-check.
