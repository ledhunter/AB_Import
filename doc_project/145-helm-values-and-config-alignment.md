# 🎯 Конфигурация через helm values и приведение к эталону service-dev

## 📋 Описание

K8s-деплой в платформе Альфы построен поверх **общего helm-chart'а**,
который читает наш yaml-манифест (`alfa-building-fm-import*.yaml`) и
маппит блок `manifest.app.environments` на ENV-переменные пода. Значения
вида `$(VAR)` платформа подставляет из своих **helm values** (Vault /
ConfigMap / Secrets — на стороне платформы).

Цель этой задачи (см. doc 145 / запрос аналитика 2026-06-23):
**получать пользователей, пароли и хосты сред исключительно через helm
values**, без хардкода в коммитимом коде/конфигах.

Структурно это означает «приведение к эталону `service-dev`» (см.
[doc 132](./132-build-alignment-with-service-dev-reference.md)):

1. Один артефакт деплоится в один pod (БД одна, secrets одни) →
   **одна connection-string `ConnectionStrings:AbFmImport`**.
2. Имена секций в `appsettings.json` совпадают с эталонными:
   `EndpointsConfiguration:{VisaryApi,VisaryAuthApi}`,
   `JwtConfiguration:{Authority,Audience,UseSsl,Secret}`,
   `Features:{Swagger,RequireJwt}`, `Cors` (одна строка), `ImportStorage`.
3. Все хосты/пароли в yaml — через `$(VAR)`-плейсхолдеры
   (`DB_HOST`, `ABFMI_DB_USER_PASSWORD`, `IDENTIRY_URL`,
   `IDENTITY_CLIENT_SYSTEM_VISARY_SECRET`, `WEBAPI_URL`, `UI_PUBLIC_URL`).

---

## ✅ Правильная реализация (выполнено 2026-06-23, B1)

### Один DbContext = один artifact_id, но **две схемы внутри одной БД**

`appsettings.json` (коммитится, пустой каркас):

```jsonc
{
  "ConnectionStrings": { "AbFmImport": "" },
  "Features":          { "Swagger": false },
  "JwtConfiguration":  { "Audience": "visary" },
  "EndpointsConfiguration": {
    "VisaryApi":       { "Endpoint": "" },
    "VisaryAuthApi":   { "User": "system-visary", "Password": "" },
    "VisaryFilestorageApi": { "Endpoint": "http://filestorage:8080" }
  },
  "ImportStorage":     { "Path": "./.local-storage" },
  "Cors":              "https://localhost:44466"
}
```

`alfa-building-fm-import.yaml` (helm values подставляет `$(VAR)`):

```yaml
manifest:
  app:
    name: ab-fm-import
    artifact_id: alfa-building-fm-import
    environments:
      ConnectionStrings__AbFmImport: >-
        Database=ab_fm_import;
        Host=$(DB_HOST);
        Port=$(DIRECT_DB_PORT);
        Username=ab_fm_import_user;
        Password=$(ABFMI_DB_USER_PASSWORD);
        ...
      Features__Swagger: false
      Features__RequireJwt: true
      JwtConfiguration__Authority: $(IDENTIRY_URL)
      JwtConfiguration__Secret: $(IDENTITY_CLIENT_SYSTEM_VISARY_SECRET)
      JwtConfiguration__UseSsl: false
      EndpointsConfiguration__VisaryApi__Endpoint: $(WEBAPI_URL)/api
      EndpointsConfiguration__VisaryAuthApi__TokenEndpoint: $(IDENTIRY_URL)/connect/token
      Cors: $(UI_PUBLIC_URL)
```

`Program.cs`:

```csharp
var abFmImportConn = builder.Configuration.GetConnectionString("AbFmImport");
builder.Services.AddDbContext<ImportServiceDbContext>(opt =>
    opt.UseNpgsql(abFmImportConn,
        npg => npg.MigrationsHistoryTable("__ef_migrations_history",
                                          ImportServiceDbContext.SchemaName)));
builder.Services.AddDbContext<VisaryDbContext>(opt =>
    opt.UseNpgsql(abFmImportConn,
        npg => npg.MigrationsHistoryTable("__ef_migrations_history",
                                          VisaryDbContext.DataSchema)));
```

### ⚠️ Важно

- **`$(VAR)` в yaml — это helm values, НЕ env-substitution Kubernetes**.
  Имена `DB_HOST`, `IDENTIRY_URL`, `WEBAPI_URL` — стандартные у платформы
  Альфы (см. эталон `service-dev`/`alfa-building-ssvd.yaml`).
  Опечатка `IDENTIRY` — намеренно сохранена, так в values платформы.
- **`Cors` — одна строка**, не массив. `Program.cs` поддерживает
  comma-separated на случай нескольких origin'ов.
- **`Features:RequireJwt=true` + пустой `JwtConfiguration:Authority`
  = fatal на старте** — backend не запускается с молчаливо отключённой
  аутентификацией.
