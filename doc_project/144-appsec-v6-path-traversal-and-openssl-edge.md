# 🛡️ appsec_v6 — Path Traversal в LocalFileStorage + edge-fallback для OpenSSL

## 📋 Описание

`appsec_v6.txt` (2026-06-22) поднял **4 находки** на образе
`alfa-building-docker-snapshots.binary.alfabank.ru/kilo-import-api:v0.0.1.45-SNAPSHOT`:

| # | Тип | Файл / Пакет | CWE / CVE |
|---|---|---|---|
| 1 | `file-taint-low` (Path Traversal) | `KiloImportService.Api/Domain/Pipeline/IFileStorage.cs` | CWE-22 |
| 2 | `file-taint` (Path Traversal) | то же | CWE-22 |
| 3 | OpenSSL Heap Use-After-Free | `libcrypto3@3.5.6-r0` (alpine 3.23.4) | CVE-2026-45447 |
| 4 | (то же) | `libssl3@3.5.6-r0` | CVE-2026-45447 |

Находки #1/#2 — одна реальная уязвимость, удвоенная сканером (low+normal).
Находки #3/#4 — одна CVE на двух системных пакетах (libcrypto+libssl).

---

## 🩹 Часть 1. Path Traversal в `LocalFileStorage`

### 🔍 Поверхность атаки

Маршрут данных:

```
HTTP multipart upload (IFormFile.FileName) →
    ImportPipeline.UploadAsync(fileName) →
        IFileStorage.SaveAsync(stream, originalFileName, ct) →
            LocalFileStorage сохраняет в {root}/{yyyy}/{MM}/{dd}/{guid}_{Sanitize(originalFileName)}
                ↓ возвращает rel
            БД (FileSnapshot.RelativePath)
                ↓ позже...
        IFileStorage.OpenReadAsync(relativePath, ct) → File.OpenRead(Path.Combine(root, rel))
```

**Атакующий контролирует `originalFileName`** — это поле `IFormFile.FileName`
из HTTP-запроса, оно произвольная строка.

### ❌ Старая реализация — две дыры

```csharp
// SaveAsync — старая версия
var rel = Path.Combine(now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"),
    $"{Guid.NewGuid():N}_{Sanitize(originalFileName)}");
var full = Path.Combine(_root, rel);
Directory.CreateDirectory(Path.GetDirectoryName(full)!);
await using var fs = File.Create(full);

private static string Sanitize(string name)
{
    foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
    return name;
}
```

Проблема #1: `Path.GetInvalidFileNameChars()` на **Linux** содержит ТОЛЬКО
`'\0'` и `'/'`. Символ `'\'` (backslash) **НЕ входит** в invalid chars Linux —
он считается обычным печатным символом для имени файла.

Атакующий шлёт `Content-Disposition: form-data; name="file"; filename="..\..\..\etc\passwd"`.
На Linux после Sanitize строка проходит как есть → попадает в `Path.Combine` →
финальный путь резолвится за пределы `/var/lib/import-files`.

```csharp
// OpenReadAsync — старая версия
public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct)
{
    var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    return Task.FromResult<Stream>(File.OpenRead(full));
}
```

Проблема #2: вообще никакой валидации `relativePath`. Если в БД попадёт
`../../etc/shadow` или абсолютный `/etc/shadow` — `File.OpenRead` его откроет.
Сейчас БД пишется только из `SaveAsync`, но defense-in-depth требует
не доверять собственным сохранённым данным.

### ✅ Правильная реализация (v1.0)

3-слойная защита:

