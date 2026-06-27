# 🌍 Конфигурация сред (test / preprod / prod)

> ⚠️ **С 2026-06-23 (doc 145, вариант B1)** имена секций конфига приведены к
> эталону `service-dev`: `EndpointsConfiguration:VisaryApi/VisaryAuthApi`,
> `JwtConfiguration:{Authority,Audience,UseSsl,Secret}`,
> `Features:{Swagger,RequireJwt}`, `Cors` (одна строка),
> `ConnectionStrings:AbFmImport` (одна БД, две схемы `import` + `Data`).
> В этом документе ниже встречаются **старые** имена (`Visary:BaseUrl`,
> `Auth:Authority`, `ConnectionStrings:ServiceDb/VisaryDb`) — см. таблицу
> маппинга в [doc 145](./145-helm-values-and-config-alignment.md#маппинг-старое--новое).

## 📋 Описание

Один артефакт сборки — `KiloImportService.Api` + `KiloImportService.Web` — должен уметь развёртываться на трёх контурах Visary:

| Контур | Visary API | Visary IdP | UI публичный URL |
|--------|------------|------------|-------------------|
| test/dev | `https://isup-alfa-test.k8s.npc.ba` | `https://id-isup-alfa-test.k8s.npc.ba` | `http://localhost:5173` |
| preprod | `https://pre-isup-alfa.k8s.npc.ba` | `https://id-pre-isup-alfa.k8s.npc.ba` | `https://pre-import.alfa.test` |
| prod | `https://isup-alfa.k8s.npc.ba` | `https://id-isup-alfa.k8s.npc.ba` | `https://import.alfa.bank` |

Плюс per-среда отличаются креды БД, refresh-токены OIDC, JWT-Audience входящих запросов.

Цель — **переключение среды одной правкой `.env`** без касания кода/конфигов в репозитории.

---

## 🎯 Архитектурные инварианты

1. **Хосты НИГДЕ в коммитимых файлах** (`appsettings.json`, `docker-compose.yml`, `vite.config.ts`) **не зашиты в литералы** — только переменные среды.
2. **Single Source Of Truth — корневой `.env`**: `VISARY_BASE_URL`, `UI_PUBLIC_URL`, `ConnectionStrings__*` и т. д. читают и backend, и frontend, и docker-compose из одной переменной.
3. **Deny-by-default**: критичные переменные в docker-compose оформлены как `${VAR:?...}` — без значения `docker compose up` останавливается с понятным сообщением. Это предотвращает «случайный» уход test-стенда на prod-хост, если кто-то забыл задать переменную.
4. **Опциональные fallback'и (`:-default`) только для НЕ-критичных значений** (`ASPNETCORE_ENVIRONMENT:-Development`, `Auth__Audience:-kilo-import-api`).
5. **Все примеры — `*.example` файлы в git**, реальные `.env`/`.env.local` — в `.gitignore`.

---

## 🗺️ Карта переменных среды

| Переменная | Используют | Назначение | Deny-by-default |
|-----------|------------|------------|:---------------:|
| `VISARY_BASE_URL` | backend (`Visary__BaseUrl`), Vite (`VITE_VISARY_API_URL`) | API Visary per-среда | ✅ |
| `VISARY_AUTH_BASE_URL` | backend (опц. derive endpoint) | IdP-хост Visary | — |
| `UI_PUBLIC_URL` | backend (`Cors__AllowedOrigins__0`) | Origin UI для CORS-allowlist | ✅ |
| `IMPORT_SERVICE_DB_USER/PASSWORD` | postgres-service контейнер + backend (build connection string) | Креды локальной БД сервиса | ✅ |
| `VISARY_DB_USER/PASSWORD` | postgres-visary контейнер + backend | Креды локальной visary-БД | ✅ |
| `ConnectionStrings__ServiceDb` | backend (override) | Полная строка подключения для managed-PG | — |
| `ConnectionStrings__VisaryDb` | backend (override) | То же для visary-БД | — |
| `Visary__BearerToken` | backend (легаси static-token) | Bearer для прямых вызовов Visary | — |
| `VITE_VISARY_API_TOKEN` | frontend (dev/легаси) | Bearer для Vite-proxy | — |
| `Visary__Auth__TokenEndpoint` | backend (OIDC refresh) | URL endpoint обновления токена | — |
| `Visary__Auth__ClientId` | backend (OIDC refresh) | client_id для OIDC | — |
| `VISARY_AUTH_REFRESH_TOKEN` | backend (OIDC refresh) | refresh_token, периодически обновляется | — |
| `Auth__Authority` | backend (валидация входящих) | IdP, выдающий JWT для нашего API | — |
| `Auth__Audience` | backend | aud-claim в JWT | — |
| `Auth__RequireHttpsMetadata` | backend | проверять `https://` у Authority | — |
| `ASPNETCORE_ENVIRONMENT` | backend | `Development` / `Production` | — |

---

## ✅ Правильная схема (выполнено)

### `.env.example` — dev/test шаблон
```env
VISARY_BASE_URL=https://isup-alfa-test.k8s.npc.ba
UI_PUBLIC_URL=http://localhost:5173

IMPORT_SERVICE_DB_USER=import_service
IMPORT_SERVICE_DB_PASSWORD=change_me_locally
VISARY_DB_USER=visary
VISARY_DB_PASSWORD=change_me_locally

# Connection strings — оставляем пустыми, docker-compose построит из частей выше.
# ConnectionStrings__ServiceDb=
# ConnectionStrings__VisaryDb=
```

### `docker-compose.yml` — backend сервис
```yaml
backend:
  environment:
    # Deny-by-default
    Visary__BaseUrl: ${VISARY_BASE_URL:?VISARY_BASE_URL не задан — скопируй .env.example → .env}
    Cors__AllowedOrigins__0: ${UI_PUBLIC_URL:?UI_PUBLIC_URL не задан}

    # Connection strings: managed-PG (env override) ИЛИ построенные из частей
    ConnectionStrings__ServiceDb: ${ConnectionStrings__ServiceDb:-Host=postgres-service;Port=5432;Database=import_service_db;Username=${IMPORT_SERVICE_DB_USER};Password=${IMPORT_SERVICE_DB_PASSWORD}}
    ConnectionStrings__VisaryDb:  ${ConnectionStrings__VisaryDb:-Host=postgres-visary;Port=5432;Database=visary_webapi_db;Username=${VISARY_DB_USER};Password=${VISARY_DB_PASSWORD}}
```

### `docker-compose.yml` — frontend сервис
```yaml
frontend:
  environment:
    # SSOT VISARY_BASE_URL → VITE_VISARY_API_URL (Vite требует префикс VITE_)
    VITE_VISARY_API_URL: ${VISARY_BASE_URL:?VISARY_BASE_URL не задан}
```

### `appsettings.json` — пустые поля + комментарии
```jsonc
{
  "ConnectionStrings": {
    "// summary": "Задавать через env, см. doc_project/121-122.",
    "ServiceDb": "",
    "VisaryDb": ""
  },
  "Visary": {
    "// summary BaseUrl": "Per-среда. Через env Visary__BaseUrl (маппинг VISARY_BASE_URL). См. doc 122.",
    "BaseUrl": ""
  }
}
```

### `vite.config.ts` — deny-by-default
```ts
const visaryTarget = env.VITE_VISARY_API_URL;
if (!visaryTarget) {
  throw new Error(
    'VITE_VISARY_API_URL пуст. Скопируй .env.example → .env и задай VISARY_BASE_URL. См. doc 122.',
  );
}
```

### ⚠️ Важно
- `appsettings.json` коммитится — должен содержать **только пустые placeholder'ы** и комментарии. Никаких хостов/паролей.
- `docker-compose.yml` коммитится — должен содержать **только подстановки** `${VAR:?...}` или `${VAR:-default}`. Никаких хостов/паролей.
- `.env` и `.env.local` — **в `.gitignore`**, реальные значения только там.

---

## ❌ Анти-паттерны (что было до этого, не делай так)

### ❌ Хардкод хоста в `appsettings.json`
```jsonc
// БЫЛО (отменено):
"Visary": { "BaseUrl": "https://isup-alfa-test.k8s.npc.ba" }
```
**Почему вредно**: коммит = test-хост размазан по git-истории. При деплое на prod забыли — приложение пошло мимо реального API. Plus коммит-полирующий PR (например, опечатка в комменте) может «нечаянно» вернуть старое значение через мердж-конфликт.

### ❌ Fallback на test-хост в `docker-compose.yml`
```yaml
# БЫЛО:
Visary__BaseUrl: ${Visary__BaseUrl:-https://isup-alfa-test.k8s.npc.ba}
```
**Почему вредно**: если в prod забыли задать env-переменную — приложение **молча** ушло на test-хост. Catastrophic: prod-данные ушли в test-инстанс.
```yaml
# ПРАВИЛЬНО (deny-by-default):
Visary__BaseUrl: ${VISARY_BASE_URL:?VISARY_BASE_URL не задан}
```

### ❌ Две переменные на один хост
```env
# БЫЛО:
VITE_VISARY_API_URL=https://isup-alfa-test.k8s.npc.ba
Visary__BaseUrl=https://isup-alfa-test.k8s.npc.ba
```
**Почему вредно**: рассинхрон. Поменял одну — забыл другую — frontend и backend стучат на разные хосты.
```env
# ПРАВИЛЬНО (SSOT):
VISARY_BASE_URL=https://isup-alfa-test.k8s.npc.ba
# Маппинг VISARY_BASE_URL → VITE_VISARY_API_URL и → Visary__BaseUrl делает docker-compose.
```

### ❌ Реальный токен/пароль в `appsettings.Local.json` коммитится
```jsonc
// УЯЗВИМОСТЬ: KiloImportService.Api/appsettings.Local.json содержит реальный JWT.
"Visary": { "BearerToken": "eyJhbGciOiJSUzI1Ni..." }
```
**Почему вредно**: токен утёк в git-историю. Лечится тем же приёмом — `.env` (gitignored).
> ⚠️ **Известная остаточная проблема (2026-06-05)**: `appsettings.Local.json` сейчас в git и содержит токен. Toкен будет revoked после миграции на OIDC refresh-flow (doc 107). Чистка git-истории — отдельная задача (`git filter-repo` или BFG).

### ❌ CORS-allowlist на `localhost:5173` в `appsettings.json` для prod
**Почему вредно**: prod-UI на другом домене, allowlist его не включает → CORS-блок → клиент не работает. Лечится через `UI_PUBLIC_URL` env.

---

## 🔄 Как переключить среду

### Dev/test (по умолчанию)
```powershell
cp .env.example .env
# Отредактируй пароли БД (минимум IMPORT_SERVICE_DB_PASSWORD, VISARY_DB_PASSWORD)
docker compose up -d
```

### Preprod
```powershell
cp .env.preprod.example .env
# Заполни __FILL__-placeholder'ы (creds БД, refresh-token) из Vault
docker compose up -d
```

### Prod
В prod docker-compose обычно НЕ используется (k8s/deployment-pipeline). Файл `.env.prod.example` фиксирует, какие env-переменные приложение ожидает на проде; реальный deployment эти переменные инжектит из Vault/k8s Secrets.

Если всё-таки запускать compose:
```powershell
cp .env.prod.example .env
# Заполни creds из Vault, удали postgres-* сервисы из compose-override
docker compose -f docker-compose.yml -f docker-compose.prod.override.yml up -d
```
(`docker-compose.prod.override.yml` — будущая задача, сейчас не делаем.)

---

## 📍 Применение в проекте

| Файл | Что изменилось |
|------|----------------|
| `.env.example` | SSOT-переменные с комментариями: `VISARY_BASE_URL`, `UI_PUBLIC_URL`. Секции 1-6 с пояснениями. |
| `.env.preprod.example` | Шаблон preprod: pre-isup, managed-PG connection strings, OIDC включён. |
| `.env.prod.example` | Шаблон prod: prod-хосты, всё из Vault, ASPNETCORE_ENVIRONMENT=Production. |
| `docker-compose.yml` | `${VISARY_BASE_URL:?...}` маппится на `Visary__BaseUrl` и `VITE_VISARY_API_URL`. `${UI_PUBLIC_URL:?...}` → CORS. Connection strings — env-override приоритетнее построенных. |
| `KiloImportService.Api/appsettings.json` | `Visary.BaseUrl: ""` с комментарием; `Cors.AllowedOrigins` — local-only fallback с комментарием. |
| `KiloImportService.Web/vite.config.ts` | Убран fallback на test-хост; пустая `VITE_VISARY_API_URL` → `throw new Error` с инструкцией. |

---

## 🎯 Чек-лист (выполнено 2026-06-05)

- [x] SSOT-переменная `VISARY_BASE_URL` в `.env.example`
- [x] `docker-compose.yml` маппит SSOT на backend (`Visary__BaseUrl`) и frontend (`VITE_VISARY_API_URL`)
- [x] `docker-compose.yml` — deny-by-default для `VISARY_BASE_URL` и `UI_PUBLIC_URL`
- [x] `docker-compose.yml` — connection strings: env-override приоритетнее построенных
- [x] `appsettings.json` — `Visary.BaseUrl` очищен, комментарий со ссылкой на doc 122
- [x] `vite.config.ts` — fallback убран, понятный `throw` при пустой переменной
- [x] `.env.preprod.example` — skeleton с pre-isup хостами и managed-PG connection strings
- [x] `.env.prod.example` — skeleton с prod-хостами, OIDC включён, ASPNETCORE_ENVIRONMENT=Production
- [x] doc 122 (этот файл)
- [x] README/MEMORY обновлены
- [ ] **Остаточная задача**: `appsettings.Local.json` с JWT-токеном — revoke + чистка git-истории (отдельная задача, координируется с заказчиком)
- [ ] **Остаточная задача**: `docker-compose.prod.override.yml` для prod-compose-сценария — не реализовано (prod использует k8s)

---

## ⚠️ Связанные документы
- [121-security-fixes-appsec-v1.md](./121-security-fixes-appsec-v1.md) — deny-by-default паттерн пришёл оттуда
- [107-visary-token-provider.md](./107-visary-token-provider.md) — OIDC refresh-flow
- [111-incoming-jwt-auth.md](./111-incoming-jwt-auth.md) — JWT-валидация входящих запросов
- [18-projects-cache.md](./18-projects-cache.md) — где Visary API используется (cache проектов)
