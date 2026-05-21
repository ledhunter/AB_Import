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
    public string? ConstructionProjectNumber { get; set; }
    public string? Description { get; set; }
    public string? Date { get; set; }
    public string? FinancingStart { get; set; }
    public string? DeveloperPIN { get; set; }
    public VisaryRef? Author { get; set; }
    public VisaryRef? ProjectManager { get; set; }
    public VisaryRef? Executor { get; set; }
    public VisaryRef? Sponsor { get; set; }
    public VisaryRef? Stage { get; set; }
    public VisaryRef? Type { get; set; }
    public VisaryRef? Region { get; set; }
    public VisaryRef? Town { get; set; }
    public VisaryRef? Developer { get; set; }
    public VisaryRef? DeveloperGroup { get; set; }
    public DateTime? Version { get; set; }
    public bool? Hidden { get; set; }
}

public sealed class ConstructionSiteRaw
{
    public int ID { get; set; }
    public string? Title { get; set; }
    public int? ConstructionProjectId { get; set; }
    public string? ConstructionPermissionNumber { get; set; }
    public string? ConstructionProjectNumber { get; set; }
    // Visary listview шлёт StageNumber числом (`"StageNumber": 1`), а CRUD-Full — int? в DTO.
    // Без Flexible-конвертера listview/constructionsite падал с «JSON value could not be
    // converted to System.String. Path: $.Data[0].StageNumber» — см. doc 101 и
    // Common/FlexibleStringJsonConverter.cs.
    [System.Text.Json.Serialization.JsonConverter(typeof(Visary.Api.Common.FlexibleStringJsonConverter))]
    public string? StageNumber { get; set; }
    public int? RegionId { get; set; }
    public int? TownId { get; set; }
    public string? Address { get; set; }
    public bool? Hidden { get; set; }
    public DateTime? Version { get; set; }
    public int? FinishingMaterialId { get; set; }
}

// FinishingMaterialRaw перенесён в Dto/Generated/FinishingMaterialRaw.cs
// (auto-generated из visary_api.fields snapshot, появился в main).
