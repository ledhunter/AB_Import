# ☸️ Запуск KiloImportService в Kubernetes — пошаговая инструкция

## 📋 Описание

Практическое руководство, как развернуть сервис в кластере Kubernetes
с нуля. Рассчитано на **системного аналитика**, который раньше работал
только с `docker compose`. Каждый шаг — что делаем, зачем, какая
команда, как проверить, что получилось.

Сервис состоит из **четырёх компонентов** (см. [docker-compose.yml](../docker-compose.yml)):

1. **backend** — .NET 10 Web API (импорт, бизнес-логика, обращение к Visary).
2. **frontend** — React + Vite (UI, который видит пользователь).
3. **service-db** — PostgreSQL, в котором сервис хранит свои сессии импорта,
   ошибки, журнал действий.
4. **visary-db** — PostgreSQL со «срезом» структуры Visary, нужен для офлайн-моков.
   На prod обычно **не используется**, потому что мы стучимся в реальный Visary
   по HTTP.

Парный с:
- [122-environment-config.md](./122-environment-config.md) — какие env-переменные сервис ждёт;
- [123-environment-switching-guide.md](./123-environment-switching-guide.md) — то же для docker-compose;
- [129-build-time-config-for-corp-network.md](./129-build-time-config-for-corp-network.md) — сборка образов для корп. контура.

---

## 🗺️ Часть 1. Маппинг docker-compose → Kubernetes

Чтобы было проще ориентироваться, вот таблица соответствий. Если вы знаете
docker-compose — узнаете k8s по аналогии.

| Docker Compose | Kubernetes | Зачем |
|----------------|------------|-------|
| `services:` блок | **Deployment** (для stateless: backend, frontend) или **StatefulSet** (для БД) | Что запускаем и сколько копий (replicas) |
| `image:` | поле `spec.containers[].image` в Deployment | Какой Docker-образ использовать |
| `environment:` | **ConfigMap** (несекретное) + **Secret** (секретное), подключаются через `envFrom` | Конфигурация без пересборки образа |
| `ports:` | **Service** (внутренний DNS) + **Ingress** (внешний URL) | Как другие Pod'ы и пользователи достучатся |
| `depends_on:` | `initContainers` или readiness/liveness-пробы | Дождаться готовности зависимости |
| `volumes:` | **PersistentVolumeClaim** | Сохранить данные между перезапусками |
| `networks:` | **Namespace** + сетевые политики | Изоляция и связь между компонентами |
| `healthcheck:` | `livenessProbe` / `readinessProbe` | Перезапуск битого Pod'а / снять трафик |
| `restart: unless-stopped` | k8s сам перезапускает Pod при падении | Не нужно настраивать — поведение по умолчанию |

**Главная идея k8s, которая отличает его от compose**:

> Сервисы общаются не через `localhost:port`, а через **DNS-имя Service'а**
> внутри Namespace. Например, backend подключается к БД по адресу
> `postgres-service.kilo-import.svc.cluster.local:5432` (или просто
> `postgres-service:5432`, если они в одном Namespace).

---

## ✅ Часть 2. Что должно быть готово ДО старта

Это — список того, что должен подтвердить **DevOps/админ кластера**.
Если хотя бы один пункт не готов — продолжать нет смысла.

| # | Требуется | Как проверить |
|---|-----------|---------------|
| 1 | **Доступ к кластеру**: установлен `kubectl`, настроен `kubeconfig` | `kubectl cluster-info` — показывает URL мастера |
| 2 | **Namespace для сервиса** (или права на его создание) | `kubectl auth can-i create namespace` |
| 3 | **Container registry**, доступный из кластера, куда залиты собранные образы | `docker pull <registry>/kilo-import/backend:latest` работает с машины, имеющей доступ к кластеру |
| 4 | **PostgreSQL** — либо managed-инстанс (рекомендуется для prod), либо разрешение поднять StatefulSet в кластере | host:port + пара логин/пароль |
| 5 | **Ingress Controller** (nginx-ingress, traefik) и **TLS-сертификат** для публичного URL | `kubectl get ingressclass` показывает хотя бы один класс |
| 6 | **Доступ кластера к Visary API**: `https://isup-alfa.k8s.npc.ba` (или соответствующий контуру) | Network policy: разрешён egress на этот хост из Namespace |
| 7 | **Доступ к Visary IdP** для OIDC: `https://id-isup-alfa.k8s.npc.ba` | То же — проверить network policy |
| 8 | **Способ хранения секретов**: либо k8s Secret вручную, либо интеграция с Vault (sealed-secrets / external-secrets-operator) | `kubectl get secret -n <ns>` или `kubectl get externalsecrets` |
| 9 | **Корп-CA в образе** (если в сети TLS-инспекция) — закладывается на этапе сборки, см. [doc 129](./129-build-time-config-for-corp-network.md) | `kubectl exec ... -- update-ca-certificates --fresh` отрабатывает без ошибок |
| 10 | **Бизнес-данные**: реальные значения хостов Visary, JWT Audience, refresh-токен, креды БД — получены у админа среды | По шаблону [.env.prod.example](../.env.prod.example) |

