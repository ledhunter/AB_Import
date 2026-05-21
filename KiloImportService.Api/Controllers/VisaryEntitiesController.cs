using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Visary.Api.CRUD;
using Visary.Api.ListView;

namespace KiloImportService.Api.Controllers;

/// <summary>
/// Прокси-контроллер для основных сущностей Visary. URL-имена идут слитно — точно
/// как мнемоники Visary (<c>/api/visary/constructionsites</c>, <c>/api/visary/rooms</c> и т.п.),
/// чтобы любому, кто знает API Visary, маршруты были очевидны.
///
/// Список (GET без id) возвращает <c>ListViewResponse&lt;TRaw&gt;</c> — облегчённое DTO
/// (то, что отдаёт <c>POST /api/visary/listview/...</c>). Конкретное значение (GET с id)
/// возвращает <c>*Full</c>-DTO с полным набором полей (через <c>GET /api/visary/crud/{m}/{id}</c>).
///
/// При добавлении новой основной сущности — добавьте 1 пару actions ниже.
/// </summary>
[ApiController]
[Route("api/visary")]
public sealed class VisaryEntitiesController : ControllerBase
{
    private readonly IListViewClient _lv;
    private readonly ICrudClient _cr;

    public VisaryEntitiesController(IListViewClient lv, ICrudClient cr)
    {
        _lv = lv;
        _cr = cr;
    }

    // ─── ConstructionProjects ────────────────────────────────────────────────
    [HttpGet("constructionprojects")]
    public Task<object> ListProjects(
        [FromQuery] string? search,
        [FromQuery] int pageSize = 200,
        CancellationToken ct = default)
        => Box(_lv.GetProjectsAsync(search, pageSize, ct));

    [HttpGet("constructionprojects/{id:int}")]
    public Task<object> GetProject(int id, CancellationToken ct)
        => Box(_cr.GetProjectByIdFullAsync(id, ct));

    // ─── ConstructionSites ───────────────────────────────────────────────────
    [HttpGet("constructionsites")]
    public Task<object> ListSites([FromQuery] int projectId, CancellationToken ct)
        => Box(_lv.GetSitesByProjectAsync(projectId, ct));

    [HttpGet("constructionsites/{id:int}")]
    public Task<object> GetSite(int id, CancellationToken ct)
        => Box(_cr.GetSiteByIdFullAsync(id, ct));

    // ─── ConstructionSections ────────────────────────────────────────────────
    [HttpGet("constructionsections")]
    public Task<object> ListSections(
        [FromQuery] int siteId,
        [FromQuery] string? titleFilter,
        CancellationToken ct)
        => Box(_lv.GetSectionsBySiteAsync(siteId, titleFilter, ct));

    [HttpGet("constructionsections/{id:int}")]
    public Task<object> GetSection(int id, CancellationToken ct)
        => Box(_cr.GetSectionByIdAsync(id, ct));

    // ─── ConstructionSiteIndicators (ТЭП) ────────────────────────────────────
    [HttpGet("constructionsiteindicators")]
    public Task<object> ListIndicators(
        [FromQuery] int siteId,
        [FromQuery] string? titleFilter,
        CancellationToken ct)
        => Box(_lv.GetIndicatorsBySiteAsync(siteId, titleFilter, ct));

    [HttpGet("constructionsiteindicators/{id:int}")]
    public Task<object> GetIndicator(int id, CancellationToken ct)
        => Box(_cr.GetIndicatorByIdAsync(id, ct));

    // ─── ConstructionSiteIndicatorValues (значения ТЭП) ──────────────────────
    [HttpGet("constructionsiteindicatorvalues")]
    public Task<object> ListIndicatorValues(
        [FromQuery] int indicatorId,
        CancellationToken ct)
        => Box(_lv.GetIndicatorValuesByIndicatorAsync(indicatorId, ct));

    [HttpGet("constructionsiteindicatorvalues/{id:int}")]
    public Task<object> GetIndicatorValue(int id, CancellationToken ct)
        => Box(_cr.GetIndicatorValueByIdAsync(id, ct));

