# 🪙 Финмодель → `dealpercentbet.PercentKind` не отправляем + ИСР-pre-check блокирует Бюджет И ГФ

## 📋 Описание

Три правки контракта импорта Финмодели по запросам заказчика 2026-06-22:

1. **Поле `PercentKind` у `dealpercentbet` не отправляется в Visary.** «Вид
   ставки» (Floating/Fixed) Visary определяет сам по типу ставки
   `PercentBetType`; импорт не должен его проставлять. `Rate` отправляется
   по прежней логике (как в [doc 139 v1.4](./139-finmodel-installments-and-conclusion.md)).

2. **Pre-check 1 (ИСР) теперь блокирует и Бюджет, и ГФ.** Если в ИСР объекта
   уже есть WBS-узлы — импорт целиком пропускает оба шага. Заказчик считает
   объект «уже сформированным» и не хочет дополнять чужую ИСР повторным
   импортом. Раньше (см. [doc 109](./109-finmodel-prechecks-wbs-and-gf.md))
   `budgetUploadOk` ставился в `true` и ГФ пытался дополнить существующие
   статьи — теперь нет.

3. **ГФ Главы 1 запускается ТОЛЬКО при `budgetUploadOk == true`** (как
   v1.0 doc 144). Все остальные случаи (нет бюджета в файле, заливка упала,
   ИСР уже есть) блокируют ГФ. Новое условие `!= true`, не `== false`.

---

## ✅ Правильная реализация

### 1. `PercentKind` убран из payload, `Rate` остаётся

[VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs)
— `DealPercentBetCreateRequest`:

```csharp
public sealed class DealPercentBetCreateRequest
{
    public int DealID { get; set; }
    public VisaryRef Deal { get; set; } = null!;
    // PercentKind НЕ отправляем — Visary определяет «Вид ставки» сам.
    public string LmID { get; set; } = null!;
    public double Rate { get; set; }                 // ← остался
    public VisaryRef PercentBetType { get; set; } = null!;
}
```

[FinModelImportMapper.Installments.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.Installments.cs):

```csharp
var created = await _visaryClient.CreateDealPercentBetAsync(new DealPercentBetCreateRequest
{
    DealID = deal.ID,
    Deal = new VisaryRef { ID = deal.ID },
    LmID = lmId,
    Rate = rate.Rate,
    PercentBetType = new VisaryRef { ID = betType.ID, Title = betType.Title },
}, ct);
```

Парсер `ReadFinancingRates` и поле `EnabledFinancingRate.PercentKind`
оставлены без изменений — `FinancingPercentKindByCode` всё ещё используется
для валидации LM-кода (если код не из словаря — строка не считается
ставкой). Просто значение Kind не попадает в payload.

### 2. ИСР-pre-check блокирует оба шага

[FinModelImportMapper.ApplyAsync](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs)
— блок `if (budgetRows.Count > 0)`:

```csharp
bool? budgetUploadOk = null;
if (budgetRows.Count > 0)
{
    var wbsExists = await WbsAlreadyExistsForSiteAsync(siteId, errors, ct);
    if (wbsExists is null)
    {
        // listview/wbs упал — не безопасно ничего трогать.
        budgetUploadOk = false;
    }
    else if (wbsExists.Value)
    {
        // ИСР уже сформирована — пропускаем И заливку Бюджета, И ГФ.
        errors.Add(new RowError(null, "budget_upload_skipped_wbs_exists",
            "Импорт бюджета в Visary пропущен: ИСР объекта строительства уже " +
            "сформирована (есть WBS-узлы). " +
            (schedulePending
                ? "ГФ Главы 1 также пропущен — заказчик не дополняет уже сформированную ИСР."
                : "ГФ Главы 1 не запрашивался.")));
        // budgetUploadOk ОСТАЁТСЯ null → правило `!= true` ниже блокирует ГФ.
    }
    else
    {
        budgetUploadOk = await UploadBudgetToVisaryAsync(...);
        if (budgetUploadOk.Value) applied += budgetRows.Count;
    }
}
```

### 3. ГФ запускается только при `budgetUploadOk == true`

```csharp
if (scheduleArticleRows.Count > 0 && scheduleQuartersRow is not null)
{
    if (budgetUploadOk != true)
    {
        if (budgetRows.Count == 0)
        {
            // Нет бюджета в файле — отдельный info-код для отчёта.
            errors.Add(new RowError(null, "schedule_skipped_no_budget",
                "ГФ Главы 1 не создан: в файле нет данных бюджета — " +
                "импорт ГФ выполняется только когда бюджет тоже импортируется."));
        }
        // ИСР-exists и upload-failed уже описаны в budget_upload_* — отдельную метку не эмитим.
    }
    else
    {
        var scheduleApply = await ApplyChapter1ScheduleAsync(...);
        applied += scheduleApply;
    }
}
```

Матрица решений:

