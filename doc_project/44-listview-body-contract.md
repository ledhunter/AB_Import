# 📡 Контракт тела запроса Visary ListView API

## 📋 Описание

Реальный контракт тела POST-запроса к Visary `/api/visary/listview/<mnemonic>/...` отличается от того, что исторически было зафиксировано в документации. Этот документ фиксирует **актуальный** набор полей, полученный из DevTools реального Visary UI (2026-05-04), и объясняет, как собирать тело правильно для разных типов эндпоинтов.

> 🔁 См. также: `21-sites-by-project.md`, `10-listview-library.md`, `08-visary-api-integration.md`, `43-sites-filter-fix.md`.

---

## ✅ Актуальный контракт тела

```json
{
  "Mnemonic": "constructionsite",
  "PageSkip": 0,
  "PageSize": 50,
  "Columns": ["ID", "Title", "..."],
  "Sorts": "[{\"selector\":\"ID\",\"desc\":false}]",
  "Hidden": false,
  "ExtraFilter": null,
  "SearchPhrase": null,
  "Summaries": []
}
```

### 📝 Поля

| Поле | Тип | Обязательное | Описание |
|------|-----|--------------|----------|
| `Mnemonic` | `string` | ✅ | Имя сущности, например `constructionproject`, `constructionsite` |
| `PageSkip` | `number` | ✅ | Сколько записей пропустить (пагинация) |
| `PageSize` | `number` | ✅ | Размер страницы |
| `Columns` | `string[]` | ✅ | Список запрашиваемых полей (PascalCase как у Visary) |
| `Sorts` | `string` | ✅ | JSON-строка с сортировками, например `[{"selector":"ID","desc":true}]` |
| `Hidden` | `boolean` | ✅ | Показывать ли архивные/скрытые записи (обычно `false`) |
| `ExtraFilter` | `string \| null` | ⚠️ опц. | DevExtreme-подобный фильтр, например `[["Type","=","Жилой"]]` |
| `SearchPhrase` | `string \| null` | ✅ | Фраза полнотекстового поиска. **`null`** когда не используется (пустая строка не принимается частью эндпоинтов) |
| `Summaries` | `unknown[]` | ✅ | Массив агрегаций (ListView UI использует для сумм). Передавать `[]`. |
| `AssociationFilter` | `object \| null` | ❌ **НЕ** для `/onetomany/*` | Для обычных ListView — объект `{ AssociatedId, Filters }`. Для `/onetomany/*` — не передавать, фильтровать через query |

---

## 🔀 Два вида эндпоинтов — разные способы фильтрации

### 1️⃣ Обычный ListView (`/listview/<mnemonic>`)

```
POST /api/visary/listview/constructionproject
```

Фильтрация связей — через поле `AssociationFilter` в теле:

```json
{
  "Mnemonic": "...",
  "PageSkip": 0,
  "PageSize": 50,
  "Columns": ["ID", "Title"],
  "Sorts": "[{\"selector\":\"ID\",\"desc\":true}]",
  "Hidden": false,
  "ExtraFilter": null,
  "SearchPhrase": null,
  "Summaries": [],
  "AssociationFilter": {
    "AssociatedId": 4584,
    "Filters": null
  }
}
```

### 2️⃣ One-to-many эндпоинт (`/listview/<mnemonic>/onetomany/<RelationName>`)

```
POST /api/visary/listview/constructionsite/onetomany/Project?associationId=4584
```

- ID связанной сущности передаётся **как query parameter** `associationId`
- `AssociationFilter` в теле **НЕ передавать** — иначе Visary игнорирует фильтрацию и возвращает все записи

```json
{
  "Mnemonic": "constructionsite",
  "PageSkip": 0,
  "PageSize": 50,
  "Columns": ["ID", "Title"],
  "Sorts": "[{\"selector\":\"ID\",\"desc\":false}]",
  "Hidden": false,
  "ExtraFilter": null,
  "SearchPhrase": null,
  "Summaries": []
}
```

---

## ❌ Типичные ошибки

### Ошибка 1: Использовать `SearchString` вместо `SearchPhrase`

```ts
// ❌ НЕПРАВИЛЬНО — старое имя поля
body.SearchString = query.searchString ?? '';
```

**Симптом:** сервер либо возвращает 400, либо игнорирует поиск.

**Правильно:**
```ts
// ✅
body.SearchPhrase = query.searchString || null;
```

### Ошибка 2: Передавать `AssociationFilter` в теле для `/onetomany/*`

```ts
// ❌ НЕПРАВИЛЬНО — фильтр будет проигнорирован
body.AssociationFilter = { AssociatedId: projectId, Filters: null };
```

**Симптом:** запрос проходит (200 OK), но возвращаются **все** объекты системы, а не только из выбранного проекта.

