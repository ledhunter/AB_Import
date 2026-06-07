# 🚀 Инструкция: перевод сервиса на другую среду

## 📋 Назначение

Пошаговая инструкция «что и где менять», чтобы развернуть сервис на одной из 3 сред:

| Среда | Visary API | UI URL |
|-------|------------|--------|
| **test** (по умолчанию) | `https://isup-alfa-test.k8s.npc.ba` | `http://localhost:5173` |
| **preprod** | `https://pre-isup-alfa.k8s.npc.ba` | `https://pre-import.alfa.test` |
| **prod** | `https://isup-alfa.k8s.npc.ba` | `https://import.alfa.bank` |

> Архитектурный контекст и анти-паттерны — в [122-environment-config.md](./122-environment-config.md). Этот файл — практическое руководство.

---

## ⚡ TL;DR — в 3 команды

```powershell
# 1. Выбрать профиль среды
cp .env.preprod.example .env       # или .env.prod.example / .env.example

# 2. Открыть .env и подставить настоящие значения __FILL__-плейсхолдеров
notepad .env

# 3. Поднять стенд
docker compose up -d --build
```

Если запустилось без ошибок и `docker compose ps` показывает все 4 сервиса `Up (healthy)` — переход завершён. Если упало — см. § «🛑 Troubleshooting».

---

## 🎯 Принцип

**Код, `appsettings.json`, `docker-compose.yml`, `vite.config.ts` — НЕ трогать**. Все различия между средами живут **в одном файле** — корневом `.env` (gitignored).

```
                        ┌──────────────┐
                        │   .env       │   ← ЕДИНСТВЕННОЕ место правки
                        │ (gitignored) │
                        └──────┬───────┘
                               │
              ┌────────────────┴────────────────┐
              ▼                                 ▼
  ┌─────────────────────┐         ┌─────────────────────┐
  │  docker-compose.yml │         │   vite.config.ts    │
  │  appsettings.json   │         │   (build-time)      │
  │  (env vars)         │         │                     │
  └─────────────────────┘         └─────────────────────┘
              │                                 │
              ▼                                 ▼
       backend контейнер              frontend dev-server
```

---

## 📦 Шаг 1. Подготовка `.env`

### 1.1. Выбор шаблона

В корне репозитория есть 3 шаблона:

| Файл | Назначение |
|------|-----------|
| `.env.example` | dev/test (по умолчанию) |
| `.env.preprod.example` | preprod (pre-isup-...) |
| `.env.prod.example` | prod (isup-...) |

Скопируй нужный в `.env`:
```powershell
# Windows / PowerShell
Copy-Item .env.preprod.example .env

# Linux / Mac
cp .env.preprod.example .env
```

### 1.2. Что заполнить в `.env`

#### 🌐 Блок 1 — Хосты (обязательно)

| Переменная | Что туда положить | Пример (preprod) |
|------------|-------------------|------------------|
| `VISARY_BASE_URL` | URL Visary API для нужной среды | `https://pre-isup-alfa.k8s.npc.ba` |
| `VISARY_AUTH_BASE_URL` | URL IdP Visary. Если IdP-хост = `id-<api-host>` — можно оставить пустым | `https://id-pre-isup-alfa.k8s.npc.ba` или пусто |
| `UI_PUBLIC_URL` | По какому URL открывается UI у пользователей | `https://pre-import.alfa.test` |

> ⚠️ **Без этих 3 переменных `docker compose up` не стартует** — `${VAR:?...}` синтаксис останавливает запуск с понятным сообщением. Это сделано специально, чтобы prod-стенд не ушёл на test-хост, если кто-то забыл задать переменную.

#### 🗄 Блок 2 — База данных (обязательно)

Два варианта в зависимости от того, где БД:

**Вариант A — БД в docker-compose (dev / стенд-в-контейнерах)**

Заполни 4 переменные (postgres-сервисы возьмут их при создании БД, backend построит connection string):
```env
IMPORT_SERVICE_DB_USER=import_service
IMPORT_SERVICE_DB_PASSWORD=<сильный пароль>

VISARY_DB_USER=visary
VISARY_DB_PASSWORD=<сильный пароль>
```

`ConnectionStrings__ServiceDb` / `ConnectionStrings__VisaryDb` в этом случае **оставь закомментированными** — compose построит их сам.

**Вариант B — managed-PostgreSQL (preprod/prod)**

