# 🔐 Visary Bearer-токен: единый источник в корневом `.env`

## 📋 Описание

**Статус**: ✅ Реализовано (v2 — консолидация)
**Дата**: 2026-05-06 (v1 hot-reload), 2026-05-06 (v2 SSOT)

В фазе активного тестирования токен Visary живёт ~1 час и обновляется по нескольку раз за день.
Раньше токен дублировался в **четырёх местах** (`.env`, `KiloImportService.Api/appsettings.Local.json`,
`KiloImportService.Api/.env`, `KiloImportService.Web/.env.local`) — при обновлении легко было забыть
один из них и получить 401 в случайной части стека.

Теперь токен живёт **только в корневом `.env`** (gitignored). Его читают:
- **docker-compose** — нативно подхватывает `.env` из cwd
- **Vite** (frontend) — через `envDir: '..'` в `vite.config.ts`
- **Backend (.NET) локально** — через мини-загрузчик `DotEnvLoader` в `Program.cs`
- **Live-тесты** — `VisaryLiveTestConfig.ReadDotEnv()`

---

## ✅ Правильная реализация

### Корневой `.env` (gitignored)

```dotenv
# Visary HTTP API — для backend (через docker и dotnet run)
Visary__BaseUrl=https://isup-alfa-test.k8s.npc.ba
Visary__BearerToken=eyJhbGci…

# Vite ENV (frontend)
VITE_VISARY_API_URL=https://isup-alfa-test.k8s.npc.ba
VITE_VISARY_API_TOKEN=eyJhbGci…
```

> **Чек обновления токена** теперь — отредактировать **этот** файл и перезапустить
> процессы (см. ниже). Других файлов с токеном в репо нет.

### Vite — `envDir: '..'`

```ts
// KiloImportService.Web/vite.config.ts
import path from 'node:path';
const envDir = path.resolve(process.cwd(), '..');

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, envDir, '');
  return {
    envDir,                          // ← Vite будет искать .env здесь
    server: { /* proxy ... */ },
  };
});
```

### Backend — `DotEnvLoader` + `AddEnvironmentVariables`

```csharp
// KiloImportService.Api/Program.cs (фрагмент)
DotEnvLoader.LoadFromAncestors(Directory.GetCurrentDirectory()); // ↑ ищет .env вверх по дереву
var builder = WebApplication.CreateBuilder(args);
// AddEnvironmentVariables вызывается автоматически в CreateBuilder.
// Visary__BearerToken (двойное подчёркивание) → конфиг-ключ "Visary:BearerToken".
```

```csharp
// KiloImportService.Api/Configuration/DotEnvLoader.cs
internal static class DotEnvLoader
{
    public static void LoadFromAncestors(string startDir, int maxDepth = 6)
    {
        // Ищет первый .env вверх от startDir и инжектит пары key=value в Environment.
        // Не перезаписывает уже выставленные переменные (env > файл).
    }
}
```

В docker-режиме `compose` сам инжектит `Visary__*` в env контейнера — `DotEnvLoader` тогда
не находит `.env` в файловой системе контейнера и тихо завершается (`maxDepth` исчерпан).

### Live-тесты — читают тот же `.env`

```csharp
// KiloImportService.Api.Tests/VisaryLive/VisaryLiveTestConfig.cs
private static (string? BaseUrl, string? Token) ReadDotEnv()
{
    var path = FindRepoFile(".env");
    // ... строчно парсит Visary__BaseUrl и Visary__BearerToken
}
```

Источники для live-тестов в порядке приоритета:
1. env var `VISARY_TEST_TOKEN` / `VISARY_TEST_BASEURL` (для CI)
2. `.audit/.token` (для audit-скриптов)
3. **корневой `.env`** (общий с docker/Vite/backend)

### ⚠️ Важно

- **Hot-reload токена больше нет.** В v1 было reloadOnChange + IOptionsMonitor — менял токен в файле,
  следующий запрос летел уже с новым. В v2 ради SSOT отказались: env-переменные читаются один раз
  при старте процесса (это ограничение `AddEnvironmentVariables`). Чтобы сменить токен:
  - **Docker**: `docker compose up -d --force-recreate backend frontend`
  - **Локальный `dotnet run`**: рестарт процесса
  - **Локальный `vite dev`**: рестарт vite (Ctrl+C → `npm run dev`)
- **Существующие env-переменные имеют приоритет.** `DotEnvLoader` НЕ перезатирает уже выставленные
  переменные — это позволяет docker-compose `environment:` секции и shell-export'ам перебить файл.
- **Чтение через `Visary__BearerToken` (двойное подчёркивание).** Стандартная .NET-конвенция
  для вложенных конфиг-ключей: `Section__Key` ⇒ `Section:Key`.
- **`.env` остаётся в `.gitignore`.** В репозиторий по-прежнему попадает только `.env.example`
  с пустыми значениями.

---

## ❌ Типичные ошибки

### 1. Положить токен в `appsettings.Local.json` снова