    // ─── Rooms ───────────────────────────────────────────────────────────────
    // Один параметр обязателен: siteId ИЛИ sectionId. Без них список будет десятками тысяч.
    [HttpGet("rooms")]
    public async Task<IActionResult> ListRooms(
        [FromQuery] int? siteId,
        [FromQuery] int? sectionId,
        [FromQuery] string? uniqueNumberFilter,
        CancellationToken ct)
    {
        if (siteId.HasValue)
            return Ok(await _lv.GetRoomsBySiteAsync(siteId.Value, uniqueNumberFilter, ct));
        if (sectionId.HasValue)
            return Ok(await _lv.GetRoomsBySectionAsync(sectionId.Value, uniqueNumberFilter, ct));
        return BadRequest(new { error = "Укажите siteId или sectionId" });
    }

    [HttpGet("rooms/{id:int}")]
    public Task<object> GetRoom(int id, CancellationToken ct)
        => Box(_cr.GetRoomByIdAsync(id, ct));

    // ─── CadastralAreas (ЗУ) ─────────────────────────────────────────────────
    [HttpGet("cadastralareas")]
    public Task<object> ListCadastralAreas(
        [FromQuery] string? cadastralNumFilter,
        CancellationToken ct)
        => Box(_lv.ListCadastralAreasAsync(cadastralNumFilter, ct));

    [HttpGet("cadastralareas/{id:int}")]
    public Task<object> GetCadastralArea(int id, CancellationToken ct)
        => Box(_cr.GetCadastralAreaByIdAsync(id, ct));

    // ─── PercentBets ─────────────────────────────────────────────────────────
    [HttpGet("percentbets")]
    public Task<object> ListPercentBets(
        [FromQuery] string? lmIdFilter,
        [FromQuery] int? dealId,
        CancellationToken ct)
        => Box(_lv.GetPercentBetsAsync(lmIdFilter, dealId, ct));

    [HttpGet("percentbets/{id:int}")]
    public Task<object> GetPercentBet(int id, CancellationToken ct)
        => Box(_cr.GetPercentBetByIdAsync(id, ct));

    // ─── ShareAgreements (ДДУ) ───────────────────────────────────────────────
    [HttpGet("shareagreements")]
    public Task<object> ListShareAgreements(
        [FromQuery] int roomId,
        [FromQuery] string? numberFilter,
        CancellationToken ct)
        => Box(_lv.GetShareAgreementsByRoomAsync(roomId, numberFilter, ct));

    [HttpGet("shareagreements/{id:int}")]
    public Task<object> GetShareAgreement(int id, CancellationToken ct)
        => Box(_cr.GetShareAgreementByIdAsync(id, ct));

    // ─── Deals ───────────────────────────────────────────────────────────────
    // Если задан projectId — фильтруем по нему; иначе — общий список со страницей.
    [HttpGet("deals")]
    public async Task<IActionResult> ListDeals(
        [FromQuery] int? projectId,
        [FromQuery] string? lmIdFilter,
        [FromQuery] string? docNumberFilter,
        CancellationToken ct)
    {
        var result = projectId.HasValue
            ? await _lv.GetDealsByProjectAsync(projectId.Value, lmIdFilter, docNumberFilter, ct)
            : await _lv.GetDealsAsync(lmIdFilter, docNumberFilter, ct);
        return Ok(result);
    }

    [HttpGet("deals/{id:int}")]
    public Task<object> GetDeal(int id, CancellationToken ct)
        => Box(_cr.GetDealByIdAsync(id, ct));

    // ─── Organizations ───────────────────────────────────────────────────────
    // clientId обязателен — без него listview организаций возвращает десятки тысяч строк.
    [HttpGet("organizations")]
    public Task<object> ListOrganizations(
        [FromQuery, BindRequired] string clientId,
        CancellationToken ct)
        => Box(_lv.GetOrganizationsByClientIdAsync(clientId, ct));

    [HttpGet("organizations/{id:int}")]
    public Task<object> GetOrganization(int id, CancellationToken ct)
        => Box(_cr.GetOrganizationByIdAsync(id, ct));

    // ─── helper ──────────────────────────────────────────────────────────────
    // Async-helper, чтобы возвращать строго-типизированный результат, не теряя
    // полиморфизм: сериализатор пишет реальный runtime-тип в JSON.
    private static async Task<object> Box<T>(Task<T> task) => (await task)!;
}
