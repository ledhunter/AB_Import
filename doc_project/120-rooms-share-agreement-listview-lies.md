# 🔁 Импорт «Помещения» — ещё две дыры угона ДДУ (matched-in-room + listview-лжёт-про-Room)

## 📋 Описание

Дополнение к [doc 119](./119-rooms-share-agreement-no-steal-from-other-room.md):
фильтр `Room null/<=0/==roomId` был добавлен только в strict/loose-find. На
файле `RoomImport/Пример импорта корявый.xlsx` (один и тот же `№ ДДУ = "ап1"`
в трёх разных помещениях листа «Квартиры») заказчик продолжал видеть
«перепривязку» ДДУ к новому помещению. Анализ показал ещё **две дыры**,
через которые угон проходил мимо защиты doc 119:

| № | Где | Что упустили | Симптом |
|---|-----|--------------|---------|
| 1 | `GetShareAgreementsByRoomAsync(roomId)` ветка (matched-in-room) | Фильтра по `Room.ID` нет — доверяли контракту `onetomany/Room?associationId={roomId}`. В проде Visary иногда отдаёт в этом списке ДДУ, реально привязанный к другому помещению | `matchedInRoom = true` → PATCH чужого ДДУ → «угон» |
| 2 | `Raw.Room` из listview (`shareagreement` / `shareagreementall`) | Иногда приходит `null` / `{ID:0}` для ДДУ, который **на самом деле** уже привязан к другому помещению. Источник правды — `GET /crud/shareagreement/{id}`, поле `Room` | Orphan-фильтр strict/loose ошибочно принимает «orphan» → PATCH с переподвязкой → «угон» |

---

## ✅ Правильная реализация

### Дыра 1 — safeguard в matched-in-room

```csharp
// Visary `onetomany/Room` по контракту должно возвращать только ДДУ,
// привязанные к roomId — но проверяем явно (симметрично strict/loose).
var byRoomCandidates = saList
    .Where(a => string.Equals(
        (a.Number ?? string.Empty).Trim(), saNumberTrim,
        StringComparison.OrdinalIgnoreCase))
    .ToList();
saMatch = byRoomCandidates
    .Where(a => a.Room is null
                || a.Room.ID <= 0
                || a.Room.ID == roomId.Value)        // 👈 такой же фильтр
    .OrderByDescending(a => a.ID)
    .FirstOrDefault();
if (saMatch is not null) matchedInRoom = true;
else
{
    saRejectedOwnedByOtherRoom = byRoomCandidates
        .Where(a => a.Room is not null
                    && a.Room.ID > 0
                    && a.Room.ID != roomId.Value)
        .FirstOrDefault();
}
```

### Дыра 2 — CRUD-верификация перед reuse «orphan»-кандидата

```csharp
// После strict/loose: если saMatch не из matched-in-room ветки —
// доуточняем фактическую привязку через `GET /crud/shareagreement/{id}`.
// listview может врать (Room=null), CRUD — авторитативный источник.
if (saMatch is not null && !matchedInRoom)
{
    var saMatchToVerify = saMatch;
    try
    {
        var saFull = await _crud.GetShareAgreementByIdAsync(saMatchToVerify.ID, gct);
        if (saFull?.Room is not null
            && saFull.Room.ID > 0
            && saFull.Room.ID != roomId.Value)
        {
            saRejectedOwnedByOtherRoom ??= new ShareAgreementRaw
            {
                ID     = saFull.ID,
                Number = saFull.Number,
                Room   = saFull.Room,
            };
            saMatch = null;                          // 👈 reuse отвергнут → CREATE
        }
    }
    catch (Exception verifyEx)
    {
        // Сетевая ошибка — продолжаем по listview-данным (не блокируемся
        // на временном сбое CRUD). doc 106 та же стратегия.
        _log.LogWarning(verifyEx, "...", saMatchToVerify.ID, verifyEx.Message);
    }
}
```

### ⚠️ Важно

- **CRUD-верификация выполняется ТОЛЬКО для strict/loose-кандидата**, не
  для matched-in-room. В matched-in-room связь уже подтверждена запросом
  по `associationId={roomId}` — двойная проверка излишняя, +1 запрос на
  каждую строку.
- **`saRejectedOwnedByOtherRoom ??=`** в strict-блоке заменил безусловное
  присваивание: иначе перезаписывался `null`-ом и терялся ID реального
  владельца, зафиксированный в matched-in-room ветке. Лог `ДДУ создан … 
  оставлен у Room.ID=…` опирается на эту переменную.
- **Сетевая ошибка CRUD GET — не блокирующая**: продолжаем по listview-данным
  (как с SA-кэшем в doc 106). Альтернатива — fail-fast — слишком хрупка для
  массового импорта.
- **Регрессия orphan-reuse (doc 76 v1.1) сохраняется**: настоящий orphan
  (Room=null И в listview, И в CRUD) переиспользуется. CRUD-проверка ловит
  только случай, когда listview соврал.

---

## ❌ Старая реализация (до doc 120)

