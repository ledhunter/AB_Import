# 🛡️ Финмодель → pre-check ИСР перед бюджетом + строгий skip ГФ

## 📋 Описание

В Финмодель-Apply добавлены два pre-check'а, защищающие уже сформированное состояние Visary от перезаписи повторным импортом того же файла:

1. **Pre-check 1 (бюджет)** — перед заливкой XLSX через `BudgetVisaryUploader` проверяется, есть ли в ИСР выбранного `constructionsite` хотя бы один WBS-узел. Если есть — заливка пропускается, ГФ продолжает работать с уже существующими статьями.
2. **Pre-check 2 (ГФ)** — в `ApplyChapter1ScheduleAsync` логика «PATCH при расхождении суммы» убрана. Теперь, если для квартала (WBSID, `PlanPeriod.Start`) уже есть `CostItem` — пропуск без сравнения суммы. Создаём только если ГФ отсутствует.

Заказчик не хочет, чтобы Финмодель «затирала» уже импортированные/отредактированные вручную данные в Visary при повторном запуске.

---

## ✅ Правильная реализация

### Pre-check 1

[FinModelImportMapper.ApplyAsync](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) — блок `if (budgetRows.Count > 0)`:

```csharp
var schedulePending = scheduleArticleRows.Count > 0 && scheduleQuartersRow is not null;
var wbsExists = await WbsAlreadyExistsForSiteAsync(siteId, errors, ct);
if (wbsExists is null)
{
    // listview/wbs упал — заливать небезопасно, можем породить дубликат WBS.
    // ГФ тоже пропускаем — без подтверждённого состояния ИСР это слепой POST.
    budgetUploadOk = false;
}
else if (wbsExists.Value)
{
    // ИСР уже есть — заливку XLSX пропускаем, ГФ запускаем как обычно.
    errors.Add(new RowError(null, "budget_upload_skipped_wbs_exists", "..."));
    budgetUploadOk = true;
}
else
{
    // ИСР пуста — обычная заливка XLSX через BudgetVisaryUploader.
    budgetUploadOk = await UploadBudgetToVisaryAsync(...);
}
```

`WbsAlreadyExistsForSiteAsync` — обёртка над `_listViewClient.GetWbsBySiteAsync(siteId, ct)` (`POST /api/visary/listview/wbs/onetomany/ConstructionSite?associationId={siteId}`), возвращает:

| Result | Что делаем |
|--------|------------|
| `true` | ИСР есть — skip заливки, `budgetUploadOk = true`, info-сообщение `budget_upload_skipped_wbs_exists` в `errors` |
| `false` | ИСР пуста — обычная заливка |
| `null` (исключение listview) | Состояние неизвестно — skip заливки И ГФ (`budgetUploadOk = false`), `budget_upload_precheck_failed` в `errors` |

### Pre-check 2

[ApplyChapter1ScheduleAsync](../KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs) — внутренний цикл по квартальным ячейкам:

```csharp
// Pre-check существующего ГФ за этот квартал: уже есть — skip без PATCH-on-diff.
if (existingByStart.TryGetValue(qStart.Date, out var match))
{
    skipped++;
    perRowActions.Add($"ГФ {cellLabel} ({quarterLabel}, статья {code.TrimEnd('.')}): " +
        $"уже существует (сумма в Visary: {existingSum}) — пропуск");
    continue;
}
// Только POST, если match не найден.
await _visaryClient.CreateCostItemAsync(new CostItemCreateRequest { ... }, ct);
```

`PatchCostItemAsync` из мапера больше не вызывается. Сам метод в `Visary.Api.Client` оставлен как часть публичной поверхности клиента.

---

## ⚠️ Важно

- **Pre-check 1 пропускает заливку, но НЕ блокирует ГФ.** Это сознательно: ГФ-логика и так идемпотентна по `(WBSID, PlanPeriod.Start)`, поэтому она дополнит уже импортированный бюджет недостающими графиками.
- **Сетевая ошибка listview/wbs ≠ «ИСР пуста».** При исключении возвращаем `null` и пропускаем И заливку, И ГФ — иначе на временном сбое можно создать дубликат ИСР.
- **`budget_upload_skipped_wbs_exists` — это диагностическое сообщение, не ошибка.** Лежит в `errors` потому что текущая модель `ApplyResult` не различает info/warning/error. UI рендерит его как обычную file-level метку.
- **Pre-check 2 не различает «cумма та же» / «cумма другая».** Любой существующий `CostItem` за этот квартал → skip. Ручные правки в Visary не перетираются.

