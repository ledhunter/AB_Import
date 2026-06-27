# ════════════════════════════════════════════════════════════════════
# Параметризация для корп. контура: base-образы, npm registry и Alpine mirror
# через build-args. Дефолты — публичные хосты, локальная сборка работает.
# См. doc_project/129-build-time-config-for-corp-network.md.
#
# Stage'ы:
#   • dev   — Vite dev-server (горячий reload). Используется docker-compose.
#   • build — компиляция бандла `npm run build` → /app/dist (промежуточный).
#   • prod  — корп. base-образ `alfa-building-ui-base` (dotnet-сервер статики,
#            non-root, слушает 8080, SPA-fallback из коробки). Используется k8s.
# ════════════════════════════════════════════════════════════════════
# Дефолты — корп. зеркало Альфы (эталонный путь, см. doc_project/132).
# Для локальной сборки docker-compose передаёт NODE_IMAGE из .env с дефолтом
# `node:20-alpine` (см. docker-compose.yml секция 7 / .env.example).
#
# ⚠️ `alfa-building-ui-base` — корп. base-образ Альфы для UI-сервисов
# (см. эталон `visary-cloud/workflow/ui` и doc_project/132). Внутри —
# dotnet-сервер для раздачи статики из /app/wwwroot, работает от non-root
# пользователя без CAP_NET_BIND_SERVICE (порт 8080). За CVE-цикл базы
# отвечает Альфа — apk-upgrade блока для nginx-Alpine здесь больше нет.
ARG NODE_IMAGE=docker-hub.binary.alfabank.ru/library/node:20-alpine
ARG UI_BASE_IMAGE=alfa-building-docker-releases.binary.alfabank.ru/alfa-building-ui-base:v0.0.8

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

# ─── Reverse-proxy base-path (см. doc 147) ───
# Передаются из docker-compose / Jenkins build-arg → ENV → читаются Vite через `loadEnv`.
# Дефолты пустые, чтобы локальная сборка работала как раньше.
#   VITE_BASE_URL    — URL-префикс, где будет опубликована SPA (Vite `base` для ассетов).
#                      Пример: `/api/ab-fm-import-web/` (заметь trailing slash для Vite).
#   VITE_API_PREFIX  — URL-префикс backend-API (для apiUrl.ts). Без trailing slash.
#                      Пример: `/api/ab-fm-import`.
ARG VITE_BASE_URL=/
ARG VITE_API_PREFIX=
ENV VITE_BASE_URL=$VITE_BASE_URL
ENV VITE_API_PREFIX=$VITE_API_PREFIX
RUN echo "[Dockerfile build] VITE_BASE_URL=${VITE_BASE_URL} VITE_API_PREFIX=${VITE_API_PREFIX}"

# `npm run build` = tsc -b && vite build → /app/dist
RUN npm run build

# ────────────── Prod stage (корп. alfa-building-ui-base, dotnet-сервер статики) ──────────────
# Эталон Альфы для UI-сервисов (см. `visary-cloud/workflow/ui` и doc 132):
# base-образ внутри содержит dotnet-сервер для раздачи статики, non-root user,
# listen 8080, SPA-fallback. CVE-цикл базы — на стороне Альфы.
#
# Раньше здесь был nginx:alpine + ~100 строк apk-upgrade для libcrypto3/libssl3
# и edge-fallback (см. doc 137 v1.6 / v1.7). Они больше не нужны — alfa-building-
# ui-base поставляется уже обновлённым, а nginx требовал бы CAP_NET_BIND_SERVICE
# при listen<1024 и доп. писать-маунты для /var/cache/nginx, /var/run, что
# несовместимо с k8s securityContext platformPolicy Альфы (runAsNonRoot,
# readOnlyRootFilesystem, drop ALL capabilities).
FROM ${UI_BASE_IMAGE} AS prod
# Собранный SPA-bundle кладём туда, где base-образ его ожидает.
# `vite build` помещает `index.html` в корень `dist/`, после COPY получим
# `/app/wwwroot/index.html` — это и есть entry-point SPA для dotnet-сервера.
COPY --from=build /app/dist /app/wwwroot
EXPOSE 8080
# ENTRYPOINT/CMD не переопределяем — alfa-building-ui-base сам запускает
# dotnet-сервер при старте контейнера.
