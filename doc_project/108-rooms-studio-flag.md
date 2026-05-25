# 🛏 Импорт «Помещения» — детектор студии

## 📋 Описание

Колонка «Колич. комнат» может содержать не только число, но и маркер студии.
По требованию заказчика такие случаи нужно превращать в пару полей Visary:
`RoomsNumber = 0` + `IsStudio = true` — иначе студии в Visary создаются как
обычные многокомнатные квартиры без флага.

Маркеры (case-insensitive, после `Trim()`):

| Raw в Excel | RoomsCount | IsStudio | Комментарий |
|-------------|-----------:|:--------:|-------------|
| `с`         | 0          | true     | сокращение |
| `ст`        | 0          | true     | сокращение |
| `студ`      | 0          | true     | сокращение |
| `студия`    | 0          | true     | полное слово |
| `Студия`/`СТУДИЯ` | 0    | true     | case-insensitive |
| ` студия ` | 0          | true     | trim |
| `0`         | 0          | true     | числовой 0 — тоже студия |
| `1`         | 1          | false    | обычная однушка |
| `1 к.`      | 1          | false    | см. [84](./84-rooms-count-normalization.md) |
| `—`/пусто   | null       | false    | для «Квартиры» → `required_missing` |

Раньше (до doc 108):
- `студия` → `RoomsCount=null`, для Kind=Квартира — ошибка `required_missing`.
- `0` → `RoomsCount=0`, без `IsStudio` → в Visary создавалась квартира с 0 комнат.

---

## ✅ Правильная реализация

### Validate

```csharp
var roomsCountRaw = ReadString(row, RoomsCountAliases);
int? roomsCount = ExtractFirstRunOfDigits(roomsCountRaw);

// Студия: текстовый маркер ИЛИ числовой 0.
bool isStudio = IsStudioMarker(roomsCountRaw) || roomsCount == 0;
if (isStudio) roomsCount = 0;

// required_missing НЕ выдаём, если это студия (валидный вариант для «Квартиры»).
if (roomsCount is null && !isStudio
    && string.Equals(roomKindTitle.Trim(), "Квартира", StringComparison.OrdinalIgnoreCase))
{
    rowErrors.Add(new RowError(/* required_missing */));
}

var mapped = new Dictionary<string, object?>
{
    ["RoomsCount"] = roomsCount,
    ["IsStudio"]   = isStudio,
    /* … */
};
```

### Apply (Create + Patch)

```csharp
// CREATE
await _crud.CreateRoomAsync(new RoomCreateRequest
{
    RoomsNumber = GetIntOrNull(v, "RoomsCount"),
    IsStudio    = GetBoolOrNull(v, "IsStudio"),
    /* … */
}, gct);

// PATCH — то же самое
```

### Хелпер `IsStudioMarker`

```csharp
internal static bool IsStudioMarker(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return false;
    var s = raw.Trim();
    return string.Equals(s, "с",      StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "ст",     StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "студ",   StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "студия", StringComparison.OrdinalIgnoreCase);
}
```

### ⚠️ Snapshot-hash

`IsStudio` добавлен в `RoomApplySnapshotStore.HashedMappedFields` — иначе
первый импорт зафиксирует `IsStudio=false`, а после переключения колонки
в файле на «студия» повторный импорт скипнет строку по hash-у и `IsStudio`
не обновится в Visary.

```csharp
private static readonly string[] HashedMappedFields =
[
    /* … */
    "RoomsCount", "IsStudio",
    /* … */
];
```

---

## ❌ Типичные ошибки

### Substring-сравнение вместо точного

```csharp
// ❌ НЕПРАВИЛЬНО — «секция»/«склад»/«стандарт» начинаются на «с»/«ст»
if (raw.StartsWith("с", StringComparison.OrdinalIgnoreCase)) isStudio = true;
```

Маркер должен быть **точным словом** (после Trim), иначе любые тексты с
буквой «с» в начале попадут под IsStudio.

### Опускать `IsStudio` в `HashedMappedFields`

```csharp
// ❌ НЕПРАВИЛЬНО — diff-skip не заметит изменения студии
private static readonly string[] HashedMappedFields =
[
    /* … */
    "RoomsCount",      // только число!
    /* … */
];
```

При повторном импорте того же файла с исправленной студией строка попадёт
под `hash-match → skip`, и `IsStudio` в Visary останется старым.

### Сохранять `RoomsCount=null` для студии

```csharp
// ❌ НЕПРАВИЛЬНО — RoomsCount должен быть 0, а не null
if (IsStudioMarker(raw))
{
    isStudio = true;
    // roomsCount остаётся null
}
```

Visary принимает `RoomsNumber: 0 + IsStudio: true`; если оставить null,
для Kind=Квартира снова сработает `required_missing`.

---

## 📍 Применение в проекте

| Файл | Что меняется |
|------|--------------|
| [Visary.Api.Client/Dto/VisaryCrudRequests.cs](../Visary.Api.Client/Dto/VisaryCrudRequests.cs) | `IsStudio: bool?` в `RoomCreateRequest`/`RoomPatchRequest` |
| [KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | `IsStudioMarker`, ветка студии в Validate, проброс в Create/Patch |
| [KiloImportService.Api/Domain/Mapping/RoomApplySnapshotStore.cs](../KiloImportService.Api/Domain/Mapping/RoomApplySnapshotStore.cs) | `IsStudio` в `HashedMappedFields` |
| [KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperTests.cs](../KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperTests.cs) | 14 unit-тестов `IsStudioMarker` |

---

## 🔗 Связанные доки

- [84-rooms-count-normalization.md](./84-rooms-count-normalization.md) — `ExtractFirstRunOfDigits` (числовые значения)
- [96-rooms-incremental-parallel-apply.md](./96-rooms-incremental-parallel-apply.md) — `HashedMappedFields` и diff-skip
- [68-rooms-import.md](./68-rooms-import.md) — общий обзор импорта Помещений

---

## 🎯 Чек-лист

- [x] `IsStudioMarker` распознаёт точные слова «с»/«ст»/«студ»/«студия» (case-insensitive)
- [x] Числовой `0` отдельной веткой → `IsStudio=true`, `RoomsCount=0`
- [x] Для Kind=Квартира студия НЕ даёт `required_missing`
- [x] `IsStudio` в `HashedMappedFields` — иначе flip-флаг не триггерит PATCH
- [x] Create и Patch оба передают `IsStudio` (drift между ними = повторный импорт всё переписывает)