---

## ❌ Типичные ошибки

```csharp
// НЕПРАВИЛЬНО — на сетевой ошибке считать «ИСР пуста» и заливать.
try { var wbs = await _listViewClient.GetWbsBySiteAsync(...); return wbs.Data.Count > 0; }
catch { return false; }  // 💥 при flaky listview породим дубликат ИСР

// НЕПРАВИЛЬНО — обнулять ГФ до запуска Pre-check 1.
budgetUploadOk = false;          // 💥 ГФ заблокирован даже когда ИСР УЖЕ есть в Visary
if (wbsExists.Value) budgetUploadOk = true;
```

```csharp
// НЕПРАВИЛЬНО — оставить PATCH-on-diff после Pre-check 2.
if (existingByStart.TryGetValue(qStart.Date, out var match))
{
    if (Math.Abs(match.PlanSum - amountRub) > 0.01)
        await _visaryClient.PatchCostItemAsync(match.ID, ...);  // 💥 затирает ручные правки
    continue;
}
```

---

## 📍 Применение в проекте

| Слой | Файл | Метод/блок |
|------|------|------------|
| Apply mapper | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs` | `ApplyAsync` (блок `if (budgetRows.Count > 0)`) |
| Helper | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs` | `WbsAlreadyExistsForSiteAsync` |
| ГФ skip | `KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs` | `ApplyChapter1ScheduleAsync` (внутренний цикл `foreach (var (col, amountThousands) in cells)`) |
| ListView client | `Visary.Api.Client/ListView/ListViewClient.cs` | `GetWbsBySiteAsync(siteId)` (уже был, doc 91) |
| Тесты бюджета | `KiloImportService.Api.Tests/Mapping/FinModelBudgetTests.cs` | `ApplyAsync_Budget_SkipsUploadWhenSiteAlreadyHasWbs`, `ApplyAsync_Budget_UploadsWhenSiteHasNoWbs`, `ApplyAsync_Budget_SkipsUploadWhenWbsPrecheckFails` |
| Тесты ГФ | `KiloImportService.Api.Tests/Mapping/FinModelChapter1ScheduleTests.cs` | `Apply_ScheduleArticle_SkipsExistingEvenOnAmountChange` (заменил `PatchesExistingOnAmountChange`) |

---

## 🔗 Связанная документация

- [doc 78 — budget-xlsx-export](./78-budget-xlsx-export.md) — XLSX-генерация бюджета по эталону «Бюджет_А4.1»
- [doc 82 — visary-file-storage-upload](./82-visary-file-storage-upload.md) — `BudgetVisaryUploader` (upload + poll `typedimportwbs`)
- [doc 91 — finmodel-chapter1-schedule](./91-finmodel-chapter1-schedule.md) — `ApplyChapter1ScheduleAsync` (исходная PATCH/skip-логика)
- [doc 94 — finmodel-auto-budget-before-gf](./94-finmodel-auto-budget-before-gf.md) — авто-заливка бюджета перед ГФ

---

## 🎯 Чек-лист

- [ ] Pre-check 1 вызывается ровно ОДИН раз перед заливкой бюджета (не вообще, не повторно на retry)
- [ ] При непустом ответе `listview/wbs/onetomany/ConstructionSite` — `BudgetVisaryUploader.UploadAndWaitAsync` НЕ вызван
- [ ] При непустом ответе `budgetUploadOk = true` — ГФ запускается
- [ ] При исключении listview — uploader НЕ вызван И ГФ НЕ запущен (`budget_upload_precheck_failed`)
- [ ] В `ApplyChapter1ScheduleAsync` существующий CostItem с любой суммой → skip (не PATCH)
- [ ] `PatchCostItemAsync` из мапера больше не вызывается (только из `Visary.Api.Client` как публичная обёртка)
- [ ] Тесты `FinModelBudgetTests` и `FinModelChapter1ScheduleTests` зелёные
