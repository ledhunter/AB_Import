# ════════════════════════════════════════════════════════════════════
# KiloImportService.Api — multi-stage build для корп. контура Альфы.
# Структура повторяет эталонный сервис (см. doc_project/132):
#   • base-образы зеркала Альфы — параметризованы через ARG, дефолты публичные;
#   • restore идёт строго через /nuget.config из корня репо (один feed);
#   • корневые CA Альфы из /certificates/ ставятся в runtime-stage,
#     чтобы HTTPS-вызовы к idp.alfaintra.net / binary.alfabank.ru проходили SSL handshake.
#   • DOTNET_NUGET_SIGNATURE_VERIFICATION=false — зеркала Альфы не подписаны MS,
#     без этой переменной restore валится «signature validation failed».
# Build-context — корень репо (см. docker-compose.yml / jenkinsConfiguration.json).
# ════════════════════════════════════════════════════════════════════
# Дефолты — корп. зеркало Альфы (эталонный путь, см. doc_project/132).
# Для локальной сборки docker-compose передаёт DOTNET_SDK_IMAGE из .env с дефолтом
# `mcr.microsoft.com/...` (см. docker-compose.yml secrets: 7 / .env.example).
ARG DOTNET_SDK_IMAGE=docker-hub.binary.alfabank.ru/dotnet/sdk:10.0-preview-alpine
ARG DOTNET_ASPNET_IMAGE=docker-hub.binary.alfabank.ru/dotnet/aspnet:10.0-preview-alpine

# ────────────── Base (runtime) ──────────────
FROM ${DOTNET_ASPNET_IMAGE} AS base
WORKDIR /app

# ────────────── Build stage ──────────────
FROM ${DOTNET_SDK_IMAGE} AS build
# Опциональный override URL'а NuGet feed'а (для локальной сборки без зеркала).
# Пусто (дефолт) ⇒ берётся BinaryNuGetMirror из скопированного nuget.config — путь эталона.
ARG NUGET_FEED_URL=""
# Три ENV нужны в build-stage. Эталон `service-dev` (см. doc 132) использует
# только первую — но эталон собирается на .NET 8 / Debian + сертификат
# binary.alfabank.ru с доступным CRL/OCSP-чейном. У нас .NET 10 preview / Alpine
# в закрытом корп. контуре, где CRL-серверы недоступны (см. doc 135 v1.1):
#
#   DOTNET_NUGET_SIGNATURE_VERIFICATION=false — зеркало не подписано MS-ключом,
#     иначе restore валится `The package signature validation failed. NU3008`.
#
#   NUGET_CERT_REVOCATION_MODE=offline — NuGet не пытается достать CRL/OCSP-ответ
#     для сертификата binary.alfabank.ru. Закрытый корп. контур не пускает наружу,
#     `restore` валит:
#       The remote certificate is invalid because of errors in the certificate chain:
#         RevocationStatusUnknown, OfflineRevocation
#
#   DOTNET_SYSTEM_NET_SECURITY_NOREVOCATIONCHECKBYDEFAULT=true — то же на уровне
#     всего .NET runtime (страховка для случаев, когда NUGET-переменная не покрывает
#     все HttpClient-вызовы внутри restore — `dotnet pack`-ходы, плагины и т.п.).
#
# ⚠️ НЕ добавлять сюда `SSL_CERT_FILE` / `SSL_CERT_DIR` и НЕ копировать `certificates/`
#    в build-stage. Это совсем другая история — переопределение пути к bundle ломает
#    TLS-handshake целиком (Mozilla bundle стирается ручной конкатенацией 6 корп. CA).
#    Симптом — EAGAIN на сокете, restore выглядит как «пакеты не найдены». См. doc 135.
ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false \
    NUGET_CERT_REVOCATION_MODE=offline \
    DOTNET_SYSTEM_NET_SECURITY_NOREVOCATIONCHECKBYDEFAULT=true

WORKDIR /src