1. **`Sanitize` режет path-составляющие через `Path.GetFileName`** (кросс-OS):
   ```csharp
   private static string Sanitize(string name)
   {
       // Шаг 1 — режем path-составляющие (кросс-OS). На Linux '\' НЕ invalid char,
       // и старая реализация пропускала '..\..\..\evil'.
       name = Path.GetFileName(name ?? string.Empty);
       if (string.IsNullOrWhiteSpace(name)) name = "unnamed";
       // Шаг 2 — стандартные invalid chars текущей OS (<>:"|?* и т.п.).
       foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
       // Шаг 3 — явный страховой replace кросс-OS path-разделителей и NUL.
       name = name.Replace('/', '_').Replace('\\', '_').Replace('\0', '_');
       return name;
   }
   ```

2. **`OpenReadAsync` отвергает абсолютные пути и любые `..` ДО `Path.Combine`**:
   ```csharp
   if (string.IsNullOrWhiteSpace(relativePath))
       throw new ArgumentException("relativePath required", nameof(relativePath));
   if (Path.IsPathRooted(relativePath)
       || relativePath.Contains("..", StringComparison.Ordinal))
   {
       throw new UnauthorizedAccessException("Relative path is not allowed.");
   }
   ```

3. **Оба метода после `Path.Combine` резолвят `Path.GetFullPath`
   и проверяют containment**:
   ```csharp
   var full = Path.GetFullPath(combined);
   if (!full.StartsWith(_rootFull, StringComparison.Ordinal))
       throw new UnauthorizedAccessException("Resolved file path is outside the storage root.");
   ```

   Где `_rootFull` — canonical absolute path с trailing `DirectorySeparator`:
   ```csharp
   _rootFull = EnsureTrailingSeparator(Path.GetFullPath(configured));
   ```

   **Trailing separator важен!** Без него `_root=/var/lib/import-files`
   проходит StartsWith-тест на `/var/lib/import-files-evil/secret.txt`
   (соседний каталог с похожим префиксом).

### ⚠️ Важно

1. **`Path.GetFullPath` нормализует `..`** и резолвит symlinks. На Linux/Windows
   результат — canonical absolute path. Это даёт надёжное containment-сравнение.
2. **`_rootFull` фиксируется ОДИН РАЗ в конструкторе.** Это устойчиво к смене
   рабочей директории процесса между запросами.
3. **Containment-check бросает `UnauthorizedAccessException`** — best-fit
   стандартное исключение, чтобы ASP.NET error-handler конвертировал в 403.
4. **Пустой/whitespace `relativePath` → `ArgumentException`** (программерская
   ошибка, не безопасность; но без проверки `Path.Combine` молча отработает
   с `_root` и `File.OpenRead` упадёт с разными ошибками в разных OS).
5. **`OpenReadAsync` отвергает СОДЕРЖАНИЕ `..`, а не только начало.** Кейсы
   типа `subdir/../../../etc/passwd` тоже отлавливаются. После нормализации
   через `Path.GetFullPath` containment-check всё равно бы поймал, но
   defense-in-depth: ранний reject понятнее в логе.
6. **GUID-префикс был частичной защитой, не полной.** Хотя `{Guid:N}_` гасит
   попытки переписать произвольный файл (имя файла всегда начинается с GUID),
   он не защищает от записи в директорию выше корня — если path-разделители
   пройдут Sanitize, `Path.Combine` уйдёт в `/etc/passwd_..._file`.

### ❌ Чего НЕ делать

#### ❌ Полагаться только на `GetInvalidFileNameChars`

```csharp
// НЕПРАВИЛЬНО — на Linux '\' пропускается (там invalid только '\0' и '/').
foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
```

Нужен ещё `Path.GetFileName` (вырезает любые path-составляющие до сегмента имени).

#### ❌ StartsWith без trailing separator

```csharp
// НЕПРАВИЛЬНО — соседний каталог с похожим префиксом проходит:
//   _root=/var/lib/import-files
//   full =/var/lib/import-files-evil/secret.txt — StartsWith returns true!
if (!full.StartsWith(_root, StringComparison.Ordinal)) throw ...;
```

Добавь `Path.DirectorySeparatorChar` в конце `_root`.

#### ❌ Whitelist символов вместо `Path.GetFileName`

