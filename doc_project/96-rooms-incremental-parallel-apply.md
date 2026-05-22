# 96 · Инкрементальный + параллельный Apply импорта «Помещения»

> Дата: 2026-05-20
>
> Связано с: [03-import-flow](./03-import-flow.md), [77-rooms-form-import](./77-rooms-form-import.md) *(если есть)*, [85-per-row-action-log](./85-per-row-action-log.md), [86-rooms-dedup-pre-check](./86-rooms-dedup-pre-check.md), [89-mappedrow-sheet-invariant](./89-mappedrow-sheet-invariant.md)
>
> 🔄 **Расширено в [doc 106](./106-rooms-snapshot-revalidation.md) (2026-05-22):** hash-match — необходимое, но НЕ достаточное условие skip-а. Перед пропуском строки маппер сверяется с реальным состоянием Visary (Room в `roomsInSection`, ДДУ через `GetShareAgreementsByRoomAsync`), иначе удалённые в Visary сущности не восстанавливались бы при повторном импорте.

## Проблема

Apply фазы маппера `RoomsFormImportMapper` была написана как один последовательный
цикл по всем валидным строкам. На реальных файлах заказчика (3–6 тыс. квартир,
2–4 листа) это давало два ощутимых эффекта:

1. **Время.** Каждая строка делает 3–5 round-trip к Visary API
   (`GetRoomsBySectionAsync`, `GetShareAgreementsByRoomAsync`,
   `CreateRoom`/`PatchRoom`, `CreateSection`/`PatchShareAgreement`,
   `LinkProjectManagementToSiteAsync`). При латентности 80–200 мс это даёт
   единицы минут на одну сессию.
2. **Повторный импорт.** Если файл прислали повторно с правкой 5 строк, маппер
   всё равно гнал `PatchRoom`/`PatchShareAgreement` для всех 3000 — каждый раз
   полным RowVersion-обходом. У Visary это бьёт по rate-limit и плодит мусор
   в audit-логах.

## Решение

Две независимые оптимизации:

### 1. Snapshot-таблица с дифф-skip-ом

Новая таблица `import.room_apply_snapshots` хранит «последнее применённое
состояние» для каждой строки импорта (бизнес-ключ совпадает с unique-key Room:
`(VisarySiteId, Sheet, SectionTitle, RoomKindId, RoomNumber, BuildingSection)`).
В неё кладём:

- `MappedHash` — SHA256 от канонизированного JSON-а `MappedValues` (только
  поля, реально записываемые в Visary, — см. `HashedMappedFields`);
- `MappedSnapshot` — полный jsonb-снапшот `MappedValues` (для debug-сравнения);
- ссылки на созданные сущности Visary (`VisarySectionId`, `VisaryRoomId`,
  `VisaryShareAgreementId`).

В Apply один SELECT в начале сессии (`LoadForSiteAsync`) поднимает всё в
`ConcurrentDictionary<RoomSnapshotKey, RoomApplySnapshot>` — дальше дифф-проверка
работает в памяти. Если `prev.MappedHash == ComputeMappedHash(mr.MappedValues)`
→ строка пропускается с меткой «Без изменений — пропуск (snapshot)».

#### Что входит в хэш и почему

```csharp
private static readonly string[] HashedMappedFields =
[
    "RoomNumber", "RoomKindId", "RoomKindTitle", "RoomCategory",
    "SectionTitle", "SectionTitleNumeric",
    "BuildingSection", "Floor",
    "RoomsCount",
    "ProjectArea", "TotalArea",
    "CostForOne", "MarketCostPerM", "ZalogCostPerM",
    "ShareAgreementNumber",
    "StageNumberRaw", "StageNumber", "ProjectNumber",
];
```

Сознательно НЕ включены:

- `Sheet`, `DeveloperPin`, `PermissionNumber` — диагностика; их изменение
  не означает изменения помещения как сущности Visary.
