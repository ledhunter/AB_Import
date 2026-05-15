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
    /// <summary>
    /// Внутренний код Visary для типа импорта «Бюджет/WBS». Test: 10.
    /// </summary>
    /// <remarks>
    /// Целевой диск/папка ФХ берутся из поля <c>ProjectFolder</c> выбранного
    /// <c>ConstructionProject</c> (формат «<c>driveId,directoryId</c>», например «32,40110»),
    /// а не из конфига — см. doc_project/82-visary-file-storage-upload.md.
    /// </remarks>
    public int ImportType { get; set; } = 10;
}