**Правильно:**
```ts
// ✅ Для /onetomany — associationId идёт в URL, AssociationFilter НЕ передаём в теле
url += `?associationId=${projectId}`;
// body БЕЗ AssociationFilter
```

### Ошибка 3: Забыть `Summaries: []`

```ts
// ❌ Поле отсутствует — некоторые версии бэка могут возвращать 400
// body.Summaries не задан
```

**Правильно:**
```ts
// ✅ Всегда передавать пустой массив
body.Summaries = [];
```

---

## ✅ Правильная реализация в проекте

Файл: `KiloImportService.Web/src/services/listView/createListViewService.ts`

```ts
export function buildListViewRequestBody<TRaw, TItem>(
  config: ListViewServiceConfig<TRaw, TItem>,
  query: ListViewQuery = {},
): ListViewRequestBody {
  const body: ListViewRequestBody = {
    Mnemonic: config.mnemonic,
    PageSkip: query.pageSkip ?? 0,
    PageSize: query.pageSize ?? config.defaultPageSize ?? DEFAULT_PAGE_SIZE,
    Columns: config.columns,
    Sorts: query.sorts ?? config.defaultSort ?? DEFAULT_SORT,
    Hidden: false,
    ExtraFilter: query.extraFilter ?? null,
    SearchPhrase: query.searchString || null,  // 👈 SearchPhrase, не SearchString
    Summaries: [],                              // 👈 Всегда []
  };

  // AssociationFilter НЕ передаётся в теле для /onetomany эндпоинтов.
  // Вместо этого используется query parameter associationId (см. fetch()).
  if (query.associationFilter && !config.pathSuffix?.includes('/onetomany/')) {
    body.AssociationFilter = query.associationFilter;
  }

  return body;
}

async function fetch(query: ListViewQuery = {}): Promise<ListViewResult<TItem>> {
  const body = buildListViewRequestBody(config, query);

  // Для эндпоинта /onetomany/<Relation> нужен query parameter associationId
  const queryParams = query.associationFilter?.AssociatedId
    ? { associationId: query.associationFilter.AssociatedId }
    : undefined;

  const raw = await visaryPost<ListViewResponseRaw<TRaw>>(path, body, {
    signal: query.signal,
    queryParams,
  });
  return parseListViewResponse(raw, config.toItem);
}
```

---

## 🧪 Как проверить, что контракт правильный

### curl

```powershell
$token = "<JWT_TOKEN>"
$body = '{"Mnemonic":"constructionsite","PageSkip":0,"PageSize":5,"Columns":["ID","Title"],"Sorts":"[{\"selector\":\"ID\",\"desc\":false}]","Hidden":false,"ExtraFilter":null,"SearchPhrase":null,"Summaries":[]}'

Invoke-WebRequest `
  -Uri "https://isup-alfa-test.k8s.npc.ba/api/visary/listview/constructionsite/onetomany/Project?associationId=4584" `
  -Method POST `
  -Headers @{"Authorization"="Bearer $token"; "Content-Type"="application/json"} `
  -Body $body `
  -UseBasicParsing
```

Ожидаемый ответ — `200 OK` с JSON вида:

```json
{
  "Data": [ { "ID": 7809, "Title": "Корпус ..." }, ... ],
  "Total": 4,
  "Summaries": []
}
```

### Unit-тесты

Файлы:
- `KiloImportService.Web/src/services/listView/__tests__/createListViewService.test.ts`
- `KiloImportService.Web/src/services/listView/__tests__/sites.test.ts`

Запуск:
```powershell
cmd /c "npx tsx src/services/listView/__tests__/createListViewService.test.ts"
cmd /c "npx tsx src/services/listView/__tests__/sites.test.ts"
```

Ожидается **14/14 passed**.

---

## 📍 Применение в проекте

| Слой | Файл |
|------|------|
| Сборка тела запроса | `KiloImportService.Web/src/services/listView/createListViewService.ts` |
| Типы `ListViewQuery`, `AssociationFilter` | `KiloImportService.Web/src/services/listView/types.ts` |
| Хелпер для объектов по проекту | `KiloImportService.Web/src/services/listView/entities/sites.ts` |
| Хук UI | `KiloImportService.Web/src/hooks/useSites.ts` |

---

## 🎯 Чек-лист при добавлении нового ListView-эндпоинта

- [ ] Определён `mnemonic` и правильный `pathSuffix` (если `/onetomany/...` — указать явно)
- [ ] В теле используется `SearchPhrase` (не `SearchString`)
- [ ] В теле есть `Summaries: []`
- [ ] Для `/onetomany/*` — `AssociationFilter` НЕ попадает в тело, а `associationId` уходит в query
- [ ] Написан curl-тест с реальным токеном → получен 200 OK с фильтрованными данными
- [ ] Обновлены unit-тесты `buildListViewRequestBody`
