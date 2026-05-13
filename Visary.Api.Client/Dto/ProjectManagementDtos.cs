// ProjectManagement — связь «Проект ↔ Организация ↔ Роль» в Visary (например,
// «Застройщик ООО Альфа в проекте Тест ДОУ»). К объекту строительства (Site)
// привязывается через listview manytomany по `associationId`.
//
// Endpoint'ы:
//   POST /api/visary/listview/organization                            ← поиск Organization по ClientID (есть)
//   POST /api/visary/listview/constructionsite/manytomany/projectmanagement
//        ?associationId={siteId}                                       ← список PM для сайта
//   POST /api/visary/crud/projectmanagement                            ← создать PM
//   POST /api/visary/listview/constructionsite/manytomany/projectmanagement/link
//        ?associationId={siteId}&ids={pmId}                            ← привязать PM к сайту

namespace Visary.Api.Dto;

/// <summary>
/// Запись «менеджмент проекта» — связка проект/организация/роль из listview ответа.
/// </summary>
public sealed class ProjectManagementRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public VisaryRef? Project { get; set; }
    public VisaryRef? Role { get; set; }
    public VisaryRef? Organization { get; set; }

    /// <summary>0 = «в проекте», другие значения — расширения. См. примеры запросов в doc 75.</summary>
    public int? Affiliation { get; set; }

    public string? DateStart { get; set; }
    public string? DateEnd { get; set; }
    public string? Version { get; set; }
    public bool? Hidden { get; set; }
}

/// <summary>
/// Тело POST <c>/api/visary/crud/projectmanagement</c>.
/// </summary>
public sealed class ProjectManagementCreateRequest
{
    public VisaryRef Project { get; set; } = null!;
    public VisaryRef Organization { get; set; } = null!;
    public VisaryRef Role { get; set; } = null!;

    /// <summary>0 = «в проекте» (значение по умолчанию для нового флага).</summary>
    public int Affiliation { get; set; } = 0;
}

/// <summary>
/// Известные идентификаторы ролей <see cref="ProjectManagementCreateRequest.Role"/>.
/// Захардкожено для MVP: справочник `role` пока не интегрирован; ID 10 наблюдается в Visary
/// для роли «Застройщик» (см. примеры запросов в doc 75).
/// </summary>
public static class ProjectManagementRoles
{
    public const int Developer = 10;
    public const string DeveloperTitle = "Застройщик";
}
