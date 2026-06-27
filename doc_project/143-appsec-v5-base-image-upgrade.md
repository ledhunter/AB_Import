# 🩹 appsec_v5 — корневое закрытие CVE-2026-45447 через обновление base-образов

## 🎯 TL;DR

В прошлой итерации (doc 137 v1.5) добавили **диагностический** `sort -V`
verify-блок: он печатает баннер, если `libcrypto3 < 3.5.7-r0`, но
**сам ничего не лечит** — если репо не отдаёт нужную версию, apk её
не достанет.

`appsec_v5.xlsx` (2026-06-19) подтвердил: остались 2 CVE из 11 —
**CVE-2026-45447** (PKCS#7 use-after-free, потенц. RCE) на `libcrypto3` и
`libssl3` в location `libcrypto3@3.5.1-r0:1`. Это значит, что внутри
финального образа сидит **исходный** libcrypto3 из base — версия `3.5.1-r0`,
датированная моментом тегирования образа `aspnet:10.0-preview-alpine`
(весна 2025, Alpine 3.20).

**Корневое решение** (v1.6): сменить тег base-образа с
`10.0-preview-alpine` на **`10.0-alpine`** (GA-тег .NET 10).

| Тег | Поведение Microsoft | Alpine внутри | OpenSSL |
|---|---|---|---|
| `10.0-preview-alpine` | ⛔ **НЕ пересобирается** после GA-релиза (ноябрь 2025) | 3.20 (заморожен) | 3.5.1-r0 |
| `10.0-alpine` | ✅ Пересобирается при каждом security-update'е Alpine | 3.22+ (свежий) | ≥3.5.7-r0 |

Аналогично для Web: `nginx:1.27-alpine` → `nginx:1.29-alpine` (mainline,
Alpine 3.22).

---

## 📍 Почему preview-тег застрял на старом Alpine

Microsoft публикует tag'и docker-образов по правилу:

- **`X.Y-preview-alpine`** — пересобирается на каждом preview-релизе .NET (до GA).
  После GA-релиза мажорной версии (`X.Y.0`) preview-тег **остаётся, но больше
  не получает rebuild'ов**. Pull при следующей сборке выдаёт тот же digest,
  что и месяц назад.
- **`X.Y-alpine`** (без `-preview`) — GA-тег. Пересобирается:
  1. при выходе каждого патч-релиза .NET (`X.Y.7` → `X.Y.8`);
  2. при выходе security-update'а Alpine — Microsoft автоматически
     ребилдит все live-теги (`X.Y-alpine`, `X.Y.Z-alpine`).

Наш проект на `net10.0`, csproj пакеты `10.0.7` — то есть **GA-релиз**.
Сидеть на `preview-alpine` — это бесплатный технический долг: получаем
старый снапшот Alpine без security-фиксов.

После смены на `10.0-alpine` system-пакеты Alpine приезжают в base
**уже свежие** — `libcrypto3 ≥ 3.5.7-r0` гарантирован без apk-upgrade.

---

## ✅ Изменения

### 1. `KiloImportService.Api/Dockerfile`

```dockerfile
# БЫЛО:
ARG DOTNET_SDK_IMAGE=docker-hub.binary.alfabank.ru/dotnet/sdk:10.0-preview-alpine
ARG DOTNET_ASPNET_IMAGE=docker-hub.binary.alfabank.ru/dotnet/aspnet:10.0-preview-alpine

# СТАЛО:
ARG DOTNET_SDK_IMAGE=docker-hub.binary.alfabank.ru/dotnet/sdk:10.0-alpine
ARG DOTNET_ASPNET_IMAGE=docker-hub.binary.alfabank.ru/dotnet/aspnet:10.0-alpine
```

### 2. `KiloImportService.Web/Dockerfile`

```dockerfile
# БЫЛО:
ARG NGINX_IMAGE=docker-hub.binary.alfabank.ru/library/nginx:1.27-alpine

# СТАЛО:
ARG NGINX_IMAGE=docker-hub.binary.alfabank.ru/library/nginx:1.29-alpine
```

### 3. `docker-compose.yml`

Дефолты build-args изменены на GA-теги.

### 4. `.env.example`, `.env.preprod.example`, `.env.prod.example`

Закомментированные примеры переменных синхронизированы:
`10.0-alpine`, `nginx:1.29-alpine`.

### 5. doc 137 v1.6, doc 130, 131, 132

Упоминания тегов обновлены (для непрерывности доков).

---

## 🛡️ Apk-upgrade блок (doc 137 v1.4) остаётся

Apk-upgrade + verify-блок из doc 137 v1.4/v1.5 **не удаляются** — это
defense-in-depth:

1. Microsoft может задержаться с rebuild'ом GA-тега на день-два после
   выхода security-update'а Alpine. Apk-upgrade в такой ситуации
   подтянет недостающее с корп. mirror'а.
2. Verify-блок остаётся как trip-wire: если когда-нибудь libcrypto3
   в base снова откатится ниже 3.5.7-r0, баннер сразу всплывёт в логе.

После v1.6 они станут **no-op** в нормальной ситуации (apk update найдёт
«нечего обновлять», verify напечатает `OK: libcrypto3=3.5.7-r0 >= 3.5.7-r0`).

---

## ⚠️ Чек-лист DevOps перед мерджем

| Что | Команда |
|---|---|
| GA-тег SDK залит в корп. registry | `docker pull docker-hub.binary.alfabank.ru/dotnet/sdk:10.0-alpine` |
| GA-тег ASPNET залит в корп. registry | `docker pull docker-hub.binary.alfabank.ru/dotnet/aspnet:10.0-alpine` |
| Nginx 1.29-alpine залит | `docker pull docker-hub.binary.alfabank.ru/library/nginx:1.29-alpine` |
| `--pull` форсирует Docker подтянуть свежий digest | уже стоит в Jenkinsfile (см. doc 137 v1.4) |
| AppSec_v6 на пересобранном образе | `apk info -v libcrypto3` показывает ≥3.5.7-r0 в логе сборки |

Если корп. registry не содержит `10.0-alpine` (отдаёт 404 / `manifest unknown`)
— DevOps action: запросить зеркалирование `mcr.microsoft.com/dotnet/{sdk,aspnet}:10.0-alpine`.
Ссылки UI Artifactory:

- https://binary.alfabank.ru/ui/packages/docker:%2F%2Fru.alfabank.skp_utils%2Fskp-docker%2Fdotnet%2Fsdk
- https://binary.alfabank.ru/ui/packages/docker:%2F%2Fru.alfabank.skp_utils%2Fskp-docker%2Fdotnet%2Faspnet

---

## ❌ Чего НЕ делать

### ❌ Откатить `apk upgrade` блок

```dockerfile
# НЕПРАВИЛЬНО — теперь base-образ свежий, но base-rebuild идёт раз в 1-2 недели.
# Если выйдет CVE-2026-XXXXX и Alpine выкатит fix через 12 часов, а Microsoft
# пересоберёт GA-тег через 7 дней, у нас окно уязвимости. Apk-upgrade закрывает его.
FROM ${DOTNET_ASPNET_IMAGE} AS runtime
# (без apk upgrade)
```

### ❌ Пинить точную версию Alpine в FROM

```dockerfile
# НЕПРАВИЛЬНО — через 3 мес. Microsoft перейдёт на alpine3.23, такой pin даст
# stale-образ через год.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine3.22 AS base
```

Используй `10.0-alpine` — Microsoft сам перепривязывает на свежую ветку Alpine.

### ❌ Использовать `latest`

```dockerfile
# НЕПРАВИЛЬНО — latest на .NET 11 / 12 ломает совместимость с net10.0 пакетами.
FROM mcr.microsoft.com/dotnet/aspnet:latest AS base
```

`10.0-alpine` — оптимальный компромисс: фикс мажорной версии, свежий Alpine.

---

## 🔗 См. также

- [doc 137 v1.6](./137-appsec-v4-alpine-package-vulnerabilities.md) — история patch-подхода через apk-upgrade
- [doc 129](./129-build-time-config-for-corp-network.md) — build-args для base-образов
- [doc 132](./132-build-alignment-with-service-dev-reference.md) — эталон корп. сборки
- [doc 19](./19-net10-deployment-gotchas.md) — особенности .NET 10 Alpine base
