# 🏠 Импорт «Помещения» (rooms)

## 📋 Описание

**Статус**: ✅ Реализовано (v1)
**Дата**: 2026-05-07
**Маппер**: `RoomsFormImportMapper` (код типа `"rooms"`)

Импорт реестра помещений (квартир, машиномест, кладовых) из Excel-файла
по шаблону **«Пример импорта.xlsx»** / **«Единая форма 3»** в Visary через
HTTP API.

**Контракт UX**:
1. Пользователь выбирает в UI **тип импорта** «Помещения», **проект** и **объект строительства** (ОКС).
2. Загружает файл (`*.xlsx`).
3. Маппер per-row проверяет, что строка принадлежит выбранному ОКСу,
   находит/создаёт корпус, помещение и (опционально) ДДУ.

В отличие от прежней реализации, маппер **не ищет** Site по содержимому
строки — Site уже выбран в UI, а строки только валидируются против него.

---

## 🏗️ Поток обработки

```
ParsedRow[] (по листам) ──► ValidateAsync ──► MappedRow[] ──► ApplyAsync
                              │                                    │
                              ├─ загрузка ОКСа из Visary           ├─ группировка по Sheet
                              ├─ загрузка RoomKind из Visary       ├─ Section.find_or_create
                              ├─ per-row сверка ОКСа                ├─ Room.find_or_create
                              ├─ резолв Kind (column → sheet)       ├─ ShareAgreement.find_or_create
                              └─ MappedRow.IsValid                  └─ apply_failed на строку
```

### Validate phase

1. **Site обязателен** в `ImportContext` (иначе `file_error: site_required`).
2. **Загрузить выбранный ОКС** через `_crud.GetSiteByIdFullAsync(siteId)`.
   Используется `ConstructionSiteFull.StageNumber` (`int?`),
   `ConstructionProjectNumber` / `ConstructionPermissionNumber` (`string?`).
3. **Загрузить справочник RoomKind** из живого Visary через
   `_listView.ListRoomKindsAsync()` (НЕ из локальной visary_db — см. ⚠️ ниже).
4. **Резолвить ожидаемый вид помещения по имени листа** один раз
   (`Квартиры`→`Квартира`, `Машиноместа`→`Машиноместо`).
5. Для каждой строки:
   - Прочитать НПС / Этап / РНС из строки.
   - Сверить с ОКСом: **НПС и Этап** обязаны совпадать, **РНС** опционально
     (только если в файле непустое).
   - При несовпадении → `IsValid=false` с кодом `site_mismatch`,
     сообщение `"для строки файла {N} не подходит выбранный объект"`.
   - Резолвить Kind: приоритет «Тип/Название/Вид» из колонки;
     если пусто → fallback по имени листа.
   - Накопить остальные поля (площадь, стоимость, № ДДУ и т. д.).

### Apply phase

1. Сгруппировать `IsValid` строки по `Sheet` (порядок листов сохраняется).
2. Для каждого листа залогировать заголовок:
   ```
   RoomsForm.Apply: ───── Лист 'Квартиры' — N валидных строк ─────
   ```
3. Для каждой строки внутри листа:
   - **Organization-застройщик**: по PIN найти Org, привязать к Site
     (один раз на сессию — кэш `siteOrgLinked`).
   - **Section**: numeric-часть из «№ стр/корп» (`«лит. 1»` → `«1»`);
     `find_or_create` (Section.Type = `МЖД (ID=3)` по умолчанию).
   - **Room**: `find_or_create` в Section по `ExplicationNumber/Number`.
   - **ShareAgreement** (если в строке указан № ДДУ):
     `find_or_create/patch` с `Project`, `RoomKindRef`, `ProjectNumber`, `ConditionalNumber`.

---

## ✅ Правильная реализация (фрагменты)

### Per-row сверка выбранного ОКСа

```csharp
bool projectOk = string.Equals(rowProjectNum, siteProjectNumber, StringComparison.OrdinalIgnoreCase);
bool stageOk   = rowStageNum.HasValue && siteStageNumber.HasValue
              && rowStageNum.Value == siteStageNumber.Value;
bool permissionOk = string.IsNullOrWhiteSpace(rowPermission)
              || string.Equals(rowPermission, sitePermissionNumber, StringComparison.OrdinalIgnoreCase);

if (!projectOk || !stageOk || !permissionOk)
{
    rowErrors.Add(new RowError(null, "site_mismatch",
        $"для строки файла {row.SourceRowNumber} не подходит выбранный объект"));
    mappedRows.Add(new MappedRow(row.SourceRowNumber, IsValid: false, ...));
    continue;
}
```

### Группировка по листу в Apply

