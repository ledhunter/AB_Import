using KiloImportService.Api.Budget;
using KiloImportService.Api.Data;
using KiloImportService.Api.Data.Entities;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Pipeline;
using KiloImportService.Api.Pdf;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KiloImportService.Api.Controllers;

/// <summary>
/// REST API сессий импорта.
///
/// Контракты:
///   POST /api/imports                 — загрузить файл (multipart/form-data)
///   GET  /api/imports                 — список сессий (история импортов) с фильтрами и пагинацией
///   GET  /api/imports/{id}            — получить статус сессии (для polling fallback)
///   GET  /api/imports/{id}/report     — получить отчёт (распарсенные строки, ошибки)
///   POST /api/imports/{id}/apply      — применить валидные строки в visary_db
///   POST /api/imports/{id}/cancel     — отменить сессию (только до Apply)
///   POST /api/imports/export-pdf      — PDF-отчёт по выбранным сессиям
/// </summary>
[ApiController]
[Route("api/imports")]
public class ImportsController : ControllerBase
{
    private readonly ImportServiceDbContext _db;
    private readonly ImportPipeline _pipeline;
    private readonly IImportSessionCancellation _cancellation;
    private readonly ILogger<ImportsController> _log;

    // Фоновые задачи парсинга/apply, чтобы Upload вернул 202 быстро.
    // В проде — лучше Hangfire / .NET BackgroundService с очередью.
    private static readonly TaskFactory _backgroundFactory = new(
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskContinuationOptions.None,
        TaskScheduler.Default);

    public ImportsController(
        ImportServiceDbContext db,
        ImportPipeline pipeline,
        IImportSessionCancellation cancellation,
        ILogger<ImportsController> log)
    {
        _db = db;
        _pipeline = pipeline;
        _cancellation = cancellation;
        _log = log;
    }

    /// <summary>
    /// Загрузить файл на импорт. Парсинг и валидация запускаются в фоне —
    /// клиент получает <c>sessionId</c> и подписывается на SignalR для прогресса.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 МБ
    public async Task<IActionResult> Upload(
        [FromForm] string importTypeCode,
        [FromForm] IFormFile file,
        [FromForm] int? projectId,
        [FromForm] int? siteId,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Файл не передан или пустой." });
        if (string.IsNullOrWhiteSpace(importTypeCode))
            return BadRequest(new { error = "Не указан importTypeCode." });

        ImportSession session;
        try
        {
            await using var stream = file.OpenReadStream();
            session = await _pipeline.UploadAsync(
                importTypeCode, stream, file.FileName, projectId, siteId,
                userId: User.Identity?.Name, ct);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (NotSupportedException ex) { return BadRequest(new { error = ex.Message }); }

        // Регистрируем CTS для сессии — Cancel-endpoint сможет отменить пайплайн.
        var ctSession = _cancellation.Register(session.Id);

        // Запускаем фоновую обработку (не ждём её здесь).
        var sessionId = session.Id;
        _ = _backgroundFactory.StartNew(async () =>
        {
            using var scope = HttpContext.RequestServices
                .GetRequiredService<IServiceScopeFactory>().CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<ImportPipeline>();
            try
            {
                await pipeline.ParseAndValidateAsync(sessionId, ctSession);
            }
            catch (OperationCanceledException) when (ctSession.IsCancellationRequested)
            {
                // Cancel был вызван — Pipeline уже выставил статус Cancelled.
                scope.ServiceProvider.GetRequiredService<ILogger<ImportsController>>()
                    .LogInformation("ParseAndValidate cancelled for session {SessionId}", sessionId);
            }
            catch (Exception ex)
            {
                scope.ServiceProvider.GetRequiredService<ILogger<ImportsController>>()
                    .LogError(ex, "ParseAndValidate failed for session {SessionId}", sessionId);
            }
            finally
            {
                _cancellation.Unregister(sessionId);
            }
        });

        return Accepted(new { sessionId, status = session.Status.ToString() });
    }

    /// <summary>
    /// Список сессий импорта — история всех загрузок. Отсортирован по StartedAt DESC.
    /// Опциональные фильтры по статусу и типу импорта; пагинация skip/take.
    /// Лёгкий ответ без stages/rows/errors — детальное состояние получают через GET {id}.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] string? status = null,
        [FromQuery] string? importTypeCode = null,
        CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        take = Math.Clamp(take, 1, 200);

        var q = _db.Sessions.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<ImportStatus>(status, ignoreCase: true, out var parsedStatus))
        {
            q = q.Where(s => s.Status == parsedStatus);
        }
        if (!string.IsNullOrWhiteSpace(importTypeCode))
        {
            q = q.Where(s => s.ImportTypeCode == importTypeCode);
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(s => s.StartedAt)
            .Skip(skip)
            .Take(take)
            .Select(s => new
            {
                sessionId = s.Id,
                importTypeCode = s.ImportTypeCode,
                fileName = s.FileName,
                fileFormat = s.FileFormat.ToString(),
                status = s.Status.ToString(),
                startedAt = s.StartedAt,
                completedAt = s.CompletedAt,
                totalRows = s.TotalRows,
                successRows = s.SuccessRows,
                errorRows = s.ErrorRows,
                errorMessage = s.ErrorMessage,
            })
            .ToListAsync(ct);

        return Ok(new
        {
            items,
            pagination = new { skip, take, total }
        });
    }

