namespace Visary.Api.Dto;

public sealed class VisaryOptions
{
    public const string SectionName = "Visary";

    public string BaseUrl { get; set; } = string.Empty;
    public string BearerToken { get; set; } = string.Empty;

    public int SyncPageSize { get; set; } = 200;
    public int DefaultPageSize { get; set; } = 50;
    public int LargePageSize { get; set; } = 500;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Параметры для автозагрузки сгенерированного XLSX-бюджета в файловое хранилище
    /// Visary и запуска импорта через <c>typedimportwbs</c>. См.
    /// <c>doc_project/82-visary-file-storage-upload.md</c> и HAR-файлы в <c>Context/</c>.
    /// </summary>
    public BudgetUploadOptions BudgetUpload { get; set; } = new();
}

public sealed class BudgetUploadOptions
{
    /// <summary>ID диска ФХ Visary, в который льётся бюджет. Test-окружение: 65.</summary>
    public int DriveId { get; set; } = 65;
    /// <summary>ID папки внутри диска. Test-окружение: 40870.</summary>
    public int DirectoryId { get; set; } = 40870;
    /// <summary>Внутренний код Visary для типа импорта «Бюджет/WBS». Test: 10.</summary>
    public int ImportType { get; set; } = 10;
}
