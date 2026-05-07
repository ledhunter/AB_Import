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
public sealed record KeyValueVertical(
    string SheetName,
    string KeyColumn,
    string ValueStartColumn,
    StageCountReference? StageCount = null) : FileLayoutHint;

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