    /// <summary>Получить состояние сессии (для polling fallback, основной канал — SignalR).</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var s = await _db.Sessions
            .AsNoTracking()
            .Include(x => x.Stages)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();

        var generatedFiles = await BuildGeneratedFilesAsync(s, ct);

        return Ok(new
        {
            sessionId = s.Id,
            importTypeCode = s.ImportTypeCode,
            fileName = s.FileName,
            fileFormat = s.FileFormat.ToString(),
            status = s.Status.ToString(),
            startedAt = s.StartedAt,
            completedAt = s.CompletedAt,
            totalRows = s.TotalRows,
            successRows = s.SuccessRows,
            errorRows = s.ErrorRows,
            errorMessage = s.ErrorMessage,
            stages = s.Stages.OrderBy(st => st.StartedAt).Select(st => new
            {
                kind = st.Kind.ToString(),
                startedAt = st.StartedAt,
                completedAt = st.CompletedAt,
                isSuccess = st.IsSuccess,
                progressPercent = st.ProgressPercent,
                message = st.Message
            }),
            generatedFiles,
        });
    }

    /// <summary>
    /// Собирает список файлов, сгенерированных по результатам сессии: бюджет XLSX для
    /// «Финмодели» (если найдены бюджетные mapped-строки), в будущем — другие выгрузки.
    /// Элементы массива описывают «доступный к скачиванию артефакт», а не существующий
    /// файл на диске: backend генерирует их по запросу через download-эндпоинт.
    /// </summary>
    private async Task<List<object>> BuildGeneratedFilesAsync(ImportSession s, CancellationToken ct)
    {
        var files = new List<object>();

        if (s.ImportTypeCode == "finmodel")
        {
            // Бюджет показываем, только если в staged-таблице есть валидные/применённые
            // строки Kind='budget' (иначе кнопка «Скачать» сразу дала бы 404).
            // Дешёвый AnyAsync с фильтром на jsonb — без полного чтения mapped values.
            var hasBudgetRows = await _db.StagedRows
                .AsNoTracking()
                .AnyAsync(r => r.ImportSessionId == s.Id
                            && (r.Status == StagedRowStatus.Valid
                                || r.Status == StagedRowStatus.Applied)
                            && r.MappedValues != null
                            && EF.Functions.JsonContains(r.MappedValues, "{\"Kind\":\"budget\"}"), ct);

            if (hasBudgetRows)
            {
                files.Add(new
                {
                    kind = "budget-xlsx",
                    label = "Бюджет для импорта в Visary",
                    description = "XLSX по эталону «Бюджет_А4.1» — для ручного импорта на стороне Visary",
                    downloadUrl = $"/api/imports/{s.Id}/budget-xlsx",
                    actionUrl = (string?)null,
                    fileName = $"Бюджет_{s.Id}.xlsx",
                });
                // Action-кнопка: автоматически залить XLSX в файловое хранилище Visary
                // и создать typedimportwbs (TypedJournal-импорт). Показываем рядом с
                // кнопкой «Скачать» — пользователь сам решает: вручную или одной кнопкой.
                // См. doc_project/82-visary-file-storage-upload.md.
                files.Add(new
                {
                    kind = "budget-upload",
                    label = "Загрузить бюджет в Visary",
                    description = "XLSX уходит в файловое хранилище Visary и стартует импорт через typedimportwbs",
                    downloadUrl = (string?)null,
                    actionUrl = $"/api/imports/{s.Id}/budget-upload",
                    fileName = $"Бюджет_{s.Id}.xlsx",
                });
            }
        }

        return files;
    }

    /// <summary>
    /// Подробный отчёт сессии: распарсенные строки + ошибки.
    /// Для большого числа строк — пагинация через <c>skip</c>/<c>take</c>.
    /// </summary>
    [HttpGet("{id:guid}/report")]
    public async Task<IActionResult> GetReport(Guid id, [FromQuery] int skip = 0, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var session = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (session is null) return NotFound();

        take = Math.Clamp(take, 1, 500);

        var rowsQ = _db.StagedRows.AsNoTracking().Where(r => r.ImportSessionId == id);
        var totalRows = await rowsQ.CountAsync(ct);
        var rows = await rowsQ
            .OrderBy(r => r.Sheet).ThenBy(r => r.SourceRowNumber)
            .Skip(skip)
            .Take(take)
            .Select(r => new { r.SourceRowNumber, r.Sheet, status = r.Status.ToString(), actions = r.Actions })
            .ToListAsync(ct);

        var errors = await _db.Errors.AsNoTracking()
            .Where(e => e.ImportSessionId == id)
            .OrderBy(e => e.Sheet).ThenBy(e => e.SourceRowNumber)
            .Select(e => new { e.SourceRowNumber, e.Sheet, e.ColumnName, e.ErrorCode, e.Message })
            .ToListAsync(ct);

        return Ok(new
        {
            sessionId = id,
            status = session.Status.ToString(),
            totalRows = session.TotalRows,
            successRows = session.SuccessRows,
            errorRows = session.ErrorRows,
            rows,
            rowsPagination = new { skip, take, total = totalRows },
            errors
        });
    }

    /// <summary>Применить валидные строки в visary_db.</summary>
    [HttpPost("{id:guid}/apply")]
    public async Task<IActionResult> Apply(Guid id, CancellationToken ct)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (session is null) return NotFound();
        if (session.Status != ImportStatus.Validated)
            return Conflict(new { error = $"Apply возможен только из статуса Validated (текущий: {session.Status})." });

        // Apply делаем синхронно — для MVP с небольшим объёмом данных это нормально.
        // Регистрируем CTS, чтобы Cancel-endpoint мог прервать долгую транзакцию.
        var ctApply = _cancellation.Register(id);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ctApply);
        try
        {
            await _pipeline.ApplyAsync(id, linked.Token);
        }
        catch (OperationCanceledException) when (ctApply.IsCancellationRequested)
        {
            _log.LogInformation("Apply cancelled for session {SessionId}", id);
            return Ok(new { sessionId = id, status = ImportStatus.Cancelled.ToString() });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apply failed for session {SessionId}", id);
            return StatusCode(500, new { error = ex.Message });
        }
        finally
        {
            _cancellation.Unregister(id);
        }
        return Ok(new { sessionId = id, status = session.Status.ToString() });
    }

    /// <summary>
    /// Сгенерировать PDF-отчёт по выбранным сессиям.
    /// Тело запроса: <c>{ "sessionIds": ["guid1", "guid2", ...] }</c>.
    /// Один PDF со всеми сессиями (page-break между ними) — пользователю удобнее, чем zip.
    /// </summary>
    [HttpPost("export-pdf")]
    public async Task<IActionResult> ExportPdf(
        [FromBody] ExportPdfRequest? request,
        [FromServices] ImportPdfReportService pdfService,
        CancellationToken ct)
    {
        if (request is null || request.SessionIds is null || request.SessionIds.Count == 0)
            return BadRequest(new { error = "Не передано ни одного sessionId." });

        // Ограничим число сессий в одном PDF — слишком большой файл генерировать долго.
        if (request.SessionIds.Count > 100)
            return BadRequest(new { error = "Можно выгрузить не более 100 сессий за раз." });

        try
        {
            var bytes = await pdfService.GenerateAsync(request.SessionIds, ct);
            var fileName = $"imports-{DateTime.Now:yyyyMMdd-HHmmss}.pdf";
            return File(bytes, "application/pdf", fileName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ExportPdf failed for {Count} sessions", request.SessionIds.Count);
            return StatusCode(500, new { error = $"Не удалось сформировать PDF: {ex.Message}" });
        }
    }

    /// <summary>DTO запроса для <see cref="ExportPdf"/>.</summary>
    public sealed class ExportPdfRequest
    {
        public List<Guid> SessionIds { get; set; } = [];
    }

    /// <summary>
    /// Сгенерировать XLSX-файл бюджета для ручного импорта в Visary (тип «Финмодель»).
    /// Visary при импорте бюджета чувствительна к структуре файла — поэтому копируем
    /// эталонный шаблон <c>Бюджет_А4.1.xlsx</c> и подменяем только суммы в колонках C/D
    /// по агрегации справочника. См. <c>doc_project/78-budget-xlsx-export.md</c>.
    /// </summary>
    [HttpGet("{id:guid}/budget-xlsx")]
    public async Task<IActionResult> ExportBudgetXlsx(
        Guid id,
        [FromServices] BudgetXlsxExporter exporter,
        CancellationToken ct)
    {
        var session = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (session is null) return NotFound();
        if (session.ImportTypeCode != "finmodel")
            return BadRequest(new { error = "Экспорт бюджета доступен только для импорта «Финмодель»." });

        try
        {
            var bytes = await exporter.GenerateAsync(id, ct);
            // RFC 5987: ASP.NET сам кодирует Unicode-имя файла в filename* для совместимости.
            var fileName = $"Бюджет_{id}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ExportBudgetXlsx failed for session {SessionId}", id);
            return StatusCode(500, new { error = $"Не удалось сформировать XLSX: {ex.Message}" });
        }
    }

    /// <summary>
    /// Загрузить сгенерированный XLSX бюджета в файловое хранилище Visary и создать
    /// задание импорта через <c>typedimportwbs</c> (TypedJournal). После успешного вызова
    /// бэк Visary запускает фоновую обработку импорта; возвращаем ID файла в ФХ и ID
    /// созданной записи импорта. См. <c>doc_project/82-visary-file-storage-upload.md</c>.
    /// </summary>
    [HttpPost("{id:guid}/budget-upload")]
    public async Task<IActionResult> UploadBudgetToVisary(
        Guid id,
        [FromServices] BudgetVisaryUploader uploader,
        CancellationToken ct)
    {
        var session = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (session is null) return NotFound();
        if (session.ImportTypeCode != "finmodel")
            return BadRequest(new { error = "Загрузка бюджета в Visary доступна только для импорта «Финмодель»." });

        try
        {
            var result = await uploader.UploadAsync(id, ct);
            return Ok(new
            {
                fileStorageItemId = result.FileStorageItemId,
                typedImportWbsId = result.TypedImportWbsId,
                fileName = result.FileName,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "UploadBudgetToVisary failed for session {SessionId}", id);
            return StatusCode(500, new { error = $"Не удалось загрузить бюджет в Visary: {ex.Message}" });
        }
    }

    /// <summary>
    /// Отменить сессию (до apply). Если фоновая задача активна — пытаемся
    /// прервать её через CTS; статус сессии в БД выставит сам пайплайн в catch.
    /// Если задача уже завершилась — просто помечаем статус как Cancelled.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (session is null) return NotFound();
        if (session.Status == ImportStatus.Applied)
            return Conflict(new { error = "Сессия уже применена, отмена невозможна." });
        if (session.Status == ImportStatus.Cancelled)
            return Ok(new { sessionId = id, status = session.Status.ToString() });

        // Пытаемся отменить фоновую задачу. Если CTS зарегистрирован — пайплайн
        // выкинет OperationCanceledException и обработает Cancelled сам.
        var cancelled = _cancellation.Cancel(id);
        if (cancelled)
        {
            _log.LogInformation("Cancel({SessionId}): сигнал отмены отправлен фоновой задаче", id);
            // Статус не трогаем — пайплайн его обновит в catch (OperationCanceledException).
            // Возвращаем подсказку клиенту.
            return Accepted(new { sessionId = id, status = "CancelRequested" });
        }

        // Активной задачи нет (Pending до старта фона / уже завершилась).
        // Помечаем сессию как Cancelled только для не-финальных статусов.
        if (session.Status is ImportStatus.Pending or ImportStatus.Validated)
        {
            session.Status = ImportStatus.Cancelled;
            session.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new { sessionId = id, status = session.Status.ToString() });
    }
}
