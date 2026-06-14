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
ARG NODE_IMAGE=node:20-alpine
ARG NGINX_IMAGE=nginx:1.27-alpine

# ────────────── Dev stage (docker-compose, hot reload) ──────────────
FROM ${NODE_IMAGE} AS dev
# ARG до FROM — global; нужно переобъявить после FROM, чтобы был виден в RUN.
ARG NPM_REGISTRY_URL=https://registry.npmjs.org
ARG ALPINE_MIRROR=dl-cdn.alpinelinux.org
WORKDIR /app
RUN sed -i "s|dl-cdn.alpinelinux.org|${ALPINE_MIRROR}|g" /etc/apk/repositories
COPY package.json package-lock.json* ./
RUN npm config set registry "${NPM_REGISTRY_URL}" && npm install
COPY . .
EXPOSE 5173
ENV HOST=0.0.0.0
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0"]

# ────────────── Build stage (компилирует bundle для prod) ──────────────
FROM ${NODE_IMAGE} AS build
ARG NPM_REGISTRY_URL=https://registry.npmjs.org
ARG ALPINE_MIRROR=dl-cdn.alpinelinux.org
WORKDIR /app
RUN sed -i "s|dl-cdn.alpinelinux.org|${ALPINE_MIRROR}|g" /etc/apk/repositories
COPY package.json package-lock.json* ./
# npm ci — детерминированная установка из package-lock.json (быстрее и воспроизводимее install).
RUN npm config set registry "${NPM_REGISTRY_URL}" && npm ci
COPY . .
# `npm run build` = tsc -b && vite build → /app/dist
RUN npm run build

# ────────────── Prod stage (nginx раздаёт собранный bundle) ──────────────
FROM ${NGINX_IMAGE} AS prod
ARG ALPINE_MIRROR=dl-cdn.alpinelinux.org
RUN sed -i "s|dl-cdn.alpinelinux.org|${ALPINE_MIRROR}|g" /etc/apk/repositories
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