---

## 🏗️ Часть 3. Архитектура развёртывания

```
┌─────────────────────────────────────────────────────────────────────────┐
│ Kubernetes Cluster                                                      │
│                                                                         │
│  ┌──────────────────────────────────────────────────────────────────┐   │
│  │ Namespace: kilo-import                                           │   │
│  │                                                                  │   │
│  │  Ingress ──────► Service (frontend) ──────► Deployment (frontend)│   │
│  │  https://import.alfa.bank                       (react + vite)   │   │
│  │           │                                                      │   │
│  │           └─►   Service (backend)  ──────► Deployment (backend)  │   │
│  │   /api/* + /hubs/*                              (.NET 10 API)    │   │
│  │                                                       │          │   │
│  │                                                       ▼          │   │
│  │                                          Service (postgres) ─────│───┼──► (managed) RDS / Azure DB
│  │                                          (если managed)          │   │
│  │                                                                  │   │
│  │                                          StatefulSet (postgres)  │   │
│  │                                          (если in-cluster)       │   │
│  │                                                                  │   │
│  └──────────────────────────────────────────────────────────────────┘   │
│                                                                         │
└──────────────────────────────┬──────────────────────────────────────────┘
                               │
                               ▼ (egress)
                    https://isup-alfa.k8s.npc.ba (Visary API)
                    https://id-isup-alfa.k8s.npc.ba (Visary IdP)
```

**Поток запроса пользователя**:
1. Браузер → Ingress (`https://import.alfa.bank`).
2. Ingress по path-правилам разводит:
   - `/api/*`, `/hubs/*` → Service `backend` → один из Pod'ов backend.
   - `/*` → Service `frontend` → один из Pod'ов frontend.
3. Backend Pod внутри:
   - читает/пишет в PostgreSQL через Service `postgres-service`;
   - ходит в Visary API (внешний хост) для проверки данных;
   - обновляет токен через Visary IdP (refresh_token-flow).
4. Frontend Pod отдаёт собранный bundle (статика) — никуда сам не ходит.

---

## 📦 Часть 4. Подготовка образов

Перед раскаткой нужно собрать два Docker-образа и положить их в
container registry, доступный кластеру.

### 4.1. Собрать локально (или в CI)

```powershell
# Из корня репозитория. .env должен содержать корп-значения, см. doc 129.
docker compose build backend frontend
```

Это создаст образы с именами `kilo-import-backend` и `kilo-import-frontend`
(см. `container_name` в docker-compose.yml).

### 4.2. Перетегировать под адрес корп. registry

```powershell
docker tag kilo-import-backend  corp.registry.alfa/kilo-import/backend:v1.0.0
docker tag kilo-import-frontend corp.registry.alfa/kilo-import/frontend:v1.0.0
```

### 4.3. Запушить

```powershell
docker login corp.registry.alfa
docker push corp.registry.alfa/kilo-import/backend:v1.0.0
docker push corp.registry.alfa/kilo-import/frontend:v1.0.0
```

### ⚠️ Важно про теги
- **Не используйте `:latest`** в проде — k8s не поймёт, что образ обновился.
- Тег — это **версия релиза**, например `v1.0.0`, `2026-06-09-rc1`,
  или git-SHA-первых-7-символов.
- Один и тот же тег **никогда** не перезаписывайте — это сломает откат.

---

## 🗄️ Часть 5. База данных (managed PostgreSQL)

Архитектурное решение: **БД managed, вне кластера** (RDS / Azure DB / внутрибанковский PG-as-a-service). Кластер только подключается. В Kubernetes никаких StatefulSet'ов мы не поднимаем.

### 5.1. Что должен сделать DBA до старта сервиса

Это **pre-condition** — без этих шагов backend не поднимется.

