# ════════════════════════════════════════════════════════════════════
# Параметризация для корп. контура: base-образы, npm registry и Alpine mirror
# через build-args. Дефолты — публичные хосты, локальная сборка работает.
# См. doc_project/129-build-time-config-for-corp-network.md.
#
# Stage'ы:
#   • dev  — Vite dev-server (горячий reload). Используется docker-compose.
#   • build — компиляция бандла `npm run build` → /app/dist (промежуточный).
#   • prod — nginx, раздаёт /app/dist. Используется k8s (см. doc 130).
# ════════════════════════════════════════════════════════════════════
# Дефолты — корп. зеркало Альфы (эталонный путь, см. doc_project/132).
# Для локальной сборки docker-compose передаёт NODE_IMAGE из .env с дефолтом
# `node:20-alpine` (см. docker-compose.yml секция 7 / .env.example).
ARG NODE_IMAGE=docker-hub.binary.alfabank.ru/library/node:20-alpine
ARG NGINX_IMAGE=docker-hub.binary.alfabank.ru/library/nginx:1.27-alpine

# ────────────── Dev stage (docker-compose, hot reload) ──────────────
FROM ${NODE_IMAGE} AS dev
# ARG до FROM — global; нужно переобъявить после FROM, чтобы был виден в RUN.
ARG NPM_REGISTRY_URL=https://binary.alfabank.ru/artifactory/api/npm/npm/
# Scope @alfalab/* — open-source библиотека Альфа-Банка (core-components),
# публикуется на npmjs.org. Дефолтно идёт через тот же virtual репо
# `binary.alfabank.ru/artifactory/api/npm/npm/`, который содержит и публичные,
# и alfalab-internal пакеты. Отдельный ARG оставлен для override на случай,
# если у DevOps появится отдельное @alfalab-репо.
# ⚠️ Префикс `/api/npm/<repo>/` обязателен — без `/api/npm/` Артифактори
# отвечает 406 Not Acceptable (UI browse-endpoint, а не npm API).
ARG NPM_REGISTRY_ALFALAB_URL=https://binary.alfabank.ru/artifactory/api/npm/npm/
ARG ALPINE_MIRROR=dl-cdn.alpinelinux.org
WORKDIR /app
RUN sed -i "s|dl-cdn.alpinelinux.org|${ALPINE_MIRROR}|g" /etc/apk/repositories
COPY package.json package-lock.json* ./
# scripts/ нужен ДО `npm install`: npm-хук `postinstall` (см. package.json)
# запускает `scripts/patch-alfalab-esm.mjs` — патчер пакетов
# `@alfalab/core-components-*`, без которого Vite 8/Rolldown не резолвит
# подпуть `/esm` (см. doc 134).
COPY scripts/ scripts/
# --legacy-peer-deps — транзитивная зависимость react-popper@2.3.0 объявляет
# peer-dep react@^16 || ^17 || ^18, а у нас react@19. В strict-режиме npm
# пытается резолвить дополнительный react@16-18, что может тянуть лишние
# запросы и mismatch. Флаг отключает enforcing peer-deps — npm ставит то,
# что в lockfile (react@19), peer-проверка остаётся как warning.
#
# --no-audit/--no-fund — отключают аудит-вызовы к npmjs.com (в закрытом контуре
# они дают невнятные сетевые ошибки в логе, к сборке не относятся).
RUN npm config set registry "${NPM_REGISTRY_URL}" && \
    if [ -n "${NPM_REGISTRY_ALFALAB_URL}" ]; then \
        npm config set @alfalab:registry "${NPM_REGISTRY_ALFALAB_URL}"; \
    fi && \
    npm install --legacy-peer-deps --no-audit --no-fund
COPY . .
EXPOSE 5173
ENV HOST=0.0.0.0
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0"]

