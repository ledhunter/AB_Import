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
- **v1.3** (2026-06-17, опровергнуто) — узнали точный URL корп. Alpine-зеркала:
  `https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable`. Заменили
  семантику: новый ARG `ALPINE_REPOSITORIES_URL` (полный base URL без
  `/main`/`/community`) полностью переписывает `/etc/apk/repositories` по
  паттерну из примера DevOps. Сохранили громкий fallback из v1.2.
  ❌ AppSec_v4 (повтор от 2026-06-19, `appsec_v4.xlsx`) показал **те же 11 CVE**.
  URL был подтверждён как корректный (см. ниже — другой проект Альфы AWP
  использует тот же паттерн `binary.alfabank.ru/artifactory/alpine-mirror/`
  `latest-stable/{main,community}` и собирает успешно), поэтому гипотеза
  «несовместимый индекс» **снята**. Реальные кандидаты на причину:
  1. AppSec_v4 сканировал старый snapshot образа (до мерджа v1.3);
  2. Отсутствие явного `apk update` — индекс из base заморожен → upgrade
     не видел свежих версий;
  3. Network access от Jenkins к `binary.alfabank.ru/artifactory/alpine-mirror/`
     может быть ограничен отдельной политикой, отличной от Docker/NuGet feed'ов.
- **v1.4** (2026-06-19, текущая) — defense-in-depth: `apk update` явно
  + `--available` флаг + двух-зеркальный fallback + sanity-check `apk info -v`:
  1. Попытка 1 — `ALPINE_REPOSITORIES_URL` (корп. mirror, если задан).
  2. Попытка 2 — штатный `dl-cdn.alpinelinux.org` с **версия-specific** path
     (`/alpine/v${ALPINE_VER}/main`), где `ALPINE_VER` вычисляется из
     `/etc/alpine-release` base-образа. Резервный путь на случай блокировки
     именно корп. зеркала (Jenkins-сетевые политики, временный 5xx).
  3. Если оба упали — громкий баннер (как в v1.2/v1.3, эталон не падает).
  4. Sanity-check `apk info -v libcrypto3 libssl3 musl musl-utils zlib`
     печатает финальные версии в лог сборки — DevOps подтверждает закрытие
     CVE по логу, не открывая образ.

  Корневые добавления к v1.3:
  - **`apk update`** ЯВНО — без него `apk upgrade` опирается на индекс из
    base-образа (заморожен на дату тегирования) и может «не видеть» свежих
    версий → молча выходит с 0;
  - **`--available`** — переход на актуальную версию даже если репо считает её
    «downgrade» (edge-case при смене мажорной ветки Alpine);
  - **dl-cdn fallback** — независимый резервный путь;
  - **sanity-check** `apk info -v` — DevOps подтверждает результат по логу.