#### Создать две базы и одного пользователя
```sql
-- Под root-аккаунтом managed-PG.
CREATE DATABASE import_service_db ENCODING 'UTF8' LC_COLLATE 'en_US.UTF-8' LC_CTYPE 'en_US.UTF-8' TEMPLATE template0;
CREATE DATABASE visary_webapi_db  ENCODING 'UTF8' LC_COLLATE 'en_US.UTF-8' LC_CTYPE 'en_US.UTF-8' TEMPLATE template0;

CREATE USER kilo_import WITH PASSWORD '<СГЕНЕРИРОВАТЬ-ПАРОЛЬ>';

-- Полные права на ОБЕ базы — backend сам создаст схему `import` и таблицы.
GRANT ALL PRIVILEGES ON DATABASE import_service_db TO kilo_import;
GRANT ALL PRIVILEGES ON DATABASE visary_webapi_db  TO kilo_import;
```

> Один пользователь на обе БД — потому что backend читает обе из одного процесса.
> Если политика безопасности требует разделения — можно завести двух пользователей,
> connection strings для каждой БД задаются отдельно (см. Часть 6).

#### Прогнать init-скрипты для `visary_webapi_db`

Эта БД управляется **DB-first** (миграциями EF Core НЕ управляется, см. [VisaryDbContext](../KiloImportService.Api/Data/Visary/VisaryDbContext.cs)). Схема создаётся тремя SQL-файлами из репо в указанном порядке:

```powershell
psql -h <host> -U kilo_import -d visary_webapi_db -f db/visary/init/01-schema.sql        # 66 CREATE TABLE
psql -h <host> -U kilo_import -d visary_webapi_db -f db/visary/init/02-missing-roots.sql # ConstructionProject/Site + 30 FK
psql -h <host> -U kilo_import -d visary_webapi_db -f db/visary/init/03-seed-data.sql     # справочники
```

Файлы лежат в [db/visary/init/](../db/visary/init/) репозитория.

#### Что НЕ нужно делать для `import_service_db`

Ничего. Backend сам прогонит EF Core миграции при старте — см. [Program.cs](../KiloImportService.Api/Program.cs):
```csharp
if (!EF.IsDesignTime)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ImportServiceDbContext>();
    await db.Database.MigrateAsync();
}
```
На старте Pod'а появятся таблицы в схеме `import`: `import_sessions`, `import_session_stages`, `staged_rows`, `import_errors`, `import_file_snapshots`, `cached_projects`, `room_apply_snapshots`. Отдельный Job для миграций **не нужен** — это упрощает rolling-update и blue/green.

### 5.2. Проверка после DBA

С машины, имеющей сетевой доступ к managed-PG:
```powershell
psql -h <host> -U kilo_import -d import_service_db -c "SELECT 1;"
psql -h <host> -U kilo_import -d visary_webapi_db  -c 'SELECT COUNT(*) FROM "Data"."Room";'
# Второй запрос должен вернуть 0 — таблица существует, просто пустая.
```

Если оба запроса прошли — БД готова к подключению backend'а.

### 5.3. Что произойдёт после первого старта backend

В логах Pod'а ([kubectl logs -n kilo-import deploy/backend](#часть-9-smoke-проверка)) будут строки:
```
[Information] Applying ImportServiceDb migrations…
[Information] Migration '20260430213808_RemoveFileSha256Constraint' applied
[Information] Migration '20260512095902_AddSheetToStagedRowAndError' applied
... (всего 5 миграций — см. KiloImportService.Api/Migrations/)
[Information] Starting KiloImportService.Api on http://+:5000
```
После первого Pod'а в БД появится таблица `import.__ef_migrations_history` со списком применённых миграций. Последующие Pod'ы увидят, что миграции уже применены, и не повторят их.

---

## 🔐 Часть 6. Конфигурация: ConfigMap + Secret

K8s разделяет конфигурацию на два типа:

- **ConfigMap** — несекретное (хосты Visary, audience, окружение). Видно всем,
  кто имеет права `get configmap`.
- **Secret** — секретное (пароли БД, refresh-токен). Хранится зашифрованным,
  доступ ограничивается RBAC.

### 6.1. Secret с кредами БД и токенами