```csharp
// НЕПРАВИЛЬНО для нашего сценария — ломает легитимные имена с кириллицей,
// пробелами, скобками, апострофами и т.п.
if (!Regex.IsMatch(name, @"^[a-zA-Z0-9._-]+$")) throw ...;
```

У нас имена файлов XLSX — кириллические, с пробелами, точками. Whitelist
блокирует половину легитимных загрузок. `Path.GetFileName` достаточно —
он вырезает только path-разделители, оставляя имя как было.

#### ❌ Чтение пути из HTTP в `OpenReadAsync`

Сейчас `relativePath` приходит из БД (`FileSnapshot.RelativePath`), который
сохранён через `SaveAsync`. Это **относительно** доверенный путь, но
defense-in-depth требует валидации (см. реализацию). Если когда-нибудь
появится endpoint, где `relativePath` приходит из query/route — он
автоматически будет защищён.

---

## 🩹 Часть 2. CVE-2026-45447 — OpenSSL PKCS#7 use-after-free

### 🔍 Что показал AppSec_v6

```
openssl: Heap Use-After-Free in OpenSSL PKCS7_verify()
KILO-IMPORT-API:V0.0.1.45-SNAPSHOT (alpine 3.23.4)
libssl3@3.5.6-r0  → CVE-2026-45447 → нужна 3.5.7-r0
```

**База обновлена до Alpine 3.23.4** (это работа doc 137 v1.6 / doc 143 —
переход с `10.0-preview-alpine` на GA-тег `10.0-alpine`). `apk upgrade`
из runtime-stage подтянул `libcrypto3=3.5.6-r0`. Но в `latest-stable`
Alpine 3.23.4 это **максимальная доступная версия**: апстрим OpenSSL
выпустил 3.5.7 (CVE-fix) только недавно, Alpine ещё не успел сделать
бэкпорт в стабильную ветку.

Verify-блок doc 137 v1.5 это **видит** и печатает баннер:

```
############################################################
# WARNING: libcrypto3=3.5.6-r0 < 3.5.7-r0
# CVE-2026-45447 (PKCS#7 use-after-free, потенц. RCE)       #
# ОСТАЁТСЯ в образе для libcrypto3 + libssl3.               #
# DevOps action — Alpine-ветка корп. mirror устарела:       #
#  /etc/alpine-release: 3.23.4
############################################################
```

Но баннер сам ничего не лечит — нужен механизм точечно подтянуть
3.5.7-r0 из ветки, где он уже есть.

### ✅ Правильная реализация (v1.7) — точечный edge-fallback

```dockerfile
ARG ALPINE_OPENSSL_EDGE_FALLBACK="false"
ARG ALPINE_EDGE_REPO_URL="https://dl-cdn.alpinelinux.org/alpine/edge/main"
RUN if [ "${ALPINE_OPENSSL_EDGE_FALLBACK}" = "true" ]; then \
        CRYPTO_VER=$(apk info -v libcrypto3 2>/dev/null | head -1 | sed -E 's/^libcrypto3-//'); \
        MIN_VER="3.5.7-r0"; \
        LOWER=$(printf '%s\n%s\n' "$CRYPTO_VER" "$MIN_VER" | sort -V | head -1); \
        if [ "$LOWER" = "$MIN_VER" ]; then \
            echo "INFO: edge-fallback включён, но libcrypto3=$CRYPTO_VER уже >= $MIN_VER — пропускаем"; \
        else \
            echo "INFO: edge-fallback — apk add --repository=${ALPINE_EDGE_REPO_URL} libcrypto3 libssl3 (точечный)"; \
            if apk add --no-cache --upgrade --repository="${ALPINE_EDGE_REPO_URL}" libcrypto3 libssl3 2>&1; then \
                NEW_VER=$(apk info -v libcrypto3 2>/dev/null | head -1 | sed -E 's/^libcrypto3-//'); \
                NEW_LOWER=$(printf '%s\n%s\n' "$NEW_VER" "$MIN_VER" | sort -V | head -1); \
                if [ "$NEW_LOWER" = "$MIN_VER" ]; then \
                    echo "OK: edge-fallback — libcrypto3=$NEW_VER >= $MIN_VER, CVE-2026-45447 закрыт"; \
                else \
                    echo "WARN: edge-fallback выполнился, но libcrypto3=$NEW_VER всё ещё < $MIN_VER"; \
                fi; \
            else \
                echo "WARN: edge-fallback apk add упал — outbound к ${ALPINE_EDGE_REPO_URL} закрыт?"; \
            fi; \
        fi; \
    else \
        echo "INFO: edge-fallback ОТКЛЮЧЁН (ALPINE_OPENSSL_EDGE_FALLBACK=false). Если verify-блок выше показал WARN — включить через build-arg."; \
    fi
```

