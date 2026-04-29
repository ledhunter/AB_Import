using KiloImportService.Api.Data;
using KiloImportService.Api.Data.Entities;
using KiloImportService.Api.Domain.Importing;
using KiloImportService.Api.Domain.Pipeline;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KiloImportService.Api.Controllers;

/// <summary>
/// REST API сессий импорта.
///
/// Контракты:
///   POST /api/imports                 — загрузить файл (multipart/form-data)
///   GET  /api/imports/{id}            — получить статус сессии (для polling fallback)
///   GET  /api/imports/{id}/report     — получить отчёт (распарсенные строки, ошибки)
///   POST /api/imports/{id}/apply      — применить валидные строки в visary_db
///   POST /api/imports/{id}/cancel     — отменить сессию (только до Apply)
/// </summary>
[ApiController]
[Route("api/imports")]
public class ImportsController : ControllerBase
{
    private readonly ImportServiceDbContext _db;
    private readonly ImportPipeline _pipeline;
    private readonly ILogger<ImportsController> _log;

    // Фоновые задачи парсинга/apply, чтобы Upload вернул 202 быстро.
    // В проде — лучше Hangfire / .NET BackgroundService с очередью.
    private static readonly TaskFactory _backgroundFactory = new(
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskContinuationOptions.None,
        TaskScheduler.Default);

    public ImportsController(ImportServiceDbContext db, ImportPipeline pipeline, ILogger<ImportsController> log)
    {
        _db = db;
        _pipeline = pipeline;
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

        // Запускаем фоновую обработку (не ждём её здесь).
        _ = _backgroundFactory.StartNew(async () =>
        {
            using var scope = HttpContext.RequestServices
                .GetRequiredService<IServiceScopeFactory>().CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<ImportPipeline>();
            try
            {
                await pipeline.ParseAndValidateAsync(session.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                scope.ServiceProvider.GetRequiredService<ILogger<ImportsController>>()
                    .LogError(ex, "ParseAndValidate failed for session {SessionId}", session.Id);
            }
        });

        return Accepted(new { sessionId = session.Id, status = session.Status.ToString() });
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
            })
        });
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
            .OrderBy(r => r.SourceRowNumber)
            .Skip(skip)
            .Take(take)
            .Select(r => new { r.SourceRowNumber, status = r.Status.ToString() })
            .ToListAsync(ct);

        var errors = await _db.Errors.AsNoTracking()
            .Where(e => e.ImportSessionId == id)
            .OrderBy(e => e.SourceRowNumber)
            .Select(e => new { e.SourceRowNumber, e.ColumnName, e.ErrorCode, e.Message })
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
        // Для тяжёлых сессий в будущем перенесём в фон.
        try
        {
            await _pipeline.ApplyAsync(id, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apply failed for session {SessionId}", id);
            return StatusCode(500, new { error = ex.Message });
        }
        return Ok(new { sessionId = id, status = session.Status.ToString() });
    }

    /// <summary>Отменить сессию (до apply).</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (session is null) return NotFound();
        if (session.Status == ImportStatus.Applied)
            return Conflict(new { error = "Сессия уже применена, отмена невозможна." });

        session.Status = ImportStatus.Cancelled;
        session.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { sessionId = id, status = session.Status.ToString() });
    }
}
