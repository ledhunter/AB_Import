# ════════════════════════════════════════════════════════════════════
# Параметризация для корп. контура: base-образы, NuGet feed и Alpine mirror
# задаются через build-args. Дефолты — публичные хосты, сборка из коробки
# работает локально. В закрытом контуре DevOps переопределяет через
# `docker compose build --build-arg ...` или через .env (см. docker-compose.yml).
# См. doc_project/129-build-time-config-for-corp-network.md.
# ════════════════════════════════════════════════════════════════════
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-preview-alpine

# ────────────── Build stage ──────────────
# Контекст билда — корень репо (см. docker-compose.yml: context: .)
# чтобы был доступен соседний проект Visary.Api.Client.
FROM ${DOTNET_SDK_IMAGE} AS build
# ⚠️ ARG до FROM — global, после FROM его НУЖНО переобъявить, иначе он невидим в RUN.
ARG NUGET_FEED_URL=https://api.nuget.org/v3/index.json
WORKDIR /src

# Сначала копируем .csproj обоих проектов для кэша restore
COPY KiloImportService.Api/KiloImportService.Api.csproj KiloImportService.Api/
COPY Visary.Api.Client/Visary.Api.Client.csproj Visary.Api.Client/
# В закрытом контуре передать --build-arg NUGET_FEED_URL=<corp-Artifactory v3 endpoint>.
# --configfile создаётся «с нуля» в /tmp, чтобы restore использовал ТОЛЬКО заданный feed
# (а не дополнял дефолтный nuget.config из образа SDK).
RUN dotnet nuget add source "${NUGET_FEED_URL}" --name primary --configfile /tmp/nuget.config && \
    dotnet restore KiloImportService.Api/KiloImportService.Api.csproj --configfile /tmp/nuget.config

# Затем — исходники, и публикуем
COPY KiloImportService.Api/ KiloImportService.Api/
COPY Visary.Api.Client/   Visary.Api.Client/
# --no-restore — restore уже сделан выше с заданным feed; publish'у нельзя позволить
# повторно сходить за пакетами на дефолтный nuget.org.
RUN dotnet publish KiloImportService.Api/KiloImportService.Api.csproj -c Release -o /app /p:UseAppHost=false --no-restore

# ────────────── Runtime stage ──────────────
FROM ${DOTNET_ASPNET_IMAGE} AS runtime
ARG ALPINE_MIRROR=dl-cdn.alpinelinux.org
WORKDIR /app
# curl — для healthcheck. icu-libs — глобализация. ttf-dejavu + fontconfig — кириллица в PDF-отчётах (PDFsharp).
# Apk-mirror override для корп. контура: sed заменяет dl-cdn на ALPINE_MIRROR в /etc/apk/repositories.
# При дефолтном значении sed «s|X|X|g» — no-op, поведение не меняется.
# ⚠️ В aspnet:10.0-preview-alpine группа `app` уже создана образом — проверяем наличие.
RUN sed -i "s|dl-cdn.alpinelinux.org|${ALPINE_MIRROR}|g" /etc/apk/repositories && \
    apk add --no-cache curl icu-libs ttf-dejavu fontconfig && \
    (getent group app  >/dev/null || addgroup -S app) && \
    (getent passwd app >/dev/null || adduser  -S app -G app)

COPY --from=build /app .
RUN mkdir -p /var/lib/import-files && chown -R app:app /var/lib/import-files /app

USER app
ENV ASPNETCORE_URLS=http://+:5000 \
    DOTNET_GCServer=1 \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 5000
ENTRYPOINT ["dotnet", "KiloImportService.Api.dll"]