| `budgetRows` | Pre-check (WBS) | upload | `budgetUploadOk` | Бюджет | ГФ | Метка |
|---|---|---|---|---|---|---|
| 0 | — | — | `null` | — | skip | `schedule_skipped_no_budget` (info) |
| > 0 | listview упал | — | `false` | skip | skip | `budget_upload_precheck_failed` |
| > 0 | WBS уже есть | — | `null` | skip | skip | `budget_upload_skipped_wbs_exists` (warning) |
| > 0 | WBS пуст | success | `true` | uploaded | run | — |
| > 0 | WBS пуст | failed | `false` | skip | skip | `budget_upload_failed` / `budget_upload_timeout` / `budget_upload_error` |

---

## ⚠️ Важно

1. **`Rate` НЕ удалён** — отправляется как раньше из ячейки «Этап 1» Excel
   (число/процент/ведущая цифра текста-флага).

2. **`PercentKind` остался внутри парсера** — `EnabledFinancingRate.PercentKind`
   и словарь `FinancingPercentKindByCode` нужны для валидации LM-кода
   (`TryGetValue(code, out kind)`: если код не из словаря — это не строка-ставка,
   пропускаем). В payload `PercentKind` не идёт — Visary вычисляет «Вид
   ставки» сам по `PercentBetType`.

3. **`DealPercentBetRaw.PercentKind` ОСТАВЛЕН** — Visary в ответе на
   listview/GET может вернуть существующее значение. Удалять Raw-свойство
   не нужно.

4. **WBS-exists случай теперь оставляет `budgetUploadOk == null`** (не `true`,
   как раньше). Это правильно — Бюджет в этой сессии не импортирован,
   downstream-правило `!= true` корректно блокирует ГФ.

5. **`budget_upload_skipped_wbs_exists` теперь рендерится как `warning`,
   а не `info`** — заказчик должен видеть, что импорт пропущен из-за
   состояния объекта (это не «нормальная» ситуация, а сигнал, что
   повторный импорт ничего не сделал).