```json
// ❌ Файл удалён сознательно — это был четвёртый дубль.
{ "Visary": { "BearerToken": "eyJhbGci..." } }
```

Если положить файл обратно — backend локально подхватит через `IConfiguration` (порядок source'ов
включает Json-файлы), но docker-compose и Vite его не увидят. Снова разойдутся источники.
Обновление в `.env` всегда должно быть достаточным.

### 2. Положить токен в `KiloImportService.Web/.env.local`

```dotenv
# ❌ Vite теперь читает корневой .env через envDir
VITE_VISARY_API_TOKEN=eyJhbGci...
```

Файл удалён сознательно. Если `.env.local` всё-таки появится — Vite загрузит и его, и
корневой `.env` (Vite мерджит по приоритету: `.env.[mode].local` > `.env.local` > `.env.[mode]` > `.env`).
Ничего не сломается, но появится тот же дубль, который мы и убирали.

### 3. Перезапустить только backend, забыв frontend

```bash
# ❌ Vite по-прежнему отдаёт старый VITE_VISARY_API_TOKEN.
docker compose up -d --force-recreate backend
```

Vite инжектит токен в client-side bundle при старте контейнера. Без `--force-recreate frontend`
браузер будет слать в Visary (через прокси) старый токен.

```bash
# ✅ Оба сразу
docker compose up -d --force-recreate backend frontend
```

### 4. `IOptionsMonitor.CurrentValue` без рестарта

```csharp
// ❌ В v2 файл .env не watch'ится; CurrentValue возвращает значение, прочитанное при старте.
var token = _options.CurrentValue.BearerToken;  // ← этот же токен на всю жизнь процесса
```

Это поведение ок — мы сознательно отдали hot-reload в обмен на единый источник.
`IOptionsMonitor` всё ещё используется в `VisaryHttpBase` (на случай возврата watch'а позже),
но de-facto работает как `IOptions`.

---

## 📍 Применение в проекте

| Файл | Роль |
|------|------|
| `.env` (gitignored) | **Single Source Of Truth** для токена |
| [.env.example](../.env.example) | Шаблон с пустыми значениями (в git) |
| [docker-compose.yml](../docker-compose.yml) | Маппит `${Visary__BearerToken:-}` и `${VITE_VISARY_API_TOKEN:-}` в env контейнеров |
| [KiloImportService.Web/vite.config.ts](../KiloImportService.Web/vite.config.ts) | `envDir: '..'` — Vite читает корневой `.env` |
| [KiloImportService.Api/Configuration/DotEnvLoader.cs](../KiloImportService.Api/Configuration/DotEnvLoader.cs) | Мини-парсер `.env`, ищет вверх по дереву |
| [KiloImportService.Api/Program.cs](../KiloImportService.Api/Program.cs) | Вызывает `DotEnvLoader.LoadFromAncestors(...)` ДО `CreateBuilder` |
| [Visary.Api.Client/Common/VisaryHttpBase.cs](../Visary.Api.Client/Common/VisaryHttpBase.cs) | Принимает `IOptionsMonitor<VisaryOptions>` (без изменений) |
| [KiloImportService.Api.Tests/VisaryLive/VisaryLiveTestConfig.cs](../KiloImportService.Api.Tests/VisaryLive/VisaryLiveTestConfig.cs) | `ReadDotEnv()` — те же ключи `Visary__BaseUrl`/`Visary__BearerToken` |

### Удалено в v2

- `KiloImportService.Api/appsettings.Local.json`
- `KiloImportService.Api/.env`
- `KiloImportService.Web/.env.local`

---

## 🎯 Чек-лист обновления токена

- [ ] DevTools в Visary UI → Network → скопировать `Authorization: Bearer ...` (или из id-сервера)
- [ ] Открыть **корневой `.env`**, заменить `Visary__BearerToken=...` И `VITE_VISARY_API_TOKEN=...`
      на новый JWT (одно и то же значение)
- [ ] Перезапустить процессы:
  - **Docker (типичный сценарий)**: `docker compose up -d --force-recreate backend frontend`
  - **Локальный backend (`dotnet run`)**: Ctrl+C → `dotnet run`
  - **Локальный фронт (`npm run dev`)**: Ctrl+C → `npm run dev`
- [ ] Открыть UI, проверить что Visary-запросы идут с 200 (не 401)

---

## 🔄 Историческая справка (v1, оставлено для контекста)

В v1 (до 2026-05-06) был принят `appsettings.Local.json` с `reloadOnChange: true` и
`IOptionsMonitor`. Замена токена в файле подхватывалась без рестарта — удобно для часовой
ротации. Но это требовало 4 копий токена в разных файлах (compose читал `.env`, Vite — свой
`.env.local`, backend — `appsettings.Local.json`, и при этом `.env` в `KiloImportService.Api/`
оставался legacy-дублем).

В v2 пожертвовали hot-reload'ом ради единого источника. На практике рестарт двух контейнеров
занимает 5-10 секунд, а проблема «обновил в одном месте, забыл в трёх» исчезает.
