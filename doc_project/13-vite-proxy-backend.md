# 🔌 Vite proxy для собственного backend

## 📋 Описание

UI обращается к backend через **относительные пути** (`/api/imports`,
`/api/import-types`, `/hubs/imports`, `/health`). В dev-режиме Vite-proxy
переписывает их на `VITE_BACKEND_URL` (по умолчанию `http://localhost:5000`).
В production frontend и backend должны жить на одном origin — proxy не нужен.

Это позволяет коду services на UI **не знать**, где находится backend, и
**не зависеть от ENV** при импорте модуля (важно для unit-тестов под Node).

---

## ✅ Правильная реализация

### Конфигурация прокси (`vite.config.ts`)

```ts
import { defineConfig, loadEnv } from 'vite';
import type { ProxyOptions } from 'vite';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const visaryTarget = env.VITE_VISARY_API_URL || 'https://isup-alfa-test.k8s.npc.ba';
  const backendTarget = env.VITE_BACKEND_URL || 'http://localhost:5000';

  // Helper: общий формат логов для каждого канала
  const logging = (tag: string, target: string): ProxyOptions['configure'] =>
    (proxy) => {
      proxy.on('proxyReq', (_req, req) =>
        console.log(`[Vite proxy → ${tag}] → ${req.method} ${target}${req.url}`));
      proxy.on('proxyRes', (res, req) =>
        console.log(`[Vite proxy → ${tag}] ← ${res.statusCode} ${req.method} ${req.url}`));
      proxy.on('error', (err, req) =>
        console.error(`[Vite proxy → ${tag}] ✗ ERROR ${req.method} ${req.url} —`, err.message));
    };

  // Фабрика для backend-каналов (общие настройки)
  const backendProxy = (extra: Partial<ProxyOptions> = {}): ProxyOptions => ({
    target: backendTarget,
    changeOrigin: true,
    secure: false,
    configure: logging('backend', backendTarget),
    ...extra,
  });

  return {
    plugins: [react()],
    server: {
      proxy: {
        // Visary (внешний API через прокси, чтобы обойти CORS)
        '/api/visary': {
          target: visaryTarget,
          changeOrigin: true,
          secure: true,
          configure: logging('visary', visaryTarget),
        },
        // Собственный backend
        '/api/imports':      backendProxy(),
        '/api/import-types': backendProxy(),
        '/hubs':             backendProxy({ ws: true }),  // 👈 WebSocket для SignalR!
        '/health':           backendProxy(),
        '/swagger':          backendProxy(),
      },
    },
  };
});
```

### ⚠️ Важно

- **`ws: true` для `/hubs`** — обязательно для SignalR. Без этого WebSocket
  upgrade не пройдёт через прокси, и хаб упадёт на `Failed to start the connection`.
- **Префиксы перечислены явно**, не общий `/api`. Это даёт две выгоды:
  1. `/api/visary` идёт на Visary, `/api/imports` — на backend, без коллизий.
  2. Все прочие пути (статика, HMR) корректно отдаёт сам Vite.
- **`changeOrigin: true`** — без него Host-header будет `localhost:5173`, и
  бэкенд может упереться в проверки CORS/host-binding.
- **`secure: false`** на backend (HTTP) и `secure: true` на Visary (HTTPS) —
  не путать.

---

## ❌ Типичные ошибки

### Ошибка 1: общий `/api` без выделения визары

```ts
// НЕПРАВИЛЬНО
proxy: {
  '/api': { target: backendTarget, changeOrigin: true },  // ❌ перехватит /api/visary тоже
}
```

**Что произойдёт:** запросы `/api/visary/listview/...` уйдут на собственный backend,
который таких эндпоинтов не имеет → 404.

**Правильно:** перечислять все префиксы явно (см. выше).

### Ошибка 2: SignalR без `ws: true`

```ts
// НЕПРАВИЛЬНО
'/hubs': { target: backendTarget, changeOrigin: true }
```

**Что произойдёт:** SignalR попытается WebSocket → upgrade провалится → fallback
на long-polling. Но на long-polling в dev'e `changeOrigin: true` может ломать
sticky-routing хаба → ошибки соединения.

**Правильно:** всегда `ws: true` для путей с SignalR-хабами.

### Ошибка 3: запросы напрямую `http://localhost:5000`

```ts
// НЕПРАВИЛЬНО (в коде сервиса)
fetch('http://localhost:5000/api/imports', { method: 'POST', body });
```

**Почему плохо:**
- Хардкод URL ломает CORS из dev-сервера.
- В production придётся менять код, а не конфиг.
- Тесты под Node не смогут замокать fetch к произвольному URL.

**Правильно:** относительный путь `/api/imports` — Vite в dev перепишет, в prod
backend на том же домене.

### Ошибка 4: чтение `VITE_BACKEND_URL` в коде сервиса при импорте

```ts
// НЕПРАВИЛЬНО
const BASE = import.meta.env.VITE_BACKEND_URL;   // ❌ undefined в Node-тестах
```

**Что произойдёт:** unit-тесты `npx tsx services/__tests__/...` упадут с
`TypeError: Cannot read 'VITE_BACKEND_URL' of undefined`, потому что в Node
`import.meta.env` пустой.

**Правильно:** относительные пути в коде сервиса; ENV читает только Vite-конфиг.

---

## 🌐 Production deploy

В production frontend и backend живут на одном домене (например, через nginx /
ingress):

```
https://import.alfa.local/             → static frontend (Vite build → dist/)
https://import.alfa.local/api/imports  → backend (KiloImportService.Api)
https://import.alfa.local/hubs/imports → backend (SignalR через nginx + ws upgrade)
```

`vite.config.ts proxy` в этом случае **не используется** (Vite запускается только
в dev). Конфигурация nginx/ingress должна:
- Проксировать `/api/imports`, `/api/import-types`, `/hubs`, `/health` на backend.
- Включить `proxy_set_header Upgrade` + `proxy_set_header Connection "upgrade"`
  для путей `/hubs` (SignalR WebSocket).
- Отдавать всё остальное из `dist/`.

---

## 📍 Применение в проекте

| Слой | Файл | Что определяет |
|------|------|----------------|
| Конфиг прокси | `KiloImportService.Web/vite.config.ts` | Префиксы + `ws: true` для /hubs |
| ENV переменные | `KiloImportService.Web/.env.example` | Шаблон `VITE_BACKEND_URL` |
| ENV локальные | `KiloImportService.Web/.env.local` | Не коммитится |
| Docker | `docker-compose.yml` (frontend) | Прокидывает `VITE_BACKEND_URL=http://backend:5000` |

---

## 🎯 Чек-лист при добавлении нового backend-эндпоинта

- [ ] Префикс пути перечислен в `vite.config.ts` (не зацепится generic-маршрутом)
- [ ] Если SignalR — стоит `ws: true`
- [ ] В коде сервиса используется **относительный** путь, без `VITE_BACKEND_URL`
- [ ] CORS на backend пропускает `http://localhost:5173` (см. `Cors:AllowedOrigins`)
- [ ] Для production-деплоя обновлена nginx/ingress конфигурация
