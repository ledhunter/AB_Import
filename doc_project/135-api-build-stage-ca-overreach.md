# 🩹 API Dockerfile: build-stage — две независимые проблемы, два разных лечения

## 📋 Описание

В build-stage `KiloImportService.Api/Dockerfile` сложились **две независимые
проблемы**, маскирующие друг друга:

1. **CA-overreach** — `COPY certificates/` + ручная конкатенация в bundle +
   `ENV SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt`. Перезатирает
   Mozilla bundle, ломает TLS-handshake целиком (см. v1.0 ниже).
2. **CRL/OCSP в закрытом контуре** — `.NET` по дефолту делает revocation-check
   сертификата binary.alfabank.ru, идёт за CRL/OCSP-ответом на публичные
   серверы, которые из корп. контура недоступны (см. v1.1).

### v1.0 — попытка убрать CA-overreach (сборка `v0.0.1.32-SNAPSHOT`, 2026-06-16)

Перед фиксом сборка падала на `RUN dotnet restore`:

```
Retrying 'FindPackagesByIdAsyncCore' for source
  'https://binary.alfabank.ru/.../FindPackagesById()?id=Microsoft.Extensions.Http&semVerLevel=2.0.0'.
Resource temporarily unavailable (binary.alfabank.ru:443)
...
error NU1101: Unable to find package Microsoft.Extensions.Http.
  No packages exist with this id in source(s): BinaryNuGetMirror
```

`NU1101` здесь — **итог**, а не причина. Под капотом: `Resource temporarily
unavailable` = POSIX `EAGAIN` на TLS-сокет → NuGet ретраит 8 раз (~55 сек) → сдаётся
и трактует пакет как «не найден». **Реальная причина — TLS-handshake к
`binary.alfabank.ru:443` не проходит**, потому что доверенный CA bundle в
build-stage оказался неполным.

### v1.1 — после убирания CA-overreach всплыл revocation (сборка `v0.0.1.33-SNAPSHOT`, 2026-06-16)

С системным Mozilla bundle TLS-handshake проходит, но .NET валит restore:

```
The SSL connection could not be established, see inner exception.
  The remote certificate is invalid because of errors in the certificate chain:
  RevocationStatusUnknown, OfflineRevocation
```

В v1.0 я ошибочно убрал вместе с `SSL_CERT_FILE` ещё и
`NUGET_CERT_REVOCATION_MODE=offline` +
`DOTNET_SYSTEM_NET_SECURITY_NOREVOCATIONCHECKBYDEFAULT=true`, считая их
тоже отсебятиной. Это разные истории — revocation env действительно нужны
в нашем корп. контуре, потому что CRL/OCSP-серверы недоступны.

---

## 🔍 Корневая причина

В build-stage Dockerfile было четыре «защитных» ENV-переменных и ручная
установка корп. CA:

```dockerfile
# БЫЛО — отсебятина, отсутствует в эталоне service-dev
ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false \
    NUGET_CERT_REVOCATION_MODE=offline \
    DOTNET_SYSTEM_NET_SECURITY_NOREVOCATIONCHECKBYDEFAULT=true \
    SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt \
    SSL_CERT_DIR=/etc/ssl/certs

COPY certificates/ /usr/local/share/ca-certificates/
RUN mkdir -p /etc/ssl/certs && \
    for crt in /usr/local/share/ca-certificates/*.crt; do \
        cat "$crt" >> /etc/ssl/certs/ca-certificates.crt; \
        printf "\n" >> /etc/ssl/certs/ca-certificates.crt; \
    done
```

Что здесь шло не так:

1. **`mkdir -p /etc/ssl/certs && for ... >> ca-certificates.crt`** — на чистом
   Alpine SDK-образе `/etc/ssl/certs/ca-certificates.crt` отсутствует
   (`ca-certificates`-пакет не в базе), поэтому `>>` создаёт файл **с нуля**
   из 6 корп. CA Альфы. Системный Mozilla-bundle (с Let's Encrypt / DigiCert
   / Sectigo и сотней других публичных CA) **не дописывается** — его просто
   нет.
2. **`SSL_CERT_FILE=...`** заставляет .NET / OpenSSL читать TLS-trust-chain
   **только** из этого файла. binary.alfabank.ru использует TLS-сертификат
   от публичного CA (не от Alfa Root CA), и его цепочка теперь не
   валидируется.
3. На уровне TLS-стека Alpine .NET 10 preview эта ошибка проявляется как
   `EAGAIN` на сокете, а не как «TrustFailure» (поведение конкретно этой
   связки `dotnet-sdk:10.0-preview-alpine` + `Microsoft.NETCore.Platforms`).
4. `NUGET_CERT_REVOCATION_MODE=offline` и
   `DOTNET_SYSTEM_NET_SECURITY_NOREVOCATIONCHECKBYDEFAULT=true` —
   ортогональны проблеме, но мешают диагностике и наводят на ложный след
   «дело в CRL/OCSP».