# 1) nuget.config — единственный feed (BinaryNuGetMirror). Кладём в корень контекста,
#    чтобы --configfile nuget.config работал из любой подпапки src-дерева.
COPY nuget.config ./
# Если NUGET_FEED_URL задан (compose `args:` или --build-arg) — подменяем URL feed'а в nuget.config.
# Без задания (Jenkins-flow по эталону) ничего не делаем, берём BinaryNuGetMirror как есть.
# `|` как sed-разделитель — URL содержит `/`, нельзя дефолтный `/`.
RUN if [ -n "${NUGET_FEED_URL}" ]; then \
        echo "Override NuGet feed -> ${NUGET_FEED_URL}"; \
        sed -i "s|https://binary.alfabank.ru/artifactory/api/nuget/v3/nuget_public|${NUGET_FEED_URL}|g" nuget.config; \
    fi

# 2) Слой restore: сначала только .csproj — кэш cli'а реюзается, если код менялся, а deps нет.
COPY KiloImportService.Api/KiloImportService.Api.csproj KiloImportService.Api/
COPY Visary.Api.Client/Visary.Api.Client.csproj         Visary.Api.Client/
RUN dotnet restore "KiloImportService.Api/KiloImportService.Api.csproj" \
    --ignore-failed-sources \
    --configfile nuget.config

# 3) Сорцы и build/publish.
COPY KiloImportService.Api/ KiloImportService.Api/
COPY Visary.Api.Client/      Visary.Api.Client/
# --no-restore — restore уже сделан выше с заданным feed; не позволяем publish
# повторно ходить за пакетами на дефолтный nuget.org (его в зеркале нет).
RUN dotnet build   "KiloImportService.Api/KiloImportService.Api.csproj" -c Release -o /app/build  --no-restore && \
    dotnet publish "KiloImportService.Api/KiloImportService.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false --no-restore

# ────────────── Final (runtime) ──────────────
FROM base AS runtime

LABEL serviceName="alfa-building-fm-import-api"
LABEL teamName="SRKBPF"

# ────────────── Security upgrade: уязвимые системные пакеты ──────────────
# Закрывает CVE из appsec_v4 (см. doc 137 v1.3):
#   libcrypto3, libssl3 — OpenSSL CVE-2026-31789/-28387/-28388/-28389/-28390/-45447,
#                         CVE-2025-15467/-69421 (8× High)
#   musl, musl-utils    — CVE-2026-40200 (qsort stack corruption)
#   zlib                — CVE-2026-22184 (untgz buffer overflow)
#
# `apk upgrade --no-cache <pkg>` — берёт latest из репо (не pin'им версию, чтобы
# через 3 мес. при выходе 3.5.8-r0 сборка не сломалась stale-pin'ом).
#
# ALPINE_REPOSITORIES_URL — полный base URL Alpine-репо без суффикса /main|/community.
# Корп. зеркало Альфы: https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable
# (Артифактори, latest-stable — всегда последний stable-выпуск Alpine).
# Path-структура корп. mirror отличается от dl-cdn.alpinelinux.org/alpine/v3.XX/ —
# поэтому НЕ можем sed'ить host, нужно ПОЛНОСТЬЮ переписать /etc/apk/repositories.
# Пустой дефолт → не трогаем repositories, используется штатный из base-образа
# (для compose-локали с outbound доступом к dl-cdn.alpinelinux.org).
#
# Громкий fallback с баннером — компромисс между v1.0/v1.1 (см. doc 137 v1.3):
# сборка не падает при недоступности репо (Jenkins без mirror'а), но баннер
# в логе хорошо виден DevOps.
ARG ALPINE_REPOSITORIES_URL=""
RUN if [ -n "${ALPINE_REPOSITORIES_URL}" ]; then \
        echo "${ALPINE_REPOSITORIES_URL}/main"      >  /etc/apk/repositories; \
        echo "${ALPINE_REPOSITORIES_URL}/community" >> /etc/apk/repositories; \
        echo "INFO: /etc/apk/repositories переписан на ${ALPINE_REPOSITORIES_URL}"; \
    fi && \
    ( apk upgrade --no-cache libcrypto3 libssl3 musl musl-utils zlib \
      && echo "OK: appsec_v4 — Alpine system packages upgraded successfully" \
    ) || ( \
        echo ""; \
        echo "############################################################"; \
        echo "# WARNING: apk upgrade FAILED — Alpine репо недоступен.    #"; \
        echo "# Уязвимые системные пакеты ОСТАЮТСЯ в финальном образе:    #"; \
        echo "#   libcrypto3 libssl3 musl musl-utils zlib                 #"; \
        echo "# AppSec_v4 будет показывать те же CVE до устранения.       #"; \
        echo "#                                                          #"; \
        echo "# DevOps action:                                           #"; \
        echo "#  передать ALPINE_REPOSITORIES_URL в build-args, например:#"; \
        echo "#  https://binary.alfabank.ru/artifactory/alpine-mirror/   #"; \
        echo "#  latest-stable                                            #"; \
        echo "# См. doc_project/137 v1.3.                                 #"; \
        echo "############################################################"; \
        echo ""; \
    )

