# 🧪 Backend тесты (xUnit)

## 📋 Описание

Тестовый проект `KiloImportService.Api.Tests` (xUnit 2.9.3, EF Core InMemory 9.0.4,
Xunit.SkippableFact 1.5.23) покрывает критичные слои backend без зависимости от
PostgreSQL — все тесты можно запустить локально без docker.

> ⚠️ **Важно**: тесты в этом проекте уже **поймали реальный баг** в парсерах
> (глотание `OperationCanceledException` через общий `catch (Exception)`).
> Это эталонный пример пользы тестов на «boring» инфраструктурные слои.

---

## 🏗️ Структура

```
KiloImportService.Api.Tests/
├── KiloImportService.Api.Tests.csproj
├── Importing/
│   ├── CsvParserTests.cs           — 7 тестов
│   ├── XlsxParserTests.cs          — 6 тестов (5 SkippableFact, 1 [Fact])
│   └── FileParserFactoryTests.cs   — 3 теста
├── Pipeline/
│   ├── LocalFileStorageTests.cs    — 5 тестов
│   └── ImportSessionCancellationTests.cs — 5 тестов
└── Mapping/
    └── RoomsImportMapperTests.cs   — 19 тестов (включая Theory с 9 кейсами)
```

**Зависимости:**

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="9.0.4" />
<PackageReference Include="Xunit.SkippableFact" Version="1.5.23" />
<!-- Стандартный xUnit + Microsoft.NET.Test.Sdk -->
```

---

## ✅ Правильные паттерны

### Паттерн 1: parser-тесты на in-memory MemoryStream

```csharp
public class CsvParserTests
{
    private readonly CsvParser _parser = new();

    private static Stream Csv(string content) =>
        new MemoryStream(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task ParsesHeadersAndRows_WithCommaDelimiter()
    {
        var csv = "Name,Age\nAlice,30\nBob,25\n";
        var result = await _parser.ParseAsync(Csv(csv));

        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "Name", "Age" }, result.Headers);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Alice", result.Rows[0].Cells["Name"]);
    }
}
```

### Паттерн 2: SkippableFact для known-issue окружения

XLSX-тесты создают XLSX через ClosedXML, который на некоторых Windows-машинах
падает с `Access to the path 'C:\WINDOWS\Fonts\Mysql' is denied` (баг
SkiaSharp + специфическая папка шрифтов). Чтобы тесты не блокировали разработку,
используем `[SkippableFact]` + ленивую probe:

```csharp
public class XlsxParserTests
{
    /// <summary>
    /// Если SkiaSharp/font-scan недоступен — кэшируем причину Skip.
    /// Probe делает то же, что реальный BuildXlsx (Save + Load) — иначе
    /// проблема может не воспроизвестись на упрощённом примере.
    /// </summary>
    private static readonly Lazy<string?> _skipReason = new(TryProbeClosedXml);
    private static string? SkipReason => _skipReason.Value;

    private static string? TryProbeClosedXml()
    {
        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("probe");
            ws.Cell(1, 1).Value = "H";
            ws.Cell(2, 1).Value = "V";
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            ms.Position = 0;
            using var loaded = new XLWorkbook(ms);
            _ = loaded.Worksheets.First().RangeUsed()?.RowCount();
            return null;
        }
        catch (Exception ex)
        {
            return $"ClosedXML/SkiaSharp недоступен ({ex.GetType().Name}): {ex.Message}";
        }
    }

    [SkippableFact]
    public async Task ParsesHeadersAndRows()
    {
        Skip.IfNot(SkipReason is null, SkipReason);
        // ... тест ...
    }
}
```

### Паттерн 3: in-memory EF для маппера

`RoomsImportMapper.ValidateAsync` принимает `VisaryDbContext`. Для тестов нет
необходимости в Postgres — InMemory provider достаточен (мы НЕ проверяем SQL,
а проверяем логику валидации).

```csharp
private static VisaryDbContext CreateInMemoryDb(
    IEnumerable<RoomKind>? kinds = null,
    IEnumerable<ConstructionSite>? sites = null)
{
    var opts = new DbContextOptionsBuilder<VisaryDbContext>()
        .UseInMemoryDatabase("visary-" + Guid.NewGuid().ToString("N"))  // 👈 уникальное имя на тест
        .Options;
    var db = new VisaryDbContext(opts);
    if (kinds is not null) db.RoomKinds.AddRange(kinds);
    if (sites is not null) db.ConstructionSites.AddRange(sites);
    db.SaveChanges();
    return db;
}

