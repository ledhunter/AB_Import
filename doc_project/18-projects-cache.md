# 🗂️ Кэш проектов Visary с поиском-as-you-type

## 📋 Описание

При выборе проекта в форме импорта пользователь видит **выпадающий список с поиском**. Источник данных — Visary, но запросы идут через собственный backend, который держит **локальный кэш** проектов. Это даёт:

- Мгновенный отклик при наборе текста (без сетевого RTT в Visary на каждое нажатие).
- Защиту от лимита `PageSize=50` Visary ListView API: backend забирает все страницы.
- Fallback — если в кэше ничего не нашли по подстроке, backend сам спросит Visary с `SearchString` и upsert'ит результат.

> 🔁 См. также: `08-visary-api-integration.md`, `09-lazy-loaded-select.md`, `10-listview-library.md`.

---

## 🏗️ Архитектура

```
┌─────────────────────────────────────────────────────────────┐
│  UI: ImportForm.tsx                                         │
│       Select(showSearch, searchProps={value,onChange})      │
│       │                                                     │
│       ▼                                                     │
│  hooks/useBackendProjects.ts                                │
│       sync()  → probe (search '') → если пусто → POST /sync │
│       debounce(300ms) → search (GET /api/projects/search)   │
│       graceful-fallback: при failed sync ещё раз search'им  │
│       │                                                     │
│       ▼                                                     │
│  services/projectsBackendApi.ts                             │
│       │                                                     │
│       ▼  (vite proxy: /api/projects → backend:5000)         │
│  Controllers/ProjectsController                             │
│       │                                                     │
│       ▼                                                     │
│  Domain/Projects/ProjectsCacheService                       │
│   ├── SyncAllAsync() — постранично VisaryListViewClient     │
│   │                     → upsert в import.cached_projects   │
│   └── SearchAsync(q)  — ILIKE по локальному кэшу;           │
│                          если пусто → Visary fallback +     │
│                          upsert + повторный поиск           │
│       │                                                     │
│       ├──────► ImportServiceDbContext.CachedProjects        │
│       └──────► IVisaryListViewClient (HttpClient + Bearer)  │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔧 Контракт API (backend)

### `POST /api/projects/sync`

Идемпотентная полная синхронизация. Backend пагинирует Visary ListView (`Mnemonic=constructionproject`) пока `skip < total`, апсертит каждую страницу.

⚠️ Реальный путь Visary — `https://isup-alfa-test.k8s.npc.ba/api/visary/listview/constructionproject` (с префиксом `/visary/`). Без него сервер отдаёт **405 Not Allowed** — это нюанс, на котором мы один раз залипли. Зашит в `@KiloImportService.Api/Domain/Visary/VisaryListViewClient.cs`.

**Ответ**:
```json
{ "total": 1242, "upserted": 1242, "durationMs": 1830 }
```

### `GET /api/projects/search?q=строка&limit=50`

1. Локальный поиск: `Title.ToLower().Contains(q)` OR `IdentifierKK.ToLower().Contains(q)` OR `IdentifierZPLM.ToLower().Contains(q)`.
2. Если локально ничего → **fallback**: Visary с `SearchString=q`, `PageSize=50`, upsert, повторный локальный поиск.
3. Пустой `q` → первая страница, отсортированная по `Title`.

**Ответ**:
```json
{
  "items": [
    { "id": 42, "title": "ЖК Север", "code": "ZK-42" }
  ],
  "fromFallback": true,
  "total": 1
}
```

---

## ⚙️ Конфигурация backend

В `appsettings.json` (или через переменные окружения с двойным подчёркиванием):

```json
"Visary": {
  "BaseUrl": "https://isup-alfa-test.k8s.npc.ba",
  "BearerToken": "<JWT>",
  "SyncPageSize": 200,
  "RequestTimeout": "00:00:30"
}
```

Эквивалент в env:
```
Visary__BaseUrl=https://isup-alfa-test.k8s.npc.ba
Visary__BearerToken=<JWT>
```

`BearerToken` **никогда** не коммитим — задаём через `dotnet user-secrets` или docker `.env`.

### 🐳 Запуск через docker-compose

1. **Создай `.env`** в корне `AB_Import` (файл уже в `.gitignore`):
   ```env
   VITE_VISARY_API_URL=https://isup-alfa-test.k8s.npc.ba
   VITE_VISARY_API_TOKEN=<JWT без префикса "Bearer ">
   Visary__BaseUrl=https://isup-alfa-test.k8s.npc.ba
   Visary__BearerToken=<тот же JWT>
   ```
   ⚠️ JWT **без** префикса `Bearer ` — `VisaryListViewClient` сам добавит его в заголовок.

2. **`docker-compose.yml`** уже пробрасывает все четыре переменных:
   - `frontend.environment.VITE_VISARY_API_URL` / `VITE_VISARY_API_TOKEN` — для прямых вызовов Visary из UI (старый flow `useSites`).
   - `backend.environment.Visary__BaseUrl` / `Visary__BearerToken` — для нового кэша проектов.

