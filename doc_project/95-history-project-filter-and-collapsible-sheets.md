# 📄 История импортов — фильтр по проекту и сворачиваемые листы

## 📋 Описание

**Статус**: ✅ Реализовано
**Дата**: 2026-05-20 (v1.1 — фиксы UX-багов после первой ревизии)
**Дополняет**: [73-import-history-page.md](73-import-history-page.md),
[87-report-pagination.md](87-report-pagination.md),
[72-multi-sheet-import.md](72-multi-sheet-import.md)

Три связанных UX-улучшения по результатам разбора истории импортов:

1. **Фильтр «Проект» в истории импортов.** Сессии стало много, и фильтра
   только по статусу/типу импорта не хватало — у пользователя нет
   способа за пару кликов вытащить все сессии конкретного объекта.

2. **Размер страницы отчёта снижен со 100 до 50 строк.** Многолистовые
   импорты («Финмодель», «Помещения» с парковками/нежильём на отдельных
   листах) физически не помещались в 100 строк по одному листу, и
   пользователь скакал по страницам внутри листа. 50 — комфортный размер
   и для длинных, и для коротких листов.

3. **Сворачиваемые листы в отчёте.** На отчёте многолистового импорта
   пользователь хочет работать с одним листом за раз: остальные надо
   убрать с экрана и из пагинации, чтобы не «есть» страницы. Backend
   принимает `excludeSheets` и пересчитывает `rowsPagination.total` —
   UI отрисовывает свёрнутые листы как пустые заголовки с «развернуть».

---

## ✅ Правильная реализация

### 1. Backend — поле `projectId` в списке + `excludeSheets` в отчёте

```csharp
// GET /api/imports — фильтр projectId и projectName из CachedProject (LEFT JOIN).
[HttpGet]
public async Task<IActionResult> List(
    [FromQuery] int skip = 0,
    [FromQuery] int take = 50,
    [FromQuery] string? status = null,
    [FromQuery] string? importTypeCode = null,
    [FromQuery] int? projectId = null,           // 👈 новое
    CancellationToken ct = default) { … }

// GET /api/imports/{id}/report — excludeSheets + sheetTotals.
[HttpGet("{id:guid}/report")]
public async Task<IActionResult> GetReport(
    Guid id,
    [FromQuery] int skip = 0,
    [FromQuery] int take = 50,                   // 👈 было 100
    [FromQuery] string[]? excludeSheets = null,  // 👈 новое
    CancellationToken ct = default) { … }
```

Ключевые инварианты:

- **`projectName` через LEFT JOIN.** Если проект уже не в кэше Visary —
  сессию всё равно показываем, `projectName=null`, UI рисует «Проект #id».
- **`excludeSheets` — нормализуем.** Пустые/null отбрасываем, остальное
  принимаем как есть: клиент пришлёт ровно то, что мы сами отдали
  в `sheetTotals[].sheet` (Postgres-сравнение по тексту чувствительно
  к регистру/пробелам).
- **Sheet `null` (одностраничный импорт) НЕ исключается** даже если
  клиент случайно прислал пустую строку — иначе сломаем фильтр по
  обычным CSV/одностраничным XLSX.
- **`rowsPagination.total` пересчитывается** с учётом `excludeSheets` —
  иначе пагинация показывает «5 страниц», а реальных строк только на 1.
- **`sheetTotals` считается по ВСЕМ строкам сессии** (без учёта
  `excludeSheets`) — UI'у нужна полная карта листов, чтобы рисовать
  заголовки и счётчики свёрнутых.

### 2. UI — фильтр «Проект» (`HistoryFilters`)

```tsx
const projects = useBackendProjects({ searchString: projectSearch, logTag: '[HistoryFilters/Projects]' });

useEffect(() => {
  projects.sync();  // прогрев кэша при монтировании
}, []);

<Select
  label="Проект"
  selected={projectId != null ? String(projectId) : ALL_OPTION_KEY}
  options={projectOptions}
  showSearch
  searchProps={{ value: projectSearch, onChange: e => setProjectSearch(e.target.value) }}
  onChange={({ selected }) => {
    if (!selected?.key || selected.key === ALL_OPTION_KEY) onProjectChange(undefined);
    else onProjectChange(Number(selected.key));
    setProjectSearch('');
  }}
  block
/>
```

