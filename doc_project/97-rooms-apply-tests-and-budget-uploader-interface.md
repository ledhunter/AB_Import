# 97 · Тесты RoomsForm Apply + IBudgetVisaryUploader интерфейс

> Дата: 2026-05-20
>
> Связано с: [96-rooms-incremental-parallel-apply](./96-rooms-incremental-parallel-apply.md),
> [94-finmodel-auto-budget-before-gf](./94-finmodel-auto-budget-before-gf.md),
> [85-per-row-action-log](./85-per-row-action-log.md), [89-mappedrow-sheet-invariant](./89-mappedrow-sheet-invariant.md),
> [17-backend-tests-xunit](./17-backend-tests-xunit.md)

## Зачем

После переезда `RoomsFormImportMapper.ApplyAsync` на инкрементальный +
параллельный режим (doc 96) у нас был только статический тест
`RoomApplySnapshotStoreTests` на hash/BuildKey. Поведение самого Apply
(RowActionLog, diff-skip, PATCH при изменении hashed-полей, многострочный
случай) покрыто не было. Параллельно сломались `FinModel*Tests`: после
добавления `IServiceScopeFactory` в `FinModelImportMapper` тесты не
знали, как сконструировать его — пустой `Mock<IServiceScopeFactory>`
вызывал NRE на `CreateScope().ServiceProvider.GetRequiredService<...>()`.

## Что сделано

### 1. `IBudgetVisaryUploader` — интерфейс ради тестируемости

```csharp
// KiloImportService.Api/Budget/BudgetVisaryUploader.cs
public interface IBudgetVisaryUploader
{
    Task<BudgetVisaryUploadAndWaitResult> UploadAndWaitAsync(
        Guid sessionId,
        TimeSpan? pollInterval = null,
        TimeSpan? maxWait = null,
        CancellationToken ct = default);
}

public sealed class BudgetVisaryUploader : IBudgetVisaryUploader { … }
```

DI:

```csharp
// Program.cs
builder.Services.AddScoped<IBudgetVisaryUploader, BudgetVisaryUploader>();
```

`FinModelImportMapper.UploadBudgetToVisaryAsync` теперь резолвит интерфейс:

```csharp
using var scope = _scopeFactory.CreateScope();
var uploader = scope.ServiceProvider.GetRequiredService<IBudgetVisaryUploader>();
```

Польза: тест может зарегистрировать `Mock<IBudgetVisaryUploader>` в
`ServiceCollection` без построения всего DI-графа
(`IFileStorageClient`, `ICrudClient`, `ImportServiceDbContext`,
`IOptionsMonitor<VisaryOptions>`, `BudgetXlsxExporter`).

Production не изменился — единственная реализация в DI та же.

### 2. `RoomsFormImportMapperApplyTests` — 4 integration-теста Apply

`KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperApplyTests.cs`.
Mock-ает `ICrudClient` + `IListViewClient`, in-memory `ImportServiceDbContext`
поднимается через `ServiceCollection` (нужен для `RoomApplySnapshotStore`,
который маппер дёргает через `IServiceScopeFactory`).

Покрытие:

| Тест | Что проверяет |
|------|---------------|
| `ApplyAsync_FirstRun_CreatesRoomAndShareAgreement_AndFillsRowActionLog` | первый Apply: CREATE Room + CREATE SA, `RowActionLog` содержит «Корпус найден», «Помещение создано», «ДДУ создан», `Sheet`+`SourceRowNumber` корректны |
| `ApplyAsync_SecondRun_SameRows_SkipsByHash_NoExtraPatchOrCreate` | повторный Apply того же набора: `MappedHash` совпадает → `RowActionLog` содержит «Без изменений — пропуск (snapshot)», нет CREATE/PATCH Room/SA |
| `ApplyAsync_SecondRun_ChangedArea_TriggersPatchRoom` | изменение `ProjectArea` (входит в `HashedMappedFields`) ломает hash → PATCH Room проходит, «Без изменений» НЕ появляется |
| `ApplyAsync_MultipleSheets_RowActionLogPreservesSheetPerRow` | каждый `RowActionLog` несёт корректный `Sheet`/`SourceRowNumber` для своей строки (инвариант doc 89, без него UI не рендерит actions) |

### 3. Фикс `FinModelBudgetTests`/`FinModelChapter1ScheduleTests`/`FinModelImportMapperTests`

Все три теста создавали `FinModelImportMapper` с пустым
`Mock<IServiceScopeFactory>` — `CreateScope()` возвращал null, маппер
ловил исключение в `try/catch` вокруг `UploadBudgetToVisaryAsync`,
`AppliedCount` оставался 0, тест на «AppliedCount > 0» падал.

Решение — реальный mini-DI:

