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

    /// <summary>
    /// Наименование корпуса/строения нормализуется через ExtractNumericPart:
    /// префиксы «лит/литер/корп/корпус/стр/строение» отбрасываются, а из
    /// остатка берутся цифры и разделители «.» «,» «-» «/» «\».
    /// Заказчик: «литер 1-1» → «1-1», «лит 1/1» → «1/1», «лит 1\1» → «1\1».
    /// Регрессии — старые формы должны продолжать работать.
    /// </summary>
    /// <summary>
    /// Резолв имени листа в Title справочника RoomKind. Plural-trim применяется
    /// к каждому слову независимо, поэтому многословные «Коммерческие помещения»
    /// тоже корректно матчатся. Substring-fallback запрещён (doc 90) — «Кв_…»
    /// не должно совпадать с «Квартира».
    /// </summary>
    [Fact]
    public void ResolveKindBySheetName_SingularDirectMatch()
    {
        var dict = MakeKindDict("Квартира", "Машиноместо", "Кладовая",
            "Коммерческое помещение", "Нежилое помещение", "Апартамент", "Гараж", "Комната");

        Assert.Equal("Квартира",              ResolveTitle("Квартира", dict));
        Assert.Equal("Гараж",                 ResolveTitle("Гараж", dict));
        Assert.Equal("Комната",               ResolveTitle("Комната", dict));
        Assert.Equal("Нежилое помещение",     ResolveTitle("Нежилое помещение", dict));
    }

    [Fact]
    public void ResolveKindBySheetName_PluralSingleWord()
    {
        var dict = MakeKindDict(
            "Квартира", "Машиноместо", "Кладовая", "Апартамент",
            "Офис", "Комната", "Студия", "Гараж");

        // Старая логика (регрессии)
        Assert.Equal("Квартира",    ResolveTitle("Квартиры",     dict));
        Assert.Equal("Машиноместо", ResolveTitle("Машиноместа",  dict));
        Assert.Equal("Кладовая",    ResolveTitle("Кладовые",     dict)); // ые → ая (doc 116)
        Assert.Equal("Апартамент",  ResolveTitle("Апартаменты",  dict));

        // Новые формы (doc 138): plural→singular для оставшихся видов
        Assert.Equal("Офис",        ResolveTitle("Офисы",        dict)); // ы → '' (head1)
        Assert.Equal("Комната",     ResolveTitle("Комнаты",      dict)); // ы → а
        Assert.Equal("Студия",      ResolveTitle("Студии",       dict)); // и → я
        Assert.Equal("Гараж",       ResolveTitle("Гаражи",       dict)); // и → '' (head1)
    }

    [Fact]
    public void ResolveKindBySheetName_PluralMultiWord()
    {
        var dict = MakeKindDict("Коммерческое помещение", "Нежилое помещение", "Иное нежилое помещение");

        // ие→ое (Коммерческие→Коммерческое) + ия→ие (помещения→помещение)
        Assert.Equal("Коммерческое помещение", ResolveTitle("Коммерческие помещения", dict));
        // Уже singular — direct match
        Assert.Equal("Нежилое помещение",      ResolveTitle("Нежилое помещение",     dict));
        // Новый суффикс ые→ое: «Нежилые помещения» → «Нежилое помещение»
        Assert.Equal("Нежилое помещение",      ResolveTitle("Нежилые помещения",     dict));
    }

    /// <summary>
    /// Doc 138: алиасы развёрнутых/уточнённых наименований вида помещения.
    /// Применяются и к имени листа, и к значению колонки «Тип/Название/Вид».
    /// Case-insensitive. Если канонического Title нет в живом справочнике —
    /// алиас не срабатывает (откат к plural-trim → null).
    /// </summary>
    [Fact]
    public void ResolveKindByTitle_SynonymAliases_Doc138()
    {
        var dict = MakeKindDict(
            "Квартира", "Нежилое помещение", "Машиноместо", "Апартаменты");

        // Квартира-студия → Квартира
        Assert.Equal("Квартира", ResolveByTitle("Квартира-студия", dict));
        Assert.Equal("Квартира", ResolveByTitle("квартира-студия", dict));

        // Нежилое помещение для коммерческого использования → Нежилое помещение
        Assert.Equal("Нежилое помещение",
            ResolveByTitle("Нежилое помещение для коммерческого использования", dict));

        // Машино-место (с дефисом) → Машиноместо
        Assert.Equal("Машиноместо", ResolveByTitle("Машино-место",     dict));
        Assert.Equal("Машиноместо", ResolveByTitle("Машино-место МГН", dict));

        // singular «Апартамент» → plural-Title «Апартаменты» (обратное направление)
        Assert.Equal("Апартаменты", ResolveByTitle("Апартамент", dict));
    }

    /// <summary>
    /// Doc 138: plural→singular должен работать и для «Офисы», «Комнаты», «Студии»,
    /// «Гаражи», «Нежилые помещения» — всех видов помещений в справочнике.
    /// </summary>
    [Fact]
    public void ResolveKindByTitle_PluralForms_Doc138()
    {
        var dict = MakeKindDict(
            "Офис", "Комната", "Студия", "Гараж", "Нежилое помещение",
            "Коммерческое помещение", "Кладовая", "Машиноместо", "Квартира", "Апартаменты");

        Assert.Equal("Офис",                   ResolveByTitle("Офисы",                 dict));
        Assert.Equal("Комната",                ResolveByTitle("Комнаты",               dict));
        Assert.Equal("Нежилое помещение",      ResolveByTitle("Нежилые помещения",     dict));
        Assert.Equal("Студия",                 ResolveByTitle("Студии",                dict));
        Assert.Equal("Коммерческое помещение", ResolveByTitle("Коммерческие помещения",dict));
        Assert.Equal("Кладовая",               ResolveByTitle("Кладовые",              dict));
        Assert.Equal("Машиноместо",            ResolveByTitle("Машиноместа",           dict));
        Assert.Equal("Квартира",               ResolveByTitle("Квартиры",              dict));
        Assert.Equal("Гараж",                  ResolveByTitle("Гаражи",                dict));
        // singular Апартамент → plural-Title — алиас (см. предыдущий тест)
        Assert.Equal("Апартаменты",            ResolveByTitle("Апартамент",            dict));
    }

    /// <summary>
    /// Алиас короткой формы: «ПСН» — отраслевая аббревиатура («помещение
    /// свободного назначения»), которую plural-trim не приведёт к Title.
    /// Резолвится через <c>SheetNameAliases</c> в «Нежилое помещение».
    /// Case-insensitive. Если канонического Title нет в живом справочнике —
    /// алиас не срабатывает (откат к plural-trim → null).
    /// </summary>
    [Fact]
    public void ResolveKindBySheetName_AliasPsn()
    {
        var dict = MakeKindDict("Квартира", "Нежилое помещение", "Кладовая");

        Assert.Equal("Нежилое помещение", ResolveTitle("ПСН", dict));
        Assert.Equal("Нежилое помещение", ResolveTitle("псн", dict));
        Assert.Equal("Нежилое помещение", ResolveTitle("  ПСН  ", dict));

        // Если в живом справочнике нет «Нежилое помещение» — алиас не помогает.
        var dictWithoutNonRes = MakeKindDict("Квартира", "Кладовая");
        Assert.Null(ResolveTitle("ПСН", dictWithoutNonRes));
    }

    [Fact]
    public void ResolveKindBySheetName_UnknownReturnsNull()
    {
        var dict = MakeKindDict("Квартира", "Машиноместо");

        // «Кв_01.04.26» — исторический снапшот, substring-fallback запрещён.
        Assert.Null(ResolveTitle("Кв_01.04.26", dict));
        // Совсем непонятное имя.
        Assert.Null(ResolveTitle("Итог",        dict));
        Assert.Null(ResolveTitle("",            dict));
        Assert.Null(ResolveTitle("   ",         dict));
    }

    private static Dictionary<string, int> MakeKindDict(params string[] titles)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < titles.Length; i++) d[titles[i]] = i + 1;
        return d;
    }

    private static string? ResolveTitle(string sheet, IDictionary<string, int> dict)
        => RoomsFormImportMapper.ResolveKindBySheetName(sheet, dict).Title;

    private static string? ResolveByTitle(string raw, IDictionary<string, int> dict)
        => RoomsFormImportMapper.ResolveKindByTitle(raw, dict).Title;

    [Theory]
    [InlineData("литер 1-1", "1-1")]
    [InlineData("лит 1/1",   "1/1")]
    [InlineData("лит 1\\1",  "1\\1")]
    [InlineData("Лит 1.1",   "1.1")]   // регрессия (см. xmldoc)
    [InlineData("корп 2",    "2")]      // регрессия
    [InlineData("3.А",       "3")]      // регрессия — буква обрывает
    [InlineData("лит. 1",    "1")]      // регрессия
    [InlineData(null,        null)]
    [InlineData("",          null)]
    [InlineData("   ",       null)]
    public void ExtractNumericPart_KeepsDashSlashBackslashSeparators(string? raw, string? expected)
    {
        var actual = RoomsFormImportMapper.ExtractNumericPart(raw);
        Assert.Equal(expected, actual);
    }
}