```csharp
var rowsBySheet = rows
    .Where(mr => mr.IsValid)
    .GroupBy(mr => GetStringOrNull(mr.MappedValues.RootElement, "Sheet") ?? "<unknown>",
             StringComparer.OrdinalIgnoreCase)
    .ToList();

foreach (var sheetGroup in rowsBySheet)
{
    _log.LogInformation(
        "RoomsForm.Apply: ───── Лист '{Sheet}' — {Count} валидных строк ─────",
        sheetGroup.Key, sheetGroup.Count());
    foreach (var mr in sheetGroup) { /* ... */ }
}
```

### Резолв Kind по имени листа (fallback)

«Квартиры» → срезаем `ы` → `Квартир` → не найдено → пробуем `Квартир + а` → **`Квартира`** ✓
«Машиноместа» → срезаем `а` → `Машиномест` → не найдено → пробуем `Машиномест + о` → **`Машиноместо`** ✓

```csharp
if ("аяыиеёАЯЫИЕЁ".Contains(last))
    candidates.Add(name[..^1]);
if (last == 'ы') candidates.Add(name[..^1] + "а");
if (last == 'и') candidates.Add(name[..^1] + "я");
if (last == 'а') candidates.Add(name[..^1] + "о");
```

### Создание Section с обязательным `Type`

```csharp
await _crud.CreateSectionAsync(new SectionCreateRequest
{
    ConstructionSiteID = siteId,
    ConstructionSite   = new VisaryRef { ID = siteId },
    Title              = sectionTitle,                           // «1.1»
    Type               = new VisaryRef { ID = 3, Title = "МЖД" }, // дефолт; парковка позже
}, ct);
```

---

## ⚠️ Важно

### 1. RoomKind берём из ЖИВОГО Visary, а не из локальной visary_db

```csharp
// ✅ ПРАВИЛЬНО: реальные ID Visary (Машиноместо=4, Квартира=…)
var roomKindList = await _listView.ListRoomKindsAsync(ct);
var kindByTitle = roomKindList.Data
    .Where(k => !string.IsNullOrWhiteSpace(k.Title))
    .GroupBy(k => k.Title!.Trim(), StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, g => g.First().ID, StringComparer.OrdinalIgnoreCase);
```

```csharp
// ❌ НЕПРАВИЛЬНО: seed-данные локальной БД могут не совпадать со стендом
var kindByTitle = await visaryDb.RoomKinds.AsNoTracking()...;  // вернёт «Гараж» когда нужно «Квартира»
```

**Симптом ошибки**: импортируется только один лист, а помещения создаются
с типом «Гараж» (или другим случайным из локального seed), потому что
строки с правильным Title из файла не находят свой ID и помечаются `fk_not_found`.

### 2. Substring-fallback для имени листа отключён

`«Машиноместа»`.Contains(`«Машино»`) = true — соблазнительно, но опасно: легко
случайно совпасть с «Машино…» / «Места …». Используем только точное совпадение
+ plural-trim heuristic. Если не подошло — требуем явное «Тип/Название/Вид» в
строке.

### 3. PATCH через `forceUpdate=true` — без `ID`/`RowVersion` в теле

Это не специфично для импорта помещений, но именно здесь оно критично:
`PatchRoomAsync` и `PatchShareAgreementAsync` зануляют эти поля перед сериализацией.
Подробности — в `doc_project/50-visary-api-new-methods.md` (раздел «Логирование запросов в Visary» / «forceUpdate=true|false»).

### 4. Полиморфные поля DTO `RoomRaw` / `ShareAgreementRaw`

`Active*ShareAgreement`, `*EscrowAccount`, `ValidityStatus` приходят разными
типами — в DTO это `JsonElement?`. См. `doc_project/56-visary-dto-deserialization-pitfalls.md`.

---

## ❌ Типичные ошибки

### Ошибка 1: 422 при создании Section

```text
[INF] Visary → POST .../api/visary/crud/constructionsection
       body={"ConstructionSiteID":7850,"ConstructionSite":{"ID":7850},"Title":"1"}
[ERR] Visary error 422: <тело>
```

**Причина**: отсутствует `Type`. Добавить `Type = new VisaryRef { ID = 3, Title = "МЖД" }`.

### Ошибка 2: 500 «Can not add property RowVersion to JObject»

```text
[INF] Visary → PATCH .../api/visary/crud/room/20586?forceUpdate=true
       body={"ID":20586,"RowVersion":0,"Kind":...}
[ERR] Visary error 500: "Can not add property RowVersion to Newtonsoft.Json.Linq.JObject..."
```

**Причина**: `forceUpdate=true` + ID/RowVersion в теле. Сделать поля `int?`/`long?`,
до сериализации присвоить `null`.

### Ошибка 3: «не подходит выбранный объект» для всех строк

**Причина**: пользователь выбрал неподходящий ОКС (НПС/Этап в файле и в ОКСе расходятся).
Лог в Validate показывает: `файл(НПС='нпс', Этап='1', РНС='рнс') vs site(НПС='', Этап=, РНС='')` —
видно, какие значения в выбранном ОКСе пустые/не совпадают.

### Ошибка 4: импортирован только один лист с типом «Гараж»

