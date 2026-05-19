# 🔒 Security Audit Workflow (full-repo)

## 📋 Описание

Подробная инструкция для агента-аудитора (Claude в роли cybersecurity specialist),
по которой производится **полный аудит** репо `KiloImportService` на уязвимости.

**Связь с встроенным `/security-review`**

| | `/security-review` (built-in skill) | `93-security-audit-workflow.md` (этот документ) |
|---|---|---|
| Скоуп | pending changes на текущей ветке (diff vs `main`) | **весь репо** + history + образы + БД + интеграция Visary |
| Глубина | review коммитов перед merge | многослойный аудит: secrets → deps → code → infra → integration |
| Триггер | каждый PR | периодически (релиз / квартальный аудит / после крупного merge) |
| Выход | список найденного по diff'у | формальный отчёт с CVSS-lite severity + remediation plan |

`/security-review` — **первый эшелон** (gate перед merge). Этот workflow —
**второй эшелон** (catches накопленных регрессий и issues, которые проскочили diff-review).

---

## 🎯 Scope аудита

Все слои стека:

- **Backend**: .NET 10 Web API (`KiloImportService.Api/`) + EF Core + SignalR
- **Frontend**: React 18-19 + Vite + TypeScript (`KiloImportService.Web/`)
- **Visary integration**: `Visary.Api.Client/` (proxy controllers `/api/visary/*`, JWT)
- **Database**: PostgreSQL 16 (service-db + visary-cache)
- **Infrastructure**: Docker Compose, Dockerfile'ы, базовые образы (alpine)
- **Git history**: secret-leak detection, удалённые но не очищенные файлы
- **External assets**: `Context/` HAR-файлы, embedded resources (`Бюджет_А4.1.xlsx`)

---

## 🗺️ Threat model (project-specific)

### Активы

| # | Актив | Критичность | Где хранится |
|---|---|---|---|
| A1 | **JWT Bearer Visary** | 🔴 Critical | `.env` (gitignored), in-memory backend, Vite bundle (⚠️ см. подсветку ниже) |
| A2 | Загружаемые XLSX-файлы | 🟡 Medium | volume `import-files`, отправляются в Visary File Storage |
| A3 | Postgres БД `service-db` | 🟡 Medium | сессии импорта, jsonb `staged_rows` (бизнес-данные) |
| A4 | Postgres БД `visary-cache` | 🟢 Low | кэш проектов из Visary (не PII) |
| A5 | `Context/har *.txt` | 🔴 Critical | untracked, но могут случайно закоммититься; **содержат живые JWT** |
| A6 | Connection strings | 🟡 Medium | docker-compose env, `.env` |

### Профили атакующего

- **A-ext**: External attacker без доступа к корп. сети (через публично выставленный endpoint)
- **A-user**: Authenticated user UI (имеет доступ к импорту, но не к чужим сессиям)
- **A-dep**: Compromised npm/nuget dependency (supply chain)
- **A-ins**: Insider (доступ к репо/CI/dev-машинам)
- **A-file**: Загрузка вредоносного XLSX (XXE, Zip-Slip, external links → SSRF)

### 🚨 Подсвеченные project-specific риск-зоны

> Это области, на которые **обязательно** обращать дополнительное внимание —
> исторический контекст из памяти и `doc_project/`.

#### R1. JWT Visary в Vite-bundle (frontend)

`.env` содержит `VITE_VISARY_API_TOKEN`. **Vite встраивает `VITE_*` переменные в
production bundle на этапе `npm run build`** — токен становится доступен любому,
кто откроет DevTools или скачает bundle.

- **Проверка**: после `docker compose build frontend` — `docker run --rm <image> sh -c "grep -r 'eyJ' /app/dist || echo CLEAN"`.
- Если frontend в dev-mode (`vite dev`) — токен виден через `import.meta.env`, что менее
  критично (dev-only), но всё равно exposed клиенту.
- См. [54-visary-token-hot-reload.md](./54-visary-token-hot-reload.md) — единый SSOT
  в `.env` принят сознательно. **Вопрос аудита**: должен ли frontend вообще иметь
  токен, или весь Visary-трафик идёт через backend-proxy `/api/visary/*` (см. R3)?

#### R2. HAR-файлы с живыми токенами в `Context/`

`Context/har ГФ.txt` (untracked) использовался для реверс-инжиниринга API CostItem
(см. [91-finmodel-chapter1-schedule.md](./91-finmodel-chapter1-schedule.md)). HAR из
DevTools содержит **полные request headers**, включая `Authorization: Bearer eyJ…`.

- **Проверка**: `gitleaks` на текущий tree + `git log --all -p -- 'Context/*'`.
- **Проверка `.gitignore`**: должна быть запись для `Context/` или `*.har`.
- **Проверка**: ни один HAR не должен был попасть в публичные коммиты (репо не
  публичный, но всё равно).

#### R3. `/api/visary/*` proxy controllers (backend) — SSRF surface