- Прогрев кэша **при первом mount'е** — иначе при первом клике в Select
  список будет пустым, пользователь увидит «нет проектов».
- **Sticky-опция выбранного проекта.** Если выбранный `projectId` пропал
  из поискового результата (узкая подстрока), мы вручную добавляем его
  в начало списка как «Проект #id». Иначе Select снимет выбор и пользователь
  потеряет фильтр при попытке что-то найти.
- **Очистка `projectSearch` после выбора** — Select после закрытия
  продолжит фильтровать по последнему запросу, и при следующем открытии
  пользователь увидит «странный» список.

### 3. UI — сворачивание листов в `SessionRowsTable`

```tsx
const [collapsedSheets, setCollapsedSheets] = useState<Set<string>>(() => new Set());

// При изменении collapsedSheets — посылаем excludeSheets в backend.
const lastSentRef = useRef<string>('');
useEffect(() => {
  const sorted = Array.from(collapsedSheets).sort();
  const key = sorted.join('|');
  if (key === lastSentRef.current) return;  // идемпотентность — initial render не дёргает API
  lastSentRef.current = key;
  onPageChange?.(0, { excludeSheets: sorted });  // skip=0 — возврат в начало
  setFilter('all');
}, [collapsedSheets, onPageChange]);
```

Свёрнутые листы рисуются заголовками-аккордеонами **в самом конце таблицы**
(под видимыми группами), чтобы пользователь видел их и мог развернуть:

```tsx
{collapsedHeaders.map(sheet => (
  <SheetHeaderRow
    sheet={sheet}
    total={totalsBySheet.get(sheet) ?? 0}
    collapsed={true}
    canToggle={true}
    onToggle={() => toggleSheet(sheet)}
  />
))}
```

Ключевые моменты:

- **Counter `total стр.` рисуем из `sheetTotals`** (по всей сессии),
  а не из локальных групп — иначе свёрнутый лист покажет «0 стр.»
  и пользователь подумает, что данных в нём нет.
- **`canCollapseAny = allSheets.length > 1`** — нет смысла сворачивать
  единственный лист (одностраничный импорт). Дополнительно: backend
  не умеет исключать `Sheet IS NULL` через `excludeSheets`.
- **`lastSentRef`-идемпотентность.** При первом render'е `collapsedSheets`
  пустой — мы НЕ должны вызывать `onPageChange?.(0, { excludeSheets: [] })`,
  иначе двойная загрузка отчёта при открытии вкладки.
- **Сброс фильтра «valid/invalid/...» при свёртывании** — иначе
  пользователь, кликнув по «свернуть», может попасть в пустой набор и
  подумать, что таблица сломалась.
- **`excludeSheetsRef` в хуках** — `loadReportPage` запоминает последний
  набор, чтобы reload/смена страницы продолжали учитывать ту же конфигурацию
  свёрнутых листов.

### 4. CSS — заголовок-аккордеон

`.sheet-header-row__toggle` — `<button>`, сброшенный под inline-flex
с chevron `▸/▾`. Свёрнутый лист подсвечен серее (`sheet-header-row--collapsed`),
плюс подсказка «— свёрнут».

---

## 🔁 v1.1 — фиксы после первой ревизии (2026-05-20)

### 1. `TypeError` при наборе в поиске проекта

**Симптом.** Пользователь открывает Select «Проект», начинает печатать
подстроку — страница «исчезает», в консоли:

```
HistoryFilters.tsx:122 Uncaught TypeError:
  Cannot read properties of undefined (reading 'value')
```

**Причина.** Alfa-Select передаёт в `searchProps.onChange` уже **извлечённую
строку** (не SyntheticEvent). При первоначальной реализации `HistoryFilters`
писал handler по аналогии с обычным `<input onChange>`:

```tsx
searchProps={{ onChange: (e) => setProjectSearch(e.target.value) }}
//                       ↑ e — это string, у неё нет .target
```