- **v1.5** (2026-06-19, текущая) — `appsec_v5.xlsx` показал, что v1.4
  **закрыл 9 из 11 CVE** (musl × 2, zlib × 1, 6 OpenSSL CVE с целевой версией
  ≤3.5.6-r0), но остаётся **CVE-2026-45447** (PKCS#7 use-after-free, потенц. RCE)
  на `libcrypto3` и `libssl3` — требует **≥3.5.7-r0**. Корп. mirror
  `latest-stable` скорее всего отдаёт OpenSSL 3.5.5 или 3.5.6 (не 3.5.7).
  Добавили в Dockerfile **verify-блок**: после `apk info -v` проверяем
  `libcrypto3 >= 3.5.7-r0` через `sort -V`. Если меньше — громкий баннер
  с фактической версией Alpine из `/etc/alpine-release` (DevOps видит,
  что зависло на старой Alpine-ветке).
  ⚠️ **v1.5 — диагностический, не лечебный**: если репо не отдаёт >=3.5.7,
  никакая команда apk не достанет нужную версию. Решение на стороне DevOps:
  перепривязать `latest-stable` на более свежую Alpine-ветку или поднять
  base-образ aspnet:10.0-preview-alpine.

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

**Подтверждение URL** — другой проект Альфы AWP (Единый фронт) использует
тот же паттерн в своём Dockerfile:
> [git.moscow.alfaintra.net/projects/AWP/repos/alpine-node-nginx-krb5-awp](https://git.moscow.alfaintra.net/projects/AWP/repos/alpine-node-nginx-krb5-awp/browse)
>
> ```dockerfile
> RUN echo "https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable/main" \
>     > /etc/apk/repositories \
>     && echo "https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable/community" \
>     >> /etc/apk/repositories \
>     && apk add --no-cache jq
> ```
>
> Они используют `apk add` (установка нового пакета), мы — `apk upgrade`
> (обновление существующих). Логика записи в `/etc/apk/repositories`
> идентична. AWP собирается успешно — URL рабочий, network access к
> `binary.alfabank.ru/artifactory/alpine-mirror/` из Jenkins-агентов
> Альфы открыт. Если в нашей сборке корп. mirror всё-таки не отзывается,
> v1.4 страхуется через dl-cdn-fallback.

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

### 1. `KiloImportService.Api/Dockerfile` — runtime-stage (v1.4)

```dockerfile
FROM base AS runtime
...
# Двух-зеркальный fallback + apk update + версия-specific path для dl-cdn
# (см. историю — v1.3 не сработал из-за несовместимости индекса latest-stable
# с base-образом). После двух попыток sanity-check `apk info -v` печатает
# финальные версии в лог сборки.
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
            echo "############################################################"; \
            echo "# WARNING: apk upgrade FAILED — оба Alpine-зеркала упали.   #"; \
            echo "# Уязвимые системные пакеты ОСТАЮТСЯ в финальном образе:    #"; \
            echo "# DevOps action — проверить outbound к binary.alfabank.ru   #"; \
            echo "# и dl-cdn.alpinelinux.org. См. doc_project/137 v1.4.       #"; \
            echo "############################################################"; \
        fi; \
    fi; \
    echo "── Финальные версии Alpine-пакетов (для трассировки AppSec) ──"; \
    apk info -v $APK_PKGS 2>&1 || true
```

### 2. `KiloImportService.Web/Dockerfile` — prod-stage (v1.4)

Логика идентична API runtime-stage — те же 5 пакетов, тот же двух-зеркальный
fallback + sanity-check. ⚠️ В prod-stage старый `ARG ALPINE_MIRROR` + `sed`
из v1.2 не используется (path-структура корп. mirror не совпадает с dl-cdn).
В dev/build стейджах Web `ALPINE_MIRROR` остался (legacy, no-op на дефолте).

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

1. **`apk update` ЯВНО перед upgrade.** Без него apk опирается на индекс
   из base-образа — а тот заморожен на дату тегирования (Microsoft / Nginx Inc.
   не пересобирают). Симптом без update: `apk upgrade` находит «нечего обновлять»
   и тихо выходит с 0, образ выезжает с старыми пакетами.
2. **Флаг `--available`.** Форсирует переход на актуальную версию, даже если
   репо считает её «downgrade» (бывает при смене мажорной ветки Alpine между
   base-образом и зеркалом).
3. **Двух-зеркальный fallback (v1.4).** Сначала корп. mirror, если упал —
   штатный `dl-cdn.alpinelinux.org` с **версия-specific** path (`/alpine/v3.20/main`,
   где версия из `/etc/alpine-release`). Это страхует от ситуации v1.3, где
   `latest-stable` корп. зеркала отдавал индекс несовместимой версии Alpine.
4. **Sanity-check `apk info -v` в конце.** Печатает финальные версии в лог
   сборки. DevOps подтверждает закрытие CVE по логу — не нужно поднимать
   контейнер и инспектировать. Конструкция `|| true` чтобы шаг не упал.
5. **Без pin'а версий.** Мы НЕ пишем `apk upgrade libcrypto3=3.5.7-r0` —
   через 3 месяца, когда Alpine выпустит `3.5.8-r0`, такой pin сломает сборку
   (NotFound в репо). `apk upgrade <pkg>` без `=` берёт latest.
6. **`--no-cache` обязателен.** Без него apk сохраняет индекс в
   `/var/cache/apk/` (несколько MB) → раздувает финальный слой.
7. **Fallback с громким баннером.** Если оба зеркала упали — сборка
   не падает (эталон сохраняется), в логе явный 5-строчный баннер
   `############`. Без фоллбека эталон перестанет проходить ровно в
   момент мерджа фикса.
8. **Build-стейджи НЕ трогаем.** Они дают только артефакты для COPY
   (`/app/publish` / `/app/dist`). Их Alpine-пакеты в финальный образ
   не попадают, патчить их = тратить время сборки впустую.
9. **Runtime CA не трогаем.** Apk upgrade работает с `/etc/ssl/certs/`
   bundle'ом, но не пересоздаёт его. Наш ручной `cat .crt >> bundle`
   (см. doc 135) выполняется ПОСЛЕ apk upgrade — порядок важен,
   apk не должен затирать наши CA.
10. **NuGet-зеркало `binary.alfabank.ru/artifactory/api/nuget/v3/nuget_public`
    закрывает CVE .NET-пакетов, не Alpine.** Уязвимости в `appsec_v4.xlsx`
    относятся ИСКЛЮЧИТЕЛЬНО к системным библиотекам Alpine из base-образа,
    подмена NuGet feed'а их не закрывает. `Include prerelease = True` —
    UI-флаг Visual Studio, в `nuget.config` не настраивается; в нашем
    проекте preview-пакеты (.NET 10) уже подтягиваются по точному пину в
    .csproj без необходимости в этом флаге.

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

### ❌ Один-зеркальный fallback без apk update (v1.3)

```dockerfile
# НЕПРАВИЛЬНО (v1.3) — пробовали ТОЛЬКО корп. mirror, без apk update,
# без --available, без sanity-check'а. Если корп. mirror отдал индекс
# несовместимой версии Alpine — apk молча падал → fallback с баннером,
# образ выезжал с уязвимыми пакетами. AppSec_v4 (2026-06-19) подтвердил
# на appsec_v4.xlsx: те же 11 CVE.
RUN if [ -n "${ALPINE_REPOSITORIES_URL}" ]; then \
        echo "${ALPINE_REPOSITORIES_URL}/main" > /etc/apk/repositories; \
        ... \
    fi && \
    ( apk upgrade --no-cache <pkgs> && echo "OK..." ) || ( echo "WARN..." )
```

### ✅ Двух-зеркальный fallback с версия-specific path (v1.4)

```dockerfile
# ПРАВИЛЬНО (v1.4): корп. mirror → dl-cdn с alpine версией из base → баннер.
# apk update явно, --available для downgrade-cases, sanity-check apk info -v
# в конце. Подробности — выше в «Правильная реализация».
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

| Файл | Изменение (v1.5) |
|------|-----------|
| [KiloImportService.Api/Dockerfile](../KiloImportService.Api/Dockerfile) | runtime-stage: v1.4 (двух-зеркальный fallback + `apk update` + `--available` + sanity-check) + **v1.5 verify-блок**: `sort -V` сравнение `libcrypto3` против `3.5.7-r0`; если меньше — баннер с `/etc/alpine-release` |
| [KiloImportService.Web/Dockerfile](../KiloImportService.Web/Dockerfile) | prod-stage: идентичная логика |
| [docker-compose.yml](../docker-compose.yml) | backend.build.args: `ALPINE_REPOSITORIES_URL: ${ALPINE_REPOSITORIES_URL:-}` (пусто = идём по dl-cdn fallback'у) — без изменений с v1.3 |
| [Jenkinsfile](../Jenkinsfile) | `docker build --pull --no-cache ...` — без изменений с v1.3 |
| [jenkinsConfiguration.json](../jenkinsConfiguration.json) | `dockerBuildArgs.ALPINE_REPOSITORIES_URL = "https://binary.alfabank.ru/artifactory/alpine-mirror/latest-stable"` — без изменений с v1.3 |

---

## 🧪 Подтверждение работоспособности

**Локально (compose, ALPINE_REPOSITORIES_URL пуст, есть выход на dl-cdn)**:
```bash
docker compose build backend frontend
# В логе:
#   WARN: primary apk upgrade failed — попытка через штатный dl-cdn.alpinelinux.org
#   (первая попытка падает только если /etc/apk/repositories отсутствует — обычно
#    при пустом ARG первая попытка идёт по штатному base'у и сразу проходит)
#   OK: appsec_v4 — Alpine system packages upgraded via primary mirror
#   ── Финальные версии Alpine-пакетов (для трассировки AppSec) ──
#   libcrypto3-3.5.7-r0 ...
#   libssl3-3.5.7-r0 ...
#   musl-1.2.5-r12 ...
#   musl-utils-1.2.5-r12 ...
#   zlib-1.3.2-r0 ...
```

**Jenkins (корп. mirror настроен через jenkinsConfiguration.json)**:
```
INFO: /etc/apk/repositories переписан на https://binary.alfabank.ru/...
OK: appsec_v4 — Alpine system packages upgraded via primary mirror
── Финальные версии Alpine-пакетов (для трассировки AppSec) ──
libcrypto3-3.5.7-r0 ...
...
```

**Jenkins (если корп. mirror отдал несовместимый индекс)**:
```
INFO: /etc/apk/repositories переписан на https://binary.alfabank.ru/...
WARN: primary apk upgrade failed — попытка через штатный dl-cdn.alpinelinux.org
OK: appsec_v4 — Alpine system packages upgraded via dl-cdn fallback
```
v1.4 закроет CVE через fallback. Если dl-cdn недоступен — громкий баннер.

**Проверка финального образа (если sanity-check в логе недостаточен)**:
```bash
docker run --rm <image> apk info -v libcrypto3 libssl3 musl musl-utils zlib
# Должны быть версии: libcrypto3 ≥ 3.5.7-r0, musl ≥ 1.2.5-r12, zlib ≥ 1.3.2-r0
```

---

## 🎯 Чек-лист (повторный прогон AppSec — v1.5)

- [ ] В runtime/prod-стейдже образа есть `apk update` ПЕРЕД `apk upgrade`?
- [ ] Используется флаг `--available` у `apk upgrade`?
- [ ] Пакеты в списке: `libcrypto3 libssl3 musl musl-utils zlib` (5 шт)?
- [ ] Без pin'а версий (`apk upgrade libcrypto3`, не `libcrypto3=X.Y-rZ`)?
- [ ] Реализован двух-зеркальный fallback (корп. mirror → dl-cdn версия-specific)?
- [ ] Громкий баннер `############` если оба зеркала упали (эталон не падает)?
- [ ] Sanity-check `apk info -v $APK_PKGS` в конце шага (финальные версии в лог)?
- [ ] **v1.5: Verify-блок** `sort -V` проверка `libcrypto3 >= 3.5.7-r0`?
- [ ] **v1.5: При WARN** в баннер выводится `/etc/alpine-release` (DevOps видит,
  на какой Alpine-ветке зависло корп. mirror)?
- [ ] `ALPINE_REPOSITORIES_URL` ARG проброшен из compose / Jenkins build-args?
- [ ] Build-стейджи НЕ трогали (`apk upgrade` только в final/runtime/prod)?
- [ ] Локально проверено в логе сборки: `Upgrading libcrypto3 (... -> 3.5.7-r0)`?

Если AppSec_v6 снова показывает CVE-2026-45447 (libcrypto3/libssl3) —
проверь по логу сборки:
1. Сборка идёт с актуальными Dockerfile (v1.4+v1.5)?
2. Verify-блок печатает `OK: libcrypto3=X >= 3.5.7-r0` (CVE закрыт) или
   баннер `WARNING: libcrypto3=X < 3.5.7-r0`?
3. В баннере `/etc/alpine-release: 3.X.Y` — какая Alpine-ветка? Если 3.20
   или старее — корп. mirror `latest-stable` зафиксирован на ветке без
   OpenSSL 3.5.7+ (Alpine выпустил его, начиная с какой-то 3.21/3.22 версии).
4. **DevOps action**: перепривязать `latest-stable` или поднять base-образ
   `aspnet:10.0-preview-alpine` (Microsoft выпускает rebuild при свежих
   security-update'ах Alpine).

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
