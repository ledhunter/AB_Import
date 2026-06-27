# 🏗️ Согласование сборки с эталоном `service-dev` (корп. контур Альфы)

## 📋 Описание

Адаптация артефактов сборки текущего проекта (`AB_Import`) под структуру **эталонного
работающего сервиса Альфы** `service-dev.zip` (AlfaBuilding.SSVD, .NET 8). Эталон собирается
в корп. Jenkins «как есть» — повторяем тот же шаблон артефактов, чтобы наша сборка тоже прошла.

Парный с [doc 129 (build-args для корп. контура)](./129-build-time-config-for-corp-network.md)
и [doc 131 (Jenkins-CI для тестового контура)](./131-jenkins-test-pipeline.md). Эти док-и описывают
наш собственный flow; **132** фиксирует, что именно скопировано из эталона и **почему**.

---

## 🎯 Скоп

Эталон `service-dev` НЕ содержит UI (это backend-only сервис). Наш проект — мульти-сервисный
(`KiloImportService.Api` + `KiloImportService.Web`). Поэтому:

| Этап | Сервис | Что сделано |
|------|--------|-------------|
| **1** | backend (`KiloImportService.Api`) | Полное соответствие эталону |
| **2** | frontend (`KiloImportService.Web`) | Адаптация по аналогии (эталон не покрывает) |

---

## ✅ Правильная реализация — артефакты в корне репо

| Файл | Назначение | Эталон | Текущий проект |
|------|-----------|--------|----------------|
| `version` | Версия для тегов docker-образа (Jenkinsfile: `readFile('version')`) | `0.0.14` | `0.0.1` |
| `global.json` | Pin SDK .NET — детерминированная сборка | net8 GA | `10.0.100` + `latestFeature` |
| `nuget.config` | Один feed корп. зеркала, `nuget.org` disabled | ✅ | ✅ |
| `certificates/` | 6 корневых CA Альфы (Root_CA, Sub2, TCA-SUB1/ROOT, idp.alfaintra.net, root) | ✅ | ✅ скопированы из эталона |
| `.dockerignore` | Исключает `bin`/`obj`/`node_modules`/`.vs`/документацию из docker-context | ✅ | ✅ |
| `Jenkinsfile` | Pipeline на k8s-агенте `dotnet*-builder` | `dotnet8` | `dotnet10` (мульти-сервис) |
| `jenkinsConfiguration.json` | dockerRepository / registryUrl / services | single | array `services[]` (Api+Web) |

### ⚠️ Важно

- **`version` не должен быть пустым** — Jenkinsfile делает `version = "v" + readFile('version')`,
  пустой файл превращает тег в `v\n`, docker отвергает с `invalid reference format`.
- **`global.json` обязателен** — без него `dotnet --list-sdks` может выбрать любую установленную
  версию, и сборка ломается на minor-несовместимостях (особенно на preview-SDK .NET 10).
- **`certificates/` нужны** для `update-ca-certificates` внутри runtime-stage Dockerfile.
  Без них HTTPS-вызовы из контейнера к `idp.alfaintra.net` (OIDC JWT-валидация) и
  `binary.alfabank.ru` (любой `HttpClient` к корп. ресурсам) падают `SSL handshake failed`.

---

## ✅ API Dockerfile — что повторили из эталона

```dockerfile
# 1. Base-образы из корп. зеркала (ARG с дефолтом на binary.alfabank.ru)
ARG DOTNET_SDK_IMAGE=docker-hub.binary.alfabank.ru/dotnet/sdk:10.0-alpine
ARG DOTNET_ASPNET_IMAGE=docker-hub.binary.alfabank.ru/dotnet/aspnet:10.0-alpine

# 2. Build-stage ENV (отличие от эталона — мы на .NET 10 preview / Alpine в закрытом
#    контуре, эталон на .NET 8 / Debian с доступным CRL/OCSP):
#    ⚠️ НЕТ `SSL_CERT_*`, НЕТ `COPY certificates/` — это ломает TLS. См. doc 135.
ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false \
    NUGET_CERT_REVOCATION_MODE=offline \
    DOTNET_SYSTEM_NET_SECURITY_NOREVOCATIONCHECKBYDEFAULT=true

# 3. nuget.config КОПИРУЕТСЯ из корня, не генерируется на лету
COPY nuget.config ./
RUN dotnet restore "<project>" --ignore-failed-sources --configfile nuget.config

# 4. Корневые CA в runtime-stage (для idp.alfaintra.net JWT; build-stage в них НЕ нуждается)
COPY certificates/ /usr/local/share/ca-certificates/
RUN update-ca-certificates  # эталон Debian; у нас Alpine — ручная конкатенация
```

