# 🏗️ Build-time конфигурация образов для закрытого корп. контура

## 📋 Описание

В закрытом корпоративном контуре банка нет прямого выхода на публичные
хосты `mcr.microsoft.com`, `docker.io`, `api.nuget.org`, `registry.npmjs.org`,
`dl-cdn.alpinelinux.org`. Без параметризации `FROM`/`dotnet restore`/
`npm install`/`apk add` команда `docker compose build` падает на первой же
сетевой попытке.

Цель — **одна правка `.env`** переключает все источники сборки на
корп-зеркала, без касания кода Dockerfile'ов или `docker-compose.yml`.
Парный к [122-environment-config.md](./122-environment-config.md), но про
**build-time** (122 — runtime).

---

## 🎯 Архитектурные инварианты

1. **Дефолты — публичные хосты.** Локальная разработка работает «из коробки»,
   разработчику не нужно ничего настраивать.
2. **Override через build-args, поднятые из `.env`.** В `docker-compose.yml`
   секция `build.args` маппит `${VAR:-default}` → ARG в Dockerfile.
3. **Никаких отдельных файлов** `nuget.config`/`.npmrc` в репозитории — feed'ы
   задаются через CLI (`dotnet nuget add source` / `npm config set registry`)
   внутри Dockerfile. Меньше файлов = меньше точек рассинхрона.
4. **Apk-mirror override через `sed` в `/etc/apk/repositories`.** При дефолтном
   значении `sed s|X|X|g` — no-op, поведение base-образа не меняется.
5. **`dotnet restore --configfile /tmp/nuget.config` + `dotnet publish --no-restore`.**
   Чтобы publish не сходил повторно на дефолтный feed, прибитый внутри SDK-образа.

---

## 🗺️ Карта build-time переменных

| Переменная | Где используется | Назначение | Дефолт |
|-----------|------------------|------------|--------|
| `DOTNET_SDK_IMAGE` | [KiloImportService.Api/Dockerfile](../KiloImportService.Api/Dockerfile) `FROM ${DOTNET_SDK_IMAGE} AS build` | SDK-образ .NET 10 для compile | `mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine` |
| `DOTNET_ASPNET_IMAGE` | то же, `FROM ${DOTNET_ASPNET_IMAGE} AS runtime` | Runtime-образ ASP.NET 10 | `mcr.microsoft.com/dotnet/aspnet:10.0-preview-alpine` |
| `NUGET_FEED_URL` | то же, `dotnet nuget add source` | NuGet v3 feed (override дефолтного nuget.org) | `https://api.nuget.org/v3/index.json` |
| `NODE_IMAGE` | [KiloImportService.Web/Dockerfile](../KiloImportService.Web/Dockerfile) `FROM ${NODE_IMAGE} AS dev` | Node.js образ для Vite-сборки | `node:20-alpine` |
| `NPM_REGISTRY_URL` | то же, `npm config set registry` | npm registry (override дефолтного npmjs.org) | `https://registry.npmjs.org` |
| `POSTGRES_IMAGE` | [docker-compose.yml](../docker-compose.yml) `postgres-service.image`, `postgres-visary.image` | PostgreSQL образ | `postgres:16-alpine` |
| `ALPINE_MIRROR` | оба Dockerfile, `sed -i s\|dl-cdn.alpinelinux.org\|${ALPINE_MIRROR}\|g` | Alpine package repo | `dl-cdn.alpinelinux.org` |

---

## ✅ Правильная реализация

### 1. Backend Dockerfile — global ARG + переобъявление после FROM

