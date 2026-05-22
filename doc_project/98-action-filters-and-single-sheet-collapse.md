# 98 · Фильтры по action-меткам + сворачивание единственного листа

> Дата: 2026-05-20
>
> Связано с: [85-per-row-action-log](./85-per-row-action-log.md),
> [95-history-project-filter-and-collapsible-sheets](./95-history-project-filter-and-collapsible-sheets.md),
> [96-rooms-incremental-parallel-apply](./96-rooms-incremental-parallel-apply.md)

## Зачем

После переезда RoomsForm.Apply на инкрементальный режим (doc 96) для каждой
строки в `RowActionLog.Actions` пишутся метки реальных действий:

- «Помещение создано» / «ДДУ создан» / «Корпус создан» / «Застройщик создан»;
- «Помещение обновлено»;
- «Без изменений — пропуск (snapshot)».

В отчёте импорта эти метки выводятся в колонке «Сообщения», но фильтра по
ним не было — пользователь не мог быстро увидеть «только то, что
действительно изменилось» или «только пропущенные diff-skip-ом строки».

Параллельно — для файла с **одним листом** «Квартира» (классика
RoomsForm-импорта) сворачивание не работало: `canCollapseAny` требовал
`allSheets.length > 1`. На 6000+ строк это значило, что свернуть лист
нельзя в принципе.

## Что сделано

### 1. Сворачивание единственного листа

[KiloImportService.Web/src/components/ImportSession/SessionRowsTable.tsx](../KiloImportService.Web/src/components/ImportSession/SessionRowsTable.tsx):

```diff
- const canCollapseAny = allSheets.length > 1;
+ const canCollapseAny = allSheets.length >= 1;
```

`SheetHeaderRow` теперь рендерит шеврон-кнопку даже когда лист один.
`null`-листы (одностраничные импорты без имени) по-прежнему non-toggle —
backend исключение по null не поддерживает, и сворачивать там нечего.

### 2. Три action-фильтра в `.filter-tags`

В тип `Filter` добавлены варианты `'created' | 'updated' | 'skipped'`.
`matchesFilter` ищет ключевые подстроки в `row.actions`:

| Фильтр | Метки в `row.actions` |
|--------|-----------------------|
| **Созданные** (`created`) | `/Помещение создан/i` — строка, в которой CREATE Room |
| **Обновлённые** (`updated`) | `/Помещение обновлен/i` — строка, в которой PATCH Room |
| **Пропущенные** (`skipped`) | `/Без изменений\|пропуск/i` — единственный источник: diff-skip из RoomApplySnapshotStore (doc 96) |

Кнопка фильтра отображается только при `count > 0` (как раньше у
`applied`/`failed`) — чтобы пустые табы не вводили в заблуждение.

### v1.2 (2026-05-20) — session-wide action totals из backend

В первой версии счётчики `created / updated / skipped` считались по
`report.rows` — это только видимая страница (`take=50`). На сессии в
6000+ строк пользователь видел `Created 27 / Updated 49 / Skipped 1` —
числа из первых 50 строк, которые не отражают сессионную картину.

Решение — server-side агрегат:

1. [ImportsController.GetReport](../KiloImportService.Api/Controllers/ImportsController.cs)
   возвращает поле `actionTotals = { created, updated, skipped }`.
   Backend читает все Applied-строки сессии (`r.Actions != null`),
   tянет `JsonDocument.RootElement.GetRawText()` и считает substring-матч
   тех же категорий, что на клиенте — «Помещение создан», «Помещение
   обновлен», «Без изменений»/«пропуск». Гонять Postgres-regex поверх
   jsonb не оправдано: для типичных 10k Applied — пара МБ JSON-а в
   память, секундная агрегация.
2. [ApiImportReport.actionTotals](../KiloImportService.Web/src/types/api.ts)
   + `UiActionTotals` в session-типах + дефолт `{0,0,0}` для legacy backend.
3. `SessionRowsTable.tsx` берёт `report.actionTotals` для action-фильтров;
   status-based счётчики (`Invalid`/`Failed`) по-прежнему по странице —
   их backend не агрегирует по сессии отдельным полем.