```csharp
// ДЫРА 1: matched-in-room — фильтр по Number, но не по Room.ID
saMatch = saList
    .Where(a => string.Equals((a.Number ?? "").Trim(), saNumberTrim, ...))
    .OrderByDescending(a => a.ID)
    .FirstOrDefault();
if (saMatch is not null) matchedInRoom = true;    // ← даже если Room=88888

// ДЫРА 2: orphan-фильтр в strict/loose доверяет Raw.Room
saMatch = strictCandidates
    .Where(a => a.Room is null || a.Room.ID <= 0 || a.Room.ID == roomId.Value)
    .FirstOrDefault();                            // ← listview соврал → пройдёт
// → PATCH с переподвязкой → «угон» ДДУ у настоящего владельца.
```

Симптом в журнале импорта (доходило до заказчика):

```
ПРИМЕНЕНО  №3  Помещение создано (№1)
            ДДУ найден глобально как orphan (привязан к помещению, №ап1)
ПРИМЕНЕНО  №4  Помещение создано (№2)
            ДДУ найден глобально как orphan (привязан к помещению, №ап1)  ← тот же ДДУ!
```

---

## 🧪 Сценарий-репродукция: `Пример импорта корявый.xlsx`

Лист «Квартиры» — три строки с одинаковым `K=ап1`, но разными
`G (RoomNumber)`/`J (Section)`:

| Стр. | Section | RoomNumber | № ДДУ |
|------|---------|------------|-------|
| 3 | лит. 1 | 1 | ап1 |
| 4 | лит. 1 | 2 | ап1 |
| 5 | лит. 1.1 | п3 | ап1 |

**Ожидаемое поведение**: каждое помещение получает **свой** ДДУ ап1
(уникальный по `Section × Kind × Number × BuildingSection`).

**Без doc 120** при повторном импорте после первой раскатки:

1. Поток лит. 1 строка 3 находит существующий R1 + SA S1 (own, Room=R1).
2. Поток лит. 1.1 строка 5 находит/создаёт R5; `GetShareAgreementsByRoom(R5)`
   пуст; strict-find возвращает S1 c `Raw.Room=null` (listview-баг) →
   фильтр считает orphan → PATCH S1 c `Room=R5` → **угон у R1**.

**С doc 120**: CRUD GET для S1 показывает `Room.ID=R1` → reuse отвергнут,
CREATE S_new для R5. Лог:
```
ДДУ создан (№ап1); существующий ДДУ id=… оставлен у Room.ID=R1
```

---

## 📍 Применение в проекте

| Компонент | Файл | Что изменилось |
|-----------|------|----------------|
| Маппер RoomsForm — matched-in-room | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | Фильтр `Room null/<=0/==roomId` + запоминаем «отвергнутого» в `saRejectedOwnedByOtherRoom` |
| Маппер RoomsForm — strict-find | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | `if (saRejected is null)` перед присваиванием — не теряем владельца из matched-in-room |
| Маппер RoomsForm — после strict/loose | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | Новый блок CRUD-верификации `GetShareAgreementByIdAsync` (не для matched-in-room) |
| Конструктор тестов | [RoomsFormImportMapperApplyTests.cs](../KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperApplyTests.cs) | Дефолтный mock `GetShareAgreementByIdAsync → Room=null` (чтобы существующие тесты не падали на NRE) |
| 3 новых теста | [RoomsFormImportMapperApplyTests.cs](../KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperApplyTests.cs) | `ByRoomListReturnsForeignSa_IsNotStolen` + `ListviewShowsOrphanButCrudShowsForeignRoom_IsNotStolen` + `TrueOrphan_ListviewAndCrudAgree_OrphanReused` |

---

## 🎯 Чек-лист

- [x] matched-in-room фильтрует по `Room.ID` симметрично strict/loose
- [x] CRUD GET `shareagreement/{id}` — источник правды о привязке
- [x] CRUD-верификация выполняется только для strict/loose (matched-in-room — нет)
- [x] Сетевая ошибка CRUD GET — продолжаем по listview-данным, не блокируемся
- [x] `saRejectedOwnedByOtherRoom` не перезаписывается null'ом — пишет первый обнаруженный «владелец»
- [x] Настоящий orphan (Room=null И в listview, И в CRUD) — переиспользуется (doc 76 v1.1 сохранён)
- [x] 3 регрессионных теста зелёные (32/32 Apply, 388/388 suite)

---

## 💡 Урок

«Listview-ответ говорит, что поле пустое» ≠ «поле пустое». Listview-выдача
зависит от запрошенных Columns, прав доступа, бага индекса — данные могут
быть **неполными**, даже если поле упомянуто в `Columns`. Когда от
значения поля зависит **деструктивная операция** (PATCH с переподвязкой),
listview-данные — только подсказка; перед action — авторитативный запрос
через CRUD GET. Стоимость +1 HTTP-запрос на сомнительный случай несопоставимо
ниже стоимости разбора инцидента «куда уехал ДДУ».
