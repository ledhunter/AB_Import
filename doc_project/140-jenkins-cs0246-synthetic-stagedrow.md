# 🩹 Jenkins CS0246 `SyntheticStagedRow` — defensive разнесение типов по файлам

## 📋 Описание

Сборка `room_branch` в Jenkins (19 июня) упала с двумя ошибками компиляции:

```
KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs(688,31):
    error CS0246: The type or namespace name 'SyntheticStagedRow' could not be found
KiloImportService.Api/Domain/Mapping/FinModelImportMapper.cs(690,30):
    error CS0246: The type or namespace name 'SyntheticStagedRow' could not be found
```

При этом локальная сборка проходит, и сборка 17 июня (`64333f2 успешная сборка`) на тех же
файлах тоже проходила — `git diff 64333f2..HEAD --stat` показывает, что
`IImportMapper.cs` и `FinModelImportMapper.cs` **между этими коммитами не менялись**.

## 🔍 Диагностика

**Факты по коду:**

| Файл | Где | Что |
|---|---|---|
| `IImportMapper.cs` | строка 144 | `public record SyntheticStagedRow(...)` |
| `FinModelImportMapper.cs` | строки 688, 690 | `List<SyntheticStagedRow>`, `IReadOnlyList<SyntheticStagedRow>` |
| Namespace | оба файла | `KiloImportService.Api.Domain.Mapping` |

Оба файла tracked в git, не в `.gitignore`, не в `.gitattributes`,
не имеют case-sensitivity-конфликтов в именах.

**Сравнение веток (`git show <branch>:<file> | grep -c SyntheticStagedRow`):**

| Branch | IImportMapper.cs | record `SyntheticStagedRow` | usage в FinModelImportMapper |
|---|---|---|---|
| `room_branch` (HEAD) | ✅ | 2× | 4× |
| `main` | ✅ | ❌ 0× | ❌ 0× |
| `my-feature-new` | ✅ | ❌ 0× | ❌ 0× |
| `master`/`fin_branche`/`my-feature` | нет файла | — | — |

В `room_branch` оба файла согласованы. В других ветках либо нет `IImportMapper.cs`
вообще, либо он старый (без `record SyntheticStagedRow`).