```yaml
# kilo-import-secrets.yaml
apiVersion: v1
kind: Secret
metadata:
  name: kilo-import-db-secret
  namespace: kilo-import
type: Opaque
stringData:
  # Connection strings под пользователя, созданного DBA на шаге 5.1.
  # Backend читает обе строки из env, маппинг `__` → `:` стандартный для .NET configuration.
  ConnectionStrings__ServiceDb: "Host=pg.corp.alfa;Port=5432;Database=import_service_db;Username=kilo_import;Password=<ПАРОЛЬ>"
  ConnectionStrings__VisaryDb:  "Host=pg.corp.alfa;Port=5432;Database=visary_webapi_db;Username=kilo_import;Password=<ПАРОЛЬ>"
---
apiVersion: v1
kind: Secret
metadata:
  name: kilo-import-visary-secret
  namespace: kilo-import
type: Opaque
stringData:
  # Refresh-токен для OIDC (см. doc 107). Получается из Visary UI один раз.
  VISARY_AUTH_REFRESH_TOKEN: "<ВСТАВИТЬ-ИЗ-VAULT>"
  # Опционально — legacy Bearer (если OIDC ещё не настроен в среде):
  Visary__BearerToken: ""
```

⚠️ **Никогда не коммитьте этот файл с реальными значениями в git.**
Варианты безопасного управления:
- **sealed-secrets**: `kubeseal` шифрует Secret публичным ключом кластера,
  результат коммитится в git, кластер расшифрует.
- **external-secrets-operator**: Secret тянется из Vault/AWS Secrets Manager
  по ссылке.

### 6.2. ConfigMap с несекретной конфигурацией

```yaml
# kilo-import-config.yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: kilo-import-config
  namespace: kilo-import
data:
  # ── Окружение ──
  ASPNETCORE_ENVIRONMENT: Production
  ASPNETCORE_URLS: "http://+:5000"

  # ── Visary API (см. doc 122) ──
  # Контур → URL:
  #   test    : https://isup-alfa-test.k8s.npc.ba
  #   preprod : https://pre-isup-alfa.k8s.npc.ba
  #   prod    : https://isup-alfa.k8s.npc.ba
  Visary__BaseUrl: "https://isup-alfa.k8s.npc.ba"

  # ── OIDC refresh-flow (см. doc 107) ──
  Visary__Auth__TokenEndpoint: "https://id-isup-alfa.k8s.npc.ba/connect/token"
  Visary__Auth__ClientId: "visary-ui"

  # ── JWT-валидация входящих запросов (см. doc 111) ──
  Auth__Authority: "https://id-isup-alfa.k8s.npc.ba"
  Auth__Audience: "kilo-import-api"
  Auth__RequireHttpsMetadata: "true"

  # ── CORS allowlist: публичный URL UI ──
  Cors__AllowedOrigins__0: "https://import.alfa.bank"

  # ── Хранилище загружаемых файлов ──
  ImportStorage__Path: "/var/lib/import-files"
```

---

## 🚀 Часть 7. Манифесты Deployment + Service + Ingress

### 7.1. Backend Deployment + Service

```yaml
# backend.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: backend
  namespace: kilo-import
  labels:
    app: backend
spec:
  replicas: 2                            # 2 копии для отказоустойчивости
  selector:
    matchLabels:
      app: backend
  strategy:
    type: RollingUpdate                  # обновление без простоя
    rollingUpdate:
      maxUnavailable: 0                  # ни один Pod не уходит до готовности нового
      maxSurge: 1
  template:
    metadata:
      labels:
        app: backend
    spec:
      containers:
        - name: backend
          image: corp.registry.alfa/kilo-import/backend:v1.0.0
          ports:
            - containerPort: 5000
              name: http
          envFrom:
            - configMapRef:
                name: kilo-import-config
            - secretRef:
                name: kilo-import-db-secret
            - secretRef:
                name: kilo-import-visary-secret
          volumeMounts:
            - name: import-files
              mountPath: /var/lib/import-files
          readinessProbe:                # «готов принять трафик»
            httpGet:
              path: /health
              port: 5000
            initialDelaySeconds: 10
            periodSeconds: 5
            failureThreshold: 3
          livenessProbe:                 # «жив? если нет — перезапусти»
            httpGet:
              path: /health
              port: 5000
            initialDelaySeconds: 30
            periodSeconds: 10
            failureThreshold: 5
          resources:
            requests:                    # гарантированно выделяется
              memory: "256Mi"
              cpu: "200m"
            limits:                      # потолок
              memory: "1Gi"
              cpu: "1000m"
      volumes:
        - name: import-files
          persistentVolumeClaim:
            claimName: import-files-pvc
---
apiVersion: v1
kind: Service
metadata:
  name: backend
  namespace: kilo-import
spec:
  selector:
    app: backend                         # выбирает Pod'ы с label app=backend
  ports:
    - port: 80                           # порт Service'а (внутренний)
      targetPort: 5000                   # порт в контейнере
      name: http
  type: ClusterIP                        # внутренний, не торчит наружу
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: import-files-pvc
  namespace: kilo-import
spec:
  accessModes: ["ReadWriteMany"]         # ⚠️ если 2 реплики — нужно ReadWriteMany;
                                         #     требует поддерживающего storageClass
                                         #     (NFS/Azure Files/EFS). Если такого нет —
                                         #     replicas: 1 + ReadWriteOnce.
  resources:
    requests:
      storage: 20Gi
```

