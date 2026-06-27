# 🏗️ Reverse-proxy base-path: публикация под `/api/ab-fm-import`

## 📋 Описание

Сервис публикуется на целевом стенде за reverse-proxy (nginx/k8s ingress) под URL-префиксами:

| Компонент | Префикс на стенде | Дефолт локально |
|-----------|-------------------|------------------|
| Backend API | `https://<host>/api/ab-fm-import/api/imports/…` | `http://localhost:5000/api/imports/…` |
| SignalR    | `https://<host>/api/ab-fm-import/hubs/imports` | `http://localhost:5000/hubs/imports` |
| SPA-UI     | `https://<host>/api/ab-fm-import-web/`        | `http://localhost:5173/` |
| Diag       | `https://<host>/api/ab-fm-import/diag`        | `http://localhost:5000/diag` |
| Health     | `https://<host>/api/ab-fm-import/health`      | `http://localhost:5000/health` |

⚠️ До этой правки backend и фронт работали ТОЛЬКО от корня. На стенде это давало «белый экран»:
браузер запрашивал `/assets/<hash>.js`, ingress отвечал 404 (фактический путь — `/api/ab-fm-import-web/assets/<hash>.js`).

---

## ✅ Правильная реализация

### 1. Backend — `Program.cs`

```csharp
// Forwarded headers — ingress присылает X-Forwarded-Proto/Host/Prefix.
// Без них фреймворк считает схему `http` и SignalR negotiate возвращает
// неверный URL (без https и без префикса). KnownNetworks/KnownProxies
// очищаем — в k8s pod-CIDR заранее неизвестен.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                       | ForwardedHeaders.XForwardedProto
                       | ForwardedHeaders.XForwardedHost
                       | ForwardedHeaders.XForwardedPrefix;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// PathBase — опционально. Пусто = маршруты от корня '/'.
// На стенде задаётся через Features:PathBase=/api/ab-fm-import.
var pathBase = (builder.Configuration["Features:PathBase"] ?? string.Empty).Trim();
if (!string.IsNullOrEmpty(pathBase))
{
    if (!pathBase.StartsWith('/')) pathBase = "/" + pathBase;
    pathBase = pathBase.TrimEnd('/');
    app.UsePathBase(pathBase);
}
app.UseForwardedHeaders();
```

### 2. Vite — `vite.config.ts`

```ts
const baseUrl = env.VITE_BASE_URL || '/';
const apiPrefix = (env.VITE_API_PREFIX || '').replace(/\/$/, '');

return {
  base: baseUrl,                                 // 👈 префикс ассетов
  define: {
    __API_PREFIX__: JSON.stringify(apiPrefix),   // 👈 build-time константа
    __BUILD_TIME__: JSON.stringify(new Date().toISOString()),
  },
  // …
};
```

### 3. API-клиент — `src/services/apiUrl.ts`

```ts
const PREFIX = __API_PREFIX__; // подставляется Vite на build

const IMPORTS = `${PREFIX}/api/imports` as const;
const HUBS    = `${PREFIX}/hubs` as const;
// …
```

### 4. Dockerfile (build-stage Web)

```dockerfile
ARG VITE_BASE_URL=/
ARG VITE_API_PREFIX=
ENV VITE_BASE_URL=$VITE_BASE_URL
ENV VITE_API_PREFIX=$VITE_API_PREFIX
RUN npm run build
```

### 5. Helm/k8s values

```yaml
backend:
  env:
    Features__PathBase: "/api/ab-fm-import"

frontend:
  buildArgs:
    VITE_BASE_URL: "/api/ab-fm-import-web/"     # 👈 trailing / для Vite
    VITE_API_PREFIX: "/api/ab-fm-import"         # 👈 без trailing /
```

### ⚠️ Важно

- `VITE_BASE_URL` — **с trailing `/`** (требование Vite). Иначе ассеты соберутся с двойным слешем.
- `VITE_API_PREFIX` — **без trailing `/`**. Префикс конкатенируется с `/api/imports`, лишний слеш ломает routing.
- Если UI и API под ОДНИМ префиксом — `VITE_BASE_URL=VITE_API_PREFIX+'/'`.
- На локалке все три переменные **пусты** — поведение не меняется.