**Фикс.** Сигнатуру выровняли с рабочим примером в
`ImportForm.tsx:174` (см. [20-select-with-search.md](20-select-with-search.md),
«Ошибка 4»):

```tsx
searchProps={{
  value: projectSearch,
  onChange: (value: string) => setProjectSearch(value),
  componentProps: { placeholder: 'Поиск проекта' },
  //              ↑ placeholder идёт сюда, а не прямо в searchProps
}}
```

### 2. Empty-state «нет возможности вернуться»

**Симптом.** Когда фильтр «Проект» отбирает проект без импортов, страница
показывала «Сессий импорта пока нет. Запустите первый импорт на вкладке
"Импорт"» — текст из «пустой системы». Пользователь видел только это
сообщение и фильтры, без подсказки, что вернуть всё назад можно через
«Все проекты».

**Фикс.** В `HistorySessionsTable` добавлен prop `hasActiveFilters` —
`ImportHistoryPage` прокидывает `true`, если установлен хотя бы один из
фильтров (`status` / `importTypeCode` / `projectId`). При активном фильтре
empty-state теперь подсказывает: «По выбранным фильтрам нет сессий
импорта. Сбросьте фильтры, чтобы увидеть всю историю.» Сами Select'ы
фильтров остаются на экране — пользователь видит, какие условия активны,
и может вернуться к «Все статусы / Все типы / Все проекты».

> Сами TypeError-краши React не «пожирают» — корневой компонент рисуется
> заново при следующем setState. Но визуально это выглядело как «всё
> сломалось», поэтому первое сообщение от пользователя было про
> «исчезла страница, нет возможности вернуться».

---

## ⚠️ Важно

- **Backend нормализует `excludeSheets`** ровно по text-equality — клиент
  должен присылать имена листов как они приходят в `sheetTotals[].sheet`.
  Нельзя строить локальное имя из строки (например, дописывать пробел).
- **Сброс фильтра свёрнутых листов при смене сессии.** В
  `useImportSessionDetail` мы сбрасываем `excludeSheetsRef.current = []`
  каждый раз, когда меняется `sessionId`. Иначе свёрнутые листы одной
  сессии «утекут» в другую и пользователь увидит пустой отчёт.
- **Backend не отличает «свёрнут» от «лист не существует».** Если клиент
  пришлёт `excludeSheets=['NonexistentSheet']` — `total` не изменится,
  ничего не сломается. Но если пришлёт пустую строку — мы её фильтруем
  на стороне backend'а (`.Where(s => !string.IsNullOrWhiteSpace(s))`),
  иначе случайно исключим `Sheet IS NOT NULL AND Sheet = ''`.
- **Фильтр «Проект» + пагинация истории**: при смене projectId мы
  сбрасываем `skip=0` (см. `useImportsHistory.setFilters`). Иначе
  можно остаться на странице 3 с фильтром, который отдаёт всего 5 строк.

---

## ❌ Типичная ошибка

```ts
// НЕПРАВИЛЬНО — считать total БЕЗ excludeSheets и слать его клиенту:
//   пагинация покажет «5 страниц», но фактически строк только на 1.
var total = await _db.StagedRows.CountAsync(r => r.ImportSessionId == id);
//                                       ↑ нет фильтра по excludeSheets
```

```tsx
// НЕПРАВИЛЬНО — не делать idempotent-guard для первого render'а:
//   onPageChange?.(0, { excludeSheets: [] }) будет вызван при mount'е,
//   что вызовет повторный GET /report с теми же параметрами.
useEffect(() => {
  onPageChange?.(0, { excludeSheets: Array.from(collapsedSheets).sort() });
}, [collapsedSheets, onPageChange]);
```

```tsx
// НЕПРАВИЛЬНО — рисовать counter «total стр.» из локальных групп:
//   у свёрнутого листа на странице нет ни одной строки, и UI покажет «0 стр.».
<SheetHeaderRow total={group.rows.length} … />  // ← должно быть из sheetTotals
```

