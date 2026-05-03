using KiloImportService.Api.Domain.Sites;
using Microsoft.AspNetCore.Mvc;
using Visary.Api.Exceptions;

namespace KiloImportService.Api.Controllers;

[ApiController]
[Route("api/sites")]
public class SitesController : ControllerBase
{
    private readonly ISitesSyncService _service;
    private readonly ILogger<SitesController> _log;

    public SitesController(
        ISitesSyncService service,
        ILogger<SitesController> log)
    {
        _service = service;
        _log = log;
    }

    [HttpPost("sync/{id:int}")]
    public async Task<IActionResult> Sync(int id, CancellationToken ct)
    {
        try
        {
            var result = await _service.SyncAsync(id, ct);
            return Ok(new { success = result, siteId = id });
        }
        catch (KeyNotFoundException ex)
        {
            _log.LogWarning(ex, "SitesController.Sync siteId={SiteId} not found", id);
            return NotFound(new
            {
                error = "site_not_found",
                message = ex.Message,
            });
        }
        catch (VisaryAuthException ex)
        {
            _log.LogError(ex, "SitesController.Sync auth failed siteId={SiteId}", id);
            return StatusCode(StatusCodes.Status401Unauthorized, new
            {
                error = "visary_auth_failed",
                message = ex.Message,
            });
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "SitesController.Sync visary request failed siteId={SiteId}", id);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "visary_sync_failed",
                message = ex.Message,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SitesController.Sync failed siteId={SiteId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "internal_error",
                message = ex.Message,
            });
        }
    }
}
