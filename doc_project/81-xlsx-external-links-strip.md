# 🔗 Парсинг XLSX с внешними ссылками (external workbook links)

## 📋 Описание

**Статус**: 🟢 v1.0 — устойчивость `XlsxParser` к файлам с external workbook references.
**Дата**: 2026-05-14
**Контекст**: Регрессия при импорте «Параметры к переносу в АБ.xlsx» — файл содержит формулы / defined names со ссылками на чужую книгу в SharePoint (`'https://spo-global.kpmg.com/.../[CAPRAPSCHED.xls]THREE VARIABLES'!$A$1:$M$65536`). ClosedXML падает при открытии книги:

```
Не удалось прочитать XLSX: Unable to determine token for
''https://spo-global.kpmg.com/Investic/nio/CAPERS/[CAPRAPSCHED.xls]THREE VARIABLES'!$A$1:$M$65536' at index 0.
```

ClosedXML парсит формулы при загрузке (включая `RefersTo` у `definedName`). URL в качестве «токена формулы» он не понимает и валит весь workbook ctor.

---

## ✅ Правильная реализация

Предобработка XLSX-zip с удалением внешних связей. Кэшированные значения ячеек (`<v>` в sheet XML) при этом сохраняются — а нам нужны только они, не формулы.

### Поток

```
ParseAsync(stream)
   │
   ├─ buffered := MemoryStream(stream)                      // буферизуем для retry
   │
   ├─ try  new XLWorkbook(buffered)                          // ⓿ как обычно
   │
   ├─ catch ex when IsExternalLinkError(ex):
   │       stripped := StripExternalLinks(buffered)          // ① zip-уровень
   │       workbook := new XLWorkbook(stripped)              // ② повторная попытка
   │
   └─ ParseTabular / ParseKeyValueVertical → ParseResult
```

### Что вырезаем в zip ([XlsxParser.cs:StripExternalLinks](../KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs))

| Часть архива | Что | Зачем |
|---|---|---|
| `xl/externalLinks/*.xml` | Удаляем целиком | Описание чужих книг, кэш их значений — нам не нужны |
| `xl/_rels/externalLink*.rels` | Удаляем целиком | Их .rels |
| `xl/workbook.xml` → `<externalReferences>` | regex-Replace | Регистрация external book index `[1]`, `[2]`, … |
| `xl/workbook.xml` → `<definedName>` с URL или `[file]` в RefersTo | regex-Replace | Именованные диапазоны, ссылающиеся на внешнюю книгу |
| `xl/_rels/workbook.xml.rels` → `Relationship` с `externalLink` в Type | regex-Replace | Связь workbook → externalLinks |

### Ключевая идея

Кэшированные значения ячеек хранятся в `<sheet>/sheetData/row/c/v` независимо от того, есть формула или нет. После вырезания внешних связей формулы в листах с `[1]Sheet1!...` становятся «оторванными», но `cell.GetString()` берёт `<v>` (кэш). Поэтому импорт продолжает видеть нужные данные.

### ⚠️ Важно

- **Не используем «всегда preprocessing»**: zip-вскрытие — дорогая операция (~10–50 ms на типичных файлах). Только при `IsExternalLinkError`. Обычные XLSX без внешних ссылок не платят за это.
- **`ZipArchiveMode.Update` требует write-access к stream** — поэтому буферизуем оригинальный `Stream` в `MemoryStream` (входной stream может быть `FileStream`/`HttpStream`, и его обновлять рискованно).
- **Перезапись entry через `Open()` ненадёжна**: если новая длина меньше старой, хвост старого содержимого остаётся в архиве. `ReplaceXmlEntry` всегда делает `Delete()` + `CreateEntry()`.
- **Регулярки осторожно, но достаточно**: внутри `definedName` контент — escaped XML, без вложенных тегов; URL и `[file]` гарантированно содержат символы, которых нет в нормальных диапазонных RefersTo (`Sheet1!$A$1`). False-positives маловероятны.
- **`IsExternalLinkError` совпадает по тексту сообщения**: при апгрейде ClosedXML формулировка может измениться — следить за тестами.

---

## ❌ Типичная ошибка

### 1. Перехватить и просто отдать `parse_failure`

```csharp
// НЕПРАВИЛЬНО — пользователь получит «не удалось прочитать XLSX», но реальные данные
// в файле есть, мы их просто не достали.
catch (Exception ex) {
    return new ParseResult([], [], [new ParseError(null, $"Не удалось прочитать XLSX: {ex.Message}")]);
}
```

```csharp
// ПРАВИЛЬНО — на конкретной известной ошибке делаем cleanup и retry.
catch (Exception ex) when (IsExternalLinkError(ex)) {
    using var stripped = StripExternalLinks(buffered);
    workbook = new XLWorkbook(stripped);
}
```

### 2. Открывать stream несколько раз без буферизации

```csharp
// НЕПРАВИЛЬНО — после неудачной первой попытки stream может быть в произвольной позиции,
// или вообще non-seekable (HttpStream). Вторая попытка прочитает мусор.
try { workbook = new XLWorkbook(stream); }
catch { workbook = new XLWorkbook(stream); }   // 💥 InvalidDataException
```

```csharp
// ПРАВИЛЬНО — буферизуем один раз, дальше работаем с MemoryStream.
using var buffered = new MemoryStream();
stream.CopyTo(buffered);
// retry с buffered.Position = 0
```

### 3. Перезаписать XML через `entry.Open()` без удаления

```csharp
// НЕПРАВИЛЬНО — если новый текст короче, хвост старого останется в архиве,
// и парсер XML словит "несоответствие тэгов" или «extra data after root element».
using var s = entry.Open();
using var w = new StreamWriter(s);
w.Write(cleaned);
```

```csharp
// ПРАВИЛЬНО — снести entry и создать заново.
entry.Delete();
var newEntry = zip.CreateEntry(name);
using var ws = newEntry.Open();
using var w = new StreamWriter(ws);
w.Write(cleaned);
```

---

## 📍 Применение в проекте

| Компонент | Файл | Ключевой метод |
|---|---|---|
| Парсер XLSX | [XlsxParser.cs](../KiloImportService.Api/Domain/Importing/Parsers/XlsxParser.cs) | `ParseAsync`, `StripExternalLinks`, `IsExternalLinkError`, `ReplaceXmlEntry` |
| Использование | finmodel / rooms / любой импорт XLSX | Прозрачно — fix ниже уровня раскладок |

---

## 🎯 Чек-лист

- [ ] При апгрейде ClosedXML — проверить текст сообщения «Unable to determine token»: если поменялся, актуализировать `IsExternalLinkError`.
- [ ] При появлении новых видов «битых» формул (например, ссылок на defined name из другого workbook без URL) — добавить кейсы в `IsExternalLinkError`.
- [ ] Файл вида «с внешними ссылками» добавить в фикстуры [XlsxParserTests.cs](../KiloImportService.Api.Tests/Importing/XlsxParserTests.cs) — пока тест не зафиксирован.
