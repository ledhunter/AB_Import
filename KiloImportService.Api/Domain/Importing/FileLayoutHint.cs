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
/// Стандартная табличная раскладка: первая строка — заголовки, каждая следующая — запись.
/// </summary>
public sealed record Tabular : FileLayoutHint;

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
    BudgetSectionHint? Budget = null) : FileLayoutHint;

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