Эталон [`service-dev/src/AlfaBuilding.SSVD.Api/Dockerfile`](../../service-dev-extract/src/AlfaBuilding.SSVD.Api/Dockerfile):

```dockerfile
FROM .../dotnet/sdk:8.0 AS build
ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false
COPY nuget.config ./
COPY <...csproj...>
RUN dotnet restore "..." --ignore-failed-sources --configfile nuget.config
```

Никаких CA, никаких SSL_CERT_*. Один ENV. Работает.

---

## ✅ Правильная реализация (v1.1, после двух итераций)

`KiloImportService.Api/Dockerfile` build-stage:

```dockerfile
FROM ${DOTNET_SDK_IMAGE} AS build
ARG NUGET_FEED_URL=""

# Три ENV — отдельные истории, не объединять в одну группу мысленно:
#   1) Подпись пакетов в зеркале — отключить (зеркало не подписано MS).
#   2) NuGet revocation — offline (закрытый контур, CRL/OCSP-серверы недоступны).
#   3) То же на уровне всего .NET runtime (страховка вне NuGet).
ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false \
    NUGET_CERT_REVOCATION_MODE=offline \
    DOTNET_SYSTEM_NET_SECURITY_NOREVOCATIONCHECKBYDEFAULT=true

WORKDIR /src
COPY nuget.config ./
# Опциональный override URL (для compose локали; в Jenkins пусто — берём дефолт).
RUN if [ -n "${NUGET_FEED_URL}" ]; then \
        sed -i "s|https://binary.alfabank.ru/.../nuget_public|${NUGET_FEED_URL}|g" nuget.config; \
    fi
COPY KiloImportService.Api/KiloImportService.Api.csproj KiloImportService.Api/
COPY Visary.Api.Client/Visary.Api.Client.csproj         Visary.Api.Client/
RUN dotnet restore "KiloImportService.Api/KiloImportService.Api.csproj" \
    --ignore-failed-sources --configfile nuget.config
COPY KiloImportService.Api/ KiloImportService.Api/
COPY Visary.Api.Client/      Visary.Api.Client/
RUN dotnet build   "..." -c Release -o /app/build  --no-restore && \
    dotnet publish "..." -c Release -o /app/publish /p:UseAppHost=false --no-restore
```

### ⚠️ Важно

- **Build-stage НЕ ставит CA**. Базовый образ `mcr.microsoft.com/dotnet/sdk:*-alpine`
  поставляется с предустановленным `ca-certificates` (Mozilla bundle) —
  этого достаточно для TLS к binary.alfabank.ru. Корп. CA нужны только для
  внутренних эндпоинтов (idp.alfaintra.net) — а они вызываются в runtime,
  не во время сборки.
- **Build-stage НЕ задаёт `SSL_CERT_FILE` / `SSL_CERT_DIR`**. Эти переменные
  переопределяют дефолтный путь к bundle, который Microsoft уже правильно
  настроил в base-образе. С неправильно собранным bundle (ручная конкатенация
  с нуля) TLS-handshake целиком ломается — симптом `EAGAIN` / `Resource
  temporarily unavailable` (v1.0).
- **Build-stage задаёт `NUGET_CERT_REVOCATION_MODE=offline` +
  `DOTNET_SYSTEM_NET_SECURITY_NOREVOCATIONCHECKBYDEFAULT=true`** (отличие от
  эталона service-dev). Эталон собирается на .NET 8 / Debian, где CRL/OCSP
  доступны — у нас .NET 10 preview / Alpine в закрытом контуре, где
  revocation-check валится `RevocationStatusUnknown, OfflineRevocation`.
- **Runtime-stage остаётся как есть** — там `COPY certificates/` + ручная
  конкатенация в bundle + `SSL_CERT_FILE`. В runtime сервису нужны корп. CA
  для idp.alfaintra.net (JWT validation для Visary OIDC). В runtime
  публичных HTTPS-вызовов сервис почти не делает — поэтому подмена bundle
  на корп.-only сходит с рук (хоть и нечисто; отдельная задача DevOps).

### 📦 Что осталось в build-stage от наших правок (не повредит)

- `ARG NUGET_FEED_URL=""` + `RUN sed` для override URL — нужно для compose
  локали (когда подсовываем публичный nuget.org вместо корп. зеркала).
  Эталон без этого; для CI-пути не активируется (`-n ""` → false → sed не
  запускается). Безопасно оставлено.

---

## ❌ Чего НЕ делать в build-stage

### ❌ Ручная конкатенация .crt в bundle через `>>`