- Любые поля, добавленные после-валидационными pass-ами (если будут) —
  чтобы они не ломали дифф-skip.

#### Канонизация значений (`ReadCanonical`)

- строки → `Trim()+ToLowerInvariant`;
- числа → invariant-строка (`"R"` для double, `InvariantCulture` для long);
- bool → `"true"`/`"false"`;
- null/undefined → `null`.

`SortedDictionary<string,string?>` гарантирует одинаковый порядок ключей при
сериализации. Без этого `JsonSerializer` мог бы выдать разный JSON при разной
итерации словаря и сломать хэш.

#### BuildKey: нормализация бизнес-ключа

```csharp
RoomApplySnapshotStore.BuildKey(
    visarySiteId, sheet, sectionTitle, roomKindId, roomNumber, buildingSection)
```

- Все строковые поля → `Trim()+ToLowerInvariant` (иначе «1.1» и «1.1 »
  из разных Excel-редакций считались бы разными строками).
- `roomKindId ?? 0` — Postgres трактует NULL в unique-index как «не равно NULL»,
  что обходило бы дедуп; превращаем null в 0 на уровне ключа.

### 2. Параллелизм по группам (Sheet, Section)

Apply теперь работает в три pre-pass + parallel main:

| # | Шаг | Конкурентность |
|---|-----|----------------|
| ① | Pre-load snapshots: один SELECT по сайту → `ConcurrentDictionary` | — |
| ② | `TryUpdateSitePermissionNumberAsync` (РНС в Site) | — |
| ③ | Sections **sequential**: для всех уникальных `SectionTitle` find-or-create | sequential by design |
| ④ | Developer link **sequential**: уникальные `DeveloperPin` → resolve org → create/link PM | sequential by design |
| ⑤ | Main **parallel** по группам (Sheet, Section): внутри группы sequential, между группами — `Parallel.ForEachAsync` с `MaxDegreeOfParallelism = min(8, ProcessorCount, N_groups)` | **parallel** |
| ⑥ | Batch upsert `RoomApplySnapshot[]` — один SaveChanges на всё | — |

**Почему Sections и DeveloperPin sequential:** find-or-create
неидемпотентна в смысле гонок. Два параллельных потока, увидев «нет
секции `1.1`», создадут две — в Visary получится duplicate Section, и
последующие Room-ы привяжутся к разным `SectionID` непредсказуемо. То же
для projectmanagement: создать две PM-записи (org, Role=Developer) в одном
проекте → orphan. Поэтому эти проходы делаются один раз в начале Apply.

**Почему внутри группы sequential:** в одном (Sheet, Section) могут оказаться
дублирующиеся строки (Excel-редактор дублирует — не невероятно). Если
параллелить ВНУТРИ группы — двое потоков увидят «нет Room №15», создадут
два дубликата. Между группами эта проблема отсутствует: `(Section A, Room 15)`
и `(Section B, Room 15)` — разные сущности.

**`ParallelismCap = 8`:** ограничивает нагрузку на Visary API. Не выкручиваем
сильно: rate-limit на их стороне + наш собственный HTTP-pool. Реальная степень
параллелизма = `min(8, Environment.ProcessorCount, N_groups)` — для маленьких
файлов (1–2 секции) degenerates в sequential.

### Lock-free счётчики и журнал

- `applied` / `skipped` — `Interlocked.Increment(ref int)`.
- `errors` — `ConcurrentBag<RowError>`.
- `actionsByRow` — `ConcurrentDictionary<(Sheet,Row), List<string>>`.
  GetOrAdd lock-free; `lock(list)` только на короткий `Add` —
  burst-операция длиной 1 строка, contention практически нулевая.
- `snapshotUpserts` — `ConcurrentBag<RoomApplySnapshot>`; финальный
  batch-upsert один `SaveChangesAsync` в `RoomApplySnapshotStore.UpsertBatchAsync`.

## DI

