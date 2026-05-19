namespace KiloImportService.Api.Domain.Importing;

/// <summary>
/// Подсказка парсеру о структуре файла. Каждый <see cref="Mapping.IImportMapper"/>
/// объявляет свой layout, и пайплайн пробрасывает его в <see cref="IFileParser"/>.
/// </summary>
public abstract record FileLayoutHint
{
    public static FileLayoutHint Default { get; } = new Tabular();
}

/// <summary>
/// Стандартная табличная раскладка: одна строка — заголовки, каждая следующая — запись.
///
/// По умолчанию заголовки берутся из первой строки <c>RangeUsed</c>. Если в файле над
/// «настоящей» шапкой есть подзаголовки (например, «Реестр вывода КВАРТИР», коэффициенты
/// Кб/Кл, сводные строки) — задайте <paramref name="HeaderAnchors"/>: парсер просканирует
/// первые ~30 строк и выберет ту, где встретилось максимум совпадений с анкорами
/// (case-insensitive по trim-ячейки). Это спасает от файлов вроде «Ежевика короткая 1.xlsx»,
/// в которых шапка живёт в строке 5, а в строках 1–4 — служебные ячейки.
/// </summary>
/// <param name="HeaderAnchors">
/// Список «опорных» названий колонок. Если задан и непуст — парсер ищет среди первых
/// 30 строк диапазона ту, где встретилось ≥2 анкоров (case-insensitive), и принимает её
/// за строку заголовков. Если не задан или не нашлось — поведение legacy (первая строка).
/// </param>
public sealed record Tabular(IReadOnlyList<string>? HeaderAnchors = null) : FileLayoutHint;

/// <summary>
/// Вертикальная key-value раскладка (параметры в столбик): один параметр = одна строка,
/// в <paramref name="KeyColumn"/> — название параметра, начиная с <paramref name="ValueStartColumn"/>
/// — одна или несколько колонок-значений (например, по этапам).
///
/// Парсер выпускает по одному <see cref="ParsedRow"/> на каждую колонку-этап со словарём
/// <c>{ название_параметра → значение_в_этой_колонке }</c>. Маппер видит каждую стадию
/// как отдельную логическую строку и валидирует независимо.
///
/// Если задан <paramref name="StageCount"/>, парсер читает ровно N колонок (N — число
/// этапов из управляющей ячейки). Без него читаются все непустые колонки до конца
/// использованного диапазона (legacy-поведение).
/// </summary>
/// <param name="SheetName">Имя листа (case-insensitive). Если не найден — file-level ошибка.</param>
/// <param name="KeyColumn">Буква колонки с названиями параметров (например, <c>"C"</c>).</param>
/// <param name="ValueStartColumn">Буква первой колонки-значения (например, <c>"H"</c>).</param>
/// <param name="StageCount">Откуда взять количество этапов (см. <see cref="StageCountReference"/>); <c>null</c> — без ограничения.</param>
/// <param name="Budget">Если задано — парсер дополнительно извлекает «бюджетную» секцию листа (главы/подстатьи) и эмитит её строки с <see cref="BudgetSectionHint.SheetMarker"/>-суффиксом в <c>Sheet</c>. <c>null</c> — секция бюджета не нужна.</param>
public sealed record KeyValueVertical(
    string SheetName,
    string KeyColumn,
    string ValueStartColumn,
    StageCountReference? StageCount = null,
    BudgetSectionHint? Budget = null,
    ChapterScheduleHint? ChapterSchedule = null) : FileLayoutHint;

/// <summary>
/// Ссылка на ячейку с количеством этапов: на листе <paramref name="SheetName"/> в столбце
/// <paramref name="KeyColumn"/> ищется строка со значением <paramref name="ParameterName"/>;
/// число этапов берётся из <paramref name="ValueColumn"/> той же строки.
/// </summary>
public sealed record StageCountReference(
    string SheetName,
    string KeyColumn,
    string ValueColumn,
    string ParameterName);

