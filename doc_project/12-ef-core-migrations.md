# 🗄️ EF Core миграции для service-db

## 📋 Описание

Служебная БД `import_service_db` (схема `import`, 5 таблиц) управляется через
EF Core миграции. Visary-БД — DB-first, миграциями НЕ трогается (там SQL-скрипты
в `db/visary/init/*.sql`).

При старте backend автоматически применяет pending-миграции через
`db.Database.MigrateAsync()` — это позволяет деплою быть «загрузил docker → всё работает».

---

## ✅ Правильная реализация

### Guard для EF tools (`EF.IsDesignTime`)

```csharp
// KiloImportService.Api/Program.cs
using Microsoft.EntityFrameworkCore;

var app = builder.Build();

// ⚠️ EF tools (dotnet ef migrations add) выполняют код Program.cs до app.RunAsync(),
// чтобы построить хост и достать DbContext. Без guard EF.IsDesignTime попытка
// подключиться к реальному Postgres сломает scaffolding, когда БД не запущена.
if (!EF.IsDesignTime)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ImportServiceDbContext>();
    Log.Information("Applying ImportServiceDb migrations…");
    await db.Database.MigrateAsync();
}
```

### Регистрация DbContext + кастомное имя `__ef_migrations_history`

```csharp
// KiloImportService.Api/Program.cs
builder.Services.AddDbContext<ImportServiceDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("ServiceDb"),
        // 👇 история миграций ложится В ТУ ЖЕ схему import, не public —
        //    чтобы наша БД была «полностью в одной схеме».
        npg => npg.MigrationsHistoryTable("__ef_migrations_history",
                                          ImportServiceDbContext.SchemaName)));
```

### Команды для разработчика

```powershell
# Установка tool'а (один раз на машину)
dotnet tool install --global dotnet-ef --version 9.0.4

# Создание новой миграции (после изменения модели):
dotnet ef migrations add <Name> --context ImportServiceDbContext --output-dir Migrations

# Применение к локальной БД (postgres-service из docker-compose):
dotnet ef database update --context ImportServiceDbContext

# Сгенерировать idempotent SQL для деплоя в проды:
dotnet ef migrations script --context ImportServiceDbContext --idempotent --output Migrations/release.sql
```

### ⚠️ Важно

- **`--context`** обязателен — у нас два DbContext'а (`ImportServiceDbContext` + `VisaryDbContext`).
  EF tools сами не выберут нужный, упадут с error.
- **HostAbortedException в выводе `dotnet ef …` — норма.** EF tools специально
  абортают хост после построения DI-контейнера (`Microsoft.Extensions.Hosting.HostFactoryResolver.HostingListener.ThrowHostAborted`).
  Команда завершится `Done.` — это успех.
- **Версия `dotnet-ef` должна совпадать с EF Core в csproj.** У нас EF Core 9.0.4 →
  `dotnet tool install dotnet-ef --version 9.0.4`.
- **`MigrationsHistoryTable("__ef_migrations_history", "import")`** — без этого EF
  пишет историю в `public.__EFMigrationsHistory`, а наша служебная таблица
  потеряется среди системных. Имя — snake_case под общий стиль Postgres.

---

## ❌ Типичные ошибки

### Ошибка 1: миграции запускаются при `dotnet ef migrations add`

```csharp
// НЕПРАВИЛЬНО — без EF.IsDesignTime guard
var app = builder.Build();
using var scope = app.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<ImportServiceDbContext>();
await db.Database.MigrateAsync();   // ❌ ломает scaffolding, если БД не запущена
```

**Что произойдёт:** `dotnet ef migrations add Initial` попытается подключиться к
Postgres до того, как ты вообще начал писать миграции. Если БД не поднята —
`Npgsql.NpgsqlException: Connection refused`. Ты не сможешь создать миграцию,
не подняв БД, которой ещё нет.

**Правильно:** `if (!EF.IsDesignTime)` вокруг `MigrateAsync()`.

### Ошибка 2: история миграций в `public`

```csharp
// НЕПРАВИЛЬНО — историю кидает в public
opt.UseNpgsql(connectionString);   // нет MigrationsHistoryTable
```

**Почему плохо:** при `\dt import.*` в psql ты видишь только 5 таблиц, а где
история — непонятно. На code-review это путает.

**Правильно:** явный `npg.MigrationsHistoryTable("__ef_migrations_history", "import")`.

### Ошибка 3: не указан `--context` при двух DbContext'ах

```powershell
# НЕПРАВИЛЬНО
dotnet ef migrations add Initial   # ❌ More than one DbContext was found
```

**Правильно:** `dotnet ef migrations add Initial --context ImportServiceDbContext`.

### Ошибка 4: миграция для Visary-БД

```powershell
# НЕПРАВИЛЬНО — Visary управляется внешними SQL-скриптами
dotnet ef migrations add Smth --context VisaryDbContext
```

**Почему плохо:** схема Visary создаётся через `db/visary/init/01-schema.sql` и
управляется внешней системой Visary. EF миграции с PascalCase-маппингом колонок
сломают существующую базу. `VisaryDbContext` намеренно не имеет миграций.

