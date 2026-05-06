using System.Collections.Generic;

namespace Visary.Api.Dto;

public sealed class VisaryRef
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public bool? Hidden { get; set; }
    public long? RowVersion { get; set; }
}

public sealed class ConstructionProjectRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public string? IdentifierKK { get; set; }
    public string? IdentifierZPLM { get; set; }
    public bool? Hidden { get; set; }
}

public sealed class ConstructionSiteRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public int? ConstructionProjectId { get; set; }
    public string? ConstructionPermissionNumber { get; set; }
    public string? ConstructionProjectNumber { get; set; }
    public string? StageNumber { get; set; }
    public int? RegionId { get; set; }
    public int? TownId { get; set; }
    public string? Address { get; set; }
    public bool? Hidden { get; set; }
    public DateTime? Version { get; set; }
    public int? FinishingMaterialId { get; set; }
}
