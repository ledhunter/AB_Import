using KiloImportService.Api.Domain.Mapping;

namespace KiloImportService.Api.Tests.Mapping;

/// <summary>
/// Тесты на нормализацию значения «Колич. комнат» в импорте Помещений.
/// Реальные пользовательские файлы содержат значения вида «1 к.», «п1», «1п»,
/// «10 к», «3-к» — раньше они отвергались как `invalid_number`. Теперь
/// маппер вытаскивает первую непрерывную группу цифр.
/// </summary>
public class RoomsFormImportMapperTests
{
    [Theory]
    // Базовые: чистое число / пусто / пробельная строка.
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    [InlineData("0", 0)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    // Просьба заказчика: суффиксы «к», «к.», «п» — берём ведущую цифру.
    [InlineData("1 к.", 1)]
    [InlineData("1 к", 1)]
    [InlineData("1к", 1)]
    [InlineData("2 к.", 2)]
    [InlineData("3 ком.", 3)]
    [InlineData("3-к", 3)]
    [InlineData("10 к", 10)]
    // «п1» / «1п» — префиксная буква не должна мешать.
    [InlineData("п1", 1)]
    [InlineData("1п", 1)]
    // Не-цифровые значения: «студия», прочерки, тире.
    [InlineData("студия", null)]
    [InlineData("—", null)]
    [InlineData("-", null)]
    [InlineData("апартамент", null)]
    // Берём ПЕРВЫЙ run — «1 к. 2» это однушка с дополнительной заметкой,
    // а не «комната 12». Так пользователь явно подразумевал «1»; склейка
    // «12» вводила бы фейковые двушки.
    [InlineData("1 к. 2", 1)]
    public void ExtractFirstRunOfDigits_NormalizesUserInput(string? raw, int? expected)
    {
        var actual = RoomsFormImportMapper.ExtractFirstRunOfDigits(raw);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Маркеры студии для колонки «Колич. комнат» (doc 108). Точные слова
    /// «с»/«ст»/«студ»/«студия» (case-insensitive, после Trim). Любые другие
    /// строки — НЕ студия, чтобы «секция»/«склад»/«стандарт» не получали
    /// IsStudio=true по substring-совпадению. Числовой 0 здесь не маркер —
    /// он распознаётся отдельной веткой в Validate.
    /// </summary>
    [Theory]
    [InlineData("с",      true)]
    [InlineData("ст",     true)]
    [InlineData("студ",   true)]
    [InlineData("студия", true)]
    [InlineData("Студия", true)]
    [InlineData("СТУДИЯ", true)]
    [InlineData(" студия ", true)]   // Trim
    [InlineData("",        false)]
    [InlineData(null,      false)]
    [InlineData("0",       false)]   // числовой 0 — отдельная ветка в Validate
    [InlineData("1",       false)]
    [InlineData("студия+", false)]   // строгое сравнение по полному слову
    [InlineData("секция",  false)]
    [InlineData("стандарт",false)]
    [InlineData("сторож",  false)]
    public void IsStudioMarker_RecognizesOnlyExactStudioWords(string? raw, bool expected)
    {
        Assert.Equal(expected, RoomsFormImportMapper.IsStudioMarker(raw));
    }

    /// <summary>
    /// «Вывод (да/нет)» (doc 113) — нормализация да/нет/синонимов.
    /// Неизвестное значение возвращает <c>null</c> (НЕ ошибка): поле опциональное.
    /// </summary>
    [Theory]
    [InlineData("да",     true)]
    [InlineData("Да",     true)]
    [InlineData("ДА",     true)]
    [InlineData(" да ",   true)]
    [InlineData("yes",    true)]
    [InlineData("y",      true)]
    [InlineData("true",   true)]
    [InlineData("1",      true)]
    [InlineData("+",      true)]
    [InlineData("✓",      true)]
    [InlineData("нет",    false)]
    [InlineData("Нет",    false)]
    [InlineData("no",     false)]
    [InlineData("n",      false)]
    [InlineData("false",  false)]
    [InlineData("0",      false)]
    [InlineData("-",      false)]
    [InlineData("—",      false)]
    [InlineData("",       null)]
    [InlineData(null,     null)]
    [InlineData("   ",    null)]
    // Незнакомое значение — НЕ ошибка, поле опциональное.
    [InlineData("возможно", null)]
    [InlineData("ага",      null)]
    public void TryParseBoolYesNo_NormalizesYesNoSynonyms(string? raw, bool? expected)
    {
        Assert.Equal(expected, RoomsFormImportMapper.TryParseBoolYesNo(raw));
    }

    /// <summary>
    /// «Дата ДДУ» (doc 113 v1.4) — поддерживается Excel-serial (число) и текстовые
    /// форматы dd.MM.yyyy / yyyy-MM-dd / dd/MM/yyyy / MM/dd/yyyy, опционально
    /// с компонентом HH:mm:ss. Результат — <b>ISO-строка yyyy-MM-dd</b>:
    /// реальный payload Visary UI шлёт <c>"Date":"2026-05-26"</c>, числовой
    /// Excel-serial не принимается. На пустом/прочерке — <c>null</c> без ошибки.
    /// </summary>
    [Theory]
    [InlineData("01.04.2026", "2026-04-01")]
    [InlineData("1.4.2026",   "2026-04-01")]
    [InlineData("2026-04-01", "2026-04-01")]
    [InlineData("01/04/2026", "2026-04-01")]
    // Форматы с компонентом времени — ClosedXML может вернуть «04/07/2025 00:00:00»
    // для ячеек с формулой/явным датным форматом (реальный кейс заказчика).
    // Неоднозначные слэш-формы (оба ≤12) парсятся как dd/MM/yyyy (русская
    // семантика, как в v1.0): `04/07/2025` → Jul 4, 2025.
    [InlineData("04/07/2025 00:00:00", "2025-07-04")]
    [InlineData("4/7/2025 0:00:00",    "2025-07-04")]
    [InlineData("01.04.2026 00:00:00", "2026-04-01")]
    [InlineData("2026-04-01 12:30:45", "2026-04-01")]
    // doc 113 v1.2: однозначно US-формы (день > 12) — fallback на MM/dd/yyyy.
    // Реальный кейс заказчика: «11/27/2025 00:00:00» — 27 не может быть месяцем,
    // поэтому dd/MM/yyyy fails, и побеждает MM/dd/yyyy → Nov 27, 2025.
    [InlineData("11/27/2025 00:00:00", "2025-11-27")]
    [InlineData("11/27/2025",          "2025-11-27")]
    [InlineData("3/15/2025",           "2025-03-15")]
    public void TryParseExcelDate_AcceptsKnownTextFormats(string raw, string expectedDateIso)
    {
        var result = RoomsFormImportMapper.TryParseExcelDate(raw, out var error);
        Assert.Null(error);
        Assert.Equal(expectedDateIso, result);
    }

    [Fact]
    public void TryParseExcelDate_07042025_ReturnsIsoString()
    {
        // Sanity-check: пример заказчика — 07.04.2025 → ISO «2025-04-07».
        // doc 113 v1.4: возвращаем строку, не Excel-serial (45754).
        var result = RoomsFormImportMapper.TryParseExcelDate("07.04.2025", out var error);
        Assert.Null(error);
        Assert.Equal("2025-04-07", result);
    }

    [Fact]
    public void TryParseExcelDate_AcceptsExcelSerial_ConvertsToIsoString()
    {
        // Если в ячейке уже Excel-serial (ClosedXML возвращает число для
        // Date-формата без явного string) — конвертируем в ISO-строку
        // через FromOADate (doc 113 v1.4).
        var serial = System.DateTime
            .ParseExact("2026-04-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            .ToOADate();
        var raw = serial.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = RoomsFormImportMapper.TryParseExcelDate(raw, out var error);
        Assert.Null(error);
        Assert.Equal("2026-04-01", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("—")]
    public void TryParseExcelDate_EmptyOrDash_IsNullWithoutError(string? raw)
    {
        var result = RoomsFormImportMapper.TryParseExcelDate(raw, out var error);
        Assert.Null(error);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("не дата")]
    [InlineData("hello")]
    [InlineData("99.99.9999")]
    public void TryParseExcelDate_InvalidString_ReturnsNullWithError(string raw)
    {
        var result = RoomsFormImportMapper.TryParseExcelDate(raw, out var error);
        Assert.Null(result);
        Assert.False(string.IsNullOrEmpty(error), "Ожидался текст ошибки для невалидной даты.");
    }
}
