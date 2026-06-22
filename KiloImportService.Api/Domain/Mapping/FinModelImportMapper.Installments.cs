using System.Globalization;
using ClosedXML.Excel;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Importing.Parsers;
using KiloImportService.Api.Domain.Pipeline;
using Visary.Api.Dto;

namespace KiloImportService.Api.Domain.Mapping;

/// <summary>
/// Доработка doc 139 — после Бюджета+ГФ Финмодель создаёт «Заключение» типа
/// «Итоговое заключение КА БП7» (<c>projectaudit</c> со <c>Stage=110</c>),
/// автоматически подтягивает связанный «Набор данных для ФМ» (<c>datasetforfm</c>),
/// и заполняет в нём долю собственных средств, долю рассрочки и виды помещений
/// по одной/нескольким из трёх схем (равномерная / единовременная / ДКП).
/// <para/>
/// Источник данных — лист <b>Control</b>, блок «Продажи» (старт по B-якорю): для
/// каждой схемы ячейка в колонке «Этап 1» (D23 = «Этап 1» → колонка D) даёт
/// «1 - Да» / «0 - Нет»; ниже якоря — sub-таблица видов помещений
/// (Квартиры/ПСН/Кладовые/Машиноместа) с теми же ячейками; параметры «Доля
/// отсрочек» и «Доля СУ по ипотеке». Indicator у <c>dataforfm</c> не заполняется
/// (заказчик: «не надо заполнять поле Indicator», см. doc 141).
/// </summary>
public sealed partial class FinModelImportMapper
{
    // ─── XLSX-маркеры ─────────────────────────────────────────────────────

    // Лист с управляющими параметрами.
    private const string InstallmentsControlSheet = "Control";

    // Якорь блока «Продажи» — точная подпись из эталонного файла.
    private const string InstallmentsSalesBlockMarker = "Продажи";

    // Лейблы трёх схем рассрочек в колонке B блока «Продажи». Регистр учитывается
    // нестрого (см. вызовы IsAnchorMatch); пробелы триммим.
    internal const string InstallmentDDUSteadyMarker   = "Отсрочка оплаты по ДДУ (равномерная)";
    internal const string InstallmentDDUOnetimeMarker  = "Отсрочка оплаты по ДДУ (единовременная)";
    internal const string InstallmentDKPMarker         = "Отсрочка оплаты по ДКП";

    // Лейблы дополнительных строк под якорем схемы.
    private const string InstallmentRoomTypesHeader        = "Тип помещений";
    private const string InstallmentPostpShareLabel        = "Доля отсрочек";
    private const string InstallmentOwnShareLabel          = "Доля СУ по ипотеке";

    // Лейбл «Этап N» в строке шапки этапов — формат ровно «Этап 1»/«Этап 2»/…
    private const string InstallmentStageHeaderPrefix = "Этап";

    // ─── Visary-параметры ─────────────────────────────────────────────────

    /// <summary>
    /// «Тип заключения» = <c>Stage</c>. 110 = «Итоговое заключение КА БП7»
    /// (единственный поддерживаемый импортом тип). Источник — HAR
    /// <c>Context/har заключ рассрочки равн.txt</c>, payload POST
    /// <c>/api/visary/crud/projectaudit</c>.
    /// </summary>
    internal const int ProjectAuditStageFinalBp7 = 110;

    /// <summary>Начальный <c>Status</c> Заключения — «Создан» (по HAR).</summary>
    internal const int ProjectAuditStatusInitial = 10;

    // Имя «синтетического» листа в отчёте импорта.
    private const string SyntheticSheetInstallments = "Заключение и рассрочки";

    // Конфигурация одной схемы рассрочки (маркер → префикс полей DataSetForFm).
    // Имена полей подтверждены GET /crud/datasetforfm/8030 (см. doc 139 v1.2).
    //   • Равномерная — `DDUSteady*` (RoomKinds БЕЗ Postp).
    //   • Единовременная — `DDUOneTime*` (CamelCase: T в Time с большой).
    //     RoomKinds-поле имеет постфикс `Postp` → `DDUOneTimePostpRoomKinds`.
    //   • ДКП — `DKPOwnShare`/`DKPPostpShare`, RoomKinds c постфиксом Postp →
    //     `DKPPostpRoomKinds`. Кроме того, у DKP есть `DKPPostpQuarterCount`
    //     (квартальный счётчик отсрочки) — в Excel такого параметра нет, так
    //     что мы его не PATCH-им (см. ответ пользователя).
    private sealed record InstallmentScheme(
        string Marker,
        string FieldPrefix,
        string OwnSharePropertyName,
        string PostpSharePropertyName,
        string RoomKindsPropertyName);

    private static readonly InstallmentScheme[] InstallmentSchemes =
    [
        new(
            Marker: InstallmentDDUSteadyMarker,
            FieldPrefix: "DDUSteady",
            OwnSharePropertyName:   "DDUSteadyOwnShare",
            PostpSharePropertyName: "DDUSteadyPostpShare",
            RoomKindsPropertyName:  "DDUSteadyRoomKinds"),
        new(
            Marker: InstallmentDDUOnetimeMarker,
            FieldPrefix: "DDUOneTime",
            OwnSharePropertyName:   "DDUOneTimeOwnShare",
            PostpSharePropertyName: "DDUOneTimePostpShare",
            RoomKindsPropertyName:  "DDUOneTimePostpRoomKinds"),
        new(
            Marker: InstallmentDKPMarker,
            FieldPrefix: "DKP",
            OwnSharePropertyName:   "DKPOwnShare",
            PostpSharePropertyName: "DKPPostpShare",
            RoomKindsPropertyName:  "DKPPostpRoomKinds"),
    ];

    // ─── Парсер Control ───────────────────────────────────────────────────

    /// <summary>
    /// Итог парсинга блока «Продажи» листа Control: список схем, у которых на
    /// «Этапе 1» проставлено «1 - Да», вместе с подсветкой включённых видов помещений.
    /// Если у схемы «0 - Нет» — она не попадает в результат (или попадает с
    /// пустым <see cref="EnabledRoomTypeLabels"/>).
    /// </summary>
    internal sealed record InstallmentsData(
        IReadOnlyList<EnabledInstallmentScheme> Schemes);

    /// <summary>
    /// Состояние одной схемы рассрочки в файле. <see cref="IsEnabled"/>=true —
    /// в Excel D{anchor}="1 - Да"; данные забираем для PATCH. IsEnabled=false —
    /// маркер найден, но D{anchor}="0 - Нет"; поля схемы нужно очистить в Visary
    /// (PATCH с null'ами и пустым массивом). Маркер вообще не найден — схема
    /// в <see cref="InstallmentsData.Schemes"/> отсутствует (не трогаем Visary).
    /// </summary>
    internal sealed record EnabledInstallmentScheme(
        string Marker,
        bool IsEnabled,
        double? OwnSharePercent,
        double? PostpSharePercent,
        IReadOnlyList<string> EnabledRoomTypeLabels);

    /// <summary>
    /// Парсит блок «Продажи» листа Control из открытого XLSX-потока. Результат
    /// материализуется внутри `using` (см. doc 110 §6 о жизненном цикле ClosedXML).
    /// Бросает <see cref="FinModelInstallmentsParseException"/> при отсутствии
    /// обязательных якорей; при отсутствии маркеров отдельных схем (например,
    /// блок «ДКП» удалён из шаблона) — возвращает результат без этих схем без ошибки.
    /// </summary>
    internal static InstallmentsData ReadInstallmentsData(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        if (!wb.TryGetWorksheet(InstallmentsControlSheet, out var sheet))
            throw new FinModelInstallmentsParseException(
                $"Лист «{InstallmentsControlSheet}» не найден в основном файле.");
        return ReadInstallmentsFromSheet(sheet);
    }

    /// <summary>Перегрузка для тестов — позволяет передать готовый sheet.</summary>
    internal static InstallmentsData ReadInstallmentsFromSheet(IXLWorksheet sheet)
    {
        // 1. Шапка этапов: ищем строку, в которой колонка D (или ближайшая
        //    непустая) содержит «Этап 1». Колонку этой ячейки запоминаем —
        //    в ней лежит «1 - Да»/«0 - Нет» для всех параметров левой половины Control.
        var stageRow = FindStageHeaderRow(sheet, out var stageColumn);
        if (stageRow < 0)
            throw new FinModelInstallmentsParseException(
                "Не найдена шапка этапов («Этап 1») на листе Control.");

        // 2. Блок «Продажи» — заголовок в колонке B. По эталону B61.
        var salesRow = FindRowByCellExact(sheet, columnLetter: "B",
            search: InstallmentsSalesBlockMarker, startRow: 1, maxRows: 1000);
        if (salesRow < 0)
            throw new FinModelInstallmentsParseException(
                "Не найден блок «Продажи» на листе Control.");

        // 3. Для каждой из трёх схем — ищем якорную строку в колонке B
        //    в диапазоне (salesRow+1)..(salesRow+200). За пределы блока «Продажи»
        //    не выходим — следующий блок «Затраты» стартует ≈ +50 строк.
        var schemeResults = new List<EnabledInstallmentScheme>(InstallmentSchemes.Length);
        foreach (var scheme in InstallmentSchemes)
        {
            var anchor = FindRowByCellExact(sheet, columnLetter: "B",
                search: scheme.Marker, startRow: salesRow + 1, maxRows: 200);
            if (anchor < 0)
            {
                // Шаблон без этого блока — skip без ошибки.
                continue;
            }

            // 4. Значение «Этап 1» в той же строке.
            var headerCellValue = ReadCellTextTrimmed(sheet, anchor, stageColumn);
            var isEnabled = IsYesNoYes(headerCellValue);

            // 5. «Доля отсрочек» / «Доля СУ по ипотеке» — в окне (anchor+1)..(anchor+12).
            //    В тех же ячейках стадии (stageColumn). Для выключенной схемы
            //    парсим тот же блок, но в результат пойдут пустые значения —
            //    оркестратор использует это как сигнал «очистить поля в Visary».
            double? ownShare = null;
            double? postpShare = null;
            var enabledRoomTypes = new List<string>(4);

            for (var r = anchor + 1; r <= anchor + 12; r++)
            {
                var label = ReadCellTextTrimmed(sheet, r, BCol);
                if (string.IsNullOrEmpty(label))
                    continue;

                // Натолкнулись на следующий якорь схемы / следующий блок «Продажи»
                // («Комплексный продукт» или новый «Отсрочка…»). Стоп.
                if (IsAnyOtherSchemeAnchor(label, scheme.Marker)
                    || StartsWith(label, "Комплексный продукт"))
                    break;

                // Заголовок sub-таблицы «Тип помещений» — пропускаем.
                if (StartsWith(label, InstallmentRoomTypesHeader))
                    continue;

                if (StartsWith(label, InstallmentPostpShareLabel))
                {
                    postpShare = TryReadPercentCell(sheet, r, stageColumn);
                    continue;
                }
                if (StartsWith(label, InstallmentOwnShareLabel))
                {
                    ownShare = TryReadPercentCell(sheet, r, stageColumn);
                    continue;
                }

                // Иные лейблы вида «Период отсрочки» / «Дата для …» — пропускаем
                // (на стороне импорта они не нужны: рассрочка состоит из 3 полей).
                if (StartsWith(label, "Период отсрочки")
                    || StartsWith(label, "Дата для"))
                    continue;

                // Иначе считаем, что это строка вида помещения — её ячейка-флаг
                // должна содержать «1 - Да»/«0 - Нет».
                var rkFlag = ReadCellTextTrimmed(sheet, r, stageColumn);
                if (IsYesNoYes(rkFlag))
                    enabledRoomTypes.Add(label);
            }

            schemeResults.Add(new EnabledInstallmentScheme(
                Marker: scheme.Marker,
                IsEnabled: isEnabled,
                // Если схема выключена — даже если в ячейках Excel что-то
                // случайно есть, в Visary должно уйти пусто. Если включена —
                // отправляем как распарсилось.
                OwnSharePercent: isEnabled ? ownShare : null,
                PostpSharePercent: isEnabled ? postpShare : null,
                EnabledRoomTypeLabels: isEnabled ? enabledRoomTypes : Array.Empty<string>()));
        }

        return new InstallmentsData(schemeResults);
    }

    // ─── Парсер Control (Ввод в эксплуатацию → CommisioningPeriod) ────────