# ────────────── Build stage (компилирует bundle для prod) ──────────────
FROM ${NODE_IMAGE} AS build
ARG NPM_REGISTRY_URL=https://binary.alfabank.ru/artifactory/api/npm/npm/
# Отдельный registry для scope @alfalab/* — см. комментарий в dev-stage выше.
# Дефолт = тот же virtual репо, что и primary (включает alfalab-internal пакеты).
ARG NPM_REGISTRY_ALFALAB_URL=https://binary.alfabank.ru/artifactory/api/npm/npm/
ARG ALPINE_MIRROR=dl-cdn.alpinelinux.org
WORKDIR /app
RUN sed -i "s|dl-cdn.alpinelinux.org|${ALPINE_MIRROR}|g" /etc/apk/repositories
COPY package.json package-lock.json* ./
# scripts/ нужен ДО `npm install`: npm-хук `postinstall` (см. package.json)
# запускает `scripts/patch-alfalab-esm.mjs` — патчер пакетов
# `@alfalab/core-components-*`, без которого Vite 8/Rolldown не резолвит
# подпуть `/esm` в Docker-build (см. doc 134).
COPY scripts/ scripts/
# ⚠️ Используем `npm install` вместо `npm ci`, и УДАЛЯЕМ package-lock.json перед этим.
# Причина: npm ci идёт строго по `resolved`-URL'ам из lockfile. Lockfile сгенерирован
# локально против registry.npmjs.org. При смене registry на корп. virtual репо
# `resolved`-ссылки в lockfile НЕ переписываются автоматически, npm ci идёт за
# конкретными tarball'ами на старые URL'ы → 404 / timeout.
# `npm install` без lockfile строит дерево заново через корп. registry,
# использует resolved-ссылки от него.
# Минус: сборка не строго детерминирована по lockfile (хорошо бы коммитнуть
# свежий lockfile, сгенерированный против корп. registry — отдельная задача).
#
# --legacy-peer-deps — react-popper@2.3.0 объявляет peer-dep react@^16 || ^17 || ^18,
# у нас react@19. Без флага npm пытается дополнительно резолвить react@16-18.
# --no-audit/--no-fund — отключают аудит-вызовы к npmjs.com (лишние сетевые
# запросы в закрытом контуре, к сборке отношения не имеют).
RUN rm -f package-lock.json && \
    npm config set registry "${NPM_REGISTRY_URL}" && \
    if [ -n "${NPM_REGISTRY_ALFALAB_URL}" ]; then \
        npm config set @alfalab:registry "${NPM_REGISTRY_ALFALAB_URL}"; \
    fi && \
    npm install --legacy-peer-deps --no-audit --no-fund
COPY . .
# `npm run build` = tsc -b && vite build → /app/dist
RUN npm run build