```csharp
_mockBudgetUploader = new Mock<IBudgetVisaryUploader>();
_mockBudgetUploader
    .Setup(u => u.UploadAndWaitAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan?>(),
                                      It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
    .ReturnsAsync(new BudgetVisaryUploadAndWaitResult(
        Upload: new BudgetVisaryUploadResult(1, 999, "stub.xlsx"),
        Success: true, TimedOut: false,
        FinalStatus: "Закончен успешно", CountErrors: 0, CountWarnings: 0));

var services = new ServiceCollection();
services.AddSingleton(_mockBudgetUploader.Object);
_serviceProvider = services.BuildServiceProvider();
var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
```

`scope.ServiceProvider.GetRequiredService<IBudgetVisaryUploader>()` уже работает
без дополнительного `IServiceScope` mock'а — `ServiceCollection` сам отдаёт
встроенный `IServiceScopeFactory`.

## Грабли

### Grabble 1: `Guid.NewGuid()` в делегате `UseInMemoryDatabase`

```csharp
// ❌ Каждый scope получает СВОЮ in-memory БД — snapshot из Apply №1
//    не виден в Apply №2.
services.AddDbContext<ImportServiceDbContext>(o =>
    o.UseInMemoryDatabase($"RoomsApply_{Guid.NewGuid()}"));

// ✅ Имя считается один раз; все scopes используют общий store.
var dbName = $"RoomsApply_{Guid.NewGuid()}";
services.AddDbContext<ImportServiceDbContext>(o => o.UseInMemoryDatabase(dbName));
```

`AddDbContext` сохраняет лямбду как `Action<DbContextOptionsBuilder>` и
**вызывает её на каждый запрос DbContext** — а маппер в `ApplyAsync`
открывает несколько scopes (один для `LoadForSiteAsync`, один для
`UpsertBatchAsync`). Если внутри лямбды `Guid.NewGuid()`, каждая из них
получит новое имя БД, и snapshot, сохранённый в первом scope, не будет
найден во втором — diff-skip перестанет работать в тестах при том, что
production-код в порядке.

Симптом был ровно такой: `ApplyAsync_SecondRun_SameRows_SkipsByHash` падал
на `Assert.Contains("Без изменений…")`, в action log были «Помещение
создано», как будто это снова первый Apply.

### Grabble 2: outer vs inner репо и compose-ownership

В репозитории два клона: `AB_Import/` (outer) и `AB_Import/AB_Import/`
(inner). Оба содержат свой `docker-compose.yml` с одинаковым
`name: kilo-import`, поэтому контейнеры с теми же именами
(`kilo-import-backend`, `kilo-import-frontend`) обслуживаются обоими
проектами как один.

`docker inspect kilo-import-backend --format '{{ index .Config.Labels "com.docker.compose.project.working_dir" }}'`
показывает, кто реально владеет контейнером. Если working_dir указывает
на outer — это значит, что выполненный из inner `.env` /
кодовый билд **до контейнеров не доехал**.

Симптомы owner-mismatch:

- В UI отчёта импорта пропадает имя листа и «Сообщения» = «—» — outer
  не имеет doc 89/85 фиксов.
- В сообщениях ошибок появляются алиасы колонок, которых нет в
  актуальном `RoomsFormImportMapper.cs` (например, `Тип / Вид помещения / Kind / KindTitle`
  — этот набор лежит в outer'овском `RoomsImportMapper.cs`, в inner
  файл переименован в `RoomsFormImportMapper.cs`).
- Backend env содержит токен из outer `.env`, даже после обновления
  inner `.env`.

Лечение — однократное:

```bash
# из inner
docker compose build backend frontend
docker compose up -d --force-recreate backend frontend
docker inspect kilo-import-backend \
  --format '{{ index .Config.Labels "com.docker.compose.project.working_dir" }}'
# должно быть ...\AB_Import\AB_Import (inner)
```

## Чек-лист добавления нового интеграционного теста на Apply

Когда понадобится покрыть ещё один Apply-сценарий
(например, новая ветка по `RoomCategory != Residential`):

1. Используй фикстуру `RoomsFormImportMapperApplyTests` — там уже
   настроены `ICrudClient`/`IListViewClient` mocks под нормальный
   happy-path и `ImportServiceDbContext` in-memory.
2. Если нужно изменить поведение per-test (например, заставить Visary
   вернуть «уже существующую» Room) — пере-`Setup` нужный метод
   `_mockListView` ДО второго `_mapper.ApplyAsync`.
3. Для `_mockCrud.Invocations.Clear()` — обнули счётчик ПЕРЕД действием,
   которое верифицируешь.
4. Если тест ожидает поведение snapshot-store между двумя `ApplyAsync`-ами,
   проверь `dbName` объявлен ВНЕ лямбды (см. Grabble 1).