    // Якоря раздела «Конфигурация этапов». Подзаголовок секции стоит выше шапки
    // таблицы с колонками «Этапы» / «Старт строительства» / «Ввод в эксплуатацию».
    private const string ConfigStagesSectionMarker = "Конфигурация этапов";
    private const string CommissioningHeaderMarker = "Ввод в эксплуатацию";
    private const string Stage1RowLabelPrefix      = "Этап 1";

    /// <summary>
    /// Дата ввода в эксплуатацию + расчёт квартала. <see cref="CommissioningDate"/>
    /// — оригинал из ячейки (для диагностики/логов); <see cref="CommissioningPeriod"/>
    /// — формат <c>"{Year}Q{N}"</c>, готов к подстановке в
    /// <see cref="FmModelCreateRequest.CommisioningPeriod"/>.
    /// </summary>
    internal sealed record CommissioningData(
        DateTime CommissioningDate,
        string CommissioningPeriod);

    /// <summary>
    /// Парсит «Конфигурация этапов» → строка «Этап 1.» → колонка «Ввод в
    /// эксплуатацию (получение РнВ)» на листе Control. Возвращает null, если
    /// какой-то из якорей не найден / ячейка не содержит даты — caller трактует
    /// это как «нет данных» (POST fmmodel идёт без CommisioningPeriod).
    /// </summary>
    internal static CommissioningData? ReadCommissioningData(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        if (!wb.TryGetWorksheet(InstallmentsControlSheet, out var sheet))
            return null;
        return ReadCommissioningFromSheet(sheet);
    }

    /// <summary>Перегрузка для тестов: работает с готовым sheet.</summary>
    internal static CommissioningData? ReadCommissioningFromSheet(IXLWorksheet sheet)
    {
        // 1. «Конфигурация этапов» — обычно в первой колонке-метке. Сканируем
        //    A..C на первых ~50 строках. Точное совпадение / contains.
        var sectionRow = FindAnyColumnRowContains(sheet,
            search: ConfigStagesSectionMarker,
            startRow: 1, endRow: 60, firstCol: 1, lastCol: 6);
        if (sectionRow < 0) return null;

        // 2. Шапка таблицы со столбцами «Старт строительства» / «Ввод в эксплуатацию» —
        //    в окне (sectionRow+1)..(sectionRow+15). Запоминаем колонку РнВ.
        int headerRow = -1, commColumn = -1;
        for (var r = sectionRow + 1; r <= sectionRow + 15; r++)
        {
            for (var c = 1; c <= 12; c++)
            {
                var text = ReadCellTextTrimmed(sheet, r, c);
                if (!string.IsNullOrEmpty(text)
                    && text.Contains(CommissioningHeaderMarker, StringComparison.OrdinalIgnoreCase))
                {
                    headerRow = r;
                    commColumn = c;
                    break;
                }
            }
            if (headerRow > 0) break;
        }
        if (headerRow < 0) return null;

        // 3. Строка «Этап 1.» (точка после 1 — заказчик так пишет). Принимаем
        //    также «Этап 1» без точки. Скан вниз от headerRow.
        int stage1Row = -1;
        int stage1Column = -1;
        for (var r = headerRow + 1; r <= headerRow + 25; r++)
        {
            for (var c = 1; c <= 6; c++)
            {
                var text = ReadCellTextTrimmed(sheet, r, c);
                if (string.IsNullOrEmpty(text)) continue;
                // Точное «Этап 1.» или «Этап 1» (но НЕ «Этап 10», «Этап 11»…).
                if (text.Equals("Этап 1.", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("Этап 1",  StringComparison.OrdinalIgnoreCase))
                {
                    stage1Row = r;
                    stage1Column = c;
                    break;
                }
            }
            if (stage1Row > 0) break;
        }
        if (stage1Row < 0) return null;

        // 4. Дата на пересечении (stage1Row, commColumn). ClosedXML
        //    TryGetValue<DateTime> работает с Excel-serial и явными датами.
        var cell = sheet.Cell(stage1Row, commColumn);
        if (cell.IsEmpty()) return null;
        DateTime date;
        if (cell.TryGetValue<DateTime>(out var d))
        {
            date = d;
        }
        else
        {
            // Текстовый fallback: «31.03.2029» / «31/03/2029» / ISO.
            var text = cell.GetFormattedString().Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!DateTime.TryParse(text, CultureInfo.GetCultureInfo("ru-RU"),
                    DateTimeStyles.AssumeLocal, out date)
                && !DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out date))
                return null;
        }

