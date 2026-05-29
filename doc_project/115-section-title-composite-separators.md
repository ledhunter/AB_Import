# 🏗 Импорт «Помещения»: составной номер корпуса с разделителями `-` / `/` / `\`

## 📋 Описание

`ExtractNumericPart` нормализует значение колонки «№ стр/корп» / «Section» /
«Строение» в `Section.Title` для Visary (`SectionTitleNumeric` в
`MappedValues`). До этой правки она оставляла только цифры и `.` `,`,
обрываясь на любом другом символе:

| Вход        | Было  | Стало  |
|-------------|-------|--------|
| `литер 1-1` | `1`   | `1-1`  |
| `лит 1/1`   | `1`   | `1/1`  |
| `лит 1\1`   | `1`   | `1\1`  |
| `Лит 1.1`   | `1.1` | `1.1`  |
| `корп 2`    | `2`   | `2`    |
| `3.А`       | `3`   | `3`    |

Заказчик: в реальных файлах застройщиков часто встречаются составные номера
корпусов «литер 1-1», «лит 1/1», «лит 1\1». Раньше все они схлопывались в один
корпус «1» — две физически разные секции дома писались как одна, второй
импорт `PATCH`-ил данные первого.

---

## ✅ Правильная реализация

[`RoomsFormImportMapper.cs:1836`](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs#L1836)

```csharp
internal static string? ExtractNumericPart(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    var sb = new System.Text.StringBuilder();
    foreach (var ch in raw)
    {
        // 👇 Расширен набор «продолжающих» символов: было только . и ,
        //    теперь ещё - / \ — для составных номеров корпусов.
        if (char.IsDigit(ch) || ch == '.' || ch == ',' || ch == '-' || ch == '/' || ch == '\\')
            sb.Append(ch == ',' ? '.' : ch);
        else if (sb.Length > 0 && ch != ' ')
            break;
    }
    return sb.Length == 0 ? null : sb.ToString().Trim('.');
}
```

### ⚠️ Важно

- Поведение «префикс отбрасывается до первой цифры» **не изменилось** —
  `литер`/`лит`/`лит.`/`корп`/`корпус`/`стр`/`строение` по-прежнему режутся.
- `Trim('.')` обрезает крайние точки, но **не** крайние `-`/`/`/`\`. На реальных
  данных файлы заказчика не дают таких форм, но если появится — оставляем как
  есть (Visary валидацию пройдёт, дедуп будет работать).
- Функция применяется **только** к `sectionTitle` (см.
  `RoomsFormImportMapper.cs:622`). На номер помещения, ДДУ, дату и пр. не
  влияет.
- Видимость поднята с `private` до `internal` ради unit-теста через
  `InternalsVisibleTo`.

---

## ❌ Типичная ошибка

```csharp
// НЕПРАВИЛЬНО — обрыв на любом не-цифре, не-точке, не-запятой
if (char.IsDigit(ch) || ch == '.' || ch == ',')
    sb.Append(...);
else if (sb.Length > 0 && ch != ' ')
    break;
```

Симптом: «литер 1-1» и «литер 1-2» оба становятся `1` → одна и та же
`Section.Title` в Visary → второй импорт `PATCH`-ит первый.

---

## 📍 Применение в проекте

| Что                                  | Файл                                                         | Строка |
|--------------------------------------|--------------------------------------------------------------|--------|
| Реализация                           | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs#L1836) | 1836 |
| Использование в Validate             | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs#L622) | 622  |
| Использование в Apply (группировка)  | [RoomsFormImportMapper.cs](../KiloImportService.Api/Domain/Mapping/RoomsFormImportMapper.cs#L744) | 744  |
| Unit-тесты                           | [RoomsFormImportMapperTests.cs](../KiloImportService.Api.Tests/Mapping/RoomsFormImportMapperTests.cs#L191) | 191  |

---

## 🎯 Чек-лист расширения списка разделителей

- [ ] Добавить символ в условие `char.IsDigit(ch) || ch == ...`
- [ ] Подтвердить, что `Trim('.')` не съест новый символ на краях
- [ ] Добавить `[InlineData]` в `ExtractNumericPart_KeepsDashSlashBackslashSeparators`
- [ ] Проверить регрессию на старых формах (`лит 1.1`, `корп 2`, `3.А`)
- [ ] Документировать новый разделитель здесь