3. **Запуск**:
   ```powershell
   docker compose up -d --build
   docker compose logs -f backend   # Дождись "Now listening on: http://[::]:5000"
   ```

4. **Прогрей кэш** (опционально — UI сам сделает это при первом open Select «Проект»):
   ```powershell
   curl.exe -X POST http://localhost:5000/api/projects/sync
   ```

### ❌ Типичный фейл с docker-compose

Если в `docker-compose.yml` **не указать** `Visary__BearerToken`, backend стартует без ошибок, но первый же `/sync` упадёт:

```
InvalidOperationException: Visary:BearerToken не задан в конфигурации.
```

Это **намеренное** поведение — fail-fast в `VisaryListViewClient.FetchProjectsAsync`. Проверка делается per-request, а не при старте, потому что в тестах backend может работать без Visary вообще.

---

## 🗄️ Схема БД

Таблица `import.cached_projects` (миграция `20260429170000_AddCachedProjects`):

| Колонка        | Тип                       | Примечание                        |
|----------------|---------------------------|-----------------------------------|
| `Id`           | `integer`, PK, не auto    | = ID из Visary                    |
| `Title`        | `varchar(500) NOT NULL`   | название проекта                  |
| `IdentifierKK` | `varchar(64) NULL`        | код КК                            |
| `IdentifierZPLM` | `varchar(64) NULL`      | код ZPLM                          |
| `LastSyncedAt` | `timestamptz NOT NULL`    | когда апсертилось последний раз   |

Индексы:
- `IX_CachedProject_Title` — под подстроковый поиск (b-tree, ускоряет only префиксы; для `%foo%` эффект ограничен — при росте кэша имеет смысл переход на `pg_trgm` GIN).
- `IX_CachedProject_IdentifierKK` — для точного/подстрокового поиска по коду.

---

## 🔁 Поведение фронта

`hooks/useBackendProjects.ts`:

```ts
const projects = useBackendProjects({ searchString, debounceMs: 300 });
```

Состояния (`status`):
- `idle` — кэш ни разу не прогрет.
- `loading` — идёт probe-search или обычный поиск.
- `syncing` — идёт `POST /sync` (только при пустом кэше).
- `success` — есть `data` (может быть пустой массив).
- `error` — `error` содержит сообщение, в UI рендерится строка-«Повторить».

### ✅ Стратегия probe-then-sync (важно)

Раньше `sync()` всегда дёргал `POST /api/projects/sync` и ждал 18+ секунд при первом open Select. Сейчас логика двухступенчатая и **резистентная** к outage Visary:

```ts
// Псевдокод useBackendProjects.sync():
async function sync() {
  if (isWarmed) return;

  // ── Шаг 1. Probe: пробуем читать кэш
  const probe = await searchProjects('', { limit });
  if (probe.items.length > 0) {
    // Кэш уже был заполнен ранее → используем его, sync пропускаем.
    setData(probe.items); setStatus('success'); setWarmed();
    return;
  }

  // ── Шаг 2. Кэш пуст → синхронизация с Visary
  try {
    await syncProjects();
    await runSearch(); // подтягиваем первую страницу
  } catch (err) {
    // ── Graceful fallback: даже если sync упал, пробуем search.
    // Вдруг прошлый sync уже что-то положил, или 502 был транзиентным.
    const fallback = await searchProjects('', { limit });
    if (fallback.items.length > 0) {
      setData(fallback.items); setStatus('success'); setWarmed();
      return;
    }
    // Только если и кэш пуст, и sync упал → state='error'.
    setError(err.message); setStatus('error');
  }
}
```

### Жизненный цикл

1. Open Select «Проект» → `projects.sync()`.
2. Если в БД уже есть проекты (типичный случай после первого запуска) — мгновенный показ первых 50.
3. Если БД пустая → 18-сек синхронизация с Visary, потом показ.
4. Юзер печатает → `setProjectSearch(value)` → debounce 300мс → `search(value)`.
5. `fromFallback: true` → под Select подсказка «Подгружено из Visary по запросу.».

---

## ❌ Типичные ошибки

### 1. ILIKE на InMemory EF
В unit-тестах используем InMemory-провайдер, который **не понимает** `EF.Functions.ILike`. Поэтому в `ProjectsCacheService.SearchLocalAsync` поиск реализован через `ToLower().Contains(...)` — провайдер-агностично. Для production-нагрузки на больших кэшах рассмотри переход на `pg_trgm` + GIN-индекс.

### 2. Bearer-токен в код
`VisaryListViewClient` берёт токен из `IOptions<VisaryApiOptions>`. Никогда не хардкодим в код, не коммитим в `appsettings.json` (там пустая строка-маркер).

