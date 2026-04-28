namespace KiloImportService.Api.Domain.Importing;

/// <summary>
/// Поддерживаемые форматы импорта. Совпадают с frontend-енумом из
/// <c>KiloImportService.Web/src/utils/fileFormat.ts</c>.
/// Имя — нижний регистр без точки (так удобно сравнивать с расширением файла).
/// </summary>
public enum FileFormat
{
    Csv,
    Xls,
    Xlsb,
    Xlsx
}

public static class FileFormatExtensions
{
    /// <summary>
    /// Определить формат по расширению файла. Возвращает <c>null</c> если расширение неизвестно.
    /// </summary>
    public static FileFormat? DetectFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "csv"  => FileFormat.Csv,
            "xls"  => FileFormat.Xls,
            "xlsb" => FileFormat.Xlsb,
            "xlsx" => FileFormat.Xlsx,
            _      => null
        };
    }

    public static string ToFileExtension(this FileFormat format) => format switch
    {
        FileFormat.Csv  => "csv",
        FileFormat.Xls  => "xls",
        FileFormat.Xlsb => "xlsb",
        FileFormat.Xlsx => "xlsx",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}