```dockerfile
# НЕПРАВИЛЬНО — на чистом Alpine SDK файла ca-certificates.crt НЕТ,
# `>>` создаст его с нуля только из корп. CA → Mozilla bundle потерян → TLS-failure.
RUN mkdir -p /etc/ssl/certs && \
    for crt in /usr/local/share/ca-certificates/*.crt; do \
        cat "$crt" >> /etc/ssl/certs/ca-certificates.crt; \
    done
```

Если ОЧЕНЬ нужны корп. CA в build-stage (для какого-то custom-HTTPS из restore),
ставить через `apk add --no-cache ca-certificates && update-ca-certificates`
— тогда Mozilla bundle сохранится, корп. CA добавятся через `update-ca-certificates`
по системной процедуре. Но **сначала проверь, нужно ли вообще**: эталон без
этого работает.

### ❌ `SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt` в build-stage

```dockerfile
# НЕПРАВИЛЬНО — переопределяет путь к bundle, который Microsoft уже настроил.
# Опасно, если рядом стоит конкатенация .crt (см. выше) — теряется доверие
# к публичным CA.
ENV SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt
```

Дефолтный путь в Alpine .NET-образах уже правильный — переопределять не нужно.

### ❌ Откат на .NET 8 ради «как в эталоне»

Эталон — `dotnet/sdk:8.0` (Debian). У нас — `dotnet/sdk:10.0-preview-alpine`.
Это разные образы, но Microsoft в Alpine-вариантах тоже ставит `ca-certificates`
— см. [dotnet/dotnet-docker](https://github.com/dotnet/dotnet-docker) `Dockerfile`
для каждого тега. Откат на .NET 8 — серьёзная переделка, не оправдан проблемой
CA в build-stage.

---

## 📍 Применение в проекте

| Файл | Что изменилось |
|------|----------------|
| [KiloImportService.Api/Dockerfile](../KiloImportService.Api/Dockerfile) | удалены ENV `NUGET_CERT_REVOCATION_MODE`/`DOTNET_SYSTEM_NET_SECURITY_NOREVOCATIONCHECKBYDEFAULT`/`SSL_CERT_FILE`/`SSL_CERT_DIR` и блок `COPY certificates/` + `RUN mkdir … for … >> bundle` из build-stage |
| [KiloImportService.Api/Dockerfile](../KiloImportService.Api/Dockerfile) | **runtime-stage не тронут** — там CA нужны для idp.alfaintra.net |

---

## 🧪 Подтверждение работоспособности

Сравнение с эталоном [service-dev/src/AlfaBuilding.SSVD.Api/Dockerfile](../../service-dev-extract/src/AlfaBuilding.SSVD.Api/Dockerfile)
(.NET 8 / Debian) — build-stage 1-в-1, кроме:
- мы на `dotnet/sdk:10.0-preview-alpine` (vs `dotnet/sdk:8.0`),
- мы оставили `ARG NUGET_FEED_URL` для compose-override (compatible).

Локально (без доступа к binary.alfabank.ru) проверить нельзя — нужен Jenkins.

---

## 🎯 Чек-лист (если снова сломалось)

Симптом → диагностика:

| Сообщение в логе | Корень | Лечение |
|---|---|---|
| `Resource temporarily unavailable (binary.alfabank.ru:443)` → `NU1101` | TLS-handshake вообще не проходит (повреждённый CA bundle) | Убрать `SSL_CERT_FILE`/`SSL_CERT_DIR` + `COPY certificates/` + `RUN ... >> ca-certificates.crt` из build-stage |
| `RevocationStatusUnknown, OfflineRevocation` → `NU1101` | TLS-handshake проходит, но revocation-check валится (CRL/OCSP недоступны) | Добавить в build-stage `ENV NUGET_CERT_REVOCATION_MODE=offline` + `DOTNET_SYSTEM_NET_SECURITY_NOREVOCATIONCHECKBYDEFAULT=true` |
| `The package signature validation failed. NU3008` | Зеркало не подписано MS-ключом | Добавить `ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false` |

Обязательная конфигурация build-stage:

- [ ] `ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false` — есть
- [ ] `ENV NUGET_CERT_REVOCATION_MODE=offline` — есть
- [ ] `ENV DOTNET_SYSTEM_NET_SECURITY_NOREVOCATIONCHECKBYDEFAULT=true` — есть
- [ ] `SSL_CERT_FILE` / `SSL_CERT_DIR` — НЕТ
- [ ] `COPY certificates/` — НЕТ (только в runtime-stage)
- [ ] `RUN ... >> /etc/ssl/certs/ca-certificates.crt` — НЕТ (только в runtime-stage)

---

## 🔗 См. также

- [doc 132](./132-build-alignment-with-service-dev-reference.md) — согласование сборки с эталоном
- [doc 129](./129-build-time-config-for-corp-network.md) — build-args для корп. контура
- service-dev `Dockerfile` — внешний эталон в `service-dev-extract/src/AlfaBuilding.SSVD.Api/Dockerfile`