```dockerfile
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-preview-alpine

FROM ${DOTNET_SDK_IMAGE} AS build
# 👈 ARG до FROM — global, после FROM его НУЖНО переобъявить, иначе невиден в RUN.
ARG NUGET_FEED_URL=https://api.nuget.org/v3/index.json
WORKDIR /src

COPY KiloImportService.Api/KiloImportService.Api.csproj KiloImportService.Api/
COPY Visary.Api.Client/Visary.Api.Client.csproj Visary.Api.Client/
# Создаём свой nuget.config с одним feed → restore не сходит на дефолтный.
RUN dotnet nuget add source "${NUGET_FEED_URL}" --name primary --configfile /tmp/nuget.config && \
    dotnet restore KiloImportService.Api/KiloImportService.Api.csproj --configfile /tmp/nuget.config

COPY KiloImportService.Api/ KiloImportService.Api/
COPY Visary.Api.Client/   Visary.Api.Client/
# 👈 --no-restore — restore уже сделан выше с заданным feed; publish'у нельзя
#     позволить повторно сходить за пакетами на дефолтный nuget.org.
RUN dotnet publish KiloImportService.Api/KiloImportService.Api.csproj -c Release -o /app /p:UseAppHost=false --no-restore

FROM ${DOTNET_ASPNET_IMAGE} AS runtime
ARG ALPINE_MIRROR=dl-cdn.alpinelinux.org
WORKDIR /app
RUN sed -i "s|dl-cdn.alpinelinux.org|${ALPINE_MIRROR}|g" /etc/apk/repositories && \
    apk add --no-cache curl icu-libs ttf-dejavu fontconfig && \
    (getent group app  >/dev/null || addgroup -S app) && \
    (getent passwd app >/dev/null || adduser  -S app -G app)
```

### 2. Frontend Dockerfile — npm registry через `npm config set`

```dockerfile
ARG NODE_IMAGE=node:20-alpine
FROM ${NODE_IMAGE} AS dev
ARG NPM_REGISTRY_URL=https://registry.npmjs.org
ARG ALPINE_MIRROR=dl-cdn.alpinelinux.org
WORKDIR /app
RUN sed -i "s|dl-cdn.alpinelinux.org|${ALPINE_MIRROR}|g" /etc/apk/repositories
COPY package.json package-lock.json* ./
RUN npm config set registry "${NPM_REGISTRY_URL}" && npm install
```

### 3. docker-compose.yml — `build.args` маппит `.env` → ARG

```yaml
backend:
  build:
    context: .
    dockerfile: KiloImportService.Api/Dockerfile
    args:
      DOTNET_SDK_IMAGE: ${DOTNET_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine}
      DOTNET_ASPNET_IMAGE: ${DOTNET_ASPNET_IMAGE:-mcr.microsoft.com/dotnet/aspnet:10.0-preview-alpine}
      NUGET_FEED_URL: ${NUGET_FEED_URL:-https://api.nuget.org/v3/index.json}
      ALPINE_MIRROR: ${ALPINE_MIRROR:-dl-cdn.alpinelinux.org}

frontend:
  build:
    context: ./KiloImportService.Web
    dockerfile: Dockerfile
    args:
      NODE_IMAGE: ${NODE_IMAGE:-node:20-alpine}
      NPM_REGISTRY_URL: ${NPM_REGISTRY_URL:-https://registry.npmjs.org}
      ALPINE_MIRROR: ${ALPINE_MIRROR:-dl-cdn.alpinelinux.org}

postgres-service:
  image: ${POSTGRES_IMAGE:-postgres:16-alpine}
postgres-visary:
  image: ${POSTGRES_IMAGE:-postgres:16-alpine}
```

### ⚠️ Важно

- **`build.args` ≠ `environment`**. Build-args применяются только при
  `docker compose build`/`up --build` и НЕ доступны на runtime в контейнере.
- **`${VAR:-default}` в compose, дефолт в Dockerfile, и обратно** — все три
  слоя имеют одинаковые дефолты. Это сознательное дублирование: если
  Dockerfile'ом пользуются напрямую (`docker build -f`), дефолты в самом
  Dockerfile сохраняют рабочую сборку. Если же — через compose, дефолт
  применит compose ещё до того, как ARG будет резолвиться внутри Dockerfile.
- **NuGet — НЕ `--source`, а `--configfile`**. Флаг `dotnet restore --source X`
  *добавляет* источник к существующему `nuget.config` внутри SDK-образа.
  Чтобы restore использовал ТОЛЬКО `${NUGET_FEED_URL}`, нужен отдельный
  configfile (`/tmp/nuget.config`) и `--configfile`.
- **`dotnet publish` после `dotnet restore` обязательно с `--no-restore`** —
  иначе publish неявно ре-restore'ит с дефолтным конфигом.
- **Apk-mirror — sed в обоих stage'ах независимо** (runtime + build).
  Build-stage SDK от Microsoft сам по себе apk-зависимостей не ставит, но
  если в будущем понадобится — override уже на месте (на frontend-Dockerfile
  тоже sed, хотя `npm install` apk не зовёт — для единообразия и страховки).
