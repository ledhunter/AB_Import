# 📅 Финмодель → dealmonthlydata (Инвестиционный кредит: Этап 1)

## 📋 Описание

Доработка doc 139/141: после создания процентных ставок сделки
(`dealpercentbet`) Финмодель создаёт **ОДНУ** запись `dealmonthlydata` на
(Deal, ТекущийГод, ТекущийМесяц) с 5 числовыми полями, прочитанными из
раздела **«Инвестиционный кредит: Этап 1»** листа Outputs основного файла
«Параметры».

| # | Что делает | Endpoint Visary |
|---|------------|-----------------|
| 1 | Резолвит сделку (по «Номер КД» Control → `listview/deal`) — переиспользует deal, найденный для `dealpercentbet` | `POST /api/visary/listview/deal` |
| 2 | Парсит Outputs: «Факт»-колонка + якорь «Инвестиционный кредит» + 5 целевых строк | — |
| 3 | POST одной записи `dealmonthlydata` | `POST /api/visary/crud/dealmonthlydata` |

---

## ✅ Правильная реализация

### Источник данных

Лист **Outputs**, раздел **«Инвестиционный кредит: Этап 1»**:

```
              <----- Факт ------>
                       2026
Инвестиционный кредит: Этап 1
  Привлечение ОД                      млн руб.       1 990
  Капитализация процентов в тело долга млн руб.      #ССЫЛКА!
  Погашение тела долга                млн руб.       —
  Погашение процентных выплат         млн руб.       #ССЫЛКА!
  Эффективная ставка по проекту       %              …
  …
  Проценты начисленные                млн руб.       506
  Расчет процентов по капитализации   млн руб.       —
  …
```

Колонка «Факт» — общая для всего листа Outputs (та же, что для Fact-блока
InputData, см. [doc 126](./126-finmodel-fact-inputdata-from-outputs.md)).
Единица измерения находится в колонке между лейблом и Фактом, конкретно в
ячейке с текстом «руб» (любой формы: «млн руб.», «тыс. руб.», «руб»).

### Маппинг лейблов → поля Visary

| Лейбл Excel | Поле `dealmonthlydata` |
|-------------|------------------------|
| Привлечение ОД | `PrincipalDebtAmount` |
| Проценты начисленные | `SimpleInterestAmount` |
| Расчет процентов по капитализации | `CapitalizedInterestAmount` |
| Погашение тела долга | `PrincipalRepaymentAmount` |
| Погашение процентных выплат | `InterestRepaymentAmount` |

Матч по `Contains` (case-insensitive, после Trim) — устойчив к ведущим
пробелам, дополнительным частям лейбла («Привлечение ОД (по графику)» тоже
сматчится).

### Единица измерения → множитель

| Текст ячейки (case-insensitive contains) | Множитель |
|------------------------------------------|-----------|
| `млн` | × 1 000 000 |
| `тыс` | × 1 000 |
| `руб` (или пусто / не «руб») | × 1 |

```csharp
internal static double GetUnitMultiplier(string? unitText)
{
    if (string.IsNullOrWhiteSpace(unitText)) return 1d;
    var s = unitText.ToLowerInvariant();
    if (s.Contains("млн", StringComparison.Ordinal))  return 1_000_000d;
    if (s.Contains("тыс", StringComparison.Ordinal))  return 1_000d;
    return 1d;
}
```

### Пустая ячейка / прочерк / 0 → 0

Заказчик: «Если значение будет 0, пусто или прочерк "-", тогда указываем 0».
Парсер `TryReadFactNumber` возвращает 0 для:

- пустой ячейки;
- любой из форм прочерка: `-`, `—`, `–`, `−`;
- нечисловой строки.

Это не пишет «пустое» поле, а отправляет `0.0` — Visary принимает.

### Payload Visary

```json
POST /api/visary/crud/dealmonthlydata
{
  "Deal":   { "ID": 91 },
  "Year":   2025,
  "Month":  4,
  "PrincipalDebtAmount":         1,
  "SimpleInterestAmount":        2,
  "CapitalizedInterestAmount":   3,
  "PrincipalRepaymentAmount":    4,
  "InterestRepaymentAmount":     5
}
```