### 7.2. Frontend Deployment + Service

⚠️ **Образ frontend для k8s собирается из `prod`-stage Dockerfile** (nginx, статика),
не из `dev`-stage. Сборка:
```powershell
docker build -t corp.registry.alfa/kilo-import/frontend:v1.0.0 `
  --target prod `
  -f KiloImportService.Web/Dockerfile `
  KiloImportService.Web
```

В prod-stage Vite-bundle (`/app/dist`) собран на этапе билда и зашит в образ. Хост
Visary, в который ходит браузер, — **тот же, что у Ingress** (same-origin через
backend proxy `/api/visary/*`); никаких VITE_* env'ов на runtime nginx-stage'у
не нужно. Чтобы переключить контур — пересобрать образ с другим `VISARY_BASE_URL`
в `.env` (см. [doc 129](./129-build-time-config-for-corp-network.md)) и задеплоить.

```yaml
# frontend.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: frontend
  namespace: kilo-import
spec:
  replicas: 2
  selector:
    matchLabels:
      app: frontend
  template:
    metadata:
      labels:
        app: frontend
    spec:
      containers:
        - name: frontend
          image: corp.registry.alfa/kilo-import/frontend:v1.0.0
          ports:
            - containerPort: 8080
          # SecurityContext: non-root, read-only-FS, no privilege escalation.
          # nginx-alpine штатно работает от пользователя `nginx` (uid 101).
          securityContext:
            runAsNonRoot: true
            runAsUser: 101
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities:
              drop: ["ALL"]
          volumeMounts:
            # nginx пишет в /var/cache/nginx и /var/run при readOnlyRootFilesystem
            - name: cache
              mountPath: /var/cache/nginx
            - name: run
              mountPath: /var/run
          readinessProbe:
            httpGet:
              path: /
              port: 8080
            initialDelaySeconds: 3
            periodSeconds: 5
          livenessProbe:
            httpGet:
              path: /
              port: 8080
            initialDelaySeconds: 10
            periodSeconds: 10
          resources:
            requests:
              memory: "32Mi"           # nginx + статика — много не надо
              cpu: "50m"
            limits:
              memory: "128Mi"
              cpu: "200m"
      volumes:
        - name: cache
          emptyDir: {}
        - name: run
          emptyDir: {}
---
apiVersion: v1
kind: Service
metadata:
  name: frontend
  namespace: kilo-import
spec:
  selector:
    app: frontend
  ports:
    - port: 80
      targetPort: 8080
  type: ClusterIP
```

### 7.3. Ingress — единая точка входа из интернета

```yaml
# ingress.yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: kilo-import
  namespace: kilo-import
  annotations:
    # Размер тела запроса — нам нужно грузить Excel-файлы.
    nginx.ingress.kubernetes.io/proxy-body-size: "50m"
    # SignalR использует WebSocket — увеличим таймауты.
    nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"
    nginx.ingress.kubernetes.io/proxy-send-timeout: "3600"
spec:
  ingressClassName: nginx                # имя ingress-class (`kubectl get ingressclass`)
  tls:
    - hosts:
        - import.alfa.bank
      secretName: kilo-import-tls        # сертификат — отдельный Secret
  rules:
    - host: import.alfa.bank
      http:
        paths:
          # ⚠️ Порядок path важен: специфичные ДО общих.
          - path: /api
            pathType: Prefix
            backend:
              service:
                name: backend
                port:
                  number: 80
          - path: /hubs                  # SignalR
            pathType: Prefix
            backend:
              service:
                name: backend
                port:
                  number: 80
          - path: /swagger               # API-документация (если разрешена в prod)
            pathType: Prefix
            backend:
              service:
                name: backend
                port:
                  number: 80
          - path: /health
            pathType: Prefix
            backend:
              service:
                name: backend
                port:
                  number: 80
          - path: /                      # всё остальное — frontend (статика)
            pathType: Prefix
            backend:
              service:
                name: frontend
                port:
                  number: 80
```

### Stage'ы Dockerfile для frontend

[Web/Dockerfile](../KiloImportService.Web/Dockerfile) содержит три stage'а:

| Stage | Что | Где используется |
|-------|-----|------------------|
| `dev` | Vite dev-server (порт 5173, hot reload) | `docker compose up` (compose явно указывает `target: dev`) |
| `build` | Промежуточный: компилирует bundle через `npm ci && npm run build` → `/app/dist` | Не запускается напрямую |
| `prod` | nginx:1.27-alpine, отдаёт `/app/dist` на порту 8080 | k8s (см. сборку выше) |

Прод-stage конфигурируется через [nginx.conf](../KiloImportService.Web/nginx.conf):
SPA-fallback (все пути → `index.html`), security headers (X-Frame-Options,
X-Content-Type-Options, Referrer-Policy, X-XSS-Protection), кэширование
`/assets/*` на год + `no-cache` на `index.html` (быстрая раскатка новой
версии без stale-cache), `server_tokens off`.

---

## ▶️ Часть 8. Запуск пошагово

Все команды — с машины, на которой настроен `kubectl` для целевого кластера.

### Шаг 8.1. Создать Namespace

```powershell
kubectl create namespace kilo-import
```

Проверка:
```powershell
kubectl get namespace kilo-import
# kilo-import   Active   5s
```

### Шаг 8.2. Применить TLS-сертификат

Если cert-manager — он сгенерирует автоматически по ingress-annotation.
Если вручную:

```powershell
kubectl create secret tls kilo-import-tls `
  --cert=fullchain.pem `
  --key=privkey.pem `
  -n kilo-import
```

### Шаг 8.3. Применить Secret'ы

```powershell
# Сначала отредактировать kilo-import-secrets.yaml — подставить реальные пароли/токены!
kubectl apply -f kilo-import-secrets.yaml
```

Проверка:
```powershell
kubectl get secret -n kilo-import
# kilo-import-db-secret      Opaque   4 keys   10s
# kilo-import-visary-secret  Opaque   2 keys   10s
```

### Шаг 8.4. Применить ConfigMap

```powershell
kubectl apply -f kilo-import-config.yaml
```

### Шаг 8.5. Подтвердить готовность БД

Перед раскаткой backend убедиться, что DBA выполнил пункты Части 5.1:
БД + пользователь + init-скрипты для `visary_webapi_db` прогнаны.
Backend при старте сам создаст таблицы в `import_service_db` через миграции
EF Core — отдельный шаг не нужен.

Быстрая проверка из любого Pod'а кластера, имеющего доступ к managed-PG:
```powershell
# Запустить временный psql-Pod на минуту:
kubectl run psql-check --rm -it --image=postgres:16-alpine --restart=Never -n kilo-import -- `
  psql "host=pg.corp.alfa port=5432 dbname=visary_webapi_db user=kilo_import password=<ПАРОЛЬ>" `
  -c 'SELECT COUNT(*) FROM "Data"."Room";'
# Должно вернуть 0 (таблица существует, пустая).
```

### Шаг 8.6. Backend и Frontend

```powershell
kubectl apply -f backend.yaml
kubectl apply -f frontend.yaml
```

Дождаться, пока все Pod'ы получат статус `Running` + `READY 1/1`:
```powershell
kubectl get pods -n kilo-import -w
# backend-7f9c8b6d4-abc12   1/1   Running   0   30s
# backend-7f9c8b6d4-def34   1/1   Running   0   30s
# frontend-5d4b6c8f9-xyz78  1/1   Running   0   28s
# frontend-5d4b6c8f9-uvw90  1/1   Running   0   28s
# postgres-service-0        1/1   Running   0   2m
```

`Ctrl-C` чтобы выйти из `-w` режима.

### Шаг 8.7. Ingress

```powershell
kubectl apply -f ingress.yaml
kubectl get ingress -n kilo-import
# kilo-import   nginx   import.alfa.bank   <EXTERNAL-IP>   80, 443   30s
```

Когда в колонке `ADDRESS` появится IP — Ingress готов принимать трафик.

---

## ✅ Часть 9. Smoke-проверка

### 9.1. Backend жив

```powershell
kubectl exec -n kilo-import deploy/backend -- curl -fsS http://localhost:5000/health
# {"status":"ok"}
```

Если из контейнера `curl` недоступен — `kubectl port-forward`:
```powershell
kubectl port-forward -n kilo-import svc/backend 5000:80
# в другом терминале:
curl http://localhost:5000/health
```

### 9.2. Backend подключился к БД

```powershell
kubectl logs -n kilo-import deploy/backend --tail=50 | Select-String -Pattern "Database|Postgres|EF"
```
Ищем строки про успешное подключение и **отсутствие** «relation does not exist».

### 9.3. Frontend отдаёт UI

```powershell
kubectl port-forward -n kilo-import svc/frontend 5173:80
# в браузере: http://localhost:5173
```
Должна загрузиться главная страница сервиса.

### 9.4. Полный E2E через Ingress

В браузере открыть `https://import.alfa.bank`. Должно:
- UI загружается;
- список проектов (раскрыть Select) — наполняется (значит backend → Visary работает);
- DevTools → Network — все запросы к `/api/*` идут на `https://import.alfa.bank` (same-origin);
- Заголовок `Authorization: Bearer ...` — присутствует в запросах к `/api/visary/*`.

---

## 🛠️ Часть 10. Troubleshooting

| Симптом | Причина | Что сделать |
|---------|---------|-------------|
| Pod в `CrashLoopBackOff`, в логах `ConnectionString` is empty | Не подключился Secret или ConfigMap | `kubectl describe pod <name>` → раздел Environment. Проверить, что `envFrom.secretRef.name` совпадает с реальным именем Secret. |
| Pod в `CrashLoopBackOff`, в логах `relation "import_sessions" does not exist` | EF-миграции не отработали при старте (нет прав на CREATE schema/table) | Проверить `GRANT ALL PRIVILEGES ON DATABASE import_service_db TO kilo_import` (Часть 5.1). После выдачи прав — `kubectl rollout restart deploy/backend` |
| Pod в `CrashLoopBackOff`, в логах `42P01: relation "Data"."Room" does not exist` | Init-скрипты для visary_webapi_db не прогнаны | Прогнать `db/visary/init/01-03.sql` (Часть 5.1, третий шаг) |
| Backend Pod 0/1 Ready, в логах ничего подозрительного, но `/health` возвращает **401** | `HealthController` не разрешает анонимные запросы — kubelet получает 401 на probe → считает Pod битым | Убедиться, что собран образ из версии репо ≥ doc 130 — там `[AllowAnonymous]` на HealthController. Если нет — пересобрать backend |
| Pod в `CrashLoopBackOff`, в логах `Connection refused 127.0.0.1:5432` | Backend пытается подключиться к localhost вместо Service | В connection string должен быть **DNS-имя Service'а**, не localhost. Пример: `Host=postgres-service` (не `Host=localhost`) |
| Pod в `ImagePullBackOff` | Кластер не может скачать образ из registry | `kubectl describe pod <name>` → секция Events. Чаще всего — нет imagePullSecret или registry недоступен из cluster network |
| Ingress есть, но `https://import.alfa.bank` отдаёт 404 | Path-правила Ingress не покрывают URL | `kubectl describe ingress kilo-import -n kilo-import` → проверить path |
| Ingress отдаёт 502 Bad Gateway | Backend Pod есть, но не в Ready (упал readinessProbe) | `kubectl get pods -n kilo-import` — колонка READY должна быть `1/1`. Если `0/1` — посмотреть `kubectl describe pod` |
| `EnvVar` со значением `$(ANOTHER_VAR)` не раскрывается | Переменная объявлена в `envFrom` (Secret/ConfigMap), а ссылается на неё `env.value` — k8s раскрывает только переменные из того же `env`-списка ВЫШЕ | Переписать значение через явный `env` с прямым literal или достать оба ключа через `valueFrom` |
| Backend стучится не туда (логи показывают cross-origin URL) | CORS-allowlist `Cors__AllowedOrigins__0` неверный | Поправить ConfigMap → `kubectl rollout restart deploy/backend -n kilo-import` |
| SignalR не подключается, в DevTools `WebSocket connection failed` | Ingress не пропускает WebSocket | Добавить annotation `nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"` (см. 7.3) |
| Все Pod'ы поднялись, но Visary вернул 401 | Refresh-токен истёк или невалиден | Получить новый через UI Visary (offline_access scope) → обновить Secret → `kubectl rollout restart deploy/backend` |

### Команды-«спасатели»

```powershell
# Полная диагностика одного Pod'а
kubectl describe pod <pod-name> -n kilo-import

# Логи backend live
kubectl logs -n kilo-import -l app=backend -f --tail=100

# Логи predыдущего упавшего инстанса (если перезапустился)
kubectl logs -n kilo-import <pod-name> --previous

# Зайти внутрь Pod'а
kubectl exec -it -n kilo-import deploy/backend -- /bin/sh

# Проверить, что внутри Pod'а видна БД
kubectl exec -n kilo-import deploy/backend -- nc -zv postgres-service 5432

# Перезапустить Deployment (применит новый Secret/ConfigMap)
kubectl rollout restart deploy/backend -n kilo-import

# Статус rollout'а
kubectl rollout status deploy/backend -n kilo-import

# Откатить на предыдущую версию
kubectl rollout undo deploy/backend -n kilo-import
```

---

## 🔄 Часть 11. Обновление сервиса (новая версия)

### 11.1. Собрать новый образ

```powershell
docker compose build backend
docker tag kilo-import-backend corp.registry.alfa/kilo-import/backend:v1.0.1
docker push corp.registry.alfa/kilo-import/backend:v1.0.1
```

### 11.2. Применить новый тег к Deployment

Вариант 1 — отредактировать `backend.yaml` и `kubectl apply -f`.

Вариант 2 — прямо из CLI:
```powershell
kubectl set image deploy/backend backend=corp.registry.alfa/kilo-import/backend:v1.0.1 -n kilo-import
```

K8s сам делает rolling-update: поднимает Pod новой версии, дожидается, что
он `Ready` (прошёл readinessProbe), потом убивает старый. Простой = 0.

```powershell
kubectl rollout status deploy/backend -n kilo-import
# deployment "backend" successfully rolled out
```

### 11.3. Если новая версия требует миграцию

Backend сам прогоняет миграции при старте Pod'а (см. Часть 5.3). При
rolling-update с `maxUnavailable: 0` k8s сначала поднимает Pod новой версии:
он прогоняет миграции на БД, и только когда `readinessProbe` зелёная — k8s
убивает старый Pod. Простой = 0.

⚠️ **Важно про обратную совместимость миграций**: пока новый Pod уже
прогнал миграцию (например, добавил колонку), а старый Pod ещё работает —
оба должны корректно обрабатывать новую схему. Поэтому миграции
**должны быть backward-compatible** на одну версию (правило «expand →
contract»). Это правило архитектуры миграций, а не правило k8s.

### 11.4. Откат, если что-то пошло не так

```powershell
kubectl rollout undo deploy/backend -n kilo-import
```

⚠️ **Откат миграций руками** — отдельная задача. EF Core не откатывает
автоматически. Если новая миграция несовместима — нужно либо
сначала ревёрстный SQL, либо новая «лечащая» миграция.

---

## 🎯 Часть 12. Финальный чек-лист аналитика

Перед тем как сказать «развёрнуто»:

- [ ] Namespace `kilo-import` существует
- [ ] Secret'ы созданы и содержат **реальные** значения (не плейсхолдеры)
- [ ] ConfigMap содержит верный `Visary__BaseUrl` для целевого контура
- [ ] PostgreSQL отвечает (или managed-инстанс, или StatefulSet `Running`)
- [ ] Миграции EF Core применены (таблицы существуют в БД)
- [ ] Backend Pod'ы `READY 1/1`, `livenessProbe` зелёная
- [ ] Frontend Pod'ы `READY 1/1`
- [ ] Ingress получил `ADDRESS` (внешний IP)
- [ ] TLS-сертификат на месте, `https://...` без warning'а
- [ ] `/health` отвечает 200 OK через Ingress
- [ ] UI открывается, список проектов из Visary подгружается
- [ ] SignalR-подключение установилось (DevTools → Network → WS → status 101)
- [ ] Сделать тестовый импорт небольшого файла — прошёл «зелёным»
- [ ] Логи бекенда без ERROR-строк за последние 5 минут

---

## 📚 Связанные документы

- [122-environment-config.md](./122-environment-config.md) — какие env-переменные сервис ждёт, deny-by-default
- [123-environment-switching-guide.md](./123-environment-switching-guide.md) — то же для docker-compose (preprod/prod через `.env`)
- [129-build-time-config-for-corp-network.md](./129-build-time-config-for-corp-network.md) — сборка образов для корп. контура
- [12-ef-core-migrations.md](./12-ef-core-migrations.md) — миграции EF Core, `EF.IsDesignTime` guard
- [19-net10-deployment-gotchas.md](./19-net10-deployment-gotchas.md) — грабли первого деплоя .NET 10 + Alpine
- [107-visary-token-provider.md](./107-visary-token-provider.md) — OIDC refresh-flow, откуда брать refresh_token
- [111-incoming-jwt-auth.md](./111-incoming-jwt-auth.md) — валидация входящих JWT
- [121-security-fixes-appsec-v1.md](./121-security-fixes-appsec-v1.md) — почему `${VAR:?...}` deny-by-default
- [15-signalr-progress.md](./15-signalr-progress.md) — почему Ingress должен поддерживать WebSocket
