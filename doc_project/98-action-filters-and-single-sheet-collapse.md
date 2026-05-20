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
