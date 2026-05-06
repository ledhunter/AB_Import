# 🔑 Обновление токена Visary и кэш Vite

> ## ⚠️ Документ устарел (superseded by [54](./54-visary-token-hot-reload.md))
>
> С 2026-05-06 Bearer-токен Visary живёт **только в корневом `.env`** репозитория
> (Single Source Of Truth). Файла `KiloImportService.Web/.env.local` больше нет —
> Vite читает корневой `.env` через `envDir: '..'` в `vite.config.ts`.
>
> **Актуальная процедура** обновления токена:
> 1. Открой корневой `.env`, замени `VITE_VISARY_API_TOKEN=...` (и `Visary__BearerToken=...`)
> 2. `docker compose up -d --force-recreate backend frontend`
>    (или Ctrl+C → `npm run dev` для локального dev-сервера)
>
> Текст ниже сохранён как **исторический контекст** v1 — описывает старую схему
> с `.env.local` и Vite-кэшем. Грабли с `node_modules/.vite` всё ещё актуальны
> при локальном запуске; всё остальное про расположение файла — нет.
> См. [54-visary-token-hot-reload.md](./54-visary-token-hot-reload.md).

---

## 📋 Описание

Токен Visary (`VITE_VISARY_API_TOKEN`) живёт **~1 час** (см. `exp` в JWT). Когда он истекает, все запросы к Visary API возвращают `401 Unauthorized` с текстом:

> Bearer-токен Visary истёк или невалиден. Обнови VITE_VISARY_API_TOKEN в .env.local и перезапусти dev-сервер.

Просто заменить значение в `.env.local` **недостаточно** — Vite подхватывает переменные окружения при запуске и кэширует клиентские зависимости в `node_modules/.vite`. Этот документ описывает правильную процедуру.

> 🔁 См. также: `08-visary-api-integration.md`, `26-troubleshooting.md`, `28-faq.md`.

---

## ✅ Правильная процедура обновления токена

### Шаг 1. Получить свежий токен

1. Открыть https://isup-alfa-test.k8s.npc.ba
2. Если сессия завершена — **выйти и войти заново** (не просто F5)
3. DevTools → Network → любой запрос к API → **Request Headers** → `Authorization`
4. Скопировать значение **БЕЗ** префикса `Bearer `
5. Убедиться, что строка **одна**, без переносов (чат/мессенджеры любят резать JWT по `.`)

### Шаг 2. Записать токен в оба `.env` файла

`KiloImportService.Web/.env.local` (frontend / Vite):
```env
VITE_VISARY_API_URL=https://isup-alfa-test.k8s.npc.ba
VITE_VISARY_API_TOKEN=eyJhbGciOi...
```

Корневой `.env` (backend через docker-compose):
```env
Visary__BaseUrl=https://isup-alfa-test.k8s.npc.ba
Visary__BearerToken=eyJhbGciOi...
VITE_VISARY_API_URL=https://isup-alfa-test.k8s.npc.ba
VITE_VISARY_API_TOKEN=eyJhbGciOi...
```

### Шаг 3. Проверить токен curl-ом ДО перезапуска UI

```powershell
$token = (Get-Content "KiloImportService.Web/.env.local" | Select-String "VITE_VISARY_API_TOKEN").Line.Replace("VITE_VISARY_API_TOKEN=", "")
$body = '{"Mnemonic":"constructionsite","PageSkip":0,"PageSize":5,"Columns":["ID","Title"],"Sorts":"[{\"selector\":\"ID\",\"desc\":false}]","Hidden":false,"ExtraFilter":null,"SearchPhrase":null,"Summaries":[]}'

Invoke-WebRequest `
  -Uri "https://isup-alfa-test.k8s.npc.ba/api/visary/listview/constructionsite/onetomany/Project?associationId=4584" `
  -Method POST `
  -Headers @{"Authorization"="Bearer $token"; "Content-Type"="application/json"} `
  -Body $body -UseBasicParsing
```

Ожидаемый ответ — `200 OK` с `{ "Data": [...], "Total": N }`.

Если возвращается `401` — токен уже инвалидирован на стороне Visary, **получить новый**. Не тратить время на отладку UI.

### Шаг 4. Перезапустить Vite с очисткой кэша

```powershell
# Остановить все Node-процессы (Vite)
Get-Process -Name "node" -ErrorAction SilentlyContinue | Stop-Process -Force

# Удалить кэш Vite (важно!)
Remove-Item -Path "KiloImportService.Web\node_modules\.vite" -Recurse -Force -ErrorAction SilentlyContinue

# Запустить заново с --force (принудительная re-optimization)
cd KiloImportService.Web
npm run dev -- --force
```

### Шаг 5. Перезапустить backend (если меняли корневой `.env`)

```powershell
docker restart kilo-import-backend
```

### Шаг 6. Hard reload в браузере

**Ctrl + Shift + R** (или DevTools → ПКМ на reload → «Очистить кэш и жёсткая перезагрузка»).

Браузер кеширует `index.html` и модули Vite — без hard reload может использоваться старый токен.

---

## ❌ Типичные ошибки

