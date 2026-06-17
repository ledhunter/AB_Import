# 🩹 appsec_v4 — закрытие CVE системных пакетов Alpine в финальных образах

## 🔥 История версий

- **v1.0** (2026-06-16) — добавили `apk upgrade --no-cache <pkgs>` с фоллбеком
  `|| echo "WARNING: apk upgrade failed..."`. ❌ Не сработало: фоллбек тихо
  проглатывал сетевую ошибку, Docker считал слой успешным, AppSec повторно
  находил те же CVE.
- **v1.1** (2026-06-17, опровергнуто) — убрали фоллбек (hard-fail), унифицировали
  `ALPINE_MIRROR` дефолт, подняли дефолт в compose, добавили `--pull` в
  Jenkinsfile. ❌ Hard-fail в Jenkins-контуре без корп. Alpine-зеркала **ломал
  сборку** на network error — нарушал требование «сохранить валидность эталона».
- **v1.2** (2026-06-17, промежуточная) — компромисс: «громкий» fallback с
  многострочным баннером (#####). Сборка ПРОХОДИТ, баннер виден DevOps.
  ⚠️ Использовала `ALPINE_MIRROR` (host для sed) — но path-структура корп.
  mirror Альфы отличается от dl-cdn (`/artifactory/alpine-mirror/latest-stable/main`
  vs `/alpine/v3.XX/main`), sed-замена host'а НЕ работает.
- **v1.3** (2026-06-17, текущая) — узнали точный URL корп. Alpine-зеркала:
  `https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable`. Заменили
  семантику: новый ARG `ALPINE_REPOSITORIES_URL` (полный base URL без
  `/main`/`/community`) полностью переписывает `/etc/apk/repositories` по
  паттерну из примера DevOps. Сохранили громкий fallback из v1.2.

---

## 📍 Точный URL корп. зеркала (для DevOps)

| Что | Значение |
|---|---|
| Base URL | `https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable` |
| Main repo | `https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable/main` |
| Community repo | `https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable/community` |
| Тип | Artifactory remote-proxy Alpine; `latest-stable` = всегда свежий stable Alpine |

Передаётся как `ALPINE_REPOSITORIES_URL` в `dockerBuildArgs` обоих сервисов
(`kilo-import-api`, `kilo-import-web`).

---

## 🚨 Почему v1.0 не закрыл уязвимости

```dockerfile
# БЫЛО (v1.0):
RUN (apk upgrade --no-cache libcrypto3 libssl3 musl musl-utils zlib \
        || echo "WARNING: apk upgrade failed (см. doc 137).")
```

`(... || echo ...)` означает: «если apk упал, напечатай WARNING и **выйди с
кодом 0**». Docker трактует exit 0 как успех слоя → коммитит образ как есть
→ финальный образ выезжает **с уязвимыми пакетами**. WARNING тихо мигает в
логе сборки и теряется среди других сообщений. AppSec_v4 при следующем
прогоне видит те же CVE, потому что слой не изменился — `apk upgrade`
буквально ничего не сделал.

Сценарий, который реализовался: в закрытом Jenkins-контуре `dl-cdn.alpinelinux.org`
недоступен (Permission denied на прокси, см. doc 132). Apk падает на TLS/network
error. Фоллбек глотает ошибку. Билд успешен. Уязвимости не закрыты.

Урок: **в security-патчах нельзя использовать тихий фоллбек**. Лучше явный
fail сборки — он немедленно поднимает проблему до DevOps. Тихая «работа без
эффекта» — худший сценарий.

---

## 📋 Описание

Очередной прогон банковского сканера `T` (Trivy-подобный, см. колонку
«Инструмент» в `appsec_v4.xlsx`) поднял **11 уникальных CVE** уровня **High**
(SLA=3) в системных пакетах Alpine, лежащих внутри наших финальных
docker-образов:

| Пакет | Текущая | Целевая | CVE |
|---|---|---|---|
| `libcrypto3` | 3.5.1-r0 | ≥3.5.7-r0 | OpenSSL × 8 |
| `libssl3`    | 3.5.1-r0 | ≥3.5.7-r0 | (пара к libcrypto3) |
| `musl`       | 1.2.5-r10 | ≥1.2.5-r12 | CVE-2026-40200 |
| `musl-utils` | 1.2.5-r10 | ≥1.2.5-r12 | (то же) |
| `zlib`       | 1.3.1-r2 | ≥1.3.2-r0 | CVE-2026-22184 |

OpenSSL-баг — это 7 CVE в `3.5.7-r0` (одна сводная версия закрывает
несколько) + ещё две (`CVE-2025-15467`→3.5.5, `CVE-2026-31789`→3.5.6), которые
тоже подтягиваются при обновлении до 3.5.7. То есть достаточно поднять до
`≥3.5.7-r0`.

Все эти пакеты — **системные библиотеки Alpine**, поставляются базовыми
образами `aspnet:10.0-preview-alpine` (API) и `nginx:1.27-alpine` (Web).
Build-стейджи отдельно патчить НЕ нужно — они не попадают в финальный образ.

### 🔍 Полный список CVE (для трассировки в журнале AppSec)

| CVE | Пакет | Тип |
|---|---|---|
| CVE-2026-31789 | libcrypto3, libssl3 | OCTET STRING → hex heap-overflow (32-bit) |
| CVE-2025-15467 | libcrypto3, libssl3 | CMS AEAD stack-overflow (IV в ASN.1 параметрах) |
| CVE-2025-69421 | libcrypto3, libssl3 | PKCS#12 NULL pointer deref (DoS) |
| CVE-2026-28387 | libcrypto3, libssl3 | DANE TLSA use-after-free / double-free |
| CVE-2026-28388 | libcrypto3, libssl3 | Delta CRL NULL pointer deref (DoS) |
| CVE-2026-28389 | libcrypto3, libssl3 | CMS KeyAgreeRecipientInfo NULL deref |
| CVE-2026-28390 | libcrypto3, libssl3 | CMS KeyTransportRecipientInfo NULL deref |
| CVE-2026-45447 | libcrypto3, libssl3 | PKCS#7 use-after-free (потенциально RCE) |
| CVE-2026-40200 | musl, musl-utils | qsort stack-corruption (требует ~7M элементов) |
| CVE-2026-22184 | zlib | Глобальный buffer-overflow в утилите `untgz` (не в core lib) |

---

## 🔍 Корневая причина

Базовые образы `aspnet:10.0-preview-alpine` и `nginx:1.27-alpine` собраны
из снапшота Alpine на момент тэгирования образа. Microsoft / Nginx Inc.
не пересобирают эти теги при выходе security-update'ов в Alpine.
Пакеты внутри образа «замерзают» — поэтому нужен `apk upgrade` в нашем
Dockerfile, чтобы при каждой пересборке подтягивать актуальные версии.

В предыдущих сборках мы намеренно НЕ ставили `apk add`/`apk upgrade`
(см. комментарий в Dockerfile + doc 132), потому что в закрытом корп.
контуре Jenkins-агент не имеет выхода на `dl-cdn.alpinelinux.org`
(Permission denied на прокси), а корп. Alpine-зеркала у Альфы пока нет.

Это решение было правильным для запуска сборки, но цена — все CVE
системных пакетов остаются в финальном образе.

---

## ✅ Правильная реализация

### 1. `KiloImportService.Api/Dockerfile` — runtime-stage (v1.3)

```dockerfile
FROM base AS runtime
...
# Полная перезапись /etc/apk/repositories по паттерну из примера DevOps
# (path-структура корп. mirror отличается от dl-cdn — sed-замена не работает).
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
        echo "# DevOps action: передать ALPINE_REPOSITORIES_URL =         #"; \
        echo "#   https://binary.alfabank.ru/artifactory/alpine-mirror/   #"; \
        echo "#   latest-stable                                            #"; \
        echo "# в jenkinsConfiguration.json -> dockerBuildArgs.           #"; \
        echo "# См. doc_project/137 v1.3.                                 #"; \
        echo "############################################################"; \
        echo ""; \
    )
```

### 2. `KiloImportService.Web/Dockerfile` — prod-stage (v1.3)

```dockerfile
FROM ${NGINX_IMAGE} AS prod
ARG ALPINE_REPOSITORIES_URL=""
RUN if [ -n "${ALPINE_REPOSITORIES_URL}" ]; then \
        echo "${ALPINE_REPOSITORIES_URL}/main"      >  /etc/apk/repositories; \
        echo "${ALPINE_REPOSITORIES_URL}/community" >> /etc/apk/repositories; \
    fi && \
    ( apk upgrade --no-cache libcrypto3 libssl3 musl musl-utils zlib \
      && echo "OK..." ) || ( echo "############ WARNING ..." )
```

⚠️ В prod-stage удалили старый `ARG ALPINE_MIRROR=dl-cdn.alpinelinux.org` + `sed`
из v1.2 — они работали только для host-замены, а корп. mirror Альфы имеет
другую path-структуру. В dev/build стейджах Web `ALPINE_MIRROR` остался
(legacy, no-op на дефолте — не вредит).

### 3. `docker-compose.yml` — проброс `ALPINE_REPOSITORIES_URL` в backend

```yaml
backend:
  build:
    args:
      ...
      # Пустой дефолт → не трогаем /etc/apk/repositories из base-образа.
      # В compose-локали с outbound доступом к dl-cdn это работает «из коробки».
      ALPINE_REPOSITORIES_URL: ${ALPINE_REPOSITORIES_URL:-}
```

### 4. `Jenkinsfile` — `--pull` для свежего base-образа

```groovy
sh("docker build --pull --no-cache ${targetArg}${buildArgsStr} -f ${svc.dockerFilePath} ...")
```

`--pull` форсирует Docker тянуть свежий base-образ из корп. registry перед
каждой сборкой. Microsoft периодически пересобирает теги Alpine-образов при
выходе security-update'ов — `--pull` даёт нам свежий snapshot, который +
наш `apk upgrade` через корп. mirror = гарантированное закрытие CVE.

### 5. `jenkinsConfiguration.json` — `ALPINE_REPOSITORIES_URL` в `dockerBuildArgs`

```json
{
    "services": [
        {
            "label": "API",
            ...
            "dockerBuildArgs": {
                "ALPINE_REPOSITORIES_URL": "https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable"
            }
        },
        {
            "label": "Web",
            ...
            "dockerBuildArgs": {
                "NPM_REGISTRY_URL": "...",
                "NPM_REGISTRY_ALFALAB_URL": "...",
                "ALPINE_REPOSITORIES_URL": "https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable"
            }
        }
    ]
}
```

URL подтверждён DevOps Альфы: Артифактори-зеркало Alpine, путь `/latest-stable/`
всегда указывает на свежий stable-выпуск.

### ⚠️ Важно

1. **Без pin'а версий.** Мы НЕ пишем `apk upgrade libcrypto3=3.5.7-r0` —
   через 3 месяца, когда Alpine выпустит `3.5.8-r0`, такой pin сломает
   сборку (NotFound в репо). `apk upgrade <pkg>` без `=` берёт latest.
2. **`--no-cache` обязателен.** Без него apk сохраняет индекс в
   `/var/cache/apk/` (несколько MB) → раздувает финальный слой.
3. **Fallback `|| echo WARNING; true`** (через group `(...)`) — если репо
   недоступен (Jenkins без mirror), сборка не падает, в логе явное
   предупреждение. Без фоллбека эталонная сборка перестанет проходить
   ровно в момент мерджа фикса.
4. **Build-стейджи НЕ трогаем.** Они дают только артефакты для COPY
   (`/app/publish` / `/app/dist`). Их Alpine-пакеты в финальный образ
   не попадают, патчить их = тратить время сборки впустую.
5. **`ALPINE_MIRROR=""` дефолт в API.** В compose-локали apk пойдёт на
   штатный `dl-cdn.alpinelinux.org` из base-образа (есть наружу) →
   apk upgrade сработает, CVE закроются. Web уже имеет
   `ALPINE_MIRROR=dl-cdn.alpinelinux.org` как дефолт — оставляем для
   консистентности с предыдущей логикой.
6. **Runtime CA не трогаем.** Apk upgrade работает с `/etc/ssl/certs/`
   bundle'ом, но не пересоздаёт его. Наш ручной `cat .crt >> bundle`
   (см. doc 135) выполняется ПОСЛЕ apk upgrade — порядок важен,
   apk не должен затирать наши CA.

---

## ❌ Чего НЕ делать

### ❌ Pin точной версии

```dockerfile
# НЕПРАВИЛЬНО — через 3 мес. в репо будет 3.5.8-r0, такая pin даст:
#   ERROR: unable to select packages: libcrypto3-3.5.7-r0: package mentioned in index, but not for repository tag
RUN apk upgrade --no-cache libcrypto3=3.5.7-r0
```

Используй `apk upgrade libcrypto3` (без `=` версии) — поднимет до latest.

### ❌ Тихий однострочный fallback (v1.0)

```dockerfile
# НЕПРАВИЛЬНО (v1.0) — однострочный WARNING теряется среди других сообщений
# сборки, никто не заметит. Образ выезжает БЕЗ обновлённых пакетов.
RUN (apk upgrade --no-cache libcrypto3 libssl3 musl musl-utils zlib \
        || echo "WARNING: apk upgrade failed.")
```

### ❌ Hard-fail без фоллбека (v1.1)

```dockerfile
# НЕПРАВИЛЬНО (v1.1) — если в Jenkins-контуре нет корп. Alpine-зеркала
# (типичный случай по doc 132), сборка падает на network error. Эталонная
# сборка перестаёт проходить → нарушение требования «сохранить валидность».
RUN apk upgrade --no-cache libcrypto3 libssl3 musl musl-utils zlib
```

### ✅ Громкий fallback с баннером (v1.2)

```dockerfile
# ПРАВИЛЬНО (v1.2) — баннер ##### в 8-12 строк хорошо виден в логе сборки.
# Сборка проходит (не ломаем эталон), но DevOps не пропустит проблему.
RUN ( apk upgrade --no-cache <pkgs> && echo "OK..." ) || ( \
        echo "############################################################"; \
        ... \
    )
```

### ❌ Патчить build-стейджи

```dockerfile
# НЕПРАВИЛЬНО — это build-stage для restore/publish, его пакеты
# в финальный образ не попадают. apk upgrade тут — бессмысленная трата
# времени сборки + лишний слой кеша Docker.
FROM ${DOTNET_SDK_IMAGE} AS build
RUN apk upgrade --no-cache libcrypto3 libssl3 musl musl-utils zlib
```

Build-стейджи дают только `/app/publish` (.NET) или `/app/dist` (Vite)
через `COPY --from=build`. Системные библиотеки в финал не попадают.

### ❌ `apk add` вместо `apk upgrade`

```dockerfile
# НЕПРАВИЛЬНО — `apk add` ставит пакет, если его нет, но НЕ обновляет
# уже установленный. Все наши пакеты уже есть в base-образе.
RUN apk add --no-cache libcrypto3 libssl3 musl musl-utils zlib
```

Используй `apk upgrade` — обновляет существующие пакеты до latest.

### ❌ `apk upgrade` без указания пакетов (полный upgrade)

```dockerfile
# НЕПРАВИЛЬНО — обновляет ВСЕ пакеты, включая те, что нам не нужны:
# может прилететь новая версия `tzdata` / `icu-libs` с breaking changes
# в локалях/таймзонах. Минимально-инвазивно — обновлять только уязвимые.
RUN apk upgrade --no-cache
```

Указывай конкретные пакеты из списка appsec.

---

## 📍 Применение в проекте

| Файл | Изменение (v1.3) |
|------|-----------|
| [KiloImportService.Api/Dockerfile](../KiloImportService.Api/Dockerfile) | runtime-stage: `ARG ALPINE_REPOSITORIES_URL` + перезапись `/etc/apk/repositories` + `apk upgrade` + громкий fallback |
| [KiloImportService.Web/Dockerfile](../KiloImportService.Web/Dockerfile) | prod-stage: то же. Удалён старый `ARG ALPINE_MIRROR`+`sed` (не подходит к path-структуре корп. mirror) |
| [docker-compose.yml](../docker-compose.yml) | backend.build.args: `ALPINE_REPOSITORIES_URL: ${ALPINE_REPOSITORIES_URL:-}` (пусто = штатный repositories) |
| [Jenkinsfile](../Jenkinsfile) | `docker build --pull --no-cache ...` — свежий base-образ при каждой сборке |
| [jenkinsConfiguration.json](../jenkinsConfiguration.json) | `dockerBuildArgs.ALPINE_REPOSITORIES_URL = "https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable"` для обоих сервисов |

---

## 🧪 Подтверждение работоспособности

**Локально (compose, есть выход на dl-cdn.alpinelinux.org)**:
```bash
docker compose build backend frontend
# В логе на backend и frontend:
#   (1/5) Upgrading libcrypto3 (3.5.1-r0 -> 3.5.7-r0)
#   (2/5) Upgrading libssl3    (3.5.1-r0 -> 3.5.7-r0)
#   (3/5) Upgrading musl       (1.2.5-r10 -> 1.2.5-r12)
#   (4/5) Upgrading musl-utils (1.2.5-r10 -> 1.2.5-r12)
#   (5/5) Upgrading zlib       (1.3.1-r2  -> 1.3.2-r0)
```

**Jenkins (закрытый контур, mirror не настроен)**:
```
WARNING: apk upgrade failed (см. doc_project/137) — уязвимые пакеты остаются в образе.
Настройте ALPINE_MIRROR.
```
Сборка проходит, но образ остаётся уязвим. **DevOps task** — добавить
`ALPINE_MIRROR` в `jenkinsConfiguration.json` -> `dockerBuildArgs` или
поднять корп. Alpine-зеркало.

**Проверка финального образа**:
```bash
docker run --rm <image> apk info -v libcrypto3 libssl3 musl musl-utils zlib
# Должны быть версии: libcrypto3 ≥ 3.5.7-r0, musl ≥ 1.2.5-r12, zlib ≥ 1.3.2-r0
```

---

## 🎯 Чек-лист (повторный прогон AppSec)

- [ ] В runtime/prod-стейдже образа есть `RUN apk upgrade --no-cache <pkgs>`?
- [ ] Пакеты в списке: `libcrypto3 libssl3 musl musl-utils zlib` (5 шт)?
- [ ] Без pin'а версий (`apk upgrade libcrypto3`, не `libcrypto3=X.Y-rZ`)?
- [ ] Завёрнут в fallback `(... || echo WARNING)` для эталонной Jenkins-сборки?
- [ ] `ALPINE_MIRROR` ARG проброшен из compose / Jenkins build-args?
- [ ] Build-стейджи НЕ трогали (`apk upgrade` только в final/runtime)?
- [ ] Локально проверено `apk info -v <pkg>` в финальном образе?

Если AppSec снова показывает те же CVE — проверь:
1. Лог сборки на `WARNING: apk upgrade failed` (репо был недоступен).
2. Не пересобрался ли образ из кеша до фикса (`docker build --no-cache`).
3. Не сосканировал ли AppSec старый образ из registry (digest не обновлён).

---

## 🔗 См. также

- [doc 121](./121-security-fixes-appsec-v1.md) — первая волна AppSec-фиксов (deps, БД-пароли, SSRF)
- [doc 129](./129-build-time-config-for-corp-network.md) — паттерн `ALPINE_MIRROR` / build-args для корп. контура
- [doc 132](./132-build-alignment-with-service-dev-reference.md) — почему apk недоступен в Jenkins без корп. зеркала
- [doc 135](./135-api-build-stage-ca-overreach.md) — TLS / CA в build/runtime stage'ах
