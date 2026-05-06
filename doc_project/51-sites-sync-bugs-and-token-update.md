# 🐛 Два бага синхронизации объектов + обновление токена

## 📋 Описание

**Статус**: ✅ Исправлено  
**Дата**: 2026-05-06  
**Симптом**: `POST /api/sites/sync/{id} → 404 Not Found (0ms)` при выборе объекта строительства в UI

---

## 🔍 Диагностика по времени ответа

Время `(0ms)` в браузере — **ключевой диагностический признак**:

| Время ответа | Что произошло |
|---|---|
| `(0ms)` | Ответ вернул Vite Dev Server сам, до проксирования (нет правила proxy) |
| `(3–50ms)` | Запрос дошёл до backend, но тот вернул ошибку |
| `(50–500ms)` | Запрос дошёл до backend и выполнил реальную работу (обращение к Visary) |

---

## ❌ Баг 1: `/api/sites` не добавлен в Vite proxy

### Симптом
```
POST http://localhost:5173/api/sites/sync/7849 404 (Not Found)  (0ms)
```

### Причина
В `vite.config.ts` route `/api/sites` отсутствовал в списке проксируемых маршрутов.
Vite возвращал 404 **не передавая запрос на backend вообще**.

### ❌ Было (старый образ)
```typescript
// vite.config.ts
proxy: {
  '/api/visary':      { target: visaryTarget, ... },
  '/api/imports':     backendProxy(),
  '/api/import-types': backendProxy(),
  '/api/projects':    backendProxy(),
  // '/api/sites' ОТСУТСТВОВАЛ
  '/hubs':            backendProxy({ ws: true }),
  '/health':          backendProxy(),
  '/swagger':         backendProxy(),
}
```

### ✅ Исправлено
```typescript
// vite.config.ts
proxy: {
  '/api/visary':       { target: visaryTarget, ... },
  '/api/imports':      backendProxy(),
  '/api/import-types': backendProxy(),
  '/api/projects':     backendProxy(),
  '/api/sites':        backendProxy(),   // ← ДОБАВЛЕНО
  '/hubs':             backendProxy({ ws: true }),
  '/health':           backendProxy(),
  '/swagger':          backendProxy(),
}
```

### ⚠️ Важно: правило применения после изменения vite.config.ts в Docker

`docker compose restart frontend` **НЕ перечитывает** `vite.config.ts` из образа — он перезапускает контейнер с **тем же слоем файловой системы**, что был при сборке образа.

**Быстрый способ** (без полной пересборки):
```bash
# 1. Скопировать обновлённый файл в работающий контейнер
docker cp ./KiloImportService.Web/vite.config.ts kilo-import-frontend:/app/vite.config.ts

# 2. Перезапустить контейнер (Vite перечитает конфиг при старте)
docker compose restart frontend

# 3. Проверить что правило применилось
docker exec kilo-import-frontend grep "api/sites" /app/vite.config.ts
```

**Правильный способ** (на постоянно, для следующих деплоев):
```bash
docker compose build frontend   # пересобрать образ из обновлённого источника
docker compose up -d frontend   # поднять новый контейнер
```

---

## ❌ Баг 2: SitesSyncService создавал HttpClient вручную

### Симптом
После фикса Vite proxy — запрос доходил до backend, но backend возвращал 404 с сообщением:
```
VisaryApiError: Объект строительства 7850 не найден в Visary
```

### Причина
`SitesSyncService` содержал метод `GetListViewClient()`, который создавал `new HttpClient()` вручную, а не использовал DI-инжектированный `IListViewClient`.

Внутри Docker-контейнера `new HttpClient()`:
- **Не получает** SSL/TLS настройки от `IHttpClientFactory`
- **Не имеет** connection pooling и retry политик
- Результат: запрос к `https://isup-alfa-test.k8s.npc.ba` завершался ошибкой (или таймаутом), метод возвращал `null` → backend бросал `KeyNotFoundException` → 404

### ❌ Было
```csharp
public sealed class SitesSyncService : ISitesSyncService
{
    private readonly VisaryDbContext _db;
    private readonly ICrudClient _visaryClient;
    private readonly VisaryOptions _options;       // ← брал Options вручную
    private readonly ILogger<SitesSyncService> _log;

    public SitesSyncService(VisaryDbContext db, ICrudClient visaryClient,
        IOptions<VisaryOptions> options, ILogger<SitesSyncService> log)
    { ... }

    public async Task<bool> SyncAsync(int siteId, int projectId, CancellationToken ct)
    {
        var client = GetListViewClient();           // ← создавал НОВЫЙ клиент!
        var siteData = await client.GetSiteByProjectAndIdAsync(projectId, siteId, ct);
        ...
    }

    // ❌ Антипаттерн: new HttpClient() в серьёзном коде
    private IListViewClient GetListViewClient()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        return new ListViewClient(
            new HttpClient(),               // ← raw HttpClient без фабрики!
            Options.Create(_options),
            loggerFactory.CreateLogger<ListViewClient>());
    }
}
```