Заполни 2 переменные с полными connection strings (выдаёт админ среды или Vault):
```env
ConnectionStrings__ServiceDb=Host=preprod-pg.alfa.test;Port=5432;Database=import_service_db;Username=svc_user;Password=...
ConnectionStrings__VisaryDb=Host=preprod-pg.alfa.test;Port=5432;Database=visary_webapi_db;Username=svc_user;Password=...
```

`IMPORT_SERVICE_DB_USER/PASSWORD` и `VISARY_DB_USER/PASSWORD` в этом случае — оставь любые dummy-значения (нужны, чтобы compose не отвалил postgres-сервисы; либо запускай compose только для backend+frontend: `docker compose up backend frontend`).

#### 🔑 Блок 3 — Bearer / OIDC (по ситуации)

| Переменная | Когда заполнять |
|------------|----------------|
| `VITE_VISARY_API_TOKEN` | Только dev (frontend ходит прямо в Visary). На preprod/prod — **оставить пустым** (используется OIDC через backend) |
| `Visary__BearerToken` | Только если OIDC ещё не настроен (легаси) |
| `Visary__Auth__TokenEndpoint` | OIDC URL обновления токена. Обычно `https://id-<среда>.../connect/token` |
| `Visary__Auth__ClientId` | OIDC client_id (по умолчанию `visary-ui`) |
| `VISARY_AUTH_REFRESH_TOKEN` | Refresh-токен, капчурится один раз через UI Visary. **На prod — в Vault, НЕ в `.env`** |

Подробнее — [doc 107: OIDC refresh-flow](./107-visary-token-provider.md).

#### 🛂 Блок 4 — JWT-аутентификация входящих запросов (опционально)

Если включена — backend проверяет JWT на каждом запросе:
```env
Auth__Authority=https://id-pre-isup-alfa.k8s.npc.ba
Auth__Audience=kilo-import-api
Auth__RequireHttpsMetadata=true
```

`Auth__Authority` пуст = JWT-валидация выключена (легаси/dev). Подробнее — [doc 111: incoming JWT auth](./111-incoming-jwt-auth.md).

#### ⚙️ Блок 5 — Окружение ASP.NET

```env
ASPNETCORE_ENVIRONMENT=Production   # для prod
# ASPNETCORE_ENVIRONMENT=Development   # для dev/preprod
```

---

## 🚀 Шаг 2. Запуск

### 2.1. Поднять стенд
```powershell
docker compose up -d --build
```