- **Preview-теги .NET 10 — отдельный риск.** Корп-Artifactory обычно фильтрует
  preview/RC. Если DevOps заказчика скажет «только GA» — нужно либо ждать
  релиза .NET 10, либо договориться о preview-feed. Параметризация это не
  решает, но даёт точку override'а (запихнуть GA-образ под тем же ARG).
- **Корп-CA и TLS-инспекция.** Если корп-сеть делает MITM с собственным CA —
  `dotnet restore`/`npm install`/`apk add` падают на verify. Это **отдельный**
  слой override'а (не закрыт текущей правкой); решается через `COPY corp-ca.crt`
  + `update-ca-certificates`. См. рекомендацию Medium #9.

---

## ❌ Типичная ошибка

### ❌ 1. Хардкод `FROM` без ARG

```dockerfile
# БЫЛО (отменено):
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine AS build
```

**Почему вредно**: в корп. контуре MCR недоступен → `docker build` падает
ещё до первого `RUN`. Нужно править Dockerfile при каждой передаче в контур —
коммит с правкой быстро рассинхронится с upstream.

```dockerfile
# ПРАВИЛЬНО:
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine
FROM ${DOTNET_SDK_IMAGE} AS build
```

### ❌ 2. `dotnet restore --source` для override feed'а

```dockerfile
# НЕПРАВИЛЬНО — --source добавляет feed, не заменяет.
RUN dotnet restore KiloImportService.Api/KiloImportService.Api.csproj --source "${NUGET_FEED_URL}"
```

`dotnet restore` пойдёт И на корп-feed, И на дефолтный nuget.org из встроенного
конфига SDK-образа. В закрытом контуре — таймаут / DNS-fail на втором.

```dockerfile
# ПРАВИЛЬНО:
RUN dotnet nuget add source "${NUGET_FEED_URL}" --name primary --configfile /tmp/nuget.config && \
    dotnet restore ... --configfile /tmp/nuget.config
```

### ❌ 3. Забыть `--no-restore` у `dotnet publish`

```dockerfile
# НЕПРАВИЛЬНО — publish сделает свой restore с дефолтным nuget.config.
RUN dotnet restore ... --configfile /tmp/nuget.config
RUN dotnet publish KiloImportService.Api/...
```

```dockerfile
# ПРАВИЛЬНО:
RUN dotnet publish KiloImportService.Api/... --no-restore
```

### ❌ 4. `ARG` до `FROM` без переобъявления

```dockerfile
# НЕПРАВИЛЬНО — ALPINE_MIRROR невиден внутри RUN.
ARG ALPINE_MIRROR=dl-cdn.alpinelinux.org
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview-alpine AS runtime
RUN sed -i "s|dl-cdn.alpinelinux.org|${ALPINE_MIRROR}|g" /etc/apk/repositories
```

Global ARG до первого `FROM` доступен только в самом `FROM`-instruction
(`FROM ${...}`). Внутри stage'а его нужно переобъявить:

```dockerfile
# ПРАВИЛЬНО:
ARG ALPINE_MIRROR=dl-cdn.alpinelinux.org
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview-alpine AS runtime
ARG ALPINE_MIRROR
RUN sed -i ... "${ALPINE_MIRROR}" ...
```

### ❌ 5. Override только через CLI build-arg, без compose

```powershell
# НЕПРАВИЛЬНО — каждый раз вручную при сборке:
docker compose build --build-arg DOTNET_SDK_IMAGE=... --build-arg NUGET_FEED_URL=... ...
```

Это не воспроизводимо. Любой коллега, который сделает простой
`docker compose up -d`, попадёт на дефолтные публичные хосты.

```env
# ПРАВИЛЬНО — закрепить в .env:
DOTNET_SDK_IMAGE=corp.registry.alfa/mcr/dotnet/sdk:10.0-preview-alpine
NUGET_FEED_URL=https://nuget.corp.alfa/repository/nuget-proxy/index.json
```

`docker compose up -d` подхватит .env автоматически через `${VAR:-default}`
в `build.args`.

---

## 🔄 Как перевести сборку в корп. контур