```csharp
// Program.cs
builder.Services.AddScoped<RoomApplySnapshotStore>();
```

Маппер зарегистрирован Singleton (общий regional registry для стратегий импорта),
а Store зависит от Scoped `ImportServiceDbContext`. Captive dependency решена
тем же паттерном, что и `BudgetVisaryUploader` в `FinModelImportMapper`:
маппер берёт `IServiceScopeFactory` через DI, открывает мини-scope перед
load/upsert (короткие in-memory операции — без блокировок).

```csharp
using var scope = _scopeFactory.CreateScope();
var store = scope.ServiceProvider.GetRequiredService<RoomApplySnapshotStore>();
```

## Миграция

```
KiloImportService.Api/Migrations/20260520072742_AddRoomApplySnapshots.cs
```

Создаёт `import.room_apply_snapshots`:
- bigint identity PK;
- jsonb `MappedSnapshot`, varchar(64) `MappedHash`;
- unique index `IX_RoomApplySnapshot_BusinessKey` на бизнес-ключ;
- index `IX_RoomApplySnapshot_Site` для batch-pre-load.

## Тесты

`KiloImportService.Api.Tests/Mapping/RoomApplySnapshotStoreTests.cs` — 10 тестов:
- хэш стабилен между запусками;
- хэш игнорирует порядок ключей;
- хэш меняется при изменении значимого поля (`ProjectArea`);
- хэш НЕ меняется при изменении `Sheet`/`DeveloperPin`;
- хэш нормализует case/whitespace;
- `BuildKey` нормализует строки и `kindId ?? 0`;
- разные siteId дают разные ключи.

Полноценный integration-тест параллельной обработки требует mock'ов
`ICrudClient`/`IListViewClient` для всех 6 методов цикла Apply — не сделан,
оставлено на будущее (или e2e через docker-compose с реальным Visary
test-стендом).

## Что осталось как было

- `RoomsFormImportMapper.ValidateAsync` — без изменений.
- `TryUpdateSitePermissionNumberAsync` — без изменений.
- Логика find/create для Section/Room/ShareAgreement, формат
  `UniqueNumber`/`Title`, ветка orphan-SA — те же; они просто упакованы
  в параллельный цикл.
- ApplyResult/RowActionLog — без изменений в контракте; Pipeline получает
  всё то же самое, что и раньше.

## Инварианты, которые нельзя нарушать

1. **Pre-pass Sections sequential.** Параллелизация → дубликаты Section в Visary.
2. **Pre-pass Developer link sequential.** Параллелизация → дубликаты
   projectmanagement в проекте.
3. **Внутри группы (Sheet, Section) — sequential.** Параллелизация → дубликаты
   Room при повторяющихся строках в Excel.
4. **HashedMappedFields замораживается.** Любое добавление поля в `MappedValues`,
   которое влияет на запись в Visary, должно быть включено в этот массив,
   иначе diff-skip станет ложноположительным (пропустим обновление).
5. **`MappedSnapshot = JsonDocument.Parse(v.GetRawText())`.** Не передаём
   `mr.MappedValues` напрямую — это shared `JsonDocument` из StagedRow, и
   EF тащил бы тот же экземпляр через value-converter. После Apply пайплайн
   может его dispose-ить.
6. **`LastAppliedSessionId` всегда = `context.SessionId`.** При откате
   таблицы snapshot к предыдущей сессии можно использовать его для
   forensic-восстановления.
7. **Hash-match — необходимое, но НЕ достаточное условие skip-а** (см. [doc 106](./106-rooms-snapshot-revalidation.md)). Перед пропуском
   строки маппер обязан проверить, что `prev.VisaryRoomId` всё ещё
   существует в Visary (поиск в уже-загруженном `roomsInSection`), и при
   наличии `prev.VisaryShareAgreementId` — что ДДУ тоже на месте. Иначе
   удалённое в Visary помещение не восстановится при повторном импорте
   того же файла.