[55-visary-proxy-controllers.md](./55-visary-proxy-controllers.md): registry-паттерн
с 8 справочниками + 11 explicit actions. **Любой неконтролируемый user-input в URL
части `/listview/{mnemonic}` или `/crud/{mnemonic}/{id}` — потенциальный SSRF**:
если backend строит URL через интерполяцию без allowlist, attacker может направить
запрос на любой эндпоинт Visary (включая `/listview/admin/…` если такой существует).

- **Проверка**: все `{mnemonic}`-параметры контроллеров должны валидироваться против
  `VisaryMnemonics` whitelist (см. `Visary.Api.Client/Common/VisaryMnemonics.cs`).
- **Проверка**: `associationId`, `siteId`, `wbsId` — должны быть `int`, не `string`
  (auto-binding ASP.NET валидирует, но смотри custom-биндинг).

#### R4. XLSX parser — XXE / Zip-Slip / external links

[81-xlsx-external-links-strip.md](./81-xlsx-external-links-strip.md): ручной zip-уровень
cleanup external links (`xl/externalLinks/*`). Это **активное взаимодействие с zip-архивом**,
загруженным пользователем.

- **Проверка XXE**: ClosedXML под капотом — XmlReader; проверить, что нигде не
  включено `DtdProcessing.Parse` / `XmlResolver = new XmlUrlResolver()`.