/// <summary>
/// Подсказка для парсера: на листе <see cref="KeyValueVertical.SheetName"/> ниже
/// строки-маркера <paramref name="StartMarker"/> (в колонке <paramref name="MarkerColumn"/>)
/// расположена секция бюджета (главы и подстатьи). Парсер эмитит каждую её строку
/// как отдельный <see cref="ParsedRow"/> с <c>Sheet = "{sheetName} {SheetMarker}"</c>
/// и ячейками, ключи которых — буквы исходных колонок (<c>"A"</c>, <c>"B"</c>, …, до
/// <paramref name="LastIncludedColumn"/>). Маппер различает обычные KV-строки и
/// бюджетные по суффиксу <see cref="SheetMarker"/>.
///
/// Парсинг останавливается на первой строке, чей текст в <paramref name="MarkerColumn"/>
/// содержит любой из <paramref name="EndMarkers"/> (case-insensitive). Пустые строки
/// внутри секции пропускаются.
/// </summary>
/// <param name="MarkerColumn">Колонка, в которой искать маркеры (обычно та же, что <c>KeyColumn</c>).</param>
/// <param name="StartMarker">Текст-маркер начала секции (например, <c>"Себестоимость"</c>).</param>
/// <param name="EndMarkers">Набор текстов-маркеров конца секции (case-insensitive substring).</param>
/// <param name="LastIncludedColumn">До какой колонки включительно собирать ячейки в строке (например, <c>"G"</c>).</param>
/// <param name="SheetMarker">Суффикс, добавляемый к <c>Sheet</c> бюджетных строк, чтобы маппер их различал. По умолчанию <c>"(budget)"</c>.</param>
public sealed record BudgetSectionHint(
    string MarkerColumn,
    string StartMarker,
    IReadOnlyList<string> EndMarkers,
    string LastIncludedColumn,
    string SheetMarker = "(budget)");

/// <summary>
/// Подсказка для парсера: на том же листе <see cref="KeyValueVertical.SheetName"/>
/// от строки-маркера <paramref name="StartMarker"/> до строки-маркера
/// <paramref name="EndMarker"/> (оба ищутся в <paramref name="MarkerColumn"/>,
/// case-insensitive substring) расположена квартальная таблица сумм для графика
/// финансирования (ГФ) Главы 1. Шапка с датами начала кварталов лежит в строке
/// <paramref name="QuarterHeaderRow"/>; квартальные суммы для каждой статьи —
/// в колонках <paramref name="FirstQuarterColumn"/>..<paramref name="LastQuarterColumn"/>.
///
/// Парсер эмитит набор <see cref="ParsedRow"/> с <c>Sheet = "{sheetName} {SheetMarker}"</c>:
/// • Первая строка — «датовая» (<c>SourceRowNumber = QuarterHeaderRow</c>):
///   <c>Cells["C"] = "__quarters__"</c> (sentinel-маркер), <c>Cells["H"]..Cells["CU"]</c> =
///   ISO-даты начала кварталов («2026-01-01»). Дату извлекаем через ClosedXML
///   <c>cell.GetDateTime()</c> где возможно, иначе по тексту.
/// • Дальше — по одной строке на каждую непустую строку диапазона (между StartMarker
///   и EndMarker). В <c>Cells</c>: <c>"C"</c> = Title из колонки <see cref="MarkerColumn"/>;
///   <c>"H"..</c> = текстовые значения квартальных колонок (пустая ячейка = пустая строка).
///   <c>SourceRowNumber</c> — абсолютный номер строки в листе (для точного указания
///   ячейки <c>{col}{row}</c> в построчном журнале ошибок ГФ).
///
/// Семантика блока (этап 1, исключение «Этап 2/3», маппинг статей в коды бюджета)
/// — на стороне маппера, парсер просто отдаёт сырые ячейки.
/// </summary>
/// <param name="MarkerColumn">Колонка с Title (обычно та же, что <c>KeyColumn</c>).</param>
/// <param name="StartMarker">Текст начала блока (например, «Глава 1.»).</param>
/// <param name="EndMarker">Текст конца блока (например, «Глава 2.»).</param>
/// <param name="QuarterHeaderRow">Абсолютный номер строки с датами начала кварталов (например, 7).</param>
/// <param name="FirstQuarterColumn">Буква первой квартальной колонки (например, «H»).</param>
/// <param name="LastQuarterColumn">Буква последней квартальной колонки (например, «CU»).</param>
/// <param name="SheetMarker">Суффикс для <c>Sheet</c> эмитируемых строк.</param>
public sealed record ChapterScheduleHint(
    string MarkerColumn,
    string StartMarker,
    string EndMarker,
    int QuarterHeaderRow,
    string FirstQuarterColumn,
    string LastQuarterColumn,
    string SheetMarker = "(schedule)");
