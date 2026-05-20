using System.Globalization;
using System.Text.Json;
using KiloImportService.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Visary.Api.CRUD;
using Visary.Api.Dto;
using Visary.Api.FileStorage;

namespace KiloImportService.Api.Budget;

/// <summary>
/// Абстракция над <see cref="BudgetVisaryUploader"/> — нужна, чтобы FinModelImportMapper
/// мог быть протестирован без построения всего DI-графа Visary/FileStorage (см.
/// <c>FinModelBudgetTests</c>). Production-код использует единственную реализацию.
/// </summary>
public interface IBudgetVisaryUploader
{
    Task<BudgetVisaryUploadAndWaitResult> UploadAndWaitAsync(
        Guid sessionId,
        TimeSpan? pollInterval = null,
        TimeSpan? maxWait = null,
        CancellationToken ct = default);
}

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
public sealed class BudgetVisaryUploader : IBudgetVisaryUploader
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

    /// <summary>
    /// Загружает XLSX-бюджет в Visary и ждёт завершения фоновой обработки Visary'я,
    /// опрашивая статус <c>typedimportwbs</c> до 5 минут (poll каждые 3 с). Возвращает
    /// результат с финальным статусом — caller (FinModelImportMapper) решает, можно ли
    /// после этого запускать импорт ГФ.
    /// </summary>
    /// <remarks>
    /// Тексты статусов в контракте Visary не зафиксированы. Классифицируем по корням
    /// слов: «успеш» или «предупреж» → ok=true (ГФ запускать можно); «ошибк»/«error»
    /// → ok=false с finalStatus; иначе — ещё в работе, опрашиваем дальше. По таймауту
    /// возвращаем ok=false с пометкой <see cref="BudgetVisaryUploadAndWaitResult.TimedOut"/>.
    /// </remarks>
    public async Task<BudgetVisaryUploadAndWaitResult> UploadAndWaitAsync(
        Guid sessionId,
        TimeSpan? pollInterval = null,
        TimeSpan? maxWait = null,
        CancellationToken ct = default)
    {
        var upload = await UploadAsync(sessionId, ct);

        var interval = pollInterval ?? TimeSpan.FromSeconds(3);
        var deadline = DateTimeOffset.UtcNow + (maxWait ?? TimeSpan.FromMinutes(5));

        TypedImportWbsRaw? snapshot = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                snapshot = await _crud.GetTypedImportWbsByIdAsync(upload.TypedImportWbsId, ct);
            }
            catch (Exception ex)
            {
                // Сетевая/HTTP-ошибка при опросе — не считаем фатальной для импорта,
                // даём шанс следующей попытке. По таймауту вернём ok=false. Логируем
                // тип и текст исключения — без них невидно, почему 5 минут опросов
                // дают «status='null'» (инцидент 2026-05-20: до полученной заплатки на
                // DTO здесь сидел JsonException из-за `Status:string?`).
                _log.LogWarning(ex,
                    "BudgetVisaryUploader: ошибка опроса typedimportwbs id={Id} ({ExType}: {ExMsg}) — попробуем снова через {Interval}",
                    upload.TypedImportWbsId, ex.GetType().Name, ex.Message, interval);
            }

            var statusRaw = snapshot?.Status;
            var statusText = ExtractStatusText(statusRaw);
            // Каждую итерацию пишем raw — это позволяет быстро увидеть, в какой форме
            // Visary шлёт статус (string / number / object {ID,Title}), если поменяется.
            _log.LogDebug(
                "BudgetVisaryUploader: typedimportwbs id={Id} poll: status raw={Raw} kind={Kind} text='{Text}'",
                upload.TypedImportWbsId,
                statusRaw?.GetRawText() ?? "(null)",
                statusRaw?.ValueKind.ToString() ?? "(null)",
                statusText ?? "(null)");

            if (IsSuccessStatus(statusRaw))
            {
                _log.LogInformation(
                    "BudgetVisaryUploader: typedimportwbs id={Id} завершён (status='{Status}', errors={Errors}, warnings={Warnings})",
                    upload.TypedImportWbsId, statusText, snapshot?.CountErrors, snapshot?.CountWarnings);
                return new BudgetVisaryUploadAndWaitResult(upload, true, false, statusText, snapshot?.CountErrors, snapshot?.CountWarnings);
            }
            if (IsFailureStatus(statusRaw))
            {
                _log.LogWarning(
                    "BudgetVisaryUploader: typedimportwbs id={Id} завершён с ошибкой (status='{Status}')",
                    upload.TypedImportWbsId, statusText);
                return new BudgetVisaryUploadAndWaitResult(upload, false, false, statusText, snapshot?.CountErrors, snapshot?.CountWarnings);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                _log.LogWarning(
                    "BudgetVisaryUploader: typedimportwbs id={Id} не завершился за отведённое время (последний status='{Status}')",
                    upload.TypedImportWbsId, statusText);
                return new BudgetVisaryUploadAndWaitResult(upload, false, true, statusText, snapshot?.CountErrors, snapshot?.CountWarnings);
            }

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Числовые коды поля <c>typedimportwbs.Status</c>, подтверждённые заказчиком
    /// (2026-05-20):
    /// <list type="bullet">
    ///   <item><c>10</c> — Новый</item>
    ///   <item><c>20</c> — Закончен успешно</item>
    ///   <item><c>30</c> — Закончен с ошибками</item>
    ///   <item><c>40</c> — В обработке</item>
    ///   <item><c>50</c> — Закончен с предупреждением</item>
    /// </list>
    /// Visary шлёт код именно числом (см. инцидент typedimportwbs ID=9443: после
    /// фикса DTO до <see cref="JsonElement"/>? polling видел `«50»`, но искал в
    /// строке корень «успеш» и не находил — 5 мин тайм-аута на завершённом за
    /// секунды импорте). 20/50 — терминальный успех (ГФ запускаем), 30 — терминальный
    /// неуспех, 10/40 — ещё в работе, ждём дальше.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> StatusCodeLabels =
        new Dictionary<int, string>
        {
            [10] = "Новый",
            [20] = "Закончен успешно",
            [30] = "Закончен с ошибками",
            [40] = "В обработке",
            [50] = "Закончен с предупреждением",
        };

    private static readonly HashSet<int> SuccessCodes = new() { 20, 50 };
    private static readonly HashSet<int> FailureCodes = new() { 30 };

    /// <summary>
    /// Извлекает human-читаемый текст статуса из <see cref="JsonElement"/>. Visary
    /// шлёт <c>Status</c> в нескольких формах (см. <see cref="TypedImportWbsRaw.Status"/>):
    /// число → ищем в <see cref="StatusCodeLabels"/>, при попадании отдаём русское
    /// название; неизвестный код — «Код N»; строка → как есть; объект → берём поле
    /// <c>Title</c>/<c>Name</c>; <c>null</c> — если значения нет или неизвестный формат.
    /// </summary>
    internal static string? ExtractStatusText(JsonElement? element)
    {
        if (element is null) return null;
        var el = element.Value;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString()?.Trim(),
            JsonValueKind.Number => FormatNumericStatus(el),
            JsonValueKind.Object => TryExtractObjectStatusTitle(el),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => null,
        };
    }

    private static string FormatNumericStatus(JsonElement el)
    {
        if (el.TryGetInt32(out var code))
            return StatusCodeLabels.TryGetValue(code, out var label)
                ? label
                : $"Код {code.ToString(CultureInfo.InvariantCulture)}";
        if (el.TryGetInt64(out var longCode))
            return $"Код {longCode.ToString(CultureInfo.InvariantCulture)}";
        return el.GetRawText();
    }

    private static string? TryExtractObjectStatusTitle(JsonElement obj)
    {
        // VisaryRef-стиль { ID, Title } или похожие — пробуем стандартные поля.
        foreach (var prop in new[] { "Title", "title", "Name", "name", "Caption" })
        {
            if (obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        // Объект без читаемого поля, но с числовым `ID` — Visary может слать
        // обёртку вокруг кода статуса. Mapping через ту же таблицу.
        if (obj.TryGetProperty("ID", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
            return FormatNumericStatus(idEl);
        return null;
    }

    /// <summary>
    /// Терминальный успех — <c>Закончен успешно</c> (20) или <c>Закончен с предупреждением</c> (50).
    /// Оба разрешают запуск ГФ (по бизнес-решению). Для строки/объекта-fallback — старая
    /// логика по корням слов (на случай, если Visary поменяет формат).
    /// </summary>
    internal static bool IsSuccessStatus(JsonElement? status)
    {
        if (TryGetStatusCode(status, out var code))
            return SuccessCodes.Contains(code);
        var s = ExtractStatusText(status);
        if (string.IsNullOrWhiteSpace(s)) return false;
        return ContainsTokenCi(s, "успеш")
            || ContainsTokenCi(s, "предупреж")
            || ContainsTokenCi(s, "complet")    // англоязычная локаль
            || ContainsTokenCi(s, "warning");
    }

    /// <summary>Терминальный неуспех (<c>30 — Закончен с ошибками</c>) — ждать дальше бессмысленно.</summary>
    internal static bool IsFailureStatus(JsonElement? status)
    {
        if (TryGetStatusCode(status, out var code))
            return FailureCodes.Contains(code);
        var s = ExtractStatusText(status);
        if (string.IsNullOrWhiteSpace(s)) return false;
        // «Закончен с ошибкой», «Ошибка», «Failed», «Error».
        return (ContainsTokenCi(s, "ошибк")
            || ContainsTokenCi(s, "ошиб")
            || ContainsTokenCi(s, "fail")
            || ContainsTokenCi(s, "error"))
            && !IsSuccessStatus(status);
    }

    /// <summary>
    /// Достаёт числовой код статуса из <c>Number</c> либо из <c>Object.ID</c> (если
    /// Visary шлёт обёртку). Возвращает <c>false</c>, если значение не числовое —
    /// классификация дальше идёт по текстовому fallback'у.
    /// </summary>
    private static bool TryGetStatusCode(JsonElement? element, out int code)
    {
        code = 0;
        if (element is null) return false;
        var el = element.Value;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out code))
            return true;
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("ID", out var idEl)
            && idEl.ValueKind == JsonValueKind.Number
            && idEl.TryGetInt32(out code))
            return true;
        return false;
    }

    private static bool ContainsTokenCi(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

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

/// <summary>
/// Результат полного цикла «залить XLSX → дождаться завершения Visary'я».
/// <see cref="Success"/> = true, если Visary вернул «Закончен успешно» либо
/// «Закончен с предупреждениями» (оба разрешают запуск импорта ГФ).
/// <see cref="TimedOut"/> = true, если за отведённое время финальный статус не пришёл.
/// </summary>
public sealed record BudgetVisaryUploadAndWaitResult(
    BudgetVisaryUploadResult Upload,
    bool Success,
    bool TimedOut,
    string? FinalStatus,
    int? CountErrors,
    int? CountWarnings);