### ⚠️ Важно

1. **`apk add --no-cache --repository=<URL> --upgrade libcrypto3 libssl3` —
   точечная операция.** `--repository=<URL>` действует **только на эту команду**.
   `/etc/apk/repositories` НЕ меняется → другие пакеты из edge не подтягиваются
   ни сейчас, ни в будущем (если кто-то добавит `apk add curl` ниже).
2. **Совместимость edge OpenSSL с GA musl.** Apk проверяет `provides`/`depends`
   при установке. Если edge libcrypto3 требует musl > чем у нас — установка
   упадёт с понятной ошибкой, образ соберётся БЕЗ edge-libcrypto3
   (`if apk add ... fails` → WARN-баннер). Совместимости пакетов мы
   не ломаем.
3. **Дефолт `false`.** Эталонная сборка (doc 137 v1.6 / doc 143) НЕ меняется.
   DevOps включает `ALPINE_OPENSSL_EDGE_FALLBACK=true` явно через build-arg.
4. **Smart-skip — если уже >=3.5.7-r0, пропускаем.** Когда Alpine 3.23.5
   докатит 3.5.7-r0 в `latest-stable` и нашему apk upgrade хватит обычного
   зеркала, edge-fallback увидит «всё уже хорошо» и тихо пропустится
   (не тратит время сборки, не делает лишнего сетевого запроса).
5. **Edge может быть недоступен из Jenkins.** `apk add` с unreachable
   `--repository=` упадёт с DNS/timeout error. Это попадает в `else`-ветку
   и печатает WARN. Сборка не падает (как и в v1.4 — эталон сохраняется).

### ❌ Чего НЕ делать

#### ❌ Добавить edge как permanent репо

```dockerfile
# НЕПРАВИЛЬНО — теперь ЛЮБОЙ apk install/upgrade ниже может подтянуть
# edge-пакеты с несовместимым musl, ломая всю runtime-цепочку.
RUN echo "https://dl-cdn.alpinelinux.org/alpine/edge/main" >> /etc/apk/repositories
RUN apk upgrade --no-cache
```

#### ❌ Переключить ВЕСЬ образ на edge

```dockerfile
# НЕПРАВИЛЬНО — Alpine edge содержит свежий glibc/icu, которые могут быть
# ABI-несовместимы с .NET 10 runtime'ом, скомпилированным под Alpine 3.23.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
RUN sed -i 's|alpine/v3.23|alpine/edge|g' /etc/apk/repositories
RUN apk upgrade --no-cache
```

#### ❌ Pin'ить версию `libcrypto3=3.5.7-r0`

```dockerfile
# НЕПРАВИЛЬНО — через месяц Alpine выпустит 3.5.8-r0, такая pin даст
#   ERROR: package mentioned in index, but not for repository tag
RUN apk add --no-cache --upgrade --repository="..." libcrypto3=3.5.7-r0
```

Используй без `=` — apk возьмёт latest из указанного репо.