6. **`schedule_skipped_no_budget` — единственный новый info-код шага ГФ.**
   Эмитим ТОЛЬКО когда `budgetRows.Count == 0` (бюджета вовсе нет в файле).
   В кейсах WBS-exists и upload-failed избегаем дублирования — причина
   уже описана в `budget_upload_*`.

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО (v1.0 doc 144) — удалить Rate из payload.
new DealPercentBetCreateRequest { ... /* без Rate */ };
// 💥 Заказчик откатил это решение: Rate отправляем как раньше (опечатка была
// в v1.0 doc 144 — имелся в виду PercentKind, а не Rate).
```

```csharp
// НЕПРАВИЛЬНО — оставить PercentKind в payload.
new DealPercentBetCreateRequest { ..., PercentKind = 10, ... };
// 💥 Visary вычисляет «Вид ставки» сам по PercentBetType; ручная установка
// PercentKind ломает классификацию (на скриншоте заказчика — Floating/Fixed
// определялись правильно ТОЛЬКО когда PercentKind не передавался).
```

```csharp
// НЕПРАВИЛЬНО — оставить старое поведение «WBS exists → budgetUploadOk=true,
// ГФ дополняет существующую ИСР».
else if (wbsExists.Value)
{
    budgetUploadOk = true; // 💥 ГФ запустится и попытается дописать CostItem'ы
                           //   в чужую/ручную ИСР — заказчик этого не хочет.
}
// Правильно (doc 144 v1.1) — оставить budgetUploadOk == null; downstream
// правило `!= true` заблокирует ГФ автоматически.
```

```csharp
// НЕПРАВИЛЬНО — эмитить schedule_skipped_no_budget и в кейсе WBS-exists.
if (budgetUploadOk != true)
{
    errors.Add(new RowError(null, "schedule_skipped_no_budget", ...));
    // 💥 Пользователь увидит две метки про одно и то же: budget_upload_skipped_wbs_exists
    //   («ГФ также пропущен») + schedule_skipped_no_budget («в файле нет бюджета»).
}
// Правильно — проверять budgetRows.Count == 0 перед эмитом, чтобы дублирование
// не возникало.
```

---

## 📍 Применение в проекте

| Слой | Файл | Метод/символ |
|------|------|--------------|
| DTO | [Visary.Api.Client/Dto/VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs) | `DealPercentBetCreateRequest` (без `PercentKind`, с `Rate`) |
| CRUD | [Visary.Api.Client/CRUD/CrudClient.cs](../Visary.Api.Client/CRUD/CrudClient.cs) | `CreateDealPercentBetAsync` (логи без `percentKind=`) |
| Мапер ставок | [KiloImportService.Api/Domain/Mapping/FinModelImportMapper.Installments.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.Installments.cs) | `EnsureDealPercentBetsAsync` (payload без `PercentKind`) |
| Apply pipeline | [KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) | блок `if (budgetRows.Count > 0)` + блок ГФ |
| Severity | [KiloImportService.Api/Controllers/ImportsController.cs](../KiloImportService.Api/Controllers/ImportsController.cs) | `ResolveErrorSeverity`: `budget_upload_skipped_wbs_exists`=warning, `schedule_skipped_no_budget`=info |
| Тесты (ставки) | [FinModelInstallmentsTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelInstallmentsTests.cs) | без изменений |
| Тесты (ГФ) | [FinModelChapter1ScheduleTests.cs](../KiloImportService.Api.Tests/Mapping/FinModelChapter1ScheduleTests.cs) | конструктор теперь регистрирует `IBudgetVisaryUploader` (success-mock); `SetupWbsBySite` — counter-based: 1-й вызов пустой (pre-check OK), 2+ возвращают полный список (Visary создал WBS upload-ом); `BudgetFixtureForScheduleTests` (минимальный бюджет в Apply); `Apply_ScheduleArticle_NoBudgetRows_SkipsGfEntirely` (новый тест) |

---

## 🎯 Чек-лист

- [ ] `DealPercentBetCreateRequest` содержит `Rate`, но НЕ содержит `PercentKind`.
- [ ] Логи `CreateDealPercentBetAsync` не упоминают `percentKind=`.
- [ ] `EnabledFinancingRate.PercentKind` ОСТАВЛЕН — используется парсером
      для валидации кода.
- [ ] `DealPercentBetRaw.PercentKind` ОСТАВЛЕН — Visary возвращает значение
      в GET/listview.
- [ ] При WBS-exists `budgetUploadOk` остаётся `null` (не `true`); downstream
      `!= true` корректно блокирует ГФ.
- [ ] Сообщение `budget_upload_skipped_wbs_exists` упоминает «ГФ Главы 1
      также пропущен» когда `schedulePending == true`.
- [ ] `schedule_skipped_no_budget` эмитим ТОЛЬКО когда `budgetRows.Count == 0`.
- [ ] `ResolveErrorSeverity`: `budget_upload_skipped_wbs_exists` → warning,
      `schedule_skipped_no_budget` → info.
- [ ] Apply-тесты ГФ имитируют поведение Visary через `SetupWbsBySite`
      (counter-based) + мок-uploader на success.
- [ ] Полный тестовый suite (520/520 без live) — зелёный.

---

## 📅 История изменений

- **v1.1 (2026-06-22)** — Две точечные правки по комментарию заказчика
  («была опечатка» + новое требование):

  1. **Откат Rate.** В v1.0 я ошибочно убрал `Rate` из payload — заказчик
     уточнил, что имел в виду `PercentKind`. Возвращаем `Rate` как в
     [doc 139 v1.4](./139-finmodel-installments-and-conclusion.md);
     убираем `PercentKind`. На скриншоте заказчика после фикса колонка
     «Вид ставки» в Visary показывает корректные Floating/Fixed —
     значит, Visary классифицирует по `PercentBetType`, а ручной
     `PercentKind` мешал.

  2. **Pre-check 1 теперь блокирует Бюджет И ГФ.** В [doc 109](./109-finmodel-prechecks-wbs-and-gf.md)
     WBS-exists выставлял `budgetUploadOk=true` и ГФ пытался дополнить
     существующую ИСР. Заказчик: «Найден ИСР в Объекте — всё, импорт
     Бюджета и ГФ пропускаются. Если ИСР не найден, тогда сначала
     Импорт Бюджета, после того, как он завершился, добавляем ГФ». Теперь
     `budgetUploadOk` остаётся `null` в WBS-exists кейсе; downstream
     `!= true` (из v1.0) корректно блокирует ГФ. Сообщение
     `budget_upload_skipped_wbs_exists` переведено с info на warning
     и теперь явно говорит «ГФ Главы 1 также пропущен».

  Тесты `FinModelChapter1ScheduleTests` переписаны: добавлен мок
  `IBudgetVisaryUploader` (success), `SetupWbsBySite` стал counter-based
  (1-й вызов = пустая ИСР → upload запускается, 2+ = полный список
  WBS, который Visary «создал»). На скриншоте — все 7 ставок успешно
  созданы с правильным Видом + ГФ пропущен с пояснением.

- **v1.0 (2026-06-22)** — первая реализация (откатана v1.1 в части `Rate`).

## 🔗 Связанная документация

- [doc 109 — finmodel-prechecks-wbs-and-gf](./109-finmodel-prechecks-wbs-and-gf.md) —
  Pre-check 1 (WBS-узлы) и Pre-check 2 (skip существующего CostItem).
  Поведение Pre-check 1 ужесточено в doc 144 v1.1 (раньше WBS exists →
  ГФ запускался; теперь → ГФ блокируется).
- [doc 139 — finmodel-installments-and-conclusion](./139-finmodel-installments-and-conclusion.md) —
  Заключение + рассрочки + ставки. Описывает payload `dealpercentbet`
  с `PercentKind` (актуально ДО doc 144 v1.1). Текущий контракт — без `PercentKind`.
- [doc 127 — report-error-severity](./127-report-error-severity.md) —
  switch `ResolveErrorSeverity`: добавлен `schedule_skipped_no_budget` (info)
  и `budget_upload_skipped_wbs_exists` переведён с info на warning.