- **Схема `Data` создаётся EF-миграцией** (`Migrations/Visary/InitialDataSchema`),
  старые `db/visary/init/*.sql` удалены.
- **`__ef_migrations_history` у каждого DbContext своя** (в свою схему),
  иначе таблица миграций станет общей и Migrate сломается на чужих
  записях.

---

## ❌ Антипаттерны (что было до B1, не повторять)

### ❌ Две отдельные БД на проде

```yaml
# БЫЛО (отменено):
ConnectionStrings__ServiceDb: ...service-db-host...
ConnectionStrings__VisaryDb:  ...visary-db-host...
```
**Почему вредно**: платформа Альфы — одна БД на pod, два секрета +
два host'а = двойной запрос на инфраструктуру, двойная точка отказа,
и Vault'у нужны две пары creds. У эталона service-dev одна БД.

```yaml
# СТАЛО (B1, одна БД с двумя схемами):
ConnectionStrings__AbFmImport: Database=ab_fm_import;Host=$(DB_HOST);...
```

### ❌ `db/visary/init/*.sql` через docker-entrypoint-initdb

```yaml
# БЫЛО:
postgres-visary:
  volumes:
    - ./db/visary/init:/docker-entrypoint-initdb.d:ro
```
**Почему вредно**: init-скрипты выполняются только при первом старте
контейнера, на проде/k8s их вообще не запускают (managed-PG). При
расхождении SQL-схемы с `OnModelCreating` EF-маппинг ломается «молча»
(NULL вместо данных). Заменено EF-миграцией `InitialDataSchema` —
схема всегда консистентна с C#-моделью.

### ❌ Имена секций «как удобно», не совпадают с эталоном

```csharp
// БЫЛО:
public const string SectionName = "Visary";           // VisaryOptions
public const string SectionName = "Visary:Auth";      // VisaryAuthOptions
builder.Configuration.GetSection("Auth")["Authority"]; // JWT
```
**Почему вредно**: yaml-манифесты у Альфы стандартизированы под имена
эталона. Если каждый сервис называет одно и то же по-своему,
платформа не может писать общие helm-чарты. Эталон уже инвестировал в
стандарт — мы переезжаем на него.

```csharp
// СТАЛО:
public const string SectionName = "EndpointsConfiguration:VisaryApi";
public const string SectionName = "EndpointsConfiguration:VisaryAuthApi";
builder.Configuration.GetSection("JwtConfiguration")["Authority"];
```

### ❌ `appsettings.Local.json` с реальным JWT в git

См. doc 122 § «Известная остаточная проблема». Структурно перенесён
в новую секцию, но revoke + чистка истории — отдельная задача.

---

## 🗺️ Маппинг старое → новое (для миграции существующих deployment'ов)

| Старое имя | Новое имя (эталон) |
|---|---|
| `ConnectionStrings:ServiceDb` | `ConnectionStrings:AbFmImport` (одна БД, схема `import`) |
| `ConnectionStrings:VisaryDb` | `ConnectionStrings:AbFmImport` (та же БД, схема `Data`) |
| `Visary:BaseUrl` | `EndpointsConfiguration:VisaryApi:Endpoint` |
| `Visary:BearerToken` | `EndpointsConfiguration:VisaryApi:BearerToken` |
| `Visary:Auth:TokenEndpoint` | `EndpointsConfiguration:VisaryAuthApi:TokenEndpoint` |
| `Visary:Auth:ClientId` | `EndpointsConfiguration:VisaryAuthApi:ClientId` |
| `Auth:Authority` | `JwtConfiguration:Authority` |
| `Auth:Audience` | `JwtConfiguration:Audience` |
| `Auth:RequireHttpsMetadata` | `JwtConfiguration:UseSsl` |
| `Cors:AllowedOrigins:0` | `Cors` (одна строка; comma-separated для нескольких) |
| (не было) | `Features:RequireJwt` — основной триггер JWT-валидации |

В env маппится двойным подчёркиванием
(`EndpointsConfiguration__VisaryApi__Endpoint`).

---

## 📍 Применение в проекте