### ⚠️ Важно

- **`--ignore-failed-sources`** — `nuget.org` отключён в `disabledPackageSources`,
  без флага restore трактует это как ошибку и падает.
- **`--no-restore` у `dotnet publish/build`** — иначе они ходят на дефолтный
  `nuget.org` (которого в зеркале нет) и валятся timeout'ом.
- **ARG до FROM** — global, после FROM их НУЖНО переобъявить (см. doc 129).
- **CA только в runtime-stage** — build-stage пользуется системными Mozilla CA
  из base-образа (Microsoft предустанавливает `ca-certificates` в `dotnet/sdk:*-alpine`).
  Попытка добавить корп. CA через ручную конкатенацию в build-stage ломает
  restore с `Resource temporarily unavailable (binary.alfabank.ru:443)` →
  `NU1101`. См. doc 135.

---

## ❌ Типичные ошибки, ломающие сборку под эталон

```dockerfile
# НЕПРАВИЛЬНО — динамическая генерация nuget.config через `dotnet nuget add source`
RUN dotnet nuget add source "${NUGET_FEED_URL}" --name primary --configfile /tmp/nuget.config && \
    dotnet restore <project> --configfile /tmp/nuget.config
# Проблема: эталон не передаёт NUGET_FEED_URL в Jenkins-сборке, дефолт `api.nuget.org` —
# а в закрытом контуре нет выхода на nuget.org.
```

```dockerfile
# НЕПРАВИЛЬНО — base из публичного registry без override
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine AS build
# Проблема: в закрытом корп. контуре mcr.microsoft.com недоступен.
# Эталон зашивает корп. зеркало прямо в FROM (без ARG).
# Мы — ARG с дефолтом на корп. зеркало, и compose переопределяет на public для локали.
```

```dockerfile
# НЕПРАВИЛЬНО — забыли DOTNET_NUGET_SIGNATURE_VERIFICATION=false
FROM ${DOTNET_SDK_IMAGE} AS build
RUN dotnet restore <project> --configfile nuget.config
# Проблема: пакеты в корп. зеркале не подписаны Microsoft, restore валится:
#   "The package signature validation failed. NU3008..."
```

---

## 📍 Применение в проекте

| Артефакт | Файл | Источник истины |
|----------|------|------------------|
| Версия для тега | [version](../version) | Bump'ит DevOps перед каждым релизом |
| Pin SDK | [global.json](../global.json) | `10.0.100` (`latestFeature`) |
| NuGet feed | [nuget.config](../nuget.config) | `BinaryNuGetMirror` |
| Корневые CA | [certificates/](../certificates/) | 6 файлов из эталона |
| API build | [KiloImportService.Api/Dockerfile](../KiloImportService.Api/Dockerfile) | этап 1 |
| Web build | [KiloImportService.Web/Dockerfile](../KiloImportService.Web/Dockerfile) | этап 2 |
| Web nginx | [KiloImportService.Web/nginx.conf](../KiloImportService.Web/nginx.conf) | SPA + security headers |
| CI-pipeline | [Jenkinsfile](../Jenkinsfile) | мульти-сервис (см. doc 131) |
| CI-конфиг | [jenkinsConfiguration.json](../jenkinsConfiguration.json) | services[] {Api, Web} |

---

## 🎯 Чек-лист совместимости с эталоном

### Этап 1 — backend (готово к Jenkins)

