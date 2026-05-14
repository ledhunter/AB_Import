using KiloImportService.Api.Data;
using KiloImportService.Api.Data.Entities;
using KiloImportService.Api.Data.Visary;
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
    private readonly VisaryDbContext _visaryDb;
    private readonly IOptionsMonitor<VisaryOptions> _visaryOptions;
    private readonly ILogger<BudgetVisaryUploader> _log;

    public BudgetVisaryUploader(
        BudgetXlsxExporter exporter,
        IFileStorageClient fileStorage,
        ICrudClient crud,
        ImportServiceDbContext db,
        VisaryDbContext visaryDb,
        IOptionsMonitor<VisaryOptions> visaryOptions,
        ILogger<BudgetVisaryUploader> log)
    {
        _exporter = exporter;
        _fileStorage = fileStorage;
        _crud = crud;
        _db = db;
        _visaryDb = visaryDb;
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

        // Подтягиваем Title проекта для тела запроса (Visary в HAR передаёт {Title, ID}).
        // Если в локальном зеркале нет — отправляем пустую строку (Visary разрешает только ID).
        var projectTitle = await _visaryDb.ConstructionProjects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.Title)
            .FirstOrDefaultAsync(ct)
            ?? string.Empty;

        // 2) Генерируем XLSX из staged budget rows. Если строк нет — exporter сам бросит
        //    InvalidOperationException, и мы пробросим её клиенту.
        var xlsxBytes = await _exporter.GenerateAsync(sessionId, ct);
        var fileName = $"Бюджет_{sessionId}.xlsx";

        // 3) Заливаем в файловое хранилище. drive/dir — из конфига (BudgetUploadOptions).
        var opt = _visaryOptions.CurrentValue.BudgetUpload;
        var itemId = await _fileStorage.UploadAsync(
            xlsxBytes, fileName, XlsxMime, opt.DriveId, opt.DirectoryId, ct);

        // 4) Получаем link-токен для залитого файла.
        var fileLink = await _fileStorage.GetFileLinkAsync(opt.DriveId, itemId, true, ct);

        // 5) Создаём задание импорта в Visary.
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
            "BudgetVisaryUploader: session={SessionId} → fileStorageItemId={ItemId} → typedImportWbsId={ImportId} (siteId={SiteId})",
            sessionId, itemId, created.ID, siteId);

        return new BudgetVisaryUploadResult(
            FileStorageItemId: itemId,
            TypedImportWbsId: created.ID,
            FileName: fileName);
    }
}

public sealed record BudgetVisaryUploadResult(
    int FileStorageItemId,
    int TypedImportWbsId,
    string FileName);