- **Проверка Zip-Slip**: при cleanup создаём новый архив — Entry.FullName не
  валидируется на `../`? (актуально, если потом куда-то extract'ится).
- **Проверка DoS**: `zip bomb` — XLSX, который распакован в гигабайты. ClosedXML
  не имеет встроенной защиты; смотреть на `Stream.Length` и лимит размера upload.
- **Проверка external links**: формулы вида `'https://evil.com/[x.xlsx]Sheet'!A1` —
  парсер их strip'ит, но **перед strip'ом первый attempt парсинга мог триггернуть
  запрос?** (ClosedXML lazy-resolve formulas — см. v1.2 в доке 81).

#### R5. File upload to Visary File Storage

[82-visary-file-storage-upload.md](./82-visary-file-storage-upload.md): автозагрузка
XLSX-бюджета в файловое хранилище Visary. Backend получает `driveId`/`directoryId` из
`ProjectFolder` проекта.

- **Проверка path traversal**: `directoryId` — числовой? Не `string` с `../`?
- **Проверка authZ**: проверка, что текущий пользователь имеет право писать в
  выбранную папку Visary (или это полностью на стороне Visary?).
- **Проверка**: размер upload-файла (DoS / квота).

#### R6. SignalR `JoinSession`

[15-signalr-progress.md](./15-signalr-progress.md): `JoinSession(sessionId)` —
hub-метод.

- **Проверка**: проверяет ли hub, что `sessionId` принадлежит **текущему** пользователю?
  Иначе любой подключённый клиент может слушать прогресс импорта чужой сессии.
- **Проверка**: rate-limit на `JoinSession` (DoS через массовые подписки).

#### R7. `staged_rows.RawValues` / `Actions` / `Errors` (jsonb)

Большой объём jsonb-данных, прилетающих в БД от парсера и маппера. **Реальная сериализация
через `System.Text.Json`** (см. [89-mappedrow-sheet-invariant.md](./89-mappedrow-sheet-invariant.md)).

- **Проверка**: input от пользователя (содержимое XLSX-ячеек) идёт прямо в jsonb. Это
  **OK** для хранения, но при последующем рендере в UI отчёта — должен быть **HTML-escape**.
- **Проверка XSS в построчном отчёте**: `SessionRowsTable.tsx` рендерит `Cells["…"]` —
  через `{value}` (React auto-escape) или `dangerouslySetInnerHTML`?

#### R8. DotEnvLoader

Backend сам парсит `.env` через `DotEnvLoader` ([54-visary-token-hot-reload.md](./54-visary-token-hot-reload.md)).

- **Проверка**: не логирует ли он содержимое `.env` целиком при ошибке?
- **Проверка**: безопасное чтение (нет path traversal к чужому `.env`?).

#### R9. PDF export (PDFsharp)

[74-import-pdf-export.md](./74-import-pdf-export.md): сборка PDF из user-controlled
данных (содержимое XLSX-ячеек попадает в PDF).

- **Проверка**: PDFsharp не интерпретирует input как PDF-команды (не PDF-injection)?
- **Проверка**: FontResolver регистрируется один раз под lock — race condition на
  старте при параллельных запросах?

#### R10. Postgres credentials

`docker-compose.yml`: пароли postgres. **Проверка**:
- Default `postgres/postgres` нигде не используется?
- Connection strings не утекают в SignalR-events / error responses?

---

## 🚀 Pre-flight (перед началом аудита)

### Шаг 0.1 — Сбор контекста

```powershell
# Бранч / commit, на котором делается аудит
git rev-parse HEAD
git log --oneline -20

# Состояние working tree (должно быть clean для воспроизводимости)
git status --short

# Untracked файлы — проверить отдельно (могут содержать секреты)
git ls-files --others --exclude-standard
```

### Шаг 0.2 — Baseline

Перед началом — **зафиксируй baseline** в файле `audit-{YYYY-MM-DD}.log`:
- Версии: `dotnet --version`, `node --version`, `docker --version`
- Список образов: `docker images | grep kilo-import`
- Известные/принятые ранее риски (см. предыдущий аудит, если есть)

### Шаг 0.3 — Инструменты (установить локально или взять Docker-образы)

| Инструмент | Назначение | Установка / запуск |
|---|---|---|
| `gitleaks` | secret scan | `docker run --rm -v ${PWD}:/repo zricethezav/gitleaks detect --source /repo --report-format json` |
| `trufflehog` | secret scan + verified leaks | `docker run --rm -v ${PWD}:/repo trufflesecurity/trufflehog git file:///repo --json` |
| `semgrep` | SAST patterns | `docker run --rm -v ${PWD}:/src returntocorp/semgrep semgrep --config=auto /src` |
| `dotnet list package --vulnerable` | .NET CVE | встроено в SDK |
| `npm audit` | npm CVE | встроено в npm |
| `trivy` | image CVE + filesystem | `docker run --rm -v ${PWD}:/repo aquasec/trivy fs /repo` |
| `docker scout` | image CVE | `docker scout cves <image>` |
| `dotnet-security-scan` (Microsoft) | .NET security analyzers | NuGet `SecurityCodeScan.VS2019` |
| `eslint-plugin-security` | JS sinks | `npm install -D eslint-plugin-security` |
| `OWASP ZAP` (manual) | dynamic scan | docker `owasp/zap2docker-stable` |

---

## 🔍 Этапы аудита

### Этап 1 — Secrets & credentials

**Цель**: убедиться, что в репо (текущий tree + git history) нет секретов.

#### 1.1 Static scan текущего tree

```powershell
# gitleaks — конфиг по умолчанию покрывает JWT, AWS, GCP, Slack tokens
docker run --rm -v ${PWD}:/repo zricethezav/gitleaks detect `
  --source /repo --report-format json --report-path /repo/.audit-secrets.json --no-banner
```

Что искать вручную, дополнительно к gitleaks:

| Паттерн | Что это | Где может быть |
|---|---|---|
| `eyJ[A-Za-z0-9_-]{20,}` | JWT (RS256) | `.env`, `appsettings*.json`, `Context/`, тесты |
| `postgres://`, `Server=`, `Host=` | Postgres connection string | `docker-compose.yml`, `appsettings*.json` |
| `Bearer\s+[A-Za-z0-9]` | HTTP-auth headers в коде | mock-данные тестов, HAR-файлы |
| `(api[_-]?key|secret)["\s:=]+["']?[A-Za-z0-9]{20,}` | API keys | конфиги |
| `BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY` | приватные ключи | случайные коммиты |

```powershell
# Поиск JWT вне .env (.env должен быть в .gitignore — JWT там ОК)
# Используй Grep tool, не PowerShell Select-String — он быстрее в Claude Code
```

Из Claude (через Grep tool):
- Паттерн: `eyJ[A-Za-z0-9_-]{30,}`
- Исключения (whitelist допустимых): `.env`, `*.har`, `Context/`, `doc_project/*.md` (примеры в доках), `appsettings.Development.json` если он в gitignore.
- Любое попадание в `**/*.cs`, `**/*.ts`, `**/*.tsx`, `**/*.json` (production configs), `*.yml` — **🔴 finding**.

#### 1.2 Git history scan

```powershell
# trufflehog копает all branches + all commits
docker run --rm -v ${PWD}:/repo trufflesecurity/trufflehog git file:///repo `
  --json --no-update > .audit-history.json
```

**Проверки**:
- Любой найденный leak → проверь, был ли он **отозван** (для JWT — истёк exp?
  для долгоживущих секретов — был ли rotation?).
- Если активный секрет в history — **`git filter-repo`** + ротация (но это
  destructive — спросить владельца репо перед).

#### 1.3 `.gitignore` audit

```powershell
# Должны игнорироваться
@(".env", ".env.*", "Context/", "*.har", "appsettings.Development.json",
  "*.pfx", "*.pem", "*.key", "secrets.json") | ForEach-Object {
    if (-not (Select-String -Path .gitignore -Pattern $_ -Quiet -ErrorAction SilentlyContinue)) {
        "MISSING in .gitignore: $_"
    }
}
```

#### 1.4 Bundle inspection (frontend)

```powershell
# Frontend bundle — VITE_VISARY_API_TOKEN встраивается на build (см. R1).
# Если bundle отдаётся пользователям prod-окружения — это leak.
docker run --rm kilo-import-frontend sh -c "find /app -name '*.js' -exec grep -l 'eyJ' {} \;"
```

Финдинг: любой `.js` в финальном bundle с pattern `eyJ` = **🔴 Critical, token disclosure**.

---

### Этап 2 — Dependencies (supply chain)

#### 2.1 .NET

```powershell
dotnet restore
dotnet list package --vulnerable --include-transitive 2>&1 |
  Out-File .audit-dotnet-vuln.txt
dotnet list package --deprecated 2>&1 | Out-File .audit-dotnet-deprecated.txt
dotnet list package --outdated 2>&1 | Out-File .audit-dotnet-outdated.txt
```

**Особое внимание**:
- `Newtonsoft.Json` (если есть) — `TypeNameHandling != None` = RCE.
- `Microsoft.AspNetCore.*` < 8 — устаревшие.
- `ClosedXML` — известны CVE про zip-bomb; проверь актуальную версию.
- `PDFsharp` 6.2.4 — лицензия MIT, см. [74](./74-import-pdf-export.md); проверить CVE
  на конкретную версию.
- `Npgsql` — версия с поддержкой SCRAM-SHA-256, не plaintext password.

#### 2.2 Frontend

```powershell
Set-Location KiloImportService.Web
npm ci
npm audit --json | Out-File ../.audit-npm.json
npm outdated --json | Out-File ../.audit-npm-outdated.json
Set-Location ..
```

**Особое внимание**:
- `@alfalab/core-components*` — внутренний пакет банка; проверить, что версии
  пинятся, нет `latest`.
- `react`/`react-dom` — 18 vs 19, нет ли смешения.
- `vite` — Dev-сервер выставлен на 5173 (`docker-compose.yml`); проверь, что
  prod НЕ запускается через `vite dev` (использует `vite preview` или статика
  через nginx).

#### 2.3 Lock files

- `package-lock.json` — закоммичен? (`git ls-files | findstr package-lock`).
- `*.csproj` — все версии пинятся? (нет `1.*`, `[1.0,)`).
- `nuget.config` — нет приватных feed'ов с creds в URL?

#### 2.4 Docker base images

```powershell
docker scout cves kilo-import-backend 2>&1 | Out-File .audit-backend-image.txt
docker scout cves kilo-import-frontend 2>&1 | Out-File .audit-frontend-image.txt
docker scout cves postgres:16-alpine 2>&1 | Out-File .audit-postgres-image.txt
```

или `trivy image kilo-import-backend`.

---

### Этап 3 — Backend code (.NET) — OWASP-mapping

Для каждого подэтапа: **Grep по паттернам** + **ручное чтение** обнаруженных мест.

#### 3.1 Injection (SQL, command, LDAP, etc.)

**SQL injection** в EF Core:

- Grep: `FromSqlRaw`, `ExecuteSqlRaw`, `ExecuteSqlInterpolated`, `EF.Functions.Like`
- Любой `FromSqlRaw($"…{userInput}…")` — **🔴 Critical**.
- `FromSqlInterpolated` ОК (parameterized под капотом), но проверь, что
  внутри не `String.Format` сначала.

**Command injection**:

- Grep: `Process.Start`, `ProcessStartInfo`, `System.Diagnostics.Process`
- Любой вызов с user-input — **🔴 Critical**.

**Path injection / Path traversal**:

- Grep: `Path.Combine`, `File.Open`, `File.ReadAllBytes`, `new FileStream`
- Везде, где есть user-input в пути — проверь:
  - Является ли путь канонизированным (`Path.GetFullPath`)?
  - Проверяется ли, что результат внутри разрешённого корня (`.StartsWith(rootDir)`)?

```csharp
// ❌ ОПАСНО
var path = Path.Combine(uploadDir, fileName);  // fileName="../../etc/passwd"
File.WriteAllBytes(path, content);

// ✅ БЕЗОПАСНО
var fullPath = Path.GetFullPath(Path.Combine(uploadDir, fileName));
if (!fullPath.StartsWith(Path.GetFullPath(uploadDir), StringComparison.Ordinal))
    throw new SecurityException("Path traversal");
```

#### 3.2 Broken AuthN/AuthZ

- Grep: `[Authorize]`, `[AllowAnonymous]`, `services.AddAuthentication`
- **Главный вопрос**: есть ли вообще auth на backend?
  - Если нет (доверенная корп. сеть) — **🟡 Medium**, задокументировать как принятый риск.
  - Если есть — проверить:
    - Все endpoints в `Controllers/` имеют `[Authorize]` или явный `[AllowAnonymous]`?
    - SignalR hubs (`/hubs/imports`) проверяют user в `JoinSession`?
- Проверь `Program.cs` / `Startup.cs`: middleware order — `UseAuthentication` ДО `UseAuthorization`.

#### 3.3 SSRF

См. R3. Дополнительно:

- Grep: `HttpClient`, `WebClient`, `new Uri(`
- Любой URL, в который попадает user-input — проверь:
  - Domain в allowlist? (`Visary__BaseUrl` из конфига — OK, user-input — bad)
  - Нет path traversal в URL?

#### 3.4 XXE / XML attacks

- Grep: `XmlReader`, `XmlDocument`, `XDocument.Load`, `XmlSerializer`
- Любой `XmlReader` без `XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }` — **🟡 Medium**.
- ClosedXML под капотом использует XML — проверь, какая версия и есть ли CVE.

#### 3.5 Deserialization

- Grep: `JsonConvert.DeserializeObject`, `BinaryFormatter`, `XmlSerializer`,
  `DataContractSerializer`, `TypeNameHandling`
- `BinaryFormatter` — **🔴 Critical RCE**, должно быть запрещено в .NET 10 (deprecated).
- `Newtonsoft.Json` с `TypeNameHandling.All` / `Auto` / `Objects` — **🔴 Critical RCE**.
- `System.Text.Json` (по умолчанию используется в проекте) — безопасен, но
  проверь custom `JsonConverter`'ы.

#### 3.6 Sensitive data exposure

- Grep по логированию: `_log.LogInformation`, `_log.LogDebug` → ищи `token`, `password`, `secret`, `authorization`
- Проверь HTTP-логирование middleware: не логируется ли `Authorization` header целиком?
- Проверь `VisaryHttpBase` — выводится ли `Bearer …` в логах при 4xx/5xx ответах?
  ([54-visary-token-hot-reload.md](./54-visary-token-hot-reload.md) упоминает hot-reload —
  проверь, не пишется ли токен в логи при reload.)

#### 3.7 CORS / CSRF

- Grep: `AddCors`, `UseCors`, `AllowAnyOrigin`, `AllowCredentials`
- `AllowAnyOrigin() + AllowCredentials()` — **запрещённая** комбинация, ASP.NET даже не запустится.
- `AllowAnyOrigin()` для всех endpoints — **🟡 Medium** (если auth есть).
- CSRF: для cookie-based auth обязательны antiforgery tokens. Для JWT в headers —
  CSRF не применим (но проверь, что нет смешанной схемы).

#### 3.8 Logging & monitoring

- Есть ли `Microsoft.AspNetCore.HttpLogging`? Если включён — какой `LoggingFields`?
  `RequestHeaders` без redact'a → утечка `Authorization`.
- ProblemDetails / exceptions — проверь, что в prod не отдаётся stack trace
  (`UseDeveloperExceptionPage` только в dev).

#### 3.9 Race conditions / TOCTOU

- Grep: `static.*Dictionary`, `Singleton`, `lazy(?!_).*ThreadSafetyMode`
- Singleton-сервисы с mutable state — проверь thread-safety:
  - `ProjectsCacheService` — concurrent reads/writes к кэшу
  - `CancellationRegistry` ([16-import-cancellation.md](./16-import-cancellation.md))
  - `FontResolver` lock ([74](./74-import-pdf-export.md))

#### 3.10 DoS

- Grep: `app.Use(.*body|MaxRequestBody|FormOptions|UploadLimits|RequestLimits`)
- Лимит размера upload в `Startup.cs` / `Program.cs`? Дефолт ASP.NET — 30 MB,
  но XLSX-бюджет может быть больше — проверь, какой лимит выставлен.
- Лимиты на количество строк в XLSX? Парсер падает на 1M-строчных файлах?
- SignalR: ConnectionTimeout, KeepAliveInterval, max concurrent connections?

#### 3.11 Timing attacks

- Grep: `==` где сравниваются токены/хэши, `String.Equals`
- Должно быть `CryptographicOperations.FixedTimeEquals` для security-sensitive
  string comparison.

---

### Этап 4 — Frontend (React / TS / Vite)

#### 4.1 XSS sinks

- Grep: `dangerouslySetInnerHTML`, `innerHTML`, `document.write`, `eval`, `new Function`
- Любое попадание — **🔴 ручная проверка**. Если данные из user-input или backend
  response — должны быть санитизированы (DOMPurify).
- React auto-escape для `{value}` — безопасен, **но** для `href={url}` — проверь
  `javascript:` scheme (открытый редирект).

#### 4.2 Token storage

См. R1. Дополнительно:

- Grep по `KiloImportService.Web/src/`: `localStorage`, `sessionStorage`, `document.cookie`
- Любое хранение JWT в `localStorage` — **🟡 Medium** (susceptible to XSS theft).
- Если JWT в `httpOnly cookie` — лучше, но проверь `Secure; SameSite=Strict`.
- Проверь, **есть ли вообще auth на frontend** или backend trusts любого клиента
  на `localhost:5173`. Если второе — задокументировать как принятый риск для
  внутреннего инструмента.

#### 4.3 CSP

- Проверь `index.html`: есть ли `<meta http-equiv="Content-Security-Policy">`?
- Если нет — **🟡 Medium**, добавить как минимум `default-src 'self'`.

#### 4.4 Open redirects

- Grep: `window.location =`, `location.href =`, `<a href={` с user-input
- Любой redirect, контролируемый параметрами URL — проверь allowlist доменов.

#### 4.5 Dependencies (повтор п. 2.2)

- `npm audit` высокого/критичного уровня — обязательно фиксить.

---

### Этап 5 — Database (PostgreSQL)

#### 5.1 Users & privileges

```powershell
# Подключиться к контейнеру и проверить роли
docker exec -it kilo-import-pg-service psql -U postgres -c "\du"
```

- Backend должен подключаться **не от** `postgres` (superuser), а от выделенной
  роли с минимальными правами (SELECT/INSERT/UPDATE/DELETE на свои таблицы).
- Если используется `postgres/postgres` (default) — **🔴 Critical**.

#### 5.2 Connection strings

- Не в коде, только в env / `.env`.
- `Pgpass` / `appsettings.Development.json` — в `.gitignore`.

#### 5.3 SSL

- Если backend и Postgres в одном Docker-сетевом namespace — SSL опционален.
- Если cross-host — `Ssl Mode=Require` в connection string.

#### 5.4 Backups

- Volume `pg-service-data` — backup есть? Куда складывается? Шифруется?
- Это **organizational**, не code-level, но задокументировать.

#### 5.5 Logging

- `log_statement = 'all'` в Postgres = все запросы в логах, включая параметры.
  Для prod — `log_statement = 'ddl'` или `'mod'`.

---

### Этап 6 — Docker / Infrastructure

#### 6.1 Dockerfile review

```powershell
Get-Content KiloImportService.Api/Dockerfile, KiloImportService.Web/Dockerfile
```

Чек-лист:

- [ ] `USER` non-root в финальном stage (не `root`).
- [ ] Multi-stage build — credentials из build-stage не утекают в final.
- [ ] `COPY --chown=…` для нужных файлов.
- [ ] `HEALTHCHECK` определён.
- [ ] Базовый образ — конкретная версия (`8.0.10-alpine`), не `latest`.
- [ ] Нет `ADD <url>` (только COPY локальных файлов).
- [ ] `ENV` не содержит секретов (только дефолты, реальные — через `--env-file`).

#### 6.2 docker-compose.yml

```powershell
Get-Content docker-compose.yml
```

Чек-лист:

- [ ] Нет `privileged: true`.
- [ ] Нет `/var/run/docker.sock` mount (docker-socket = root на host).
- [ ] Нет `network_mode: host` (только bridge).
- [ ] `restart: unless-stopped` или `on-failure`, не `always` без лимита.
- [ ] `mem_limit` / `cpus` определены для DoS protection.
- [ ] Postgres порты выставлены только на `127.0.0.1`, не `0.0.0.0` (см. текущий
      `0.0.0.0:5433->5432/tcp` — для prod это **🔴 Critical**, для dev — OK).
- [ ] Backend `5000` — за reverse proxy в prod, не напрямую публично.
- [ ] env-файлы в `env_file:` секции — не закоммичены.

#### 6.3 Volumes

- `import-files` — где физически? Доступ из host = доступ к загруженным
  пользовательскими XLSX (могут содержать PII).
- Volume permissions — owned by non-root user в контейнере?

#### 6.4 Network

- Все 4 контейнера в одной сети `kilo-net` — backend может достучаться до
  обеих БД, **frontend** не должен иметь сетевого доступа к БД (но в Docker
  Compose default — все в одной сети).
- Опционально разделить на `frontend-net` (frontend ↔ backend) и `backend-net`
  (backend ↔ postgres).

---

### Этап 7 — Visary integration

#### 7.1 Bearer transport

- Grep: `BearerToken`, `Authorization`
- Проверка: `Visary__BaseUrl` всегда `https://`, не `http://`.
- Проверка `HttpClient` configuration: TLS 1.2+ (.NET 10 default OK).
- Проверка revocation: `ServicePointManager.CheckCertificateRevocationList = true`
  (см. [19-net10-deployment-gotchas.md](./19-net10-deployment-gotchas.md)).

#### 7.2 Token rotation

- Текущая практика — ручная замена JWT в `.env` (см. история этой сессии).
- Token TTL: `exp - nbf` ≈ 1 час по примерам в `.env`. **Это короткий TTL** — это хорошо.
- **Вопрос аудита**: что происходит при истечении токена в середине long-running
  import? Backend ретраит / прокидывает 401? См. [54](./54-visary-token-hot-reload.md).

#### 7.3 Error leakage

- Grep: `result.Content`, `response.Content.ReadAsStringAsync` в catch-блоках
- Если Visary 4xx-ответ ретранслируется клиенту дословно — может утечь
  внутренняя информация (имена полей, URL, версии).

#### 7.4 Audit log

- Каждый CREATE/PATCH в Visary логируется на backend? (см. `_log.LogInformation`
  в `CrudClient`). Это нужно для **forensics** при инциденте.

---

## 🧰 Дополнительные техники

### Static analysis configs

Добавь в репо (если ещё нет):

**`.semgrepignore`**:
```
node_modules/
bin/
obj/
KiloImportService.Web/dist/
Context/
*.har
```

**`semgrep --config p/owasp-top-ten --config p/csharp --config p/typescript --config p/react`** — пресеты OWASP + языкоспецифичные.

### Dynamic analysis (опционально)

После того как backend работает локально:

```powershell
# ZAP baseline scan против работающего backend
docker run --rm -v ${PWD}:/zap/wrk owasp/zap2docker-stable zap-baseline.py `
  -t http://host.docker.internal:5000 -r zap-baseline.html
```

### Fuzzing XLSX-parser

Создай в `KiloImportService.Api.Tests/`:
- 1MB zip-bomb (.xlsx с 1 ячейкой, decompresses в 1 GB)
- XLSX с XXE-payload в shared strings
- XLSX с external links на `file://`, `http://localhost`, `\\smb-share`
- XLSX с тысячью листов
- XLSX с формулами с тысячами вложений

Все должны или парситься безопасно, или давать `file-level error`, **не падать
с unhandled exception**.

---

## 📊 Severity rubric (CVSS-lite)

Используй для классификации findings:

| Severity | Критерий | Пример |
|---|---|---|
| 🔴 **Critical** | RCE / mass data leak / auth bypass без user-interaction | SQL injection в публичном endpoint; live JWT в публичном репо |
| 🟠 **High** | RCE с user-interaction / leak секретов / privesc | XXE в XLSX-parser; `BinaryFormatter` deserialize |
| 🟡 **Medium** | XSS / SSRF (контролируемый) / DoS / weak crypto | XSS в admin-only UI; missing CSP; `==` для секретов |
| 🟢 **Low** | info disclosure без секретов / hardening miss | stack trace в 500; verbose error messages |
| ⚪ **Info** | best-practice deviation без impact | устаревший пакет без CVE; неоптимальная конфигурация |

### Triage rule

- **Critical** → блокирует релиз; fix immediately + ротация скомпрометированных секретов.
- **High** → fix в течение sprint'a; включить в release notes.
- **Medium** → создать ticket, fix в течение месяца.
- **Low/Info** → backlog.

---

## 📝 Шаблон отчёта об уязвимости

Для каждой находки используй такой формат (по `doc.md` стилю):

```markdown
## [SEV-XXX] Краткое название (Severity)

### 📋 Описание
Одно-два предложения: что нашли и почему это важно.

### 📍 Локация
- Файл: `path/to/file.cs:42-55`
- Функция: `ClassName.MethodName`
- Версия: commit `<sha>`

### 🎯 Impact
- **Атакующий**: A-ext / A-user / A-dep / A-ins / A-file
- **Что получает**: RCE / data leak / privesc / DoS / …
- **На какой актив**: A1 (Bearer Visary) / A2 (XLSX files) / …

### 🔬 Reproduction
\`\`\`
шаги или PoC-команда
\`\`\`

### ✅ Recommended fix
\`\`\`csharp
// before
var path = Path.Combine(root, userFile);

// after
var fullPath = Path.GetFullPath(Path.Combine(root, userFile));
if (!fullPath.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal))
    throw new SecurityException("path traversal");
\`\`\`

### 🧪 Verification
- Тест, который ловит регрессию: `Tests/Security/PathTraversalTests.cs`
- Команда: `dotnet test --filter FullyQualifiedName~PathTraversal`

### 🔗 References
- CWE-22 (Path Traversal)
- OWASP: …
- Внутренние доки: [N-…](./N-…)
```

---

## 🎯 Чек-лист закрытия аудита

- [ ] **Этап 1**: gitleaks + trufflehog отработали, все findings triaged
- [ ] **Этап 1**: `Context/` и `*.har` в `.gitignore`
- [ ] **Этап 1**: frontend bundle проверен на наличие `eyJ` pattern
- [ ] **Этап 2**: `dotnet list package --vulnerable` — пусто или все триажены
- [ ] **Этап 2**: `npm audit` — high/critical = 0
- [ ] **Этап 2**: `trivy image` / `docker scout cves` для всех образов
- [ ] **Этап 3**: все 11 OWASP-подэтапов пройдены
- [ ] **Этап 4**: frontend XSS sinks + token storage проверены
- [ ] **Этап 4**: CSP заголовок добавлен (или принятый риск задокументирован)
- [ ] **Этап 5**: Postgres user НЕ superuser
- [ ] **Этап 5**: connection strings не в коде
- [ ] **Этап 6**: Dockerfile — non-root USER
- [ ] **Этап 6**: `docker-compose.yml` — нет `privileged`/`docker.sock`
- [ ] **Этап 7**: Bearer всегда HTTPS, не утекает в логи/errors
- [ ] **Этап 7**: SignalR `JoinSession` проверяет ownership
- [ ] **Project-specific** R1-R10 — каждый явно отмечен «ОК» или «finding» в отчёте
- [ ] Отчёт `audit-{YYYY-MM-DD}.md` создан в `doc_project/audits/` (или ином месте)
- [ ] Все Critical/High findings — issue/ticket создан
- [ ] Если найдены активные секреты в git history — спрошен владелец репо
      о `filter-repo` + rotation

---

## ❌ Типичные ошибки при аудите

```text
НЕПРАВИЛЬНО: пробежаться по чек-листу OWASP Top 10 и закрыть.
ПРАВИЛЬНО:   project-specific риск-зоны (R1-R10) сначала, OWASP — потом как safety net.
ВЫВОД:       generic-чек-листы пропускают то, что специфично для именно этого репо.
```

```text
НЕПРАВИЛЬНО: gitleaks нашёл «секрет», пометили finding.
ПРАВИЛЬНО:   gitleaks нашёл — проверь exp/rotation/whitelist. Часть «секретов» —
             expired JWT, мокинг, примеры в доках.
ВЫВОД:       false-positives засоряют отчёт и снижают доверие к остальным findings.
```

```text
НЕПРАВИЛЬНО: «нет [Authorize] → critical».
ПРАВИЛЬНО:   проверь архитектурный контекст: backend за reverse proxy в корп.
             сети с network-level ACL — отсутствие app-level auth может быть
             принятым риском. Задокументируй это как explicit assumption.
ВЫВОД:       severity = impact × likelihood × exposure, не только impact.
```

```text
НЕПРАВИЛЬНО: запушить .audit-secrets.json в репо.
ПРАВИЛЬНО:   .audit-*.json — в .gitignore. Сами findings — в приватном отчёте.
ВЫВОД:       audit-артефакты сами по себе содержат потенциальные leaks.
```

```text
НЕПРАВИЛЬНО: «memory говорит, что токен в .env — значит OK, finding-а нет».
ПРАВИЛЬНО:   memory — это контекст, не оправдание. Если memory описывает риск —
             отметь его в отчёте явно, чтобы будущие сессии знали, что он осознан.
ВЫВОД:       аудит — independent re-verification, не trust-the-cache.
```

---

## 📍 Применение в проекте — карта файлов для аудита

| Слой | Где смотреть | Какие риски | Связанная дока |
|---|---|---|---|
| Visary client | `Visary.Api.Client/**` | R3 (SSRF), R7 (logging) | [50](./50-visary-api-new-methods.md), [55](./55-visary-proxy-controllers.md) |
| Backend | `KiloImportService.Api/**` | injection, XXE, deserialization, auth | [12](./12-ef-core-migrations.md), [17](./17-backend-tests-xunit.md) |
| XLSX parser | `KiloImportService.Api/Domain/Importing/Parsers/**` | R4 (XXE/Zip-Slip/external links) | [81](./81-xlsx-external-links-strip.md), [88](./88-xlsx-skip-hidden-sheets.md) |
| File upload | `KiloImportService.Api/Domain/.../*Upload*` | R5 (path traversal) | [82](./82-visary-file-storage-upload.md) |
| SignalR | `KiloImportService.Api/Hubs/**` | R6 (broken authZ) | [15](./15-signalr-progress.md) |
| StagedRows / jsonb | `KiloImportService.Api/Data/**` + миграции | R7 (jsonb → XSS sink) | [89](./89-mappedrow-sheet-invariant.md) |
| PDF export | `KiloImportService.Api/Domain/.../*Pdf*` | R9 (FontResolver race) | [74](./74-import-pdf-export.md) |
| Frontend | `KiloImportService.Web/src/**` | XSS, token storage | [13](./13-vite-proxy-backend.md), [54](./54-visary-token-hot-reload.md) |
| Frontend bundle | `KiloImportService.Web/dist/` (build artifact) | R1 (JWT в bundle) | [54](./54-visary-token-hot-reload.md) |
| Docker | `docker-compose.yml`, `*/Dockerfile` | privileged, exposed ports, secrets in env | [33](./33-docker-cli-troubleshooting.md), [35](./35-run-through-docker-ui.md) |
| Configs | `.env`, `appsettings*.json` | secret leak | [54](./54-visary-token-hot-reload.md) |
| History | `git log --all`, `Context/` | secret leak в прошлых коммитах | R2 |

---

## 🚀 Запуск аудита Claude-агентом

Когда пользователь говорит «проведи security audit / просмотри проект на уязвимости /
сделай аудит безопасности»:

1. **Прочитай этот документ** (`93-security-audit-workflow.md`) целиком.
2. **Создай TodoWrite** с этапами 1-7 + closure.
3. **Сначала R1-R10** (project-specific) — они приоритетнее generic-чек-листа.
4. **Параллелизуй** независимые проверки через `Agent` subagent_type=Explore
   (например, отдельный агент на gitleaks-result analysis, отдельный — на
   .NET vuln packages, отдельный — на frontend XSS sinks).
5. **Не выполняй destructive операций** без подтверждения:
   - `git filter-repo` для удаления секретов
   - изменение `.env` / ротация токенов
   - снос volume'ов
6. **Финальный отчёт** — `doc_project/audits/audit-{YYYY-MM-DD}-{branch}.md`,
   используя шаблон из этого документа.
7. **При нахождении активного секрета** (живой JWT, не expired) — **немедленно
   остановись и сообщи владельцу**, не предпринимай автоматических действий.