        return new CommissioningData(date, DateToFmPeriod(date));
    }

    /// <summary>
    /// Дата → строка квартала <c>"{Year}Q{N}"</c> по стандартному определению
    /// (см. уточнение заказчика 2026-06-18):
    /// <list type="bullet">
    ///   <item>Q1: январь–март  (месяцы 1..3)</item>
    ///   <item>Q2: апрель–июнь  (месяцы 4..6)</item>
    ///   <item>Q3: июль–сентябрь (месяцы 7..9)</item>
    ///   <item>Q4: октябрь–декабрь (месяцы 10..12)</item>
    /// </list>
    /// Примеры: <c>31.03.2029 → "2029Q1"</c>, <c>01.04.2029 → "2029Q2"</c>,
    /// <c>15.05.2029 → "2029Q2"</c>, <c>31.12.2029 → "2029Q4"</c>.
    /// </summary>
    internal static string DateToFmPeriod(DateTime date)
    {
        var quarter = (date.Month - 1) / 3 + 1;
        return $"{date.Year}Q{quarter}";
    }

    // ─── Маппинг XLSX-лейблов на RoomKind (Visary) ───────────────────────

    /// <summary>
    /// Маппинг лейблов из блока «Продажи» (Control) на Title справочника RoomKind.
    /// Лейбл «Квартиры/Апартаменты» — это группа: импорт создаёт <c>dataforfm</c>
    /// один раз для базового RoomKind «Квартира»; отдельная «Апартамент»-строка
    /// в этом блоке не появляется.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> ControlRoomTypeToKindTitles =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Квартиры/Апартаменты"] = new[] { "Квартира" },
            ["Квартиры"]             = new[] { "Квартира" },
            ["Апартаменты"]          = new[] { "Апартаменты" },
            ["ПСН"]                  = new[] { "Нежилое помещение" },
            ["Кладовые"]             = new[] { "Кладовая" },
            ["Машиноместа"]          = new[] { "Машиноместо" },
        };

    /// <summary>
    /// Title заголовка <c>dataforfm</c>. Сервер не валидирует Title, важна
    /// читаемость в UI Visary. По HAR — «Данные по {RoomKind в дат.падеже}»,
    /// но безопасный fallback «Данные по {RoomKind.Title}» сервер тоже принимает.
    /// </summary>
    private static string BuildDataForFmTitle(string roomKindTitle) =>
        roomKindTitle switch
        {
            "Квартира"     => "Данные по Квартирам",
            "Апартаменты"  => "Данные по Апартаментам",
            "Нежилое помещение" => "Данные по Нежилым помещениям",
            "Кладовая"     => "Данные по Кладовым",
            "Машиноместо"  => "Данные по Машиноместам",
            _              => $"Данные по {roomKindTitle}",
        };

    // ─── Оркестратор ──────────────────────────────────────────────────────

    /// <summary>
    /// Главный шаг: создаёт <c>projectaudit</c> (Заключение «Итоговое заключение
    /// КА БП7»), находит автоматически созданный <c>datasetforfm</c>, создаёт по
    /// одной <c>dataforfm</c> на каждый включённый RoomKind и PATCH-ит datasetforfm
    /// тройкой полей для каждой включённой схемы рассрочки.
    /// <para/>
    /// Дизайн: ни одно из ограничений (нет включённых схем, нет файла, нет проекта,
    /// невозможность распарсить) не должно валить весь Apply — пишем единичную
    /// row-error и тихо выходим. Бюджет/ГФ уже отработали выше.
    /// </summary>
    private async Task EnsureProjectAuditAndInstallmentsAsync(
        int projectId,
        int siteId,
        string? primaryFilePath,
        bool paramsApplied,
        bool? budgetUploadOk,
        List<RowError> errors,
        SyntheticRowEmitter synthetic,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(primaryFilePath))
        {
            // Без основного файла Excel-парсера нет смысла продолжать.
            _log.LogDebug(
                "FinModelImportMapper.Installments: primary file path empty (siteId={SiteId}) — skipped",
                siteId);
            return;
        }

        // 1) Парсим блок «Продажи» Control. Площади из Outputs больше не читаем
        //    (Indicator у dataforfm не заполняется — см. doc 141). Любая ошибка
        //    парсинга — одна row-error + skip всего шага.
        InstallmentsData installments;
        try
        {
            await using var stream = await _fileStorage.OpenReadAsync(primaryFilePath, ct);
            installments = ReadInstallmentsData(stream);
        }
        catch (FinModelInstallmentsParseException ex)
        {
            errors.Add(new RowError(null, "installments_parse_error",
                "Не удалось прочитать блок «Продажи» из основного файла: " + ex.Message));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                ["Заключение и рассрочки: ошибка парсинга — " + ex.Message]);
            return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FinModelImportMapper.Installments: чтение основного файла упало (siteId={SiteId}, path={Path})",
                siteId, primaryFilePath);
            errors.Add(new RowError(null, "installments_file_read_error",
                "Не удалось открыть основной файл для блока «Продажи»: " + ex.Message));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                ["Заключение и рассрочки: ошибка чтения файла — " + ex.Message]);
            return;
        }

        // 2) Если ни одна схема не включена И ни одна dataforfm-строка не нужна,
        //    создавать Заключение бессмысленно. Skip с информационной записью.
        var anyScheme = installments.Schemes.Any(s => s.IsEnabled && s.EnabledRoomTypeLabels.Count > 0);
        if (!anyScheme)
        {
            _log.LogInformation(
                "FinModelImportMapper.Installments: ни одна схема рассрочек не включена в файле — Заключение не создаётся (siteId={SiteId})",
                siteId);
            errors.Add(new RowError(null, "installments_skipped_no_schemes",
                "В файле на листе Control в блоке «Продажи» ни для одной схемы рассрочек не проставлено «1 - Да». " +
                "Создание Заключения и Набора данных для ФМ пропущено."));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                ["Заключение и рассрочки: нет включённых схем — пропуск"]);
            return;
        }

        // 3) Резолв справочника RoomKind. Возможны 2 проблемы:
        //   • Visary недоступен → row-error и skip всего шага;
        //   • в Visary нет нужного Title → row-error по конкретной строке dataforfm
        //     при попытке создать; продолжаем для остальных RoomKind.
        Dictionary<string, (int Id, string Title)> kindByTitle;
        try
        {
            var resp = await _listViewClient.ListRoomKindsAsync(ct);
            kindByTitle = new Dictionary<string, (int Id, string Title)>(StringComparer.OrdinalIgnoreCase);
            foreach (var rk in resp.Data ?? new List<RoomKindRaw>())
            {
                if (rk.ID <= 0 || string.IsNullOrWhiteSpace(rk.Title))
                    continue;
                kindByTitle[rk.Title!] = (rk.ID, rk.Title!);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FinModelImportMapper.Installments: listview/roomkind упал (siteId={SiteId})", siteId);
            errors.Add(new RowError(null, "installments_roomkind_unavailable",
                "Не удалось получить справочник «Виды помещений» из Visary: " + ex.Message + ". " +
                "Заключение и рассрочки не созданы."));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                ["Заключение: справочник видов помещений недоступен — " + ex.Message]);
            return;
        }

        // 3.5) Процентные ставки сделки (doc 139 v1.4) — перед созданием
        //      Заключения. Шаг ортогонален к схемам рассрочек: если в файле есть
        //      «Номер КД» и в Visary найдена сделка в нашем проекте, создаём
        //      по одной dealpercentbet на каждую включённую ставку Этапа 1
        //      (LM10/LM20/LM30/LM40). Любые проблемы (КД отсутствует, сделка не
        //      найдена / в чужом проекте / несколько по КД) → row-error + skip
        //      ставок, далее идём к POST projectaudit как и раньше.
        // Возвращает Deal.ID найденной сделки (или null, если не нашли) —
        // для doc 142 (dealmonthlydata) тот же deal используем сразу следом.
        var resolvedDealId = await EnsureDealPercentBetsAsync(
            projectId, siteId, primaryFilePath, errors, synthetic, ct);

        // 3.6) Помесячные данные по сделке (doc 142) — POST dealmonthlydata
        //      по разделу «Инвестиционный кредит: Этап 1» листа Outputs.
        //      Создаётся одна запись на (Deal, ТекущийГод, ТекущийМесяц).
        //      Использует тот же deal, что и ставки; если шаг ставок не нашёл
        //      сделку (resolvedDealId=null), шаг помесячных данных пропускается.
        if (resolvedDealId is { } dealIdForMonthly)
        {
            await EnsureDealMonthlyDataAsync(
                dealIdForMonthly, siteId, primaryFilePath, errors, synthetic, ct);
        }

        // 4) Создание Заключения. По требованию заказчика — каждый импорт
        //    создаёт НОВУЮ запись «Итоговое заключение КА БП7». Идемпотентности
        //    нет: в Visary можно вручную удалить лишние; pre-check мог бы
        //    «угнать» Заключение, созданное вручную или предыдущим импортом
        //    другой версии файла. См. doc 139 v1.1 — был pre-check по
        //    (Site, Stage), но в логах он реюзал чужое 7135-е Заключение.
        int projectAuditId;
        try
        {
            var created = await _visaryClient.CreateProjectAuditAsync(new ProjectAuditCreateRequest
            {
                Date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Status = ProjectAuditStatusInitial,
                Stage = ProjectAuditStageFinalBp7,
                ProjectID = projectId,
                Project = new VisaryRef { ID = projectId },
                ConstructionSite = new VisaryRef { ID = siteId },
            }, ct);
            projectAuditId = created.ID;
            _log.LogInformation(
                "FinModelImportMapper.Installments: projectaudit создан id={Id} stage={Stage} (siteId={SiteId})",
                created.ID, ProjectAuditStageFinalBp7, siteId);
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                [$"Заключение «Итоговое заключение КА БП7»: создано (id={created.ID})"]);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FinModelImportMapper.Installments: ошибка создания projectaudit (siteId={SiteId})", siteId);
            errors.Add(new RowError(null, "project_audit_create_failed",
                "Не удалось создать Заключение «Итоговое заключение КА БП7»: " + ex.Message));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                ["Заключение: ошибка создания — " + ex.Message]);
            return;
        }

        // 5) Найти DataSetForFm — он автоматически создаётся сервером при POST
        //    projectaudit (см. HAR). На паре (Site, Project) — 1 запись.
        int dataSetId;
        long dataSetRowVersion;
        try
        {
            var resp = await _listViewClient.FindDataSetForFmAsync(siteId, projectId, ct);
            var first = resp.Data?.FirstOrDefault(d => d.ID > 0);
            if (first is null)
            {
                errors.Add(new RowError(null, "datasetforfm_not_found",
                    "После создания Заключения сервер не вернул «Набор данных для ФМ» по (Site, Project). " +
                    "Заполнение рассрочек невозможно — проверьте конфигурацию Visary."));
                synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                    ["Набор данных для ФМ: не найден после создания Заключения"]);
                return;
            }
            dataSetId = first.ID;
            // Для PATCH нужен RowVersion — берём явный GET.
            var fresh = await _visaryClient.GetDataSetForFmByIdAsync(dataSetId, ct);
            dataSetRowVersion = fresh.RowVersion ?? 0;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FinModelImportMapper.Installments: ошибка поиска datasetforfm (siteId={SiteId})", siteId);
            errors.Add(new RowError(null, "datasetforfm_lookup_failed",
                "Не удалось получить «Набор данных для ФМ» из Visary: " + ex.Message));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                ["Набор данных для ФМ: ошибка поиска — " + ex.Message]);
            return;
        }

        // 6) Pre-check существующих dataforfm. На сервере действует ограничение
        //    `UX_DataForFM_DataSetForFMID_RoomKindID` — попытка POST дубликата
        //    даёт 422. Поэтому собираем `RoomKindId → existingDataForFmId`:
        //      • есть запись → PATCH (обновляем Indicator);
        //      • нет записи → POST.
        //    Если listview упал — продолжаем без pre-check; при последующем
        //    POST'е 422 ловится в catch ниже и трактуется как «уже есть, skip».
        var existingDataForFmIdByKindId = new Dictionary<int, int>();
        try
        {
            var resp = await _listViewClient.GetDataForFmByDataSetAsync(dataSetId, ct);
            foreach (var d in resp.Data ?? new List<DataForFmRaw>())
            {
                if (d.RoomKind?.ID is { } kindId && kindId > 0 && d.ID > 0)
                    existingDataForFmIdByKindId[kindId] = d.ID;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "FinModelImportMapper.Installments: pre-check dataforfm не удался — продолжаем без него (dataSetId={DataSetId})",
                dataSetId);
        }

        // 7) Собрать набор RoomKind, для которых нужно создать dataforfm
        //    (объединение по всем включённым схемам). Параллельно строим
        //    словарь label → (kindId, kindTitle) — нужен для PATCH datasetforfm
        //    в шаге 9 (поле RoomKinds одной схемы).
        var roomKindsByControlLabel = new Dictionary<string, (int Id, string Title)>(StringComparer.OrdinalIgnoreCase);
        var roomKindsToCreate = new Dictionary<int, string>();
        var unresolvedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scheme in installments.Schemes)
        {
            foreach (var label in scheme.EnabledRoomTypeLabels)
            {
                if (roomKindsByControlLabel.ContainsKey(label))
                    continue; // уже обработан

                if (!ControlRoomTypeToKindTitles.TryGetValue(label, out var kindTitles))
                {
                    unresolvedLabels.Add(label);
                    continue;
                }

                foreach (var kindTitle in kindTitles)
                {
                    if (!kindByTitle.TryGetValue(kindTitle, out var kindRef))
                    {
                        unresolvedLabels.Add($"{label} → {kindTitle}");
                        continue;
                    }
                    roomKindsByControlLabel[label] = kindRef;
                    roomKindsToCreate.TryAdd(kindRef.Id, kindRef.Title);
                }
            }
        }

        if (unresolvedLabels.Count > 0)
        {
            errors.Add(new RowError(null, "installments_roomkind_not_resolved",
                "Виды помещений из блока «Продажи» не удалось сопоставить со справочником Visary: " +
                string.Join(", ", unresolvedLabels.Select(l => $"«{l}»")) + ". " +
                "Соответствующие строки «Данные для ФМ» не созданы; рассрочки могут содержать неполный список видов."));
        }

        // 8) Для каждого RoomKind: если в Visary уже есть dataforfm под пару
        //    (DataSet, RoomKind) — skip (заказчик: Indicator не заполняем, ничего
        //    обновлять не нужно). Если нет — POST с RoomKind+Title; Indicator
        //    не передаём (см. doc 141).
        foreach (var (kindId, kindTitle) in roomKindsToCreate)
        {
            ct.ThrowIfCancellationRequested();

            if (existingDataForFmIdByKindId.TryGetValue(kindId, out var existingId))
            {
                synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                    [$"Данные для ФМ [{kindTitle}]: уже существует (id={existingId}) — пропуск"]);
                continue;
            }

            try
            {
                var created = await _visaryClient.CreateDataForFmAsync(new DataForFmCreateRequest
                {
                    DataSetForFMID = dataSetId,
                    DataSetForFM = new VisaryRef { ID = dataSetId },
                    Title = BuildDataForFmTitle(kindTitle),
                    RoomKind = new VisaryRef { ID = kindId, Title = kindTitle },
                }, ct);
                existingDataForFmIdByKindId[kindId] = created.ID;
                synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                    [$"Данные для ФМ [{kindTitle}]: создано (id={created.ID})"]);
            }
            catch (Exception ex) when (IsDuplicateDataForFmConflict(ex))
            {
                // 422 на уникальном (DataSetForFMID, RoomKindID): pre-check
                // не нашёл запись (variant-поле / транспорт-fail), но она есть.
                // Indicator не апдейтим, поэтому просто пропускаем — Visary не
                // даст создать дубликат.
                _log.LogInformation(
                    "FinModelImportMapper.Installments: dataforfm для kindId={KindId} уже существует (422) — пропуск (dataSetId={DataSetId})",
                    kindId, dataSetId);
                synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                    [$"Данные для ФМ [{kindTitle}]: уже существует в Visary — пропуск"]);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "FinModelImportMapper.Installments: ошибка создания dataforfm (kindId={KindId}, dataSetId={DataSetId})",
                    kindId, dataSetId);
                errors.Add(new RowError(null, "dataforfm_create_failed",
                    $"Не удалось создать «Данные для ФМ» по виду «{kindTitle}»: {ex.Message}"));
                synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                    [$"Данные для ФМ [{kindTitle}]: ошибка создания — {ex.Message}"]);
            }
        }

        // 9) PATCH datasetforfm одной схемой за раз. RowVersion перечитываем
        //    между PATCH'ами — каждый успешный PATCH инкрементит его на сервере.
        //    Логика:
        //      • Маркер схемы найден в Excel и IsEnabled=true → PATCH значениями.
        //      • Маркер найден, IsEnabled=false → PATCH null'ами (очистка
        //        полей в Visary, чтобы старые данные из предыдущего импорта /
        //        ручной правки не остались).
        //      • Маркер не найден (нет блока в шаблоне) → schema отсутствует
        //        в installments.Schemes → пропускаем (не трогаем Visary).
        foreach (var scheme in InstallmentSchemes)
        {
            var schemeData = installments.Schemes.FirstOrDefault(s => s.Marker == scheme.Marker);
            if (schemeData is null)
                continue; // блок схемы отсутствует в Excel — не трогаем поля Visary

            var kinds = schemeData.IsEnabled
                ? schemeData.EnabledRoomTypeLabels
                    .Where(l => roomKindsByControlLabel.ContainsKey(l))
                    .Select(l => roomKindsByControlLabel[l])
                    .DistinctBy(r => r.Id)
                    .Select(r => new VisaryRef { ID = r.Id, Title = r.Title })
                    .ToList()
                : new List<VisaryRef>();

            try
            {
                // Перечитать RowVersion непосредственно перед PATCH.
                var fresh = await _visaryClient.GetDataSetForFmByIdAsync(dataSetId, ct);
                dataSetRowVersion = fresh.RowVersion ?? dataSetRowVersion;

                await _visaryClient.PatchDataSetForFmInstallmentsAsync(
                    new DataSetForFmInstallmentsPatchRequest
                    {
                        ID = dataSetId,
                        RowVersion = dataSetRowVersion,
                        OwnSharePropertyName   = scheme.OwnSharePropertyName,
                        PostpSharePropertyName = scheme.PostpSharePropertyName,
                        RoomKindsPropertyName  = scheme.RoomKindsPropertyName,
                        OwnShare = schemeData.OwnSharePercent,
                        PostpShare = schemeData.PostpSharePercent,
                        RoomKinds = kinds,
                    }, ct);

                if (schemeData.IsEnabled)
                {
                    synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                        [$"Рассрочка [{scheme.Marker}]: записана (виды: {string.Join(", ", kinds.Select(k => k.Title))}, " +
                         $"OwnShare={schemeData.OwnSharePercent:0.##}, PostpShare={schemeData.PostpSharePercent:0.##})"]);
                }
                else
                {
                    synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                        [$"Рассрочка [{scheme.Marker}]: выключена в Excel — поля очищены"]);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "FinModelImportMapper.Installments: ошибка PATCH datasetforfm для схемы '{Marker}' (dataSetId={DataSetId})",
                    scheme.Marker, dataSetId);
                errors.Add(new RowError(null, "datasetforfm_patch_failed",
                    $"Не удалось записать поля рассрочки «{scheme.Marker}» (поля {scheme.FieldPrefix}OwnShare/PostpShare/RoomKinds): {ex.Message}"));
                synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                    [$"Рассрочка [{scheme.Marker}]: ошибка PATCH — {ex.Message}"]);
            }
        }

        // Подсказки заказчику, если что-то не сделано.
        _log.LogInformation(
            "FinModelImportMapper.Installments: завершено siteId={SiteId} projectAuditId={ProjectAuditId} dataSetId={DataSetId} schemes={SchemeCount} kinds={KindCount} paramsApplied={ParamsApplied} budgetUploadOk={BudgetOk}",
            siteId, projectAuditId, dataSetId, installments.Schemes.Count, roomKindsToCreate.Count,
            paramsApplied, budgetUploadOk);
    }

    // ─── Парсер Control (Результаты + Финансирование → ставки сделки) ────

    // Якоря разделов листа Control для блока процентных ставок (см. doc 139 v1.4
    // и doc 141: ставки лежат под подразделом «Инвестиционные кредиты» внутри
    // раздела «Финансирование»; первичный якорь — подраздел, fallback —
    // основной раздел).
    private const string ResultsBlockMarker             = "Результаты";
    private const string KdNumberHeaderMarker           = "Номер КД";
    private const string InvestmentCreditsBlockMarker   = "Инвестиционные кредиты";
    private const string FinancingBlockMarker           = "Финансирование";

    // Имя «синтетического» листа в отчёте — для шага ставок переиспользуем тот же
    // что и у Заключения; в отчёте «Заключение и рассрочки» все out-of-band-записи
    // живут вместе (см. ApplyAsync).

    /// <summary>
    /// Результат парсинга разделов «Результаты» + «Финансирование» листа Control:
    /// номер КД (опционально) + список включённых ставок Этапа 1.
    /// </summary>
    /// <remarks>
    /// <see cref="KdNumber"/> nullable: если в файле нет раздела «Результаты» / нет
    /// строки «Номер КД» — возвращается null, оркестратор молча пропускает шаг
    /// ставок (создание Заключения продолжается).
    /// <para/>
    /// <see cref="Rates"/> пустой, если в «Финансирование» все строки помечены
    /// «0 - Нет» или раздел не найден — оркестратор пропустит создание ставок
    /// (это не ошибка, см. doc 139 v1.4).
    /// </remarks>
    internal sealed record FinancingData(
        string? KdNumber,
        IReadOnlyList<EnabledFinancingRate> Rates);

    /// <summary>
    /// Одна включённая ставка для Этапа 1. <see cref="Rate"/> берётся из ячейки
    /// «Этап 1» строки-лейбла: число/процент — как есть; текст вида <c>«N - X»</c>
    /// — ведущая цифра N (см. <see cref="TryReadPercentCell"/>).
    /// <see cref="Label"/> — лейбл строки-ставки (для логов/synthetic отчёта;
    /// пустой для синтетических вставок из тестов).
    /// </summary>
    internal sealed record EnabledFinancingRate(
        string Code,
        int PercentKind,
        double Rate,
        string? Label = null);

    // Мапа кода ставки → PercentKind (поле Visary, см. dealpercentbet).
    // 7 кодов: LM10..LM40 + новые LM50/LM60/LM70 (doc 143).
    private static readonly Dictionary<string, int> FinancingPercentKindByCode =
        new(StringComparer.Ordinal)
        {
            ["LM10"] = 10,
            ["LM20"] = 20,
            ["LM30"] = 30,
            ["LM40"] = 40,
            ["LM50"] = 50,
            ["LM60"] = 60,
            ["LM70"] = 70,
        };

    /// <summary>
    /// Для LM10/LM20/LM30 Rate берётся не из ячейки родителя, а из ячейки
    /// «Этап 1» подстроки с одним из приведённых лейблов (заказчик: «Надо
    /// искать значение напротив строк…»). Если родитель «0 - Нет» — ставка
    /// не создаётся; если у обеих подстрок «Этап 1» пуст/«0 - Нет» —
    /// ставка тоже не создаётся.
    /// <para/>
    /// LM40/LM50/LM60/LM70 — одна строка = одна ставка с Rate из той же
    /// строки родителя, sub-row lookup для них не применяется.
    /// </summary>
    private static readonly Dictionary<string, string[]> SubRowRateLabelsByCode =
        new(StringComparer.Ordinal)
        {
            ["LM10"] = new[]
            {
                "Фиксированная ставка (сценарий 1)",
                "Премия к КС РФ (фикс) (сценарий 2)",
            },
            ["LM20"] = new[]
            {
                "Ручной ввод периода отсрочки (сценарий 2), кварталы",
                "Доля капитализации/отсрочки процентов в тело долга (сценарии 1-3)",
            },
            ["LM30"] = new[]
            {
                "Фиксированная ставка (сценарии 1-2)",
                "Премия к КС РФ (фикс) (сценарии 1-2)",
            },
        };

    /// <summary>
    /// Открывает Control и читает разделы «Результаты» (КД) + «Финансирование»
    /// (4 ставки). Возвращает пустую структуру, если листа Control нет —
    /// оркестратор молча пропустит создание ставок.
    /// </summary>
    internal static FinancingData ReadFinancingData(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        if (!wb.TryGetWorksheet(InstallmentsControlSheet, out var sheet))
            return new FinancingData(null, Array.Empty<EnabledFinancingRate>());
        return ReadFinancingFromSheet(sheet);
    }

    /// <summary>Перегрузка для тестов.</summary>
    internal static FinancingData ReadFinancingFromSheet(IXLWorksheet sheet)
    {
        // Шапка этапов — общая для левой половины Control: используется и в блоке
        // «Продажи» (рассрочки), и в блоке «Финансирование» (ставки). Если шапки
        // нет — ставки распарсить нельзя.
        var stageRow = FindStageHeaderRow(sheet, out var stageColumn);
        var kdNumber = TryReadKdNumber(sheet);
        if (stageRow < 0)
            return new FinancingData(kdNumber, Array.Empty<EnabledFinancingRate>());

        var rates = ReadFinancingRates(sheet, stageColumn);
        return new FinancingData(kdNumber, rates);
    }

    /// <summary>
    /// Читает «Номер КД» с листа Control. Алгоритм:
    /// <list type="number">
    ///   <item>Сначала пытаемся найти раздел «Результаты» (anchor) — если есть,
    ///         поиск «Номер КД» ограничен ~50 строками ниже него (быстрее и
    ///         защищает от ложных совпадений в другом разделе).</item>
    ///   <item>Если раздел не найден или «Номер КД» под ним нет — сканируем
    ///         ВЕСЬ лист (заказчик может разнести «Результаты» и «Номер КД»
    ///         далеко друг от друга, или вовсе убрать заголовок секции).</item>
    /// </list>
    /// Для каждого найденного «Номер КД» — берём значение из ячейки СНИЗУ
    /// (вертикальная раскладка по спецификации); fallback — справа
    /// (горизонтальный layout из doc 105). Не пустое → возвращаем.
    /// Пустые / только пробелы → продолжаем поиск (может быть другой
    /// заголовок ниже).
    /// </summary>
    private static string? TryReadKdNumber(IXLWorksheet sheet)
    {
        // 1) Узкий поиск под разделом «Результаты», если он есть.
        var sectionRow = FindAnyColumnRowContains(sheet,
            search: ResultsBlockMarker,
            startRow: 1, endRow: 500, firstCol: 1, lastCol: 6);
        if (sectionRow > 0)
        {
            var below = ScanForKdNumberHeader(sheet, sectionRow + 1, sectionRow + 50);
            if (below is not null) return below;
        }

        // 2) Глобальный fallback: сканируем весь лист (до конца использованной
        //    области). Заказчик может разместить «Номер КД» вне явного раздела.
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        if (lastRow <= 0) return null;
        return ScanForKdNumberHeader(sheet, 1, lastRow);
    }

    /// <summary>Сканирует диапазон строк в поисках заголовка «Номер КД» и возвращает
    /// значение из ячейки снизу (или справа как fallback). null если не нашёл.</summary>
    private static string? ScanForKdNumberHeader(IXLWorksheet sheet, int startRow, int endRow)
    {
        var lastCol = Math.Min(50, sheet.LastColumnUsed()?.ColumnNumber() ?? 30);
        for (var r = startRow; r <= endRow; r++)
        {
            for (var c = 1; c <= lastCol; c++)
            {
                var text = ReadCellTextTrimmed(sheet, r, c);
                if (string.IsNullOrEmpty(text)) continue;

                // Заголовок матчим строго — «Номер КД» (case-insensitive).
                // Контейн-match слишком жадный: «Номер КД (старый)» тоже подцепится,
                // но это OK — заказчик использует одно поле.
                if (!text.Contains(KdNumberHeaderMarker, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Значение снизу — приоритетно (вертикальная раскладка спецификации).
                var below = ReadCellTextTrimmed(sheet, r + 1, c);
                if (!string.IsNullOrWhiteSpace(below)) return below;

                // Fallback: значение справа (горизонтальная раскладка doc 105).
                var right = ReadCellTextTrimmed(sheet, r, c + 1);
                if (!string.IsNullOrWhiteSpace(right)) return right;
            }
        }
        return null;
    }

    /// <summary>
    /// Сканирует раздел «Финансирование» → подраздел «Инвестиционные кредиты»
    /// (Control). Поведение зависит от кода ставки (см. <see cref="SubRowRateLabelsByCode"/>):
    /// <list type="number">
    ///   <item><b>LM10/LM20/LM30</b> — родитель + специфичные подстроки. Если
    ///         родитель в «Этап 1» содержит «0 - Нет» — ставка не создаётся.
    ///         Иначе ищем подстроки с лейблами из <see cref="SubRowRateLabelsByCode"/>
    ///         в окне (parent.Row+1 .. nextParent.Row-1); берём Rate из первой
    ///         непустой ячейки «Этап 1» подстроки. Если все подстроки пусты —
    ///         ставка не создаётся (заказчик: «Если в подстроках нет значений,
    ///         тогда ставку не создавать»).</item>
    ///   <item><b>LM40/LM50/LM60/LM70</b> — одна строка = одна ставка. Rate
    ///         берётся прямо из ячейки «Этап 1» родительской строки. Пустая
    ///         ячейка / «0 - Нет» → ставка не создаётся.</item>
    /// </list>
    /// Все остальные строки в блоке (без матча на LM-код) — не ставки, игнор.
    /// </summary>
    private static IReadOnlyList<EnabledFinancingRate> ReadFinancingRates(
        IXLWorksheet sheet, int stageColumn)
    {
        // Якорь — подраздел «Инвестиционные кредиты»; fallback — «Финансирование».
        var anchorRow = FindAnyColumnRowContains(sheet,
            search: InvestmentCreditsBlockMarker,
            startRow: 1, endRow: 500, firstCol: 1, lastCol: 6);
        if (anchorRow < 0)
        {
            anchorRow = FindAnyColumnRowContains(sheet,
                search: FinancingBlockMarker,
                startRow: 1, endRow: 500, firstCol: 1, lastCol: 6);
        }
        if (anchorRow < 0) return Array.Empty<EnabledFinancingRate>();

        const int blockSize = 80;
        var blockEnd = anchorRow + blockSize;

        // Шаг 1: собрать все родительские LM-строки (по 1 на код, первое
        // совпадение в блоке). Это даёт нам границы окон подстрок для LM10/20/30.
        var parents = new List<(int Row, string Code, int Kind, string Label)>(7);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        for (var r = anchorRow + 1; r <= blockEnd; r++)
        {
            string? label = null;
            for (var labelCol = 1; labelCol <= 3; labelCol++)
            {
                var t = ReadCellTextTrimmed(sheet, r, labelCol);
                if (!string.IsNullOrEmpty(t)) { label = t; break; }
            }
            if (label is null) continue;

            var code = TryMatchFinancingRateCode(label);
            if (code is null) continue;
            if (!matched.Add(code)) continue;
            if (!FinancingPercentKindByCode.TryGetValue(code, out var kind)) continue;

            parents.Add((r, code, kind, label));
        }
        if (parents.Count == 0) return Array.Empty<EnabledFinancingRate>();

        // Шаг 2: для каждого родителя — построить ставку по правилам её типа.
        var result = new List<EnabledFinancingRate>(parents.Count);
        for (var i = 0; i < parents.Count; i++)
        {
            var parent = parents[i];
            var parentCell = ReadCellTextTrimmed(sheet, parent.Row, stageColumn);

            // «0 - Нет» в родителе → ставка отключена.
            if (IsYesNoNo(parentCell)) continue;

            double? rate;
            if (SubRowRateLabelsByCode.TryGetValue(parent.Code, out var subLabels))
            {
                // LM10/LM20/LM30: ищем Rate в специфичной подстроке.
                var windowEnd = i + 1 < parents.Count
                    ? parents[i + 1].Row
                    : Math.Min(parent.Row + 10, blockEnd + 1);
                rate = FindRateInSubRows(sheet, parent.Row + 1, windowEnd, stageColumn, subLabels);
            }
            else
            {
                // LM40/LM50/LM60/LM70: Rate из самой строки родителя.
                rate = TryReadPercentCell(sheet, parent.Row, stageColumn)
                       ?? TryParseLeadingNumber(parentCell);
            }

            if (rate is null) continue;

            result.Add(new EnabledFinancingRate(parent.Code, parent.Kind, rate.Value, parent.Label));
        }
        return result;
    }

    /// <summary>
    /// Ищет первую подстроку в окне [startRow, endRowExclusive), чей лейбл
    /// совпадает (нормализованный contains) с одним из <paramref name="targetLabels"/>,
    /// и у которой в ячейке «Этап 1» есть распознаваемое значение. Возвращает
    /// Rate (как из <see cref="TryReadPercentCell"/>) или <c>null</c>, если ни
    /// одна подстрока не содержит значения.
    /// </summary>
    private static double? FindRateInSubRows(
        IXLWorksheet sheet,
        int startRow,
        int endRowExclusive,
        int stageColumn,
        IReadOnlyList<string> targetLabels)
    {
        var normalizedTargets = targetLabels
            .Select(NormalizeFinancingLabel)
            .ToArray();

        for (var r = startRow; r < endRowExclusive; r++)
        {
            string? subLabel = null;
            for (var c = 1; c <= 3; c++)
            {
                var t = ReadCellTextTrimmed(sheet, r, c);
                if (!string.IsNullOrEmpty(t)) { subLabel = t; break; }
            }
            if (subLabel is null) continue;

            var subNorm = NormalizeFinancingLabel(subLabel);
            var isTarget = normalizedTargets.Any(target =>
                subNorm.Contains(target, StringComparison.Ordinal)
                || target.Contains(subNorm, StringComparison.Ordinal));
            if (!isTarget) continue;

            var subVal = ReadCellTextTrimmed(sheet, r, stageColumn);
            if (string.IsNullOrEmpty(subVal)) continue;
            if (IsYesNoNo(subVal)) continue;

            var rate = TryReadPercentCell(sheet, r, stageColumn)
                       ?? TryParseLeadingNumber(subVal);
            if (rate is not null) return rate;
        }
        return null;
    }

    /// <summary>Лейбл sub-строки — обычно в B (как и родитель), но проверяем 1..4.</summary>
    private static string TryReadSubrowLabel(IXLWorksheet sheet, int row)
    {
        for (var c = 1; c <= 4; c++)
        {
            var text = ReadCellTextTrimmed(sheet, row, c);
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return string.Empty;
    }

    /// <summary>
    /// Парсит ведущее число строки (с поддержкой «,»/«.»). Используется когда
    /// ячейка sub-строки содержит текст-флаг «N - X» (например, «1 - Фиксированная»):
    /// возвращает <c>N</c>. Если в начале нет цифр — <c>null</c>.
    /// </summary>
    private static double? TryParseLeadingNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.TrimStart();
        var i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == ','))
            i++;
        if (i == 0) return null;
        var num = s.Substring(0, i).Replace(",", ".", StringComparison.Ordinal);
        return double.TryParse(num, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    /// <summary>
    /// Распознаёт строку лейбла процентной ставки. Возвращает <c>LM10..LM70</c>
    /// или <c>null</c>. ORDER MATTERS:
    /// <list type="number">
    ///   <item>LM60 «Коэф покрытия эскроу/долг…» — проверяем по «эскроу» (уникально).</item>
    ///   <item>LM50 «Спец. процентная ставка» — по «спец» + «процентная ставка».</item>
    ///   <item>LM70 «Выбор ставки для капитализации процентов» — по «выбор ставки».</item>
    ///   <item>LM30 «Базовая процентная ставка по капи(т|ат)ализированным…» —
    ///         проверяем сначала него (есть «процентная ставка»), иначе LM10
    ///         поглотит. У заказчика встречается опечатка «капиатализированным»
    ///         (R190 файла «Параметры к переносу») — матчим по корню «капи».</item>
    ///   <item>LM20 «Капитализация / отсрочка уплаты %%».</item>
    ///   <item>LM40 «Комисия за отсрочку %%» (с одной «с» в исходнике).</item>
    ///   <item>LM10 «Базовая %% ставка» (узкое — нет «процентная»).</item>
    /// </list>
    /// </summary>
    internal static string? TryMatchFinancingRateCode(string label)
    {
        var n = NormalizeFinancingLabel(label);
        // LM60: «Коэф покрытия эскроу/долг для перехода на 0,01% (для спец ставки)».
        if (n.Contains("эскроу", StringComparison.Ordinal)
            && n.Contains("долг", StringComparison.Ordinal)) return "LM60";
        // LM50: «Спец. процентная ставка» (двойной пробел в исходнике).
        if (n.Contains("спец", StringComparison.Ordinal)
            && n.Contains("процентная ставка", StringComparison.Ordinal)) return "LM50";
        // LM70: «Выбор ставки для капитализации процентов».
        if (n.Contains("выбор ставки", StringComparison.Ordinal)) return "LM70";
        // LM30: «Базовая процентная ставка по капи(т|ат)ализированным %%».
        if (n.Contains("базовая", StringComparison.Ordinal)
            && n.Contains("процентная ставка", StringComparison.Ordinal)
            && n.Contains("капи", StringComparison.Ordinal)) return "LM30";
        // LM20: «Капитализация / отсрочка уплаты %%».
        if (n.Contains("капитализация", StringComparison.Ordinal)
            || n.Contains("отсрочка уплаты", StringComparison.Ordinal)) return "LM20";
        // LM40: «Комисия за отсрочку %%».
        if (n.Contains("комис", StringComparison.Ordinal)
            && n.Contains("отсрочк", StringComparison.Ordinal)) return "LM40";
        // LM10: «Базовая %% ставка» — узкое, без «процентная».
        if (n.Contains("базовая", StringComparison.Ordinal)
            && n.Contains("ставка", StringComparison.Ordinal)
            && !n.Contains("процентная", StringComparison.Ordinal)) return "LM10";
        return null;
    }

    /// <summary>
    /// Приводит лейбл ставки к каноничной форме (lower-case, без «%», свёрнутые
    /// пробелы). Это позволяет матчить варианты типа «%%»/«% %»/«%»/«100 %» и
    /// разный регистр без явных перечислений.
    /// </summary>
    private static string NormalizeFinancingLabel(string text)
    {
        var s = text.ToLowerInvariant().Trim();
        s = s.Replace("%", string.Empty, StringComparison.Ordinal);
        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s;
    }

    /// <summary>
    /// Сравнение double-полей dealmonthlydata «строгое, но с эпсилоном
    /// доли копейки» (1e-6). Используется как локальный фильтр после
    /// listview-pre-check'а, потому что Visary не уважает наши range-фильтры
    /// (>=N AND <=N) на double-полях и возвращает «соседние» записи. Null
    /// в Visary считаем равным 0 (так Visary хранит default).
    /// </summary>
    internal static bool NumberEqualsExact(double? visaryValue, double parsedValue)
    {
        var v = visaryValue ?? 0d;
        return Math.Abs(v - parsedValue) < 1e-6;
    }

    /// <summary>
    /// Нормализованное сравнение «Номер КД» из Excel с DocNumber из Visary.
    /// Заказчик: «В ячейке написан реальный номер договора, он может содержать
    /// буквы, цифры и символы». Excel может вставлять non-breaking space
    /// ( ), zero-width space (​), таб'ы и подряд идущие пробелы —
    /// strict Equals на них ломается. Схлопываем все whitespace-классы в один
    /// обычный пробел, обрезаем, lower-case.
    /// </summary>
    internal static string NormalizeDocNumber(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s)
        {
            // Любая whitespace + NBSP + ZWSP → один обычный пробел.
            if (char.IsWhiteSpace(ch) || ch == ' ' || ch == '​')
            {
                if (!prevSpace && sb.Length > 0) { sb.Append(' '); prevSpace = true; }
                continue;
            }
            sb.Append(char.ToLowerInvariant(ch));
            prevSpace = false;
        }
        // Хвостовой пробел.
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// «0 - Нет»: фиксированный маркер «выключено» в файле заказчика.
    /// Раньше проверяли только <c>StartsWith("0")</c> — это съедало числовые
    /// значения вроде «0,05» (Rate=5%) и «0,11» (Rate=11%), и активные ставки
    /// LM40/LM50/LM60 пропадали с пометкой «выключено». Сейчас матчим строго
    /// «0…Нет» (в обоих регистрах, с любыми разделителями между).
    /// </summary>
    private static bool IsYesNoNo(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return false;
        var s = cell.Trim();
        if (!s.StartsWith("0", StringComparison.Ordinal)) return false;
        return s.Contains("Нет", StringComparison.OrdinalIgnoreCase);
    }

    // ─── Оркестратор шага процентных ставок ───────────────────────────────

    /// <summary>
    /// Создаёт <c>dealpercentbet</c>-записи в Visary по 4 ставкам «Этапа 1»
    /// (LM10/LM20/LM30/LM40). Вызывается из <see cref="EnsureProjectAuditAndInstallmentsAsync"/>
    /// ДО POST <c>projectaudit</c> (заказчик: «перед тем, как создавать заключение»).
    /// <para/>
    /// Все ошибки этого шага трактуются как row-error + skip — создание Заключения
    /// продолжается. Если в файле нет «Номер КД» / нет включённых ставок / сделка
    /// в чужом проекте / по КД найдено несколько сделок — пишем info или row-error
    /// в отчёт и тихо выходим. См. doc 139 v1.4.
    /// <para/>
    /// Возвращает <c>Deal.ID</c> найденной сделки, если она однозначно определилась
    /// (в нашем проекте) — нужен для doc 142 (<c>dealmonthlydata</c>), где
    /// шаг помесячных данных переиспользует ту же сделку. <c>null</c> в любом
    /// случае, когда дальше шаг помесячных данных смысла не имеет.
    /// </summary>
    private async Task<int?> EnsureDealPercentBetsAsync(
        int projectId,
        int siteId,
        string? primaryFilePath,
        List<RowError> errors,
        SyntheticRowEmitter synthetic,
        CancellationToken ct)
    {
        // Caller гарантирует non-empty (см. ранний return в
        // EnsureProjectAuditAndInstallmentsAsync), но защищаемся явно: defensive.
        if (string.IsNullOrWhiteSpace(primaryFilePath))
            return null;

        // 1) Парсим раздел «Финансирование» (+ КД из «Результатов»). Файл уже
        //    открывался выше — берём заново, чтобы не держать stream между шагами.
        FinancingData fin;
        try
        {
            await using var stream = await _fileStorage.OpenReadAsync(primaryFilePath, ct);
            fin = ReadFinancingData(stream);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "FinModelImportMapper.Rates: ошибка парсинга «Финансирование» (siteId={SiteId})", siteId);
            errors.Add(new RowError(null, "rates_parse_error",
                "Не удалось прочитать разделы «Результаты»/«Финансирование» листа Control: " +
                ex.Message + ". Процентные ставки сделки не созданы."));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                ["Процентные ставки: ошибка парсинга — " + ex.Message]);
            return null;
        }

        if (string.IsNullOrWhiteSpace(fin.KdNumber))
        {
            _log.LogInformation(
                "FinModelImportMapper.Rates: «Номер КД» отсутствует — ставки сделки не создаются (siteId={SiteId})",
                siteId);
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                ["Процентные ставки: «Номер КД» не указан в разделе «Результаты» — пропуск"]);
            return null;
        }

        // 2) Глобальный listview по DocNumber. Точное совпадение мы фильтруем
        //    сами (Visary contains-семантика по DocNumber выдаёт лишнее).
        ListViewResponse<DealRaw> deals;
        try
        {
            deals = await _listViewClient.GetDealsAsync(
                lmIdFilter: null, docNumberFilter: fin.KdNumber, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FinModelImportMapper.Rates: deal lookup failed (kd='{KD}', siteId={SiteId})",
                fin.KdNumber, siteId);
            errors.Add(new RowError(null, "rates_deal_lookup_failed",
                $"Не удалось найти сделку по «Номер КД»=«{fin.KdNumber}» в Visary: " +
                ex.Message + ". Процентные ставки не созданы."));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                [$"Процентные ставки: ошибка поиска сделки по КД «{fin.KdNumber}» — {ex.Message}"]);
            return null;
        }

        // Сравнение DocNumber — устойчивое: схлопываем всю whitespace (включая
        // NBSP/таб) в один пробел и приводим к lower. Заказчик: «В ячейке
        // написан реальный номер договора, он может содержать буквы, цифры и
        // символы» — strict Equals после .Trim() слишком жёсткий, ломается на
        // невидимых различиях (Excel ↔ Visary).
        var normalizedKd = NormalizeDocNumber(fin.KdNumber);
        var allDeals = deals.Data ?? new List<DealRaw>();
        var matches = allDeals
            .Where(d => string.Equals(NormalizeDocNumber(d.DocNumber), normalizedKd, StringComparison.Ordinal))
            .ToList();

        _log.LogInformation(
            "FinModelImportMapper.Rates: deal lookup kd='{KD}' (norm='{Norm}') → Visary вернул {Total} сделок, " +
            "точное совпадение {Match}. Сэмпл DocNumber: {Sample}",
            fin.KdNumber, normalizedKd, allDeals.Count, matches.Count,
            string.Join(" | ", allDeals.Take(5).Select(d => $"'{d.DocNumber}'")));

        if (matches.Count == 0)
        {
            errors.Add(new RowError(null, "rates_deal_not_found",
                $"В Visary не найдена сделка по «Номер КД»=«{fin.KdNumber}». " +
                "Процентные ставки не созданы."));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                [$"Процентные ставки: сделка с КД «{fin.KdNumber}» не найдена — пропуск"]);
            return null;
        }

        if (matches.Count > 1)
        {
            errors.Add(new RowError(null, "rates_multiple_deals",
                $"По «Номер КД»=«{fin.KdNumber}» в Visary найдено несколько сделок ({matches.Count}). " +
                "Однозначно выбрать сделку для записи процентных ставок невозможно — создание пропущено."));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                [$"Процентные ставки: по КД «{fin.KdNumber}» найдено сделок — {matches.Count}, пропуск"]);
            return null;
        }

        var deal = matches[0];
        if (deal.ConstructionProject?.ID is null || deal.ConstructionProject.ID != projectId)
        {
            var otherProj = deal.ConstructionProject?.Title is { } t && !string.IsNullOrWhiteSpace(t)
                ? $"«{t}»"
                : (deal.ConstructionProject?.ID is { } pid ? $"ID={pid}" : "другим проектом");
            errors.Add(new RowError(null, "rates_deal_in_other_project",
                $"Сделка по «Номер КД»=«{fin.KdNumber}» относится к другому проекту ({otherProj}). " +
                "Процентные ставки не созданы; создание Заключения продолжается."));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                [$"Процентные ставки: сделка по КД «{fin.KdNumber}» в проекте {otherProj} — пропуск"]);
            return null;
        }

        if (fin.Rates.Count == 0)
        {
            _log.LogInformation(
                "FinModelImportMapper.Rates: в файле нет включённых ставок Этапа 1 (siteId={SiteId})", siteId);
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                ["Процентные ставки: в файле нет ставок Этапа 1 (все «0 - Нет» или раздел отсутствует) — пропуск"]);
            // Сделка валидна — возвращаем её, чтобы шаг dealmonthlydata мог создать
            // запись даже без процентных ставок.
            return deal.ID;
        }

        // 3) LmID должен быть УНИКАЛЕН для каждой ставки — Visary держит
        //    UNIQUE-индекс `UX_DealPercentBet_LmID`, второй POST с тем же LmID
        //    валится 422 «duplicate key value». Поэтому генерим LmID per-rate:
        //    `dd-MM-yyyy-HH-mm-ss-fff-{Code}-{idx}` — timestamp с миллисекундами +
        //    код ставки + индекс sub-строки. Это закрывает три источника
        //    коллизий: (а) две ставки в один POST-цикл, (б) две sub-строки под
        //    одним родителем (LM10 sub1 + LM10 sub2 → один Code), (в) повторный
        //    импорт того же файла в ту же миллисекунду.
        //    Формат заказчика "dd-MM-yyyy-HH-mm-ss" расширен — UNIQUE-индекс
        //    важнее точного длинного варианта.
        var lmIdBase = DateTime.Now.ToString("dd-MM-yyyy-HH-mm-ss-fff", CultureInfo.InvariantCulture);
        var rateIdx = 0;

        foreach (var rate in fin.Rates)
        {
            ct.ThrowIfCancellationRequested();
            rateIdx++;
            var lmId = $"{lmIdBase}-{rate.Code}-{rateIdx}";

            ListViewResponse<PercentBetTypeRaw> betTypes;
            try
            {
                betTypes = await _listViewClient.FindPercentBetTypeByCodeAsync(rate.Code, ct);
            }
            catch (Exception ex)
            {
                errors.Add(new RowError(null, "rates_bettype_lookup_failed",
                    $"Не удалось получить тип ставки по коду «{rate.Code}» из Visary: {ex.Message}."));
                synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                    [$"Ставка [{rate.Code}]: ошибка получения типа из справочника — {ex.Message}"]);
                continue;
            }

            var betType = (betTypes.Data ?? new List<PercentBetTypeRaw>())
                .FirstOrDefault(b => string.Equals(b.Code, rate.Code, StringComparison.Ordinal));
            if (betType is null || betType.ID <= 0)
            {
                errors.Add(new RowError(null, "rates_bettype_not_found",
                    $"В справочнике «Тип процентной ставки» Visary не найден код «{rate.Code}». Ставка не создана."));
                synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                    [$"Ставка [{rate.Code}]: тип ставки в справочнике Visary не найден"]);
                continue;
            }

            try
            {
                // doc 144 v1.1: Rate отправляем по прежней логике (как в v1.4 doc 139).
                // PercentKind НЕ отправляем — «Вид ставки» (Floating/Fixed) Visary
                // определяет сам по PercentBetType, импорт не должен его проставлять.
                var created = await _visaryClient.CreateDealPercentBetAsync(new DealPercentBetCreateRequest
                {
                    DealID = deal.ID,
                    Deal = new VisaryRef { ID = deal.ID },
                    LmID = lmId,
                    Rate = rate.Rate,
                    PercentBetType = new VisaryRef { ID = betType.ID, Title = betType.Title },
                }, ct);
                var labelSuffix = string.IsNullOrEmpty(rate.Label)
                    ? string.Empty
                    : $", «{rate.Label}»";
                synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                    [$"Ставка [{rate.Code}]: создана (id={created.ID}, {rate.Rate:0.##}%, тип «{betType.Title}»{labelSuffix})"]);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "FinModelImportMapper.Rates: ошибка создания dealpercentbet (code={Code}, dealId={DealId}, siteId={SiteId})",
                    rate.Code, deal.ID, siteId);
                errors.Add(new RowError(null, "rates_create_failed",
                    $"Не удалось создать процентную ставку «{rate.Code}» в Visary: {ex.Message}."));
                synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                    [$"Ставка [{rate.Code}]: ошибка создания — {ex.Message}"]);
            }
        }
        return deal.ID;
    }

    // ─── Парсер Outputs «Инвестиционный кредит: Этап 1» (doc 142) ─────────

    // Якоря раздела помесячных данных на листе Outputs.
    // На листе Outputs у заказчика 8 разных строк содержат «Инвестиционный
    // кредит» (в др. банке × 4, Этапы, Этап 1, Этап 2, Этап 3), и под каждой
    // повторяется одинаковый набор лейблов («Привлечение ОД» и т.д.). Нам
    // нужен именно «Этап 1» — раньше якорем был просто «Инвестиционный
    // кредит» и парсер цеплялся за первый же «в др. банке», где всё 0/—,
    // отсюда был skip с нулями. Сейчас якорь — совпадение обоих токенов
    // («Инвестиционный кредит» И «Этап 1») в одной ячейке (см. doc 142).
    private const string InvestmentCreditMarker      = "Инвестиционный кредит";
    private const string InvestmentCreditStage1Token = "Этап 1";
    private const string OutputsFactColumnMarker     = "Факт";

    /// <summary>
    /// Результат парсинга раздела «Инвестиционный кредит: Этап 1» листа Outputs.
    /// Все 5 полей хранятся в РУБЛЯХ — единица измерения строки (тыс./млн руб.)
    /// уже учтена в парсере. Если в файле нет листа Outputs / нет колонки «Факт» /
    /// нет якоря раздела — все поля = 0 (см. <see cref="HasAnyValue"/>).
    /// </summary>
    internal sealed record InvestmentCreditMonthlyData(
        double PrincipalDebtAmount,
        double SimpleInterestAmount,
        double CapitalizedInterestAmount,
        double PrincipalRepaymentAmount,
        double InterestRepaymentAmount)
    {
        public bool HasAnyValue()
            => Math.Abs(PrincipalDebtAmount) > 1e-9d
            || Math.Abs(SimpleInterestAmount) > 1e-9d
            || Math.Abs(CapitalizedInterestAmount) > 1e-9d
            || Math.Abs(PrincipalRepaymentAmount) > 1e-9d
            || Math.Abs(InterestRepaymentAmount) > 1e-9d;
    }

    // Маппинг русских лейблов на поля DTO (порядок важен только для отчёта).
    private static readonly (string Label, string Field)[] InvestmentCreditFieldMap =
    [
        ("Привлечение ОД",                       "PrincipalDebtAmount"),
        ("Проценты начисленные",                 "SimpleInterestAmount"),
        ("Расчет процентов по капитализации",    "CapitalizedInterestAmount"),
        ("Погашение тела долга",                 "PrincipalRepaymentAmount"),
        ("Погашение процентных выплат",          "InterestRepaymentAmount"),
    ];

    /// <summary>
    /// Парсит раздел Outputs «Инвестиционный кредит: Этап 1». Алгоритм:
    /// <list type="number">
    ///   <item>Находим колонку «Факт» — точно та же логика, что у Fact-блока
    ///         InputData (см. doc 126): сканируем лист, берём ПЕРВОЕ совпадение
    ///         «Факт» (raw или formatted, custom-format `[=0]"Факт";…`).</item>
    ///   <item>Находим строку-якорь «Инвестиционный кредит» (contains-матч,
    ///         чтобы поймать «Инвестиционный кредит: Этап 1» / без двоеточия).</item>
    ///   <item>В окне (anchor+1 .. anchor+30) ищем по каждому из 5 лейблов
    ///         строку (contains-матч), читаем единицу измерения из колонок между
    ///         label и Fact (находим ячейку с «руб»), значение из Fact-колонки,
    ///         умножаем на multiplier (тыс=×1 000, млн=×1 000 000, руб=×1).</item>
    /// </list>
    /// Прочерк («-/—/–/−»), пустая ячейка, 0 — все эквивалентны нулю.
    /// </summary>
    internal static InvestmentCreditMonthlyData ReadInvestmentCreditMonthlyData(Stream stream)
        => ReadInvestmentCreditMonthlyDataWithDiagnostics(stream).Data;

    internal static (InvestmentCreditMonthlyData Data, InvestmentCreditDiagnostics Diag)
        ReadInvestmentCreditMonthlyDataWithDiagnostics(Stream stream)
    {
        byte[] bytes;
        using (var src = new MemoryStream())
        {
            stream.CopyTo(src);
            bytes = src.ToArray();
        }
        try
        {
            return ReadInvestmentCreditMonthlyDataFromBytes(bytes);
        }
        catch (Exception ex) when (XlsxParser.IsExternalLinkError(ex))
        {
            // Шаблоны заказчика содержат external-link формулы (см. doc 81/126);
            // ClosedXML на них кидает, но cached <v>-значения остаются — чистим
            // zip и читаем повторно.
            var cleaned = XlsxParser.StripExternalLinks(bytes);
            return ReadInvestmentCreditMonthlyDataFromBytes(cleaned);
        }
    }

    private static (InvestmentCreditMonthlyData Data, InvestmentCreditDiagnostics Diag)
        ReadInvestmentCreditMonthlyDataFromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var wb = new XLWorkbook(ms);
        var sheet = wb.Worksheets.FirstOrDefault(ws =>
            string.Equals(ws.Name?.Trim(), "Outputs", StringComparison.OrdinalIgnoreCase));
        if (sheet is null)
        {
            return (new InvestmentCreditMonthlyData(0, 0, 0, 0, 0),
                new InvestmentCreditDiagnostics(-1, -1, -1, Array.Empty<string>(), Array.Empty<string>()));
        }
        return ReadInvestmentCreditMonthlyDataFromSheetWithDiagnostics(sheet);
    }

    internal static InvestmentCreditMonthlyData ReadInvestmentCreditMonthlyDataFromSheet(IXLWorksheet sheet)
        => ReadInvestmentCreditMonthlyDataFromSheetWithDiagnostics(sheet).Data;

    /// <summary>
    /// Diagnostic-вариант парсера: возвращает значения + срез того, что увидел
    /// парсер (factRow/factCol/anchorRow + первые лейблы окна). Используется
    /// для логирования из <see cref="EnsureDealMonthlyDataAsync"/>, когда
    /// Visary отвечает 422 на нулевые значения — нужно различить «парсер не
    /// нашёл колонку/якорь» от «в Excel реально пусто».
    /// </summary>
    internal static (InvestmentCreditMonthlyData Data, InvestmentCreditDiagnostics Diag)
        ReadInvestmentCreditMonthlyDataFromSheetWithDiagnostics(IXLWorksheet sheet)
    {
        var (factRow, factCol) = FindOutputsFactCell(sheet);
        if (factRow < 0 || factCol <= 0)
        {
            return (new InvestmentCreditMonthlyData(0, 0, 0, 0, 0),
                new InvestmentCreditDiagnostics(-1, -1, -1, Array.Empty<string>(), Array.Empty<string>()));
        }

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? factRow + 200;
        var lastCol = Math.Min(50, sheet.LastColumnUsed()?.ColumnNumber() ?? factCol + 5);

        // Якорь раздела — ОДНОВРЕМЕННОЕ совпадение «Инвестиционный кредит» и
        // «Этап 1» в одной ячейке. На листе Outputs существуют параллельные
        // секции «в др. банке», «Этапы», «Этап 2», «Этап 3» — все они
        // содержат подстроку «Инвестиционный кредит», но не «Этап 1».
        // Дополнительно отсекаем «Этап 10..19», чтобы не словить будущие
        // секции с двузначным номером (Contains «Этап 1» матчит и «Этап 11»).
        var anchorRow = -1;
        for (var r = factRow + 1; r <= lastRow && anchorRow < 0; r++)
        {
            for (var c = 1; c <= Math.Min(lastCol, factCol); c++)
            {
                var t = sheet.Cell(r, c).GetString().Trim();
                if (t.Length == 0) continue;
                if (!t.Contains(InvestmentCreditMarker, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsStage1Anchor(t)) continue;
                anchorRow = r; break;
            }
        }
        if (anchorRow < 0)
        {
            return (new InvestmentCreditMonthlyData(0, 0, 0, 0, 0),
                new InvestmentCreditDiagnostics(factRow, factCol, -1, Array.Empty<string>(), Array.Empty<string>()));
        }

        // Сканируем окно под якорем. 30 строк — запас под все 5 полей + промежуточные.
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var matchedLabels = new List<string>();
        var seenLabels = new List<string>();
        var windowEnd = Math.Min(lastRow, anchorRow + 30);
        for (var r = anchorRow + 1; r <= windowEnd; r++)
        {
            var label = string.Empty;
            for (var c = 1; c < factCol; c++)
            {
                var t = sheet.Cell(r, c).GetString().Trim();
                if (t.Length == 0) continue;
                label = t;
                break;
            }
            if (label.Length == 0) continue;
            seenLabels.Add(label);

            // Сматчить с одним из 5 целевых лейблов.
            string? matchedField = null;
            foreach (var (target, field) in InvestmentCreditFieldMap)
            {
                if (label.Contains(target, StringComparison.OrdinalIgnoreCase))
                { matchedField = field; break; }
            }
            if (matchedField is null) continue;
            if (values.ContainsKey(matchedField)) continue; // первое попадание

            // Единица измерения — в любой колонке между label и Fact, ищем ячейку с «руб».
            var unitText = string.Empty;
            for (var c = 1; c < factCol; c++)
            {
                var t = sheet.Cell(r, c).GetString().Trim();
                if (t.Length == 0) continue;
                if (t.Contains("руб", StringComparison.OrdinalIgnoreCase))
                { unitText = t; break; }
            }
            var multiplier = GetUnitMultiplier(unitText);

            // Значение из колонки «Факт». Пусто/прочерк/0 → 0.
            var rawValue = TryReadFactNumber(sheet, r, factCol);
            var value = rawValue * multiplier;
            values[matchedField] = value;
            matchedLabels.Add($"{matchedField}={value:0.##} (raw={rawValue:0.##}×{multiplier:0} «{label}» / «{unitText}»)");
        }

        var data = new InvestmentCreditMonthlyData(
            PrincipalDebtAmount:        values.GetValueOrDefault("PrincipalDebtAmount", 0),
            SimpleInterestAmount:       values.GetValueOrDefault("SimpleInterestAmount", 0),
            CapitalizedInterestAmount:  values.GetValueOrDefault("CapitalizedInterestAmount", 0),
            PrincipalRepaymentAmount:   values.GetValueOrDefault("PrincipalRepaymentAmount", 0),
            InterestRepaymentAmount:    values.GetValueOrDefault("InterestRepaymentAmount", 0));
        return (data, new InvestmentCreditDiagnostics(factRow, factCol, anchorRow, matchedLabels, seenLabels));
    }

    internal sealed record InvestmentCreditDiagnostics(
        int FactRow,
        int FactCol,
        int AnchorRow,
        IReadOnlyList<string> MatchedLabels,
        IReadOnlyList<string> SeenLabelsInWindow);

    /// <summary>
    /// Возвращает true, если в строке встречается «Этап 1» как самостоятельный
    /// маркер этапа (а не префикс «Этап 10/11/12…»). Используется, чтобы
    /// отличить нужный раздел «Инвестиционный кредит: Этап 1» от
    /// «Инвестиционный кредит: Этап 2/3», «Этапы», «в др. банке».
    /// </summary>
    internal static bool IsStage1Anchor(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var idx = text.IndexOf(InvestmentCreditStage1Token, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var endIdx = idx + InvestmentCreditStage1Token.Length;
            // Следующий символ не должен быть цифрой — иначе это «Этап 10..».
            if (endIdx >= text.Length || !char.IsDigit(text[endIdx])) return true;
            idx = text.IndexOf(InvestmentCreditStage1Token, endIdx, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// Возвращает множитель для строкового представления единицы измерения.
    /// «млн руб.»/«млн. руб»/«млн.руб.» → 1 000 000, «тыс. руб.» → 1 000,
    /// «руб»/любая другая строка с руб → 1.
    /// Совпадает с правилом заказчика из doc 142.
    /// </summary>
    internal static double GetUnitMultiplier(string? unitText)
    {
        if (string.IsNullOrWhiteSpace(unitText)) return 1d;
        var s = unitText.ToLowerInvariant();
        if (s.Contains("млн", StringComparison.Ordinal))  return 1_000_000d;
        if (s.Contains("тыс", StringComparison.Ordinal))  return 1_000d;
        return 1d;
    }

    /// <summary>
    /// Чтение числовой ячейки «Факта». Пусто/прочерк/нечислоавя строка → 0
    /// (заказчик: «Если значение будет 0, пусто или прочерк — указываем 0»).
    /// </summary>
    private static double TryReadFactNumber(IXLWorksheet sheet, int row, int col)
    {
        var cell = sheet.Cell(row, col);
        if (cell.IsEmpty()) return 0d;
        if (cell.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
            return d;
        var text = cell.GetString().Trim();
        if (string.IsNullOrWhiteSpace(text)) return 0d;
        if (text is "-" or "—" or "–" or "−") return 0d;
        text = text.Replace(',', '.').Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0d;
    }

    /// <summary>
    /// Находит на листе Outputs ПЕРВУЮ ячейку «Факт» (raw или formatted: у
    /// заказчика часто стоит custom number format `[=0]"Факт";[<>0]"Прогноз"`
    /// на числовой ячейке). Возвращает (-1, -1) если не нашли. Логика
    /// совпадает с <see cref="ReadOutputsFactDataFromBytes"/> в основном классе.
    /// </summary>
    private static (int Row, int Col) FindOutputsFactCell(IXLWorksheet sheet)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (var r = 1; r <= lastRow; r++)
        {
            for (var c = 1; c <= lastCol; c++)
            {
                var cell = sheet.Cell(r, c);
                if (cell.IsEmpty()) continue;
                var raw = cell.GetString().Trim();
                if (string.Equals(raw, OutputsFactColumnMarker, StringComparison.OrdinalIgnoreCase))
                    return (r, c);
                if (cell.DataType == XLDataType.Number || cell.DataType == XLDataType.Boolean)
                {
                    var formatted = cell.GetFormattedString().Trim();
                    if (string.Equals(formatted, OutputsFactColumnMarker, StringComparison.OrdinalIgnoreCase))
                        return (r, c);
                }
            }
        }
        return (-1, -1);
    }

    // ─── Оркестратор шага помесячных данных (doc 142) ─────────────────────

    /// <summary>
    /// Создаёт ОДНУ запись <c>dealmonthlydata</c> в Visary на (Deal, Текущий
    /// год, Текущий месяц) по данным раздела «Инвестиционный кредит: Этап 1»
    /// листа Outputs. Вызывается из <see cref="EnsureProjectAuditAndInstallmentsAsync"/>
    /// сразу после <see cref="EnsureDealPercentBetsAsync"/> (заказчик: «Deal
    /// — указывается сделка, которая найдена для создания Ставок»).
    /// <para/>
    /// Все ошибки шага трактуются как row-error + skip — Заключение и
    /// рассрочки создаются как обычно.
    /// </summary>
    private async Task EnsureDealMonthlyDataAsync(
        int dealId,
        int siteId,
        string? primaryFilePath,
        List<RowError> errors,
        SyntheticRowEmitter synthetic,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(primaryFilePath))
            return;

        // 1) Парсим раздел Outputs «Инвестиционный кредит: Этап 1».
        InvestmentCreditMonthlyData parsed;
        InvestmentCreditDiagnostics diag;
        try
        {
            await using var stream = await _fileStorage.OpenReadAsync(primaryFilePath, ct);
            (parsed, diag) = ReadInvestmentCreditMonthlyDataWithDiagnostics(stream);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "FinModelImportMapper.MonthlyData: ошибка парсинга «Инвестиционный кредит» (siteId={SiteId})", siteId);
            errors.Add(new RowError(null, "dealmonthlydata_parse_error",
                "Не удалось прочитать раздел «Инвестиционный кредит: Этап 1» листа Outputs: " +
                ex.Message + ". Помесячные данные по сделке не созданы."));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                ["Помесячные данные: ошибка парсинга — " + ex.Message]);
            return;
        }

        // Диагностика: явно логируем, что прочитал парсер, чтобы при 422 от
        // Visary можно было различить «парсер не нашёл значение» и «в Excel
        // действительно 0/пусто/прочерк». Diag показывает, где парсер нашёл
        // «Факт»-колонку, якорь «Инвестиционный кредит», и какие лейблы
        // увидел в окне 30 строк под якорем.
        _log.LogInformation(
            "FinModelImportMapper.MonthlyData: parsed values (dealId={DealId} siteId={SiteId}) " +
            "PrincipalDebtAmount={PDA} SimpleInterestAmount={SIA} CapitalizedInterestAmount={CIA} " +
            "PrincipalRepaymentAmount={PRA} InterestRepaymentAmount={IRA}",
            dealId, siteId,
            parsed.PrincipalDebtAmount, parsed.SimpleInterestAmount, parsed.CapitalizedInterestAmount,
            parsed.PrincipalRepaymentAmount, parsed.InterestRepaymentAmount);
        _log.LogInformation(
            "FinModelImportMapper.MonthlyData: parser diagnostics — factRow={FactRow} factCol={FactCol} anchorRow={AnchorRow} " +
            "matched=[{Matched}] seenLabels(first 12)=[{Seen}]",
            diag.FactRow, diag.FactCol, diag.AnchorRow,
            string.Join(" | ", diag.MatchedLabels),
            string.Join(" | ", diag.SeenLabelsInWindow.Take(12)));

        // 2) POST dealmonthlydata. Заказчик: «Year — текущий год, Month —
        //    текущий месяц». Берём локальное время — для интеграции и логов это
        //    привычнее, чем UTC.
        var now = DateTime.Now;

        // Visary валидирует PrincipalDebtAmount как NotEmptyValidator: 0.0 → 422
        // «должно быть заполнено». doc 142 говорит «0/пусто/прочерк→0», но
        // сервер этого не принимает — не плодим бессмысленный POST, если все
        // 5 значений = 0 (парсер не нашёл данных или раздел пуст).
        var allZero =
            parsed.PrincipalDebtAmount == 0d &&
            parsed.SimpleInterestAmount == 0d &&
            parsed.CapitalizedInterestAmount == 0d &&
            parsed.PrincipalRepaymentAmount == 0d &&
            parsed.InterestRepaymentAmount == 0d;
        if (allZero)
        {
            _log.LogInformation(
                "FinModelImportMapper.MonthlyData: пропуск POST — все 5 значений раздела «Инвестиционный кредит: Этап 1» = 0 " +
                "(dealId={DealId}, siteId={SiteId}). Возможные причины: раздел/якорь/Fact-колонка не найдены, либо в Excel пусто/прочерк.",
                dealId, siteId);
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                [$"Помесячные данные ({now.Year}-{now.Month:D2}): пропуск — в разделе «Инвестиционный кредит: Этап 1» все значения 0/пусто/прочерк."]);
            return;
        }

        // Pre-check на дубликат. Серверной идемпотентности у dealmonthlydata
        // нет — повторный импорт того же файла создавал бы новую запись с
        // идентичными значениями (см. отчёт заказчика). Ищем точное совпадение
        // по (Deal, Year, Month + 5 чисел) через listview. Если найдено —
        // skip POST, без ошибки.
        try
        {
            var existing = await _listViewClient.FindDealMonthlyDataAsync(
                dealId, now.Year, now.Month,
                parsed.PrincipalDebtAmount, parsed.SimpleInterestAmount,
                parsed.CapitalizedInterestAmount, parsed.PrincipalRepaymentAmount,
                parsed.InterestRepaymentAmount, ct);
            // Visary listview/dealmonthlydata НЕ уважает наши range-фильтры
            // (>=N AND <=N) на double-полях и возвращает все записи с тем же
            // (Deal, Year, Month). Поэтому строго фильтруем на нашей стороне:
            // точное совпадение по Deal + Year + Month + всем 5 числовым полям.
            var localMatches = (existing?.Data ?? new List<DealMonthlyDataRaw>())
                .Where(d =>
                    d.Deal?.ID == dealId &&
                    d.Year == now.Year &&
                    d.Month == now.Month &&
                    NumberEqualsExact(d.PrincipalDebtAmount,       parsed.PrincipalDebtAmount) &&
                    NumberEqualsExact(d.SimpleInterestAmount,      parsed.SimpleInterestAmount) &&
                    NumberEqualsExact(d.CapitalizedInterestAmount, parsed.CapitalizedInterestAmount) &&
                    NumberEqualsExact(d.PrincipalRepaymentAmount,  parsed.PrincipalRepaymentAmount) &&
                    NumberEqualsExact(d.InterestRepaymentAmount,   parsed.InterestRepaymentAmount))
                .ToList();
            if (localMatches.Count > 0)
            {
                var first = localMatches[0];
                _log.LogInformation(
                    "FinModelImportMapper.MonthlyData: дубликат уже есть в Visary (id={Id}, dealId={DealId}, {Year}-{Month}) — POST пропущен. " +
                    "Visary вернул всего {Total}, точно совпадает {Match}",
                    first.ID, dealId, now.Year, now.Month,
                    existing?.Data?.Count ?? 0, localMatches.Count);
                synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                    [$"Помесячные данные ({now.Year}-{now.Month:D2}): уже существует (id={first.ID}) — пропуск"]);
                return;
            }
            // Иначе строгого совпадения нет — продолжаем POST. Логируем для
            // диагностики, что Visary вернул на наш фильтр.
            if ((existing?.Data?.Count ?? 0) > 0)
            {
                _log.LogInformation(
                    "FinModelImportMapper.MonthlyData: pre-check вернул {Total} «соседних» записей, точного совпадения нет — POST продолжается. " +
                    "Сэмпл: {Sample}",
                    existing!.Data!.Count,
                    string.Join(" | ", existing.Data.Take(3).Select(d =>
                        $"id={d.ID} pda={d.PrincipalDebtAmount} sia={d.SimpleInterestAmount} cia={d.CapitalizedInterestAmount} pra={d.PrincipalRepaymentAmount} ira={d.InterestRepaymentAmount}")));
            }
        }
        catch (Exception ex)
        {
            // Сетевая или серверная ошибка pre-check'а не должна блокировать
            // создание Заключения и остальной маппинг — продолжаем POST, риск
            // дубликата в этом редком кейсе допустим.
            _log.LogWarning(ex,
                "FinModelImportMapper.MonthlyData: pre-check duplicate failed (dealId={DealId}, {Year}-{Month}) — продолжаем POST",
                dealId, now.Year, now.Month);
        }

        try
        {
            var created = await _visaryClient.CreateDealMonthlyDataAsync(new DealMonthlyDataCreateRequest
            {
                Deal = new VisaryRef { ID = dealId },
                Year = now.Year,
                Month = now.Month,
                PrincipalDebtAmount = parsed.PrincipalDebtAmount,
                SimpleInterestAmount = parsed.SimpleInterestAmount,
                CapitalizedInterestAmount = parsed.CapitalizedInterestAmount,
                PrincipalRepaymentAmount = parsed.PrincipalRepaymentAmount,
                InterestRepaymentAmount = parsed.InterestRepaymentAmount,
            }, ct);
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Applied,
                [$"Помесячные данные ({now.Year}-{now.Month:D2}): создана (id={created.ID}, " +
                 $"ОД={parsed.PrincipalDebtAmount:0.##}, %% нач.={parsed.SimpleInterestAmount:0.##}, " +
                 $"%% капит.={parsed.CapitalizedInterestAmount:0.##}, " +
                 $"погаш. ОД={parsed.PrincipalRepaymentAmount:0.##}, погаш. %%={parsed.InterestRepaymentAmount:0.##})"]);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FinModelImportMapper.MonthlyData: ошибка создания dealmonthlydata (dealId={DealId}, siteId={SiteId})",
                dealId, siteId);
            errors.Add(new RowError(null, "dealmonthlydata_create_failed",
                $"Не удалось создать помесячные данные по сделке (id={dealId}): {ex.Message}."));
            synthetic.Emit(SyntheticSheetInstallments, StagedRowStatus.Failed,
                [$"Помесячные данные ({now.Year}-{now.Month:D2}): ошибка создания — {ex.Message}"]);
        }
    }

    // ─── Низкоуровневые помощники чтения Excel ────────────────────────────

    // Колонки, к которым обращаемся часто. ClosedXML использует 1-based индексы.
    private const int BCol = 2;
    private const int CCol = 3;
    private const int ECol = 5;

    /// <summary>
    /// Возвращает row, в которой в одной из колонок D/E/F (3..7) встречается
    /// «Этап 1». Запоминает номер колонки. -1 если шапки нет.
    /// </summary>
    private static int FindStageHeaderRow(IXLWorksheet sheet, out int stageColumn)
    {
        stageColumn = 0;
        var firstRow = sheet.FirstRowUsed()?.RowNumber() ?? 1;
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? firstRow;
        // По эталону шапка лежит в строке 23, но устойчиво сканируем
        // первые ~60 строк (заголовки/блоки могут смещаться между шаблонами).
        var cap = Math.Min(lastRow, firstRow + 60);
        for (var r = firstRow; r <= cap; r++)
        {
            for (var c = 3; c <= 7; c++)
            {
                var text = ReadCellTextTrimmed(sheet, r, c);
                if (string.Equals(text, "Этап 1", StringComparison.OrdinalIgnoreCase))
                {
                    stageColumn = c;
                    return r;
                }
            }
        }
        return -1;
    }

    private static int FindRowByCellExact(
        IXLWorksheet sheet, string columnLetter, string search,
        int startRow, int maxRows)
    {
        var col = ColumnLetterToIndex(columnLetter);
        var endRow = startRow + maxRows;
        for (var r = startRow; r <= endRow; r++)
        {
            var text = ReadCellTextTrimmed(sheet, r, col);
            if (string.Equals(text, search, StringComparison.OrdinalIgnoreCase))
                return r;
        }
        return -1;
    }

    /// <summary>
    /// Найти строку в диапазоне колонок <c>[firstCol..lastCol]</c>, в которой
    /// хоть одна ячейка содержит подстроку <paramref name="search"/>. Возвращает
    /// номер строки или -1. Используется для anchor-поиска заголовков, точное
    /// расположение которых неизвестно (например, «Конфигурация этапов» может
    /// лежать в A или B в зависимости от шаблона).
    /// </summary>
    private static int FindAnyColumnRowContains(
        IXLWorksheet sheet, string search,
        int startRow, int endRow, int firstCol, int lastCol)
    {
        for (var r = startRow; r <= endRow; r++)
        {
            for (var c = firstCol; c <= lastCol; c++)
            {
                var text = ReadCellTextTrimmed(sheet, r, c);
                if (!string.IsNullOrEmpty(text)
                    && text.Contains(search, StringComparison.OrdinalIgnoreCase))
                    return r;
            }
        }
        return -1;
    }

    private static string ReadCellTextTrimmed(IXLWorksheet sheet, int row, int col)
    {
        if (row <= 0 || col <= 0) return string.Empty;
        var cell = sheet.Cell(row, col);
        if (cell.IsEmpty()) return string.Empty;
        // GetFormattedString — корректно работает с custom number format'ами
        // («1 - Да» / «0 - Нет» / «—» и т.д.), включая текстовое представление
        // числовых ячеек. Trim сразу — у заказчика тонна ведущих пробелов.
        return cell.GetFormattedString().Trim();
    }

    /// <summary>
    /// Парсит ячейку с процентом. Принимает форматы «50,0%»/«0,5»/«50%»/«50»/«0.5».
    /// Возвращает значение в процентах (50% → 50), как принято в Visary
    /// (см. HAR: <c>DDUSteadyOwnShare:30</c> = 30%).
    /// </summary>
    internal static double? TryReadPercentCell(IXLWorksheet sheet, int row, int col)
    {
        if (row <= 0 || col <= 0) return null;
        var cell = sheet.Cell(row, col);
        if (cell.IsEmpty()) return null;

        // 1. Если ячейка — число (типизированное), Excel хранит проценты как доли
        //    (50% → 0.5). Признак: cell.Style.NumberFormat.Format содержит «%»
        //    ИЛИ FormatId соответствует built-in процентному. Дополнительная
        //    эвристика: если значение в [0..1] — считаем его долей и переводим
        //    в проценты. В файле «Параметры» Доля отсрочек/СУ всегда хранится
        //    долей; легитимная вероятность того, что пользователь введёт
        //    «50» вместо «50%» по нашим параметрам незначительна (все доли —
        //    проценты от 0 до 100, а число > 1 в этой ячейке семантически
        //    допустимо только как уже-в-процентах: 30 = 30%).
        if (cell.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            var fmt = cell.Style.NumberFormat.Format ?? string.Empty;
            var fmtId = cell.Style.NumberFormat.NumberFormatId;
            // built-in 9 = "0%", 10 = "0.00%" — этого достаточно для эталонного файла.
            if (fmt.Contains('%') || fmtId == 9 || fmtId == 10)
                return d * 100d;
            if (d >= 0d && d <= 1d)
                return d * 100d;
            return d;
        }

        // 2. Текстовый fallback: парсим строку «50,0%» / «0,5» / «50%».
        var text = cell.GetFormattedString().Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hasPercent = text.Contains('%');
        text = text.Replace(",", ".", StringComparison.Ordinal).Replace("%", string.Empty);
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            return hasPercent ? v : v;
        return null;
    }

    private static int ColumnLetterToIndex(string letter)
    {
        if (string.IsNullOrEmpty(letter)) return 0;
        int idx = 0;
        foreach (var ch in letter.ToUpperInvariant())
        {
            if (ch is < 'A' or > 'Z') return 0;
            idx = idx * 26 + (ch - 'A' + 1);
        }
        return idx;
    }

    private static bool IsYesNoYes(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return false;
        var s = cell.Trim();
        if (s.StartsWith("1", StringComparison.Ordinal)) return true; // «1 - Да»
        return false;
    }

    /// <summary>
    /// Идентифицирует 422-конфликт уникальности <c>(DataSetForFMID, RoomKindID)</c>
    /// — единственный известный «нормальный» провал CREATE dataforfm. Источник
    /// сигнала: текст исключения, формируемый <c>HandleErrorAsync</c> — он содержит
    /// номер статуса и тело ответа сервера. Проверяем оба признака: статус 422
    /// и упоминание PG-ограничения / русского заголовка ошибки.
    /// </summary>
    private static bool IsDuplicateDataForFmConflict(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        if (!msg.Contains("422", StringComparison.Ordinal))
            return false;
        return msg.Contains("UX_DataForFM_DataSetForFMID_RoomKindID", StringComparison.Ordinal)
            || msg.Contains("Тип помещения", StringComparison.Ordinal)
            || msg.Contains("\\u0022Тип помещения\\u0022", StringComparison.Ordinal);
    }

    private static bool IsAnyOtherSchemeAnchor(string label, string currentMarker)
    {
        foreach (var s in InstallmentSchemes)
        {
            if (string.Equals(s.Marker, currentMarker, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(label, s.Marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool StartsWith(string label, string prefix)
        => label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Ошибки парсинга блока «Продажи». Обрабатываются caller-ом как row-error
/// верхнего уровня (см. EnsureProjectAuditAndInstallmentsAsync).
/// </summary>
internal sealed class FinModelInstallmentsParseException : Exception
{
    public FinModelInstallmentsParseException(string message) : base(message) { }
    public FinModelInstallmentsParseException(string message, Exception inner) : base(message, inner) { }
}
