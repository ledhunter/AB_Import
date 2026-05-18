# 📄 Пагинация построчного отчёта

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-18
**Дополняет**: [14-imports-backend-integration.md](14-imports-backend-integration.md), [73-import-history-page.md](73-import-history-page.md)

Раньше UI отчёта показывал только первую страницу из 100 строк («Показано
100 из 187 строк»). На длинных импортах (6000+ помещений в файле Ежевики/
Волги) пользователь физически не мог дотянуться до строк после 100-й.

Сервер `GET /api/imports/{id}/report` уже поддерживал `skip`/`take` (см.
[14-imports-backend-integration](14-imports-backend-integration.md)), но
хуки `useImportSession` / `useImportSessionDetail` параметры не передавали.

Теперь хуки умеют переключать страницу, а `SessionRowsTable` рисует
`@alfalab/core-components/pagination` под таблицей.

---

## ✅ Правильная реализация

### 1. Хуки — `loadReportPage(skip)`

```ts
// useImportSession.ts
export const REPORT_PAGE_SIZE = 100;

const loadReport = useCallback(async (sessionId: string, skip = 0) => {
    /* … */
    const apiReport = await getImportReport(sessionId, {
        signal: ctrl.signal, skip, take: REPORT_PAGE_SIZE,
    });
    /* … */
}, []);

const loadReportPage = useCallback(async (skip: number) => {
    const sid = sessionIdRef.current;
    if (!sid) return;
    await loadReport(sid, skip);
}, [loadReport]);
```

Аналогично — `useImportSessionDetail` (для страницы истории), импортирует
`REPORT_PAGE_SIZE` из `useImportSession`, чтобы значения не расходились.

### 2. `SessionRowsTable` — Pagination + range-индикатор

```tsx
const { skip, take, total } = report.rowsPagination;
const pagesCount = Math.max(1, Math.ceil(total / take));
const currentPageIndex = Math.min(pagesCount - 1, Math.floor(skip / take));
const rangeFrom = total === 0 ? 0 : skip + 1;
const rangeTo = Math.min(total, skip + report.rows.length);

<div>
    <Typography.Text>Показано {rangeFrom}–{rangeTo} из {total} строк.</Typography.Text>
    {pagesCount > 1 && (
        <Pagination
            currentPageIndex={currentPageIndex}
            pagesCount={pagesCount}
            onPageChange={(pageIndex) => {
                setFilter('all');                    // 👈 пользователь иначе попадёт
                                                     //    в пустой набор на новой странице
                onPageChange?.(pageIndex * take);
            }}
        />
    )}
</div>
```

### 3. Прокидывание сквозь компоненты

- `App.tsx` → `<SessionView onReportPageChange={importSession.loadReportPage} />`
- `SessionView` → `<SessionRowsTable onPageChange={onReportPageChange} />`
- `HistoryDetailView` → `<SessionRowsTable onPageChange={loadReportPage} />`

### ⚠️ Важно

- **Сброс локального фильтра** (`setFilter('all')`) при смене страницы.
  Активный фильтр «С ошибками» при переходе на страницу без ошибок
  отрисует «Нет строк для текущего фильтра», и пользователю кажется,
  что навигация сломалась.
- **`pagesCount = Math.max(1, …)`** — защита от деления на 0 при пустом
  отчёте.
- **`currentPageIndex` зажимаем сверху** на `pagesCount - 1`. На бэке
  Take clamped to 500, и если кто-то откроет `?skip=99999`, не попадём
  в `>= pagesCount` (Alfa Pagination ругается в логи).
- **Пагинация — только при `pagesCount > 1`**. Один экран не нуждается
  в навигации, range-индикатор остаётся для контекста.
- **`REPORT_PAGE_SIZE = 100`** — синхронизирован с backend (controller
  без `take` отдаёт первые 100). Если меняешь — меняй обе стороны.

---

## ❌ Типичная ошибка

```tsx
// НЕПРАВИЛЬНО — забыть сбросить фильтр: пользователь, кликнув «след.
// страница» из вьюхи «С ошибками», увидит «Нет строк» и решит, что
// пагинация сломана.
<Pagination onPageChange={(p) => onPageChange(p * take)} />
```

```tsx
// НЕПРАВИЛЬНО — не учитывать total === 0 в pagesCount: Alfa Pagination
// валится при `pagesCount=0` (warning в консоли + не рендерится).
const pagesCount = Math.ceil(total / take);  // ← 0 при total=0
```

```ts
// НЕПРАВИЛЬНО — хранить currentPageIndex в локальном state-компонента
// SessionRowsTable. После повторного fetch'а отчёта (apply → applied)
// state не синхронизируется с серверным skip, и навигация показывает
// «страница 3», когда реально загружена 1. Источник истины — apiReport.
//                                              rowsPagination.skip.
const [page, setPage] = useState(0);  // ← рассинхрон с сервером
```

```ts
// НЕПРАВИЛЬНО — задублировать `REPORT_PAGE_SIZE` в двух хуках. Если
// захочется поменять (например, до 50 для мобилок) — забудешь в одном
// месте, и hooks-а будут просить разные `take`, отчёт «мигает».
const REPORT_PAGE_SIZE = 100;  // в useImportSession.ts
const REPORT_PAGE_SIZE = 100;  // в useImportSessionDetail.ts  ← дубль
```

---

## 📍 Применение в проекте

| Компонент                          | Файл                                                         | Что добавилось |
|------------------------------------|--------------------------------------------------------------|----------------|
| `loadReport(skip)`                 | `KiloImportService.Web/src/hooks/useImportSession.ts`        | Параметр `skip`, экспорт `loadReportPage` + `REPORT_PAGE_SIZE` |
| `loadReportPage`                   | `KiloImportService.Web/src/hooks/useImportSessionDetail.ts`  | Тот же контракт для read-only истории |
| `Pagination`                       | `components/ImportSession/SessionRowsTable.tsx`              | `@alfalab/core-components/pagination` + range-индикатор |
| `onReportPageChange` prop          | `components/ImportSession/SessionView.tsx`                   | Прокидывает в таблицу |
| App-обвязка                        | `App.tsx`, `components/ImportHistory/HistoryDetailView.tsx`  | Передают `loadReportPage` соответствующего хука |

---

## 🎯 Чек-лист

- [x] `take` передаётся в `getImportReport` в обоих хуках
- [x] `REPORT_PAGE_SIZE` один на весь модуль (`useImportSession`)
- [x] Сброс фильтра «valid/invalid/applied/failed» при смене страницы
- [x] `pagesCount > 1` — условие отрисовки Pagination (на 1 странице — не показываем)
- [x] Range-индикатор «{from}–{to} из {total}» всегда виден
- [ ] При появлении сортировки/фильтров на стороне сервера — добавить параметры в `getImportReport` и передавать в обоих хуках (сейчас сортировка фиксирована `Sheet,SourceRowNumber`)
