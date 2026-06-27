using ClosedXML.Excel;
using KiloImportService.Api.Domain.Mapping;
using Xunit;

namespace KiloImportService.Api.Tests.Mapping;

/// <summary>
/// Покрытие парсеров блока «Продажи» (лист Control) и таблицы «Площадь реализации»
/// (лист Outputs), а также резолва ячеек-процентов. Тестируется только парсинг —
/// для интеграционных тестов оркестратора (создание Заключения, dataforfm,
/// PATCH datasetforfm) см. FinModelInstallmentsApplyTests.
/// См. doc_project/139-finmodel-installments-and-conclusion.md.
/// </summary>
public class FinModelInstallmentsTests
{
    // ─── Control ──────────────────────────────────────────────────────────

    /// <summary>
    /// Эталонная раскладка по файлу «Параметры к переносу в АБ восст.xlsx»:
    /// • строка 23: D = «Этап 1», E = «Этап 2», F = «Этап 3»;
    /// • строка 61: B = «Продажи»;
    /// • строка 69: B = «Отсрочка оплаты по ДДУ (равномерная)», D = «1 - Да»;
    ///   - 70: «Тип помещений»
    ///   - 71: «      Квартиры/Апартаменты» D = «1 - Да»
    ///   - 72: «      ПСН»                  D = «0 - Нет»
    ///   - 73: «      Кладовые»             D = «0 - Нет»
    ///   - 74: «      Машиноместа»          D = «1 - Да»
    ///   - 77: «Доля отсрочек»               D = 0.5 (50%)
    ///   - 78: «Доля СУ по ипотеке …»       D = 0.3 (30%)
    /// • строка 80: B = «Отсрочка оплаты по ДДУ (единовременная)», D = «0 - Нет» → schema skip
    /// • строка 92: B = «Отсрочка оплаты по ДКП», D = «0 - Нет» → schema skip
    /// </summary>
    [Fact]
    public void ReadInstallments_Reference_OnlySteadyEnabled_WithTwoRoomTypes()
    {
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceControlSheet(wb);

        var data = FinModelImportMapper.ReadInstallmentsFromSheet(sheet);

        // Парсер возвращает все 3 маркера, но IsEnabled=true только у «равномерной».
        Assert.Equal(3, data.Schemes.Count);
        var steady = data.Schemes.Single(s => s.Marker == FinModelImportMapper.InstallmentDDUSteadyMarker);
        Assert.True(steady.IsEnabled);
        // Включены 2 типа помещений: Квартиры/Апартаменты + Машиноместа.
        Assert.Equal(2, steady.EnabledRoomTypeLabels.Count);
        Assert.Contains("Квартиры/Апартаменты", steady.EnabledRoomTypeLabels);
        Assert.Contains("Машиноместа", steady.EnabledRoomTypeLabels);
        Assert.DoesNotContain("ПСН", steady.EnabledRoomTypeLabels);
        Assert.DoesNotContain("Кладовые", steady.EnabledRoomTypeLabels);
        // Доли — 30% и 50%.
        Assert.NotNull(steady.OwnSharePercent);
        Assert.Equal(30d, steady.OwnSharePercent!.Value, 1);
        Assert.NotNull(steady.PostpSharePercent);
        Assert.Equal(50d, steady.PostpSharePercent!.Value, 1);

        // Остальные две схемы — IsEnabled=false, RoomKinds пуст, доли null.
        var single = data.Schemes.Single(s => s.Marker == FinModelImportMapper.InstallmentDDUOnetimeMarker);
        Assert.False(single.IsEnabled);
        Assert.Empty(single.EnabledRoomTypeLabels);
        Assert.Null(single.OwnSharePercent);
        Assert.Null(single.PostpSharePercent);

        var dkp = data.Schemes.Single(s => s.Marker == FinModelImportMapper.InstallmentDKPMarker);
        Assert.False(dkp.IsEnabled);
        Assert.Empty(dkp.EnabledRoomTypeLabels);
    }

    [Fact]
    public void ReadInstallments_AllSchemesEnabled_AllReturned()
    {
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceControlSheet(wb);

        // Включим единовременную и ДКП. Доли поставим разные, чтобы проверить парс.
        sheet.Cell(80, 4).Value = "1 - Да"; // anchor единовременной
        sheet.Cell(82, 4).Value = "1 - Да"; // Квартиры/Апартаменты
        sheet.Cell(88, 4).Value = 0.4d;     // Доля отсрочек = 40%
        sheet.Cell(88, 4).Style.NumberFormat.Format = "0%";
        sheet.Cell(89, 4).Value = 0.2d;     // Доля СУ = 20%
        sheet.Cell(89, 4).Style.NumberFormat.Format = "0%";

        sheet.Cell(92, 4).Value = "1 - Да"; // anchor ДКП
        sheet.Cell(97, 4).Value = "1 - Да"; // Машиноместа
        sheet.Cell(99, 4).Value = 0.6d;     // Доля отсрочек = 60%
        sheet.Cell(99, 4).Style.NumberFormat.Format = "0%";
        sheet.Cell(100, 4).Value = 0.1d;    // Доля СУ = 10%
        sheet.Cell(100, 4).Style.NumberFormat.Format = "0%";

        var data = FinModelImportMapper.ReadInstallmentsFromSheet(sheet);
        Assert.Equal(3, data.Schemes.Count);
        Assert.All(data.Schemes, s => Assert.True(s.IsEnabled));

        var single = data.Schemes.Single(s => s.Marker == FinModelImportMapper.InstallmentDDUOnetimeMarker);
        Assert.Single(single.EnabledRoomTypeLabels);
        Assert.Equal(40d, single.PostpSharePercent!.Value, 1);
        Assert.Equal(20d, single.OwnSharePercent!.Value, 1);

        var dkp = data.Schemes.Single(s => s.Marker == FinModelImportMapper.InstallmentDKPMarker);
        Assert.Single(dkp.EnabledRoomTypeLabels);
        Assert.Contains("Машиноместа", dkp.EnabledRoomTypeLabels);
        Assert.Equal(60d, dkp.PostpSharePercent!.Value, 1);
    }