[Fact]
public async Task Validate_ValidRow_ProducesValidMappedRow()
{
    using var db = CreateInMemoryDb(
        kinds: new[] { new RoomKind { Id = 1, Title = "Квартира" } },
        sites: new[] { new ConstructionSite { Id = 100, Title = "Корпус 1" } });

    var rows = new[] { Row(2, ("Тип", "Квартира"), ("Площадь по проекту", "50,5")) };
    var result = await _mapper.ValidateAsync(Ctx(), rows, db, CancellationToken.None);

    var mr = Assert.Single(result.Rows);
    Assert.True(mr.IsValid);
    Assert.Equal(50.5, mr.MappedValues.RootElement.GetProperty("ProjectArea").GetDouble());
}
```

### Паттерн 4: временная папка в LocalFileStorageTests с Dispose

```csharp
public class LocalFileStorageTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;

    public LocalFileStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kilo-tests-" + Guid.NewGuid().ToString("N"));
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ImportStorage:Path"] = _root })
            .Build();
        _storage = new LocalFileStorage(cfg, NullLogger<LocalFileStorage>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
    // ... тесты ...
}
```

### Паттерн 5: Theory + InlineData для семейств кейсов

```csharp
[Theory]
[InlineData("Да", true)]
[InlineData("yes", true)]
[InlineData("истина", true)]
[InlineData("1", true)]
[InlineData("Нет", false)]
[InlineData("0", false)]
[InlineData("", false)]
public async Task Validate_ParsesIsStudio_FromVariousFormats(string raw, bool expected)
{
    using var db = CreateInMemoryDb(...);
    var rows = new[] { Row(2, ("Тип", "Квартира"), ("Площадь по проекту", "50"), ("Студия", raw)) };
    var result = await _mapper.ValidateAsync(Ctx(), rows, db, CancellationToken.None);

    var mr = Assert.Single(result.Rows);
    Assert.True(mr.IsValid);
    Assert.Equal(expected, mr.MappedValues.RootElement.GetProperty("IsStudio").GetBoolean());
}
```

---

## ❌ Типичные ошибки

### Ошибка 1: общая InMemoryDatabase между тестами

```csharp
// НЕПРАВИЛЬНО — все тесты используют одну БД
.UseInMemoryDatabase("visary")
```

**Что произойдёт:** тест 1 кладёт RoomKind с Id=1, тест 2 ожидает пустую БД и
не находит свою row. Ошибки зависят от порядка выполнения, нестабильны.

**Правильно:** уникальное имя на каждый тест: `"visary-" + Guid.NewGuid().ToString("N")`.

### Ошибка 2: `[Fact]` для known-issue вместо `[SkippableFact]`

```csharp
// НЕПРАВИЛЬНО
[Fact]
public void XlsxTest()
{
    if (FontsBroken) return;  // тест помечен как passed, хотя ничего не проверил
}
```

**Правильно:** `[SkippableFact] + Skip.IfNot(condition, reason)` — тест в отчёте
помечается как «skipped», а не «passed».

### Ошибка 3: probe слишком простой

```csharp
// НЕПРАВИЛЬНО — probe не воспроизводит проблему
private static string? TryProbe()
{
    try { using var wb = new XLWorkbook(); return null; }
    catch (Exception ex) { return ex.Message; }
}
```

**Что произойдёт:** баг ClosedXML/SkiaSharp возникает при `wb.SaveAs(ms)` с
несколькими колонками + последующем `new XLWorkbook(stream)`. Простой
`new XLWorkbook()` не проверяет font-scan → SkipReason всегда null → тесты
падают при реальном тесте.

**Правильно:** probe должен быть **идентичен** реальному тесту по сложности
(несколько колонок, Save + Load).

### Ошибка 4: тест парсера без проверки cancellation

```csharp
// Без этого теста баг "catch (Exception) глотает OCE" не был бы найден.
[Fact]
public async Task RespectsCancellationToken()
{
    using var cts = new CancellationTokenSource();
    cts.Cancel();   // 👈 уже отменён ДО старта парсинга
    await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => _parser.ParseAsync(Csv("A,B\n1,2\n"), cts.Token));
}
```

**Зачем:** проверяет, что парсер реагирует на ct и не превращает отмену в
«ошибку парсинга».

---

## 📊 Запуск и интерпретация

```powershell
# Все тесты
dotnet test KiloImportService.Api.Tests/KiloImportService.Api.Tests.csproj

