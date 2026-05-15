using System.Globalization;
using KiloImportService.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.FileStorage;

namespace KiloImportService.Api.Budget;

/// <summary>
/// Загружает сгенерированный XLSX-бюджет в файловое хранилище Visary и создаёт
/// TypedJournal-задание импорта (<c>typedimportwbs</c>). После успешного выполнения
/// импорт уезжает на бэк Visary и обрабатывается в фоне; пользователю возвращается ID
/// созданной записи импорта.
///
/// Цепочка (см. doc_project/82-visary-file-storage-upload.md):
/// 0. <see cref="ICrudClient.GetProjectByIdFullAsync"/> — GET <c>/api/visary/crud/constructionproject/{id}</c>:
///    из поля <c>ProjectFolder</c> (формат «driveId,directoryId», напр. «32,40110»)
///    получаем целевой диск/папку ФХ — папка определяется выбранным Проектом, а не конфигом.
/// 1. <see cref="BudgetXlsxExporter.GenerateAsync"/> — собрать XLSX из staged_rows.
/// 2. <see cref="IFileStorageClient.UploadAsync"/> — POST <c>/api/files/files/upload</c>.
/// 3. <see cref="IFileStorageClient.GetFileLinkAsync"/> — POST <c>/api/files/link/file_link/by_id</c>.
/// 4. <see cref="ICrudClient.CreateTypedImportWbsAsync"/> — POST <c>/api/visary/crud/typedimportwbs</c>.
/// </summary>
public sealed class BudgetVisaryUploader
{
    private const string XlsxMime =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly BudgetXlsxExporter _exporter;
    private readonly IFileStorageClient _fileStorage;
    private readonly ICrudClient _crud;
    private readonly ImportServiceDbContext _db;
    private readonly IOptionsMonitor<VisaryOptions> _visaryOptions;
    private readonly ILogger<BudgetVisaryUploader> _log;

    public BudgetVisaryUploader(
        BudgetXlsxExporter exporter,
        IFileStorageClient fileStorage,
        ICrudClient crud,
        ImportServiceDbContext db,
        IOptionsMonitor<VisaryOptions> visaryOptions,
        ILogger<BudgetVisaryUploader> log)
    {
        _exporter = exporter;
        _fileStorage = fileStorage;
        _crud = crud;
        _db = db;
        _visaryOptions = visaryOptions;
        _log = log;
    }

    public async Task<BudgetVisaryUploadResult> UploadAsync(Guid sessionId, CancellationToken ct)
    {
        // 1) Берём сессию — нужны Project/Site ID для тела запроса typedimportwbs.
        var session = await _db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new KeyNotFoundException($"Сессия импорта {sessionId} не найдена.");

        if (session.VisaryProjectId is not > 0)
            throw new InvalidOperationException(
                "У сессии не указан Visary projectId — невозможно создать typedimportwbs.");
        if (session.VisarySiteId is not > 0)
            throw new InvalidOperationException(
                "У сессии не указан Visary siteId — невозможно создать typedimportwbs.");

        var projectId = session.VisaryProjectId.Value;
        var siteId = session.VisarySiteId.Value;

        // 2) Тянем проект из Visary, чтобы достать ProjectFolder + актуальный Title.
        //    ProjectFolder задаёт целевую папку ФХ для загрузки сгенерированного XLSX
        //    (см. doc_project/82-visary-file-storage-upload.md). Полагаться на локальное
        //    зеркало нельзя — там этого поля нет.
        var project = await _crud.GetProjectByIdFullAsync(projectId, ct)
            ?? throw new InvalidOperationException(
                $"Visary не вернул проект id={projectId} для загрузки бюджета.");

        var (driveId, directoryId) = ParseProjectFolder(project.ProjectFolder, projectId);
        var projectTitle = project.Title ?? string.Empty;

        _log.LogInformation(
            "BudgetVisaryUploader: session={SessionId} projectId={ProjectId} → ProjectFolder='{Folder}' → driveId={DriveId}, directoryId={DirectoryId}",
            sessionId, projectId, project.ProjectFolder, driveId, directoryId);

        // 3) Генерируем XLSX из staged budget rows. Если строк нет — exporter сам бросит
        //    InvalidOperationException, и мы пробросим её клиенту.
        var xlsxBytes = await _exporter.GenerateAsync(sessionId, ct);
        var fileName = $"Бюджет_{sessionId}.xlsx";

        // 4) Заливаем в файловое хранилище в папку проекта.
        var opt = _visaryOptions.CurrentValue.BudgetUpload;
        var itemId = await _fileStorage.UploadAsync(
            xlsxBytes, fileName, XlsxMime, driveId, directoryId, ct);

        // 5) Получаем link-токен для залитого файла (тот же диск, что и при upload).
        var fileLink = await _fileStorage.GetFileLinkAsync(driveId, itemId, true, ct);

        // 6) Создаём задание импорта в Visary (link-токен из шага 5 — opaque-строка).
        var request = new TypedImportWbsCreateRequest
        {
            ProjectID = projectId,
            Project = new VisaryRef { ID = projectId, Title = projectTitle },
            ConstructionSiteID = siteId,
            ConstructionSite = new VisaryRef { ID = siteId },
            ImportType = opt.ImportType,
            StartLine = 0,
            SheetName = string.Empty,
            File = fileLink,
        };

        var created = await _crud.CreateTypedImportWbsAsync(request, ct);

        _log.LogInformation(
            "BudgetVisaryUploader: session={SessionId} → drive={DriveId}/dir={DirectoryId} → fileStorageItemId={ItemId} → typedImportWbsId={ImportId} (siteId={SiteId})",
            sessionId, driveId, directoryId, itemId, created.ID, siteId);

        return new BudgetVisaryUploadResult(
            FileStorageItemId: itemId,
            TypedImportWbsId: created.ID,
            FileName: fileName);
    }

    /// <summary>
    /// Парсит значение <c>ConstructionProject.ProjectFolder</c> в пару (driveId, directoryId).
    /// Формат — две неотрицательные целые цифры через запятую, например «<c>32,40110</c>».
    /// </summary>
    /// <remarks>
    /// Папка загрузки бюджета задаётся бизнес-процессом на стороне Visary (привязана к
    /// конкретному проекту), поэтому fallback на конфиг сознательно отсутствует:
    /// если поле пустое или не соответствует формату — это конфигурационная ошибка
    /// в карточке проекта, и пользователю должна вернуться внятная ошибка, а не
    /// «тихая» заливка не в ту папку.
    /// </remarks>
    private static (int DriveId, int DirectoryId) ParseProjectFolder(string? projectFolder, int projectId)
    {
        if (string.IsNullOrWhiteSpace(projectFolder))
            throw new InvalidOperationException(
                $"У Visary-проекта id={projectId} не заполнено поле ProjectFolder — невозможно определить папку для загрузки бюджета.");

        var parts = projectFolder.Split(',', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var driveId)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var directoryId)
            || driveId <= 0 || directoryId <= 0)
        {
            throw new InvalidOperationException(
                $"У Visary-проекта id={projectId} поле ProjectFolder='{projectFolder}' имеет неожиданный формат. Ожидается «driveId,directoryId», напр. «32,40110».");
        }

        return (driveId, directoryId);
    }
}

public sealed record BudgetVisaryUploadResult(
    int FileStorageItemId,
    int TypedImportWbsId,
    string FileName);
