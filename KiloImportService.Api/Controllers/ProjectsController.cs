using KiloImportService.Api.Domain.Projects;
using Microsoft.AspNetCore.Mvc;

namespace KiloImportService.Api.Controllers;

/// <summary>
/// Поиск/синхронизация проектов Visary, кэшированных в локальной БД.
///
/// Контракт:
///   POST /api/projects/sync       — bulk-синхронизация всех проектов из Visary.
///   GET  /api/projects/search?q=  — поиск с fallback в Visary при пустом результате.
///
/// Используется UI'ем для autocomplete-Select при выборе проекта в импорте.
/// См. doc_project/18-projects-cache.md.
/// </summary>
[ApiController]
[Route("api/projects")]
public class ProjectsController : ControllerBase
{
    private readonly IProjectsCacheService _service;
    private readonly ILogger<ProjectsController> _log;

    public ProjectsController(
        IProjectsCacheService service,
        ILogger<ProjectsController> log)
    {
        _service = service;
        _log = log;
    }

    /// <summary>
    /// Полная синхронизация. Идемпотентна — можно вызывать при каждом открытии формы.
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        try
        {
            var result = await _service.SyncAllAsync(ct);
            return Ok(new
            {
                total = result.Total,
                upserted = result.Upserted,
                durationMs = result.DurationMs,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ProjectsController.Sync failed");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "visary_sync_failed",
                message = ex.Message,
            });
        }
    }

    /// <summary>
    /// Поиск проектов: сначала локально (ILIKE), при пустом результате — Visary fallback.
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? q,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        try
        {
            var result = await _service.SearchAsync(q ?? string.Empty, limit, ct);
            return Ok(new
            {
                items = result.Items.Select(p => new
                {
                    id = p.Id,
                    title = p.Title,
                    code = !string.IsNullOrEmpty(p.IdentifierKK)
                        ? p.IdentifierKK
                        : (!string.IsNullOrEmpty(p.IdentifierZPLM) ? p.IdentifierZPLM : $"ID-{p.Id}"),
                }),
                fromFallback = result.FromFallback,
                total = result.Items.Count,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ProjectsController.Search failed q='{Q}'", q);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "projects_search_failed",
                message = ex.Message,
            });
        }
    }
}