`--build` обязателен **после первого pull** (чтобы пересобрать образы) и **при правках кода**. См. [memory: Docker rebuild](#).

### 2.2. Проверить, что всё взлетело
```powershell
docker compose ps
```
Ожидаемый вывод — 4 сервиса в состоянии `Up (healthy)`:
- `kilo-import-pg-service`
- `kilo-import-pg-visary`
- `kilo-import-backend`
- `kilo-import-frontend`

### 2.3. Smoke-проверка
| Что | Команда / URL | Ожидаемый результат |
|-----|---------------|---------------------|
| Backend health | `curl http://localhost:5000/health` | `Healthy` |
| Backend Swagger | http://localhost:5000/swagger | страница открывается |
| Frontend | http://localhost:5173 (dev) или `UI_PUBLIC_URL` (preprod/prod) | UI рендерится |
| Visary-прокси (через UI DevTools → Network) | открыть UI → выбрать проект | запросы идут на `/api/visary/*` → внутри переадресуются на `VISARY_BASE_URL` |

---

## 🔎 Шаг 3. Проверка, что хост действительно сменился

После запуска убедись, что приложение реально стучит куда нужно:

### 3.1. Проверка backend
```powershell
docker compose exec backend env | findstr Visary__BaseUrl
```
Должен показать тот URL, который ты задал в `.env`.

### 3.2. Проверка frontend в браузере
1. Открой UI → DevTools (`F12`) → вкладка **Network**.
2. Выбери проект в drop-down → должны пойти запросы на `/api/visary/...`.
3. У одного из запросов: правый клик → **Headers** → ищи `Referer` или раскрой **General** → должен быть твой `UI_PUBLIC_URL`.
4. В docker-логах frontend:
   ```powershell
   docker compose logs frontend | findstr "Vite proxy"
   ```
   Строка `[Vite proxy → visary] → GET https://pre-isup-alfa.k8s.npc.ba/api/visary/...` подтвердит, что proxy переадресовал на нужный хост.

### 3.3. Проверка CORS
Если UI в браузере ругается «CORS blocked» — значит `UI_PUBLIC_URL` в `.env` не совпадает с тем origin'ом, по которому ты открыл UI. Перепроверь блок 1.

---

## 🛑 Troubleshooting

### Ошибка: `VISARY_BASE_URL не задан`
```
error while interpolating services.backend.environment.Visary__BaseUrl:
required variable VISARY_BASE_URL is missing a value: VISARY_BASE_URL не задан — скопируй .env.example → .env
```
**Причина**: в `.env` нет `VISARY_BASE_URL` (или вообще нет `.env`).
**Решение**: `cp .env.{среда}.example .env` и заполнить.

### Ошибка: `UI_PUBLIC_URL не задан`
Аналогично — добавь `UI_PUBLIC_URL=...` в `.env`.

### Ошибка: `IMPORT_SERVICE_DB_PASSWORD не задан`
**Причина**: запускаешь compose с включёнными postgres-сервисами без локальных кредов.
**Решение**:
- либо задай `IMPORT_SERVICE_DB_*`/`VISARY_DB_*` в `.env` (даже dummy),
- либо запусти только нужные сервисы: `docker compose up backend frontend` (БД managed).

### Backend стартует, но падает на подключении к БД
```
Npgsql.NpgsqlException: Failed to connect ...
```
**Причина**: connection string ссылается на `postgres-visary` (имя docker-сервиса), но БД managed/внешняя.
**Решение**: задай `ConnectionStrings__ServiceDb` и `ConnectionStrings__VisaryDb` в `.env` целиком с реальным хостом БД.

### UI открывается, но «Сетевая ошибка» в DevTools
- Открой `docker compose logs backend` — ищи ошибки `401`/`403` от Visary.
- Если `401` — токен истёк или неверный для среды. Перевыпусти `VITE_VISARY_API_TOKEN` / refresh OIDC.
- Если `403` — нет прав в Visary; обратись к админу IdP.
- Если совсем ничего нет в логах backend — проверь, что frontend стучится через `/api/visary/...`, а не напрямую на хост (см. § 3.2).

### `Vite: VITE_VISARY_API_URL пуст`
**Причина**: запускаешь `npm run dev` локально (вне compose) без переменной.
**Решение**: либо запусти через compose, либо положи `KiloImportService.Web/.env.local` с локальными VITE_-переменными.

### После смены среды старые данные «всё ещё показываются»
**Причина**: локальные тома postgres-volume помнят старые данные с предыдущей среды.
**Решение**: `docker compose down -v` (`-v` удалит volumes) и заново `up -d`. **Осторожно**: это сотрёт локальные импорты.

---

## ✅ Чек-лист для переключения среды

Прохожу чек-лист по порядку, не пропуская:

- [ ] **1. Текущий стенд остановлен**: `docker compose down`
- [ ] **2. `.env` сохранён** (если в нём были рабочие настройки прошлой среды): `cp .env .env.backup`
- [ ] **3. Выбран профиль**: `cp .env.{test|preprod|prod}.example .env`
- [ ] **4. Заполнен блок 1 — хосты**:
  - [ ] `VISARY_BASE_URL`
  - [ ] `VISARY_AUTH_BASE_URL` (если IdP-хост нестандартный)
  - [ ] `UI_PUBLIC_URL`
- [ ] **5. Заполнен блок 2 — БД** (выбран Вариант A или Вариант B)
- [ ] **6. Заполнен блок 3 — токены/OIDC**:
  - [ ] dev → `VITE_VISARY_API_TOKEN` или `Visary__BearerToken`
  - [ ] preprod/prod → `Visary__Auth__TokenEndpoint` + `VISARY_AUTH_REFRESH_TOKEN`
- [ ] **7. Блок 4 — JWT** (если нужно)
- [ ] **8. Блок 5 — `ASPNETCORE_ENVIRONMENT`**
- [ ] **9. Запуск**: `docker compose up -d --build`
- [ ] **10. Все 4 сервиса `Up (healthy)`**: `docker compose ps`
- [ ] **11. Smoke-проверка** (§ 2.3): health, swagger, UI, Visary-прокси в DevTools
- [ ] **12. Подтверждение хоста** (§ 3): `docker exec env | findstr Visary__BaseUrl`

---

## 🗺 Карта файлов и переменных

Шпаргалка: какая переменная на что влияет.

| Переменная в `.env` | Влияет на | Где читается |
|---------------------|-----------|--------------|
| `VISARY_BASE_URL` | Хост Visary API (backend + frontend) | docker-compose.yml: `Visary__BaseUrl` (backend), `VITE_VISARY_API_URL` (frontend) |
| `VISARY_AUTH_BASE_URL` | Хост IdP (для OIDC) | docker-compose.yml: `Visary__Auth__TokenEndpoint` (опц.) |
| `UI_PUBLIC_URL` | CORS-allowlist backend | docker-compose.yml: `Cors__AllowedOrigins__0` |
| `IMPORT_SERVICE_DB_USER/PASSWORD` | Postgres-service контейнер + connection string | docker-compose.yml: `POSTGRES_USER/PASSWORD` + построение connection string |
| `VISARY_DB_USER/PASSWORD` | Postgres-visary контейнер + connection string | docker-compose.yml |
| `ConnectionStrings__ServiceDb` | Полная строка подключения (override для managed-PG) | docker-compose.yml (приоритетнее построенной) |
| `ConnectionStrings__VisaryDb` | То же для visary-БД | docker-compose.yml |
| `VITE_VISARY_API_TOKEN` | Bearer для frontend (dev/легаси) | docker-compose.yml: `VITE_VISARY_API_TOKEN` |
| `Visary__BearerToken` | Bearer для backend (легаси) | docker-compose.yml |
| `Visary__Auth__TokenEndpoint` | OIDC token endpoint | docker-compose.yml: `Visary__Auth__TokenEndpoint` |
| `Visary__Auth__ClientId` | OIDC client_id | docker-compose.yml |
| `VISARY_AUTH_REFRESH_TOKEN` | OIDC refresh-токен | docker-compose.yml |
| `Auth__Authority` | IdP для валидации входящих JWT | docker-compose.yml + Program.cs |
| `Auth__Audience` | aud-claim в JWT | docker-compose.yml |
| `Auth__RequireHttpsMetadata` | https-only для Authority | docker-compose.yml |
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Production` | docker-compose.yml: backend |

### Файлы, которые ты НЕ трогаешь при смене среды

| Файл | Что в нём |
|------|-----------|
| `KiloImportService.Api/appsettings.json` | placeholder'ы (пустые строки) + комментарии. Реальные значения приходят через env |
| `docker-compose.yml` | подстановки `${VAR:?...}` и `${VAR:-default}`. Хосты не зашиты |
| `KiloImportService.Web/vite.config.ts` | читает `VITE_VISARY_API_URL` из env; при пустом значении бросает понятную ошибку |
| `KiloImportService.Api/appsettings.Development.json` | dev-логирование, без хостов |

---

## 🎓 Частые ошибки

### ❌ «Поменяю просто `appsettings.json`»
Не сработает: в нём только пустые placeholder'ы. Реальное значение приходит через `Visary__BaseUrl` env-переменную, которую compose читает из `.env`.

### ❌ «Подменю хост прямо в `docker-compose.yml`»
Технически сработает на твоём ноуте, но **сломает все остальные среды и PR**. В compose только подстановки — все хосты в `.env`.

### ❌ «Положу пароль в `appsettings.Local.json`»
`appsettings.Local.json` коммитится в git (известный долг, см. [doc 122 § Анти-паттерны](./122-environment-config.md)). Все секреты — в `.env` (gitignored).

### ❌ «Скопирую `.env` коллеги»
В `.env` лежат секреты — пароли, токены. Лучше: `cp .env.{среда}.example .env` и попроси админа выдать creds через защищённый канал (Vault / 1Password).

### ❌ «Заодно почищу volumes — пересоздадим БД»
`docker compose down -v` удалит **все локальные импорты и историю**. Если данные нужны — `down` без `-v`.

---

## 📚 Связанные документы

- [122-environment-config.md](./122-environment-config.md) — архитектура и анти-паттерны конфигов
- [121-security-fixes-appsec-v1.md](./121-security-fixes-appsec-v1.md) — откуда взялся deny-by-default паттерн
- [107-visary-token-provider.md](./107-visary-token-provider.md) — OIDC refresh-flow подробно
- [111-incoming-jwt-auth.md](./111-incoming-jwt-auth.md) — JWT валидация входящих
- [31-smoke-test-instructions.md](./31-smoke-test-instructions.md) — полный smoke-цикл после деплоя
- [33-docker-cli-troubleshooting.md](./33-docker-cli-troubleshooting.md) — общие проблемы Docker
