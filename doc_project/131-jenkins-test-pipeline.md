# 🦊 Jenkins CI: сборка образов для тестового контура

## 📋 Описание

Декларативный Jenkins pipeline ([Jenkinsfile](../Jenkinsfile) в корне репо)
собирает два Docker-образа (`backend` + `frontend prod-stage`) и пушит их
в корп. container-registry. Готов к запуску в test-контуре банка.

Парный с:
- [129-build-time-config-for-corp-network.md](./129-build-time-config-for-corp-network.md) — build-args для корп-зеркал (тут используются как env Jenkins);
- [130-kubernetes-deployment-guide.md](./130-kubernetes-deployment-guide.md) — после Jenkins-сборки образы разворачиваются в k8s.

---

## 🎯 Что делает pipeline

```
┌─────────────────────────────────────────────────────────────────┐
│  1. Checkout      → git pull, вычисление тега                   │
│  2. Backend test  → docker run dotnet test (xUnit, TRX)         │
│  3. Frontend test → docker run npm ci + lint + vitest (JUnit)   │
│  4. Build images (parallel):                                    │
│       • Backend  : docker build (.NET 10 multi-stage)           │
│       • Frontend : docker build --target prod (nginx)           │
│  5. Verify images → image inspect + nginx -t                    │
│  6. Push          → docker push в corp.registry.alfa            │
│  7. Cleanup       → удаление локальных образов, prune кэша      │
└─────────────────────────────────────────────────────────────────┘
```

**Что pipeline НЕ делает** (намеренно):
- Деплой в k8s (`kubectl apply`) — это отдельный CD-pipeline.
- E2E smoke-test на развёрнутом стенде — после деплоя, отдельный job.
- Сканирование уязвимостей образов (Trivy/Aqua) — отдельный security-pipeline.

---

## ✅ Часть 1. Что должен подготовить DevOps Jenkins (один раз)

### 1.1. Агент с label `docker-builder`

Pipeline требует агента с:
- Установленным `docker` (БЕЗ Docker Desktop — Linux daemon),
- `git`,
- Сетевым доступом к корп. container-registry,
- Сетевым доступом к корп. NuGet/npm/Alpine зеркалам.

В Jenkins UI: **Manage Jenkins → Nodes → New Node** → label `docker-builder`.

### 1.2. Credentials с ID `corp-registry-creds`

Тип — **Username with password**. Логин/пароль робота, у которого есть
push-права в репозитории `kilo-import/*` корп. registry.

**Manage Jenkins → Credentials → System → Global → Add Credentials**:

| Поле | Значение |
|------|----------|
| Kind | Username with password |
| Scope | Global |
| Username | `ci-kilo-import-bot` (или аналогичный robot-account) |
| Password | (выдаёт админ registry) |
| **ID** | **`corp-registry-creds`** ← важно, точно так |
| Description | Push-доступ к kilo-import/* в corp.registry.alfa |

### 1.3. Необходимые Jenkins-плагины

| Плагин | Зачем |
|--------|-------|
| `Pipeline` | сам декларативный pipeline |
| `Docker Pipeline` | `agent { docker { image '...' } }` блоки |
| `JUnit` | парс vitest-junit.xml → отображение в UI |
| `MSTest` | парс xUnit-TRX → отображение в UI (опционально — Jenkinsfile graceful-fallback) |
| `Workspace Cleanup` | `cleanWs()` в post-блоке |
| `AnsiColor` | цветные логи (`ansiColor('xterm')` в options) |
| `Timestamper` | таймстампы в логах (`timestamps()`) |

Без `MSTest` pipeline тоже отработает — try/catch в Jenkinsfile сделает fallback на archiveArtifacts.

### 1.4. Multibranch pipeline job в Jenkins

Создать **New Item → Multibranch Pipeline**:
- Branch Sources → Git → URL: `<git-url-репозитория>`, credentials с read-доступом;
- Build Configuration → Mode: by Jenkinsfile, Script Path: `Jenkinsfile`;
- Scan Multibranch Pipeline Triggers: периодически или Webhook от GitLab/Bitbucket.

После сохранения Jenkins автоматически найдёт `Jenkinsfile` в каждой ветке
и подхватит изменения.

---

## ✅ Часть 2. Параметры pipeline

| Параметр | По умолчанию | Зачем |
|----------|--------------|-------|
| `IMAGE_TAG` | пусто → `test-<BUILD>-<git-sha7>` | Кастомный тег для образов (релизные сборки, hot-fix'ы) |
| `SKIP_TESTS` | `false` | Пропустить xUnit + vitest (срочные фиксы) |
| `PUSH_IMAGES` | `true` | Пушить образы или только собрать локально (для дебага) |
| `CORP_REGISTRY` | `corp.registry.alfa` | Хост корп. container-registry |

Запуск из UI: **Build with Parameters** → выставить значения → Build.

---

## 🗺️ Часть 3. Как настроить env-блок под свой контур

В [Jenkinsfile:55-65](../Jenkinsfile) хардкожены адреса корп-зеркал по
шаблону `corp.registry.alfa`, `nuget.corp.alfa`, `alpine.mirror.corp.alfa`.
**Заменить на реальные адреса** вашего Artifactory/Nexus:

```groovy
environment {
    DOTNET_SDK_IMAGE    = 'РЕАЛЬНЫЙ-РЕГИСТРИ/mcr/dotnet/sdk:10.0-preview-alpine'
    DOTNET_ASPNET_IMAGE = 'РЕАЛЬНЫЙ-РЕГИСТРИ/mcr/dotnet/aspnet:10.0-preview-alpine'
    NODE_IMAGE          = 'РЕАЛЬНЫЙ-РЕГИСТРИ/library/node:20-alpine'
    NGINX_IMAGE         = 'РЕАЛЬНЫЙ-РЕГИСТРИ/library/nginx:1.27-alpine'
    NUGET_FEED_URL      = 'https://nuget.РЕАЛЬНЫЙ-ДОМЕН/repository/nuget-proxy/index.json'
    NPM_REGISTRY_URL    = 'https://npm.РЕАЛЬНЫЙ-ДОМЕН/repository/npm-proxy/'
    ALPINE_MIRROR       = 'alpine.РЕАЛЬНЫЙ-ДОМЕН'
}
```

⚠️ Альтернатива — вынести в **Manage Jenkins → System → Global properties →
Environment variables**. Тогда `environment {}` блок в Jenkinsfile удалить,
переменные приедут из Jenkins-config'а. Полезно, если корп-зеркала
адресуются одинаково во всех job'ах банка.

---

## ✅ Часть 4. Что должно быть подтверждено DevOps до запуска

| # | Проверка | Команда |
|---|----------|---------|
| 1 | Агент с label `docker-builder` есть, online | `Manage Jenkins → Nodes → docker-builder → Status` |
| 2 | На агенте установлен docker 24+ и docker-buildkit | `docker version` на агенте |
| 3 | Agent видит корп-registry | `docker pull corp.registry.alfa/library/node:20-alpine` с агента |
| 4 | NuGet feed доступен | `curl -sf https://nuget.corp.alfa/repository/nuget-proxy/index.json` |
| 5 | npm registry доступен | `curl -sf https://npm.corp.alfa/repository/npm-proxy/-/all` |
| 6 | Credentials `corp-registry-creds` сохранены | `Manage Jenkins → Credentials` |
| 7 | У робота есть push-права в `kilo-import/*` | вручную: `docker login + push test-image` |
| 8 | preview-теги .NET 10 (`10.0-preview-alpine`) залиты в корп. registry | `docker pull corp.registry.alfa/mcr/dotnet/sdk:10.0-preview-alpine` |

⚠️ Пункт 8 — самый частый блокер. Корп-Artifactory банка обычно фильтрует
preview-/RC-теги. Если DevOps скажет «только GA» — нужно либо ждать .NET 10
GA и обновить теги в env, либо договориться об исключении.

---

## ❌ Типичные ошибки

### ❌ 1. `docker: command not found` на агенте

Pipeline нацелен на агента с docker daemon. Если запустить на master-агенте
Jenkins без docker — упадёт на первом же `agent { docker { ... } }` блоке.

**Решение**: создать docker-builder агент (см. § 1.1) или установить docker
на существующего агента.

### ❌ 2. `unauthorized: authentication required` на push

Credentials с ID `corp-registry-creds` либо отсутствуют, либо у робота
нет прав push в нужный репозиторий.

**Решение**: проверить § 1.2 + п. 7 чек-листа.

### ❌ 3. `manifest unknown` на pull preview-тега .NET 10

Корп-registry не имеет нужного образа.

**Решение**: запросить у DevOps зеркалирование `mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine`
и `aspnet:10.0-preview-alpine` в корп-Artifactory. См. § Часть 4 п. 8.

### ❌ 4. `dotnet ef` падает на restore в SDK-контейнере

Корп-NuGet feed не содержит preview-пакетов `Microsoft.AspNetCore.* 10.0.7`.

**Решение**: настроить proxy-repository в Artifactory под `api.nuget.org` БЕЗ
фильтра preview, либо вручную залить нужные пакеты.

### ❌ 5. vitest падает с `EAI_AGAIN registry.npmjs.org`

`npm config set registry "${NPM_REGISTRY_URL}"` не отработал — переменная
пуста или registry недоступен.

**Решение**: проверить, что env-блок Jenkinsfile содержит реальный URL
(см. § 3), и что агент видит этот URL (`curl -sf $NPM_REGISTRY_URL` на агенте).

---

## 📍 Применение в проекте

| Файл | Что |
|------|-----|
| [Jenkinsfile](../Jenkinsfile) | Declarative pipeline, 7 stage'ей, ~200 строк с комментариями |
| [KiloImportService.Api/Dockerfile](../KiloImportService.Api/Dockerfile) | Получает build-args от Jenkinsfile (см. doc 129) |
| [KiloImportService.Web/Dockerfile](../KiloImportService.Web/Dockerfile) | То же, plus `--target prod` для nginx-stage (см. doc 130 § 7.2) |

---

## 🎯 Чек-лист первого запуска

- [ ] Все плагины Jenkins из § 1.3 установлены
- [ ] Агент с label `docker-builder` создан и online
- [ ] Credentials `corp-registry-creds` сохранены
- [ ] env-блок Jenkinsfile отредактирован под реальные хосты корп. контура
- [ ] `Manage Jenkins → Credentials` для git read-доступа настроены (для checkout SCM)
- [ ] Multibranch pipeline job создан, репозиторий просканирован
- [ ] Тестовый запуск с `SKIP_TESTS=true PUSH_IMAGES=false` прошёл (валидация Dockerfile'ов)
- [ ] Тестовый запуск с дефолтами прошёл, образы появились в corp-registry с тегом `test-<BUILD>-<sha>`

---

## ⚠️ Связанные документы

- [129-build-time-config-for-corp-network.md](./129-build-time-config-for-corp-network.md) — build-args для корп. контура (Jenkinsfile их прокидывает в `docker build`)
- [130-kubernetes-deployment-guide.md](./130-kubernetes-deployment-guide.md) — куда деплоятся собранные образы
- [123-environment-switching-guide.md](./123-environment-switching-guide.md) — операционка по контурам (test/preprod/prod)
- [122-environment-config.md](./122-environment-config.md) — runtime env-переменные