# Доп. пакеты через apk add НЕ ставим — в закрытом корп. контуре Jenkins-агент
# не имеет выхода на dl-cdn.alpinelinux.org (Permission denied на прокси), а
# подтверждённого корп. Alpine-зеркала у Альфы пока нет (см. doc 132).
# Эталонный сервис тоже ничего не ставит (он на Debian-base, где ca-certificates
# идёт из коробки).
#
# Что встроено в base aspnet:10.0-preview-alpine из стандартного Dockerfile
# Microsoft (см. github.com/dotnet/dotnet-docker): ca-certificates, icu-libs,
# libstdc++, libintl, tzdata. То есть update-ca-certificates ниже работает.
#
# ⚠️ TODO (отдельная задача для DevOps): для кириллицы в PDF-отчётах PDFsharp
# нужны шрифты ttf-dejavu + fontconfig. До настройки корп. Alpine-зеркала
# PDF-функция работать НЕ будет (шрифты будут заменены, либо рантайм-ошибка).
# Решение через копирование .ttf в /usr/share/fonts/ — отдельный commit, когда
# DevOps подтвердят, можно ли тянуть files-репозиторий с шрифтами из binary.
#
# ⚠️ TODO: healthcheck в docker-compose.yml использует curl — в Alpine-base curl
# не входит. На k8s-проде healthcheck через httpGet, не shell — там безопасно.
# Для локального compose-стенда healthcheck заменён на wget --spider (см. compose).
#
# `addgroup`/`adduser -S` — из busybox, всегда доступны без apk.
RUN (getent group app  >/dev/null || addgroup -S app) && \
    (getent passwd app >/dev/null || adduser  -S app -G app)

# Корневые CA Альфы: idp.alfaintra.net.crt — для JWT-валидации (issuer Visary OIDC),
# Alfa-Bank_Root_CA_2012.crt + Sub2 — для доступа к binary.alfabank.ru.
# Каталог /usr/local/share/ca-certificates/ — стандартное место «исходников» CA
# (туда же кладёт ca-certificates пакет в нормальном Linux'е).
#
# ⚠️ В aspnet:10.0-preview-alpine пакет ca-certificates НЕ установлен — нет
# команды update-ca-certificates. Установить его через apk не можем (корп. контур
# не пускает на dl-cdn.alpinelinux.org, см. doc 132).
#
# Делаем то, что update-ca-certificates делает под капотом: конкатенируем .crt-файлы
# в стандартный bundle /etc/ssl/certs/ca-certificates.crt — его читает OpenSSL,
# через него .NET TLS-стек на Linux. Команды `cat`/`mkdir`/`for` — POSIX, есть в busybox.
# Если bundle уже существует в base (системные CA) — дописываем в конец, не теряем.
COPY certificates/ /usr/local/share/ca-certificates/
RUN mkdir -p /etc/ssl/certs && \
    for crt in /usr/local/share/ca-certificates/*.crt; do \
        cat "$crt" >> /etc/ssl/certs/ca-certificates.crt; \
        printf "\n" >> /etc/ssl/certs/ca-certificates.crt; \
    done

WORKDIR /app
COPY --from=build /app/publish .

# Каталог для загруженных XLSX — bind-volume в docker-compose / PVC в k8s.
RUN mkdir -p /var/lib/import-files && chown -R app:app /var/lib/import-files /app

USER app
# SSL_CERT_FILE — явно указываем bundle для OpenSSL/.NET TLS. В минимальном
# aspnet:alpine без ca-certificates пакета default-путь может не работать;
# с явным env переменной .NET HttpClient читает наш bundle (созданный выше
# через ручную конкатенацию).
ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_TEMP=/tmp \
    DOTNET_GCServer=1 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt \
    SSL_CERT_DIR=/etc/ssl/certs

EXPOSE 5000/tcp
ENTRYPOINT ["dotnet", "KiloImportService.Api.dll"]