    [Fact]
    public void ReadInstallments_NoSchemesEnabled_AllMarkersReturned_AllDisabled()
    {
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceControlSheet(wb);
        // Выключим единственную включённую в эталоне.
        sheet.Cell(69, 4).Value = "0 - Нет";

        var data = FinModelImportMapper.ReadInstallmentsFromSheet(sheet);
        // Все 3 маркера найдены в шаблоне, но ни одна не включена — оркестратор
        // увидит anyScheme=false и не создаст Заключение, плюс PATCH-нёт каждую
        // схему null'ами на случай, если в Visary остались старые данные.
        Assert.Equal(3, data.Schemes.Count);
        Assert.All(data.Schemes, s => Assert.False(s.IsEnabled));
        Assert.All(data.Schemes, s => Assert.Empty(s.EnabledRoomTypeLabels));
    }

    [Fact]
    public void ReadInstallments_MissingSalesBlock_ThrowsParseException()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Control");
        // Только шапка этапов, никакого «Продажи».
        sheet.Cell(23, 4).Value = "Этап 1";

        Assert.Throws<FinModelInstallmentsParseException>(
            () => FinModelImportMapper.ReadInstallmentsFromSheet(sheet));
    }

    [Fact]
    public void ReadInstallments_MissingStageHeader_ThrowsParseException()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Control");
        sheet.Cell(61, 2).Value = "Продажи";

        Assert.Throws<FinModelInstallmentsParseException>(
            () => FinModelImportMapper.ReadInstallmentsFromSheet(sheet));
    }

    // ─── TryReadPercentCell ───────────────────────────────────────────────

    [Fact]
    public void TryReadPercentCell_PercentFormat_ReturnsPercentValue()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("T");
        sheet.Cell(1, 1).Value = 0.5d;
        sheet.Cell(1, 1).Style.NumberFormat.Format = "0.0%";

        var v = FinModelImportMapper.TryReadPercentCell(sheet, 1, 1);
        Assert.Equal(50d, v!.Value, 1);
    }

    [Fact]
    public void TryReadPercentCell_PlainNumber_ReturnsAsIs()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("T");
        sheet.Cell(1, 1).Value = 30d;

        var v = FinModelImportMapper.TryReadPercentCell(sheet, 1, 1);
        Assert.Equal(30d, v!.Value, 1);
    }

    [Fact]
    public void TryReadPercentCell_FractionWithoutFormat_ReturnsAsPercent()
    {
        // Эвристика: значение в [0..1] трактуется как доля (× 100 → проценты).
        // Гарантирует, что повторно открытый файл с потерянным процентным форматом
        // (бывает после правки в LibreOffice) всё равно прочитается корректно.
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("T");
        sheet.Cell(1, 1).Value = 0.5d;

        var v = FinModelImportMapper.TryReadPercentCell(sheet, 1, 1);
        Assert.Equal(50d, v!.Value, 1);
    }

    [Fact]
    public void TryReadPercentCell_EmptyCell_ReturnsNull()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("T");
        Assert.Null(FinModelImportMapper.TryReadPercentCell(sheet, 1, 1));
    }

    // ─── DateToFmPeriod ───────────────────────────────────────────────────

    [Theory]
    // Q1 = январь–март
    [InlineData("2029-01-01", "2029Q1")]
    [InlineData("2029-01-15", "2029Q1")]
    [InlineData("2029-03-15", "2029Q1")]
    [InlineData("2029-03-31", "2029Q1")] // последний день Q1 — всё ещё Q1
    // Q2 = апрель–июнь
    [InlineData("2029-04-01", "2029Q2")] // первый день Q2
    [InlineData("2029-05-15", "2029Q2")]
    [InlineData("2029-06-30", "2029Q2")] // последний день Q2
    // Q3 = июль–сентябрь
    [InlineData("2029-07-01", "2029Q3")]
    [InlineData("2029-09-30", "2029Q3")]
    // Q4 = октябрь–декабрь
    [InlineData("2029-10-01", "2029Q4")]
    [InlineData("2029-12-31", "2029Q4")] // конец года — всё ещё текущий год Q4
    public void DateToFmPeriod_ReturnsExpectedQuarter(string isoDate, string expected)
    {
        var date = DateTime.Parse(isoDate, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, FinModelImportMapper.DateToFmPeriod(date));
    }

    // ─── ReadCommissioningFromSheet ───────────────────────────────────────

    [Fact]
    public void ReadCommissioning_Reference_ReturnsStage1ProductionQuarter()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Control");
        // Раздел «Конфигурация этапов» в колонке B на строке 13.
        sheet.Cell(13, 2).Value = "Конфигурация этапов";
        // Шапка таблицы на строке 14: B=«Этапы», D=«Старт строительства (дата РнС)»,
        // F=«Ввод в эксплуатацию (получение РнВ)».
        sheet.Cell(14, 2).Value = "Этапы";
        sheet.Cell(14, 4).Value = "Старт строительства (дата РнС)";
        sheet.Cell(14, 6).Value = "Ввод в эксплуатацию (получение РнВ)";
        // Строка «Этап 1.» на 15.
        sheet.Cell(15, 2).Value = "Этап 1.";
        sheet.Cell(15, 4).Value = new DateTime(2024, 7, 29);
        sheet.Cell(15, 6).Value = new DateTime(2029, 3, 31);

        var data = FinModelImportMapper.ReadCommissioningFromSheet(sheet);
        Assert.NotNull(data);
        Assert.Equal(new DateTime(2029, 3, 31), data!.CommissioningDate);
        // 31.03.2029 — последний день Q1 (январь–март) → Q1.
        Assert.Equal("2029Q1", data.CommissioningPeriod);
    }

    [Fact]
    public void ReadCommissioning_NoStage1Row_ReturnsNull()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Control");
        sheet.Cell(13, 2).Value = "Конфигурация этапов";
        sheet.Cell(14, 6).Value = "Ввод в эксплуатацию (получение РнВ)";
        // Без строки «Этап 1.».

        var data = FinModelImportMapper.ReadCommissioningFromSheet(sheet);
        Assert.Null(data);
    }

    [Fact]
    public void ReadCommissioning_NoCommissioningHeader_ReturnsNull()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Control");
        sheet.Cell(13, 2).Value = "Конфигурация этапов";
        sheet.Cell(15, 2).Value = "Этап 1.";
        sheet.Cell(15, 6).Value = new DateTime(2029, 3, 31);
        // Без шапки «Ввод в эксплуатацию».

        var data = FinModelImportMapper.ReadCommissioningFromSheet(sheet);
        Assert.Null(data);
    }

    [Fact]
    public void ReadCommissioning_EmptyDateCell_ReturnsNull()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Control");
        sheet.Cell(13, 2).Value = "Конфигурация этапов";
        sheet.Cell(14, 6).Value = "Ввод в эксплуатацию (получение РнВ)";
        sheet.Cell(15, 2).Value = "Этап 1.";
        // F15 пустая.

        var data = FinModelImportMapper.ReadCommissioningFromSheet(sheet);
        Assert.Null(data);
    }

    // ─── Helpers (фикстуры) ───────────────────────────────────────────────

    /// <summary>
    /// Воссоздаёт эталонную раскладку Control из отчёта парсера xlsx-файла.
    /// Только нужные ячейки (минимальный набор для парсера).
    /// </summary>
    private static IXLWorksheet BuildReferenceControlSheet(XLWorkbook wb)
    {
        var sheet = wb.AddWorksheet("Control");

        // Шапка этапов: строка 23.
        sheet.Cell(23, 4).Value = "Этап 1";
        sheet.Cell(23, 5).Value = "Этап 2";
        sheet.Cell(23, 6).Value = "Этап 3";

        // Блок «Продажи».
        sheet.Cell(61, 2).Value = "Продажи";

        // 1) «Отсрочка оплаты по ДДУ (равномерная)» — включена.
        sheet.Cell(69, 2).Value = FinModelImportMapper.InstallmentDDUSteadyMarker;
        sheet.Cell(69, 4).Value = "1 - Да";
        sheet.Cell(70, 2).Value = "Тип помещений";
        sheet.Cell(71, 2).Value = "      Квартиры/Апартаменты";
        sheet.Cell(71, 4).Value = "1 - Да";
        sheet.Cell(72, 2).Value = "      ПСН";
        sheet.Cell(72, 4).Value = "0 - Нет";
        sheet.Cell(73, 2).Value = "      Кладовые";
        sheet.Cell(73, 4).Value = "0 - Нет";
        sheet.Cell(74, 2).Value = "      Машиноместа";
        sheet.Cell(74, 4).Value = "1 - Да";
        sheet.Cell(75, 2).Value = "Период отсрочки (ДДУ)";
        sheet.Cell(76, 2).Value = "Дата для периода рассрочки";
        sheet.Cell(77, 2).Value = "Доля отсрочек";
        sheet.Cell(77, 4).Value = 0.5d;
        sheet.Cell(77, 4).Style.NumberFormat.Format = "0.0%";
        sheet.Cell(78, 2).Value = "Доля СУ по ипотеке / первоначальный взнос";
        sheet.Cell(78, 4).Value = 0.3d;
        sheet.Cell(78, 4).Style.NumberFormat.Format = "0.0%";

        // 2) «Отсрочка оплаты по ДДУ (единовременная)» — выключена.
        sheet.Cell(80, 2).Value = FinModelImportMapper.InstallmentDDUOnetimeMarker;
        sheet.Cell(80, 4).Value = "0 - Нет";
        sheet.Cell(81, 2).Value = "Тип помещений";
        sheet.Cell(82, 2).Value = "      Квартиры/Апартаменты";
        sheet.Cell(82, 4).Value = "0 - Нет";
        sheet.Cell(83, 2).Value = "      ПСН";
        sheet.Cell(83, 4).Value = "0 - Нет";
        sheet.Cell(84, 2).Value = "      Кладовые";
        sheet.Cell(84, 4).Value = "0 - Нет";
        sheet.Cell(85, 2).Value = "      Машиноместа";
        sheet.Cell(85, 4).Value = "0 - Нет";
        sheet.Cell(86, 2).Value = "Период отсрочки (ДДУ) Последний платеж";
        sheet.Cell(88, 2).Value = "Доля отсрочек";
        sheet.Cell(89, 2).Value = "Доля СУ по ипотеке / первоначальный взнос";

        // 3) «Отсрочка оплаты по ДКП» — выключена. Labels включены, чтобы тесты
        //    с включённой схемой могли набивать только D-ячейки.
        sheet.Cell(92, 2).Value = FinModelImportMapper.InstallmentDKPMarker;
        sheet.Cell(92, 4).Value = "0 - Нет";
        sheet.Cell(93, 2).Value = "Тип помещений";
        sheet.Cell(94, 2).Value = "      Квартиры/Апартаменты";
        sheet.Cell(94, 4).Value = "0 - Нет";
        sheet.Cell(95, 2).Value = "      ПСН";
        sheet.Cell(95, 4).Value = "0 - Нет";
        sheet.Cell(96, 2).Value = "      Кладовые";
        sheet.Cell(96, 4).Value = "0 - Нет";
        sheet.Cell(97, 2).Value = "      Машиноместа";
        sheet.Cell(97, 4).Value = "0 - Нет";
        sheet.Cell(98, 2).Value = "Период отсрочки (ДКП), квартал";
        sheet.Cell(99, 2).Value = "Доля отсрочек";
        sheet.Cell(100, 2).Value = "Доля СУ по ипотеке / первоначальный взнос";

        return sheet;
    }

    // ─── Financing (Результаты + Финансирование → ставки сделки, doc 139 v1.4) ──

    /// <summary>
    /// Эталонная раскладка «Финансирование» (doc 144): LM10/LM20/LM30 берут Rate
    /// из специфичной подстроки родителя; LM40/LM50/LM60/LM70 — из самой строки
    /// родителя. В эталоне LM30 подстроки пусты → ставка LM30 НЕ создаётся.
    /// </summary>
    [Fact]
    public void ReadFinancing_Reference_SixRatesParentPlusSubRows()
    {
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceFinancingControlSheet(wb);

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);

        Assert.Equal("KD-12345", data.KdNumber);
        Assert.Equal(6, data.Rates.Count);

        var lm10 = data.Rates.Single(r => r.Code == "LM10");
        Assert.Equal(10, lm10.PercentKind);
        Assert.Equal(5d, lm10.Rate, 0); // подстрока «Премия к КС РФ (сценарий 2)» = 5%

        var lm20 = data.Rates.Single(r => r.Code == "LM20");
        Assert.Equal(20, lm20.PercentKind);
        Assert.Equal(100d, lm20.Rate, 0); // подстрока «Доля капитализации…» = 100%

        // LM30 — обе подстроки пусты → ставка не создаётся.
        Assert.DoesNotContain(data.Rates, r => r.Code == "LM30");

        var lm40 = data.Rates.Single(r => r.Code == "LM40");
        Assert.Equal(40, lm40.PercentKind);
        Assert.Equal(11d, lm40.Rate, 0);

        var lm50 = data.Rates.Single(r => r.Code == "LM50");
        Assert.Equal(50, lm50.PercentKind);
        Assert.Equal(5d, lm50.Rate, 0);

        var lm60 = data.Rates.Single(r => r.Code == "LM60");
        Assert.Equal(60, lm60.PercentKind);
        Assert.Equal(1.3d, lm60.Rate, 1);

        var lm70 = data.Rates.Single(r => r.Code == "LM70");
        Assert.Equal(70, lm70.PercentKind);
        Assert.Equal(1d, lm70.Rate, 1);
    }

    [Fact]
    public void ReadFinancing_Lm30FirstSubRowFilled_RateTaken()
    {
        // Если первая подстрока LM30 заполнена — Rate берётся из неё.
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceFinancingControlSheet(wb);
        sheet.Cell(221, 4).Value = 0.07d;
        sheet.Cell(221, 4).Style.NumberFormat.Format = "0.0%";

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);

        var lm30 = data.Rates.Single(r => r.Code == "LM30");
        Assert.Equal(7d, lm30.Rate, 0);
    }

    [Fact]
    public void ReadFinancing_Lm10ParentIsNo_RateNotCreated()
    {
        // Родитель LM10 «0 - Нет» → даже если подстрока непуста, ставка не создаётся.
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceFinancingControlSheet(wb);
        sheet.Cell(210, 4).Value = "0 - Нет";

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);

        Assert.DoesNotContain(data.Rates, r => r.Code == "LM10");
    }

    [Fact]
    public void ReadFinancing_AllRatesNo_ReturnsEmpty()
    {
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceFinancingControlSheet(wb);
        // Все 7 строк-родителей «0 - Нет» → ничего не создаётся.
        sheet.Cell(210, 4).Value = "0 - Нет"; // LM10
        sheet.Cell(213, 4).Value = "0 - Нет"; // LM50
        sheet.Cell(214, 4).Value = "0 - Нет"; // LM60
        sheet.Cell(215, 4).Value = "0 - Нет"; // LM20
        sheet.Cell(218, 4).Value = "0 - Нет"; // LM70
        sheet.Cell(220, 4).Value = "0 - Нет"; // LM30
        sheet.Cell(225, 4).Value = "0 - Нет"; // LM40

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);

        Assert.Empty(data.Rates);
        Assert.Equal("KD-12345", data.KdNumber);
    }

    [Fact]
    public void ReadFinancing_DirectRateEmpty_SkipsRate()
    {
        // Для LM40/LM50/LM60/LM70 — Rate берётся прямо из строки родителя.
        // Пустая ячейка «Этап 1» → ставка не создаётся.
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceFinancingControlSheet(wb);
        sheet.Cell(213, 4).Clear(); // LM50: пусто
        sheet.Cell(214, 4).Clear(); // LM60: пусто

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);

        Assert.DoesNotContain(data.Rates, r => r.Code == "LM50");
        Assert.DoesNotContain(data.Rates, r => r.Code == "LM60");
    }

    [Fact]
    public void ReadFinancing_Lm10AllSubRowsEmpty_NotCreated()
    {
        // Заказчик: «Если в подстроках нет значений, тогда ставку не создавать».
        // Родитель LM10 активен, но обе подстроки пусты.
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceFinancingControlSheet(wb);
        sheet.Cell(212, 4).Clear(); // вторая подстрока — была 5%, чистим

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);

        Assert.DoesNotContain(data.Rates, r => r.Code == "LM10");
    }

    [Fact]
    public void ReadFinancing_NumericZeroValue_NotTreatedAsNo()
    {
        // Раньше IsYesNoNo() матчил всё, что начинается с «0», и съедал числа
        // вроде «0,05» (Rate=5%). После фикса (doc 143) «0 - Нет» матчится
        // только если в строке есть «Нет», а число 0.12 в долевом виде —
        // через TryReadPercentCell → Rate=12% (без false-skip).
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceFinancingControlSheet(wb);
        // LM60 = 0.12 — должен быть прочитан как Rate=12%, не как «0 - Нет».
        sheet.Cell(214, 4).Value = 0.12d;

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);
        var lm60 = data.Rates.Single(r => r.Code == "LM60");
        Assert.Equal(12d, lm60.Rate, 0);
    }

    [Fact]
    public void ReadFinancing_NoFinancingSection_ReturnsEmptyRates()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Control");
        sheet.Cell(23, 4).Value = "Этап 1";
        // Только «Результаты», без «Финансирование».
        sheet.Cell(50, 2).Value = "Результаты";
        sheet.Cell(51, 2).Value = "Номер КД";
        sheet.Cell(52, 2).Value = "KD-A";

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);

        Assert.Equal("KD-A", data.KdNumber);
        Assert.Empty(data.Rates);
    }

    [Fact]
    public void ReadFinancing_NoResultsSection_KdIsNull_RatesStillRead()
    {
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceFinancingControlSheet(wb);
        // Затрём «Результаты» — оставим только «Финансирование».
        sheet.Cell(200, 2).Value = string.Empty;
        sheet.Cell(201, 2).Value = string.Empty;
        sheet.Cell(202, 2).Value = string.Empty;

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);

        Assert.Null(data.KdNumber);
        Assert.Equal(6, data.Rates.Count); // LM30 не создаётся: подстроки пусты
    }

    [Fact]
    public void ReadFinancing_NoControlSheet_ReturnsEmpty()
    {
        using var wb = new XLWorkbook();
        // Лист другой — Control отсутствует.
        wb.AddWorksheet("Other");
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var data = FinModelImportMapper.ReadFinancingData(ms);

        Assert.Null(data.KdNumber);
        Assert.Empty(data.Rates);
    }

    [Theory]
    // LM10 — «Базовая %% ставка», нет «процентная».
    [InlineData("Базовая %% ставка", "LM10")]
    [InlineData("базовая % ставка", "LM10")]
    [InlineData("Базовая ставка", "LM10")]
    // LM20 — «Капитализация / отсрочка уплаты %%».
    [InlineData("Капитализация / отсрочка уплаты %%", "LM20")]
    [InlineData("отсрочка уплаты процентов", "LM20")]
    // LM30 — «Базовая процентная ставка по капитализированным %%». Опечатка
    // «капиатализированным» в файле заказчика (R190) — тоже матчится.
    [InlineData("Базовая процентная ставка по капитализированным %%", "LM30")]
    [InlineData("Базовая процентная ставка по капиатализированным %%", "LM30")]
    // LM40 — «Комисия за отсрочку %% (сценарий 3)». Корень «комис» ловит и «Комиссия».
    [InlineData("Комисия за отсрочку %% (сценарий 3)", "LM40")]
    [InlineData("Комиссия за отсрочку %% (сценарий 3)", "LM40")]
    // LM50 — «Спец. процентная ставка» (двойной пробел в исходнике учтён).
    [InlineData("Спец.  процентная ставка", "LM50")]
    [InlineData("Спец. процентная ставка", "LM50")]
    // LM60 — «Коэф покрытия эскроу/долг для перехода на 0,01% (для спец ставки)».
    [InlineData("Коэф покрытия эскроу/долг для перехода на 0,01% (для спец ставки)", "LM60")]
    [InlineData("Коэф покрытия Эскроу/Долг", "LM60")]
    // LM70 — «Выбор ставки для капитализации процентов».
    [InlineData("Выбор ставки для капитализации процентов ", "LM70")]
    [InlineData("выбор ставки", "LM70")]
    // Не матчатся (sub-rows сценариев и не-ставочные строки).
    [InlineData("Фиксированная ставка (сценарий 1)", null)]
    [InlineData("Премия к КС РФ (фикс) (сценарий 2)", null)]
    [InlineData("Ручной ввод периода отсрочки (сценарий 2), кварталы", null)]
    [InlineData("Доля капитализации/отсрочки процентов в тело долга (сценарии 1-3)", null)]
    [InlineData("Опцион", null)]
    [InlineData("Срок кредита", null)]
    [InlineData("Иной параметр", null)]
    public void TryMatchFinancingRateCode_VariousLabels_MatchesExpected(string label, string? expectedCode)
    {
        Assert.Equal(expectedCode, FinModelImportMapper.TryMatchFinancingRateCode(label));
    }

    [Fact]
    public void ReadFinancing_NoResultsSection_GlobalKdFallback_StillFound()
    {
        // Заказчик может разместить «Номер КД» вне раздела «Результаты»
        // (или раздел переименовать). Глобальный fallback должен находить
        // заголовок где угодно на листе.
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Control");
        sheet.Cell(23, 4).Value = "Этап 1";
        // Никакого «Результаты». Только «Номер КД» с значением ниже —
        // далеко от начала листа.
        sheet.Cell(400, 5).Value = "Номер КД";
        sheet.Cell(401, 5).Value = "KD-FAR";

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);
        Assert.Equal("KD-FAR", data.KdNumber);
    }

    [Fact]
    public void ReadFinancing_KdValueIsZero_NotTreatedAsEmpty()
    {
        // Заказчик может ввести «0» как валидный № КД (пример с тест-стенда).
        // Парсер обязан вернуть «0», а не считать ячейку пустой.
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Control");
        sheet.Cell(23, 4).Value = "Этап 1";
        sheet.Cell(50, 2).Value = "Результаты";
        sheet.Cell(51, 2).Value = "Номер КД";
        sheet.Cell(52, 2).Value = 0d;

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);
        Assert.Equal("0", data.KdNumber);
    }

    [Fact]
    public void ReadFinancing_KdLabelAlsoInHorizontalLayout_StillFound()
    {
        // Защита от файла, где «Номер КД» лежит как label-value в одной строке
        // (старая раскладка из doc 105: F=label, G=value). Парсер тогда читает
        // значение из ячейки СПРАВА от заголовка.
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Control");
        sheet.Cell(23, 4).Value = "Этап 1";
        sheet.Cell(20, 6).Value = "Результаты";
        sheet.Cell(21, 6).Value = "Номер КД";
        sheet.Cell(21, 7).Value = "HORIZ-1";

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);
        Assert.Equal("HORIZ-1", data.KdNumber);
    }

    [Fact]
    public void ReadFinancing_RatePercentValue_ParsedAsPercent()
    {
        // LM50 — Rate берётся из строки родителя; формат «0%» / 0.25 → 25%.
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceFinancingControlSheet(wb);
        sheet.Cell(213, 4).Value = 0.25d;
        sheet.Cell(213, 4).Style.NumberFormat.Format = "0%";

        var data = FinModelImportMapper.ReadFinancingFromSheet(sheet);
        var lm50 = data.Rates.Single(r => r.Code == "LM50");
        Assert.Equal(25d, lm50.Rate, 1);
    }

    [Fact]
    public void ReadFinancing_Lm70RateTextWithLeadingDigit_RateIsLeadingDigit()
    {
        // LM70 — Rate из строки родителя, текст «1 - Средневзвешанная» → Rate=1.
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceFinancingControlSheet(wb);

        var lm70 = FinModelImportMapper.ReadFinancingFromSheet(sheet)
            .Rates.Single(r => r.Code == "LM70");
        Assert.Equal(1d, lm70.Rate, 1);
    }

    /// <summary>
    /// Эталонный Control: «Результаты» с КД + «Финансирование» → «Инвестиционные
    /// кредиты» с 7 ставками. По схеме заказчика (doc 144): LM10/LM20/LM30 —
    /// parent + специфичная подстрока с Rate; LM40/LM50/LM60/LM70 — одна строка =
    /// одна ставка с Rate из той же строки.
    /// </summary>
    private static IXLWorksheet BuildReferenceFinancingControlSheet(XLWorkbook wb)
    {
        var sheet = wb.AddWorksheet("Control");
        // Шапка этапов: строка 23, Этап 1 в D.
        sheet.Cell(23, 4).Value = "Этап 1";
        sheet.Cell(23, 5).Value = "Этап 2";
        sheet.Cell(23, 6).Value = "Этап 3";

        // Раздел «Результаты» с КД ниже.
        sheet.Cell(200, 2).Value = "Результаты";
        sheet.Cell(201, 2).Value = "Номер КД";
        sheet.Cell(202, 2).Value = "KD-12345";

        // Раздел «Финансирование» → подраздел «Инвестиционные кредиты».
        sheet.Cell(204, 2).Value = "Финансирование";
        sheet.Cell(205, 2).Value = "Инвестиционные кредиты";

        // LM10 «Базовая %% ставка» — родитель активен («2 - Премия...»), Rate
        // берётся из подстроки «Премия к КС РФ (фикс) (сценарий 2)» = 5%.
        sheet.Cell(210, 2).Value = "Базовая %% ставка";
        sheet.Cell(210, 4).Value = "2 - Премия к КС РФ (фикс)";
        sheet.Cell(211, 2).Value = "Фиксированная ставка (сценарий 1)"; // пусто → пропуск
        sheet.Cell(212, 2).Value = "Премия к КС РФ (фикс) (сценарий 2)";
        sheet.Cell(212, 4).Value = 0.05d;
        sheet.Cell(212, 4).Style.NumberFormat.Format = "0.0%";

        // LM50 «Спец. процентная ставка» — Rate напротив наименования (5%).
        sheet.Cell(213, 2).Value = "Спец.  процентная ставка"; // двойной пробел как в файле
        sheet.Cell(213, 4).Value = 0.05d;
        sheet.Cell(213, 4).Style.NumberFormat.Format = "0.0%";

        // LM60 «Коэф покрытия эскроу/долг…» — Rate напротив (1.3).
        sheet.Cell(214, 2).Value = "Коэф покрытия эскроу/долг для перехода на 0,01% (для спец ставки)";
        sheet.Cell(214, 4).Value = 1.3d;

        // LM20 «Капитализация / отсрочка уплаты %%» — родитель «3 - Отсрочка...»,
        // Rate берётся из подстроки «Доля капитализации…» = 100%.
        sheet.Cell(215, 2).Value = "Капитализация / отсрочка уплаты %%";
        sheet.Cell(215, 4).Value = "3 - Отсрочка (Без капитализации с комиссией за отсрочку)";
        sheet.Cell(216, 2).Value = "Ручной ввод периода отсрочки (сценарий 2), кварталы"; // пусто
        sheet.Cell(217, 2).Value = "Доля капитализации/отсрочки процентов в тело долга (сценарии 1-3)";
        sheet.Cell(217, 4).Value = 1d;
        sheet.Cell(217, 4).Style.NumberFormat.Format = "0%"; // → 100%

        // LM70 «Выбор ставки для капитализации процентов» — Rate=1.
        sheet.Cell(218, 2).Value = "Выбор ставки для капитализации процентов ";
        sheet.Cell(218, 4).Value = "1 - Средневзвешанная";

        // LM30 «Базовая процентная ставка по капи(а)тализированным %%» — родитель
        // активен, но обе подстроки пусты → ставка НЕ создаётся.
        sheet.Cell(220, 2).Value = "Базовая процентная ставка по капиатализированным %%";
        sheet.Cell(220, 4).Value = "2 - Премия к КС РФ (фикс)";
        sheet.Cell(221, 2).Value = "Фиксированная ставка (сценарии 1-2)";
        sheet.Cell(222, 2).Value = "Премия к КС РФ (фикс) (сценарии 1-2)";

        // LM40 «Комисия за отсрочку %%» — Rate напротив (11%).
        sheet.Cell(225, 2).Value = "Комисия за отсрочку %% (сценарий 3)";
        sheet.Cell(225, 4).Value = 0.11d;
        sheet.Cell(225, 4).Style.NumberFormat.Format = "0.0%";
        return sheet;
    }

    // ─── DealMonthlyData / «Инвестиционный кредит: Этап 1» (doc 142) ──────

    /// <summary>
    /// Эталонная раскладка раздела «Инвестиционный кредит: Этап 1» по скриншоту
    /// заказчика. Колонка «Факт» в H, единица измерения в D, лейблы в B.
    /// </summary>
    private static IXLWorksheet BuildReferenceInvestmentCreditSheet(XLWorkbook wb)
    {
        var sheet = wb.AddWorksheet("Outputs");
        // Колонка «Факт» — H3 (год — H4=2026, квартал H5=1 и т.д., но для парсера
        // dealmonthlydata важен только номер колонки).
        sheet.Cell(3, 8).Value = "Факт";

        // Якорь раздела — B10.
        sheet.Cell(10, 1).Value = "Инвестиционный кредит: Этап 1";

        // 5 целевых полей в окне 11..15. Колонка B = label, D = единица, H = Факт.
        sheet.Cell(11, 2).Value = "Привлечение ОД";
        sheet.Cell(11, 4).Value = "млн руб.";
        sheet.Cell(11, 8).Value = 1990d; // → 1 990 000 000

        sheet.Cell(12, 2).Value = "Погашение тела долга";
        sheet.Cell(12, 4).Value = "млн руб.";
        sheet.Cell(12, 8).Value = "—"; // → 0

        sheet.Cell(13, 2).Value = "Погашение процентных выплат";
        sheet.Cell(13, 4).Value = "млн руб.";
        sheet.Cell(13, 8).Value = 0d;

        sheet.Cell(14, 2).Value = "Проценты начисленные";
        sheet.Cell(14, 4).Value = "млн руб.";
        sheet.Cell(14, 8).Value = 506d; // → 506 000 000

        sheet.Cell(15, 2).Value = "Расчет процентов по капитализации";
        sheet.Cell(15, 4).Value = "млн руб.";
        sheet.Cell(15, 8).Value = "-";  // → 0
        return sheet;
    }

    [Fact]
    public void ReadInvestmentCreditMonthlyData_Reference_ParsesAllFiveFieldsWithMlnRubMultiplier()
    {
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceInvestmentCreditSheet(wb);

        var data = FinModelImportMapper.ReadInvestmentCreditMonthlyDataFromSheet(sheet);

        Assert.Equal(1_990_000_000d, data.PrincipalDebtAmount, 0);
        Assert.Equal(0d, data.PrincipalRepaymentAmount, 0);
        Assert.Equal(0d, data.InterestRepaymentAmount, 0);
        Assert.Equal(506_000_000d, data.SimpleInterestAmount, 0);
        Assert.Equal(0d, data.CapitalizedInterestAmount, 0);
        Assert.True(data.HasAnyValue());
    }

    [Fact]
    public void ReadInvestmentCreditMonthlyData_ThousandRub_MultiplierIs1000()
    {
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceInvestmentCreditSheet(wb);
        // Перебиваем единицу для «Привлечение ОД» на «тыс. руб.».
        sheet.Cell(11, 4).Value = "тыс. руб.";

        var data = FinModelImportMapper.ReadInvestmentCreditMonthlyDataFromSheet(sheet);
        Assert.Equal(1_990_000d, data.PrincipalDebtAmount, 0);
    }

    [Fact]
    public void ReadInvestmentCreditMonthlyData_PlainRub_MultiplierIs1()
    {
        using var wb = new XLWorkbook();
        var sheet = BuildReferenceInvestmentCreditSheet(wb);
        sheet.Cell(11, 4).Value = "руб";

        var data = FinModelImportMapper.ReadInvestmentCreditMonthlyDataFromSheet(sheet);
        Assert.Equal(1990d, data.PrincipalDebtAmount, 0);
    }

    [Fact]
    public void ReadInvestmentCreditMonthlyData_NoOutputsSheet_ReturnsAllZeros()
    {
        using var wb = new XLWorkbook();
        wb.AddWorksheet("Other");
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var data = FinModelImportMapper.ReadInvestmentCreditMonthlyData(ms);
        Assert.False(data.HasAnyValue());
        Assert.Equal(0d, data.PrincipalDebtAmount);
    }

    [Fact]
    public void ReadInvestmentCreditMonthlyData_NoFactColumn_ReturnsAllZeros()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Outputs");
        sheet.Cell(10, 1).Value = "Инвестиционный кредит: Этап 1";
        // Маркера «Факт» нигде нет.
        var data = FinModelImportMapper.ReadInvestmentCreditMonthlyDataFromSheet(sheet);
        Assert.False(data.HasAnyValue());
    }

    [Fact]
    public void ReadInvestmentCreditMonthlyData_NoAnchor_ReturnsAllZeros()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Outputs");
        sheet.Cell(3, 8).Value = "Факт";
        // Нет якоря «Инвестиционный кредит».
        var data = FinModelImportMapper.ReadInvestmentCreditMonthlyDataFromSheet(sheet);
        Assert.False(data.HasAnyValue());
    }

    [Fact]
    public void ReadInvestmentCreditMonthlyData_DashAndEmpty_AreTreatedAsZero()
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Outputs");
        sheet.Cell(3, 8).Value = "Факт";
        sheet.Cell(10, 1).Value = "Инвестиционный кредит: Этап 1";
        // Все 5 полей с разными формами «нет данных».
        sheet.Cell(11, 2).Value = "Привлечение ОД";
        sheet.Cell(11, 4).Value = "млн руб.";
        sheet.Cell(11, 8).Value = "—";

        sheet.Cell(12, 2).Value = "Погашение тела долга";
        sheet.Cell(12, 4).Value = "млн руб.";
        // 12/H пустая — не пишем

        sheet.Cell(13, 2).Value = "Погашение процентных выплат";
        sheet.Cell(13, 4).Value = "млн руб.";
        sheet.Cell(13, 8).Value = "-";

        sheet.Cell(14, 2).Value = "Проценты начисленные";
        sheet.Cell(14, 4).Value = "млн руб.";
        sheet.Cell(14, 8).Value = 0d;

        sheet.Cell(15, 2).Value = "Расчет процентов по капитализации";
        sheet.Cell(15, 4).Value = "млн руб.";
        sheet.Cell(15, 8).Value = "–";

        var data = FinModelImportMapper.ReadInvestmentCreditMonthlyDataFromSheet(sheet);
        Assert.False(data.HasAnyValue());
    }

    [Theory]
    [InlineData("млн руб.",        1_000_000d)]
    [InlineData("млн.руб.",        1_000_000d)]
    [InlineData("МЛН РУБ",         1_000_000d)]
    [InlineData("тыс. руб.",       1_000d)]
    [InlineData("тыс.руб.",        1_000d)]
    [InlineData("ТЫС РУБ",         1_000d)]
    [InlineData("руб",             1d)]
    [InlineData("руб.",            1d)]
    [InlineData("",                1d)]
    [InlineData(null,              1d)]
    public void GetUnitMultiplier_VariousFormats_ReturnsExpected(string? unit, double expected)
    {
        Assert.Equal(expected, FinModelImportMapper.GetUnitMultiplier(unit), 0);
    }
}