# Конкретный класс
dotnet test --filter "FullyQualifiedName~CsvParserTests"

# Конкретный тест
dotnet test --filter "FullyQualifiedName~CsvParserTests.RespectsCancellationToken"
```

**Output:**
```
Пройден!: не пройдено 0, пройдено 45, пропущено 5, всего 50, длительность 1 s.
```

- **passed = 45** — все «реальные» тесты прошли.
- **skipped = 5** — XLSX-тесты пропущены (на этой машине ClosedXML/SkiaSharp).
  На CI с чистой папкой `Fonts` они тоже пройдут.
- **failed = 0** — желаемое состояние.

---

## 📍 Применение в проекте

| Слой | Тестовый файл | Кол-во кейсов |
|------|---------------|---------------|
| `CsvParser` | `KiloImportService.Api.Tests/Importing/CsvParserTests.cs` | 7 |
| `XlsxParser` | `KiloImportService.Api.Tests/Importing/XlsxParserTests.cs` | 6 (5 Skippable) |
| `FileParserFactory` + `FileFormatExtensions` | `KiloImportService.Api.Tests/Importing/FileParserFactoryTests.cs` | 3 |
| `LocalFileStorage` | `KiloImportService.Api.Tests/Pipeline/LocalFileStorageTests.cs` | 5 |
| `ImportSessionCancellation` | `KiloImportService.Api.Tests/Pipeline/ImportSessionCancellationTests.cs` | 5 |
| `RoomsImportMapper` | `KiloImportService.Api.Tests/Mapping/RoomsImportMapperTests.cs` | 19 |
| **Итого** | | **45 passed / 5 skipped** |

---

## 🎯 Чек-лист при добавлении нового теста

- [ ] Тестовый класс в подпапке по слою (`Importing/`, `Pipeline/`, `Mapping/`)
- [ ] In-memory зависимости (`MemoryStream`, `InMemoryDatabase` с уникальным именем)
- [ ] `[Fact]` или `[Theory(..., InlineData(...))]` для семейств
- [ ] Если зависит от хрупкого окружения — `[SkippableFact]` + `Skip.IfNot(probe, reason)`
- [ ] Если ресурсы (папки) — реализуй `IDisposable` для cleanup
- [ ] Один тест проверяет **одно** утверждение (Arrange / Act / Assert разделены явно)
- [ ] Если тест поймал баг — добавь регрессионный тест (повторение легко)

## 🎯 Чек-лист при добавлении нового маппера/сервиса

- [ ] Создан соответствующий `*Tests.cs` файл
- [ ] Тесты на happy path + типичные ошибки + cancellation
- [ ] `dotnet test` проходит локально
- [ ] Тесты документированы — рядом со сложным кейсом комментарий «зачем»
