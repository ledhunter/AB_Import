# 🏠 Импорт «Помещения»: резолв многословных листов («Коммерческие помещения», «Кладовые»)

## 📋 Описание

`ResolveKindBySheetName` сопоставляет имя листа («Квартиры», «Кладовые»,
«Коммерческие помещения», …) с Title справочника Visary RoomKind. Раньше
эвристика брала только одну последнюю букву и подходила лишь для
односложных имён в мн.ч. («Квартиры», «Машиноместа»). В реальных файлах
(см. скриншот заказчика 2026-05-29) встречаются:

- `Кладовые` (мн.ч., прил. ж.р.) — справочник содержит `Кладовая`,
- `Коммерческие помещения` (многословное) — справочник содержит
  `Коммерческое помещение`.

Без правки оба листа целиком пропускались как «исторические снапшоты»
(см. [doc 90](./90-rooms-skip-unknown-kind-sheets.md)) — пользователь видел
импорт «успешно завершён», но половина помещений из файла в Visary не
попадала.

---

## ✅ Правильная реализация

[`RoomsFormImportMapper.cs:1646`](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs#L1646)

```csharp
internal static (int? Id, string? Title) ResolveKindBySheetName(
    string sheetName, IDictionary<string, int> kindByTitle)
{
    // 1) Direct match.
    if (kindByTitle.TryGetValue(name, out var id1))
        return (id1, FindMatchingTitle(name, kindByTitle));

    // 2) Plural-trim per word + декартово произведение.
    var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var perWord = words.Select(w => SingularCandidates(w).Distinct().ToList()).ToList();
    if (total > 512) return (null, null);  // safeguard

    foreach (var combo in CartesianProduct(perWord))
        if (kindByTitle.TryGetValue(string.Join(' ', combo), out var idN))
            return (idN, ...);
    return (null, null);
}

private static IEnumerable<string> SingularCandidates(string word)
{
    yield return word;
    // 2-буквенные plural-окончания берём ПЕРЕД 1-буквенными,
    // иначе «Кладовые» застряло бы на ложном «Кладовы».
    if (last2 == "ые") yield return head2 + "ая";   // Кладовые  → Кладовая
    if (last2 == "ие") yield return head2 + "ое";   // Коммерческие → Коммерческое
    if (last2 == "ия") yield return head2 + "ие";   // помещения → помещение
    // 1-буквенные (старая логика, doc 90):
    if ("аяыиеёАЯЫИЕЁ".Contains(last1)) yield return head1;
    if (last1 == 'ы') yield return head1 + "а";
    if (last1 == 'и') yield return head1 + "я";
    if (last1 == 'а') yield return head1 + "о";
}
```

### ⚠️ Важно

- **Кандидаты ед.ч. безопасны как «ложные»** — если кандидат не совпал
  ни с одним Title в справочнике, он молча отбрасывается. Главное —
  не пропустить корректный матч.
- **Substring-fallback по-прежнему запрещён** (doc 90): иначе
  `Кв_01.04.26` совпало бы с `Квартира`.
- **Защита от комбинаторного взрыва**: при >512 комбинаций per-word
  возвращаем `(null, null)`. Реальные имена дают ≤64.
- **Порядок суффиксов критичен**: сначала 2-буквенные (`ые/ие/ия`),
  потом 1-буквенные. Иначе `Кладовые → срез 'е' → Кладовы` создаст
  ложный шорт-цикл, а «Кладовая» не попадёт в перебор.

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — режем только последнюю букву, многословные не обрабатываем
var last = name[^1];
if ("аяыиеё".Contains(last))
    candidates.Add(name[..^1]);
```

Симптомы для файла «Пример импорта все типы помещений.xlsx»:
- лист `Кладовые` (3 строки) → пропущен, в Visary не уехал,
- лист `Коммерческие помещения` (3 строки) → пропущен,
- в отчёте 26 валидных из 33 — заказчик: «не обрабатываются листы».

---

## 📍 Применение в проекте

| Что | Файл | Строка |
|-----|------|--------|
| Резолв | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs#L1646) | 1646 |
| Helper `SingularCandidates` | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs#L1693) | 1693 |
| Helper `CartesianProduct` | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs#L1720) | 1720 |
| Tests | [RoomsFormImportMapperTests.cs](../KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperTests.cs#L195) | 195 |

---

## 🎯 Чек-лист при добавлении нового plural-окончания

- [ ] Добавить `yield return` в `SingularCandidates` ВЫШЕ 1-буквенных, если суффикс 2-буквенный
- [ ] `[InlineData]` в `ResolveKindBySheetName_PluralSingleWord` / `_PluralMultiWord`
- [ ] Подтвердить отсутствие регрессии — старые формы (`Квартиры`, `Машиноместа`, `Апартаменты`) продолжают резолвиться
- [ ] Проверить, что `Кв_01.04.26` / служебные листы по-прежнему `null` (substring-fallback запрещён)
- [ ] Документировать новый суффикс в этом файле