| Файл | Что изменилось |
|------|----------------|
| [Visary.Api.Client/VisaryOptions.cs](../Visary.Api.Client/VisaryOptions.cs) | `SectionName = "EndpointsConfiguration:VisaryApi"`, `BaseUrl` → `Endpoint` |
| [Visary.Api.Client/Auth/VisaryAuthOptions.cs](../Visary.Api.Client/Auth/VisaryAuthOptions.cs) | `SectionName = "EndpointsConfiguration:VisaryAuthApi"` |
| [Visary.Api.Client/Common/VisaryHttpBase.cs](../Visary.Api.Client/Common/VisaryHttpBase.cs) | Чтение `Options.Endpoint` (был `BaseUrl`); ошибка с новым именем секции |
| [KiloImportService.Api/Program.cs](../KiloImportService.Api/Program.cs) | Одна connection-string `AbFmImport`; оба DbContext'а — на одну строку; `Auth` → `JwtConfiguration`; `Features:RequireJwt`; `Cors` как строка; Migrate обоих контекстов на старте |
| [KiloImportService.Api/Migrations/Visary/](../KiloImportService.Api/Migrations/Visary/) | Новая EF-миграция `InitialDataSchema` — заменяет старые init-скрипты |
| `db/visary/init/*.sql` | **Удалено** (заменено EF-миграцией) |
| [appsettings.json](../KiloImportService.Api/appsettings.json) | Эталонные секции, пустые placeholder'ы |
| [appsettings.Local.json](../KiloImportService.Api/appsettings.Local.json) | Структурно переименовано в `EndpointsConfiguration:VisaryApi:BearerToken` (revoke токена — отдельная задача) |
| [docker-compose.yml](../docker-compose.yml) | Один `postgres` (вместо `postgres-service` + `postgres-visary`); новые env-имена |
| [.env.example](../.env.example) / `.preprod.example` / `.prod.example` | Новые имена секций; одна `ConnectionStrings__AbFmImport`; `ABFMI_DB_USER`/`ABFMI_DB_USER_PASSWORD` |
| [alfa-building-fm-import.yaml](../../alfa-building-fm-import.yaml) | Дополнен `Cors`, OIDC-секция `VisaryAuthApi`, `JwtConfiguration:Audience` |
| [alfa-building-fm-import-dev.yaml](../../alfa-building-fm-import-dev.yaml) | На dev-стенде `Features__Swagger: true` |
| [alfa-building-fm-import-web.yaml](../../alfa-building-fm-import-web.yaml) | **Новый** — манифест для frontend `kilo-import-web` |
| [alfa-building-fm-import-web-dev.yaml](../../alfa-building-fm-import-web-dev.yaml) | **Новый** — dev-overlay для frontend |
| [jenkinsConfiguration.json](../jenkinsConfiguration.json) | `artifactName` сервисов = `alfa-building-fm-import{,-web}` (соответствует `artifact_id` в yaml) |
| [VisaryClients/TestVisaryClientFactory.cs](../KiloImportService.Api.Tests/VisaryClients/TestVisaryClientFactory.cs) / [VisaryLiveClientFactory.cs](../KiloImportService.Api.Tests/VisaryLive/VisaryLiveClientFactory.cs) | `BaseUrl` → `Endpoint` в `new VisaryOptions { ... }` |

---

## 🌐 Адреса для проверки

| Контур | Backend URL | Frontend URL |
|--------|-------------|--------------|
| dev/test | `https://abdev.moscow.alfaintra.net/api/ab-fm-import/health` | `https://abdev.moscow.alfaintra.net/` |
| preprod | по publish'у platform | по publish'у platform |
| prod    | по publish'у platform | по publish'у platform |

Swagger на dev-стенде: `https://abdev.moscow.alfaintra.net/api/ab-fm-import/swagger`
(в `-dev.yaml` `Features__Swagger: true`; в основном yaml выключен).

SignalR-хаб: `wss://abdev.moscow.alfaintra.net/api/ab-fm-import/hubs/imports?access_token=...`.

---

## 🎯 Чек-лист (выполнено 2026-06-23)

- [x] Имена секций в коде = эталонная иерархия
- [x] Одна connection-string `AbFmImport`, два DbContext'а на одну БД
- [x] EF-миграция `InitialDataSchema` заменяет `db/visary/init/*.sql`
- [x] `Migrate` обоих контекстов на старте, в свои `__ef_migrations_history`
- [x] `docker-compose.yml` — один postgres, новые env-имена
- [x] `.env*.example` обновлены
- [x] `alfa-building-fm-import*.yaml` дополнен (Cors, OIDC, Audience)
- [x] `alfa-building-fm-import-web*.yaml` создан (frontend)
- [x] `jenkinsConfiguration.json` — `artifactName` = `artifact_id` yaml
- [x] Тесты обновлены и прошли (490 ОК, 0 fail)
- [x] doc 145 написан
- [ ] **Остаточная задача**: `appsettings.Local.json` JWT — revoke + git filter-repo
- [ ] **Остаточная задача**: задокументировать у DevOps Альфы реальные имена values
      (`DB_HOST`, `DIRECT_DB_PORT`, `IDENTIRY_URL`, etc.) и какие из них
      требуют ExternalSecret/Vault

---

## ⚠️ Связанные документы

- [122-environment-config.md](./122-environment-config.md) — env-переменные и SSOT (обновлён под новые имена)
- [130-kubernetes-deployment-guide.md](./130-kubernetes-deployment-guide.md) — k8s-деплой (обновлён под одну БД)
- [132-build-alignment-with-service-dev-reference.md](./132-build-alignment-with-service-dev-reference.md) — эталон service-dev
- [107-visary-token-provider.md](./107-visary-token-provider.md) — OIDC refresh-flow
- [111-incoming-jwt-auth.md](./111-incoming-jwt-auth.md) — JWT-валидация входящих
- [121-security-fixes-appsec-v1.md](./121-security-fixes-appsec-v1.md) — deny-by-default
