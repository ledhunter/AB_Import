# 🩹 Jenkins build description — `KEY:VALUE` без пробела после `:`

## 📋 Описание

После успешной сборки Docker-образов платформенная система Альфы
(`PlatformArtifactsClient`) валит post-processing:

```
Error during record processing.
[500] during [GET] to [http://artifacts-api/search/by.checksum/v2?checksum=%209ecee1e35a722db2d45cc4145b4ebb268a4bda0d]
[PlatformArtifactsClient#fetchArtifactInfoByChecksum(String)]:
{"timestamp":"2026-06-16T10:01:36.139+00:00","status":500,
 "error":"Internal Server Error",
 "trace":"java.lang.RuntimeException: feign.FeignException$BadRequest: [400 ] during [GET] to [https://binary…"}

Не проставлены параметры в build description сборки или же образ с таким хэшем
уже есть в докер репозитории.
```

Ключ в URL: `checksum=%209ecee1...` — **`%20` это URL-encoded пробел**.
То есть в платформенный запрос ушёл checksum с лидирующим пробелом:
`" 9ecee1e35a722db2d45cc4145b4ebb268a4bda0d"`.

---

## 🔍 Корневая причина

Платформа парсит `currentBuild.description` Jenkins-сборки по шаблону
`KEY:VALUE`, забирая VALUE **без trim**. В нашем Jenkinsfile description
формировался с пробелом после `:`:

```groovy
// БЫЛО — пробел после `:` ломает парсер
descriptionLines << "artifact_app_sha1: ${sha1sum}"
descriptionLines << "dockerImageDigest: ${dockerImageDigest}"
descriptionLines << "version: $version"
descriptionLines << "gitBranche: ${params.branch}"
descriptionLines << "dockerImage: http://..."
```

Платформа делает что-то вроде `value = line.split(':', 2)[1]` (без trim) → в
`value` попадает лидирующий пробел. Затем `URLEncoder.encode(value)` →
пробел становится `%20` → backend artifacts-api получает невалидный
checksum → 400 → 500 wrapping.

Эталон [`service-dev/Jenkinsfile`](../../service-dev-extract/Jenkinsfile)
строго БЕЗ пробелов:

```groovy
gitBranche:${params.branch}\n
version:$version\n
dockerImageDigest:$dockerImageDigest\n
artifact_app_sha1:$sha1sum
```

---

## ✅ Правильная реализация

```groovy
def descriptionLines = []
descriptionLines << "gitBranche:${params.branch}"
descriptionLines << "version:$version"

// per-service блок:
descriptionLines << "dockerImage:http://..."
descriptionLines << "dockerImageDigest:${dockerImageDigest}"
descriptionLines << "artifact_app_sha1:${sha1sum}"

currentBuild.description = descriptionLines.join('\n')
```

### ⚠️ Важно

- **Никаких пробелов после `:`** — ни в одной строке `descriptionLines`.
  Парсер платформы строго `KEY:VALUE`.
- **`sha1sum.trim()`** обязателен (сейчас уже есть, см. `sh(...).trim()`)
  — `sh` возвращает stdout с trailing `\n`, без trim в строку
  description попадёт лишний newline.
- **Заголовочные строки без `:`** (`── ${svc.label} ──`) безопасны —
  парсер просто их игнорирует.
- **Пустые строки-разделители** (`descriptionLines << ''`) безопасны
  — парсер пропускает.

---

## ❌ Чего НЕ делать

### ❌ Пробел после `:`

```groovy
// НЕПРАВИЛЬНО — value получит лидирующий пробел
descriptionLines << "artifact_app_sha1: ${sha1sum}"
```

URL уйдёт как `checksum=%20HASH`, платформа упадёт 500.

### ❌ Trailing whitespace в VALUE

```groovy
// НЕПРАВИЛЬНО — sha1sum без .trim() содержит \n
def sha1sum = sh(script: "...", returnStdout: true)
descriptionLines << "artifact_app_sha1:${sha1sum}"  // "<hash>\n"
```

Хотя эталон тоже без `.trim()`, в нашем коде `.trim()` уже стоит —
сохраняем, это надёжнее (не зависит от того, как платформенный парсер
обрабатывает trailing whitespace).

### ❌ Многострочный VALUE через `\n` в одной строке

```groovy
// НЕПРАВИЛЬНО — парсер увидит KEY:VALUE\nKEY2:VALUE2 как один VALUE
descriptionLines << "artifact_app_sha1:${sha1sum}\ndockerImage:..."
```

Использовать отдельный `descriptionLines <<` для каждого ключа.

---

## 📍 Применение в проекте

| Файл | Что |
|------|-----|
| [Jenkinsfile](../Jenkinsfile) | строки `descriptionLines << "key:value"` (5 ключей) — без пробела после `:` |

---

## 🧪 Подтверждение

Сравнение «было / стало» / эталон:

| Ключ | Было (наше) | Стало (наше) | Эталон service-dev |
|---|---|---|---|
| `gitBranche` | `"gitBranche: ${params.branch}"` | `"gitBranche:${params.branch}"` | `gitBranche:${params.branch}` |
| `version` | `"version: $version"` | `"version:$version"` | `version:$version` |
| `dockerImage` | `"dockerImage: http://..."` | `"dockerImage:http://..."` | (отсутствует — у эталона single-service) |
| `dockerImageDigest` | `"dockerImageDigest: ${dockerImageDigest}"` | `"dockerImageDigest:${dockerImageDigest}"` | `dockerImageDigest:$dockerImageDigest` |
| `artifact_app_sha1` | `"artifact_app_sha1: ${sha1sum}"` | `"artifact_app_sha1:${sha1sum}"` | `artifact_app_sha1:$sha1sum` |

---

## 🎯 Чек-лист (если снова сломалось)

- [ ] В логе платформы фраза `Error during record processing` /
      `fetchArtifactInfoByChecksum` / `Не проставлены параметры в build description`?
- [ ] URL в ошибке содержит `%20`, `%0A`, `%09` (URL-encoded пробел/newline/tab)?
- [ ] В `Jenkinsfile` все `descriptionLines << "KEY:VALUE"` — без пробела
      после `:`?
- [ ] `sh(...).trim()` есть после `returnStdout: true` для значений,
      попадающих в description?

---

## 🔗 См. также

- [doc 131](./131-jenkins-test-pipeline.md) — общий Jenkins-pipeline проекта
- эталон [`service-dev/Jenkinsfile`](../../service-dev-extract/Jenkinsfile) —
  формат `key:value` без пробелов (источник истины)