### 3. Sync без идемпотентности
`SyncAllAsync` upsert'ит по PK (`Id`) — повторный вызов безопасен. Но **не вызывай** sync на каждое нажатие клавиши — фронт триггерит его только при первом open Select (через `isWarmed`).

### 4. Дебаунс на фронте без cleanup
`useBackendProjects` использует `setTimeout` + `clearTimeout` в cleanup'е useEffect и `AbortController` для прерывания предыдущих запросов. Без этого получаем «гонку»: ранний ответ перезаписывает поздний.

### 5. Миграция, созданная без `dotnet ef migrations add`

Миграция `20260429170000_AddCachedProjects` была изначально написана **руками** (без EF tools — в среде не было SDK). Это работает, но требует синхронности **трёх** файлов:

| Файл | Что содержит |
|------|--------------|
| `_AddCachedProjects.cs` | `Up()` / `Down()` — DDL операции, **выполняется** при `database update` |
| `_AddCachedProjects.Designer.cs` | `BuildTargetModel()` — снимок модели **после** этой миграции |
| `ImportServiceDbContextModelSnapshot.cs` | `BuildModel()` — снимок модели **последней** миграции (= Designer верхней миграции) |

#### ⚠️ Правило

`Designer.cs` верхней миграции должен **попроцентно совпадать** с `ImportServiceDbContextModelSnapshot.cs`. Если расходятся — следующий `dotnet ef migrations add` сгенерирует «лишние» Up/Down-операции, пытаясь свести модель к snapshot'у.

#### ✅ Как делать впредь

После того как .NET 10 SDK установлен — **используй EF tools**, не пиши миграции руками:

```powershell
# Удалить ручную миграцию (если ещё не применена)
dotnet ef migrations remove --project .\KiloImportService.Api

# Сгенерировать заново
dotnet ef migrations add AddCachedProjects --project .\KiloImportService.Api

# Применить к БД (либо положиться на auto-migrate в Program.cs)
dotnet ef database update --project .\KiloImportService.Api
```

### 6. Backend читает `Visary:` секцию, а не `VITE_VISARY_*`

**Не путай**:
- `VITE_VISARY_API_URL` / `VITE_VISARY_API_TOKEN` — используются только Vite/UI (`import.meta.env.VITE_*`).
- `Visary__BaseUrl` / `Visary__BearerToken` — читаются только backend'ом через `IOptions<VisaryApiOptions>`.

В `.env` для docker-compose должны быть **обе** пары.

---

## 🧪 Тесты

Backend (xUnit, in-memory EF):

```powershell
dotnet test KiloImportService.Api.Tests --filter FullyQualifiedName~ProjectsCacheServiceTests
```

Покрывают:
- `SyncAllAsync_PaginatesUntilTotal_AndUpserts`
- `SyncAllAsync_StopsWhenServerReturnsEmptyPage`
- `SyncAllAsync_UpdatesExistingProjectsById`
- `SyncAllAsync_DropsRowsWithInvalidId`
- `SyncAllAsync_FillsTitlePlaceholder_WhenNullOrEmpty`
- `SearchAsync_EmptyQuery_ReturnsOrderedLocalWithoutVisary`
- `SearchAsync_FallbackToVisary_WhenLocalEmpty_AndUpserts`
- `SearchAsync_FallbackReturnsNothing_WhenVisaryEmpty`

---

## 📍 Файлы

| Слой | Файл |
|------|------|
| Backend конфиг | `KiloImportService.Api/Domain/Visary/VisaryApiOptions.cs` |
| Backend HTTP-клиент | `KiloImportService.Api/Domain/Visary/VisaryListViewClient.cs` |
| Backend DTO | `KiloImportService.Api/Domain/Visary/VisaryDtos.cs` |
| Backend entity | `KiloImportService.Api/Data/Entities/CachedProject.cs` |
| Backend service | `KiloImportService.Api/Domain/Projects/ProjectsCacheService.cs` |
| Backend controller | `KiloImportService.Api/Controllers/ProjectsController.cs` |
| Backend миграция | `KiloImportService.Api/Migrations/20260429170000_AddCachedProjects.cs` |
| Backend тесты | `KiloImportService.Api.Tests/Projects/ProjectsCacheServiceTests.cs` |
| Frontend клиент | `KiloImportService.Web/src/services/projectsBackendApi.ts` |
| Frontend хук | `KiloImportService.Web/src/hooks/useBackendProjects.ts` |
| Frontend форма | `KiloImportService.Web/src/components/ImportForm/ImportForm.tsx` |
| Vite proxy | `KiloImportService.Web/vite.config.ts` (`/api/projects` → backend) |

> ⚠️ Старый хук `useProjects` (прямой Visary через UI-прокси) остаётся для обратной совместимости — но больше **не используется** в форме импорта. Удалить можно после стабилизации backend-варианта.