- [x] `version` заполнен (`0.0.1`)
- [x] `global.json` создан (`10.0.100` `latestFeature`)
- [x] `nuget.config` сведён к одному feed (BinaryNuGetMirror)
- [x] `certificates/` скопированы из эталона (6 файлов)
- [x] `.dockerignore` исключает `node_modules`/`bin`/`obj`/документацию
- [x] API `Dockerfile`:
  - [x] base из `docker-hub.binary.alfabank.ru/dotnet/...:10.0`
  - [x] `DOTNET_NUGET_SIGNATURE_VERIFICATION=false`
  - [x] `COPY nuget.config ./` + `--configfile nuget.config`
  - [x] `--ignore-failed-sources` на restore
  - [x] `--no-restore` на build/publish
  - [x] `COPY certificates/` + `update-ca-certificates`
  - [x] `USER app` (non-root) + `EXPOSE 5000`
- [x] `jenkinsConfiguration.json` указывает `dotNetProjectName` = csproj API
- [x] `Jenkinsfile` `dotnet restore` идёт с `--configfile nuget.config`
- [ ] **DevOps-зависимость**: подтвердить, что в кластере Jenkins есть pod-template `dotnet10`
      с `dotnet10-builder` контейнером. Если нет — задача DevOps (см. doc 131).
- [ ] **DevOps-зависимость**: подтвердить, что в `docker-hub.binary.alfabank.ru/dotnet/...:10.0-preview-alpine`
      зеркало содержит preview-теги .NET 10 (см. doc 129 «остаточные задачи»).

### Этап 2 — UI (готово, но требует подтверждения URL'а npm-зеркала)

- [x] Web `Dockerfile`:
  - [x] base из `docker-hub.binary.alfabank.ru/library/node:20-alpine` / `nginx:1.29-alpine`
  - [x] `NPM_REGISTRY_URL` дефолт — `https://binary.alfabank.ru/artifactory/api/npm/npm_public/`
  - [x] multi-stage: `dev` (Vite) / `build` (npm ci + vite build) / `prod` (nginx)
- [x] `KiloImportService.Web/nginx.conf` создан (SPA-fallback + security headers, `listen 8080`)
- [x] `KiloImportService.Web/.dockerignore` исключает `node_modules`/`dist`/`.env`
- [ ] **DevOps-зависимость**: подтвердить точный URL корп. npm-registry (паттерн `npm_public`
      может отличаться). Передать через `.env` `NPM_REGISTRY_URL=...` или поправить дефолт ARG.

---

## 🧪 Локальная проверка перед Jenkins

```bash
# Backend
docker build --no-cache -f KiloImportService.Api/Dockerfile -t kilo-import-api:test .

# Frontend (prod-stage, как в Jenkins)
docker build --no-cache --target prod -f KiloImportService.Web/Dockerfile \
  -t kilo-import-web:test KiloImportService.Web

# Локальная docker-compose сборка (использует mcr.microsoft.com через .env-дефолты)
cp .env.example .env  # поправить пароли БД и VISARY_BASE_URL
docker compose build
docker compose up -d
```

В закрытом контуре локальный `docker build` без `--build-arg` пойдёт на `binary.alfabank.ru`
(дефолты ARG). Локально (вне корп. сети) сборку выполняй через `docker compose build` —
compose передаёт `DOTNET_SDK_IMAGE=${DOTNET_SDK_IMAGE:-mcr.microsoft.com/...}` через args
из `.env`, и `mcr.microsoft.com` используется по умолчанию.

---

## 🔗 См. также

- [doc 129 — build-args для корп. контура](./129-build-time-config-for-corp-network.md) — описание `${VAR:-default}` паттерна
- [doc 130 — Kubernetes deployment guide](./130-kubernetes-deployment-guide.md) — что делать после CI
- [doc 131 — Jenkins CI для тестового контура](./131-jenkins-test-pipeline.md) — наш собственный pipeline (декларативный, не эталонный)
- [doc 122 — environment config](./122-environment-config.md) — переменные runtime (vs. build-time)
