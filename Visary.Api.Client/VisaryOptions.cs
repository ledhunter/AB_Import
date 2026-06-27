namespace Visary.Api.Dto;

public sealed class VisaryOptions
{
    // Имена секций приведены к эталону `service-dev` (см. doc 145 + doc 132):
    // конфиг живёт в `EndpointsConfiguration:VisaryApi`, в env маппится как
    // `EndpointsConfiguration__VisaryApi__Endpoint` (двойное подчёркивание).
    public const string SectionName = "EndpointsConfiguration:VisaryApi";

    // Имя поля «Endpoint» — тоже эталонное; в helm values платформа Альфы
    // подставляет `$(WEBAPI_URL)/api` (см. alfa-building-fm-import.yaml).
    public string Endpoint { get; set; } = string.Empty;

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