---

## 📊 Что создаёт `Initial` миграция

```
Схема: import
Таблицы:
  - import_sessions          (PK: Id GUID, индексы: Status, StartedAt,
                              UX_ImportSession_TypeAndSha — partial unique
                              с фильтром Status NOT IN ('Failed','Cancelled'))
  - import_session_stages    (FK → import_sessions, ON DELETE CASCADE)
  - staged_rows              (jsonb RawValues + MappedValues, FK → sessions)
  - import_errors            (FK → sessions)
  - import_file_snapshots    (1:1 с sessions)
  - __ef_migrations_history  (служебная таблица EF)
```

**Partial unique index** `UX_ImportSession_TypeAndSha`:

```sql
CREATE UNIQUE INDEX "UX_ImportSession_TypeAndSha"
  ON import.import_sessions ("ImportTypeCode", "FileSha256")
  WHERE "Status" NOT IN ('Failed','Cancelled');
```

Защищает от повторной загрузки того же файла в активную сессию, но позволяет
переимпортировать после отмены или ошибки. Реализуется в EF через:

```csharp
e.HasIndex(x => new { x.ImportTypeCode, x.FileSha256 })
    .HasDatabaseName("UX_ImportSession_TypeAndSha")
    .IsUnique()
    .HasFilter("\"Status\" NOT IN ('Failed','Cancelled')");
```

---

## 📍 Применение в проекте

| Файл | Что определяет |
|------|----------------|
| `KiloImportService.Api/Program.cs` | Регистрация DbContext, guard `EF.IsDesignTime`, MigrateAsync на старте |
| `KiloImportService.Api/Data/ImportServiceDbContext.cs` | Модель + `MigrationsHistoryTable` |
| `KiloImportService.Api/Migrations/20260429084812_Initial.cs` (+ `.Designer.cs`) | Базовая миграция: создаёт схему `import` и 5 базовых таблиц |
| `KiloImportService.Api/Migrations/20260429170000_AddCachedProjects.cs` (+ `.Designer.cs`) | Кэш проектов |
| `KiloImportService.Api/Migrations/20260430213808_RemoveFileSha256Constraint.cs` + 4 последующих | Поэтапные правки модели |
| `KiloImportService.Api/Migrations/ImportServiceDbContextModelSnapshot.cs` | Текущий снимок модели |
| `KiloImportService.Api/Migrations/Initial.sql` | Idempotent SQL-snapshot Initial-миграции (для ручного деплоя/ревью, **НЕ исполняется** EF Core'ом — он работает только с `.cs`) |
| `docker-compose.yml` (postgres-service) | БД, к которой применяется миграция |

---

## ⚠️ Никогда не удаляйте Initial-миграцию

В мае 2026 (коммит `4f0e224`) `20260429084812_Initial.cs` и `20260429170000_AddCachedProjects.cs`
были по ошибке удалены вместе с одноимёнными `.Designer.cs` (рефакторинг при «решении
проблемы с докером»). После удаления цепочка миграций для пустой БД ломалась: `MigrateAsync()`
пытался применить `20260430213808_RemoveFileSha256Constraint` первым, а тот `DropIndex`/`DropColumn`
на `import_sessions` — таблицы ещё нет → `relation does not exist`. Восстановлено в июне 2026
через `git checkout 4f0e224^ -- Migrations/20260429*` (см. doc 144 git-история).

**Урок:** Initial-миграция нужна на диске постоянно. `Initial.sql` её НЕ заменяет — EF Core
`MigrateAsync()` исполняет только `.cs` миграции, зарегистрированные в собранной сборке.
Если нужно «обнулить» цепочку — делайте `dotnet ef migrations remove` (вытащит из снимка
обратно), а не `rm`.

**Проверка после любого касания папки `Migrations/`:**

```powershell
# 1) Цепочка валидна (видны все timestamps + первая = Initial):
dotnet ef migrations list --context ImportServiceDbContext --no-build

# 2) Скрипт «from scratch» начинается с CREATE SCHEMA + CREATE TABLE,
#    а не с DROP/ALTER (что значило бы потерю Initial):
dotnet ef migrations script --context ImportServiceDbContext --no-build --output /tmp/scratch.sql
head -30 /tmp/scratch.sql   # ожидаем CREATE SCHEMA import; CREATE TABLE import.import_sessions
```

---

## 🎯 Чек-лист при изменении модели

- [ ] Изменил entity / `DbContext.OnModelCreating`
- [ ] Запустил `dotnet ef migrations add <ОписаниеИзменения> --context ImportServiceDbContext`
- [ ] Прочитал сгенерированный `*.cs` — нет ли неожиданных DROP COLUMN
- [ ] **Не удалил ни одной существующей миграции** (см. предупреждение выше)
- [ ] Запустил `dotnet ef database update --context ImportServiceDbContext` локально
- [ ] Проверил структуру в psql: `\d import.<table>`
- [ ] Сгенерировал SQL-скрипт `dotnet ef migrations script --idempotent` для production-деплоя
- [ ] Прогнал тесты: `dotnet test KiloImportService.Api.Tests/`
