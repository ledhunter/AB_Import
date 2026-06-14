using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KiloImportService.Api.Controllers;

[ApiController]
[Route("health")]
// liveness/readinessProbe в k8s/докере идут БЕЗ Authorization-header.
// При включённой JWT-валидации (Auth:Authority задан) MapControllers().RequireAuthorization()
// в Program.cs закрывает все контроллеры; этот атрибут локально снимает требование
// только с health-эндпоинта. См. doc_project/130-kubernetes-deployment-guide.md.
[AllowAnonymous]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", time = DateTimeOffset.UtcNow });
}