---

## ❌ Типичные ошибки

```ts
// НЕПРАВИЛЬНО — Vite не понимает префикс из import.meta.env.BASE_URL в runtime коде.
const URL = `${import.meta.env.BASE_URL}api/imports`;
// → собирается без префикса, ассеты грузятся правильно, но fetch'и идут на /api/imports
```

```dockerfile
# НЕПРАВИЛЬНО — ARG объявлен ДО `FROM`, после FROM не виден.
ARG VITE_BASE_URL=/
FROM node:20-alpine AS build
RUN npm run build  # VITE_BASE_URL уже не существует
```

```csharp
// НЕПРАВИЛЬНО — UsePathBase ПОСЛЕ MapControllers (порядок важен).
app.MapControllers();
app.UsePathBase("/api/ab-fm-import");  // 👈 уже поздно, роуты примонтированы от корня
```

---

## 📍 Применение в проекте

| Слой | Файл | Что задаётся |
|------|------|---------------|
| Backend | `KiloImportService.Api/Program.cs` | `app.UsePathBase(...)` + `UseForwardedHeaders` + диагностический endpoint `/diag` |
| Vite | `KiloImportService.Web/vite.config.ts` | `base: baseUrl` + `define: __API_PREFIX__` |
| API-клиент | `KiloImportService.Web/src/services/apiUrl.ts` | `${PREFIX}/api/...` |
| TS-types | `KiloImportService.Web/src/vite-env.d.ts` | `declare const __API_PREFIX__: string` |
| Dockerfile | `KiloImportService.Web/Dockerfile` | `ARG VITE_BASE_URL` + `ARG VITE_API_PREFIX` → `ENV` перед `npm run build` |
| docker-compose | `docker-compose.yml` | `Features__PathBase`, `VITE_BASE_URL`, `VITE_API_PREFIX` (с дефолтами) |
| .env | `.env.example`, `.env.preprod.example`, `.env.prod.example` | секция «5a. Reverse-proxy base-path» |

---

## 🎯 Чек-лист публикации под новым префиксом

- [ ] Helm values содержат `Features__PathBase` для backend
- [ ] Helm values содержат `VITE_BASE_URL` (c trailing `/`) и `VITE_API_PREFIX` (без) для frontend buildArgs
- [ ] Ingress пути матчатся (`/api/ab-fm-import`, `/api/ab-fm-import-web`)
- [ ] Открыть `https://<host>/api/ab-fm-import/diag` → видны `pathBase`, `endpoints`, CORS-allowlist
- [ ] Открыть `https://<host>/api/ab-fm-import-web/` → в DevTools-консоли видно `[ab-fm-import] SPA boot start` + `apiPrefix=/api/ab-fm-import`
- [ ] Загрузить тестовый файл → в логе backend (`kubectl logs <pod>`) видна цепочка `POST /api/imports → 200 → PARSE → VALIDATE`
- [ ] SignalR подключается (нет 404 на `/hubs/imports/negotiate`)

---

## 🔍 Диагностика

| Симптом | Где смотреть | Что искать |
|---------|--------------|------------|
| Белый экран, ассеты 404 | DevTools → Network | Префикс в URL `assets/*.js` должен совпадать с `VITE_BASE_URL` |
| Белый экран, ассеты грузятся | DevTools → Console | Сообщения с префиксом `[ab-fm-import]` |
| 404 на `/api/imports` | Backend log | Строка `Зарегистрировано N HTTP-маршрутов:` со списком |
| SignalR не подключается | DevTools → Network → WS | `negotiate` должен идти на `<host>/<prefix>/hubs/imports/negotiate` |
| Schema=`http` в редиректах | Backend log → `HTTP ... scheme=http` | Проверь `UseForwardedHeaders`, ingress присылает `X-Forwarded-Proto: https` |

См. также:
- doc 122 — environment-config (SSOT-переменные .env)
- doc 130 — kubernetes-deployment-guide
- doc 145 — helm-values-and-config-alignment
- doc 148 — расширенное логирование (для диагностики того же класса проблем)