**Причина**: справочник RoomKind тянулся из локальной visary_db с устаревшим seed.
Решение — использовать `_listView.ListRoomKindsAsync()` (см. ⚠️ #1).

---

## 📍 Применение в проекте

| Компонент | Файл | Что делает |
|-----------|------|-----------|
| Маппер | `KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs` | Validate + Apply, sheet-группировка, Kind-резолв |
| Регистрация типа в UI | `KiloImportService.Api/Controllers/ImportTypesController.cs` | `["rooms"] = ("Помещения", ...)` |
| DI | `KiloImportService.Api/Program.cs` | `AddSingleton<IImportMapper, RoomsFormImportMapper>()` |
| Visary CRUD | `Visary.Api.Client/CRUD/CrudClient.cs` | `CreateSection/Room/ShareAgreement`, `PatchRoom/ShareAgreement` |
| Visary listview | `Visary.Api.Client/ListView/ListViewClient.cs` | `GetSectionsBySite`, `GetRoomsBySection`, `ListRoomKinds`, `GetShareAgreementsByRoom`, `GetOrganizationsByClientId` |
| Request DTO | `Visary.Api.Client/Dto/VisaryCrudRequests.cs` | `SectionCreateRequest`, `RoomCreateRequest/PatchRequest`, `ShareAgreementCreateRequest/PatchRequest` |
| Response DTO | `Visary.Api.Client/Dto/VisaryEntities.cs` | `RoomRaw` (с `JsonElement?` полями), `ShareAgreementRaw.ValidityStatus` |
| Пример файла | `RoomImport/Пример импорта.xlsx` | 2 листа: «Квартиры», «Машиноместа» |
| Описание методов | `RoomImport/описание методов.txt` | Валидные тела JSON для Section/Room/SA |
| Сценарий | `RoomImport/room_sa_create.puml` | PlantUML-диаграмма пути исходного «roomsForm» (наследник) |

### Удалено в v1 (по сравнению с предыдущими ревизиями)

- `KiloImportService.Api/Domain/Mapping/RoomsImportMapper.cs` — старый «простой» маппер,
  который писал в локальную visary_db. Не использовался в актуальном пайплайне.
- `KiloImportService.Api.Tests/Mapping/RoomsImportMapperTests.cs` — тесты к нему.
- Регистрация в `Program.cs` и meta-запись в `ImportTypesController` для
  `["roomsForm"]` (теперь оба сценария живут под кодом `"rooms"`).

---

## 🎯 Чек-лист отладки

### Импорт упал на 4xx/5xx от Visary

- [ ] Открыть `docker compose logs -f backend | grep "Visary →\|Visary error"`
- [ ] В строке `Visary → POST/PATCH ... body=...` посмотреть ровно то, что ушло
- [ ] В строке `Visary error <code>: <body>` — что Visary ответил
- [ ] Сравнить с эталонами в `RoomImport/описание методов.txt`
- [ ] При 422 — проверить обязательные поля (`Type` в Section, `UniqueNumber` в Room)
- [ ] При 500 «Can not add property RowVersion» — это `forceUpdate=true` + ID/RowVersion в теле

### Импорт прошёл, но помещения не там, где ожидалось

- [ ] Проверить лог `RoomsForm.Validate: загружен справочник RoomKind ... — N записей`:
      убедиться, что там есть нужные виды (`Квартира`, `Машиноместо`).
- [ ] Проверить `RoomsForm.Validate: лист 'X' → ожидаемый вид помещений 'Y' (ID=…)` —
      имя листа резолвится в правильный Kind.
- [ ] Проверить `RoomsForm.Apply: ───── Лист 'X' — N валидных строк` — все ли листы обработаны.
- [ ] При расхождении row.Kind vs sheet.Kind — будет лог-warn,
      доверяем строке (колонка приоритетнее).

### Все строки получили `site_mismatch`

- [ ] Лог в Validate показывает: `файл(НПС=…, Этап=…, РНС=…) vs site(НПС=…, Этап=…, РНС=…)`.
- [ ] У выбранного ОКСа `ConstructionProjectNumber` / `StageNumber` могут быть пустыми
      (синхронизация Sites из Visary могла не подтянуть `StageNumber` — он не входит в
      базовый `SiteColumns` в `ListViewClient`, маппер берёт его через `GetSiteByIdFullAsync`).

---

## 📚 См. также

- `doc_project/50-visary-api-new-methods.md` — методы Visary client (PATCH, forceUpdate, body-логирование)
- `doc_project/56-visary-dto-deserialization-pitfalls.md` — `JsonElement?` для полиморфных полей
- `doc_project/14-imports-backend-integration.md` — общий контур импорта (UI ↔ pipeline)
- `doc_project/15-signalr-progress.md` — прогресс импорта по SignalR
- `RoomImport/описание методов.txt` — эталонные JSON-тела запросов
- `RoomImport/room_sa_create.puml` — диаграмма исходного сценария
