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
# Закрывает CVE из appsec_v4 (см. doc 137 v1.3):
#   libcrypto3, libssl3 — 8× OpenSSL (High)
#   musl, musl-utils    — CVE-2026-40200 (qsort stack corruption)
#   zlib                — CVE-2026-22184 (untgz buffer overflow)
#
# ALPINE_REPOSITORIES_URL — полный base URL Alpine-репо без суффикса /main|/community.
# Корп. зеркало: https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable.
# Пустой дефолт → не трогаем repositories (используется штатный из base-образа,
# compose-локаль с outbound на dl-cdn). Подробности — в API/Dockerfile и doc 137.
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
