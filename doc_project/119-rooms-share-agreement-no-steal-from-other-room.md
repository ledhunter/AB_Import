# 🔁 Импорт «Помещения» — ДДУ, привязанный к другому помещению, не «угоняется»

## 📋 Описание

Если глобальный поиск ДДУ ([doc 76](./76-share-agreement-dedup.md))
находит ДДУ с тем же бизнес-ключом (`Number + Kind + ConditionalNumber +
Stage + Project`), **но он уже привязан к другому, реально существующему
помещению** (`Room.ID > 0 && Room.ID != roomId`), маппер **не**
перевязывает его на наше помещение. Вместо этого создаётся **новый ДДУ**
для текущего помещения, а в журнале строки появляется метка:

> `ДДУ создан (№…); существующий ДДУ id=… оставлен у Room.ID=…`

**Зачем:** до правки маппер делал `PatchShareAgreement` чужого ДДУ,
переставляя `Room/Project/Site/Stage` на новые значения. В отчёте
заказчика появлялись действия вида:

> `ДДУ найден глобально (привязан к новому помещению, №ап1)`

Это «угоняло» договор у его законного помещения (и потенциально из
другого проекта/этапа), создавая иллюзию правильного импорта, тогда как
на стороне Visary одна реальная сущность ДДУ молча мигрировала на чужую
квартиру. Регрессия проявилась на текстовых номерах квартир после
[doc 118](./118-rooms-room-number-accept-any-string.md): сходные
`ConditionalNumber` («п1»/«п3»/«п4») в strict-find чаще пересекались.

---

## ✅ Правильная реализация

### Strict-find (Шаг А)

```csharp
// Visary listview/shareagreementall с фильтром по полному бизнес-ключу.
var foundStrict = await _listView.FindShareAgreementsAsync(
    number: saNumber, roomKindId: kindId, conditionalNumber: roomNumber,
    stageNumber: stageNumberForSa, projectNumber: projectNumberForSa, gct);

var strictCandidates = foundStrict.Data
    .Where(a => string.Equals(
        (a.Number ?? string.Empty).Trim(), saNumberTrim,
        StringComparison.OrdinalIgnoreCase))
    .Where(a => kindId is null || a.RoomKindRef?.ID == kindId)
    .OrderByDescending(a => a.ID)
    .ToList();

// doc 119: принимаем только orphan (Room null/<=0) или уже-наш Room.
// Кандидата с Room.ID указывающим на чужое помещение — не «угоняем».
saMatch = strictCandidates
    .Where(a => a.Room is null
                || a.Room.ID <= 0
                || a.Room.ID == roomId.Value)
    .FirstOrDefault();

if (saMatch is null)
{
    saRejectedOwnedByOtherRoom = strictCandidates
        .Where(a => a.Room is not null
                    && a.Room.ID > 0
                    && a.Room.ID != roomId.Value)
        .FirstOrDefault();
}
```

### Loose-find (Шаг Б) — то же правило

```csharp
saMatch = candidates
    .Where(a => a.Room is null
                || a.Room.ID <= 0
                || a.Room.ID == roomId.Value)   // ← было: только orphan-фильтр
    .OrderByDescending(a => a.ID)
    .FirstOrDefault();

// На случай если loose ничего не нашёл, а strict тоже — фиксируем для лога.
if (saMatch is null && saRejectedOwnedByOtherRoom is null)
{
    saRejectedOwnedByOtherRoom = candidates
        .Where(a => a.Room is not null && a.Room.ID > 0 && a.Room.ID != roomId.Value)
        .FirstOrDefault();
}
```

### CREATE-ветка — расширенная метка

```csharp
if (saMatch is null)
{
    var saCreated = await _crud.CreateShareAgreementAsync(...);
    if (saRejectedOwnedByOtherRoom is not null)
    {
        Log(sheet, row,
            $"ДДУ создан (№{saNumber}); существующий ДДУ id={saRejectedOwnedByOtherRoom.ID} оставлен у Room.ID={saRejectedOwnedByOtherRoom.Room?.ID}");
    }
    else
    {
        Log(sheet, row, $"ДДУ создан (№{saNumber})");
    }
}
```

### Orphan-ветка (`saMatch != null && isOrphan`)

Лог переформулирован, чтобы не путать с «угоном»:

```csharp
// БЫЛО:
// "ДДУ найден глобально (привязан к новому помещению, №…)"
// СТАЛО (только orphan'ы, Room null/<=0):
"ДДУ найден глобально как orphan (привязан к помещению, №…)"
```