Now: переход на 187-страничную сессию даёт `Created 187 / Updated 0 /
Skipped 0` на первом импорте и `Created 0 / Updated 0 / Skipped 187` на
повторном — независимо от пагинации.

### v1.1 (2026-05-20) — категории по ГЛАВНОЙ сущности строки

Изначально матчили по широким корням `/создан/i`, `/обновлен/i` — это
включало «ДДУ создан», «Корпус найден», «Застройщик создан/привязан».
Строка с PATCH Room + CREATE SA попадала **сразу в две** категории
(Created и Updated), сумма `created + updated + skipped` превышала
`applied` (живой кейс: 27 + 49 + 1 = 77 > 50 Applied).

Исправлено: матчим конкретно метки про **помещение** —
`«Помещение создано»` / `«Помещение обновлено»`. Это даёт
взаимоисключающие категории: каждая RoomsForm-строка делает CREATE Room,
либо PATCH Room, либо skip → ровно один матч. Сумма теперь
`created + updated + skipped ≤ applied`. Побочные действия (ДДУ, корпус,
застройщик) по-прежнему видны в колонке «Сообщения» как контекст, но
в счёт фильтров не идут.

Если в будущем появится отдельный пользовательский запрос «покажи
строки, где создались ДДУ» — лучше отдельный фильтр (например,
`sa-created` с regex `/ДДУ создан/i`), а не размывать корневые три.

### 3. Подсчёт — по текущей странице

`counts.created / updated / skipped` считаются по `report.rows` — то есть
**по строкам видимой страницы**, не по всему набору сессии. Это тот же
паттерн, что и для `counts.invalid` / `counts.failed`.

Альтернатива — считать на backend через JSONB-запрос вида
`where Actions ->> 0 ~* 'создан'`. Для типичного отчёта на 50 строк
выгода нулевая, кода и индексов больше. Если в будущем потребуется
точный agreggate на ВСЮ сессию — `ImportSessionReport.SheetTotals` уже
содержит счётчики, осталось расширить его новыми полями (см. дочернее
TODO в `ImportSessionReportService`).

## Влияние на смежный код

| Файл | Изменение |
|------|-----------|
| `SessionRowsTable.tsx` | `Filter` тип расширен; `matchesFilter` и `counts` дополнены; 3 новые `FilterButton`; `canCollapseAny` смягчён |
| Backend | без изменений — фильтры чисто клиентские |
| `SheetHeaderRow` | без изменений — он уже рендерил toggle-кнопку по `canToggle` |

## Если действия маппера переименуют

`actionMatchesCreated / Updated / Skipped` намеренно матчат по
**корням** русских слов (`создан`, `обновлен`, `пропуск`/«Без изменений»),
чтобы не ломаться от форм женского/среднего рода («создана»/«создано»)
и от мелких правок текста. Если переименование пойдёт настолько глубоко,
что корень исчезнет (например, «создан» → «зарегистрирован»), фильтры
перестанут срабатывать — это будет видно сразу: счётчик 0 при том, что
такие строки точно есть. Чинить — синхронно поправить регулярки в
[SessionRowsTable.tsx](../KiloImportService.Web/src/components/ImportSession/SessionRowsTable.tsx).

---

## v1.3 (2026-05-22) — Status-фильтры тоже session-wide

### Что было сломано

В верхней панели отчёта (`SessionSummary`) карточка «С ошибками» показывала
`session.errorRows` — session-wide значение (например, **22**). В блоке
`filter-tags` чуть ниже кнопка «С ошибками» при этом показывала **0**,
потому что счётчик считался по `report.rows` — а на странице 1 (первые 50)
ошибочных строк не было, они лежали на странице 3.

Симптомы:
- `Применённые 50` в фильтре vs `Создано 100` в action-фильтре (та же сессия!).
- «Всего строк 625 / Валидных 100 / С ошибками 22» в карточках, но `Все 50`,
  `Валидные 50`, `С ошибками 0` в фильтрах.
- Пользователь не понимает, где правда.

Корень — async-миграция в v1.2 перевела **только** action-фильтры
(Created/Updated/Skipped) на session-wide через `actionTotals` на backend.
Status-фильтры (`Все`/`Валидные`/`С ошибками`/`Применённые`/`Не применилось`)
остались page-level → рассинхрон с верхней панелью.