```tsx
// НЕПРАВИЛЬНО — не очищать projectSearch после выбора проекта:
//   после закрытия Select продолжит фильтровать по старому запросу,
//   и при следующем открытии пользователь увидит «странный» неполный список.
onChange={({ selected }) => onProjectChange(Number(selected?.key))}
// ↑ нет setProjectSearch('') — UX-баг
```

---

## 📍 Применение в проекте

| Что | Файл | Что добавилось |
|-----|------|----------------|
| Поле `projectId` в списке сессий | `KiloImportService.Api/Controllers/ImportsController.cs` (`List`) | `[FromQuery] int? projectId`, LEFT JOIN на `CachedProjects` для `projectName` |
| `excludeSheets` + `sheetTotals` в отчёте | `KiloImportService.Api/Controllers/ImportsController.cs` (`GetReport`) | `[FromQuery] string[]? excludeSheets`, `take` 100 → 50 |
| `Select` «Проект» в фильтрах | `KiloImportService.Web/src/components/ImportHistory/HistoryFilters.tsx` | `useBackendProjects`, sticky-опция выбранного проекта |
| `projectId` в hooks/services | `useImportsHistory.ts`, `importsService.ts`, `importMappers.ts`, `types/api.ts`, `types/session.ts` | Сквозной проброс параметра и поля сводки |
| Сворачивание листов в таблице | `KiloImportService.Web/src/components/ImportSession/SessionRowsTable.tsx` | `collapsedSheets` state, `SheetHeaderRow` с кнопкой-аккордеоном, idempotent-guard через `lastSentRef` |
| `excludeSheets` в хуках | `useImportSession.ts`, `useImportSessionDetail.ts` | `excludeSheetsRef`, проброс в `getImportReport` |
| `REPORT_PAGE_SIZE = 50` | `useImportSession.ts` | Размер страницы 100 → 50 |
| CSS заголовков-аккордеонов | `App.css` | `.sheet-header-row__toggle`, `.sheet-header-row__chevron`, `--collapsed`-модификатор |
| **v1.1**: фикс `searchProps.onChange` | `KiloImportService.Web/src/components/ImportHistory/HistoryFilters.tsx` | `(e) => e.target.value` → `(value: string) => …`; `placeholder` перенесён в `componentProps` |
| **v1.1**: empty-state с учётом фильтров | `KiloImportService.Web/src/components/ImportHistory/HistorySessionsTable.tsx`, `ImportHistoryPage.tsx` | Новый prop `hasActiveFilters`; разделение сообщений «нет сессий» / «нет под фильтры» |
| **v1.1**: расширена «типичная ошибка №4» | `doc_project/20-select-with-search.md` | Симптом TypeError + правильная сигнатура `searchProps.onChange` |

---

## 🎯 Чек-лист

- [x] Backend `GET /api/imports` принимает `projectId` и отдаёт `projectName` через LEFT JOIN
- [x] Backend `GET /api/imports/{id}/report` принимает `excludeSheets[]`, отдаёт `sheetTotals[]`, дефолтный `take=50`
- [x] UI `HistoryFilters`: Select «Проект» с поиском и sticky-опцией выбранного
- [x] UI `HistorySessionsTable`: колонка «Проект» с фолбэком на `Проект #id`
- [x] UI `SessionRowsTable`: сворачивание листа через `<button>` в заголовке, отображение свёрнутых в конце таблицы
- [x] Idempotent-guard для первого render'а `collapsedSheets`
- [x] `REPORT_PAGE_SIZE = 50` единственное место (`useImportSession`)
- [x] `excludeSheetsRef` запоминается между сменой страницы / reload
- [x] Сброс `excludeSheetsRef` при смене `sessionId` в истории
- [x] Документация обновлена: `87-report-pagination.md` (100 → 50)
- [x] **v1.1**: `searchProps.onChange` принимает `string`, не event (см. `20-select-with-search.md` «Ошибка 4»)
- [x] **v1.1**: `placeholder` для поиска внутри Select идёт через `componentProps`, а не на верхний уровень `searchProps`
- [x] **v1.1**: empty-state в `HistorySessionsTable` различает «нет сессий вообще» и «нет под текущие фильтры» (prop `hasActiveFilters`)