1. Скопировать актуальный `.env.example` → `.env`.
2. Раскомментировать секцию **7. Build-time** и подставить корп-значения:
   ```env
   DOTNET_SDK_IMAGE=corp.registry.alfa/mcr/dotnet/sdk:10.0-preview-alpine
   DOTNET_ASPNET_IMAGE=corp.registry.alfa/mcr/dotnet/aspnet:10.0-preview-alpine
   NODE_IMAGE=corp.registry.alfa/library/node:20-alpine
   POSTGRES_IMAGE=corp.registry.alfa/library/postgres:16-alpine
   NUGET_FEED_URL=https://nuget.corp.alfa/repository/nuget-proxy/index.json
   NPM_REGISTRY_URL=https://npm.corp.alfa/repository/npm-proxy/
   ALPINE_MIRROR=alpine.mirror.corp.alfa
   ```
3. Запустить `docker compose build` — compose подставит build-args, Dockerfile
   подхватит ARG, никаких хождений в публичный интернет.
4. Опционально — проверить, что сборка не лезла наружу:
   ```powershell
   docker compose build --no-cache --progress=plain backend | Select-String -Pattern 'http'
   ```
   Только корп-хосты в выводе.

### Точки, которые корп-DevOps должен подтвердить заранее
- Все base-образы (`dotnet/sdk:10.0-preview-alpine`, `dotnet/aspnet:10.0-preview-alpine`,
  `node:20-alpine`, `postgres:16-alpine`) **залиты** в корп-Artifactory/Harbor.
  ⚠️ Preview-теги .NET 10 могут быть не зеркалены — уточнить.
- NuGet feed зеркалит `api.nuget.org` v3, включая preview-пакеты (см. список
  PackageReference'ов в [KiloImportService.Api.csproj](../KiloImportService.Api/KiloImportService.Api.csproj)).
- npm registry зеркалит `registry.npmjs.org` включая scope `@alfalab/*`,
  `@microsoft/*`, `@testing-library/*`, `@vitejs/*`, `@types/*`,
  `@eslint/*`, `@alfalab/core-components-*`.
- Alpine mirror содержит main + community (нужны `curl`, `icu-libs`,
  `ttf-dejavu`, `fontconfig`, `ca-certificates`).

---

## 📍 Применение в проекте

| Файл | Что изменилось |
|------|----------------|
| [KiloImportService.Api/Dockerfile](../KiloImportService.Api/Dockerfile) | `ARG`-параметризация base-образов (SDK/ASPNET) + `dotnet nuget add source` через `--configfile` + `dotnet publish --no-restore` + sed-override apk-mirror |
| [KiloImportService.Web/Dockerfile](../KiloImportService.Web/Dockerfile) | `ARG NODE_IMAGE/NPM_REGISTRY_URL/ALPINE_MIRROR` + `npm config set registry` + sed-override apk-mirror |
| [docker-compose.yml](../docker-compose.yml) | `build.args` на backend и frontend, `${POSTGRES_IMAGE:-...}` на обоих postgres-сервисах |
| [.env.example](../.env.example) | Секция «7. Build-time» с закомментированными примерами корп-зеркал |
| [.env.preprod.example](../.env.preprod.example), [.env.prod.example](../.env.prod.example) | То же — секция «6/7. Build-time» |

---

## 🎯 Чек-лист добавления новой build-time зависимости

- [ ] Сначала ARG в Dockerfile с публичным дефолтом
- [ ] Если используется после `FROM` — переобъявить ARG ВНУТРИ stage'а
- [ ] Если переменная задаёт URL/feed — override через CLI команды
      (`dotnet nuget add source` / `npm config set` / `sed`), НЕ через
      создание `.npmrc`/`nuget.config` в репо (меньше файлов = меньше
      точек рассинхрона)
- [ ] `build.args` в `docker-compose.yml` с тем же `${VAR:-default}`
- [ ] Закомментированный пример в `.env.example` (+ preprod + prod)
- [ ] Запись в карту переменных в этом документе
- [ ] Проверить, что `docker compose build` без `.env` всё ещё работает
      (тест на дефолтные значения)

---

## ⚠️ Связанные документы

- [122-environment-config.md](./122-environment-config.md) — runtime-конфигурация (хосты Visary, БД, CORS)
- [123-environment-switching-guide.md](./123-environment-switching-guide.md) — операционка переключения сред
- [121-security-fixes-appsec-v1.md](./121-security-fixes-appsec-v1.md) — deny-by-default паттерн `${VAR:?...}` пришёл оттуда
- [19-net10-deployment-gotchas.md](./19-net10-deployment-gotchas.md) — грабли первого деплоя .NET 10 + Alpine (addgroup, apk-зависимости)