### Что сделано

**Backend** ([ImportsController.cs:354-389](../KiloImportService.Api/Controllers/ImportsController.cs)):
один EF GroupBy-запрос по `StagedRow.Status`, агрегат в словарь, сборка
объекта:

```csharp
var byStatus = await _db.StagedRows.AsNoTracking()
    .Where(r => r.ImportSessionId == id)
    .GroupBy(r => r.Status)
    .Select(g => new { Status = g.Key, Count = g.Count() })
    .ToListAsync(ct);
var statusDict = byStatus.ToDictionary(x => x.Status, x => x.Count);
int countOf(StagedRowStatus s) => statusDict.TryGetValue(s, out var c) ? c : 0;

statusTotals = new {
    all     = statusDict.Values.Sum(),
    valid   = countOf(StagedRowStatus.Valid) + countOf(StagedRowStatus.Applied),
    invalid = countOf(StagedRowStatus.Invalid),
    applied = countOf(StagedRowStatus.Applied),
    failed  = countOf(StagedRowStatus.Failed),
}
```

Важно: `valid = Valid + Applied` — Applied означает «прошёл validation + apply»,
он же «валидный». Без сложения «Применённые» оказались бы > «Валидные», что
бессмыслица.

`statusTotals` **не** учитывает `excludeSheets` — как `actionTotals` и
`session.errorRows`. Свёрнутый лист на пагинацию влияет (`rowsPagination.total`),
а на верхние/фильтр-счётчики — нет. Это сознательно: пользователь видит общее
состояние сессии независимо от текущего вида.

**Frontend**:
- [api.ts](../KiloImportService.Web/src/types/api.ts) — `ApiStatusTotals` interface.
- [session.ts](../KiloImportService.Web/src/types/session.ts) — `UiStatusTotals`,
  `UiReport.statusTotals: UiStatusTotals | null`.
- [importMappers.ts](../KiloImportService.Web/src/services/importMappers.ts) —
  `statusTotals: api.statusTotals ?? null` (legacy backend → null).
- [SessionRowsTable.tsx](../KiloImportService.Web/src/components/ImportSession/SessionRowsTable.tsx) —
  если `statusTotals` есть, берём оттуда; иначе fallback на старый page-level
  подсчёт (для обратной совместимости со старым backend'ом).

### ⚠️ Грабли

1. **`statusValid = Valid + Applied`** — не просто `Valid`. Applied тоже валидные,
   они просто УЖЕ применены. Если посчитать только `Valid`, получим 0 после
   успешного Apply, а кнопка «Валидные» обнулится.

2. **Фронт-фильтр (`matchesFilter`) по-прежнему page-level** — он применяется к
   видимым строкам и решает, **показать ли строку**. Session-wide цифры
   рассказывают «сколько таких в сессии», но клик по фильтру отфильтрует
   только текущую страницу. Чтобы найти ошибочные строки в большой сессии,
   пользователю придётся пагинироваться (страница, где status=Invalid).

3. **Legacy fallback** — без `report.statusTotals` UI вычисляет
   как раньше (page-level). Это нужно для случая, когда фронт обновили,
   а backend ещё старый.

### Зачем не unit-test'нули

`statusTotals` — это детерминированный SQL GroupBy с явным маппингом enum→ключи.
Существующих integration-тестов для `ImportsController` в проекте нет (нужна
WebApplicationFactory + in-memory ImportServiceDb + seed). Заводить инфраструктуру
ради одной 7-строчной добавки — overkill. Проверка — `curl GET /api/imports/{id}/report`
после смешанного импорта и сравнение `statusTotals` с `session.errorRows`/`SuccessRows`.
Если потребуется тест-инфра — стоит делать единым кешем с `actionTotals`-тестом.

### Как это видно в UI

| До v1.3 | После v1.3 |
|---|---|
| `С ошибками 22` (карточка) ≠ `С ошибками 0` (фильтр) | Оба — `22` |
| `Применённые 50` (page) ≠ `Создано 100` (session) | Оба — session-wide |
| Цифры в фильтре растут при переходе на следующую страницу | Цифры стабильны |