---

### ⚠️ Важно

- Match-условие теперь **симметричное** в strict и loose: orphan ИЛИ
  уже-наш `roomId`. Это значит, что повторный импорт того же файла
  по-прежнему найдёт «свой» ДДУ через strict (уже привязан к нашему Room)
  и сделает PATCH с обновлёнными полями — diff/idempotency сохраняется.
- Filter `Room.ID == roomId.Value` важен **именно** для повторных
  импортов; без него strict-find упирался бы в собственный ДДУ от
  предыдущего прогона и считал его «чужим».
- Orphan-кейс (Room null/0) сохраняется как валидный — `doc 76 v1.1`
  не отменяется. ДДУ, заведённый «вручную» без привязки, всё ещё
  переиспользуется первым импортом, который подойдёт по бизнес-ключу.
- `saRejectedOwnedByOtherRoom` живёт **в пределах одной строки** —
  переменная объявляется внутри per-row блока, отсева между строками
  нет.

---

## ❌ Старая реализация (отменено)

```csharp
// НЕПРАВИЛЬНО — strict-find не различал orphan/чужой Room, PATCH-ил
// найденный ДДУ с переподвязкой на новое помещение, тем самым «угоняя»
// его у предыдущего владельца.
saMatch = foundStrict.Data
    .Where(a => string.Equals(...))
    .Where(a => kindId is null || a.RoomKindRef?.ID == kindId)
    .OrderByDescending(a => a.ID)
    .FirstOrDefault();   // ← Room.ID не проверялся

// …
await _crud.PatchShareAgreementAsync(saMatch.ID, new ShareAgreementPatchRequest
{
    RoomID = roomId.Value,
    Room   = new VisaryRef { ID = roomId.Value },
    // ↑ перезаписывало `Room` на новый, без оглядки на старый владельца.
});
```

Симптом в журнале (из отчёта заказчика):
```
ПРИМЕНЕНО  №5  Помещение создано (№п3)
            ДДУ найден глобально (привязан к новому помещению, №ап1)
```

---

## 📍 Применение в проекте

| Компонент | Файл | Что изменилось |
|-----------|------|----------------|
| Маппер RoomsForm — strict-find | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | `strictCandidates` + фильтр `Room null/<=0/==roomId`; запоминаем «owned by other» |
| Маппер RoomsForm — loose-find | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | Тот же фильтр (раньше только orphan-only) |
| Маппер RoomsForm — CREATE | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | Развилка лога: `создан` vs `создан; существующий id=… оставлен у Room.ID=…` |
| Маппер RoomsForm — orphan-ветка | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs) | Лог: `найден глобально как orphan` вместо `привязан к новому помещению` |
| Test | [RoomsFormImportMapperApplyTests.cs](../KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperApplyTests.cs) | Новый `ApplyAsync_ShareAgreementOwnedByOtherRoom_IsNotStolen_NewSaCreated` |

---

## 🎯 Чек-лист

- [x] strict-find фильтрует по Room.ID (null/<=0/==roomId)
- [x] loose-find — тот же фильтр; раньше был orphan-only (Room null/<=0)
- [x] orphan-ДДУ продолжают переиспользоваться ([doc 76 v1.1](./76-share-agreement-dedup.md))
- [x] Повторный импорт того же файла → strict-find матчит «уже наш» ДДУ → PATCH с актуальными полями (idempotency сохраняется)
- [x] Чужой ДДУ — НЕ трогаем; PATCH вызывается только для orphan/уже-нашего
- [x] В журнале строки сохраняется ID «отверженного» кандидата для диагностики
- [x] Новый тест `ApplyAsync_ShareAgreementOwnedByOtherRoom_IsNotStolen_NewSaCreated` зелёный (29 Apply-тестов)
- [x] Старый orphan-test продолжает работать — orphan переиспользуется

---

## 💡 Урок

«Поиск по полному бизнес-ключу» гарантирует совпадение **по полям**, но
не по **состоянию связи**. Если в Visary одно и то же значение
`Number + Kind + Cond + Stage + Project` теоретически может встретиться
у двух разных Room'ов (например, пользователь скопировал номер ДДУ при
переоформлении), strict-find вернёт обоих, и любой `FirstOrDefault()`
без проверки `Room.ID` рискует переподвязать не то. Когда «глобальный»
поиск НЕ ограничен Site/Room — добавлять явный фильтр по конечной связи
(`Room is null || Room.ID == ourRoomId`) и в else-ветку явный лог
«найдено, но принадлежит чужому Room — создаём новое».