#### ❌ Удалять верифай-блок v1.5

```dockerfile
# НЕПРАВИЛЬНО — verify-блок печатает текущую версию libcrypto3 в лог.
# DevOps подтверждает статус CVE-2026-45447 без открывания контейнера.
# Без блока — никаких сигналов в логе, если что-то пойдёт не так.
```

---

## 📍 Применение в проекте

| Файл | Изменение |
|------|-----------|
| [KiloImportService.Api/Domain/Pipeline/IFileStorage.cs](../KiloImportService.Api/Domain/Pipeline/IFileStorage.cs) | `Sanitize` через `Path.GetFileName`, containment-check в `SaveAsync`/`OpenReadAsync`, ранний reject `..`/absolute в `OpenReadAsync`. |
| [KiloImportService.Api.Tests/Pipeline/LocalFileStorageTests.cs](../KiloImportService.Api.Tests/Pipeline/LocalFileStorageTests.cs) | 3 новых `[Theory]`/`[Fact]` на path-traversal payload'ы. |
| [KiloImportService.Api/Dockerfile](../KiloImportService.Api/Dockerfile) | v1.7: опц. edge-fallback `apk add --repository=<edge> libcrypto3 libssl3`. |
| [KiloImportService.Web/Dockerfile](../KiloImportService.Web/Dockerfile) | то же |
| [docker-compose.yml](../docker-compose.yml) | проброс `ALPINE_OPENSSL_EDGE_FALLBACK` + `ALPINE_EDGE_REPO_URL` в backend и frontend (default `false`). |
| [.env.example](../.env.example), [.env.preprod.example](../.env.preprod.example), [.env.prod.example](../.env.prod.example) | закомментированные примеры новых build-args. |
| [doc 137 v1.6](./137-appsec-v4-alpine-package-vulnerabilities.md), [doc 143](./143-appsec-v5-base-image-upgrade.md) | apk-upgrade блок + verify-блок ОСТАЮТСЯ — defense-in-depth, см. v1.7 как дополнительный слой. |

---

## 🎯 Чек-лист (повторный прогон AppSec — v6)

### Часть 1 — Path Traversal
- [ ] `Sanitize` вызывает `Path.GetFileName` ПЕРЕД `GetInvalidFileNameChars`?
- [ ] `Sanitize` ещё дополнительно `Replace('/', '_').Replace('\\', '_')`?
- [ ] `_rootFull` зафиксирован через `Path.GetFullPath` + trailing separator?
- [ ] `SaveAsync` и `OpenReadAsync` оба делают `Path.GetFullPath` + StartsWith-check?
- [ ] `OpenReadAsync` отвергает `IsPathRooted` и `Contains("..")` ДО Combine?
- [ ] Тесты на `..\..\etc\passwd` и `/etc/shadow` проходят?

### Часть 2 — CVE-2026-45447
- [ ] Sanity-check verify-блока в логе сборки — какая версия libcrypto3?
- [ ] Если `OK: libcrypto3=X >= 3.5.7-r0` — ничего делать не надо, CVE закрыт.
- [ ] Если `WARNING: libcrypto3=X < 3.5.7-r0` — включить
      `ALPINE_OPENSSL_EDGE_FALLBACK=true` в Jenkins-build-args.
- [ ] Перепроверить лог сборки — должен быть `OK: edge-fallback —
      libcrypto3=3.5.7-r0 >= 3.5.7-r0, CVE-2026-45447 закрыт`.

---

## 🔗 См. также

- [doc 137 v1.5](./137-appsec-v4-alpine-package-vulnerabilities.md) — apk-upgrade + verify-блок
- [doc 143 v1.6](./143-appsec-v5-base-image-upgrade.md) — base-images upgrade на `10.0-alpine`
- [doc 121](./121-security-fixes-appsec-v1.md) — первая волна AppSec-фиксов
- [doc 93](./93-security-audit-workflow.md) — workflow аудита
