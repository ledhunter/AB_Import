namespace Visary.Api.Dto;

public sealed class SitePatchRequest
{
    public int ID { get; set; }
    public long RowVersion { get; set; }
    public VisaryRef? Type { get; set; }
    public VisaryRef? EstateClass { get; set; }
    public VisaryRef? BuildingMaterial { get; set; }
    public VisaryRef? FinishingMaterial { get; set; }
    public VisaryRef? Town { get; set; }
    public string? Description { get; set; }
    public string? ConstructionPermissionNumber { get; set; }
    public string? ConstructionProjectNumber { get; set; }
    public int? StageNumber { get; set; }
}

/// <summary>
/// PATCH-запрос для Room (через <c>/crud/room/{id}?forceUpdate=true</c>).
/// ID берётся из URL, RowVersion не требуется → поля nullable, чтобы при
/// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull"/>
/// не попадать в тело. Иначе Visary падает 500: «Can not add property RowVersion to JObject».
/// </summary>
public sealed class RoomPatchRequest
{
    public int? ID { get; set; }
    public long? RowVersion { get; set; }
    public VisaryRef? Kind { get; set; }
    public VisaryRef? Section { get; set; }
    public string? ExplicationNumber { get; set; }
    public string? Floor { get; set; }
    public string? BuildingSection { get; set; }
    public int? RoomsNumber { get; set; }
    public double? ProjectArea { get; set; }
    public double? CostForOne { get; set; }
    public double? MarketCostPerM { get; set; }
    public double? ZalogCostPerM { get; set; }
}

/// <summary>
/// PATCH-запрос для ShareAgreement (через <c>/crud/shareagreement/{id}?forceUpdate=true</c>).
/// Аналогично <see cref="RoomPatchRequest"/>: ID/RowVersion не нужны в теле.
/// </summary>
public sealed class ShareAgreementPatchRequest
{
    public int? ID { get; set; }
    public long? RowVersion { get; set; }
    public string? Number { get; set; }
    public string? Title { get; set; }
    public VisaryRef? Site { get; set; }
    public VisaryRef? Project { get; set; }
}

public sealed class SiteCreateRequest
{
    public int? StatusReadiness { get; set; }
    public int? ProjectID { get; set; }
    public VisaryRef? Project { get; set; }
    public string? ConstructionProjectNumber { get; set; }
    public string? ConstructionPermissionNumber { get; set; }
    public int? StageNumber { get; set; }
    public VisaryRef? Type { get; set; }
    public VisaryRef? EstateClass { get; set; }
    public VisaryRef? BuildingMaterial { get; set; }
    public VisaryRef? FinishingMaterial { get; set; }
    public string? Description { get; set; }
}

public sealed class ProjectPatchRequest
{
    public int ID { get; set; }
    public long RowVersion { get; set; }
    public VisaryRef? Town { get; set; }
    public VisaryRef? Type { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? IdentifierKK { get; set; }
    public string? IdentifierZPLM { get; set; }
}

public sealed class ProjectCreateRequest
{
    public VisaryRef? Author { get; set; }
    public string? Date { get; set; }
    public string? Title { get; set; }
    public VisaryRef? Type { get; set; }
    public string? IdentifierZPLM { get; set; }
    public string? IdentifierKK { get; set; }
    public VisaryRef? Town { get; set; }
    public string? Description { get; set; }
}

public sealed class IndicatorValuePatchRequest
{
    // Включено в body для optimistic locking — `forceUpdate=false` требует, чтобы клиент
    // прислал актуальный RowVersion. Берётся из `GetIndicatorValueByIdAsync(...)`.
    public int ID { get; set; }
    public long RowVersion { get; set; }

    public double? Value { get; set; }
    public double? PlanValue { get; set; }
    public double? ForecastValue { get; set; }
}

public sealed class CadastralAreaCreateRequest
{
    public double? Area { get; set; }
    public string? CadastralNum { get; set; }
    public VisaryRef? LandCategory { get; set; }
    public List<CadastralUseTypeRef>? UseTypes { get; set; }
}

public sealed class CadastralUseTypeRef
{
    public VisaryRef? Object { get; set; }
}

public sealed class CadastralAreaPatchRequest
{
    public int ID { get; set; }
    public long RowVersion { get; set; }
    public string? UnstructuredArea { get; set; }
    public double? Area { get; set; }
    public string? CadastralNum { get; set; }
    public string? EGRNNumber { get; set; }
    public int? ParentID { get; set; }
}

public sealed class PercentBetCreateRequest
{
    public string? LmID { get; set; }
    public int? BaseRateType { get; set; }
    public int? PercentKind { get; set; }
    public VisaryRef? Deal { get; set; }
    public double? Rate { get; set; }
    public double? CommissionSum { get; set; }
    public VisaryRef? Currency { get; set; }
    public double? StandardRate { get; set; }
    public double? SpecialRate { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public VisaryRef? PaymentCurrency { get; set; }
    public double? BasePart { get; set; }
    public double? FloatRateMin { get; set; }
    public double? FloatRateMax { get; set; }
}

public sealed class SectionCreateRequest
{
    public int ConstructionSiteID { get; set; }
    public VisaryRef? ConstructionSite { get; set; }
    public VisaryRef? Type { get; set; }
    public VisaryRef? BuildingMaterial { get; set; }
    public int? Stage { get; set; }
    public string? Title { get; set; }
}

public sealed class RoomCreateRequest
{
    public int SiteID { get; set; }
    public VisaryRef? Site { get; set; }
    public string? Title { get; set; }
    public VisaryRef? Kind { get; set; }
    public string? ExplicationNumber { get; set; }
    public VisaryRef? Section { get; set; }
    public string? BuildingSection { get; set; }
    public string? Floor { get; set; }
    public double? CalculatedCostPerM { get; set; }
    public double? MarketCostPerM { get; set; }
    public double? ZalogCostPerM { get; set; }
    public int? RoomsNumber { get; set; }
    public double? ProjectArea { get; set; }
    public double? TotalAreaWithoutSummerRoom { get; set; }
    public double? SummerRoomArea { get; set; }
    public string? UniqueNumber { get; set; }
    public string? CadastralNumber { get; set; }
    public string? Description { get; set; }
    public double? Cost { get; set; }
    public double? CostForOne { get; set; }
}

public sealed class ShareAgreementCreateRequest
{
    public int RoomID { get; set; }
    public VisaryRef? Room { get; set; }
    public VisaryRef? Project { get; set; }
    public VisaryRef? Site { get; set; }
    public string? Title { get; set; }
    public string? Number { get; set; }
    public VisaryRef? RoomKindRef { get; set; }
    public string? ProjectNumber { get; set; }
    public string? StageNumber { get; set; }
    public string? ConditionalNumber { get; set; }
}

/// <summary>
/// POST <c>/api/visary/crud/wbs</c> — создание главы или подстатьи ИСР.
/// • Глава: <c>ParentID=null, Parent=null</c>, ProjectID указывает на проект.
/// • Подстатья: <c>ParentID</c> = ID родителя (главы или другой статьи).
/// Code (КБК, например "1.1.") присваивается сервером автоматически.
/// </summary>
public sealed class WbsCreateRequest
{
    public int ProjectID { get; set; }
    public VisaryRef? Project { get; set; }
    public int? ParentID { get; set; }
    public VisaryRef? Parent { get; set; }
    public int? ConstructionSiteID { get; set; }
    public VisaryRef? ConstructionSite { get; set; }
    public string? Title { get; set; }
    public double? DeclaredSum { get; set; }
    public double? ConfirmedSum { get; set; }
}