### Ошибка 1. Просто обновить `.env.local` и жать F5

```powershell
# Токен записан ✓
Set-Content .env.local $newToken
# ... но Vite уже запущен и не перечитает env
```

**Симптом:** UI продолжает слать старый токен, 401 не уходит.

**Правильно:** после правки `.env.local` **перезапустить** `npm run dev` (полностью убить процесс, не HMR).

### Ошибка 2. Копировать токен с префиксом `Bearer `

```env
# ❌ НЕПРАВИЛЬНО
VITE_VISARY_API_TOKEN=Bearer eyJhbGciOi...
```

Код добавляет `Bearer ` автоматически (`visaryApi.ts`), в итоге заголовок будет `Authorization: Bearer Bearer eyJ...` → 401.

**Правильно:** токен без префикса — только сам JWT, начинается на `eyJ`.

### Ошибка 3. Тот же токен, что уже лежит в `.env.local`

Иногда пользователь копирует из Visary UI «тот же самый» токен (если не делал перелогин). Последние 10–20 символов совпадают с уже записанным.

**Проверка:**
```powershell
$line = (Get-Content .env.local | Select-String "VITE_VISARY_API_TOKEN").Line
Write-Host "Last 30 chars: ...$($line.Substring($line.Length - 30))"
```

Сравнить с последними символами нового токена. Если совпадают — перелогиниться в Visary и получить действительно новый.

### Ошибка 4. `node_modules/.vite` не очищен

Vite агрессивно кэширует pre-bundled зависимости. Иногда старый env «зашит» в прекэшированный чанк.

**Правильно:** запускать с `--force` **или** удалить `node_modules/.vite` перед `npm run dev`.

### Ошибка 5. Порт 5173 занят другим Vite-процессом

Если Vite пишет `Port 5173 is in use, trying another one...` — запустится на 5174/5175. Старый процесс на 5173 может продолжать отдавать стейл-код.

**Правильно:**
```powershell
netstat -ano | findstr :5173
taskkill /PID <pid> /F
```

Либо убить все `node`-процессы сразу (см. шаг 4).

---

## 🧪 Диагностика «UI всё ещё шлёт старый токен»

### 1. Проверить, что реально отправляет браузер

DevTools → Network → запрос `onetomany/Project?associationId=...` → Headers → **Request Headers** → `Authorization: Bearer <последние 20 символов>`.

Сравнить с последними символами токена в `.env.local`:
```powershell
(Get-Content KiloImportService.Web/.env.local | Select-String "VITE_VISARY_API_TOKEN").Line | ForEach-Object { $_.Substring($_.Length - 30) }
```

- Совпадают → токен передаётся правильно, но Visary его не принимает → **получить новый токен**
- Не совпадают → Vite не подхватил `.env.local` → **повторить шаг 4** (очистка кэша + перезапуск)

### 2. curl-тест (изолирует frontend)

См. Шаг 3 выше. Если curl тоже даёт 401 — проблема в самом токене.

### 3. Быстрый способ: запустить Vite на новом порту

Старые вкладки браузера держат WebSocket к старому Vite-процессу на :5173. Чтобы точно обойти:

```powershell
npm run dev -- --force --port 5175
```

Открыть http://localhost:5175 в **новой** вкладке. Без кэша → точно свежий env.

---

## 🔐 Жизненный цикл токена

| Поле JWT | Описание |
|----------|----------|
| `iat` | Когда выдан (Unix timestamp, UTC) |
| `nbf` | Не валиден до (обычно = `iat`) |
| `exp` | Истекает (обычно `iat + 3600` — один час) |
| `auth_time` | Когда пользователь реально логинился (остаётся при refresh) |

Декодировать payload: https://jwt.io/ (или просто `atob` средней части токена).

**Важно:** даже если `exp` ещё в будущем, Visary может инвалидировать токен если:
- Сессия завершена в Visary UI (logout)
- Пользователь перезаходил заново (старый `jti` отозван)

---

## 📍 Применение в проекте

| Файл | Назначение |
|------|------------|
| `KiloImportService.Web/.env.local` | Токен для Vite (frontend) |
| `.env` (корневой) | Токен для backend-контейнера через docker-compose |
| `KiloImportService.Web/src/services/visaryApi.ts::getToken` | Чтение `import.meta.env.VITE_VISARY_API_TOKEN` |
| `docker-compose.yml` | Проброс `Visary__BearerToken` в backend |

---

## 🎯 Чек-лист обновления токена

- [ ] Получен **новый** токен из Visary (последние символы отличаются от текущих)
- [ ] Токен записан БЕЗ префикса `Bearer ` в `.env.local`
- [ ] Токен записан в корневой `.env` (если нужен backend)
- [ ] Проверен curl-ом → `200 OK`
- [ ] Остановлены все `node`-процессы
- [ ] Удалён `node_modules/.vite`
- [ ] `npm run dev -- --force` запущен заново
- [ ] Backend перезапущен (`docker restart kilo-import-backend`), если менялся корневой `.env`
- [ ] В браузере выполнен **Ctrl + Shift + R**
- [ ] DevTools → Network → запрос возвращает `200` с реальными данными