# ────────────── Prod stage (nginx раздаёт собранный bundle) ──────────────
FROM ${NGINX_IMAGE} AS prod
# ────────────── Security upgrade: уязвимые системные пакеты ──────────────
# Закрывает CVE из appsec_v4 (см. doc 137 v1.4 — двух-зеркальный fallback).
# Логика идентична API/Dockerfile runtime-stage:
#   1) ALPINE_REPOSITORIES_URL (корп. зеркало Альфы) → apk update+upgrade.
#   2) Fallback на штатный dl-cdn.alpinelinux.org с версия-specific path
#      (берётся из /etc/alpine-release).
#   3) Громкий баннер если оба зеркала упали (эталон не падает).
#   4) Sanity-check `apk info -v` — финальные версии в лог сборки.
# Подробности — в API/Dockerfile и doc 137 v1.4.
ARG ALPINE_REPOSITORIES_URL=""
RUN set -e; \
    REPO_URL="${ALPINE_REPOSITORIES_URL:-}"; \
    if [ -n "$REPO_URL" ]; then \
        echo "${REPO_URL}/main"      >  /etc/apk/repositories; \
        echo "${REPO_URL}/community" >> /etc/apk/repositories; \
        echo "INFO: /etc/apk/repositories переписан на ${REPO_URL}"; \
    fi; \
    APK_PKGS="libcrypto3 libssl3 musl musl-utils zlib"; \
    if apk update 2>&1 && apk upgrade --no-cache --available $APK_PKGS 2>&1; then \
        echo "OK: appsec_v4 — Alpine system packages upgraded via primary mirror"; \
    else \
        echo "WARN: primary apk upgrade failed — попытка через штатный dl-cdn.alpinelinux.org"; \
        ALPINE_VER=$(cut -d. -f1,2 /etc/alpine-release 2>/dev/null || echo "3.20"); \
        echo "http://dl-cdn.alpinelinux.org/alpine/v${ALPINE_VER}/main"      >  /etc/apk/repositories; \
        echo "http://dl-cdn.alpinelinux.org/alpine/v${ALPINE_VER}/community" >> /etc/apk/repositories; \
        if apk update 2>&1 && apk upgrade --no-cache --available $APK_PKGS 2>&1; then \
            echo "OK: appsec_v4 — Alpine system packages upgraded via dl-cdn fallback"; \
        else \
            echo ""; \
            echo "############################################################"; \
            echo "# WARNING: apk upgrade FAILED — оба Alpine-зеркала упали.   #"; \
            echo "# Уязвимые системные пакеты ОСТАЮТСЯ в финальном образе:    #"; \
            echo "#   libcrypto3 libssl3 musl musl-utils zlib                 #"; \
            echo "# AppSec_v4 будет показывать те же CVE до устранения.       #"; \
            echo "#                                                          #"; \
            echo "# DevOps action — проверить:                                #"; \
            echo "#  1. Outbound к binary.alfabank.ru/artifactory/...         #"; \
            echo "#  2. Outbound к dl-cdn.alpinelinux.org (если разрешён)     #"; \
            echo "#  3. ALPINE_REPOSITORIES_URL в jenkinsConfiguration.json  #"; \
            echo "# См. doc_project/137 v1.4.                                 #"; \
            echo "############################################################"; \
            echo ""; \
        fi; \
    fi; \
    echo "── Финальные версии Alpine-пакетов (для трассировки AppSec) ──"; \
    apk info -v $APK_PKGS 2>&1 || true; \
    echo "── Verify: libcrypto3 >= 3.5.7-r0 (CVE-2026-45447 PKCS#7 use-after-free, см. doc 137 v1.5) ──"; \
    CRYPTO_VER=$(apk info -v libcrypto3 2>/dev/null | head -1 | sed -E 's/^libcrypto3-//'); \
    MIN_VER="3.5.7-r0"; \
    LOWER=$(printf '%s\n%s\n' "$CRYPTO_VER" "$MIN_VER" | sort -V | head -1); \
    if [ "$LOWER" = "$MIN_VER" ]; then \
        echo "OK: libcrypto3=$CRYPTO_VER >= $MIN_VER — CVE-2026-45447 закрыт"; \
    else \
        echo "############################################################"; \
        echo "# WARNING: libcrypto3=$CRYPTO_VER < $MIN_VER"; \
        echo "# CVE-2026-45447 (PKCS#7 use-after-free, потенц. RCE)       #"; \
        echo "# ОСТАЁТСЯ в образе для libcrypto3 + libssl3.               #"; \
        echo "# Apk upgrade сработал, но Alpine репо не содержит >=3.5.7. #"; \
        echo "# DevOps action — Alpine-ветка корп. mirror устарела:       #"; \
        echo "#  /etc/alpine-release: $(cat /etc/alpine-release)"; \
        echo "#  Перепривязать latest-stable или поднять base-образ.       #"; \
        echo "# См. doc_project/137 v1.5.                                 #"; \
        echo "############################################################"; \
    fi
# Свой конфиг (SPA-fallback + security headers + кэширование). Перезаписывает
# дефолтный server-блок из nginx:alpine — там listen 80, у нас listen 8080.
COPY nginx.conf /etc/nginx/conf.d/default.conf
# Собранный bundle из build-stage.
COPY --from=build /app/dist /usr/share/nginx/html
# Non-root: nginx:alpine содержит пользователя `nginx` (uid 101) — переключаемся на него.
# /var/cache/nginx, /var/run, /etc/nginx — уже writable для группы nginx в base-образе.
USER nginx
EXPOSE 8080
# Стандартный entrypoint nginx-образа сам запустит nginx; CMD оставляем дефолтным.