### ✅ Исправлено
```csharp
public sealed class SitesSyncService : ISitesSyncService
{
    private readonly VisaryDbContext _db;
    private readonly ICrudClient _crudClient;
    private readonly IListViewClient _listViewClient;   // ← инжектируем напрямую
    private readonly ILogger<SitesSyncService> _log;

    public SitesSyncService(
        VisaryDbContext db,
        ICrudClient crudClient,
        IListViewClient listViewClient,                 // ← через DI
        ILogger<SitesSyncService> log)
    {
        _db = db;
        _crudClient = crudClient;
        _listViewClient = listViewClient;
        _log = log;
    }

    public async Task<bool> SyncAsync(int siteId, int projectId, CancellationToken ct)
    {
        var siteData = await _listViewClient.GetSiteByProjectAndIdAsync(projectId, siteId, ct);
        if (siteData == null)
            throw new KeyNotFoundException(...);
        ...
    }
    // Никакого GetListViewClient() — HttpClient управляется фабрикой
}
```

---

## 🔑 Обновление Bearer Token

Visary JWT токены живут ~1 час. При истечении токена все запросы к Visary возвращают 401.

### Где хранится токен

| Файл | Переменная | Назначение |
|---|---|---|
| `.env` (gitignored) | `Visary__BearerToken` | Backend (Docker env) |
| `.env` (gitignored) | `VITE_VISARY_API_TOKEN` | Frontend Visary proxy |
| `appsettings.json` | `Visary:BearerToken` | Локальный запуск backend |

### Алгоритм обновления (PowerShell)

```powershell
$newToken = "eyJhbGci..."   # новый токен из браузера

$envPath = "C:\...\AB_Import\.env"
$content = Get-Content $envPath -Raw
$content = $content -replace '(?m)^Visary__BearerToken=.*$', "Visary__BearerToken=$newToken"
$content = $content -replace '(?m)^VITE_VISARY_API_TOKEN=.*$', "VITE_VISARY_API_TOKEN=$newToken"
Set-Content $envPath -Value $content -NoNewline
```

### Перезапуск после обновления

```bash
# Только backend (frontend не зависит от серверного токена)
docker compose up -d --no-build backend

# Проверка: синхронизация проектов должна вернуть 2000+ записей
Invoke-RestMethod -Uri "http://localhost:5000/api/projects/sync" -Method Post
```

> ℹ️ Frontend использует `VITE_VISARY_API_TOKEN` только для **прямых запросов к Visary** (`/api/visary/*`).
> Backend использует `Visary__BearerToken` для **всех остальных** запросов через `IListViewClient` / `ICrudClient`.

---

## 🏗️ Правила DI для HTTP-клиентов

### ✅ Всегда инжектировать через DI

```csharp
// ✅ Правильно — управляется IHttpClientFactory
public class MyService
{
    private readonly IListViewClient _listViewClient;
    private readonly ICrudClient _crudClient;

    public MyService(IListViewClient listViewClient, ICrudClient crudClient)
    {
        _listViewClient = listViewClient;
        _crudClient = crudClient;
    }
}
```

### ❌ Никогда не создавать HttpClient вручную в сервисах

```csharp
// ❌ Никогда так — особенно в Docker!
private IListViewClient GetListViewClient()
{
    return new ListViewClient(
        new HttpClient(),   // ← нет SSL цепочки, нет пула, утечка соединений
        Options.Create(_options),
        logger);
}
```

### Почему это ломается в Docker, но может работать локально

| Среда | Поведение raw `new HttpClient()` |
|---|---|
| Windows localhost | Часто работает — системный TLS стек наследуется |
| Docker (Linux Alpine) | Падает — отдельный TLS контекст, нет корневых CA окружения |

---

## 📍 Применение в проекте

| Компонент | Файл | Статус |
|---|---|---|
| `SitesSyncService` | `Domain/Sites/SitesSyncService.cs` | ✅ Исправлен |
| Vite proxy config | `KiloImportService.Web/vite.config.ts` | ✅ Исправлен |
| Bearer token storage | `.env` (gitignored) | ✅ Документировано |

---

## 🎯 Чек-лист при добавлении нового backend endpoint

- [ ] Добавить endpoint в `Controllers/`
- [ ] **Добавить путь в `vite.config.ts` proxy** (иначе 0ms 404 в dev)
- [ ] Не создавать `new HttpClient()` — инжектировать `IListViewClient` / `ICrudClient`
- [ ] Проверить маршрут через `http://localhost:5173/api/...` (не только через `:5000`)

---

**Версия**: 1.0  
**Дата**: 2026-05-06
