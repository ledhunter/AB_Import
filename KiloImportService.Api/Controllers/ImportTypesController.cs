using KiloImportService.Api.Domain.Mapping;
using Microsoft.AspNetCore.Mvc;

namespace KiloImportService.Api.Controllers;

/// <summary>
/// Реестр поддерживаемых типов импорта.
/// Frontend подгружает этот список вместо локального mock.
/// </summary>
[ApiController]
[Route("api/import-types")]
public class ImportTypesController : ControllerBase
{
    private readonly IImportMapperRegistry _registry;

    public ImportTypesController(IImportMapperRegistry registry) => _registry = registry;

    /// <summary>Список реализованных типов импорта (есть mapper) с человекочитаемыми названиями.</summary>
    [HttpGet]
    public IActionResult List()
    {
        // Метаданные (label, description) — пока статические, позже могут переехать в БД.
        var meta = new Dictionary<string, (string label, string description)>
        {
            ["rooms"]            = ("Помещения",          "Импорт реестра помещений (квартир, машиномест, кладовых)"),
            ["shareAgreements"]  = ("ДДУ",                "Импорт договоров долевого участия"),
            ["mixed"]            = ("Помещения + ДДУ",   "Совместный импорт помещений и связанных ДДУ"),
            ["paymentSchedule"]  = ("График платежей",   "Импорт графика платежей по ДДУ"),
            ["escrowAccounts"]   = ("Счета эскроу",      "Импорт данных по счетам эскроу"),
            ["constructionSites"]= ("Объекты строительства","Импорт объектов и секций"),
            ["organizations"]    = ("Организации",        "Импорт справочника организаций"),
            ["buyers"]           = ("Покупатели",         "Импорт покупателей по ДДУ"),
        };

        var implemented = _registry.SupportedTypeCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = meta
            .Select(kvp => new
            {
                id = kvp.Key,
                label = kvp.Value.label,
                description = kvp.Value.description,
                isImplemented = implemented.Contains(kvp.Key)
            })
            .OrderByDescending(t => t.isImplemented)
            .ThenBy(t => t.label)
            .ToList();

        return Ok(new { items = result, total = result.Count });
    }
}
