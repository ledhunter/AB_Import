using KiloImportService.Api.Visary;
using Microsoft.AspNetCore.Mvc;

namespace KiloImportService.Api.Controllers;

/// <summary>
/// Универсальный прокси-контроллер для справочников Visary.
/// Маршруты регистрируются динамически через <see cref="VisaryDictionaryRegistry"/>:
/// <code>
/// GET /api/visary/{name}        — список
/// GET /api/visary/{name}/{id}   — конкретное значение
/// </code>
/// Чтобы добавить новый справочник, его не нужно дописывать в этом файле —
/// достаточно зарегистрировать через <c>AddVisaryDictionary&lt;TDto&gt;(...)</c> в Program.cs.
/// </summary>
[ApiController]
[Route("api/visary")]
public sealed class VisaryDictionariesController : ControllerBase
{
    private readonly VisaryDictionaryRegistry _registry;
    private readonly ILogger<VisaryDictionariesController> _log;

    public VisaryDictionariesController(
        VisaryDictionaryRegistry registry,
        ILogger<VisaryDictionariesController> log)
    {
        _registry = registry;
        _log = log;
    }

    /// <summary>Список значений справочника. Опциональный фильтр по подстроке в Title.</summary>
    [HttpGet("{name}")]
    public async Task<IActionResult> List(
        string name,
        [FromQuery] string? titleFilter,
        CancellationToken ct)
    {
        if (!_registry.TryGet(name, out var handler))
            return DictionaryNotFound(name);

        var result = await handler.ListAsync(titleFilter, ct);
        return Ok(result);
    }

    /// <summary>Одно значение справочника по ID.</summary>
    [HttpGet("{name}/{id:int}")]
    public async Task<IActionResult> GetById(
        string name,
        int id,
        CancellationToken ct)
    {
        if (!_registry.TryGet(name, out var handler))
            return DictionaryNotFound(name);

        var result = await handler.GetByIdAsync(id, ct);
        return Ok(result);
    }

    private IActionResult DictionaryNotFound(string name)
    {
        _log.LogWarning("Dictionary '{Name}' not registered. Known: {Known}",
            name, string.Join(", ", _registry.RegisteredNames));
        return NotFound(new
        {
            error = $"Справочник '{name}' не зарегистрирован.",
            available = _registry.RegisteredNames,
        });
    }
}