- `Deal.ID` — та же сделка, что для процентных ставок
  (см. [doc 139 v1.4](./139-finmodel-installments-and-conclusion.md#v14)).
  Если шаг ставок не нашёл сделку (КД отсутствует / в чужом проекте /
  несколько) — шаг помесячных данных пропускается тихо.
- `Year` = текущий год (DateTime.Now.Year).
- `Month` = текущий месяц (DateTime.Now.Month).

### Интеграция в `EnsureProjectAuditAndInstallmentsAsync`

```csharp
// 3.5) Процентные ставки сделки — возвращают Deal.ID (или null).
var resolvedDealId = await EnsureDealPercentBetsAsync(
    projectId, siteId, primaryFilePath, errors, synthetic, ct);

// 3.6) Помесячные данные — той же сделки.
if (resolvedDealId is { } dealIdForMonthly)
{
    await EnsureDealMonthlyDataAsync(
        dealIdForMonthly, siteId, primaryFilePath, errors, synthetic, ct);
}

// 4) POST projectaudit, далее dataforfm и т.д. — как раньше.
```

`EnsureDealPercentBetsAsync` рефакторен с `Task` на `Task<int?>`:

- `null` если КД нет, сделка не найдена, несколько сделок, чужой проект,
  parsing-fail;
- `deal.ID` если сделка однозначно резолвится в нашем проекте — даже
  если ставок в файле нет (`fin.Rates.Count == 0`).

---

## ⚠️ Важно

1. **Идемпотентности нет.** Каждый импорт создаёт новую запись
   `dealmonthlydata` — так же, как `projectaudit` и `dealpercentbet`
   (см. doc 139 v1.4). Если потребуется — добавить pre-check по
   `(Deal, Year, Month)` через listview.

2. **`Year`/`Month` = текущий момент**, а не дата из Excel. Заказчик:
   «Year — текущий год, Month — текущий месяц». Используем `DateTime.Now`
   (локальное время, не UTC — Visary работает в TZ заказчика).

3. **Шаг не блокирует Заключение.** Любая ошибка парсинга / POST'а →
   row-error + skip. POST `projectaudit` и далее идут как обычно.

4. **Якорь раздела — `Contains("Инвестиционный кредит")`.** Так ловятся
   варианты «Инвестиционный кредит: Этап 1», «Инвестиционный кредит. Этап 1»,
   «Инвестиционный кредит (Этап 1)».

5. **«Факт»-колонка — глобальная по листу Outputs.** Та же, что Fact-блок
   InputData (см. doc 126). Парсер ищет ПЕРВУЮ ячейку «Факт» (raw + formatted
   для custom number format `[=0]"Факт";[<>0]"Прогноз"`).

6. **Единица измерения опциональна.** Если в строке нет ячейки с «руб» — берём
   множитель 1 (как будто значение уже в рублях).

7. **Capitalized field на скрине имеет несовпадение терминологии**: лейбл
   «Капитализация процентов в тело долга» (наверху) ≠ «Расчет процентов
   по капитализации» (внизу). Маппинг к `CapitalizedInterestAmount` — по
   ВТОРОМУ лейблу (на скрине именно он соответствует расчёту процентов).
   Если заказчик попросит маппить первый лейбл — добавить в
   `InvestmentCreditFieldMap`.

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО — игнорировать единицу измерения.
var rawValue = TryReadFactNumber(sheet, row, factCol);
return new InvestmentCreditMonthlyData(rawValue, ...); // 💥 «1 990» вместо 1_990_000_000
// Правильно — найти ячейку с «руб» в этой же строке и применить множитель.
```

```csharp
// НЕПРАВИЛЬНО — резолвить deal отдельно от шага ставок.
var deal = await ResolveDealByKd(kd); // 💥 второй раунд listview/deal
var rates = ResolveRates(...);
await CreateDealPercentBetsFor(deal, rates);
await CreateDealMonthlyDataFor(deal);
// Правильно — EnsureDealPercentBetsAsync возвращает Deal.ID, мы передаём
// его в EnsureDealMonthlyDataAsync. Один listview/deal на оба шага.
```

```csharp
// НЕПРАВИЛЬНО — брать год/месяц из Excel.
var year = ReadCell(sheet, factRow + 1, factCol); // 💥 заказчик хочет «текущий»
// Правильно — DateTime.Now.Year / DateTime.Now.Month.
```

```csharp
// НЕПРАВИЛЬНО — пропускать поле если 0 или «—».
if (rawValue == 0 || isDash) continue; // 💥 Visary получит payload без поля
// Правильно — отправлять 0 (заказчик: «0, пусто или прочерк → 0»).
```

---

## 📍 Применение в проекте

| Слой | Файл | Метод/символ |
|------|------|--------------|
| Мнемоника | `Visary.Api.Client/Common/VisaryMnemonics.cs` | `DealMonthlyData = "dealmonthlydata"` |
| DTO (Raw) | `Visary.Api.Client/Dto/VisaryEntities.cs` | `DealMonthlyDataRaw` (9 полей с ID) |
| Create request | `Visary.Api.Client/Dto/VisaryCrudRequests.cs` | `DealMonthlyDataCreateRequest` |
| CRUD | `Visary.Api.Client/CRUD/CrudClient.cs` | `CreateDealMonthlyDataAsync` |
| Парсер | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.Installments.cs` | `ReadInvestmentCreditMonthlyData(Stream)`, `ReadInvestmentCreditMonthlyDataFromSheet(IXLWorksheet)` |
| Helpers | там же | `GetUnitMultiplier`, `TryReadFactNumber`, `FindOutputsFactCell`, `InvestmentCreditFieldMap` |
| Оркестратор | там же | `EnsureDealMonthlyDataAsync` (вызов из `EnsureProjectAuditAndInstallmentsAsync` после `EnsureDealPercentBetsAsync`) |
| Рефактор | там же | `EnsureDealPercentBetsAsync` → `Task<int?>` (возвращает Deal.ID) |
| Тесты | `KiloImportService.Api.Tests/Mapping/FinModelInstallmentsTests.cs` | 8 новых: `ReadInvestmentCreditMonthlyData_*` (reference / тыс / руб / нет листа / нет Fact / нет якоря / прочерк-пусто-ноль) + `GetUnitMultiplier_VariousFormats_ReturnsExpected` Theory (10 кейсов) |

---

## 🎯 Чек-лист

- [ ] На листе Outputs есть колонка «Факт» (raw или formatted custom number
      format) — иначе шаг тихо пропускается.
- [ ] Под Fact-ячейкой найден якорь «Инвестиционный кредит» (Contains-матч).
- [ ] Каждое из 5 целевых полей читается из Fact-колонки на пересечении со
      строкой лейбла.
- [ ] Единица измерения берётся из любой ячейки между label и Fact, где
      есть «руб»; множитель: млн×1 000 000, тыс×1 000, руб×1.
- [ ] Пусто / `-` / `—` / `–` / `0` → 0.
- [ ] `Year`/`Month` — текущие (DateTime.Now).
- [ ] `Deal.ID` — тот же, что для `dealpercentbet`. Если шаг ставок не нашёл
      сделку — шаг помесячных данных пропускается тихо.
- [ ] Все 66 тестов `FinModelInstallmentsTests` зелёные.
- [ ] Полный suite (без VisaryLive) — 470/470.

---

## 📅 История изменений

- **v1.0 (2026-06-19)** — первая реализация. Добавлены мнемоника, Raw, Create
  request, CRUD-метод, парсер раздела «Инвестиционный кредит: Этап 1» с
  unit-мультипликатором и оркестратор `EnsureDealMonthlyDataAsync`. Шаг
  ставок (`EnsureDealPercentBetsAsync`) рефакторен с `Task` на `Task<int?>`,
  чтобы один listview/deal-поиск обслуживал оба шага.

## 🔗 Связанная документация

- [doc 139 — Заключение БП7 + рассрочки + ставки](./139-finmodel-installments-and-conclusion.md) —
  базовая doc; шаг ставок (v1.4) предоставляет `Deal.ID` для doc 142.
- [doc 141 — dataforfm без Indicator + якорь «Инвестиционные кредиты»](./141-dataforfm-without-indicator-and-investment-credits-anchor.md) —
  предыдущая итерация по тому же оркестратору.
- [doc 126 — fact-inputdata-from-outputs](./126-finmodel-fact-inputdata-from-outputs.md) —
  парсер Fact-колонки Outputs (та же логика поиска «Факт»-ячейки).
- [doc 105 — control-value-ref](./105-control-value-ref.md) — паттерн чтения
  скаляра с управляющего листа (используется для «Номер КД»).