**Корневая гипотеза:** [Jenkinsfile:20](../Jenkinsfile#L20) содержит
`defaultValue: 'dev'` для параметра `branch`. Ветки `dev` в `git branch -r` нет.
Если сборка была запущена без явного указания `branch=room_branch`,
возможны два сценария:

1. SCM-плагин получил **inconsistent workspace** (свежий `FinModelImportMapper.cs`
   подтянутый из `room_branch`, старый `IImportMapper.cs` от другой ветки).
2. `git checkout dev` на Jenkins-агенте с Bitbucket-fetch'ем нашёл какую-то
   stale-ветку с частичным состоянием.

Локальная сборка проходит — `dotnet build` → `Ошибок: 0`, 108 warnings (косметические).
То есть **код корректен**, проблема в окружении Jenkins.

## ✅ Правильная реализация (defense-in-depth)

### 1. Вынести `SyntheticStagedRow` и `RowActionLog` в отдельный файл

```csharp
// KiloImportService.Api/Domain/Mapping/SyntheticStagedRow.cs
using KiloImportService.Api.Domain.Importing;

namespace KiloImportService.Api.Domain.Mapping;

public record SyntheticStagedRow(
    string Sheet,
    int SourceRowNumber,
    StagedRowStatus Status,
    IReadOnlyList<string> Actions,
    string? MappedValuesJson = null);

public record RowActionLog(int SourceRowNumber, string Sheet, IReadOnlyList<string> Actions);
```

В `IImportMapper.cs` остаются только:
- `IImportMapper` (interface)
- `ImportContext` (record)
- `MappedRow`, `RowError`, `ValidationResult`, `ApplyResult` (records интерфейса)

### 2. Почему это помогает

- Если workspace на Jenkins-агенте получит неполный checkout (например, новый
  `FinModelImportMapper.cs` без обновлённого `IImportMapper.cs`), отдельный
  `SyntheticStagedRow.cs` всё равно будет включён компилятором — это
  страховка от любого checkout-edge-case'а.
- Соответствует .NET convention «один публичный тип = один файл с тем же именем».
- Не меняет namespace, API, поведение runtime — **полностью безопасный рефакторинг**.

### 3. Параметр `branch` в Jenkinsfile (DevOps action)

[Jenkinsfile:20](../Jenkinsfile#L20):
```groovy
string(name: 'branch', description: 'branch to build', defaultValue: 'dev')
```

`dev` ветки в репо нет. При запуске сборки **обязательно** указывать
`branch=room_branch` (или актуальную feature-ветку). DevOps action — поменять
дефолтное значение на ту ветку, что должна собираться по умолчанию (но это
вне scope этой правки — менять Jenkinsfile без явного согласования рискованно,
см. doc 132 «эталон service-dev»).

### ⚠️ Важно

1. **Не дублировать record'ы.** Если в `IImportMapper.cs` оставить старое
   определение `SyntheticStagedRow` — будет `CS0101: namespace already contains
   a definition`. Старое определение в `IImportMapper.cs` заменено на блок
   комментария-указатель.
2. **using `KiloImportService.Api.Domain.Importing`** — для `StagedRowStatus`
   enum. Без него `record SyntheticStagedRow(...)` не скомпилируется.
3. **using `KiloImportService.Api.Data.Visary`** НЕ нужен в новом файле —
   это для `VisaryDbContext`, которого тут нет.
4. **XML-doc `<see cref="StagedRow"/>`** валиден без using
   `KiloImportService.Api.Data.Entities` — резолвится compiler'ом по
   namespace tree.
5. **NU1510 warning** на `System.Text.Encoding.CodePages` — не блокирует
   сборку, оставляем (пакет нужен для CP1251 в XLS-парсере, см. csproj
   комментарий).
6. **101 warning CS8669** в Visary.Api.Client/Dto/Generated/ — auto-generated
   код без `#nullable` директивы. Не блокирует, отдельная задача.

## ❌ Чего НЕ делать

### ❌ Менять Jenkinsfile без согласования

```groovy
// НЕ выдумываем — пользователь сказал «сборка 17 июня проходила»,
// значит инфраструктура работала. Меняем только код, не CI.
defaultValue: 'room_branch'  // НЕТ — это решает DevOps
```

### ❌ Hard-fail на NU1510 / CS8669

```csharp
// НЕПРАВИЛЬНО — это auto-generated DTO от nswag/openapi-generator.
// Добавлять #nullable вручную = ломать регенерацию.
#nullable enable
```

Эти warning'и накапливаются в сторонних generated-файлах. Не блокируют сборку.
Если нужно убрать из лога — `<NoWarn>CS8669</NoWarn>` в `Visary.Api.Client.csproj`.

### ❌ Wipe workspace без диагностики

Если AppSec/CI-инцидент повторится после этого фикса, **не** нажимать «Wipe
Workspace» наугад. Сначала прислать строку из лога: какая ветка / какой коммит
сборки запускались (видно в `Build description` после
[doc 136](./136-jenkins-build-description-no-space-after-colon.md)).

## 🧪 Подтверждение

**Локально** (`dotnet 10.0.107`):
```
$ dotnet build KiloImportService.Api/KiloImportService.Api.csproj -c Release
    Предупреждений: 108
    Ошибок: 0
    Прошло времени 00:00:12.33
```

**Jenkins** — должен дать ту же картину при правильно указанной ветке
(`branch=room_branch` или ветка-наследник `room_branch`/`64333f2`).

## 📍 Применение в проекте

| Файл | Изменение |
|------|-----------|
| [KiloImportService.Api/Domain/Mapping/SyntheticStagedRow.cs](../KiloImportService.Api/Domain/Mapping/SyntheticStagedRow.cs) | **Создан** — record'ы `SyntheticStagedRow` + `RowActionLog` |
| [KiloImportService.Api/Domain/Mapping/IImportMapper.cs](../KiloImportService.Api/Domain/Mapping/IImportMapper.cs) | Удалены record'ы (вынесены в новый файл), оставлен блок-комментарий с ссылкой на этот doc |

## 🎯 Чек-лист

- [ ] Локальная сборка `dotnet build KiloImportService.Api/...csproj`: `Ошибок: 0`
- [ ] `git status`: `IImportMapper.cs` modified, `SyntheticStagedRow.cs` untracked → закоммитить вместе
- [ ] Jenkins запускать с `branch=room_branch` (не дефолтным `dev`)
- [ ] Если CS0246 повторится — проверить в Jenkins workspace, что **оба** файла
  (`IImportMapper.cs` и `SyntheticStagedRow.cs`) присутствуют в
  `KiloImportService.Api/Domain/Mapping/` на агенте
- [ ] Warning'и CS8669/NU1510 — не блокируют, оставляем (отдельный backlog)

## 🔗 См. также

- [doc 128](./128-synthetic-stagedrows-and-file-grouping.md) — концепция SyntheticStagedRow
- [doc 132](./132-build-alignment-with-service-dev-reference.md) — эталон сборки service-dev
- [doc 136](./136-jenkins-build-description-no-space-after-colon.md) — Jenkins build description format
